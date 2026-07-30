using Shopee.Core.BigSeller;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// GIỮ TOKEN BigSeller (<c>muc_token</c>) SỐNG cho một instance: tự đăng nhập đầu phiên (mint token khớp IP
/// proxy của Brave này), nạp cookie từ file CHỈ khi thật sự cần, và ghi ngược token server-vừa-xoay về file
/// sau mỗi dòng cào xong. Toàn bộ quy tắc "khi nào được đè token" nằm ở đây — đè sai là mất phiên
/// ("log in BigSeller first"), nên tách khỏi <see cref="BraveInstanceSession"/> để đọc/kiểm được một chỗ.
/// </summary>
internal sealed class BigSellerTokenGuard(
    int cdpPort,
    Func<InstanceConfig?> config,
    Func<bool> isRunning,
    Action<string> log)
{
    /// <summary>
    /// File cookie BigSeller của account này — được lưu từ phiên đăng nhập không proxy (IP máy thật).
    /// Khi instance chạy, cookies này sẽ được inject vào browser qua CDP (kết nối local, không qua proxy).
    /// </summary>
    private string _cookieFile = "";

    public void SetCookieFile(string? cookieFile) => _cookieFile = cookieFile?.Trim() ?? "";

    /// <summary>Có cấu hình file cookie cho tk này không (không có → mọi bước token đều bỏ qua).</summary>
    public bool HasCookieFile => !string.IsNullOrWhiteSpace(_cookieFile);

    /// <summary>
    /// Proxy RIÊNG cho tk BigSeller (KiotProxy key/region/type). Có key → traffic bigseller.com đi qua
    /// proxy này (split-tunnel PAC, xem BraveProfileManager) thay vì IP máy → mỗi tk BigSeller 1 IP.
    /// Phân giải LẠI mỗi lần mở Brave (ưu tiên /current sticky) để không dùng IP đã hết hạn.
    /// </summary>
    private string _proxyKey = "";
    private string _proxyRegion = "random";
    private string _proxyType = "http";

    public void SetProxy(string? key, string? region, string? proxyType)
    {
        _proxyKey = key?.Trim() ?? "";
        _proxyRegion = string.IsNullOrWhiteSpace(region) ? "random" : region!.Trim();
        _proxyType = string.IsNullOrWhiteSpace(proxyType) ? "http" : proxyType!.Trim();
    }

    // BIGSELLER LUÔN ĐI IP MÁY (direct/bypass) — CHỦ ĐÍCH tắt proxy riêng cho BigSeller.
    // Lý do: proxy riêng (/current·/new) phân giải LẠI mỗi lần mở Brave, không đồng bộ giữa các lane →
    // IP đổi giữa chừng / nhiều IP trên 1 token → server đá phiên. Đây vốn là bản vá muộn không hiệu quả.
    // Trả null → BraveProfileManager dùng --proxy-bypass-list cho bigseller.* (đi IP máy ổn định).
    // (Muốn bật lại proxy riêng theo tk: khôi phục bản gọi BigSellerProxyResolver.ResolveServerAsync.)
    public Task<string?> ResolveProxyServerAsync() => Task.FromResult<string?>(null);

    /// <summary>Browser của instance này HIỆN có muc_token BigSeller sống không (qua CDP). Dùng để verify
    /// "BigSeller có out thật không": nếu 1 process báo login-first mà process ANH EM cùng khung vẫn còn
    /// token sống → account CÒN sống (chỉ process kia glitch), KHÔNG dừng cả job.</summary>
    public async Task<bool> HasAuthAsync()
    {
        if (!isRunning()) return false;
        try { return await BigSellerCookieEngine.HasAuthCookieInBrowserAsync(cdpPort).ConfigureAwait(false); }
        catch { return false; }
    }

    /// <summary>Kiểm token trong browser KHÔNG kèm guard/nuốt lỗi — dùng ở nhánh mở lại profile sau lỗi
    /// extension, nơi caller cần biết chính xác còn token hay không để quyết định có nạp đè hay không.</summary>
    public Task<bool> BrowserHasAuthCookieAsync() =>
        BigSellerCookieEngine.HasAuthCookieInBrowserAsync(cdpPort);

    /// <summary>
    /// GHI NGƯỢC token BigSeller server-VỪA-XOAY về FILE sau khi cào xong 1 dòng. Server xoay muc_token mỗi
    /// lần dùng → token TĨNH trong file "ôi" dần; process khác rớt token mà nạp lại token ôi = đập server
    /// bằng token chết → chuỗi auth-thất-bại dồn dập → BigSeller đá nguyên account. Lưu token MỚI NHẤT (từ
    /// browser vừa cào THÀNH CÔNG nên chắc chắn còn sống) vào file → process khác nạp lại token HIỆN HÀNH →
    /// tự hồi, không đập server. Best-effort: lỗi ghi không được làm hỏng vòng cào.
    /// </summary>
    public async Task WriteBackTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_cookieFile) || !isRunning() || cancellationToken.IsCancellationRequested)
            return;
        try
        {
            var cookies = await BigSellerCookieEngine.GetBigSellerCookiesAsync(cdpPort).ConfigureAwait(false);
            // Chỉ ghi khi token còn sống (có muc_token) — tránh ghi đè file bằng cookie rỗng/chết.
            if (!BigSellerCookieEngine.HasAuthCookie(cookies))
                return;
            BigSellerCookieEngine.TryWriteCookieFile(_cookieFile, cookies, log);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Tk BigSeller của instance này (theo AccountId) đã nhập mật khẩu chưa — để quyết định có thử
    /// tự đăng nhập lại khi mất phiên giữa chừng không.</summary>
    public bool HasPassword()
    {
        var cfg = config();
        if (string.IsNullOrWhiteSpace(cfg?.AccountId)) return false;
        var acct = BigSellerStore.Shared.Accounts.FirstOrDefault(a => a.Id == cfg.AccountId);
        return acct is not null && !string.IsNullOrWhiteSpace(acct.Password);
    }

    /// <summary>Phase 4c — ĐẦU PHIÊN: tự đăng nhập BigSeller ngay TRONG Brave scrape (khớp IP proxy) để có
    /// token tươi. Resolve email/mật khẩu từ kho BigSeller theo <c>AccountId</c> của instance; TTL + attach
    /// CDP + xuất token do <see cref="BigSellerAutoLogin.EnsureFreshSessionAsync"/> lo. AN TOÀN: chưa nhập mật
    /// khẩu → im lặng bỏ qua; mọi lỗi rơi về nạp cookie-file như cũ (không phá luồng scrape).</summary>
    public async Task TryAutoLoginAsync(CancellationToken ct)
    {
        var cfg = config();
        if (!isRunning() || string.IsNullOrWhiteSpace(cfg?.AccountId)) return;
        var acct = BigSellerStore.Shared.Accounts.FirstOrDefault(a => a.Id == cfg.AccountId);
        if (acct is null || string.IsNullOrWhiteSpace(acct.Password)) return;   // chưa nhập pass → dùng cookie file như cũ
        try
        {
            await BigSellerAutoLogin.EnsureFreshSessionAsync(
                cdpPort, cfg.AccountId, acct.Email, acct.Password, _cookieFile, log, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log($"BigSeller auto-login (bỏ qua, dùng cookie file): {ex.Message}"); }
    }

    /// <summary>
    /// Import BigSeller cookie từ file account vào browser qua CDP.
    /// CDP là kết nối WebSocket local — KHÔNG đi qua proxy của instance.
    /// </summary>
    public async Task ImportCookiesIfConfiguredAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_cookieFile) || !isRunning())
            return;

        // CHẨN ĐOÁN "token mất đi đâu": ghi token ĐANG có trong browser TRƯỚC khi import (để biết import
        // có ghi đè token đang sống không, và token có hết hạn không).
        var tokenBefore = await BigSellerCookieEngine.GetAuthCookieDebugAsync(cdpPort).ConfigureAwait(false);
        // Log RÕ tk BigSeller + file cookie đang nạp (để bắt nếu multi-BigSeller nạp nhầm file của tk khác).
        log($"BigSeller nạp cookie: tk=\"{config()?.BigSellerAccountName ?? "?"}\" file=\"{Path.GetFileName(_cookieFile)}\"");
        log($"BigSeller token (trước import): {tokenBefore}");

        // ─── GIỮ TOKEN SỐNG — chống "log in first" sau vài vòng ──────────────────────────────────────
        // Server BigSeller XOAY muc_token mỗi khi cào → profile bền của instance giữ token MỚI HƠN token
        // TĨNH trong file. Trước đây hàm này LUÔN nhập đè token-file lên token-đang-sống ⇒ ép browser quay
        // về token cũ ⇒ sau vài vòng server đã xoay qua từ lâu nên TỪ CHỐI ⇒ "log in BigSeller first"
        // (xảy ra ở CẢ đi-IP-máy lẫn đi-proxy-instance vì lỗi nằm ở lệnh đè, không phải IP).
        // Quy tắc mới (cùng tinh thần đường restart-extension ở trên): chỉ nhập từ file khi
        //   • browser CHƯA có muc_token (seed lần đầu / profile mới), HOẶC
        //   • token trong file MỚI HƠN browser theo hạn (tức user vừa ĐĂNG NHẬP LẠI → file cập nhật).
        // Nếu token browser mới hơn/bằng hoặc TRÙNG giá trị file ⇒ GIỮ phiên server vừa cấp, KHÔNG đè.
        var browserTok = await BigSellerCookieEngine.GetBrowserAuthTokenInfoAsync(cdpPort).ConfigureAwait(false);
        if (browserTok is { } bt)
        {
            var fileTok = BigSellerCookieEngine.GetFileAuthTokenInfo(_cookieFile);
            var sameValue = fileTok is { } ft && string.Equals(ft.Value, bt.Value, StringComparison.Ordinal);
            // File mới hơn khi: file CÓ hạn rõ VÀ (browser KHÔNG hạn — session cookie, thường do user vừa
            // login lại → cần nạp token mới; HOẶC cả hai có hạn nhưng file trễ hơn). Cả hai có hạn mà browser
            // ≥ file ⇒ server vừa xoay ⇒ GIỮ (đây là fix bug "login first sau vài vòng", KHÔNG được phá).
            var fileNewer = fileTok is { Expires: { } fe } && (bt.Expires is not { } be || fe > be);
            if (sameValue || !fileNewer)
            {
                log("BigSeller: browser đã có muc_token sống (server vừa xoay) — GIỮ phiên, KHÔNG nạp đè token cũ từ file.");
                return;
            }
            log("BigSeller: token trong file MỚI HƠN browser (có thể bạn vừa đăng nhập lại) → nạp đè để cập nhật.");
        }

        // Nạp + XÁC NHẬN có muc_token; thử lại tối đa 3 lần (import lần đầu hay flaky do CDP/page chưa
        // sẵn sàng → trước đây nuốt lỗi 1 lần là instance đó dính "login bigseller first").
        for (var attempt = 1; attempt <= 3 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await BigSellerCookieEngine.ImportFromFileAsync(
                    cdpPort, _cookieFile, log,
                    reloadBigSellerTabs: false, navigateUrl: null, cancellationToken).ConfigureAwait(false);
                if (await BigSellerCookieEngine.HasAuthCookieInBrowserAsync(cdpPort).ConfigureAwait(false))
                {
                    var tokenAfter = await BigSellerCookieEngine.GetAuthCookieDebugAsync(cdpPort).ConfigureAwait(false);
                    log($"BigSeller token (sau import, lần {attempt}): {tokenAfter}");
                    return;
                }
                log($"BigSeller cookie: chưa thấy muc_token sau khi nạp — thử lại ({attempt}/3)…");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log($"BigSeller cookie (lần {attempt}): {ex.Message}"); }
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }
        log("⚠ BigSeller cookie: KHÔNG nạp được muc_token sau 3 lần — instance này có thể bị 'login bigseller first'.");
    }
}
