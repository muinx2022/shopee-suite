using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Ẩn/hiện cột phụ của bảng đơn (menu chuột phải trên lưới — đợt G): mặc định hiện đủ, tắt một cột là GHI
/// NGAY vào settings của máy, mở lại màn vẫn nhớ, và bật lại thì xoá khỏi danh sách ẩn.
/// </summary>
public class OrdersColumnVisibilityTests
{
    [Fact]
    public void MacDinh_HienDuMoiCotPhu()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);

        var vm = new OrdersViewModel(services);

        Assert.True(vm.ShowColPhanLoai);
        Assert.True(vm.ShowColDonTraHang);
        Assert.True(vm.ShowColUocTinh);
        Assert.True(vm.ShowColThanhToan);
        Assert.True(vm.ShowColDvvc);
        Assert.True(vm.ShowColSyncLuc);
    }

    [Fact]
    public void TatCot_LuuVaoSettings_VaBanVmMoiVanNho()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var vm = new OrdersViewModel(services);

        vm.ShowColUocTinh = false;
        vm.ShowColThanhToan = false;

        var hidden = services.Settings.GetHiddenOrderColumns();
        Assert.Equal(2, hidden.Count);
        Assert.Contains(OrdersViewModel.ColUocTinh, hidden);
        Assert.Contains(OrdersViewModel.ColThanhToan, hidden);

        // Mở lại màn (VM mới trên cùng DB) → vẫn đúng trạng thái đã chọn.
        var lai = new OrdersViewModel(services);
        Assert.False(lai.ShowColUocTinh);
        Assert.False(lai.ShowColThanhToan);
        Assert.True(lai.ShowColDvvc);
    }

    [Fact]
    public void BatLaiCot_XoaKhoiDanhSachAn()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var vm = new OrdersViewModel(services);

        vm.ShowColDvvc = false;
        vm.ShowColDvvc = true;

        Assert.Empty(services.Settings.GetHiddenOrderColumns());
        Assert.True(new OrdersViewModel(services).ShowColDvvc);
    }

    [Fact]
    public void TatCot_BanPropertyChanged_DungTenProperty()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var vm = new OrdersViewModel(services);
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        vm.ShowColSyncLuc = false;

        Assert.Contains(nameof(OrdersViewModel.ShowColSyncLuc), seen);
    }
}
