using Microsoft.Playwright;
using Shopee.Toolkit.MsLogin;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Mở + ĐĂNG NHẬP hộp thư Hotmail/Outlook trong một tab của phiên đang chạy. Dùng chung cho luồng verify
/// (<see cref="EmailVerifyFlow"/> — tự bấm link trong mail) và luồng subaccount
/// (<see cref="SubaccountLoginFlow"/> — chỉ mở cho người dùng tự lấy mã).
/// <para>Selector form Microsoft lấy từ bộ dùng chung <see cref="MsLoginSelectors"/> qua
/// <see cref="LoginSelectors"/> — Microsoft đổi form thì sửa ở ĐÓ, không chép chuỗi vào đây.</para>
/// </summary>
internal static class MicrosoftMailLogin
{
    /// <summary>
    /// Mở TAB MỚI rồi ĐĂNG NHẬP hộp thư Hotmail/Outlook: <c>NewPage</c> → Goto trang đăng nhập Microsoft (nuốt lỗi
    /// điều hướng) → <see cref="LoginHotmailAsync"/>; đăng nhập được thì Goto vào hộp thư Outlook (nuốt lỗi). Trả về
    /// tab mail ĐÃ mở (kể cả khi login thất bại — caller quyết đóng hay giữ) và cờ <c>LoggedIn</c>. Best-effort —
    /// KHÔNG ném (trừ hủy). KHÔNG log giá trị mật khẩu. Dùng chung cho luồng verify (tự bấm link) và luồng
    /// subaccount (chỉ mở cho người dùng tự lấy mã).
    /// </summary>
    internal static async Task<(IPage? MailPage, bool LoggedIn)> OpenMailboxSignedInAsync(
        IBrowserContext context, string email, string password, Action<string>? log, Random rng, CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);

        var mailPage = await context.NewPageAsync().ConfigureAwait(false);
        L("Mở trang đăng nhập Microsoft để lấy mail...");
        try
        {
            await mailPage.GotoAsync("https://login.microsoftonline.com/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000
            }).ConfigureAwait(false);
        }
        catch { /* nuốt lỗi điều hướng — các bước dưới poll selector tự lo */ }

        if (!await LoginHotmailAsync(mailPage, email, password, log, rng, ct).ConfigureAwait(false))
        {
            return (mailPage, false);
        }

        // Đăng nhập ở trang login xong → điều hướng vào HỘP THƯ Outlook để đọc mail (login.microsoftonline.com
        // hạ cánh ở portal, không phải hộp thư). Nếu session đã có sẵn thì vào thẳng.
        L("Vào hộp thư Outlook...");
        try
        {
            await mailPage.GotoAsync("https://outlook.live.com/mail/0/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000
            }).ConfigureAwait(false);
        }
        catch { /* nuốt lỗi điều hướng — bước dưới poll selector tự lo */ }

        return (mailPage, true);
    }

    /// <summary>
    /// Đăng nhập hộp thư Hotmail/Outlook trên <paramref name="mailPage"/>: username → (nếu hiện) "Use your
    /// password"/"Sử dụng mật khẩu" → password → "Stay signed in?" Yes. MỖI bước "chờ có selector thì làm,
    /// timeout ngắn thì bỏ qua sang bước sau" (đã đăng nhập sẵn từ profile → mọi bước tự skip). KHÔNG log
    /// giá trị mật khẩu. Trả <c>false</c> khi phát hiện lỗi đăng nhập (sai user/pass qua error box).
    /// <para>Bước 2 xử lý CẢ form mới "Xác minh email của bạn" (Fluent UI, không còn link "Sử dụng mật khẩu"):
    /// khi không thấy ô mật khẩu lẫn link "Sử dụng mật khẩu", bấm "Các cách khác để đăng nhập" rồi chọn tile
    /// "Mật khẩu" để hiện ô nhập pass. Không thấy thì thất bại mềm (log URL, KHÔNG ném) cho verify tay.</para>
    /// </summary>
    private static async Task<bool> LoginHotmailAsync(
        IPage mailPage, string email, string password, Action<string>? log, Random rng, CancellationToken ct)
    {
        void L(string m) => log?.Invoke(m);
        var vp = mailPage.ViewportSize;
        double mx = vp is not null ? vp.Width / 2.0 : 640;
        double my = vp is not null ? vp.Height / 2.0 : 360;

        // 0) Có thể mở ra trang landing (chưa vào form nhập email) → bấm "Đăng nhập"/"Sign in" trước.
        //    Thử tìm ô email nhanh (6s); không thấy mà có nút Đăng nhập thì bấm rồi tìm lại.
        var userField = await LoginPageProbe.FindFirstVisibleByRectsAsync(mailPage, LoginSelectors.MsUserSelectors, 6000, ct).ConfigureAwait(false);
        if (userField is null)
        {
            var signIn = await LoginPageProbe.FindVisibleByTextAsync(mailPage, LoginSelectors.MsSignInSelectors, LoginSelectors.SignInRegex, ct, 4000).ConfigureAwait(false);
            if (signIn is not null)
            {
                L("Chưa vào form đăng nhập — bấm 'Đăng nhập'...");
                (mx, my, _) = await LoginHumanInput.TryHumanClickVisibleAsync(mailPage, signIn, mx, my, rng, ct).ConfigureAwait(false);
                await Task.Delay(rng.Next(1500, 3500), ct).ConfigureAwait(false);
            }
            userField = await LoginPageProbe.FindFirstVisibleByRectsAsync(mailPage, LoginSelectors.MsUserSelectors, 15000, ct).ConfigureAwait(false);
        }

        // 1) Username (đã tìm ở bước 0; điền nếu thấy).
        if (userField is not null)
        {
            L("Nhập email đăng nhập hộp thư...");
            (mx, my) = await LoginHumanInput.HumanFillAsync(mailPage, userField, email, mx, my, rng, ct).ConfigureAwait(false);
            var next = await LoginPageProbe.FindFirstVisibleByRectsAsync(mailPage, LoginSelectors.MsSubmitSelectors, 3000, ct).ConfigureAwait(false);
            if (next is not null)
            {
                (mx, my) = await LoginHumanInput.HumanMoveAndClickAsync(mailPage, next, mx, my, rng, ct).ConfigureAwait(false);
            }
            await Task.Delay(rng.Next(1500, 3000), ct).ConfigureAwait(false);

            if (await LoginPageProbe.IsSelectorVisibleAsync(mailPage, MsLoginSelectors.UsernameError).ConfigureAwait(false))
            {
                L("Email hộp thư không hợp lệ (Microsoft báo lỗi tài khoản).");
                return false;
            }
        }

        // 2) Đưa về Ô MẬT KHẨU. Microsoft redirect nhiều bước (login.microsoftonline → login.live oauth) +
        //    form Fluent "Xác minh email" render CHẬM/MUỘN hơn cửa sổ tìm → nếu tìm 1 lần rồi thôi hay bị trượt.
        //    POLL tới ~45s, mỗi vòng: (a) thấy ô mật khẩu → xong; (b) thấy "Dùng mật khẩu"/"Nhập mật khẩu"
        //    (tile trên màn 'các cách khác') → click; (c) thấy "Các cách khác để đăng nhập" (form passwordless)
        //    → click (vòng sau sẽ thấy tile "Nhập mật khẩu"). Chịu được redirect/render trễ + đi qua nhiều bước.
        IElementHandle? passField = null;
        var passDeadline = DateTime.UtcNow.AddSeconds(45);
        var clickedOtherWays = false;
        while (DateTime.UtcNow < passDeadline)
        {
            ct.ThrowIfCancellationRequested();

            passField = await LoginPageProbe.FindFirstVisibleByRectsAsync(mailPage, LoginSelectors.MsPasswordSelectors, 1500, ct).ConfigureAwait(false);
            if (passField is not null)
            {
                break;
            }

            // "Sử dụng mật khẩu" (màn chọn cách) HOẶC tile "Nhập mật khẩu" (màn 'các cách khác') — khớp KHÔNG
            // dấu để tránh lỗi NFC/NFD (text MS dạng tổ hợp dấu).
            var usePwd = await LoginPageProbe.FindByNormalizedTextInFramesAsync(mailPage, LoginSelectors.MsUsePasswordSelectors, MsLoginSelectors.UsePasswordNeedles, ct, 1200).ConfigureAwait(false);
            if (usePwd is not null)
            {
                L("Chọn 'Dùng mật khẩu' / 'Nhập mật khẩu'...");
                (mx, my, _) = await LoginHumanInput.TryHumanClickVisibleAsync(mailPage, usePwd, mx, my, rng, ct).ConfigureAwait(false);
                await Task.Delay(rng.Next(1200, 2200), ct).ConfigureAwait(false);
                continue;
            }

            // Form mới "Xác minh email của bạn" (Fluent, passwordless): "Các cách khác để đăng nhập" → (vòng sau
            // thấy tile "Nhập mật khẩu"). Quét mọi frame + khớp KHÔNG dấu (tránh lỗi NFC/NFD). Click 1 lần rồi
            // để vòng sau lo tile mật khẩu.
            var otherWays = await LoginPageProbe.FindByNormalizedTextInFramesAsync(mailPage, LoginSelectors.MsOtherWaysSelectors, MsLoginSelectors.OtherWaysNeedles, ct, 1200).ConfigureAwait(false);
            if (otherWays is not null)
            {
                L("Form 'Xác minh email' — bấm 'Các cách khác để đăng nhập'...");
                (mx, my, _) = await LoginHumanInput.TryHumanClickVisibleAsync(mailPage, otherWays, mx, my, rng, ct).ConfigureAwait(false);
                clickedOtherWays = true;
                await Task.Delay(rng.Next(1200, 2200), ct).ConfigureAwait(false);
                continue;
            }

            // Chưa thấy gì (đang redirect / form chưa render) → chờ rồi thử lại.
            await Task.Delay(rng.Next(1200, 2000), ct).ConfigureAwait(false);
        }

        if (passField is null)
        {
            L($"Không đưa được về ô mật khẩu sau 45s ({(clickedOtherWays ? "đã bấm 'Các cách khác' nhưng không thấy tile Mật khẩu" : "không thấy 'Các cách khác'/ô mật khẩu")}; URL: {mailPage.Url}) — bỏ qua, verify tay.");
        }

        // 3) Password (KHÔNG log giá trị).
        if (passField is not null)
        {
            L("Nhập mật khẩu hộp thư...");
            (mx, my) = await LoginHumanInput.HumanFillAsync(mailPage, passField, password, mx, my, rng, ct).ConfigureAwait(false);
            var signIn = await LoginPageProbe.FindFirstVisibleByRectsAsync(mailPage, LoginSelectors.MsSubmitSelectors, 3000, ct).ConfigureAwait(false);
            if (signIn is not null)
            {
                (mx, my) = await LoginHumanInput.HumanMoveAndClickAsync(mailPage, signIn, mx, my, rng, ct).ConfigureAwait(false);
            }
            await Task.Delay(rng.Next(2000, 4000), ct).ConfigureAwait(false);

            if (await LoginPageProbe.IsSelectorVisibleAsync(mailPage, MsLoginSelectors.PasswordError).ConfigureAwait(false))
            {
                L("Sai mật khẩu hộp thư (Microsoft báo lỗi).");
                return false;
            }
        }

        // 4) "Duy trì đăng nhập?" (KMSI) → bấm "Có" (giữ đăng nhập trong profile). Form Fluent MỚI: nút "Có"
        //    KHÔNG có #acceptButton/#idSIButton9 mà là [data-testid='primaryButton'] — nhưng NHIỀU form khác
        //    cũng có primaryButton (vd "Gửi mã"/"Đăng nhập") nên CHỈ bấm nó khi CHẮC đang ở form KMSI, nhận
        //    diện qua testid ỔN ĐỊNH (kmsiVideo/kmsiImage — không phụ thuộc ngôn ngữ). Bản Outlook cũ:
        //    #acceptButton/#idSIButton9. Poll ~8s vì KMSI render sau submit password (có thể trễ).
        await Task.Delay(rng.Next(1000, 2500), ct).ConfigureAwait(false);
        var kmsiDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < kmsiDeadline)
        {
            ct.ThrowIfCancellationRequested();
            var onKmsi = await LoginPageProbe.IsAnyVisibleByClientRectsAsync(
                mailPage, MsLoginSelectors.KmsiFormMarkers, ct).ConfigureAwait(false);
            var kmsiSelectors = onKmsi ? MsLoginSelectors.KmsiYesFluent : LoginSelectors.MsKmsiYesSelectors;
            var kmsi = await LoginPageProbe.FindFirstVisibleByRectsAsync(mailPage, kmsiSelectors, 1000, ct).ConfigureAwait(false);
            if (kmsi is not null)
            {
                L("Bấm 'Có' để giữ đăng nhập hộp thư...");
                (mx, my) = await LoginHumanInput.HumanMoveAndClickAsync(mailPage, kmsi, mx, my, rng, ct).ConfigureAwait(false);
                await Task.Delay(rng.Next(1500, 3000), ct).ConfigureAwait(false);
                break;
            }
            await Task.Delay(rng.Next(500, 900), ct).ConfigureAwait(false);
        }

        return true;
    }
}
