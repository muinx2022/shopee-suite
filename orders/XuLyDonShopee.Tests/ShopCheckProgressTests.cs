using System.Globalization;
using System.Linq;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Cột tiến độ của tab "Kết quả" (chấm tròn shop phiên đang chạy tới + vòng quay khi đang check shop đó).
/// Sự kiện <c>AppServices.ShopCheckChanged</c> do phiên cầu nối bắn; test gọi thẳng
/// <c>RaiseShopCheckChanged</c> nên không cần trình duyệt. Chạy trên thread test (CheckAccess()==true) →
/// <c>RunOnUi</c> chạy đồng bộ, giống lúc phiên bắn về UI thread.
/// </summary>
public class ShopCheckProgressTests
{
    private const string LoginA = "alina99.store";
    private const string LoginB = "shop9x.store";

    /// <summary>Dựng VM đang mở MỘT tài khoản có 2 shop (tên hiển thị KHÁC login — đúng dữ liệu thật).</summary>
    private static (AppServices Services, AccountsViewModel Vm, long AccountId) NewVmWith2Shops(TempDatabase temp)
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

        vm.SelectedRow = vm.Accounts.First(); // nạp lưới kết quả của tài khoản này
        Assert.Equal(2, vm.ResultRows.Count);
        return (services, vm, accountId);
    }

    private static ShopPrepareRow Row(AccountsViewModel vm, string login)
        => vm.ResultRows.Single(r => r.ShopLogin == login);

    // ===== Bắt đầu check shop A → chấm + vòng quay ở A, B sạch cờ =====
    [Fact]
    public void BatDauCheckShopA_ChamVaVongQuayOA()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = NewVmWith2Shops(temp);

        services.RaiseShopCheckChanged(accountId, LoginA, checking: true);

        var a = Row(vm, LoginA);
        Assert.True(a.IsCurrent);
        Assert.True(a.IsChecking);
        Assert.False(a.ShowDot); // đang quay thì vòng quay thế chỗ chấm

        var b = Row(vm, LoginB);
        Assert.False(b.IsCurrent);
        Assert.False(b.IsChecking);
    }

    // ===== Xong shop A → vòng quay TẮT nhưng chấm VẪN Ở LẠI A =====
    [Fact]
    public void XongShopA_TatVongQuay_ChamVanOLaiA()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = NewVmWith2Shops(temp);

        services.RaiseShopCheckChanged(accountId, LoginA, checking: true);
        services.RaiseShopCheckChanged(accountId, LoginA, checking: false);

        var a = Row(vm, LoginA);
        Assert.True(a.IsCurrent);   // chấm ở lại shop vừa xong
        Assert.False(a.IsChecking); // hết quay
        Assert.True(a.ShowDot);
    }

    // ===== Chỉ khi shop MỚI bắt đầu thì chấm mới chuyển sang shop đó =====
    [Fact]
    public void BatDauShopB_ChamChuyenTuASangB()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = NewVmWith2Shops(temp);

        services.RaiseShopCheckChanged(accountId, LoginA, checking: true);
        services.RaiseShopCheckChanged(accountId, LoginA, checking: false);
        services.RaiseShopCheckChanged(accountId, LoginB, checking: true);

        var a = Row(vm, LoginA);
        Assert.False(a.IsCurrent); // A nhả chấm ĐÚNG lúc B bắt đầu
        Assert.False(a.IsChecking);

        var b = Row(vm, LoginB);
        Assert.True(b.IsCurrent);
        Assert.True(b.IsChecking);
    }

    // ===== Sự kiện của TÀI KHOẢN KHÁC → lưới đang mở KHÔNG đổi gì =====
    [Fact]
    public void SuKienTaiKhoanKhac_LuoiDangMoKhongDoi()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = NewVmWith2Shops(temp);
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "p" });
        var otherId = services.Accounts.GetAll().Single(a => a.Email == "b@mail.com").Id;
        Assert.NotEqual(accountId, otherId);

        services.RaiseShopCheckChanged(otherId, LoginA, checking: true);

        Assert.All(vm.ResultRows, r =>
        {
            Assert.False(r.IsCurrent);
            Assert.False(r.IsChecking);
        });
    }

    // ===== BẪY CHÍNH: số đơn cập nhật (LoadResults dựng lại dòng) KHÔNG được làm mất chấm/vòng quay =====
    [Fact]
    public void LoadResultsKhiSoDonCapNhat_KhongLamMatChamVaVongQuay()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = NewVmWith2Shops(temp);
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        services.RaiseShopCheckChanged(accountId, LoginA, checking: true);

        // Đúng luồng thật: mỗi đơn arrange xong → +1 DB rồi phát PrepareCountChanged → VM gọi LoadResults().
        services.Results.IncrementPrepared(accountId, LoginA, today);
        services.RaisePrepareCountChanged(accountId);

        var a = Row(vm, LoginA);
        Assert.Equal(1, a.PreparedCount); // số đã cập nhật
        Assert.True(a.IsCurrent);         // ... mà cờ tiến độ VẪN còn
        Assert.True(a.IsChecking);

        // Xong shop rồi vẫn tiếp tục có lượt nạp lại (đơn của shop kế) → chấm vẫn ở A.
        services.RaiseShopCheckChanged(accountId, LoginA, checking: false);
        services.Results.IncrementPrepared(accountId, LoginA, today);
        services.RaisePrepareCountChanged(accountId);

        a = Row(vm, LoginA);
        Assert.Equal(2, a.PreparedCount);
        Assert.True(a.IsCurrent);
        Assert.False(a.IsChecking);
    }

    // ===== Nhãn phiên gửi là LOGIN, dòng lưới hiển thị TÊN shop khác login → vẫn phải khớp đúng dòng =====
    [Fact]
    public void NhanShopLaLogin_KhacTenHienThi_VanKhopDungDong()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = NewVmWith2Shops(temp);
        Assert.Equal("Alina Store1", Row(vm, LoginA).ShopName); // tên hiển thị KHÁC login

        services.RaiseShopCheckChanged(accountId, LoginA, checking: true);

        Assert.True(Row(vm, LoginA).IsCurrent);
    }

    // ===== So khớp nhãn: bỏ khoảng trắng thừa + không phân biệt hoa/thường =====
    [Fact]
    public void NhanShopLechHoaThuongVaKhoangTrang_VanKhop()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = NewVmWith2Shops(temp);

        services.RaiseShopCheckChanged(accountId, "  ALINA99.Store ", checking: true);

        Assert.True(Row(vm, LoginA).IsCurrent);
        Assert.False(Row(vm, LoginB).IsCurrent);
    }

    // ===== Đổi sang tài khoản KHÁC (chưa chạy) → lưới của nó không dính chấm của tài khoản đang chạy =====
    [Fact]
    public void DoiSangTaiKhoanKhac_KhongMangChamTheoSang()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = NewVmWith2Shops(temp);
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "p" });
        vm.Reload();
        var otherId = vm.Accounts.Single(r => r.Email == "b@mail.com").Id;
        services.Results.UpsertShops(otherId, new[] { new ShopListItem("111", "Alina Store1", LoginA) });

        services.RaiseShopCheckChanged(accountId, LoginA, checking: true);
        vm.SelectedRow = vm.Accounts.Single(r => r.Id == otherId);

        Assert.All(vm.ResultRows, r => Assert.False(r.IsCurrent));

        // Quay lại tài khoản đang chạy → chấm/vòng quay của NÓ hiện lại.
        vm.SelectedRow = vm.Accounts.Single(r => r.Id == accountId);
        Assert.True(Row(vm, LoginA).IsCurrent);
        Assert.True(Row(vm, LoginA).IsChecking);
    }
}
