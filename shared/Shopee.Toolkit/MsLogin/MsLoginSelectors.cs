using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Shopee.Toolkit.MsLogin;

/// <summary>
/// Bộ selector + từ khoá của form đăng nhập Microsoft/Outlook, DÙNG CHUNG cho mọi nơi app tự mở hộp thư
/// (module Đơn hàng: xác minh đăng nhập Shopee qua mail; Hub/BigSeller: đọc mã 6 số). Driver hai bên KHÁC nhau
/// (Playwright "human-like" vs locator API headless) nên KHÔNG gộp — thứ trùng và hay hỏng cùng lúc là chính
/// các CHUỖI selector này: Microsoft đổi form thì phải sửa ĐÚNG MỘT chỗ, không để một bên vá còn bên kia gãy.
/// <para>
/// Selector Microsoft đổi thường xuyên → mỗi bộ luôn nhiều fallback theo thứ tự ƯU TIÊN (cụ thể trước, rộng
/// sau), caller dò lần lượt với timeout ngắn và bỏ qua được khi không thấy.
/// </para>
/// </summary>
public static class MsLoginSelectors
{
    /// <summary>Ô nhập email/tài khoản.</summary>
    public static readonly string[] User =
        { "input[type='email']", "input[name='loginfmt']", "#i0116" };

    /// <summary>Ô nhập mật khẩu.</summary>
    public static readonly string[] Password =
        { "input[name='passwd']", "input[type='password']", "#i0118" };

    /// <summary>Nút "Tiếp theo"/"Đăng nhập" của form (submit chính).</summary>
    public static readonly string[] Submit =
        { "#idSIButton9", "input[type='submit']", "button[type='submit']" };

    /// <summary>Tile "Nhập mật khẩu"/"Sử dụng mật khẩu" — khớp thêm text không dấu <see cref="UsePasswordNeedles"/>
    /// trong đám clickable (selector ở đây chỉ thu hẹp ứng viên).</summary>
    public static readonly string[] UsePassword =
        { "#idA_PWD_SwitchToPassword", "a", "[role='button']", "button", "span" };

    /// <summary>Link "Các cách khác để đăng nhập" trên form mới "Xác minh email của bạn" (Fluent UI):
    /// span[role='button'] class fui-Link trong span[data-testid='viewFooter'].</summary>
    public static readonly string[] OtherWays =
        { "span[role='button']", "[role='button']", "a", "button" };

    /// <summary>KMSI ("Duy trì đăng nhập?"/"Stay signed in?") bản Outlook CŨ — CHỈ dùng ID: nút là
    /// <c>&lt;input value="Yes"&gt;</c> KHÔNG có innerText nên không match theo text. KHÔNG dùng
    /// "button[type='submit']" trần: trên form mới "Xác minh email" nút submit chính là "Gửi mã" → click nhầm.</summary>
    public static readonly string[] KmsiYes =
        { "#acceptButton", "#idSIButton9" };

    /// <summary>KMSI bản Fluent MỚI: nút "Có" là <c>[data-testid='primaryButton']</c> — nhưng NHIỀU form khác cũng
    /// có primaryButton (vd "Gửi mã") nên CHỈ dùng bộ này khi đã chắc đang ở KMSI qua <see cref="KmsiFormMarkers"/>.</summary>
    public static readonly string[] KmsiYesFluent =
        { "[data-testid='primaryButton']", "#acceptButton", "#idSIButton9" };

    /// <summary>Dấu hiệu ỔN ĐỊNH (không phụ thuộc ngôn ngữ) nhận diện đang ở form KMSI.</summary>
    public static readonly string[] KmsiFormMarkers =
        { "[data-testid='kmsiVideo']", "[data-testid='kmsiImage']" };

    /// <summary>Nút "Đăng nhập"/"Sign in" ở trang landing (khi chưa nhảy thẳng vào form nhập email) — lọc tiếp
    /// bằng <see cref="SignInRegex"/>.</summary>
    public static readonly string[] SignIn =
        { "a[data-task='signin']", "a[href*='login.live.com']", "a[href*='login.microsoftonline']", "a[href*='login']", "a", "button", "[role='button']" };

    /// <summary>Text (đã chuẩn hoá KHÔNG DẤU, chữ thường) của tile "Nhập mật khẩu"/"Dùng mật khẩu" — khớp không
    /// dấu để tránh lỗi NFC/NFD của text tiếng Việt trên form Microsoft.</summary>
    public static readonly string[] UsePasswordNeedles =
        { "mat khau", "password", "contrasena" };

    /// <summary>Text (đã chuẩn hoá KHÔNG DẤU, chữ thường) của link "Các cách khác để đăng nhập".</summary>
    public static readonly string[] OtherWaysNeedles =
        { "cach khac de dang nhap", "other ways to sign in", "otras formas de iniciar sesion" };

    /// <summary>Hộp lỗi Microsoft báo SAI TÀI KHOẢN (email không tồn tại).</summary>
    public const string UsernameError = "#usernameError";

    /// <summary>Hộp lỗi Microsoft báo SAI MẬT KHẨU.</summary>
    public const string PasswordError = "#passwordError";

    /// <summary>Text nút "Đăng nhập"/"Sign in" (vi + en, có dấu lẫn không dấu — KHÔNG bám text EN cứng).</summary>
    public static readonly Regex SignInRegex =
        new(@"sign\s*in|đăng nhập|dang nhap", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Chuẩn hoá text để so khớp BỀN: bỏ dấu tiếng Việt (kể cả đ→d), gộp mọi cụm khoảng trắng về một dấu cách,
    /// trim, hạ chữ thường — tức đưa text đọc từ trang về ĐÚNG dạng của <see cref="UsePasswordNeedles"/> /
    /// <see cref="OtherWaysNeedles"/> để so <c>Contains</c>. TRỊ lỗi NFC/NFD: text tiếng Việt trên form Microsoft
    /// ở dạng tổ hợp dấu (NFD) KHÔNG khớp literal dựng sẵn (NFC) dù mắt thấy giống.
    /// <para>DÙNG CHUNG hai phía (module Đơn hàng qua <c>LoginParsers</c>, Hub/BigSeller qua
    /// <c>HotmailOtpReader</c>) — trước đây là hai bản chép tay trùng logic. Đặt trong CHÍNH file này thay vì
    /// file riêng vì hub chỉ <c>Compile</c>-link đúng file này từ Toolkit (xem
    /// <c>server/Shopee.Hub.Web/Shopee.Hub.Web.csproj</c>), tách ra file mới thì hub không thấy.</para>
    /// </summary>
    public static string NormalizeForMatch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var collapsed = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var decomposed = collapsed.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            // Bỏ dấu thanh/dấu phụ (combining marks); đ/Đ không tách được bằng FormD → thay thủ công bên dưới.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            switch (ch)
            {
                case 'đ': sb.Append('d'); break;
                case 'Đ': sb.Append('D'); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}
