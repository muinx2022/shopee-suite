using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace XuLyDonShopee.Core.Services;

/// <summary>Kết quả click link xác nhận trong một mail: không có link / đã xác nhận / link hết hạn (cần chờ mail mới).</summary>
internal enum ConfirmOutcome { NoLink, Confirmed, Expired }

/// <summary>
/// Trong hộp thư Outlook đã đăng nhập: tìm mail <b>"Cảnh báo bảo mật"</b> của Shopee, mở và click link xác nhận
/// ("TẠI ĐÂY"), xử lý link hết hạn + bấm "Gửi lại" trên trang xác minh Shopee khi chờ mãi không có mail mới.
/// Chỉ dùng bởi <see cref="EmailVerifyFlow"/> (nhánh "Tự động xác nhận" đang BẬT).
/// </summary>
internal static class ShopeeMailConfirm
{
    /// <summary>
    /// Trong hộp thư Outlook: ưu tiên tab "Ưu tiên"/"Focused" (không có mail Shopee thì thử "Khác"/"Other"),
    /// DUYỆT các mail <b>"Cảnh báo bảo mật"</b> của Shopee theo thứ tự MỚI NHẤT trước — mở lần lượt, mail nào
    /// có link xác nhận ("TẠI ĐÂY") thì click. Shopee gửi nhiều mail cảnh báo bảo mật khi thử lại nhiều lần;
    /// nếu link mở ra báo HẾT HẠN thì bỏ, tải lại hộp thư + chờ để tìm mail mới hơn. Lặp reload + chờ tới hết
    /// deadline (~6'). Trả <c>true</c> khi đã click được link (đã xác nhận).
    /// </summary>
    internal static async Task<bool> OpenShopeeMailAndConfirmAsync(
        IBrowser browser, IPage mailPage, IPage sellerPage, Action<string>? log, Random rng, CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);
        const int MaxMailsPerRound = 8; // mỗi vòng duyệt tối đa 8 mail Shopee đầu (tìm cái có link xác nhận)
        var deadline = DateTime.UtcNow.AddMinutes(6); // chờ mail xác thực tới (đến sau loạt mail cảnh báo)

        // Chờ danh sách mail render lần đầu.
        await Task.Delay(rng.Next(2000, 4000), ct).ConfigureAwait(false);

        var round = 0;
        var noMailStreak = 0; // số vòng LIÊN TIẾP không có mail MỚI để thử — đủ 3 thì bấm "Gửi lại" trên trang Shopee
        var triedKeys = new HashSet<string>(StringComparer.Ordinal); // text dòng mail đã thử KHÔNG thành (hết hạn / không có link "TẠI ĐÂY") → không mở lại; XÓA khi bấm 'Gửi lại'
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            round++;

            // Sau đăng nhập, Microsoft đôi khi điều hướng KHỎI Outlook sang trang home M365
            // (m365.cloud.microsoft) → nếu quét mail ở đó sẽ không bao giờ thấy. Lạc khỏi outlook → quay lại
            // hộp thư trước khi quét.
            var mailUrl = mailPage.Url ?? string.Empty;
            if (!mailUrl.Contains("outlook", StringComparison.OrdinalIgnoreCase))
            {
                L("Không ở Outlook (m365?) — điều hướng lại hộp thư...");
                try
                {
                    await mailPage.GotoAsync("https://outlook.live.com/mail/0/", new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 60000
                    }).ConfigureAwait(false);
                }
                catch { /* nuốt lỗi điều hướng — bước dưới poll selector tự lo */ }
                await Task.Delay(rng.Next(1500, 3000), ct).ConfigureAwait(false);
            }

            // Ưu tiên tab "Ưu tiên"/"Focused"; không có mail Shopee ở đó → thử "Khác"/"Other".
            await TryClickPivotAsync(mailPage, "focused", LoginSelectors.FocusedPivotRegex, "Ưu tiên", log, rng, ct).ConfigureAwait(false);
            await Task.Delay(rng.Next(800, 1500), ct).ConfigureAwait(false);
            var rows = await FindAllShopeeMailRowsAsync(mailPage, MaxMailsPerRound, ct).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                await TryClickPivotAsync(mailPage, "other", LoginSelectors.OtherPivotRegex, "Khác", log, rng, ct).ConfigureAwait(false);
                await Task.Delay(rng.Next(800, 1500), ct).ConfigureAwait(false);
                rows = await FindAllShopeeMailRowsAsync(mailPage, MaxMailsPerRound, ct).ConfigureAwait(false);
            }

            var triedNewMail = false; // vòng này có mở được mail CHƯA-hết-hạn nào để thử không?
            if (rows.Count > 0)
            {
                L($"Thấy {rows.Count} mail Shopee (mới nhất trước) — mở lần lượt tìm link xác nhận 'TẠI ĐÂY'...");
                var vp = mailPage.ViewportSize;
                double mx = vp is not null ? vp.Width / 2.0 : 640;
                double my = vp is not null ? vp.Height / 2.0 : 360;

                for (var i = 0; i < rows.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    // Nhận dạng mail theo TEXT dòng (người gửi + tiêu đề + ngày). Mail đã thử mà link HẾT HẠN
                    // thì GHI NHỚ và KHÔNG mở lại — tránh đọc-đi-đọc-lại cùng 1 mail hết hạn vô tận.
                    string key;
                    try { key = ((await rows[i].InnerTextAsync().ConfigureAwait(false)) ?? string.Empty).Trim(); }
                    catch { continue; }
                    if (key.Length > 0 && triedKeys.Contains(key))
                    {
                        continue;
                    }

                    // Mở mail thứ i bằng click CÓ HIT-TEST: Outlook load quảng cáo async, danh sách hay xê dịch —
                    // nếu đúng lúc click mà quảng cáo chèn vào chỗ dòng mail thì elementFromPoint KHÔNG còn là dòng
                    // mail → KHÔNG click (Clicked=false) → bỏ qua. Dòng cũng có thể detached khi list vẽ lại.
                    bool clickedRow;
                    try { (mx, my, clickedRow) = await LoginHumanInput.HumanMoveAndClickVerifiedAsync(mailPage, rows[i], mx, my, rng, ct).ConfigureAwait(false); }
                    catch { continue; }
                    if (!clickedRow)
                    {
                        L($"Mail Shopee #{i + 1}: danh sách đang xê dịch (quảng cáo?) — chưa click được, thử lại vòng sau.");
                        continue;
                    }
                    await Task.Delay(rng.Next(1200, 2500), ct).ConfigureAwait(false);

                    triedNewMail = true;
                    var outcome = await ClickConfirmLinkInMailAsync(browser, mailPage, sellerPage, log, rng, ct).ConfigureAwait(false);
                    if (outcome == ConfirmOutcome.Confirmed)
                    {
                        return true;
                    }
                    if (outcome == ConfirmOutcome.Expired)
                    {
                        // Link HẾT HẠN → GHI NHỚ mail này (không mở lại), thử mail KẾ trong danh sách. Khi mọi
                        // mail đều đã thử-và-hết-hạn → vòng sau rơi vào nhánh 'không có mail mới' → bấm 'Gửi lại'.
                        if (key.Length > 0) triedKeys.Add(key);
                        L($"Mail Shopee #{i + 1}: link hết hạn → bỏ qua mail này (không mở lại), thử mail khác.");
                        continue;
                    }
                    // Mail KHÔNG có link "TẠI ĐÂY" (vd mail vận đơn/thông báo khác của Shopee) → cũng GHI NHỚ
                    // để KHÔNG mở lại mỗi vòng (kẻo cứ mở #1 → NoLink → coi là 'đã thử mail mới' → reset chuỗi
                    // → không bao giờ đủ 3 vòng để bấm 'Gửi lại').
                    if (key.Length > 0) triedKeys.Add(key);
                    L($"Mail Shopee #{i + 1} không có link xác nhận — bỏ qua, thử mail kế.");
                }
            }

            if (triedNewMail)
            {
                noMailStreak = 0; // vòng này có thử mail MỚI → reset chuỗi
            }
            else
            {
                // KHÔNG có mail xác nhận MỚI để thử (hộp thư rỗng HOẶC mọi mail đều đã thử-và-hết-hạn) → đếm.
                // Sau 3 vòng LIÊN TIẾP → QUAY LẠI trang xác minh Shopee bấm "Gửi lại" (sellerPage vẫn mở), chờ ~1'
                // cho Shopee gửi mail MỚI (link tươi) rồi kiểm lại.
                noMailStreak++;
                L($"Vòng {round}: không có mail xác nhận MỚI (mail cũ đã hết hạn?) — tải lại, chờ mail mới...");
                if (noMailStreak >= 3 && DateTime.UtcNow < deadline)
                {
                    noMailStreak = 0;
                    if (await TryResendVerifyEmailAsync(sellerPage, log, rng, ct).ConfigureAwait(false))
                    {
                        L("Đã bấm 'Gửi lại' trên trang xác minh Shopee — chờ ~1' mail mới về rồi kiểm hộp thư lại...");
                        triedKeys.Clear(); // sắp có mail MỚI (link tươi) → quên danh sách đã-thử để quét lại từ đầu
                        await Task.Delay(60000, ct).ConfigureAwait(false);
                    }
                    try { await mailPage.BringToFrontAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                }
            }

            // Reload hộp thư rồi thử vòng kế (chờ mail tới / mail mới hơn).
            try
            {
                await mailPage.ReloadAsync(new PageReloadOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30000
                }).ConfigureAwait(false);
            }
            catch { /* nuốt lỗi reload */ }
            await Task.Delay(rng.Next(10000, 15000), ct).ConfigureAwait(false);
        }

        L("Hết thời gian chờ mail xác nhận Shopee — bỏ qua (kiểm tra tay).");
        return false;
    }

    /// <summary>Quay lại trang xác minh Shopee (<paramref name="sellerPage"/>) và bấm nút "Gửi lại" để Shopee
    /// GỬI LẠI mail xác thực (khi chờ mãi không thấy mail). Đưa tab lên trước để nút hiển thị (getClientRects),
    /// tìm nút theo <see cref="LoginSelectors.ResendVerifyRegex"/> trong button/a/[role=button] rồi click kiểu người.
    /// Trả <c>true</c> nếu đã bấm được nút.</summary>
    private static async Task<bool> TryResendVerifyEmailAsync(IPage sellerPage, Action<string>? log, Random rng, CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);
        try { await sellerPage.BringToFrontAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
        await Task.Delay(rng.Next(600, 1400), ct).ConfigureAwait(false);

        var btn = await LoginPageProbe.FindVisibleByTextAsync(
            sellerPage, new[] { "button", "a", "[role='button']" }, LoginSelectors.ResendVerifyRegex, ct, 6000).ConfigureAwait(false);
        if (btn is null)
        {
            L("Không thấy nút 'Gửi lại' trên trang xác minh Shopee — bỏ qua lần gửi lại này.");
            return false;
        }

        var vp = sellerPage.ViewportSize;
        double mx = vp is not null ? vp.Width / 2.0 : 640;
        double my = vp is not null ? vp.Height / 2.0 : 360;
        var (_, _, clicked) = await LoginHumanInput.TryHumanClickVisibleAsync(sellerPage, btn, mx, my, rng, ct).ConfigureAwait(false);
        return clicked;
    }

    /// <summary>
    /// Trong reading-pane của mail đang mở (thường nằm trong iframe), dò link/nút xác nhận (text vi/en
    /// khớp <see cref="LoginSelectors.ConfirmLinkRegex"/>) rồi click kiểu người. Link thường mở TAB MỚI
    /// (target _blank) → bắt tab mới bằng snapshot trước/sau (như pattern bắt tab phiếu), chờ tải rồi ĐÓNG tab đó. Trả:
    /// <see cref="ConfirmOutcome.NoLink"/> nếu mail không có link xác nhận; <see cref="ConfirmOutcome.Expired"/>
    /// nếu trang mở ra báo link đã hết hạn/hết hiệu lực (đã đóng tab, caller cần chờ mail MỚI HƠN);
    /// <see cref="ConfirmOutcome.Confirmed"/> nếu Shopee báo thành công HOẶC không rõ kết quả (giữ hành vi lạc
    /// quan cũ để không hồi quy ca xác nhận thật nhưng trang thiếu text thành công).
    /// </summary>
    private static async Task<ConfirmOutcome> ClickConfirmLinkInMailAsync(
        IBrowser browser, IPage mailPage, IPage sellerPage, Action<string>? log, Random rng, CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);

        // Dò trong MỌI frame (thân mail HTML hay nằm trong iframe reading-pane).
        var confirmEl = await LoginPageProbe.FindVisibleByTextInFramesAsync(
            mailPage, new[] { "a", "button", "[role='button']" }, LoginSelectors.ConfirmLinkRegex, ct, 6000).ConfigureAwait(false);
        if (confirmEl is null)
        {
            return ConfirmOutcome.NoLink;
        }

        L("Bấm link xác nhận trong mail...");
        var before = browser.Contexts.SelectMany(c => c.Pages).ToList();

        // Cuộn link vào tầm nhìn TRƯỚC (link "TẠI ĐÂY" có thể nằm cuối mail, ngoài màn hình → click tọa độ
        // sẽ trượt), rồi ƯU TIÊN click ĐÚNG phần tử link bằng Playwright: nó tự hit-test theo đúng frame của
        // element (không lệch hệ tọa độ như elementFromPoint ở main frame) → bấm trúng đúng chữ "TẠI ĐÂY".
        try { await confirmEl.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }

        bool clicked = false;
        try
        {
            await confirmEl.ClickAsync(new ElementHandleClickOptions { Timeout = 5000 }).ConfigureAwait(false);
            clicked = true;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* bị che / actionability timeout → lùi về click theo tọa độ ở dưới */ }

        if (!clicked)
        {
            // Fallback: di chuột kiểu người tới tâm bounding box của ĐÚNG link rồi click tọa độ.
            var vp = mailPage.ViewportSize;
            double mx = vp is not null ? vp.Width / 2.0 : 640;
            double my = vp is not null ? vp.Height / 2.0 : 360;
            await LoginHumanInput.HumanMoveAndClickAsync(mailPage, confirmEl, mx, my, rng, ct).ConfigureAwait(false);
        }

        // Link thường mở TAB MỚI → bắt tab (poll ≤10s).
        IPage? confirmTab = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (confirmTab is null && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                confirmTab = browser.Contexts.SelectMany(c => c.Pages)
                    .FirstOrDefault(p => p != mailPage && p != sellerPage && !before.Contains(p));
            }
            catch { /* context ngắt — thử vòng sau */ }
            if (confirmTab is null)
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
        }

        if (confirmTab is not null)
        {
            L("Đã mở tab xác nhận — CHỜ Shopee báo xác nhận thành công rồi mới đóng...");
            try
            {
                await confirmTab.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions { Timeout = 15000 }).ConfigureAwait(false);
            }
            catch { /* vẫn poll text thành công ở dưới */ }

            // ĐỪNG đóng sớm: poll tới khi trang xác nhận hiện thông báo THÀNH CÔNG (tối đa 45s) — Shopee cần
            // vài giây để ghi nhận xác nhận; đóng trước lúc đó thì xác nhận KHÔNG ăn. Song song: nếu trang báo
            // link HẾT HẠN/HẾT HIỆU LỰC (mail cũ) → thoát sớm, coi là Expired để caller chờ mail mới hơn.
            var okDeadline = DateTime.UtcNow.AddSeconds(45);
            var confirmed = false;
            var expired = false;
            while (DateTime.UtcNow < okDeadline)
            {
                ct.ThrowIfCancellationRequested();
                string body;
                try { body = await confirmTab.EvaluateAsync<string>("() => document.body ? (document.body.innerText || '') : ''").ConfigureAwait(false); }
                catch { body = string.Empty; }
                if (!string.IsNullOrWhiteSpace(body))
                {
                    // Ưu tiên bắt "hết hạn" TRƯỚC: trang lỗi hết hạn không được coi nhầm là thành công.
                    if (LoginSelectors.ConfirmExpiredRegex.IsMatch(body))
                    {
                        expired = true;
                        break;
                    }
                    if (LoginSelectors.ConfirmSuccessRegex.IsMatch(body))
                    {
                        confirmed = true;
                        break;
                    }
                }
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }

            L(expired
                ? "Link xác nhận đã HẾT HẠN — đóng tab, sẽ chờ mail MỚI HƠN."
                : confirmed
                    ? "Shopee đã xác nhận thành công — đóng tab xác nhận."
                    : "Chờ 45s chưa thấy thông báo xác nhận — vẫn đóng tab xác nhận (kiểm tra tay nếu cần).");
            try { await confirmTab.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }

            if (expired)
            {
                return ConfirmOutcome.Expired;
            }
        }
        else
        {
            // Link mở CÙNG tab (hoặc AJAX) → chờ một nhịp rồi thôi.
            await Task.Delay(rng.Next(2000, 4000), ct).ConfigureAwait(false);
        }

        return ConfirmOutcome.Confirmed;
    }

    /// <summary>Click tab/pivot (Outlook "Khác"/"Other" hoặc "Ưu tiên"/"Focused") nếu tìm thấy — best-effort,
    /// không thấy thì bỏ qua (một số hộp thư không chia Focused/Other).</summary>
    private static async Task TryClickPivotAsync(
        IPage page, string pivotValue, Regex regex, string label, Action<string>? log, Random rng, CancellationToken ct)
    {
        // ƯU TIÊN chọn theo thuộc tính `value` (focused/other) của tab Outlook (Fluent fui-Tab) — KHÔNG phụ
        // thuộc NGÔN NGỮ UI (vi/en/es/fr...): <button role="tab" value="focused">Prioritarios</button>. Dự
        // phòng: khớp text đa ngôn ngữ (regex) cho bản Outlook cũ/khác không có thuộc tính value.
        var pivot = await LoginPageProbe.FindFirstVisibleByRectsAsync(
            page, new[] { $"button[role='tab'][value='{pivotValue}']", $"[role='tab'][value='{pivotValue}']" }, 2500, ct).ConfigureAwait(false);
        if (pivot is null)
        {
            pivot = await LoginPageProbe.FindVisibleByTextAsync(
                page, new[] { "button", "[role='tab']", "[role='menuitemradio']", "div[role='heading']", "span" },
                regex, ct, 2500).ConfigureAwait(false);
        }
        if (pivot is null)
        {
            return;
        }

        try
        {
            var vp = page.ViewportSize;
            double mx = vp is not null ? vp.Width / 2.0 : 640;
            double my = vp is not null ? vp.Height / 2.0 : 360;
            await LoginHumanInput.HumanMoveAndClickAsync(page, pivot, mx, my, rng, ct).ConfigureAwait(false);
            log?.Invoke($"Đã mở mục '{label}' trong hộp thư.");
        }
        catch { /* best-effort — bỏ qua */ }
    }

    /// <summary>Danh sách các dòng mail <b>"Cảnh báo bảo mật" của Shopee</b> ĐANG HIỂN THỊ (người gửi khớp
    /// "shopee" VÀ tiêu đề chứa "cảnh báo bảo mật" — xem <see cref="LoginParsers.IsSecurityWarningMailRow"/>) theo
    /// thứ tự DOM (trên cùng = MỚI NHẤT), tối đa <paramref name="maxRows"/>. Trả NHIỀU dòng để caller DUYỆT vì
    /// Shopee gửi nhiều mail cảnh báo bảo mật khi thử lại nhiều lần; mail mới nhất (đầu danh sách) được ưu
    /// tiên. Dùng selector đầu tiên cho ra kết quả (không trộn nhiều selector để tránh trùng dòng); khử trùng
    /// theo text dòng.</summary>
    private static async Task<List<IElementHandle>> FindAllShopeeMailRowsAsync(IPage page, int maxRows, CancellationToken ct)
    {
        foreach (var sel in new[] { "div[role='option']", "div[role='listitem']", "div[role='row']", "[data-convid]" })
        {
            IReadOnlyList<IElementHandle> els;
            try { els = await page.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
            catch { continue; }

            var security = new List<IElementHandle>();  // mail "Cảnh báo bảo mật" — ƯU TIÊN
            var anyShopee = new List<IElementHandle>();  // mọi mail Shopee — DỰ PHÒNG khi Outlook không hiện tiêu đề
            var seenSec = new HashSet<string>();
            var seenAny = new HashSet<string>();
            foreach (var el in els)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!await LoginPageProbe.IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                    {
                        continue;
                    }

                    var txt = await el.InnerTextAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(txt) || !LoginSelectors.ShopeeSenderRegex.IsMatch(txt))
                    {
                        continue; // không phải mail Shopee
                    }

                    var key = txt.Trim();
                    if (seenAny.Add(key))
                    {
                        anyShopee.Add(el);
                    }
                    // ƯU TIÊN mail "Cảnh báo bảo mật" NẾU đọc được tiêu đề trong dòng. Outlook nhiều khi rút
                    // gọn dòng không hiện tiêu đề → security rỗng → DỰ PHÒNG duyệt mọi mail Shopee (vẫn an
                    // toàn vì chỉ click "TẠI ĐÂY" — regex đã bỏ "here" nên không dính mail trả hàng).
                    if (LoginParsers.IsSecurityWarningMailRow(txt) && seenSec.Add(key))
                    {
                        security.Add(el);
                        if (security.Count >= maxRows)
                        {
                            return security;
                        }
                    }
                }
                catch { /* detached / lỗi đọc — bỏ qua dòng này */ }
            }

            var chosen = security.Count > 0 ? security : anyShopee;
            if (chosen.Count > 0)
            {
                // selector này đã cho danh sách mail Shopee — dừng, không trộn selector khác
                return chosen.Count > maxRows ? chosen.GetRange(0, maxRows) : chosen;
            }
        }

        return new List<IElementHandle>();
    }
}
