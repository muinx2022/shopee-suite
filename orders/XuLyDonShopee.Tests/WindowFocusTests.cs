using System.Diagnostics;
using Xunit;
using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test tính AN TOÀN của <see cref="WindowFocus.BringToFront"/> (best-effort, không được ném). Phần đưa
/// cửa sổ Brave thật ra trước cần môi trường GUI + tiến trình có cửa sổ → kiểm ở smoke/tay; ở đây chỉ
/// đảm bảo các nhánh biên (null / process không có cửa sổ) KHÔNG ném và không phá luồng.
/// </summary>
public class WindowFocusTests
{
    [Fact]
    public void BringToFront_Null_KhongNem()
    {
        var ex = Record.Exception(() => WindowFocus.BringToFront(null));
        Assert.Null(ex);
    }

    [Fact]
    public void BringToFront_ProcessKhongCoCuaSo_KhongNem()
    {
        // Tiến trình test-host không có MainWindowHandle (=> nhánh handle == 0). Phải trả về êm, không ném.
        using var current = Process.GetCurrentProcess();
        var ex = Record.Exception(() => WindowFocus.BringToFront(current));
        Assert.Null(ex);
    }
}
