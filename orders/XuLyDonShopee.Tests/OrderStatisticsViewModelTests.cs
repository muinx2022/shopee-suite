using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;

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
}
