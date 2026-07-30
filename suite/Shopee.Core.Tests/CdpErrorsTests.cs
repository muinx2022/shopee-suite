using Shopee.Core.Cdp;

namespace Shopee.Core.Tests;

/// <summary>
/// <see cref="CdpErrors.IsTransientNavigationError(string?)"/> gom 3 danh sách marker gần trùng trước đây
/// (Import-to-store, vòng điền form login MultiBrave, bộ lọc lỗi service worker) — mỗi marker của bản cũ
/// đều phải còn nhận diện được.
/// </summary>
public sealed class CdpErrorsTests
{
    [Theory]
    // Bản Import-to-store (UpdateProduct)
    [InlineData("Execution context was destroyed, most likely because of a navigation.")]
    [InlineData("Cannot find context with specified id")]
    // Bản vòng điền form login (MultiBrave)
    [InlineData("Target closed")]
    [InlineData("The remote party closed the WebSocket connection")]
    // Bản lỗi service worker (runner extension)
    [InlineData("Inspected target navigated or closed")]
    public void NhanDienMarkerCuaCaBaBanCu(string message)
    {
        Assert.True(CdpErrors.IsTransientNavigationError(message));
    }

    [Fact]
    public void KhongPhanBietHoaThuong()
    {
        Assert.True(CdpErrors.IsTransientNavigationError("TARGET CLOSED"));
        Assert.True(CdpErrors.IsTransientNavigationError("cannot find context"));
    }

    [Fact]
    public void LoiThat_TraFalse()
    {
        Assert.False(CdpErrors.IsTransientNavigationError("Failed, log in BigSeller first"));
        Assert.False(CdpErrors.IsTransientNavigationError("CDP result thieu."));
    }

    [Fact]
    public void RongHoacNull_TraFalse()
    {
        Assert.False(CdpErrors.IsTransientNavigationError((string?)null));
        Assert.False(CdpErrors.IsTransientNavigationError("   "));
        Assert.False(CdpErrors.IsTransientNavigationError((Exception?)null));
    }

    [Fact]
    public void NhanCaException()
    {
        Assert.True(CdpErrors.IsTransientNavigationError(
            new InvalidOperationException("Execution context was destroyed")));
        Assert.False(CdpErrors.IsTransientNavigationError(new InvalidOperationException("het bo nho")));
    }
}
