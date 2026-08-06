using System.Globalization;
using System.Linq;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Tab "Shops" phải TỰ sang ngày mới khi máy chạy xuyên đêm (module Đơn hàng chạy vòng liên tục cả đêm):
/// trước đây ô ngày được đặt MỘT LẦN lúc mở app nên qua nửa đêm số đóng băng ở hôm qua, và
/// <c>OnPrepareCountChanged</c> thoát sớm vì ngày lệch ⇒ đơn của ngày mới KHÔNG hiện ra tới khi mở lại app.
/// <para>Phần quyết định là hàm THUẦN <see cref="AccountsViewModel.QuyetDinhSangNgay"/> (test thẳng, KHÔNG chờ
/// timer); phần áp vào ViewModel nhận "hôm nay" từ bên gọi nên mô phỏng được lúc qua nửa đêm.</para>
/// VM chạy trên thread test (CheckAccess()==true) → <c>RunOnUi</c> chạy đồng bộ.
/// </summary>
public class PrepareDayRolloverTests
{
    private const string LoginA = "alina99.store";
    private const string LoginB = "shop9x.store";

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ===== Hàm THUẦN: 4 ca của quy tắc quyết định =====

    // Đang xem "hôm nay (cũ)" + đồng hồ đã sang ngày → CHUYỂN sang ngày mới.
    [Fact]
    public void QuyetDinhSangNgay_DangXemHomNay_NgayDaSang_ThiChuyen()
    {
        var homQua = new DateTime(2026, 7, 27);
        var homNay = new DateTime(2026, 7, 28);

        var (chuyen, ngayMoi) = AccountsViewModel.QuyetDinhSangNgay(homQua, homQua, homNay);

        Assert.True(chuyen);
        Assert.Equal(homNay, ngayMoi);
    }

    // Người dùng CHỦ ĐỘNG mở một ngày cũ để đối chiếu + đồng hồ đã sang ngày → KHÔNG giật ngày khỏi tay họ.
    [Fact]
    public void QuyetDinhSangNgay_NguoiDungChonNgayCu_NgayDaSang_ThiKhongChuyen()
    {
        var ngayHoChon = new DateTime(2026, 7, 20);
        var mocCu = new DateTime(2026, 7, 27);
        var homNay = new DateTime(2026, 7, 28);

        var (chuyen, ngayMoi) = AccountsViewModel.QuyetDinhSangNgay(ngayHoChon, mocCu, homNay);

        Assert.False(chuyen);
        Assert.Equal(ngayHoChon, ngayMoi); // giữ nguyên ngày họ đang xem
    }

    // Ngày CHƯA sang → không làm gì (kể cả khi đang xem ngày cũ).
    [Theory]
    [InlineData("2026-07-27")] // đang xem đúng hôm nay
    [InlineData("2026-07-20")] // đang xem ngày cũ
    public void QuyetDinhSangNgay_NgayChuaSang_ThiKhongLamGi(string dangXem)
    {
        var homNay = new DateTime(2026, 7, 27);
        var xem = DateTime.ParseExact(dangXem, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        var (chuyen, ngayMoi) = AccountsViewModel.QuyetDinhSangNgay(xem, homNay, homNay);

        Assert.False(chuyen);
        Assert.Equal(xem, ngayMoi);
    }

    // Đồng hồ máy bị chỉnh LÙI: đi theo đồng hồ (chuyển) nhưng ĐÚNG MỘT lần — mốc được cập nhật nên lần dò kế
    // không chuyển lại ⇒ không có vòng đổi qua đổi lại.
    [Fact]
    public void QuyetDinhSangNgay_DongHoChinhLui_ChuyenMotLanRoiOnDinh()
    {
        var mocCu = new DateTime(2026, 7, 28);
        var homNayLui = new DateTime(2026, 7, 27);

        var lan1 = AccountsViewModel.QuyetDinhSangNgay(mocCu, mocCu, homNayLui);
        Assert.True(lan1.Chuyen);
        Assert.Equal(homNayLui, lan1.NgayMoi);

        // Lần dò kế (mốc đã = ngày lùi, ô ngày đã theo) → đứng yên.
        var lan2 = AccountsViewModel.QuyetDinhSangNgay(lan1.NgayMoi, homNayLui, homNayLui);
        Assert.False(lan2.Chuyen);
        Assert.Equal(homNayLui, lan2.NgayMoi);
    }

    // ===== Áp vào ViewModel =====

    /// <summary>VM đang mở 1 tài khoản 2 shop; HÔM NAY: A=2, B=1 (tổng 3). NGÀY MAI: A=5 (tổng 5) — để chứng minh
    /// sau khi sang ngày, lưới + dòng tổng về số của NGÀY MỚI chứ không giữ số hôm qua.</summary>
    private static (AppServices Services, AccountsViewModel Vm, long AccountId) NewVm(TempDatabase temp)
    {
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        var accountId = vm.Accounts.First().Id;
        services.Results.UpsertShops(accountId, new[]
        {
            new ShopListItem("111", "Alina Store1", LoginA),
            new ShopListItem("222", "Shop 9X", LoginB),
        });

        var homNay = Key(DateTime.Now.Date);
        var ngayMai = Key(DateTime.Now.Date.AddDays(1));
        services.Results.IncrementPrepared(accountId, LoginA, homNay);
        services.Results.IncrementPrepared(accountId, LoginA, homNay);
        services.Results.IncrementPrepared(accountId, LoginB, homNay);
        for (var i = 0; i < 5; i++)
        {
            services.Results.IncrementPrepared(accountId, LoginA, ngayMai);
        }

        vm.SelectedRow = vm.Accounts.First(); // nạp lưới của hôm nay
        Assert.Equal(3, vm.TongChuanBiHang);
        return (services, vm, accountId);
    }

    // Đang xem hôm nay → qua nửa đêm: ô ngày sang ngày mới, lưới nạp lại, DÒNG TỔNG về số của ngày mới.
    [Fact]
    public void KiemTraSangNgay_DangXemHomNay_KeoONgaySang_LuoiVaTongTheoNgayMoi()
    {
        using var temp = new TempDatabase();
        var (_, vm, _) = NewVm(temp);
        var ngayMai = DateTime.Now.Date.AddDays(1);

        var daChuyen = vm.KiemTraSangNgay(ngayMai);

        Assert.True(daChuyen);
        Assert.Equal(ngayMai, vm.ResultDate.Date);
        Assert.Equal(5, vm.ResultRows.Single(r => r.ShopLogin == LoginA).PreparedCount);
        Assert.Equal(0, vm.ResultRows.Single(r => r.ShopLogin == LoginB).PreparedCount);
        Assert.Equal(5, vm.TongChuanBiHang); // KHÔNG giữ 3 của hôm qua
    }

    // Người dùng chủ động chọn ngày cũ để đối chiếu → qua nửa đêm KHÔNG bị giật khỏi ngày đang xem.
    [Fact]
    public void KiemTraSangNgay_NguoiDungDangXemNgayCu_KhongBiGiatNgay()
    {
        using var temp = new TempDatabase();
        var (_, vm, _) = NewVm(temp);

        var ngayCu = DateTime.Now.Date.AddDays(-3);
        vm.ResultDate = new DateTimeOffset(ngayCu, DateTimeOffset.Now.Offset); // họ tự chọn

        var daChuyen = vm.KiemTraSangNgay(DateTime.Now.Date.AddDays(1));

        Assert.False(daChuyen);
        Assert.Equal(ngayCu, vm.ResultDate.Date); // vẫn ở ngày họ đang xem
    }

    // Mốc "coi là hôm nay" được cập nhật ở CẢ hai nhánh → dò lại lần nữa trong cùng ngày là no-op (không nạp
    // thừa, không tốn thêm lượt hỏi hub).
    [Fact]
    public void KiemTraSangNgay_DoLaiTrongCungNgay_LaNoOp()
    {
        using var temp = new TempDatabase();
        var (_, vm, _) = NewVm(temp);
        var ngayMai = DateTime.Now.Date.AddDays(1);

        Assert.True(vm.KiemTraSangNgay(ngayMai));
        Assert.False(vm.KiemTraSangNgay(ngayMai)); // lần 2 cùng ngày → đứng yên
        Assert.Equal(ngayMai, vm.ResultDate.Date);
    }

    // Sang ngày → QUÊN map hub của ngày cũ, không áp nhầm số hôm qua lên lưới hôm nay.
    [Fact]
    public async Task KiemTraSangNgay_QuenSoHubCuaNgayCu()
    {
        using var temp = new TempDatabase();
        var (services, vm, _) = NewVm(temp);

        services.QueryPrepareStats = (_, _) => Task.FromResult<IReadOnlyDictionary<string, int>?>(
            new Dictionary<string, int>(StringComparer.Ordinal) { [LoginA] = 9 });
        await vm.RefreshHubCountsAsync();
        Assert.Equal(9, vm.ResultRows.Single(r => r.ShopLogin == LoginA).PreparedCount);
        Assert.True(vm.DangDungSoHub);

        services.QueryPrepareStats = null; // hub không trả lời cho ngày mới
        Assert.True(vm.KiemTraSangNgay(DateTime.Now.Date.AddDays(1)));

        Assert.Equal(5, vm.ResultRows.Single(r => r.ShopLogin == LoginA).PreparedCount); // số CỤC BỘ ngày mới
        Assert.False(vm.DangDungSoHub);
    }

    // ===== LỖI GỐC người dùng báo, tái hiện qua ĐÚNG đường sự kiện thật =====
    // Mô phỏng "app mở từ hôm qua, chạy xuyên đêm": kéo mốc + ô ngày về hôm qua (đúng trạng thái VM lúc mở app
    // hôm qua), rồi để phiên chuẩn bị xong một đơn. Trước đây OnPrepareCountChanged thoát sớm vì ngày lệch ⇒ ô
    // ngày kẹt ở hôm qua và đơn của ngày mới KHÔNG hiện ra tới khi mở lại app.
    [Fact]
    public void PrepareCountChanged_AppMoTuHomQua_DonDauNgayMoiKeoONgaySang()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = NewVm(temp);

        var homQua = DateTime.Now.Date.AddDays(-1);
        Assert.True(vm.KiemTraSangNgay(homQua)); // VM giờ ở trạng thái "mở app hôm qua"
        Assert.Equal(homQua, vm.ResultDate.Date);
        Assert.Equal(0, vm.TongChuanBiHang);     // hôm qua chưa chuẩn bị đơn nào

        // Qua nửa đêm, phiên chuẩn bị xong đơn đầu tiên của ngày mới (số cục bộ của HÔM NAY: A=2+1, B=1).
        services.Results.IncrementPrepared(accountId, LoginA, Key(DateTime.Now.Date));
        services.RaisePrepareCountChanged(accountId);

        Assert.Equal(DateTime.Now.Date, vm.ResultDate.Date); // ô ngày tự sang ngày mới
        Assert.Equal(3, vm.ResultRows.Single(r => r.ShopLogin == LoginA).PreparedCount);
        Assert.Equal(1, vm.ResultRows.Single(r => r.ShopLogin == LoginB).PreparedCount);
        Assert.Equal(4, vm.TongChuanBiHang);
    }

    // Đơn chuẩn bị xong trong CÙNG ngày (đường thường) → vẫn nạp lại lưới như cũ, không hồi quy.
    [Fact]
    public void PrepareCountChanged_CungNgay_VanNapLaiLuoi()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = NewVm(temp);

        services.Results.IncrementPrepared(accountId, LoginB, Key(DateTime.Now.Date));
        services.RaisePrepareCountChanged(accountId);

        Assert.Equal(DateTime.Now.Date, vm.ResultDate.Date);
        Assert.Equal(2, vm.ResultRows.Single(r => r.ShopLogin == LoginB).PreparedCount);
        Assert.Equal(4, vm.TongChuanBiHang);
    }
}
