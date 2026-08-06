using System;
using System.Linq;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Lọc theo KHOẢNG NGÀY ở màn "Đơn hàng" (H2.2) — chạy headless trên DB tạm.
/// Canh 3 thứ dễ sai: biên "Đến ngày" phải bao HẾT ngày đó, hai đầu mút ĐỘC LẬP (bỏ trống một đầu = không chặn
/// đầu đó), và đổi khoảng ngày phải RESET về trang 1 (giữ trang cũ là người dùng thấy lưới trống mà tưởng mất đơn).
/// </summary>
public class OrdersDateFilterTests
{
    /// <summary>Mốc UTC ứng với 12:00 TRƯA ngày địa phương <paramref name="ngay"/> — giữa ngày nên mọi phép quy
    /// đổi múi giờ vẫn rơi đúng ngày đó, test không phụ thuộc máy chạy ở múi nào.</summary>
    private static DateTime TruaNgay(DateTime ngay)
        => TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(ngay.Date.AddHours(12), DateTimeKind.Unspecified), TimeZoneInfo.Local);

    private static readonly DateTime Ngay1 = new(2026, 8, 1);
    private static readonly DateTime Ngay2 = new(2026, 8, 2);
    private static readonly DateTime Ngay3 = new(2026, 8, 3);

    private static AppServices SeedBaNgay(TempDatabase temp)
    {
        var services = new AppServices(temp.Path);
        // created_at = mốc truyền vào UpsertMany (đơn MỚI đặt created_at = updated_at = synced_at).
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1", Status = "Chờ lấy hàng" } },
            TruaNgay(Ngay1), shopLogin: "shopA");
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN2", Status = "Chờ lấy hàng" } },
            TruaNgay(Ngay2), shopLogin: "shopA");
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN3", Status = "Chờ lấy hàng" } },
            TruaNgay(Ngay3), shopLogin: "shopA");
        return services;
    }

    private static string[] Ma(OrdersViewModel vm) => vm.Rows.Select(r => r.OrderSn).OrderBy(s => s).ToArray();

    [Fact]
    public void KhongDatNgay_HienDuMoiDon()
    {
        using var temp = new TempDatabase();
        var vm = new OrdersViewModel(SeedBaNgay(temp));
        Assert.Equal(new[] { "SN1", "SN2", "SN3" }, Ma(vm));
    }

    [Fact]
    public void DenNgay_BaoHET_NgayDo()
    {
        using var temp = new TempDatabase();
        var vm = new OrdersViewModel(SeedBaNgay(temp)) { ToDate = Ngay2 };
        // Đơn của ĐÚNG ngày 2 phải còn (biên "Đến" là hết ngày, không phải 00:00 ngày đó).
        Assert.Equal(new[] { "SN1", "SN2" }, Ma(vm));
        Assert.Equal(2, vm.TotalCount);
    }

    [Fact]
    public void TuNgay_BaoDon_CuaChinhNgayDo()
    {
        using var temp = new TempDatabase();
        var vm = new OrdersViewModel(SeedBaNgay(temp)) { FromDate = Ngay2 };
        Assert.Equal(new[] { "SN2", "SN3" }, Ma(vm));
    }

    [Fact]
    public void CaHaiDauMut_RaDungMotNgay()
    {
        using var temp = new TempDatabase();
        var vm = new OrdersViewModel(SeedBaNgay(temp)) { FromDate = Ngay2, ToDate = Ngay2 };
        Assert.Equal(new[] { "SN2" }, Ma(vm));
    }

    [Fact]
    public void XoaLocNgay_TraVeDuMoiDon()
    {
        using var temp = new TempDatabase();
        var vm = new OrdersViewModel(SeedBaNgay(temp)) { FromDate = Ngay2, ToDate = Ngay2 };
        Assert.Single(vm.Rows);

        vm.ClearDateFilterCommand.Execute(null);

        Assert.Null(vm.FromDate);
        Assert.Null(vm.ToDate);
        Assert.Equal(new[] { "SN1", "SN2", "SN3" }, Ma(vm));
    }

    [Fact]
    public void DoiKhoangNgay_ResetVeTrang1()
    {
        using var temp = new TempDatabase();
        var vm = new OrdersViewModel(SeedBaNgay(temp)) { PageSize = 1 };
        vm.NextPageCommand.Execute(null);
        vm.NextPageCommand.Execute(null);
        Assert.Equal(3, vm.CurrentPage);   // đang ở trang cuối

        vm.FromDate = Ngay1;               // đổi bộ lọc ngày
        Assert.Equal(1, vm.CurrentPage);   // phải về trang 1, không để lưới trống

        vm.CurrentPage = 3;                // giả lập lại (clamp về TotalPages)
        vm.ToDate = Ngay3;
        Assert.Equal(1, vm.CurrentPage);
    }

    [Fact]
    public void TuLonHonDen_KhongCoDon_VaCoCanhBao()
    {
        using var temp = new TempDatabase();
        var vm = new OrdersViewModel(SeedBaNgay(temp)) { FromDate = Ngay3, ToDate = Ngay1 };
        Assert.Empty(vm.Rows);
        Assert.False(string.IsNullOrWhiteSpace(vm.DateWarning));

        vm.ToDate = Ngay3;                 // sửa lại cho hợp lệ → cảnh báo phải tắt
        Assert.Null(vm.DateWarning);
    }

    [Fact]
    public void XuatCsv_VaInNhieuDon_DungCUNG_BoLocNgay()
    {
        // Cả hai đường đi qua CurrentFilter nên phạm vi phải khớp lưới; kiểm gián tiếp bằng TotalCount + Query
        // cùng tham số mà CurrentFilter dựng.
        using var temp = new TempDatabase();
        var services = SeedBaNgay(temp);
        var vm = new OrdersViewModel(services) { FromDate = Ngay2, ToDate = Ngay2 };

        var (fromUtc, beforeUtc) = OrdersViewModel.BuildCreatedRange(Ngay2, Ngay2);
        var moiTrang = services.Orders.Query(createdFromUtc: fromUtc, createdBeforeUtc: beforeUtc);

        Assert.Equal(vm.TotalCount, moiTrang.Count);
        Assert.Equal(new[] { "SN2" }, moiTrang.Select(r => r.OrderSn).ToArray());
    }

    // ══════════ Hàm thuần dựng biên ══════════

    [Fact]
    public void BuildCreatedRange_BoTrong_ThiKhongChanDauDo()
    {
        Assert.Equal((null, null), OrdersViewModel.BuildCreatedRange(null, null));

        var (f1, b1) = OrdersViewModel.BuildCreatedRange(Ngay2, null);
        Assert.NotNull(f1);
        Assert.Null(b1);

        var (f2, b2) = OrdersViewModel.BuildCreatedRange(null, Ngay2);
        Assert.Null(f2);
        Assert.NotNull(b2);
    }

    [Fact]
    public void BuildCreatedRange_BienTren_LaDauNgayHOMSAU()
    {
        var (from, before) = OrdersViewModel.BuildCreatedRange(Ngay2, Ngay2);
        Assert.Equal(TimeSpan.FromDays(1), before!.Value - from!.Value);
        // Giờ trong ngày của mốc gốc bị bỏ (chỉ lấy phần .Date) → chọn 23:59 hay 00:01 cùng ngày đều ra một biên.
        var (from2, before2) = OrdersViewModel.BuildCreatedRange(Ngay2.AddHours(23), Ngay2.AddMinutes(1));
        Assert.Equal(from, from2);
        Assert.Equal(before, before2);
    }
}
