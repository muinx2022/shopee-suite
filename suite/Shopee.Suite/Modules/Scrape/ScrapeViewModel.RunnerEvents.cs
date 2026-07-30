using System.IO;
using Shopee.Core.Accounts;
using Shopee.Core.BigSeller;
using Shopee.Core.Browser;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;
using Shopee.Modules.MultiBrave;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.Scrape;

// Partial của ScrapeViewModel: đấu event của ScrapeRunner ra lưới/log + dọn profile tk dính captcha — pure move.
public sealed partial class ScrapeViewModel
{
    private void WireRunner(ScrapeRunner runner, int seq, BigSellerAccount account)
    {
        var bigSellerName = account.DisplayName;
        string K(string key) => $"{seq}:{key}";   // namespace key theo job để nhiều BigSeller chạy đồng thời không đụng lưới
        // Đang Ở UI thread (trong OnUi) + cần tra Instances → ghi THẲNG cả 2 buffer, khỏi lồng OnUi thừa (LogAcc).
        runner.InstanceLog += (key, line) => OnUi(() =>
        {
            var inst = Instances.FirstOrDefault(x => x.Key == K(key));
            var text = $"[{bigSellerName}][{inst?.Label ?? key}] {line}";
            LogLines.Add(text);
            AccountLogs.Get(account.Id, bigSellerName).Add(text);
        });
        runner.InstanceStatus += (key, st) => OnUi(() =>
        {
            var inst = Instances.FirstOrDefault(x => x.Key == K(key));
            if (inst is not null) inst.Status = st;
        });
        runner.SlotAssigned += (key, account, range) => OnUi(() =>
        {
            var inst = Instances.FirstOrDefault(x => x.Key == K(key));
            if (inst is not null) { inst.AccountName = account; inst.RangeText = range; }
        });
        runner.AccountErrored += (id, label, reason, captchaUrl) => OnUi(() =>
        {
            // Lỗi NON-CAPTCHA (giữ nguyên): quarantine tk qua khu "Tài khoản bị lỗi" + đánh dấu bền + báo Hub.
            // (Captcha KHÔNG đi đường này nữa — xem AccountCaptchaDropped.)
            AccountErrorReporter.Report(ErroredAccounts, id, label, reason, "Scrape", captchaUrl);
            var text = $"⚠ Tk lỗi: {label} — {reason}";
            LogLines.Add(text);
            AccountLogs.Get(account.Id, bigSellerName).Add(text);
            // Cột "Tình trạng" → "⚠ Captcha" cho tk vừa dính lỗi trong lượt chạy này.
            ShopeeAccountUsage.Shared.MarkCaptcha(id);
        });
        runner.AccountCaptchaDropped += (id, label) =>
        {
            // Captcha khi scrape: tk đã bị LOẠI khỏi khung (đổi tk khác). KHÔNG đánh dấu tk lỗi (không
            // AccountErrorReporter/Disabled/lưới lỗi/MarkCaptcha/báo Hub) — tk vẫn ở pool, dùng lại với profile
            // mới. XÓA cả profile Brave scrape lẫn nguồn cookie → lần chạy sau ép login mới hoàn toàn.
            OnUi(() =>
            {
                var text = $"🚫 [{bigSellerName}] Tk Shopee \"{label}\" dính captcha → loại khỏi khung, xóa profile (login mới lần sau), đổi tk khác chạy tiếp.";
                LogLines.Add(text);
                AccountLogs.Get(account.Id, bigSellerName).Add(text);
            });
            // Xóa profile trên luồng nền (best-effort, không chặn UI, tự nuốt lỗi) — Brave của chunk đã StopAsync.
            _ = Task.Run(() => DeleteAccountProfilesBestEffort(id, label, account.Id, bigSellerName));
        };
        runner.JobFatal += reason =>
        {
            // LỖI HẠ TẦNG TOÀN CỤC (key proxy chết) → runner đã DỪNG job (không vá, KHÔNG bỏ dòng nào). Ghi lý do
            // gọn để AssignmentWorker báo 'failed' lên Hub; việc chạy TAY thì chỉ có dòng log này.
            _jobFatal[account.Id] = ScrapeFailurePolicy.FleetWideReason(reason);
            OnUi(() =>
            {
                var text = $"⛔ [{bigSellerName}] DỪNG job: {reason} — mọi tk Shopee dùng CHUNG key proxy này nên đổi tk vô ích. Gia hạn key rồi Tiếp tục.";
                LogLines.Add(text);
                AccountLogs.Get(account.Id, bigSellerName).Add(text);
            });
        };
        runner.BigSellerNeedLogin += reason =>
        {
            // Tk BigSeller mất phiên ("log in first") → job tk này đã bị dừng. NHỜ HUB đăng nhập lại ngay (hub có
            // mật khẩu + tự giải captcha + tự đọc mã verify từ hòm thư) rồi kéo cookie mới về — người dùng KHÔNG
            // phải ra tận máy đăng nhập tay. Hub chưa cấu hình → coordinator null, giữ nguyên hành vi cũ (chỉ log).
            CoordinationRuntime.Relogin?.Request(account.Id, reason);
            OnUi(() =>
            {
                var text = CoordinationRuntime.Relogin is null
                    ? $"⛔ [{bigSellerName}] BigSeller mất đăng nhập: {reason} — đã DỪNG job tk này. Hãy ĐĂNG NHẬP LẠI BigSeller rồi chạy lại."
                    : $"⛔ [{bigSellerName}] BigSeller mất đăng nhập: {reason} — đã nhờ Hub đăng nhập lại, cookie mới sẽ tự về, việc sẽ chạy lại.";
                LogLines.Add(text);
                AccountLogs.Get(account.Id, bigSellerName).Add(text);
            });
        };
    }

    /// <summary>Xóa CẢ 2 profile của tk Shopee vừa dính captcha → lần chạy sau ép login MỚI hoàn toàn:
    ///  • <c>persistent-data/profiles/{Id}</c> — profile Brave scrape (đã import phiên login sang);
    ///  • <c>shared/profiles/{Id}</c> — nguồn cookie đã lưu (bỏ để buộc nhập lại tk, không nạp phiên cũ).
    /// Best-effort qua <see cref="BraveCachePolicy.DeleteDirBestEffort"/> (gỡ read-only + retry, KHÔNG ném) →
    /// còn khóa thì StartupJanitor dọn nốt lần mở sau, không chặn vòng scrape. Chạy trên luồng nền; log về UI
    /// qua <see cref="LogAcc"/> (tự marshal). accountId = tk Shopee cần xóa; bsId/bsName = job BigSeller để ghi log.</summary>
    private void DeleteAccountProfilesBestEffort(string accountId, string label, string bsId, string bsName)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        try
        {
            var braveProfile = Path.Combine(SuitePaths.ModuleDir("persistent-data"), "profiles", accountId);
            var cookieProfile = Path.Combine(SuitePaths.ModuleDir("shared"), "profiles", accountId);
            var freed = BraveCachePolicy.DeleteDirBestEffort(braveProfile)
                      + BraveCachePolicy.DeleteDirBestEffort(cookieProfile);
            var leftover = Directory.Exists(braveProfile) || Directory.Exists(cookieProfile);
            LogAcc(bsId, bsName, leftover
                ? $"⚠ [{bsName}] Xóa profile tk \"{label}\" chưa sạch (còn khóa?) — giải phóng ~{freed / 1024 / 1024} MB; StartupJanitor dọn nốt lần mở sau."
                : $"🗑 [{bsName}] Đã xóa profile tk \"{label}\" (Brave scrape + nguồn cookie), ~{freed / 1024 / 1024} MB — lần chạy sau login mới.");
        }
        catch (Exception ex)
        {
            LogAcc(bsId, bsName, $"⚠ [{bsName}] Lỗi xóa profile tk \"{label}\": {ex.Message}");
        }
    }
}
