using Microsoft.Playwright;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Luồng <b>đăng nhập Nền tảng tài khoản phụ</b> (<see cref="ShopeeLoginService.SubaccountUrl"/>) rồi bắc cầu SSO
/// sang Seller Centre — thân của <see cref="ILoginSession.TryLoginSubaccountAsync"/>. Xem tài liệu hợp đồng
/// (graceful, không ném trừ hủy) ở chính interface đó.
/// </summary>
internal static class SubaccountLoginFlow
{
    /// <inheritdoc cref="ILoginSession.TryLoginSubaccountAsync"/>
    internal static async Task<bool> RunAsync(
        IBrowserContext context, string user, string password, string? verifyEmail, string? verifyEmailPassword,
        Action<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) => log?.Invoke(m);

        // URL của một trang có phải Seller Centre (banhang.shopee.vn) không.
        static bool UrlIsBanhang(string? u) =>
            !string.IsNullOrEmpty(u) && u.Contains("banhang.shopee.vn", StringComparison.OrdinalIgnoreCase);

        async Task<string> DiagAsync(IPage p)
        {
            try { return $"title=[{await p.TitleAsync().ConfigureAwait(false)}], url={p.Url}"; }
            catch { return $"url={p.Url}"; }
        }

        var page = context.Pages.Count > 0 ? context.Pages[0] : null;
        if (page is null)
        {
            return false;
        }

        // Cap NỘI BỘ 20': chờ NGƯỜI dùng gõ code là phần lâu nhất. Timeout nội bộ ≠ HỦY của người dùng —
        // phân biệt ở khối catch dưới.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(20));
        var sct = timeoutCts.Token;

        // Selector nhóm cho nav "Tài khoản của tôi" (tín hiệu đã đăng nhập) — dùng lại ở nhiều bước.
        var accountNavSelectors = new[] { "li", "a", "div", "span", "[role='menuitem']" };

        var rng = new Random();
        IPage? mailPage = null;
        try
        {
            var vp = page.ViewportSize;
            double mx = vp is not null ? vp.Width / 2.0 : 640;
            double my = vp is not null ? vp.Height / 2.0 : 360;

            // ── Bước 2: dò trạng thái đầu (SPA còn render) — poll tối đa ~15s. KHÔNG dùng
            //    ShopeeLoginCookies.IsLoggedIn (cookie SPC_* của shopee.vn KHÔNG nói gì về phiên subaccount).
            bool onLoginForm = false;
            bool loggedIn = false;
            var detectDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < detectDeadline)
            {
                sct.ThrowIfCancellationRequested();

                // "Đang ở form login" = ô mật khẩu subaccount HIỂN THỊ (getClientRects).
                if (await LoginPageProbe.IsAnyVisibleByClientRectsAsync(page, LoginSelectors.SubPassSelectors, sct).ConfigureAwait(false))
                {
                    onLoginForm = true;
                    break;
                }

                // "Đã đăng nhập" = phần tử khớp nav "Tài khoản của tôi" HIỂN THỊ.
                if (await LoginPageProbe.FindVisibleByTextAsync(page, accountNavSelectors, LoginSelectors.MyAccountNavRegex, sct, 1000).ConfigureAwait(false) is not null)
                {
                    loggedIn = true;
                    break;
                }

                await Task.Delay(500, sct).ConfigureAwait(false);
            }

            if (!onLoginForm && !loggedIn)
            {
                L("Chưa rõ trạng thái trang subaccount sau 15s — thử tiếp nhánh 'đã đăng nhập'. " + await DiagAsync(page).ConfigureAwait(false));
            }

            // ── Bước 3: ở form login → tự điền tài khoản + mật khẩu rồi bấm "Đăng nhập".
            if (onLoginForm)
            {
                if (string.IsNullOrEmpty(password))
                {
                    L("Tài khoản chưa có mật khẩu — đăng nhập tay.");
                    return false;
                }

                L("Đang điền form đăng nhập Nền tảng tài khoản phụ...");
                // Re-query handle TƯƠI ngay trước khi điền (Vue re-render — không giữ handle qua các bước chờ).
                var userInput = await LoginPageProbe.FindFirstVisibleByRectsAsync(page, LoginSelectors.SubUserSelectors, 8000, sct).ConfigureAwait(false);
                var passInput = await LoginPageProbe.FindFirstVisibleByRectsAsync(page, LoginSelectors.SubPassSelectors, 4000, sct).ConfigureAwait(false);
                if (userInput is null || passInput is null)
                {
                    L("Không thấy ô đăng nhập subaccount — đăng nhập tay. " + await DiagAsync(page).ConfigureAwait(false));
                    return false;
                }

                (mx, my) = await LoginHumanInput.HumanFillAsync(page, userInput, user, mx, my, rng, sct).ConfigureAwait(false);
                (mx, my) = await LoginHumanInput.HumanFillAsync(page, passInput, password, mx, my, rng, sct).ConfigureAwait(false);

                // Nút "Đăng nhập" là <button type="button"> chứa <span>Đăng nhập</span> — khớp text bằng SignInRegex.
                var submit = await LoginPageProbe.FindVisibleByTextAsync(page, LoginSelectors.SubSubmitSelectors, LoginSelectors.SignInRegex, sct, 5000).ConfigureAwait(false);
                if (submit is null)
                {
                    L("Không thấy nút 'Đăng nhập' subaccount — đăng nhập tay. " + await DiagAsync(page).ConfigureAwait(false));
                    return false;
                }
                (mx, my, _) = await LoginHumanInput.TryHumanClickVisibleAsync(page, submit, mx, my, rng, sct).ConfigureAwait(false);
                L("Đã bấm Đăng nhập — chờ Shopee đòi mã xác thực...");

                // ── Bước 4: mở hộp thư cho NGƯỜI DÙNG tự lấy mã (KHÔNG tự verify, KHÔNG tự bấm gì trong mail).
                if (!string.IsNullOrWhiteSpace(verifyEmail) && !string.IsNullOrWhiteSpace(verifyEmailPassword))
                {
                    try
                    {
                        bool mailLoggedIn;
                        (mailPage, mailLoggedIn) = await MicrosoftMailLogin.OpenMailboxSignedInAsync(context, verifyEmail!, verifyEmailPassword!, log, rng, sct).ConfigureAwait(false);
                        L(mailLoggedIn
                            ? "Đã mở hộp thư ở tab bên — lấy mã rồi nhập vào trang Shopee."
                            : "Chưa đăng nhập được hộp thư tự động — GIỮ tab mail mở để bạn tự đăng nhập, lấy mã rồi nhập vào trang Shopee.");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        L("Lỗi khi mở hộp thư: " + ex.ToString() + " — bạn tự lấy mã và nhập vào trang Shopee.");
                    }

                    // Đưa cửa sổ về trang Shopee cho người dùng gõ code (best-effort).
                    try { await page.BringToFrontAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                }
                else
                {
                    L("Chưa cấu hình Email xác minh — bạn tự lấy mã và nhập vào trang Shopee.");
                }
            }

            // ── Bước 5: chờ NGƯỜI DÙNG nhập code — poll mỗi 3s, tối đa 15'. Thoát khi nav "Tài khoản của tôi"
            //    HIỂN THỊ (đã về trang tài khoản). KHÔNG tự bấm gì trong mail, KHÔNG reload (kẻo xóa ô code).
            bool reached = loggedIn; // đã đăng nhập sẵn từ đầu (hồ sơ bền) → khỏi chờ
            if (!reached)
            {
                L("Chờ đăng nhập xong (bạn nhập mã nếu Shopee yêu cầu)...");
                var waitDeadline = DateTime.UtcNow.AddMinutes(15);
                while (DateTime.UtcNow < waitDeadline)
                {
                    sct.ThrowIfCancellationRequested();
                    if (await LoginPageProbe.FindVisibleByTextAsync(page, accountNavSelectors, LoginSelectors.MyAccountNavRegex, sct, 1000).ConfigureAwait(false) is not null)
                    {
                        reached = true;
                        break;
                    }
                    await Task.Delay(3000, sct).ConfigureAwait(false);
                }
            }

            if (!reached)
            {
                L("Chờ 15' chưa thấy đăng nhập vào Nền tảng tài khoản phụ — GIỮ cửa sổ để bạn thao tác tay. " + await DiagAsync(page).ConfigureAwait(false));
                return false;
            }

            // ── Bước 6: đóng tab mail (best-effort, chỉ tab mình mở) rồi click "Tài khoản của tôi".
            if (mailPage is not null)
            {
                try { await mailPage.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                mailPage = null;
            }

            L("Đã đăng nhập Nền tảng tài khoản phụ.");

            var myAccountNav = await LoginPageProbe.FindVisibleByTextAsync(page, accountNavSelectors, LoginSelectors.MyAccountNavRegex, sct, 10000).ConfigureAwait(false);
            if (myAccountNav is null)
            {
                L("Không thấy 'Tài khoản của tôi' — GIỮ cửa sổ để bạn thao tác tay. " + await DiagAsync(page).ConfigureAwait(false));
                return false;
            }
            (mx, my, _) = await LoginHumanInput.TryHumanClickVisibleAsync(page, myAccountNav, mx, my, rng, sct).ConfigureAwait(false);
            await Task.Delay(rng.Next(1500, 3001), sct).ConfigureAwait(false);

            // ── Bước 7: click "Kênh Người bán" → chờ Seller Centre (tab MỚI HOẶC cùng tab). Hứng tab mới bằng
            //    event context.Page TRƯỚC khi click (không bỏ lỡ popup nhanh); song song vẫn quét context.Pages.
            var sellerEntry = await LoginPageProbe.FindVisibleByTextAsync(
                page, new[] { "span.entry-text", ".entry", "span", "div", "[role='button']", "a" },
                LoginSelectors.SellerChannelRegex, sct, 10000).ConfigureAwait(false);
            if (sellerEntry is null)
            {
                L("Không thấy entry 'Kênh Người bán' — GIỮ cửa sổ để bạn thao tác tay. " + await DiagAsync(page).ConfigureAwait(false));
                return false;
            }

            IPage? popped = null;
            void OnNewPage(object? _, IPage p) => popped ??= p;
            context.Page += OnNewPage;

            IPage sellerPage = page;
            bool sellerInNewTab = false;
            try
            {
                (mx, my, _) = await LoginHumanInput.TryHumanClickVisibleAsync(page, sellerEntry, mx, my, rng, sct).ConfigureAwait(false);
                L("Đã bấm 'Kênh Người bán' — chờ Seller Centre mở...");

                var sellerDeadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < sellerDeadline)
                {
                    sct.ThrowIfCancellationRequested();

                    // (a) chính page điều hướng sang banhang (cùng tab).
                    if (UrlIsBanhang(page.Url))
                    {
                        sellerPage = page;
                        sellerInNewTab = false;
                        break;
                    }

                    // (b) tab mới (bắt qua event hoặc quét Pages) đã có URL banhang.
                    var candidate = (popped is not null && UrlIsBanhang(popped.Url))
                        ? popped
                        : context.Pages.FirstOrDefault(p => p != page && UrlIsBanhang(p.Url));
                    if (candidate is not null)
                    {
                        sellerPage = candidate;
                        sellerInNewTab = true;
                        break;
                    }

                    await Task.Delay(500, sct).ConfigureAwait(false);
                }
            }
            finally
            {
                context.Page -= OnNewPage;
            }

            if (!UrlIsBanhang(sellerPage.Url))
            {
                var tabs = new List<string>();
                foreach (var p in context.Pages)
                {
                    tabs.Add(await DiagAsync(p).ConfigureAwait(false));
                }
                L("Bấm 'Kênh Người bán' xong chờ 90s chưa thấy Seller Centre — GIỮ cửa sổ để bạn thao tác tay. Các tab: " + string.Join(" ; ", tabs));
                return false;
            }

            // Tab seller mới mở → chờ DOMContentLoaded best-effort (đừng để bước sau đọc trang trắng).
            if (sellerInNewTab)
            {
                try
                {
                    await sellerPage.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                        new PageWaitForLoadStateOptions { Timeout = 15000 }).ConfigureAwait(false);
                }
                catch { /* bỏ qua — trang vẫn dùng được, bước sau tự poll */ }
            }

            // ── Bước 8: chuẩn hóa tab — nếu seller là TAB MỚI → đóng tab subaccount để seller thành Pages[0].
            if (sellerInNewTab)
            {
                for (int attempt = 0; attempt < 3 && !page.IsClosed; attempt++)
                {
                    try { await page.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua — thử lại */ }
                    if (page.IsClosed) break;
                    await Task.Delay(400, sct).ConfigureAwait(false);
                }

                if (!page.IsClosed)
                {
                    L("Cảnh báo: tab subaccount chưa đóng được — theo dõi đơn có thể đọc nhầm tab (Pages[0] không phải Seller Centre).");
                }
                else if (context.Pages.Count == 0 || context.Pages[0] != sellerPage)
                {
                    L("Cảnh báo: sau khi đóng subaccount, Seller Centre chưa ở Pages[0] — theo dõi đơn có thể đọc nhầm tab.");
                }
            }

            L("Đã vào Kênh Người bán.");
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout NỘI BỘ (cap 20') — KHÔNG phải người dùng Dừng → degrade êm, KHÔNG ném.
            L("Đăng nhập Nền tảng tài khoản phụ quá thời gian — GIỮ cửa sổ để bạn thao tác tay.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw; // người dùng Dừng / thoát app → để caller xử như HỦY.
        }
        catch (Exception ex)
        {
            L("Lỗi khi đăng nhập Nền tảng tài khoản phụ: " + ex.ToString() + " — GIỮ cửa sổ để bạn thao tác tay.");
            return false;
        }
        // KHÔNG đóng tab seller/subaccount ở finally — việc đóng tab subaccount làm CÓ CHỦ ĐÍCH ở Bước 8; tab mail
        // đóng ở Bước 6 (đường thành công) hoặc GIỮ mở ở đường lỗi cho người dùng tự lấy mã.
    }
}
