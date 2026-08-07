using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho hàm thuần <see cref="BraveLaunchArgs.BuildBraveArgs"/> — chuỗi tham số launch Brave/Chromium.
/// </summary>
public class BraveLaunchArgsTests
{
    [Fact]
    public void CoUserDataDir_RemoteDebuggingPort_KhongEpWebdriver()
    {
        var args = BraveLaunchArgs.BuildBraveArgs(@"C:\profiles\1", 9222, null);

        // KHÔNG ép navigator.webdriver=false nữa (khớp shopee-suite — Shopee không gate theo webdriver).
        // Không còn cờ nào tắt AutomationControlled, cũng không dùng họ --disable-blink-features.
        Assert.DoesNotContain(args, a => a.Contains("--disable-blink-features"));
        Assert.DoesNotContain(args, a => a.Contains("AutomationControlled"));
        Assert.Contains(@"--user-data-dir=C:\profiles\1", args);
        Assert.Contains("--remote-debugging-port=9222", args);
    }

    [Fact]
    public void CoNhomCoChongTreoNen()
    {
        // Nhóm cờ khớp shopee-suite: chống Brave bóp renderer khi cửa sổ nền + mở đúng profile/cửa sổ mới.
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0);

        Assert.Contains("--disable-background-timer-throttling", args);
        Assert.Contains("--disable-backgrounding-occluded-windows", args);
        Assert.Contains("--disable-renderer-backgrounding", args);
        Assert.Contains("--profile-directory=Default", args);
        Assert.Contains("--new-window", args);
        Assert.Contains("--hide-crash-restore-bubble", args);
    }

    [Fact]
    public void CoDisableFeaturesOnDinh_KhongCoAutomationControlled()
    {
        // Chuỗi --disable-features đúng như shopee-suite: Translate + CalculateNativeWinOcclusion +
        // IntensiveWakeUpThrottling — và tuyệt đối KHÔNG chứa AutomationControlled. Nối thêm nhóm chặn tải
        // model AI on-device do BraveArgs bổ sung cho MỌI call-site (~4 GB/hồ sơ nếu để Chrome tự tải).
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0);

        Assert.Contains(
            "--disable-features=Translate,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling"
            + ",OptimizationGuideOnDeviceModel,OptimizationGuideModelDownloading,TextSafetyClassifier",
            args);
        Assert.DoesNotContain(args, a => a.StartsWith("--disable-features") && a.Contains("AutomationControlled"));
    }

    /// <summary>Chromium giữ switch trong map theo TÊN: có hai cờ <c>--disable-features</c> thì chỉ một cái sống,
    /// cái kia mất trắng (mất <c>DisableLoadExtensionCommandLineSwitch</c> = extension ngừng nạp, không báo lỗi).
    /// Chốt để không ai "sửa nhanh" bằng cách thêm cờ thứ hai.</summary>
    [Fact]
    public void ChiCoDungMotCoDisableFeatures()
    {
        Assert.Single(BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0),
            a => a.StartsWith("--disable-features="));
        Assert.Single(BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, "/tmp/ext"),
            a => a.StartsWith("--disable-features="));
    }

    /// <summary>Model AI on-device (3,98 GB/hồ sơ) được CÀI QUA COMPONENT UPDATER về gốc user-data-dir → cờ này
    /// là đường chặn trực tiếp nhất, nhóm feature AI là lớp thứ hai. Orders TỪNG là đường phóng duy nhất thiếu
    /// nó và cũng là nơi duy nhất đo được rò rỉ (07/08/2026) — đừng bỏ đi.</summary>
    [Fact]
    public void CoChanComponentUpdater()
    {
        Assert.Contains("--disable-component-update", BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0));
        Assert.Contains("--disable-component-update", BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, "/tmp/ext"));
    }

    [Fact]
    public void CoExtension_VanGiuCoChoPhepLoadExtension_VaCoChanModelAi()
    {
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0, "/tmp/ext");

        var co = Assert.Single(args, a => a.StartsWith("--disable-features="));
        Assert.Contains("DisableLoadExtensionCommandLineSwitch", co);
        Assert.Contains("OptimizationGuideOnDeviceModel", co);
        Assert.Contains("OptimizationGuideModelDownloading", co);
        Assert.Contains("TextSafetyClassifier", co);
    }

    [Fact]
    public void CoCoLocaleTiengViet()
    {
        // Locale VN đặt bằng cờ trình duyệt (không hook navigator.languages bằng JS để tránh lộ bot).
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0);

        Assert.Contains("--lang=vi-VN", args);
    }

    [Fact]
    public void CoCoDisablePopupBlocking()
    {
        // Nút "In phiếu giao" mở tab phiếu bằng window.open — không chặn popup để tab phiếu luôn mở ra
        // (nếu bị chặn thì không bắt được tab để tải/in). Cờ này BẮT BUỘC có cho bước In phiếu giao.
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0);

        Assert.Contains("--disable-popup-blocking", args);
    }

    [Fact]
    public void KhongChua_EnableAutomation_VaKhongChua_Headless()
    {
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0);

        // Không có bất kỳ tham số nào bật cờ automation hoặc chạy ẩn.
        Assert.DoesNotContain(args, a => a.Contains("--enable-automation"));
        Assert.DoesNotContain(args, a => a.Contains("--headless"));
        Assert.DoesNotContain(args, a => a.Contains("--remote-debugging-pipe"));
    }

    /// <summary>Module Đơn hàng đã bỏ hẳn proxy runtime — args KHÔNG bao giờ được mang <c>--proxy-server</c>
    /// nữa (cụm xoay proxy + ProxyHealthChecker đã xoá). Chốt để đừng ai lặng lẽ nối lại.</summary>
    [Fact]
    public void KhongBaoGioCoProxyServer()
    {
        var args = BraveLaunchArgs.BuildBraveArgs("/tmp/p", 0);

        Assert.DoesNotContain(args, a => a.StartsWith("--proxy-server"));
    }
}
