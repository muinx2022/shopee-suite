using System.Text.RegularExpressions;
using Shopee.Toolkit.MsLogin;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Kho SELECTOR + REGEX của luồng đăng nhập (Shopee seller / Nền tảng tài khoản phụ / hộp thư Microsoft).
/// Gom một chỗ để khi Shopee/Microsoft đổi giao diện chỉ phải sửa ĐÚNG file này; các file luồng
/// (<see cref="SubaccountLoginFlow"/>, <see cref="EmailVerifyFlow"/>, <see cref="MicrosoftMailLogin"/>,
/// <see cref="ShopeeMailConfirm"/>) chỉ tham chiếu tên, KHÔNG chép lại chuỗi.
/// </summary>
internal static class LoginSelectors
{
    // Selector ô đăng nhập Shopee (thử theo thứ tự; selector Shopee CÓ THỂ ĐỔI → luôn có fallback,
    // không thấy gì thì bỏ qua để người dùng tự nhập tay).
    internal static readonly string[] UserSelectors =
    {
        "input[name='loginKey']",       // ô user chính của Shopee
        "input[type='text']",           // fallback: ô text đầu tiên
        "input[type='email']",
        "input[type='tel']",
    };

    internal static readonly string[] PasswordSelectors =
    {
        "input[name='password']",       // ô mật khẩu chính
        "input[type='password']",       // fallback theo type
    };

    internal static readonly string[] SubmitSelectors =
    {
        "button[type='submit']",        // nút submit chính
        "button:has-text('Đăng nhập')", // fallback: nút chứa chữ "Đăng nhập"
        "button:has-text('ĐĂNG NHẬP')",
    };

    // ===================== Nền tảng tài khoản phụ (subaccount.shopee.com) =====================
    // Form login subaccount là Vue SPA: input KHÔNG có name → dò trong .login-card trước, rồi placeholder,
    // rồi type (fallback rộng nhất cuối). Nút "Đăng nhập" là <button type="button"> (KHÔNG phải submit) chứa
    // <span>Đăng nhập</span> → tuyệt đối không dò button[type='submit']; khớp text bằng SignInRegex có sẵn.
    internal static readonly string[] SubUserSelectors =
        { ".login-card input[type='text']", "input[placeholder*='Tên đăng nhập']", "input[placeholder*='SĐT']", "input[type='text']" };
    internal static readonly string[] SubPassSelectors =
        { ".login-card input[type='password']", "input[type='password']" };
    internal static readonly string[] SubSubmitSelectors =
        { ".login-card button.shopee-button--primary", "button.shopee-button--primary", "button", "[role='button']" };

    // Nav trái "Tài khoản của tôi" (tín hiệu ĐÃ đăng nhập) + entry "Kênh Người bán" (mở Seller Centre). Mỗi regex
    // chứa CẢ dạng có dấu (khớp InnerText thô NFC qua FindVisibleByTextAsync) LẪN dạng không dấu (khớp text đã qua
    // NormalizeForMatch trong matcher/test, và trang render ascii). KHÔNG bám text EN cứng — có nhánh vi + en.
    internal static readonly Regex MyAccountNavRegex =
        new(@"tài khoản của tôi|tai khoan cua toi|my account", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Dùng ở bước bắc cầu SSO cuối TryLoginSubaccountAsync (click "Kênh Người bán" để chuyển sang Seller Centre).
    internal static readonly Regex SellerChannelRegex =
        new(@"kênh người bán|kenh nguoi ban|seller\s*cent(re|er)|seller\s*channel", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ===================== Phát hiện trạng thái trang + verify qua email Hotmail =====================

    // Selector ô đăng nhập Shopee dùng để NHẬN DIỆN "đang ở form login" (CỤ THỂ, không dùng input[type=text]
    // chung — trang bán hàng đã đăng nhập có ô tìm kiếm sẽ nhận nhầm). Kiểm hiển thị bằng getClientRects.
    internal static readonly string[] LoginFormDetectSelectors =
    {
        "input[name='loginKey']",
        "input[name='password']",
        "input[type='password']",
    };

    // --- Selector đăng nhập Microsoft/Outlook: LẤY TỪ BỘ DÙNG CHUNG <see cref="MsLoginSelectors"/>
    //     (shared/Shopee.Toolkit). Microsoft đổi form thì sửa ở ĐÓ — chỗ này chỉ đặt tên cục bộ cho gọn,
    //     tuyệt đối KHÔNG chép lại chuỗi selector vào đây (bản chép tay cũ là nguồn lệch với phía Hub). ---
    internal static readonly string[] MsUserSelectors = MsLoginSelectors.User;
    internal static readonly string[] MsPasswordSelectors = MsLoginSelectors.Password;
    internal static readonly string[] MsSubmitSelectors = MsLoginSelectors.Submit;
    internal static readonly string[] MsUsePasswordSelectors = MsLoginSelectors.UsePassword;
    internal static readonly string[] MsOtherWaysSelectors = MsLoginSelectors.OtherWays;
    internal static readonly string[] MsKmsiYesSelectors = MsLoginSelectors.KmsiYes;
    internal static readonly string[] MsSignInSelectors = MsLoginSelectors.SignIn;

    // --- Regex đa ngôn ngữ (vi/en), KHÔNG bám text EN cứng ---
    internal static readonly Regex VerifyEmailOptionRegex =
        new("email", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static readonly Regex UsePasswordRegex =
        new(@"use.*password|dùng mật khẩu|sử dụng mật khẩu|mật khẩu|mat khau", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Link "Các cách khác để đăng nhập" (footer form "Xác minh email của bạn" mới của Microsoft).
    internal static readonly Regex OtherWaysRegex =
        new(@"cách khác để đăng nhập|cach khac de dang nhap|other ways to sign in", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Nút "Có"/"Yes" ở màn KMSI mới (Fluent) — nút submit generic CHỈ được click khi text khớp đúng đây.
    internal static readonly Regex KmsiYesRegex =
        new(@"^\s*(yes|có|co)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static readonly Regex ShopeeSenderRegex =
        new("shopee", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Tab "Khác"/"Ưu tiên" của hộp thư Outlook — UI đổi theo NGÔN NGỮ tài khoản (vi/en/es/pt/fr...). Thêm
    // đa ngôn ngữ; các từ thêm đều KHÔNG dấu (Otros/Prioritarios...) nên khớp chắc, không dính lỗi NFC/NFD.
    internal static readonly Regex OtherPivotRegex =
        new(@"^\s*(other|otros|outros|autres|khác|khac)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static readonly Regex FocusedPivotRegex =
        new(@"^\s*(focused|prioritarios|prioritaire|prioritaires|ưu tiên|uu tien)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Text CỦA LINK xác nhận trong mail "Cảnh báo bảo mật" của Shopee — link thường CHỈ bọc "TẠI ĐÂY" (không
    // phải cả câu "xác nhận tại đây") nên phải bắt riêng "tại đây". CỐ Ý BỎ "here"/"click here": chữ "here"
    // dính cả link trong mail TRẢ HÀNG của Shopee → click nhầm; mail đã được lọc đúng "Cảnh báo bảo mật" nên
    // chỉ cần khớp các cụm xác nhận tiếng Việt an toàn.
    internal static readonly Regex ConfirmLinkRegex =
        new(@"xác nhận|xac nhan|verify|confirm|đúng là tôi|dung la toi|yes,?\s*it'?s me|tại đây|tại đấy|tai day|nhấn vào đây|bấm vào đây|nhan vao day|bam vao day", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Text nút "Đăng nhập"/"Sign in" — dùng CHUNG với form Microsoft (bộ selector dùng chung), và cũng khớp
    // đúng nút "Đăng nhập" của form subaccount Shopee nên hai chỗ xài chung một regex.
    internal static readonly Regex SignInRegex = MsLoginSelectors.SignInRegex;
    // Thông báo Shopee đã XÁC NHẬN đăng nhập thành công (trên tab mở ra sau khi bấm "TẠI ĐÂY") — chờ dấu
    // hiệu này rồi mới đóng tab, kẻo đóng sớm khi Shopee CHƯA kịp ghi nhận xác nhận.
    internal static readonly Regex ConfirmSuccessRegex =
        new(@"thành công|thanh cong|đã xác nhận|da xac nhan|xác nhận đăng nhập|xac nhan dang nhap|verified|confirmed|success", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Trang mở ra sau khi bấm "TẠI ĐÂY" báo link đã HẾT HẠN/HẾT HIỆU LỰC (Shopee gửi nhiều mail "Cảnh báo
    // bảo mật" khi thử lại nhiều lần → link mail cũ hết hạn). Gặp trang này thì KHÔNG coi là xác nhận thành
    // công — phải quay lại chờ mail MỚI HƠN. Liệt kê cả dạng có dấu lẫn không dấu (khớp IgnoreCase).
    internal static readonly Regex ConfirmExpiredRegex =
        new(@"hết hiệu lực|het hieu luc|hết hạn|het han|đã hết|da het|expired|no longer valid", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Nút "Gửi lại" trên trang xác minh Shopee (sellerPage) — bấm để Shopee GỬI LẠI mail xác thực khi chờ
    // mãi không thấy mail. Khớp text nút (InnerText "Gửi lại").
    internal static readonly Regex ResendVerifyRegex =
        new(@"^\s*(gửi lại|gui lai|resend)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ===================== Danh sách shop (/portal/shop) — mô hình 1 subaccount = nhiều shop =====================

    // Regex nhận entry nút mở shop ("Chi tiết"): chuẩn hóa không dấu rồi khớp. GIỮ nhiều biến thể (vi + en).
    internal static readonly Regex ShopDetailRegex =
        new(@"chi tiet|detail", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
