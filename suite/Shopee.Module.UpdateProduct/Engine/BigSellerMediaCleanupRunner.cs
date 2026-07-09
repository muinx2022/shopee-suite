using Microsoft.Playwright;

namespace UpdateProduct;

/// <summary>Runner ĐỘC LẬP xóa sạch Material Center theo yêu cầu (nút "Xóa Medias" ở trang cấu hình
/// BigSeller): mở Brave riêng (profile/port riêng, không đụng lane update đang chạy) → nạp cookie tk
/// → chạy wipe của <see cref="BigSellerMaterialCenterCleaner"/> → đóng Brave (DisposeAsync của base).</summary>
internal sealed class BigSellerMediaCleanupRunner : BigSellerBraveRunner
{
    // exportCookie:false = KHÔNG ghi cookie ngược ra file → tránh rotation-war muc_token với lane update.
    public BigSellerMediaCleanupRunner(BigSellerWorkflowSettings settings, Action<string> log)
        : base(settings, log, pauseToken: null, exportCookie: false) { }

    protected override string StartUrl => BigSellerMaterialCenterCleaner.MaterialCenterUrl;

    public async Task RunAsync(CancellationToken ct)
    {
        if (!File.Exists(_settings.BravePath))
            throw new FileNotFoundException($"Khong tim thay Brave: {_settings.BravePath}");

        StartBrave();
        _log($"Đã gọi Brave PID={_braveProcess?.Id.ToString() ?? "?"}, chờ CDP port {_settings.DebugPort}...");
        await EnsureCdpReadyAsync(30,
            $"CDP port {_settings.DebugPort} không sẵn sàng. Đóng Brave BigSeller cũ rồi chạy lại.", ct)
            .ConfigureAwait(false);

        await EnsureCookieAsync(ct).ConfigureAwait(false);

        _log($"Kết nối CDP port {_settings.DebugPort}...");
        await ConnectBrowserAsync(ct).ConfigureAwait(false);

        IBrowserContext context = _browser!.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("Brave chưa có browser context.");

        // Tab đầu (mở sẵn ở StartUrl) → tự mint token tươi đầu phiên như Update runner (cookie thiu thì login lại).
        IPage? page = context.Pages.FirstOrDefault();
        if (page is not null)
        {
            await page.BringToFrontAsync();
            await BigSellerAutoLogin.EnsureFreshSessionAsync(   // Phase 4b: đầu phiên tự mint token tươi (mỗi máy tự login)
                page, _settings.AccountId, _settings.Email, _settings.Password,
                _settings.BigSellerCookieFile, _settings.DebugPort, exportCookie: false, _log, ct).ConfigureAwait(false);
        }

        // claim:null → wipe chạy thẳng (phiên độc lập, không tranh khoá đa-lane); overlay no-op (không có runner update).
        var cleaner = new BigSellerMaterialCenterCleaner(context, claim: null, _log, DelayAsync, _ => Task.CompletedTask);
        await cleaner.RunMediaCleanupLockedAsync(ct).ConfigureAwait(false);
        _log("✔ Xóa media (Material Center) xong.");
    }
}
