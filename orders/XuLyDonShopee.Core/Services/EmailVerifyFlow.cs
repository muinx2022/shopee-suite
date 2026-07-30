using Microsoft.Playwright;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Luồng <b>xác minh đăng nhập qua email Hotmail/Outlook</b> khi Shopee bắt verify — thân của
/// <see cref="ILoginSession.TryVerifyByEmailAsync"/>. Xem tài liệu hợp đồng (graceful, không ném trừ hủy, LUÔN
/// đóng tab đã mở ở finally, KHÔNG log mật khẩu) ở chính interface đó.
/// </summary>
internal static class EmailVerifyFlow
{
    /// <inheritdoc cref="ILoginSession.TryVerifyByEmailAsync"/>
    internal static async Task<bool> RunAsync(
        IBrowser browser, IBrowserContext context,
        string verifyEmail, string verifyEmailPassword, bool autoConfirm, Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) => log?.Invoke(m);

        if (string.IsNullOrWhiteSpace(verifyEmail) || string.IsNullOrWhiteSpace(verifyEmailPassword))
        {
            L("Chưa cấu hình Email xác minh cho tài khoản — bỏ qua verify tự động (verify tay).");
            return false;
        }

        var page = context.Pages.Count > 0 ? context.Pages[0] : null;
        if (page is null)
        {
            return false;
        }

        // Cap tổng ~8 phút (linh hoạt): mail xác thực Shopee thường ĐẾN MUỘN sau loạt mail cảnh báo → cần
        // chờ đủ lâu. Timeout NỘI BỘ khác HỦY của người dùng — phân biệt ở khối catch dưới.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(8));
        var vct = timeoutCts.Token;

        var rng = new Random();
        IPage? mailPage = null;
        var keepMailOpenForManual = false; // true = đã đăng nhập email + DỪNG cho user test tay → finally KHÔNG đóng tab Outlook
        try
        {
            // BƯỚC 1: trên trang verify Shopee, click lựa chọn "xác minh qua email".
            var emailOption = await LoginPageProbe.FindVisibleByTextAsync(
                page, new[] { "button", "a", "[role='button']", "label", "li", "div[class*='item']", "div[class*='option']" },
                LoginSelectors.VerifyEmailOptionRegex, vct, 8000).ConfigureAwait(false);
            if (emailOption is null)
            {
                // Log DOM đoạn quyết định để lần sau tinh chỉnh nhanh (title/url).
                string diag;
                try { diag = $"title=[{await page.TitleAsync().ConfigureAwait(false)}], url={page.Url}"; }
                catch { diag = $"url={page.Url}"; }
                L("Không tìm thấy lựa chọn 'xác minh qua email' trên trang verify — bỏ qua. " + diag);
                return false;
            }

            L("Chọn phương thức xác minh qua email...");
            var vp = page.ViewportSize;
            double mx = vp is not null ? vp.Width / 2.0 : 640;
            double my = vp is not null ? vp.Height / 2.0 : 360;
            (mx, my, _) = await LoginHumanInput.TryHumanClickVisibleAsync(page, emailOption, mx, my, rng, vct).ConfigureAwait(false);

            // Chờ trang đổi (thường sang màn "đã gửi link xác minh, kiểm tra email").
            await Task.Delay(rng.Next(2000, 5000), vct).ConfigureAwait(false);

            // BƯỚC 2: mở tab mới đăng nhập hộp thư Hotmail/Outlook rồi vào hộp thư (helper dùng chung với luồng
            //    subaccount). Login lỗi → bỏ qua verify như cũ (finally đóng tab mail vì keepMailOpenForManual=false).
            bool mailLoggedIn;
            (mailPage, mailLoggedIn) = await MicrosoftMailLogin.OpenMailboxSignedInAsync(context, verifyEmail, verifyEmailPassword, log, rng, vct).ConfigureAwait(false);
            if (!mailLoggedIn)
            {
                L("Không đăng nhập được hộp thư Hotmail/Outlook — bỏ qua verify.");
                return false;
            }

            // Cờ "Tự động xác nhận" (checkbox ribbon → autoConfirm): TẮT ⇒ đăng nhập email XONG thì DỪNG, GIỮ
            // hộp thư Outlook mở để user TỰ bấm link "TẠI ĐÂY". BẬT ⇒ chạy tiếp đoạn tự-xác-minh bên dưới
            // (tìm mail → click "TẠI ĐÂY" → chờ seller đăng nhập).
            if (!autoConfirm)
            {
                keepMailOpenForManual = true; // GIỮ tab Outlook cho user (finally không đóng)
                L("Đã đăng nhập email thành công — DỪNG ('Tự động xác nhận' đang TẮT). Giữ hộp thư mở để bạn tự bấm link 'TẠI ĐÂY' duyệt.");
                return false;
            }

            // BƯỚC 3+4: tìm mail Shopee mới nhất + mở + click link xác nhận. (mailPage chắc chắn non-null vì
            //           mailLoggedIn=true ⇒ OpenMailboxSignedInAsync đã tạo tab qua NewPageAsync.)
            if (!await ShopeeMailConfirm.OpenShopeeMailAndConfirmAsync(browser, mailPage!, page, log, rng, vct).ConfigureAwait(false))
            {
                L("Không tìm/không mở được mail xác minh Shopee — bỏ qua.");
                return false;
            }

            // BƯỚC 5: quay lại tab seller, reload, chờ LoggedIn tối đa 90s.
            L("Đã click xác nhận trong mail — quay lại trang bán hàng, chờ đăng nhập...");
            try
            {
                await page.ReloadAsync(new PageReloadOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30000
                }).ConfigureAwait(false);
            }
            catch { /* nuốt lỗi reload — vẫn poll trạng thái */ }

            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                vct.ThrowIfCancellationRequested();
                if (await ShopeeSessionState.DetectPageStateAsync(context, vct).ConfigureAwait(false) == ShopeePageState.LoggedIn)
                {
                    L("Xác minh qua email xong — đã đăng nhập.");
                    return true;
                }
                await Task.Delay(3000, vct).ConfigureAwait(false);
            }

            L("Chờ 90s sau xác nhận mà chưa thấy đăng nhập — bỏ qua (kiểm tra tay).");
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout nội bộ (cap 4') — KHÔNG phải người dùng Dừng → degrade êm, KHÔNG ném (kẻo phá phiên).
            L("Xác minh qua email quá 8 phút — bỏ qua (kiểm tra tay).");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw; // người dùng Dừng / thoát app → để caller xử như HỦY.
        }
        catch (Exception ex)
        {
            L("Lỗi khi xác minh qua email: " + ex.Message);
            return false;
        }
        finally
        {
            // Đóng MỌI tab Microsoft/Outlook đã mở trong lượt xác minh (mailPage + tab redirect OAuth) trừ tab
            // bán hàng Shopee. NGOẠI TRỪ khi đã đăng nhập email + DỪNG cho user test tay
            // (keepMailOpenForManual) → GIỮ tab Outlook mở để user tự thao tác. Best-effort, không để tab treo.
            try
            {
                var toClose = browser.Contexts.SelectMany(c => c.Pages)
                    .Where(p => !keepMailOpenForManual && p != page && (p == mailPage ||
                        (!string.IsNullOrEmpty(p.Url) && (
                            p.Url.Contains("outlook", StringComparison.OrdinalIgnoreCase)
                            || p.Url.Contains("live.com", StringComparison.OrdinalIgnoreCase)
                            || p.Url.Contains("microsoftonline", StringComparison.OrdinalIgnoreCase)
                            || p.Url.Contains("office.com", StringComparison.OrdinalIgnoreCase)
                            || p.Url.Contains("m365", StringComparison.OrdinalIgnoreCase)
                            || p.Url.Contains("microsoft.com", StringComparison.OrdinalIgnoreCase)))))
                    .ToList();
                foreach (var p in toClose)
                {
                    try { await p.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                }
            }
            catch { /* context ngắt — bỏ qua */ }
        }
    }
}
