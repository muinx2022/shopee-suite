using System.Diagnostics;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Hồi quy cho bộ lọc ngày của thống kê đơn: VM phải dùng DateTime? khớp CalendarDatePicker
/// và xử lý rõ ràng khi người dùng xóa một mốc ngày.
/// </summary>
public class OrderStatisticsViewModelTests
{
    [Fact]
    public void Constructor_DefaultsToCurrentMonthThroughToday()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var before = DateTime.Today;

        var vm = new OrderStatisticsViewModel(services);

        var after = DateTime.Today;
        Assert.NotNull(vm.FromDate);
        Assert.NotNull(vm.ToDate);
        Assert.Equal(1, vm.FromDate!.Value.Day);
        Assert.Equal(vm.ToDate.Value.Year, vm.FromDate.Value.Year);
        Assert.Equal(vm.ToDate.Value.Month, vm.FromDate.Value.Month);
        Assert.InRange(vm.ToDate.Value.Date, before, after);
    }

    [Fact]
    public void ClearingEitherDate_DoesNotThrow_AndShowsValidationMessage()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var vm = new OrderStatisticsViewModel(services);

        var exception = Record.Exception(() => vm.FromDate = null);

        Assert.Null(exception);
        Assert.Null(vm.FromDate);
        Assert.False(vm.HasData);
        Assert.Equal("Hãy chọn đầy đủ Từ ngày và Đến ngày để xem thống kê.", vm.EmptyMessage);
        Assert.Equal(vm.EmptyMessage, vm.ScopeText);
    }

    // ===== Nguồn số đang xem phải nói RÕ (chống "hỏng im lặng": hub chết mà vẫn hiện số máy như số chung) =====

    [Fact]
    public void ChuaNoiHub_HienSoMayNay_VaNoiRoNguon()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);

        var vm = new OrderStatisticsViewModel(services); // KHÔNG rót hook hub → app chạy độc lập

        Assert.Equal("Số trên MÁY NÀY (app chạy độc lập, chưa nối Hub).", vm.SourceText);
        Assert.Equal("1", vm.TotalOrdersText);
    }

    [Fact]
    public void HubKhongPhanHoi_GiuSoLocal_VaNoiRoNguon()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(null); // hub lỗi/hub cũ

        var vm = new OrderStatisticsViewModel(services);

        Assert.Equal("Số trên MÁY NÀY — Hub không phản hồi nên chưa gộp được số chung.", vm.SourceText);
        Assert.Equal("1", vm.TotalOrdersText); // vẫn có số để nhìn, không rỗng
    }

    /// <summary>Hồi quy lỗi "đơ tới 8 giây mỗi lần chỉnh ngày": số local phải vẽ NGAY, lời gọi Hub chạy nền.</summary>
    [Fact]
    public void HubChamKhongChanLuongUi_SoLocalHienNgay()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        services.QueryOrderStatistics = async (_, _, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct); // hub treo tới sát timeout
            return null;
        };

        var sw = Stopwatch.StartNew();
        var vm = new OrderStatisticsViewModel(services);
        vm.FromDate = DateTime.Today.AddDays(-3); // đổi ngày: mỗi lần đổi lại gọi hub
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"Vẽ thống kê không được chờ hub: {sw.ElapsedMilliseconds}ms");
        Assert.Equal("1", vm.TotalOrdersText);
    }
}
