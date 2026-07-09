using System.Collections.ObjectModel;
using Shopee.Core.Accounts;
using Shopee.Core.Coordination;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Gom xử lý "tk Shopee dính captcha/lỗi lúc chạy" — trước là 2 bản near-verbatim <c>FlagAccountErrored</c> +
/// wiring dòng <see cref="ErroredAccountRow"/> ở Scrape ⇄ Search:
///  • UPSERT 1 dòng vào lưới hiển thị (theo Id) để người dùng thấy tk nào lỗi;
///  • đánh dấu BỀN account (Disabled + LastError + CaptchaUrl) → gom ở "Tài khoản & Proxy" (lọc "Bị lỗi /
///    captcha") xử lý sau rồi "Bật lại"; và (CLIENT) báo Hub để operator quyết giữ/xóa.
/// Gọi trên UI thread (đụng ObservableCollection). Phần RIÊNG từng module (dòng log, MarkCaptcha) để ở caller.
/// </summary>
public static class AccountErrorReporter
{
    /// <summary>Upsert dòng tk lỗi vào <paramref name="rows"/> + đánh dấu bền + báo Hub. <paramref name="reason"/>
    /// là lý do thô (hiển thị ở lưới); <paramref name="moduleTag"/> ("Scrape"/"Search") ghép vào lý do BỀN lưu
    /// account. <paramref name="captchaUrl"/> = URL/link lúc dính captcha (rỗng → giữ URL cũ đã lưu).</summary>
    public static void Report(ObservableCollection<ErroredAccountRow> rows, string id, string label,
        string reason, string moduleTag, string? captchaUrl)
    {
        var now = DateTime.Now.ToString("HH:mm:ss");
        var row = rows.FirstOrDefault(x => x.Id == id);
        if (row is null) rows.Insert(0, new ErroredAccountRow(id, label, reason, now));
        else { row.Reason = reason; row.Time = now; }

        Flag(id, $"Dính captcha/lỗi ({moduleTag}) — {DateTime.Now:dd/MM HH:mm}: {reason}", captchaUrl);
    }

    // Đánh dấu BỀN account dính captcha/lỗi: Disabled (tự bỏ qua lượt sau) + LastError → gom ở "Tài khoản &
    // Proxy". Lưu URL captcha để "Kiểm tra tk lỗi" mở đúng trang/link đó (thay vì auto-login).
    private static void Flag(string id, string reason, string? captchaUrl)
    {
        var acc = AccountStore.Shared.Accounts.FirstOrDefault(a => a.Id == id);
        if (acc is null) return;
        var alreadyFlagged = acc.Disabled;
        acc.Disabled = true;
        acc.LastError = reason;
        if (!string.IsNullOrWhiteSpace(captchaUrl)) acc.CaptchaUrl = captchaUrl;
        if (!alreadyFlagged || !string.IsNullOrWhiteSpace(captchaUrl)) AccountStore.Shared.Save();
        // CLIENT: báo Hub acc này dính captcha (Hub xem ở panel + operator quyết giữ/xóa). Hub/standalone: là
        // bản chính, khỏi báo. Báo acc.CaptchaUrl (URL đã lưu — nguồn sự thật "Kiểm tra tk lỗi" sẽ mở).
        if (CoordinationRuntime.Active && !HubServerConfigStore.Shared.Current.Enabled)
            _ = CoordinationRuntime.Hub?.ReportErroredAccountAsync(id, reason, acc.CaptchaUrl, "captcha");
    }
}
