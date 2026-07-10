using Shopee.Core.BigSeller;
using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Tự động re-login TOÀN BỘ acc BigSeller định kỳ ~7 ngày để device-trust (fingerPrint, TTL ~15 ngày) không bao
/// giờ hết hạn → không bao giờ bị đòi mã email ĐỒNG LOẠT (sự cố 2026-07-11). RẢI CÁCH QUÃNG theo yêu cầu user:
/// tick mỗi 60' xử lý TỐI ĐA 1 acc → không bao giờ có 2 login sát nhau (nhiều login liên tiếp từ 1 IP làm BigSeller
/// siết cả cụm). Vì <c>exportedAt</c> của mỗi acc được làm mới ở giờ khác nhau, các tuần sau mỗi acc tự giữ slot giờ
/// riêng → vĩnh viễn cách quãng. Điểm A của BigSellerLoginService (seed device-trust) đảm bảo các re-login này
/// captcha-only, KHÔNG đòi OTP khi trust còn.
/// </summary>
public sealed class BigSellerReloginScheduler : BackgroundService
{
    // fingerPrint TTL ~15 ngày → re-login mỗi 7 ngày (nửa chu kỳ) là dư an toàn kể cả khi 1-2 đợt trễ vì máy tắt.
    private const int IntervalDays = 7;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(60);   // 1 acc/giờ → rải cách quãng
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);    // đừng đua lúc app boot
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromHours(24);    // acc thử-hỏng → hoãn 24h (khỏi nã lại mỗi giờ)
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(15);

    private readonly BigSellerLoginService _login;
    private readonly FileStoreConfigService _config;
    private readonly HubDatabase _db;
    private readonly ILogger<BigSellerReloginScheduler> _log;
    // acctId → thời điểm thử gần nhất mà KHÔNG thành công (failed/needsOtp/kẹt) → hoãn thử lại RetryCooldown.
    private readonly Dictionary<string, DateTimeOffset> _lastAttempt = new();

    public BigSellerReloginScheduler(
        BigSellerLoginService login, FileStoreConfigService config, HubDatabase db, ILogger<BigSellerReloginScheduler> log)
    { _login = login; _config = config; _db = db; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            using var timer = new PeriodicTimer(TickInterval);
            while (!ct.IsCancellationRequested)
            {
                try { await TickAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogWarning(ex, "relogin scheduler tick failed"); }
                if (!await timer.WaitForNextTickAsync(ct)) break;   // chờ SAU khi tick → ~60' giữa 2 lần bắt đầu
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Đừng chen phiên admin đang login/chờ OTP trên UI (1 browser dùng chung + tránh 2 login sát nhau).
        if (_login.AnyActive) return;

        var accounts = _config.BigSellerAccounts()
            .Where(a => !string.IsNullOrWhiteSpace(a.Email) && !string.IsNullOrWhiteSpace(a.Password))
            .ToList();
        if (accounts.Count == 0) return;

        var now = DateTimeOffset.UtcNow;

        // Chọn acc ĐẾN HẠN (cookie > 7 ngày tuổi HOẶC thiếu file) CŨ NHẤT; bỏ acc vừa thử-hỏng trong 24h.
        BigSellerAccount? pick = null;
        var pickAt = DateTimeOffset.MaxValue;   // exportedAt của acc được chọn — càng nhỏ = càng cũ = ưu tiên trước
        foreach (var a in accounts)
        {
            if (_lastAttempt.TryGetValue(a.Id, out var last) && now - last < RetryCooldown) continue;
            var exportedAt = ReadCookieExportedAt(a.Id);              // null = thiếu file/parse fail → coi quá hạn từ lâu
            var effAt = exportedAt ?? DateTimeOffset.MinValue;
            if (now - effAt < TimeSpan.FromDays(IntervalDays)) continue;   // chưa tới hạn 7 ngày
            if (effAt < pickAt) { pick = a; pickAt = effAt; }
        }
        if (pick is null) return;   // không acc nào tới hạn / tất cả đang trong cooldown

        var ageStr = pickAt == DateTimeOffset.MinValue ? "chưa có cookie file" : $"{(now - pickAt).TotalDays:F1} ngày tuổi";
        _log.LogInformation("relogin định kỳ: {Email} (cookie {Age})…", pick.Email, ageStr);
        if (!_login.Start(pick.Id, pick.Email, pick.Password))
        {
            _log.LogInformation("relogin: {Email} — có phiên khác đang chạy, để tick sau.", pick.Email);
            return;   // không đánh dấu _lastAttempt (chỉ bận nhất thời)
        }

        // Poll trạng thái tới terminal (success/failed) / needsOtp / trần 15'. 1 acc/tick.
        var deadline = now + LoginTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var st = _login.GetState(pick.Id);
            if (st is null) break;
            switch (st.Status)
            {
                case "success":
                    _lastAttempt.Remove(pick.Id);   // thành công → không cooldown
                    _log.LogInformation("relogin: {Email} ✔ thành công — device-trust gia hạn.", pick.Email);
                    return;
                case "failed":
                    _lastAttempt[pick.Id] = DateTimeOffset.UtcNow;
                    _log.LogWarning("relogin: {Email} ✘ thất bại — hoãn thử lại 24h. ({Msg})", pick.Email, st.Message);
                    return;
                case "needsOtp":
                    // Device-trust đã chết → BigSeller đòi mã email; scheduler KHÔNG tự nhập được → để phiên đó chờ
                    // admin trên UI (AccountConfigPanel hiển thị state qua GetState). Hoãn tự-thử acc này 24h.
                    _lastAttempt[pick.Id] = DateTimeOffset.UtcNow;
                    _log.LogWarning("relogin: acc {Email} CẦN xác minh mã email TAY trên trang cấu hình acc của hub (device-trust hết hạn). Hoãn tự-thử 24h.", pick.Email);
                    return;
            }
        }
        // Hết trần 15' mà chưa terminal (kẹt) → cooldown để khỏi nã lại giờ sau.
        _lastAttempt[pick.Id] = DateTimeOffset.UtcNow;
        _log.LogWarning("relogin: {Email} — quá 15' chưa xong, bỏ qua (hoãn 24h).", pick.Email);
    }

    /// <summary>Đọc <c>exportedAt</c> từ kho <c>cookies/{acctId}.json</c>. Thiếu file / parse fail → null (caller coi
    /// như quá hạn từ lâu → login lại từ đầu, có thể needsOtp → admin làm tay, chấp nhận).</summary>
    private DateTimeOffset? ReadCookieExportedAt(string acctId)
    {
        try
        {
            var bytes = _db.ReadFile($"cookies/{acctId}.json");
            if (bytes is null || bytes.Length == 0) return null;
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("exportedAt", out var e) && e.TryGetDateTimeOffset(out var dt)) return dt;
        }
        catch { }
        return null;
    }
}
