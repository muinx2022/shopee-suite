using Microsoft.Playwright;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Trình duyệt <b>Playwright RIÊNG</b> chỉ để mở hộp thư Hotmail/Outlook cho người dùng tự đọc mã xác thực khi
/// đăng nhập Shopee qua cầu nối extension (GĐ2). TÁCH khỏi trình duyệt "sạch" điều khiển Shopee: domain Microsoft
/// (login.microsoftonline.com / outlook.live.com) KHÔNG bị Shopee anti-bot soi → dùng Playwright bình thường là đủ,
/// tái dùng nguyên luồng <see cref="ShopeeLoginService.OpenMailboxSignedInAsync"/> (đăng nhập Microsoft + vào hộp thư).
/// <para>
/// GĐ2 (đã đơn giản hoá theo yêu cầu): CHỈ một chế độ <see cref="OpenForManualCodeAsync"/> — đăng nhập hộp thư rồi
/// DỪNG, GIỮ cửa sổ mở để NGƯỜI DÙNG tự đọc mã và gõ vào form Shopee. KHÔNG tự tìm mail, KHÔNG bấm link xác nhận.
/// </para>
/// </summary>
public sealed class OrdersMailboxSession : IAsyncDisposable
{
    private readonly string _userDataDir;
    private readonly BrowserChoice _browserChoice;
    private readonly Action<string>? _log;
    private readonly Random _rng = new();

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _mailPage;

    /// <param name="userDataDir">Thư mục hồ sơ persistent RIÊNG cho hộp thư (tách khỏi hồ sơ Shopee) — giữ đăng
    /// nhập Microsoft giữa các lần chạy.</param>
    /// <param name="browserChoice">Lựa chọn trình duyệt (dùng chung cấu hình app); không có trình duyệt thật thì
    /// rơi về Chromium đóng gói của Playwright.</param>
    public OrdersMailboxSession(string userDataDir, BrowserChoice browserChoice, Action<string>? log = null)
    {
        _userDataDir = userDataDir;
        _browserChoice = browserChoice;
        _log = log;
    }

    private void L(string m) => _log?.Invoke(m);

    /// <summary>
    /// Mở trình duyệt hộp thư (Playwright riêng) + đăng nhập Hotmail/Outlook rồi DỪNG cho người dùng tự đọc mã.
    /// Trả về <c>true</c> nếu tự đăng nhập được hộp thư (best-effort — login lỗi vẫn GIỮ cửa sổ mở để user tự
    /// đăng nhập, trả <c>false</c>). Ném <see cref="OperationCanceledException"/> khi bị hủy.
    /// </summary>
    public async Task<bool> OpenForManualCodeAsync(
        string verifyEmail, string verifyEmailPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(verifyEmail) || string.IsNullOrWhiteSpace(verifyEmailPassword))
        {
            L("Chưa cấu hình Email xác minh — không mở hộp thư (bạn tự lấy mã).");
            return false;
        }

        L("Mở trình duyệt hộp thư (Playwright riêng) để bạn tự đọc mã xác thực...");
        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);

        // Trình duyệt thật đã cài → dùng luôn (đỡ tải Chromium); không có → để trống ExecutablePath (Chromium đóng gói).
        var exe = BrowserLocator.ResolveExecutable(_browserChoice);
        System.IO.Directory.CreateDirectory(_userDataDir);

        var opts = new BrowserTypeLaunchPersistentContextOptions { Headless = false };
        if (!string.IsNullOrEmpty(exe))
        {
            opts.ExecutablePath = exe;
        }
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(_userDataDir, opts).ConfigureAwait(false);

        var (mailPage, ok) = await ShopeeLoginService
            .OpenMailboxSignedInAsync(_context, verifyEmail, verifyEmailPassword, _log, _rng, ct)
            .ConfigureAwait(false);
        _mailPage = mailPage;

        L(ok
            ? "Đã đăng nhập hộp thư — đọc mã rồi gõ vào cửa sổ Shopee."
            : "Chưa tự đăng nhập được hộp thư — GIỮ cửa sổ mail mở để bạn tự đăng nhập + lấy mã.");
        return ok;
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_context is not null) await _context.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
        try { _playwright?.Dispose(); } catch { /* bỏ qua */ }
        _context = null;
        _playwright = null;
        _mailPage = null;
    }
}
