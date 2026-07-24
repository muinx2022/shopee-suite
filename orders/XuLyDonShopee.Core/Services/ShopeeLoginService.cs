using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Một phiên đăng nhập đang mở (cửa sổ trình duyệt). Giữ tham chiếu tới browser/context
/// và cho phép bắt cookie khi người dùng đã đăng nhập xong. Đóng phiên qua <c>DisposeAsync</c>.
/// </summary>
public interface ILoginSession : IAsyncDisposable
{
    /// <summary>Lấy toàn bộ cookie hiện có của phiên dưới dạng JSON (định dạng <see cref="CookieJson"/>).</summary>
    Task<string> CaptureCookiesJsonAsync();

    /// <summary>Task hoàn tất khi người dùng đóng cửa sổ trình duyệt (tiến trình Brave thoát / CDP ngắt).</summary>
    Task Closed { get; }

    /// <summary>True nếu cửa sổ trình duyệt đã đóng.</summary>
    bool IsClosed { get; }

    /// <summary>
    /// Tiến trình Brave/Chromium mà phiên đang sở hữu. Dùng ở tầng App để (Plan B) đưa cửa sổ ra trước
    /// (focus) và để kill dự phòng khi dừng phiên. Null nếu phiên không giữ tiến trình.
    /// </summary>
    Process? BraveProcess { get; }

    /// <summary>
    /// Số cửa sổ/tab (Pages) đang mở của phiên. Dùng làm tín hiệu "người dùng đã đóng hết cửa sổ"
    /// đáng tin hơn "tiến trình Brave chết" (Brave có thể còn chạy nền). Trả 0 nếu context đã ngắt.
    /// </summary>
    int OpenPageCount { get; }

    /// <summary>
    /// <b>Tự đăng nhập kiểu người</b>: nếu trang đang hiển thị form đăng nhập Shopee thì dò ô user &amp;
    /// password, di chuột theo <b>đường cong</b> (<see cref="HumanMouse"/>) tới từng ô rồi click, gõ
    /// <b>từng ký tự có delay</b> (<see cref="HumanTyping"/>), cuối cùng bấm nút đăng nhập. KHÔNG xử lý
    /// captcha/OTP (để người dùng tự làm).
    /// <para>
    /// <b>Graceful — không bao giờ ném:</b> nếu đã đăng nhập sẵn, hoặc không tìm thấy ô đăng nhập, hoặc
    /// bất kỳ lỗi nào xảy ra → trả <c>false</c> (bỏ qua, để người dùng tự thao tác tay). Trả <c>true</c>
    /// khi đã điền được user &amp; password.
    /// </para>
    /// </summary>
    Task<bool> TryHumanLoginAsync(string user, string password, CancellationToken ct = default);

    /// <summary>
    /// <b>Phát hiện trạng thái trang bán hàng</b> sau khi mở seller URL: đã đăng nhập / form đăng nhập /
    /// trang verify / trang captcha / không rõ. Ưu tiên theo URL (captcha, <c>/verify</c>), rồi ô đăng nhập
    /// hiển thị (kiểm <c>getClientRects</c>), rồi cookie phiên. Dùng để điều phối auto-login → verify →
    /// captcha-retry ở tầng App.
    /// <para><b>Graceful — không bao giờ ném:</b> không có trang / lỗi bất kỳ → <see cref="ShopeePageState.Unknown"/>.</para>
    /// </summary>
    Task<ShopeePageState> DetectPageStateAsync(CancellationToken ct = default);

    /// <summary>
    /// <b>Xác minh đăng nhập qua email Hotmail/Outlook</b> khi Shopee bắt verify: (1) trên trang verify
    /// Shopee click lựa chọn "verify qua email"; (2) mở TAB MỚI đăng nhập hộp thư Hotmail/Outlook
    /// (username → "Use your password" → password → "Stay signed in?" Yes) — mọi bước dò nhiều selector,
    /// timeout ngắn bỏ qua được, KHÔNG fail cứng; (3) vào hộp thư, ưu tiên tab "Khác"/"Other", tìm mail
    /// Shopee MỚI NHẤT, mở mail rồi click link/nút xác nhận (bắt tab mới nếu link mở tab), đóng tab;
    /// (4) quay lại tab seller, chờ trạng thái về <see cref="ShopeePageState.LoggedIn"/>.
    /// <para>
    /// <b>Graceful — không bao giờ ném (trừ hủy):</b> thiếu cấu hình / không tìm được lựa chọn / login mail
    /// lỗi / không thấy mail / hết thời gian → <c>false</c> (caller giữ phiên cho người dùng verify tay).
    /// Trả <c>true</c> khi seller đã về LoggedIn sau khi click xác nhận. LUÔN đóng các tab đã mở (finally),
    /// KHÔNG log giá trị mật khẩu. Mọi bước ghi qua <paramref name="log"/> để theo dõi trên panel nhật ký.
    /// </para>
    /// </summary>
    Task<bool> TryVerifyByEmailAsync(
        string verifyEmail, string verifyEmailPassword, bool autoConfirm, Action<string>? log = null, CancellationToken ct = default);

    /// <summary>
    /// <b>Đăng nhập Nền tảng tài khoản phụ</b> (<see cref="ShopeeLoginService.SubaccountUrl"/>): nếu đang ở
    /// form đăng nhập subaccount thì tự điền tài khoản (<paramref name="user"/>) + mật khẩu (<paramref name="password"/>)
    /// kiểu người rồi bấm "Đăng nhập"; khi Shopee đòi mã thì <b>mở hộp thư</b> (<paramref name="verifyEmail"/> /
    /// <paramref name="verifyEmailPassword"/>) cho người dùng TỰ lấy mã (KHÔNG tự verify, KHÔNG tự bấm gì trong mail),
    /// đưa cửa sổ về trang Shopee; chờ người dùng nhập code (tối đa 15') tới khi nav "Tài khoản của tôi" hiện thì
    /// DỪNG ở đó (ĐÃ đăng nhập subaccount). Rồi <b>bắc cầu SSO</b>: click "Tài khoản của tôi" → "Kênh Người bán" để
    /// chuyển phiên sang Seller Centre (<c>banhang.shopee.vn</c> — lập cookie seller) và chuẩn hóa <c>Pages[0]</c> =
    /// tab banhang; caller (RunAsync) rồi mới mở danh sách shop <c>/portal/shop</c> và lặp qua từng shop (xem
    /// <see cref="ReadShopListAsync"/> / <see cref="OpenShopDetailAsync"/>).
    /// <para>
    /// <b>Graceful — không bao giờ ném (trừ hủy người dùng):</b> mọi thất bại (không thấy ô/nút, hết giờ, lỗi) →
    /// <c>false</c> (caller GIỮ cửa sổ cho người dùng thao tác tay). Trả <c>true</c> khi đã bắc cầu SSO sang Seller
    /// Centre (<c>Pages[0]</c> là tab <c>banhang.shopee.vn</c>). KHÔNG log giá trị mật khẩu; mọi nhánh selector trượt
    /// đều log <c>title=…, url=…</c>.
    /// </para>
    /// </summary>
    Task<bool> TryLoginSubaccountAsync(
        string user, string password, string? verifyEmail, string? verifyEmailPassword,
        Action<string>? log = null, CancellationToken ct = default);

    /// <summary>
    /// <b>Đọc danh sách shop</b> của tài khoản subaccount: điều hướng tab gốc (<c>Pages[0]</c>) tới
    /// <see cref="ShopeeLoginService.ShopListUrl"/>, chờ bảng shop render rồi quét mỗi dòng thành
    /// <see cref="ShopListItem"/> (mã shop = <c>data-row-key</c>, tên shop, tên đăng nhập). Đặt lại "trang làm việc"
    /// về <c>Pages[0]</c> trước khi đọc.
    /// <para>
    /// <b>Graceful — không bao giờ ném (trừ hủy):</b> không đọc được bảng / trang bounce về login / lỗi bất kỳ →
    /// danh sách RỖNG (+ log <c>title=…, url=…</c>).
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ShopListItem>> ReadShopListAsync(Action<string>? log = null, CancellationToken ct = default);

    /// <summary>
    /// <b>Mở trang chi tiết một shop</b>: định vị dòng bảng theo <see cref="ShopListItem.ShopId"/>, bấm nút "Chi tiết"
    /// KIỂU NGƯỜI; hứng TAB MỚI (hoặc điều hướng cùng tab), đặt tab đó làm "trang làm việc" hiện tại để các hàm flow
    /// đơn chạy trên đúng shop này.
    /// <para>
    /// <b>Graceful — không bao giờ ném (trừ hủy):</b> không thấy nút / không mở được → <c>false</c> (+ log
    /// <c>title=…, url=…</c>). Trả <c>true</c> khi đã mở được tab shop.
    /// </para>
    /// </summary>
    Task<bool> OpenShopDetailAsync(ShopListItem shop, Action<string>? log = null, CancellationToken ct = default);

    /// <summary>
    /// <b>Đóng tab shop</b> đang mở (nếu là tab riêng khác <c>Pages[0]</c>) rồi quay lại tab danh sách shop; xóa
    /// "trang làm việc" hiện tại. Best-effort — không ném (trừ hủy).
    /// </summary>
    Task CloseShopTabAsync(CancellationToken ct = default);

    /// <summary>
    /// Đọc số đơn <b>"Chờ Lấy Hàng"</b> trong to-do box của trang bán hàng (Seller Centre).
    /// <para>
    /// <b>Gate:</b> chỉ đọc khi đã đăng nhập (to-do box chỉ có sau đăng nhập) — chưa đăng nhập → trả
    /// <c>null</c> và KHÔNG reload (tránh phá thao tác đăng nhập/captcha của người dùng). Nếu
    /// <paramref name="reload"/> = <c>true</c> thì reload lại trang trước khi đọc (lấy số mới nhất).
    /// </para>
    /// <para>
    /// <b>Graceful — không bao giờ ném:</b> chưa đăng nhập, không tìm thấy ô (Shopee đổi selector), hoặc
    /// bất kỳ lỗi nào → trả <c>null</c>. Trả về số đơn (≥ 0) khi đọc được.
    /// </para>
    /// </summary>
    Task<int?> ReadToShipCountAsync(bool reload, CancellationToken ct = default);

    /// <summary>
    /// <b>Về trang chủ Seller rồi đọc lại số "Chờ Lấy Hàng" ngay:</b> điều hướng tab hiện tại về trang chủ
    /// Seller Centre (<see cref="ShopeeLoginService.SellerUrl"/>) bằng <see cref="IPage.GotoAsync"/> —
    /// tương đương người gõ URL / bấm bookmark (hành vi bình thường, <b>KHÔNG</b> click máy vào element) —
    /// kèm khoảng dừng "đọc trang" ngẫu nhiên trước/sau, rồi đọc số "Chờ Lấy Hàng" từ to-do box qua
    /// <see cref="ReadToShipCountAsync"/> với <c>reload=false</c> (trang vừa load nên không reload lại).
    /// Dùng cho việc kiểm tra đơn THỦ CÔNG (không đợi chu kỳ 30').
    /// <para>
    /// <b>Graceful — không bao giờ ném:</b> chưa đăng nhập, không có trang, không đọc được, hoặc bất kỳ
    /// lỗi nào → trả <c>null</c> (KHÔNG phá phiên). Trả về số đơn (≥ 0) khi đọc được.
    /// </para>
    /// </summary>
    Task<int?> GoHomeAndReadToShipCountAsync(CancellationToken ct = default);

    /// <summary>
    /// <b>Bước đầu xử lý đơn:</b> ở menu trái (nhóm "Quản Lý Đơn Hàng") tìm &amp; bấm link
    /// <b>"Cài Đặt Vận Chuyển"</b>, chờ trang cài đặt vận chuyển mở rồi bấm tab <b>"Địa Chỉ"</b> —
    /// <b>toàn bộ bằng thao tác kiểu người CÓ HIT-TEST</b> (di chuột theo đường cong <see cref="HumanMouse"/>,
    /// click down→trễ→up, có khoảng dừng/chờ ngẫu nhiên kiểu "người đọc trang"; TRƯỚC KHI nhả click kiểm
    /// <c>document.elementFromPoint</c> để KHÔNG click nhầm link khác khi submenu bị cụp / flyout đè). Nếu
    /// submenu đang đóng thì click mục cha "Quản Lý Đơn Hàng" kiểu người để mở ra rồi tìm lại.
    /// <para>
    /// <b>Graceful — không bao giờ ném:</b> mọi lỗi/hủy → trả một giá trị <see cref="ShippingNavResult"/>
    /// (KHÔNG phá phiên). Kết quả phân biệt bước hỏng: <see cref="ShippingNavResult.Ok"/> (tab "Địa Chỉ" đã
    /// active — đã bấm hoặc vốn đang active); <see cref="ShippingNavResult.PageNotOpened"/> (không đưa được
    /// tới trang cài đặt vận chuyển, kể cả sau fallback Goto); <see cref="ShippingNavResult.AddressTabNotFound"/>
    /// (đã mở trang nhưng không thấy / không bấm được tab "Địa Chỉ"); <see cref="ShippingNavResult.Failed"/>
    /// (không có trang/phiên hoặc lỗi bất ngờ).
    /// </para>
    /// </summary>
    Task<ShippingNavResult> OpenShippingAddressSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// <b>Bước 2 xử lý đơn — đặt "địa chỉ lấy hàng":</b> chạy khi ĐANG ở tab "Địa Chỉ" của Cài đặt vận
    /// chuyển. Duyệt danh sách địa chỉ, tìm địa chỉ có dòng tỉnh/thành khớp
    /// <paramref name="province"/> (<c>Account.PickupAddress</c>). Nếu địa chỉ đó đã là địa chỉ lấy hàng
    /// (có tag "Địa chỉ lấy hàng") → coi như xong. Ngược lại bấm <b>Sửa</b> → tick checkbox "Đặt làm địa
    /// chỉ lấy hàng" → bấm <b>Lưu</b> → chờ modal đóng. <b>Toàn bộ bằng thao tác kiểu người</b> (di chuột
    /// theo đường cong, click down→trễ→up, dừng "đọc trang" ngẫu nhiên giữa các bước); CHỈ click khi phần
    /// tử có bounding box. Modal chứa Google Map load bất đồng bộ → Vue vẽ lại form nên checkbox được
    /// <b>re-query tươi</b> trước mỗi lần dùng (không giữ handle qua re-render), trạng thái tick đọc bằng
    /// JS eval trên phần tử vừa query.
    /// <para>
    /// <b>Graceful — không bao giờ ném:</b> mọi lỗi/không-làm-được → trả một giá trị
    /// <see cref="SetPickupResult"/> (KHÔNG phá phiên, nghiêng về KHÔNG bấm thêm), và <b>mọi nhánh thất bại
    /// đều Hủy modal</b> (không để modal "Sửa Địa chỉ" mở treo). Kết quả phân biệt bước hỏng:
    /// <see cref="SetPickupResult.Ok"/> (địa chỉ lấy hàng đã đúng — sẵn có hoặc đã Lưu thành công);
    /// <see cref="SetPickupResult.AddressNotFound"/> (không thấy địa chỉ khớp tỉnh);
    /// <see cref="SetPickupResult.EditModalNotOpened"/> (bấm Sửa nhưng modal không mở — shop khóa sửa?);
    /// <see cref="SetPickupResult.CheckboxNotFound"/> (modal mở nhưng không thấy ô cần tick);
    /// <see cref="SetPickupResult.CheckboxClickFailed"/> (click không tick được sau vài lần);
    /// <see cref="SetPickupResult.SaveFailed"/> (đã tick nhưng bấm Lưu không được / modal không đóng);
    /// <see cref="SetPickupResult.Failed"/> (không có trang/phiên hoặc lỗi bất ngờ).
    /// </para>
    /// </summary>
    Task<SetPickupResult> SetPickupAddressAsync(string province, CancellationToken ct = default);

    /// <summary>
    /// <b>Bước 4 xử lý đơn — đặt "địa chỉ lấy hàng" về MỘT ĐỊA CHỈ KHÁC:</b> chạy khi ĐANG ở tab "Địa Chỉ"
    /// (caller mở sẵn, giống hợp đồng <see cref="SetPickupAddressAsync"/>). Chọn <b>địa chỉ ĐẦU TIÊN KHÔNG
    /// mang tag "Địa chỉ lấy hàng"</b> rồi bấm <b>Sửa</b> → tick "Đặt làm địa chỉ lấy hàng" → <b>Lưu</b>
    /// (dùng chung khối với <see cref="SetPickupAddressAsync"/>). Dùng SAU khi đã xử lý hết đơn để KHÔNG giữ
    /// nguyên địa chỉ mà app đã đặt lúc xử lý. "Địa chỉ khác" = item đầu tiên đọc được DOM và chắc chắn KHÔNG
    /// có tag pickup; item đọc tag LỖI (DOM chưa render) bị BỎ QUA để tránh chọn bừa; nếu tag không đọc được
    /// đúng lúc chọn (DOM đổi) vẫn có thể trúng chính địa chỉ đang là pickup — <b>vô hại</b> (tick lại chính
    /// nó, Shopee giữ nguyên).
    /// <para>
    /// <b>Graceful:</b> mọi lỗi → một giá trị <see cref="SetPickupResult"/> (bộ kết quả phân biệt bước hỏng
    /// như <see cref="SetPickupAddressAsync"/>); shop chỉ có 1 địa chỉ / mọi địa chỉ đang là pickup →
    /// <see cref="SetPickupResult.AddressNotFound"/>. Riêng <see cref="OperationCanceledException"/> ném XUYÊN
    /// để caller dừng sạch.
    /// </para>
    /// </summary>
    Task<SetPickupResult> SetPickupAddressToOtherAsync(CancellationToken ct = default);

    /// <summary>
    /// <b>Bước 3 xử lý đơn — xử lý ĐƠN ĐẦU TIÊN trong danh sách "Tất cả" (tab "Chờ xử lý"):</b> điều hướng
    /// về trang danh sách đơn (<c>/portal/sale/order</c>), lấy đơn đầu tiên → bấm <b>"Chuẩn bị hàng"</b> →
    /// trong modal <b>"Giao Đơn Hàng"</b> chọn <b>"Tôi sẽ tự mang hàng tới Bưu cục"</b> (mặc định đã chọn)
    /// → bấm <b>"Xác nhận"</b> → chờ modal <b>"Thông Tin Chi Tiết"</b> → <b>CHỜ nút "In phiếu giao" bấm được
    /// tới 5 phút</b> (Shopee tạo mã vận đơn có thể lâu, có log tiến trình 30s) rồi bấm → BẮT
    /// tab phiếu mới, lưu phiếu PDF (ưu tiên bản GỐC từ blob) về <paramref name="downloadDir"/> (KHÔNG gửi
    /// lệnh in — lưu để in sau) rồi ĐÓNG tab. <b>Toàn bộ click bằng thao tác kiểu người CÓ HIT-TEST</b> (di chuột cong, click
    /// down→trễ→up, có dừng "đọc trang" ngẫu nhiên; KHÔNG dùng ClickAsync/Fill/native). Mỗi bước gọi
    /// <paramref name="log"/> (nếu có) để theo dõi live.
    /// <para>
    /// <b>Graceful — không bao giờ ném:</b> mọi lỗi/hủy → trả một giá trị <see cref="ArrangeShipmentResult"/>
    /// (KHÔNG phá phiên). <see cref="ArrangeShipmentResult.NoOrder"/> khi danh sách rỗng;
    /// <see cref="ArrangeShipmentResult.Ok"/> khi đã qua bước "In phiếu giao" (bắt được tab phiếu). Việc
    /// lưu phiếu là <b>best-effort có log</b> — thất bại chỉ cảnh báo, KHÔNG hạ kết quả xuống fail (đơn đã
    /// được arrange). Chỉ làm MỘT đơn (chạy 1 lần).
    /// </para>
    /// </summary>
    /// <param name="downloadDir">Thư mục lưu file phiếu (được tạo nếu chưa có).</param>
    /// <param name="log">Callback ghi log từng bước (null → bỏ qua).</param>
    Task<ArrangeShipmentResult> ProcessFirstOrderAsync(string downloadDir, Action<string>? log = null, CancellationToken ct = default);

    /// <summary>
    /// <b>Sync đơn hàng — thu thập MỌI đơn ở tab "Tất cả":</b> điều hướng về trang danh sách đơn
    /// (<c>/portal/sale/order</c>), chuyển sang tab <b>"Tất cả"</b>, rồi <b>duyệt TOÀN BỘ các trang</b> danh
    /// sách (có chốt chặn an toàn 10 trang), quét thông tin mỗi đơn (mã đơn, người mua, sản phẩm, tổng tiền,
    /// trạng thái, lý do hủy, kênh/ĐVVC, mã vận đơn) bằng JS <b>CHỈ ĐỌC</b>, khử trùng lặp theo mã đơn, trả
    /// về danh sách. <b>KHÔNG đụng DB</b> (Core chỉ thu thập &amp; trả DTO; tầng App lưu). Điều hướng/chuyển
    /// tab bằng thao tác kiểu người CÓ HIT-TEST; phân trang code PHÒNG THỦ (nhiều selector nút "trang sau" +
    /// điều kiện danh sách phải ĐỔI sau khi bấm) + log chẩn đoán pager nếu không thấy nút.
    /// <para>
    /// <b>Mô hình vòng đời đơn (tầng App quyết):</b> Core CHỈ thu thập tab "Tất cả" và trả DTO; việc LƯU do
    /// <c>AccountSession</c> lọc — đơn MỚI chỉ được LƯU khi đang ở trạng thái "Chuẩn bị hàng" (Chờ lấy hàng),
    /// đơn ĐÃ theo dõi luôn được cập nhật đến trạng thái cuối (Đã giao / Đã hủy) rồi bị DỌN khỏi DB sau khi
    /// mọi nghĩa vụ (GSheet / "Đã bán" / hub) hoàn tất. Nhờ quét tab "Tất cả", nhánh "Đã bán" theo SKU và cờ
    /// tô đỏ đơn hủy trên GSheet hoạt động bình thường trở lại.
    /// </para>
    /// <para>
    /// <b>Lấy thêm "Số tiền cuối cùng":</b> sau khi quét xong MỖI TRANG, với từng đơn KHÁC "Đã hủy" và CHƯA có
    /// trong <paramref name="ordersWithFinalAmount"/>, MỞ trang chi tiết (ưu tiên click nút "Xem chi tiết" kiểu
    /// người CÓ HIT-TEST → bắt TAB MỚI; fallback mở tab bằng <c>NewPageAsync</c>+<c>GotoAsync</c> URL chi tiết),
    /// <c>EvaluateAsync</c> CHỈ-ĐỌC lấy <c>.amount</c> của card <c>[type='FinalAmount']</c>, parse qua
    /// <see cref="ShopeeShippingNav.ParseVndAmount"/>, rồi ĐÓNG đúng tab vừa mở (KHÔNG đóng tab danh sách gốc).
    /// Best-effort per-đơn: 1 đơn lỗi KHÔNG phá cả lượt sync. Bước này CHẬM (mỗi đơn = mở+đọc+đóng 1 tab).
    /// </para>
    /// <para>
    /// <b>Graceful — không bao giờ ném (trừ hủy):</b> mọi lỗi bất ngờ → trả về những đơn ĐÃ gom được (không
    /// mất dữ liệu đã quét) + log lỗi. Riêng <see cref="OperationCanceledException"/> ném XUYÊN để caller dừng
    /// sạch. Kết quả gồm danh sách đơn, số trang đã quét, và cờ có chạm chốt chặn số trang không.
    /// </para>
    /// </summary>
    /// <param name="log">Callback ghi log tiến trình từng trang (null → bỏ qua).</param>
    /// <param name="ordersWithFinalAmount">
    /// Tập <c>order_sn</c> ĐÃ có "Số tiền cuối cùng" trong DB (App cấp từ
    /// <c>OrdersRepository.GetOrderSnsWithFinalAmount</c>). Đơn nằm trong tập này sẽ KHÔNG mở lại trang chi tiết
    /// (tối ưu tốc độ — lần đầu lâu, các lần sau nhanh). Null → coi như CHƯA có đơn nào (mở chi tiết mọi đơn
    /// khác "Đã hủy").
    /// </param>
    Task<SyncOrdersResult> SyncAllOrdersAsync(
        Action<string>? log = null,
        IReadOnlySet<string>? ordersWithFinalAmount = null,
        CancellationToken ct = default);

    /// <summary>
    /// <b>Tải LẠI file phiếu giao cho các đơn ĐÃ arrange nhưng THIẾU file PDF:</b> về trang danh sách đơn,
    /// chuyển tab <b>"Tất cả"</b>, duyệt các trang (chốt chặn số trang như sync); với từng mã trong
    /// <paramref name="orderSns"/> còn thiếu, định vị card theo mã (như bước lấy "Số tiền cuối cùng"), bấm
    /// <b>"In phiếu giao"</b> (ưu tiên nút ngay trong card của đơn đã arrange; fallback mở CHI TIẾT đơn rồi
    /// bấm) → bắt TAB PHIẾU (awbprint) → lưu PDF (<see cref="SaveSlipAsync"/>) về <paramref name="downloadDir"/>
    /// với tên <c>&lt;mã đơn&gt;.pdf</c> → đóng tab phiếu. KHÔNG arrange lại (đơn đã arrange — chỉ IN LẠI).
    /// <para>
    /// <b>Best-effort per-đơn:</b> 1 đơn lỗi (không thấy card / nút in không bấm được / lưu fail) → log rõ +
    /// sang đơn khác, KHÔNG ném. Sau khi lưu, KIỂM lại file (tồn tại + magic <c>%PDF-</c>) rồi mới đếm thành
    /// công. <see cref="OperationCanceledException"/> ném XUYÊN để caller dừng sạch. Trả về SỐ phiếu lưu lại
    /// thành công.
    /// </para>
    /// </summary>
    /// <param name="orderSns">Danh sách mã đơn cần tải lại phiếu (thường ≤ 5/lượt do App chốt chặn).</param>
    /// <param name="downloadDir">Thư mục lưu file phiếu (được tạo nếu chưa có).</param>
    /// <param name="log">Callback ghi log từng bước (null → bỏ qua).</param>
    Task<int> RedownloadSlipsAsync(
        IReadOnlyList<string> orderSns,
        string downloadDir,
        Action<string>? log = null,
        CancellationToken ct = default);
}

/// <summary>
/// Mở trang Shopee Seller Centre bằng <b>Brave thật</b> (tự khởi chạy tiến trình Brave rồi nối vào
/// qua CDP — <see cref="IBrowserType.ConnectOverCDPAsync"/>), định tuyến qua proxy nếu có, để người
/// dùng tự đăng nhập; sau đó bắt cookie phiên.
/// <para>
/// Vì tự launch Brave như trình duyệt bình thường (KHÔNG để Playwright launch với cờ
/// <c>--enable-automation</c>) nên KHÔNG hiện thanh "controlled by automated test software" và
/// <c>navigator.webdriver</c> giữ <c>false</c> — <b>do chính Brave thật</b>, không do vá JS.
/// CHỦ ĐÍCH <b>không tiêm init script vá fingerprint</b> (plugins/WebGL/webdriver/window.chrome...) vì
/// các vá đó lại <b>tự tạo dấu hiệu lộ bot</b> (own-property <c>navigator.webdriver</c>, hàm mất
/// <c>"[native code]"</c>, plugin giả không phải <c>Plugin</c>). Dựa vào Brave thật vốn đã sạch
/// (webdriver=false, plugins/window.chrome/WebGL thật) + hành vi kiểu người (Plan 2). Locale VN đặt qua
/// cờ <c>--lang=vi-VN</c>. <b>Không đảm bảo 100%</b> né được anti-bot của Shopee (CDP/fingerprint/hành
/// vi/IP vẫn có thể bị dò) — đây là best-effort.
/// </para>
/// <para>
/// Ưu tiên mở <b>Brave</b> nếu đã cài trên máy; nếu không có Brave dùng <b>Chromium đóng gói</b> của
/// Playwright (cùng cơ chế CDP).
/// </para>
/// </summary>
public class ShopeeLoginService
{
    /// <summary>URL trang bán hàng (Shopee Seller Centre).</summary>
    public const string SellerUrl = "https://banhang.shopee.vn/";

    /// <summary>URL Nền tảng tài khoản phụ — điểm vào đăng nhập mới (một tài khoản có nhiều shop).</summary>
    public const string SubaccountUrl = "https://subaccount.shopee.com/";

    /// <summary>Trang "Tài khoản" của Nền tảng tài khoản phụ — điểm vào của BẢN SẠCH (cầu nối): có cookie hồ sơ →
    /// hiện trang tài khoản (có "Kênh Người bán"); hết cookie → ra form đăng nhập. Dùng để SSO lại về trang chọn
    /// shop (né sticky-shop server-side khi mở thẳng /portal/shop).</summary>
    public const string SubaccountAccountUrl = "https://subaccount.shopee.com/account";

    /// <summary>URL bảng danh sách shop của Nền tảng tài khoản phụ — sau khi đăng nhập, mở thẳng đây để lặp qua từng shop.</summary>
    public const string ShopListUrl = "https://banhang.shopee.vn/portal/shop";

    // ===== Forwarder cho test (luồng verify-email) =====
    // Logic khớp text thực nằm trong nested class LoginSession (nơi giữ các Regex + luồng verify-email). Phơi
    // lại ở cấp class ngoài (internal — InternalsVisibleTo cho XuLyDonShopee.Tests) để unit-test được các hàm
    // thuần này mà không cần dựng cả phiên trình duyệt.

    /// <summary>Chuẩn hóa text để so khớp bền (bỏ dấu tiếng Việt kể cả đ→d, gộp space, hạ chữ thường).</summary>
    internal static string NormalizeForMatch(string? s) => LoginSession.NormalizeForMatch(s);

    /// <summary>True nếu dòng mail là "Cảnh báo bảo mật Tài khoản Shopee" (người gửi shopee + tiêu đề chứa
    /// "cảnh báo bảo mật"); loại mail trả hàng/khác của Shopee.</summary>
    internal static bool IsSecurityWarningMailRow(string? rowText) => LoginSession.IsSecurityWarningMailRow(rowText);

    /// <summary>True nếu text khớp link xác nhận cần bấm (vd "TẠI ĐÂY") — KHÔNG còn khớp "here"/"click here".</summary>
    internal static bool MatchesConfirmLink(string? text) => LoginSession.MatchesConfirmLink(text);

    /// <summary>True nếu text là trang báo link đã hết hạn/hết hiệu lực.</summary>
    internal static bool MatchesConfirmExpired(string? text) => LoginSession.MatchesConfirmExpired(text);

    /// <summary>True nếu text là nav "Tài khoản của tôi" trên Nền tảng tài khoản phụ (tín hiệu ĐÃ đăng nhập).</summary>
    internal static bool MatchesMyAccountNav(string? text) => LoginSession.MatchesMyAccountNav(text);

    /// <summary>True nếu text là entry "Kênh Người bán" (mở sang Seller Centre) trên Nền tảng tài khoản phụ —
    /// dùng ở bước bắc cầu SSO cuối TryLoginSubaccountAsync.</summary>
    internal static bool MatchesSellerChannelEntry(string? text) => LoginSession.MatchesSellerChannelEntry(text);

    /// <summary>Chuyển JSON mảng <c>{rowKey,name,login}</c> (đọc từ bảng <c>/portal/shop</c>) thành danh sách
    /// <see cref="ShopListItem"/> — forwarder để unit-test hàm thuần mà không cần dựng phiên trình duyệt.</summary>
    internal static IReadOnlyList<ShopListItem> ParseShopListJson(string? json) => LoginSession.ParseShopListJson(json);

    /// <summary>Chuyển JSON mảng đơn (do <c>pageScanOrders</c>/<c>ScanOrdersJs</c> đọc từ DOM) thành danh sách
    /// <see cref="SyncedOrder"/> — forwarder tái dùng hàm thuần <c>LoginSession.ParseOrdersJson</c> cho cầu nối
    /// extension (<c>OrdersBridgeSession</c>), không viết lại logic parse.</summary>
    internal static List<SyncedOrder> ParseOrdersJson(string? json) => LoginSession.ParseOrdersJson(json);

    /// <summary>Forwarder tái dùng luồng đăng nhập hộp thư Hotmail/Outlook (mở tab mới trong <paramref name="context"/>,
    /// đăng nhập Microsoft, vào hộp thư) — dùng cho <c>OrdersMailboxSession</c> (trình duyệt Playwright RIÊNG cho mail,
    /// tách khỏi trình duyệt Shopee sạch). Trả về tab mail đã mở + cờ đã đăng nhập.</summary>
    internal static Task<(IPage? MailPage, bool LoggedIn)> OpenMailboxSignedInAsync(
        IBrowserContext context, string email, string password, Action<string>? log, Random rng, CancellationToken ct)
        => LoginSession.OpenMailboxSignedInAsync(context, email, password, log, rng, ct);

    /// <summary>
    /// Đảm bảo có sẵn trình duyệt để mở cho <paramref name="browserChoice"/>. Nếu phân giải được một
    /// trình duyệt thật đã cài trên máy (Chrome/Edge/Brave tùy lựa chọn) thì trả về ngay (0) mà
    /// <b>không tải</b> Chromium đóng gói. Ngược lại (không có trình duyệt thật phù hợp, hoặc chọn
    /// Chromium đóng gói) thì tải Chromium của Playwright (~150MB lần đầu; idempotent — đã cài thì
    /// trả về nhanh). Trả về exit code (0 = thành công); bọc try/catch, trả code khác 0 khi lỗi để
    /// tầng gọi thông báo.
    /// </summary>
    public int EnsureBrowserInstalled(BrowserChoice browserChoice = BrowserChoice.Auto)
    {
        // Phân giải được trình duyệt thật → không cần tải Chromium đóng gói (đỡ ~150MB).
        if (BrowserLocator.ResolveExecutable(browserChoice) != null)
        {
            return 0;
        }

        try
        {
            return Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Mô tả trình duyệt <b>THỰC SỰ</b> sẽ được dùng cho <paramref name="browserChoice"/> (để hiển
    /// thị ở Cài đặt / log): phân giải file thực thi rồi phân loại bằng cách <b>so path bằng nhau</b>
    /// với <see cref="BrowserLocator.FindChromeExecutable"/> / <see cref="BrowserLocator.FindEdgeExecutable"/>
    /// / <see cref="BrowserLocator.FindBraveExecutable"/> (KHÔNG đoán theo tên file để tránh sai với
    /// đường dẫn lạ): khớp Chrome → <c>"Chrome (&lt;path&gt;)"</c>; khớp Edge → <c>"Edge (&lt;path&gt;)"</c>;
    /// khớp Brave → <c>"Brave (&lt;path&gt;)"</c>; <c>null</c> (không có trình duyệt thật / chọn Chromium
    /// đóng gói) → <c>"Chromium đóng gói của Playwright"</c>.
    /// <para>
    /// Hành vi mặc định (<see cref="BrowserChoice.Auto"/>): ưu tiên Chrome → Edge → Brave; đây là đổi so
    /// với bản cũ (trước ưu tiên Brave) — CÓ CHỦ ĐÍCH vì Chrome/Edge ít bị Shopee bắt captcha hơn Brave.
    /// </para>
    /// </summary>
    public static string DescribeBrowser(BrowserChoice browserChoice)
    {
        var exe = BrowserLocator.ResolveExecutable(browserChoice);
        if (exe == null)
        {
            return "Chromium đóng gói của Playwright";
        }

        if (PathEquals(exe, BrowserLocator.FindChromeExecutable()))
        {
            return $"Chrome ({exe})";
        }
        if (PathEquals(exe, BrowserLocator.FindEdgeExecutable()))
        {
            return $"Edge ({exe})";
        }
        if (PathEquals(exe, BrowserLocator.FindBraveExecutable()))
        {
            return $"Brave ({exe})";
        }

        // Không khớp trình duyệt thật nào (không kỳ vọng xảy ra) → mô tả trung tính theo path.
        return $"Trình duyệt ({exe})";
    }

    /// <summary>So sánh hai đường dẫn file (không phân biệt hoa/thường trên Windows). <c>b</c> null → false.</summary>
    private static bool PathEquals(string a, string? b)
    {
        if (string.IsNullOrEmpty(b))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(a, b, comparison);
    }

    /// <summary>
    /// Mở một cửa sổ trình duyệt (Brave nếu có, không thì Chromium đóng gói) tới trang bán hàng bằng
    /// <b>hồ sơ persistent</b> đặt tại <paramref name="userDataDir"/> (mỗi tài khoản một thư mục riêng)
    /// — nhờ đó lần sau mở lại vẫn còn đăng nhập — qua proxy đã chọn, rồi trả về phiên đang mở.
    /// Cơ chế: tự khởi chạy tiến trình Brave với cờ stealth + <c>--user-data-dir</c> +
    /// <c>--remote-debugging-port</c> + <c>--proxy-server</c>, chờ CDP sẵn sàng, nối vào qua
    /// <see cref="IBrowserType.ConnectOverCDPAsync"/>. Ném <see cref="InvalidOperationException"/>
    /// (message tiếng Việt) nếu không mở được.
    /// </summary>
    public async Task<ILoginSession> OpenAsync(
        string userDataDir, ProxyEntry? proxy, BrowserChoice browserChoice = BrowserChoice.Auto, CancellationToken ct = default)
    {
        IPlaywright? playwright = null;
        Process? process = null;
        IBrowser? browser = null;

        try
        {
            playwright = await Playwright.CreateAsync().ConfigureAwait(false);

            // Phân giải trình duyệt thật theo lựa chọn của người dùng; không có → Chromium đóng gói (cùng cơ chế CDP).
            var exePath = BrowserLocator.ResolveExecutable(browserChoice);
            if (exePath == null)
            {
                EnsureChromiumInstalledForFallback();
                exePath = playwright.Chromium.ExecutablePath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    throw new InvalidOperationException(
                        "Không tìm thấy trình duyệt đã chọn và cũng chưa tải được Chromium đóng gói của Playwright.");
                }
            }

            // Đọc cổng CDP thật từ DevToolsActivePort → xóa file cũ để tránh đọc nhầm cổng phiên trước.
            var portFile = Path.Combine(userDataDir, "DevToolsActivePort");
            try { if (File.Exists(portFile)) File.Delete(portFile); } catch { /* bỏ qua */ }

            // Launch Brave/Chromium với cổng 0 (Chromium tự chọn cổng trống, ghi vào DevToolsActivePort).
            // POC né anti-bot: nạp extension "shopee-orders-test" (nếu tìm thấy) để thao tác Seller Centre bằng
            // extension thay Playwright lái trang. Không thấy → null → không nạp (giữ hành vi cũ).
            var extPath = BraveLaunchArgs.ResolveOrdersExtension();

            var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
            foreach (var arg in BraveLaunchArgs.BuildBraveArgs(userDataDir, 0, proxy, extPath))
            {
                psi.ArgumentList.Add(arg);
            }

            process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Không khởi chạy được tiến trình trình duyệt.");
            process.EnableRaisingEvents = true;

            // Chờ Brave mở cổng CDP (đọc cổng thật) rồi chờ endpoint /json/version sẵn sàng.
            var port = await WaitForDevToolsPortAsync(portFile, process, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            await WaitForCdpEndpointAsync(port, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

            // Nối vào Brave đang chạy qua CDP.
            browser = await playwright.Chromium
                .ConnectOverCDPAsync($"http://127.0.0.1:{port}").ConfigureAwait(false);

            // Brave chạy --user-data-dir → có sẵn context mặc định = hồ sơ persistent.
            var context = browser.Contexts.Count > 0
                ? browser.Contexts[0]
                : await browser.NewContextAsync().ConfigureAwait(false);

            // CHỦ ĐÍCH KHÔNG tiêm init script vá fingerprint: Brave thật đã sạch (webdriver=false,
            // plugins/window.chrome/WebGL thật), vá lại chỉ tự tạo dấu hiệu lộ bot. Locale VN đặt qua
            // cờ --lang=vi-VN trong BraveLaunchArgs.

            var page = context.Pages.Count > 0
                ? context.Pages[0]
                : await context.NewPageAsync().ConfigureAwait(false);

            // Proxy có user:pass → xử lý xác thực qua CDP (không hiện hộp thoại đăng nhập proxy).
            if (!string.IsNullOrEmpty(proxy?.Username))
            {
                await SetupProxyAuthAsync(context, page, proxy!).ConfigureAwait(false);
            }

            try
            {
                await page.GotoAsync(SubaccountUrl, new PageGotoOptions
                {
                    Timeout = 60000,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                }).ConfigureAwait(false);
            }
            catch
            {
                // Nuốt lỗi timeout/điều hướng — vẫn giữ cửa sổ mở để người dùng tự thao tác.
            }

            return new LoginSession(playwright, browser, context, process);
        }
        catch (Exception ex)
        {
            // Dọn dẹp: ngắt CDP, KILL cả cây tiến trình Brave (tránh Brave mồ côi giữ khóa hồ sơ),
            // giải phóng Playwright.
            if (browser is not null)
            {
                try { await browser.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
            }
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { /* bỏ qua */ }
            }
            try { process?.Dispose(); } catch { /* bỏ qua */ }
            try { playwright?.Dispose(); } catch { /* bỏ qua */ }

            throw new InvalidOperationException(
                "Không mở được trình duyệt Shopee. Kiểm tra đã cài Brave hoặc Chromium và kết nối mạng/proxy. " +
                "Chi tiết: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Chờ Brave khởi động xong và ghi cổng CDP vào file <c>DevToolsActivePort</c> (dòng đầu = cổng).
    /// Poll có timeout; nếu tiến trình thoát sớm (thường do hồ sơ đang bị một cửa sổ Brave khác khóa)
    /// thì ném lỗi tiếng Việt.
    /// </summary>
    private static async Task<int> WaitForDevToolsPortAsync(
        string portFile, Process process, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "Trình duyệt thoát ngay khi khởi động (thường do hồ sơ đang bị một cửa sổ Brave khác khóa). " +
                    "Hãy đóng hết cửa sổ Brave rồi thử lại.");
            }

            try
            {
                if (File.Exists(portFile))
                {
                    var lines = await File.ReadAllLinesAsync(portFile, ct).ConfigureAwait(false);
                    if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out var port) && port > 0)
                    {
                        return port;
                    }
                }
            }
            catch (IOException)
            {
                // File đang được Brave ghi dở — thử lại vòng sau.
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "Quá thời gian chờ trình duyệt mở cổng gỡ lỗi (DevToolsActivePort).");
    }

    /// <summary>
    /// Chờ endpoint CDP HTTP <c>/json/version</c> trả 200 (báo trình duyệt đã sẵn sàng nhận kết nối CDP).
    /// Poll có timeout; hết giờ thì ném lỗi tiếng Việt.
    /// </summary>
    private static async Task WaitForCdpEndpointAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var url = $"http://127.0.0.1:{port}/json/version";
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var res = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // Chưa sẵn sàng — thử lại vòng sau.
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Quá thời gian chờ endpoint CDP sẵn sàng.");
    }

    /// <summary>
    /// Xử lý <b>xác thực proxy</b> (proxy có user:pass) qua CDP để không hiện hộp thoại đăng nhập proxy.
    /// Bật <c>Fetch</c> với <c>handleAuthRequests</c>: nghe <c>Fetch.authRequired</c> → trả credential
    /// khi nguồn là "Proxy"; nghe <c>Fetch.requestPaused</c> → tiếp tục request (để không chặn request
    /// thường). Fire-and-forget các lệnh CDP trong handler (event là đồng bộ).
    /// </summary>
    private static async Task SetupProxyAuthAsync(IBrowserContext context, IPage page, ProxyEntry proxy)
    {
        var cdp = await context.NewCDPSessionAsync(page).ConfigureAwait(false);
        await cdp.SendAsync("Fetch.enable", new Dictionary<string, object>
        {
            ["handleAuthRequests"] = true
        }).ConfigureAwait(false);

        var username = proxy.Username ?? string.Empty;
        var password = proxy.Password ?? string.Empty;

        cdp.Event("Fetch.authRequired").OnEvent += (_, e) =>
        {
            if (e is not { } json)
            {
                return;
            }

            if (!TryGetString(json, "requestId", out var requestId))
            {
                return;
            }

            var isProxy = json.TryGetProperty("authChallenge", out var challenge)
                          && challenge.TryGetProperty("source", out var source)
                          && string.Equals(source.GetString(), "Proxy", StringComparison.OrdinalIgnoreCase);

            var response = isProxy
                ? new Dictionary<string, object>
                {
                    ["response"] = "ProvideCredentials",
                    ["username"] = username,
                    ["password"] = password
                }
                : new Dictionary<string, object> { ["response"] = "Default" };

            _ = SafeSendAsync(cdp, "Fetch.continueWithAuth", new Dictionary<string, object>
            {
                ["requestId"] = requestId,
                ["authChallengeResponse"] = response
            });
        };

        cdp.Event("Fetch.requestPaused").OnEvent += (_, e) =>
        {
            if (e is not { } json)
            {
                return;
            }

            if (!TryGetString(json, "requestId", out var requestId))
            {
                return;
            }

            _ = SafeSendAsync(cdp, "Fetch.continueRequest", new Dictionary<string, object>
            {
                ["requestId"] = requestId
            });
        };
    }

    /// <summary>Đọc một thuộc tính chuỗi từ JSON của sự kiện CDP (an toàn, không ném).</summary>
    private static bool TryGetString(JsonElement json, string name, out string value)
    {
        if (json.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return value.Length > 0;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Gửi lệnh CDP nuốt lỗi (session có thể đã ngắt / request đã hủy giữa chừng).</summary>
    private static async Task SafeSendAsync(ICDPSession cdp, string method, Dictionary<string, object> args)
    {
        try { await cdp.SendAsync(method, args).ConfigureAwait(false); }
        catch { /* bỏ qua */ }
    }

    /// <summary>
    /// Tải Chromium đóng gói của Playwright cho nhánh fallback (khi máy không có Brave). Nuốt lỗi —
    /// nếu thực sự thiếu, bước lấy <c>ExecutablePath</c>/launch tiếp theo sẽ ném và được xử lý ở tầng trên.
    /// </summary>
    private static void EnsureChromiumInstalledForFallback()
    {
        try { Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }); }
        catch { /* bỏ qua — bước launch tiếp theo sẽ ném nếu thật sự thiếu */ }
    }

    /// <summary>
    /// Phiên đăng nhập <b>sở hữu tiến trình Brave</b>: <see cref="Closed"/> hoàn tất khi tiến trình
    /// thoát / CDP ngắt / context đóng; <see cref="DisposeAsync"/> ngắt CDP và KILL cả cây tiến trình
    /// Brave để không để lại tiến trình mồ côi giữ khóa hồ sơ.
    /// </summary>
    private sealed class LoginSession : ILoginSession
    {
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private readonly IBrowserContext _context;
        private readonly Process _process;

        // "TRANG LÀM VIỆC" hiện tại của các hàm flow đơn (mô hình nhiều-shop): tab của shop đang được mở qua
        // OpenShopDetailAsync. null → dùng Pages[0] (tab gốc / danh sách shop). Các hàm flow đọc qua WorkPage()
        // để chạy trên ĐÚNG tab shop thay vì cứng Pages[0]. volatile: RunAsync (thread nền) đặt, hàm flow đọc.
        private volatile IPage? _workPage;

        // Hoàn tất khi cửa sổ đóng (tiến trình Brave thoát / CDP ngắt). RunContinuationsAsynchronously
        // để không chạy tiếp phần chờ ngay trong callback sự kiện của Playwright/Process.
        private readonly TaskCompletionSource _closedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LoginSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, Process process)
        {
            _playwright = playwright;
            _browser = browser;
            _context = context;
            _process = process;

            // Người dùng đóng cửa sổ → tiến trình Brave thoát (tín hiệu chính); kèm CDP ngắt / context đóng.
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => _closedTcs.TrySetResult();
            _browser.Disconnected += (_, _) => _closedTcs.TrySetResult();
            _context.Close += (_, _) => _closedTcs.TrySetResult();

            // Phòng trường hợp tiến trình đã thoát trước khi gắn handler.
            if (_process.HasExited)
            {
                _closedTcs.TrySetResult();
            }
        }

        public Task Closed => _closedTcs.Task;

        public bool IsClosed => _closedTcs.Task.IsCompleted;

        public Process? BraveProcess => _process;

        public int OpenPageCount
        {
            get
            {
                // Context đã ngắt (browser chết) → coi như không còn cửa sổ.
                try { return _context.Pages.Count; }
                catch { return 0; }
            }
        }

        /// <summary>Đặt "trang làm việc" hiện tại (tab shop) cho các hàm flow đơn. null → về Pages[0].</summary>
        internal void SetWorkPage(IPage? p) => _workPage = p;

        /// <summary>"Trang làm việc" hiện tại của các hàm flow đơn: <see cref="_workPage"/> (tab shop) nếu đã đặt,
        /// ngược lại Pages[0] (tab gốc). null nếu không còn tab nào.</summary>
        private IPage? WorkPage()
        {
            var wp = _workPage;
            if (wp is not null && !wp.IsClosed)
            {
                return wp;
            }
            try { return _context.Pages.Count > 0 ? _context.Pages[0] : null; }
            catch { return null; }
        }

        // Selector ô đăng nhập Shopee (thử theo thứ tự; selector Shopee CÓ THỂ ĐỔI → luôn có fallback,
        // không thấy gì thì bỏ qua để người dùng tự nhập tay).
        private static readonly string[] UserSelectors =
        {
            "input[name='loginKey']",       // ô user chính của Shopee
            "input[type='text']",           // fallback: ô text đầu tiên
            "input[type='email']",
            "input[type='tel']",
        };

        private static readonly string[] PasswordSelectors =
        {
            "input[name='password']",       // ô mật khẩu chính
            "input[type='password']",       // fallback theo type
        };

        private static readonly string[] SubmitSelectors =
        {
            "button[type='submit']",        // nút submit chính
            "button:has-text('Đăng nhập')", // fallback: nút chứa chữ "Đăng nhập"
            "button:has-text('ĐĂNG NHẬP')",
        };

        // ===================== Nền tảng tài khoản phụ (subaccount.shopee.com) =====================
        // Form login subaccount là Vue SPA: input KHÔNG có name → dò trong .login-card trước, rồi placeholder,
        // rồi type (fallback rộng nhất cuối). Nút "Đăng nhập" là <button type="button"> (KHÔNG phải submit) chứa
        // <span>Đăng nhập</span> → tuyệt đối không dò button[type='submit']; khớp text bằng SignInRegex có sẵn.
        private static readonly string[] SubUserSelectors =
            { ".login-card input[type='text']", "input[placeholder*='Tên đăng nhập']", "input[placeholder*='SĐT']", "input[type='text']" };
        private static readonly string[] SubPassSelectors =
            { ".login-card input[type='password']", "input[type='password']" };
        private static readonly string[] SubSubmitSelectors =
            { ".login-card button.shopee-button--primary", "button.shopee-button--primary", "button", "[role='button']" };

        // Nav trái "Tài khoản của tôi" (tín hiệu ĐÃ đăng nhập) + entry "Kênh Người bán" (mở Seller Centre). Mỗi regex
        // chứa CẢ dạng có dấu (khớp InnerText thô NFC qua FindVisibleByTextAsync) LẪN dạng không dấu (khớp text đã qua
        // NormalizeForMatch trong matcher/test, và trang render ascii). KHÔNG bám text EN cứng — có nhánh vi + en.
        private static readonly Regex MyAccountNavRegex =
            new(@"tài khoản của tôi|tai khoan cua toi|my account", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Dùng ở bước bắc cầu SSO cuối TryLoginSubaccountAsync (click "Kênh Người bán" để chuyển sang Seller Centre).
        private static readonly Regex SellerChannelRegex =
            new(@"kênh người bán|kenh nguoi ban|seller\s*cent(re|er)|seller\s*channel", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public async Task<bool> TryHumanLoginAsync(string user, string password, CancellationToken ct = default)
        {
            try
            {
                // 1) Đã đăng nhập sẵn (profile bền) → bỏ qua, không tự điền.
                if (ShopeeLoginCookies.IsLoggedIn(await CaptureCookiesJsonAsync().ConfigureAwait(false)))
                {
                    return false;
                }

                var page = _context.Pages.Count > 0 ? _context.Pages[0] : null;
                if (page is null)
                {
                    return false;
                }

                // 2) Dò ô đăng nhập (timeout ngắn). Không thấy → return false (người dùng tự nhập tay).
                var userInput = await FindFirstVisibleAsync(page, UserSelectors, 5000, ct).ConfigureAwait(false);
                if (userInput is null)
                {
                    return false;
                }

                var passInput = await FindFirstVisibleAsync(page, PasswordSelectors, 3000, ct).ConfigureAwait(false);
                if (passInput is null)
                {
                    return false;
                }

                // Random tạo nội bộ — app dùng ngẫu nhiên thật (không cần seed).
                var rng = new Random();

                // Con trỏ chuột bắt đầu ở giữa viewport (đọc kích thước thật; null → mặc định 640x360).
                var vp = page.ViewportSize;
                double mx = vp is not null ? vp.Width / 2.0 : 640;
                double my = vp is not null ? vp.Height / 2.0 : 360;

                // 3) Điền user rồi password: di chuột cong + click + gõ từng ký tự có delay.
                (mx, my) = await HumanFillAsync(page, userInput, user, mx, my, rng, ct).ConfigureAwait(false);
                (mx, my) = await HumanFillAsync(page, passInput, password, mx, my, rng, ct).ConfigureAwait(false);

                // 4) Bấm nút đăng nhập (nếu tìm thấy). KHÔNG xử lý captcha/OTP.
                var submit = await FindFirstVisibleAsync(page, SubmitSelectors, 3000, ct).ConfigureAwait(false);
                if (submit is not null)
                {
                    await HumanMoveAndClickAsync(page, submit, mx, my, rng, ct).ConfigureAwait(false);
                }

                return true;
            }
            catch
            {
                // Bất kỳ lỗi nào → bỏ qua, để người dùng tự thao tác (KHÔNG phá luồng).
                return false;
            }
        }

        // ===================== Phát hiện trạng thái trang + verify qua email Hotmail =====================

        // Selector ô đăng nhập Shopee dùng để NHẬN DIỆN "đang ở form login" (CỤ THỂ, không dùng input[type=text]
        // chung — trang bán hàng đã đăng nhập có ô tìm kiếm sẽ nhận nhầm). Kiểm hiển thị bằng getClientRects.
        private static readonly string[] LoginFormDetectSelectors =
        {
            "input[name='loginKey']",
            "input[name='password']",
            "input[type='password']",
        };

        // --- Selector đăng nhập Microsoft/Outlook (đổi thường xuyên → luôn nhiều fallback, timeout ngắn bỏ qua được) ---
        private static readonly string[] MsUserSelectors =
            { "input[type='email']", "input[name='loginfmt']", "#i0116" };
        private static readonly string[] MsPasswordSelectors =
            { "input[name='passwd']", "input[type='password']", "#i0118" };
        private static readonly string[] MsSubmitSelectors =
            { "#idSIButton9", "input[type='submit']", "button[type='submit']" };
        private static readonly string[] MsUsePasswordSelectors =
            { "#idA_PWD_SwitchToPassword", "a", "[role='button']", "button", "span" };
        // Link "Các cách khác để đăng nhập" trên form mới "Xác minh email của bạn" (Fluent UI):
        // span[role='button'] class fui-Link trong span[data-testid='viewFooter'].
        private static readonly string[] MsOtherWaysSelectors =
            { "span[role='button']", "[role='button']", "a", "button" };
        // Lựa chọn "Mật khẩu"/"Password" trên màn danh sách cách đăng nhập (sau khi bấm "Các cách khác"):
        // clickable trước — thứ tự selector là thứ tự ưu tiên (button/role trước div/span to).
        private static readonly string[] MsPasswordOptionSelectors =
            { "button", "[role='button']", "[role='radio']", "[role='listitem']", "[role='link']", "div[data-testid]", "span" };
        // KMSI ("Stay signed in?") chỉ dùng ID: UI cũ là <input value="Yes"> KHÔNG có innerText → không match theo text.
        // KHÔNG dùng "button[type='submit']" trần: trên form mới "Xác minh email" nút submit chính là "Gửi mã" → click nhầm.
        private static readonly string[] MsKmsiYesSelectors =
            { "#acceptButton", "#idSIButton9" };
        // Nút "Đăng nhập"/"Sign in" ở trang landing (khi chưa nhảy thẳng vào form nhập email).
        private static readonly string[] MsSignInSelectors =
            { "a[data-task='signin']", "a[href*='login.live.com']", "a[href*='login.microsoftonline']", "a[href*='login']", "a", "button", "[role='button']" };

        // --- Regex đa ngôn ngữ (vi/en), KHÔNG bám text EN cứng ---
        private static readonly Regex VerifyEmailOptionRegex =
            new("email", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UsePasswordRegex =
            new(@"use.*password|dùng mật khẩu|sử dụng mật khẩu|mật khẩu|mat khau", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Link "Các cách khác để đăng nhập" (footer form "Xác minh email của bạn" mới của Microsoft).
        private static readonly Regex OtherWaysRegex =
            new(@"cách khác để đăng nhập|cach khac de dang nhap|other ways to sign in", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Nút "Có"/"Yes" ở màn KMSI mới (Fluent) — nút submit generic CHỈ được click khi text khớp đúng đây.
        private static readonly Regex KmsiYesRegex =
            new(@"^\s*(yes|có|co)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ShopeeSenderRegex =
            new("shopee", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Tab "Khác"/"Ưu tiên" của hộp thư Outlook — UI đổi theo NGÔN NGỮ tài khoản (vi/en/es/pt/fr...). Thêm
        // đa ngôn ngữ; các từ thêm đều KHÔNG dấu (Otros/Prioritarios...) nên khớp chắc, không dính lỗi NFC/NFD.
        private static readonly Regex OtherPivotRegex =
            new(@"^\s*(other|otros|outros|autres|khác|khac)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FocusedPivotRegex =
            new(@"^\s*(focused|prioritarios|prioritaire|prioritaires|ưu tiên|uu tien)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Text CỦA LINK xác nhận trong mail "Cảnh báo bảo mật" của Shopee — link thường CHỈ bọc "TẠI ĐÂY" (không
        // phải cả câu "xác nhận tại đây") nên phải bắt riêng "tại đây". CỐ Ý BỎ "here"/"click here": chữ "here"
        // dính cả link trong mail TRẢ HÀNG của Shopee → click nhầm; mail đã được lọc đúng "Cảnh báo bảo mật" nên
        // chỉ cần khớp các cụm xác nhận tiếng Việt an toàn.
        private static readonly Regex ConfirmLinkRegex =
            new(@"xác nhận|xac nhan|verify|confirm|đúng là tôi|dung la toi|yes,?\s*it'?s me|tại đây|tại đấy|tai day|nhấn vào đây|bấm vào đây|nhan vao day|bam vao day", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SignInRegex =
            new(@"sign\s*in|đăng nhập|dang nhap", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Thông báo Shopee đã XÁC NHẬN đăng nhập thành công (trên tab mở ra sau khi bấm "TẠI ĐÂY") — chờ dấu
        // hiệu này rồi mới đóng tab, kẻo đóng sớm khi Shopee CHƯA kịp ghi nhận xác nhận.
        private static readonly Regex ConfirmSuccessRegex =
            new(@"thành công|thanh cong|đã xác nhận|da xac nhan|xác nhận đăng nhập|xac nhan dang nhap|verified|confirmed|success", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Trang mở ra sau khi bấm "TẠI ĐÂY" báo link đã HẾT HẠN/HẾT HIỆU LỰC (Shopee gửi nhiều mail "Cảnh báo
        // bảo mật" khi thử lại nhiều lần → link mail cũ hết hạn). Gặp trang này thì KHÔNG coi là xác nhận thành
        // công — phải quay lại chờ mail MỚI HƠN. Liệt kê cả dạng có dấu lẫn không dấu (khớp IgnoreCase).
        private static readonly Regex ConfirmExpiredRegex =
            new(@"hết hiệu lực|het hieu luc|hết hạn|het han|đã hết|da het|expired|no longer valid", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Nút "Gửi lại" trên trang xác minh Shopee (sellerPage) — bấm để Shopee GỬI LẠI mail xác thực khi chờ
        // mãi không thấy mail. Khớp text nút (InnerText "Gửi lại").
        private static readonly Regex ResendVerifyRegex =
            new(@"^\s*(gửi lại|gui lai|resend)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Kết quả click link xác nhận trong một mail: không có link / đã xác nhận / link hết hạn (cần chờ mail mới).
        private enum ConfirmOutcome { NoLink, Confirmed, Expired }

        /// <summary>Chuẩn hóa text để so khớp bền: bỏ dấu tiếng Việt (kể cả đ→d), gộp mọi cụm khoảng trắng về một
        /// dấu cách, trim, hạ chữ thường. Dùng cho lọc tiêu đề "Cảnh báo bảo mật" (so <c>Contains</c> không dấu).</summary>
        internal static string NormalizeForMatch(string? s)
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

        /// <summary>True nếu text của một dòng mail (InnerText: người gửi + tiêu đề + preview) là mail
        /// <b>"Cảnh báo bảo mật Tài khoản Shopee"</b> — người gửi khớp "shopee" VÀ nội dung (chuẩn hóa không dấu)
        /// CHỨA "canh bao bao mat". Loại mail trả hàng/khuyến mãi/khác của Shopee.</summary>
        internal static bool IsSecurityWarningMailRow(string? rowText)
        {
            if (string.IsNullOrWhiteSpace(rowText) || !ShopeeSenderRegex.IsMatch(rowText))
            {
                return false;
            }

            return NormalizeForMatch(rowText).Contains("canh bao bao mat", StringComparison.Ordinal);
        }

        /// <summary>True nếu <paramref name="text"/> khớp <see cref="ConfirmLinkRegex"/> (text của link cần bấm,
        /// vd "TẠI ĐÂY"). Phơi ra để test — KHÔNG còn khớp "here"/"click here".</summary>
        internal static bool MatchesConfirmLink(string? text)
            => !string.IsNullOrEmpty(text) && ConfirmLinkRegex.IsMatch(text);

        /// <summary>True nếu <paramref name="text"/> khớp <see cref="ConfirmExpiredRegex"/> (trang báo link đã hết
        /// hạn/hết hiệu lực). Phơi ra để test.</summary>
        internal static bool MatchesConfirmExpired(string? text)
            => !string.IsNullOrEmpty(text) && ConfirmExpiredRegex.IsMatch(text);

        /// <summary>True nếu <paramref name="text"/> là nav "Tài khoản của tôi" trên Nền tảng tài khoản phụ: CHUẨN HÓA
        /// không dấu (<see cref="NormalizeForMatch"/> — trị cả NFC/NFD, chữ HOA) rồi khớp <see cref="MyAccountNavRegex"/>.
        /// KHÔNG khớp "Phân bổ chat" / "Tài khoản" đơn lẻ. Phơi ra để test.</summary>
        internal static bool MatchesMyAccountNav(string? text)
            => MyAccountNavRegex.IsMatch(NormalizeForMatch(text));

        /// <summary>True nếu <paramref name="text"/> là entry "Kênh Người bán"/"Seller Centre": CHUẨN HÓA không dấu
        /// (<see cref="NormalizeForMatch"/>) rồi khớp <see cref="SellerChannelRegex"/>. KHÔNG khớp "Kênh" đơn lẻ.
        /// Phơi ra để test.</summary>
        internal static bool MatchesSellerChannelEntry(string? text)
            => SellerChannelRegex.IsMatch(NormalizeForMatch(text));

        public async Task<ShopeePageState> DetectPageStateAsync(CancellationToken ct = default)
        {
            try
            {
                var page = _context.Pages.Count > 0 ? _context.Pages[0] : null;
                if (page is null)
                {
                    return ShopeePageState.Unknown;
                }

                // 1) URL trước: cookie phiên có thể CÒN mà vẫn bị bắt verify/captcha (chép logic từ
                //    ShopeeAccountChecker.WaitOutcomeAsync, điều chỉnh cho seller site).
                var url = (page.Url ?? string.Empty).ToLowerInvariant();
                if (url.Contains("captcha"))
                {
                    return ShopeePageState.Captcha;
                }
                if (url.Contains("/verify"))
                {
                    return ShopeePageState.Verify;
                }

                // 2) Form đăng nhập: ô user/pass HIỂN THỊ (kiểm getClientRects — KHÔNG offsetParent).
                if (await IsAnyVisibleByClientRectsAsync(page, LoginFormDetectSelectors, ct).ConfigureAwait(false))
                {
                    return ShopeePageState.LoginForm;
                }

                // 3) Không ở form login mà có alert xác minh (otp/mã xác/xác minh) → Verify (tín hiệu phụ).
                var alert = (await ReadAlertTextAsync(page).ConfigureAwait(false)).ToLowerInvariant();
                if (alert.Contains("otp") || alert.Contains("mã xác") || alert.Contains("ma xac")
                    || alert.Contains("xác minh") || alert.Contains("xac minh"))
                {
                    return ShopeePageState.Verify;
                }

                // 4) Cookie phiên đăng nhập → LoggedIn; còn lại Unknown.
                if (ShopeeLoginCookies.IsLoggedIn(await CaptureCookiesJsonAsync().ConfigureAwait(false)))
                {
                    return ShopeePageState.LoggedIn;
                }

                return ShopeePageState.Unknown;
            }
            catch
            {
                // Không bao giờ ném (kể cả hủy) — trả Unknown, caller đọc ct riêng để dừng.
                return ShopeePageState.Unknown;
            }
        }

        public async Task<bool> TryLoginSubaccountAsync(
            string user, string password, string? verifyEmail, string? verifyEmailPassword,
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

            var page = _context.Pages.Count > 0 ? _context.Pages[0] : null;
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
                    if (await IsAnyVisibleByClientRectsAsync(page, SubPassSelectors, sct).ConfigureAwait(false))
                    {
                        onLoginForm = true;
                        break;
                    }

                    // "Đã đăng nhập" = phần tử khớp nav "Tài khoản của tôi" HIỂN THỊ.
                    if (await FindVisibleByTextAsync(page, accountNavSelectors, MyAccountNavRegex, sct, 1000).ConfigureAwait(false) is not null)
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
                    var userInput = await FindFirstVisibleByRectsAsync(page, SubUserSelectors, 8000, sct).ConfigureAwait(false);
                    var passInput = await FindFirstVisibleByRectsAsync(page, SubPassSelectors, 4000, sct).ConfigureAwait(false);
                    if (userInput is null || passInput is null)
                    {
                        L("Không thấy ô đăng nhập subaccount — đăng nhập tay. " + await DiagAsync(page).ConfigureAwait(false));
                        return false;
                    }

                    (mx, my) = await HumanFillAsync(page, userInput, user, mx, my, rng, sct).ConfigureAwait(false);
                    (mx, my) = await HumanFillAsync(page, passInput, password, mx, my, rng, sct).ConfigureAwait(false);

                    // Nút "Đăng nhập" là <button type="button"> chứa <span>Đăng nhập</span> — khớp text bằng SignInRegex.
                    var submit = await FindVisibleByTextAsync(page, SubSubmitSelectors, SignInRegex, sct, 5000).ConfigureAwait(false);
                    if (submit is null)
                    {
                        L("Không thấy nút 'Đăng nhập' subaccount — đăng nhập tay. " + await DiagAsync(page).ConfigureAwait(false));
                        return false;
                    }
                    (mx, my, _) = await TryHumanClickVisibleAsync(page, submit, mx, my, rng, sct).ConfigureAwait(false);
                    L("Đã bấm Đăng nhập — chờ Shopee đòi mã xác thực...");

                    // ── Bước 4: mở hộp thư cho NGƯỜI DÙNG tự lấy mã (KHÔNG tự verify, KHÔNG tự bấm gì trong mail).
                    if (!string.IsNullOrWhiteSpace(verifyEmail) && !string.IsNullOrWhiteSpace(verifyEmailPassword))
                    {
                        try
                        {
                            bool mailLoggedIn;
                            (mailPage, mailLoggedIn) = await OpenMailboxSignedInAsync(_context, verifyEmail!, verifyEmailPassword!, log, rng, sct).ConfigureAwait(false);
                            L(mailLoggedIn
                                ? "Đã mở hộp thư ở tab bên — lấy mã rồi nhập vào trang Shopee."
                                : "Chưa đăng nhập được hộp thư tự động — GIỮ tab mail mở để bạn tự đăng nhập, lấy mã rồi nhập vào trang Shopee.");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            L("Lỗi khi mở hộp thư: " + ex.Message + " — bạn tự lấy mã và nhập vào trang Shopee.");
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
                        if (await FindVisibleByTextAsync(page, accountNavSelectors, MyAccountNavRegex, sct, 1000).ConfigureAwait(false) is not null)
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

                var myAccountNav = await FindVisibleByTextAsync(page, accountNavSelectors, MyAccountNavRegex, sct, 10000).ConfigureAwait(false);
                if (myAccountNav is null)
                {
                    L("Không thấy 'Tài khoản của tôi' — GIỮ cửa sổ để bạn thao tác tay. " + await DiagAsync(page).ConfigureAwait(false));
                    return false;
                }
                (mx, my, _) = await TryHumanClickVisibleAsync(page, myAccountNav, mx, my, rng, sct).ConfigureAwait(false);
                await Task.Delay(rng.Next(1500, 3001), sct).ConfigureAwait(false);

                // ── Bước 7: click "Kênh Người bán" → chờ Seller Centre (tab MỚI HOẶC cùng tab). Hứng tab mới bằng
                //    event _context.Page TRƯỚC khi click (không bỏ lỡ popup nhanh); song song vẫn quét _context.Pages.
                var sellerEntry = await FindVisibleByTextAsync(
                    page, new[] { "span.entry-text", ".entry", "span", "div", "[role='button']", "a" },
                    SellerChannelRegex, sct, 10000).ConfigureAwait(false);
                if (sellerEntry is null)
                {
                    L("Không thấy entry 'Kênh Người bán' — GIỮ cửa sổ để bạn thao tác tay. " + await DiagAsync(page).ConfigureAwait(false));
                    return false;
                }

                IPage? popped = null;
                void OnNewPage(object? _, IPage p) => popped ??= p;
                _context.Page += OnNewPage;

                IPage sellerPage = page;
                bool sellerInNewTab = false;
                try
                {
                    (mx, my, _) = await TryHumanClickVisibleAsync(page, sellerEntry, mx, my, rng, sct).ConfigureAwait(false);
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
                            : _context.Pages.FirstOrDefault(p => p != page && UrlIsBanhang(p.Url));
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
                    _context.Page -= OnNewPage;
                }

                if (!UrlIsBanhang(sellerPage.Url))
                {
                    var tabs = new List<string>();
                    foreach (var p in _context.Pages)
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
                    else if (_context.Pages.Count == 0 || _context.Pages[0] != sellerPage)
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
                L("Lỗi khi đăng nhập Nền tảng tài khoản phụ: " + ex.Message + " — GIỮ cửa sổ để bạn thao tác tay.");
                return false;
            }
            // KHÔNG đóng tab seller/subaccount ở finally — việc đóng tab subaccount làm CÓ CHỦ ĐÍCH ở Bước 8; tab mail
            // đóng ở Bước 6 (đường thành công) hoặc GIỮ mở ở đường lỗi cho người dùng tự lấy mã.
        }

        public async Task<bool> TryVerifyByEmailAsync(
            string verifyEmail, string verifyEmailPassword, bool autoConfirm, Action<string>? log = null, CancellationToken ct = default)
        {
            void L(string m) => log?.Invoke(m);

            if (string.IsNullOrWhiteSpace(verifyEmail) || string.IsNullOrWhiteSpace(verifyEmailPassword))
            {
                L("Chưa cấu hình Email xác minh cho tài khoản — bỏ qua verify tự động (verify tay).");
                return false;
            }

            var page = _context.Pages.Count > 0 ? _context.Pages[0] : null;
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
                var emailOption = await FindVisibleByTextAsync(
                    page, new[] { "button", "a", "[role='button']", "label", "li", "div[class*='item']", "div[class*='option']" },
                    VerifyEmailOptionRegex, vct, 8000).ConfigureAwait(false);
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
                (mx, my, _) = await TryHumanClickVisibleAsync(page, emailOption, mx, my, rng, vct).ConfigureAwait(false);

                // Chờ trang đổi (thường sang màn "đã gửi link xác minh, kiểm tra email").
                await Task.Delay(rng.Next(2000, 5000), vct).ConfigureAwait(false);

                // BƯỚC 2: mở tab mới đăng nhập hộp thư Hotmail/Outlook rồi vào hộp thư (helper dùng chung với luồng
                //    subaccount). Login lỗi → bỏ qua verify như cũ (finally đóng tab mail vì keepMailOpenForManual=false).
                bool mailLoggedIn;
                (mailPage, mailLoggedIn) = await OpenMailboxSignedInAsync(_context, verifyEmail, verifyEmailPassword, log, rng, vct).ConfigureAwait(false);
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
                if (!await OpenShopeeMailAndConfirmAsync(mailPage!, page, log, rng, vct).ConfigureAwait(false))
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
                    if (await DetectPageStateAsync(vct).ConfigureAwait(false) == ShopeePageState.LoggedIn)
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
                    var toClose = _browser.Contexts.SelectMany(c => c.Pages)
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
            var userField = await FindFirstVisibleByRectsAsync(mailPage, MsUserSelectors, 6000, ct).ConfigureAwait(false);
            if (userField is null)
            {
                var signIn = await FindVisibleByTextAsync(mailPage, MsSignInSelectors, SignInRegex, ct, 4000).ConfigureAwait(false);
                if (signIn is not null)
                {
                    L("Chưa vào form đăng nhập — bấm 'Đăng nhập'...");
                    (mx, my, _) = await TryHumanClickVisibleAsync(mailPage, signIn, mx, my, rng, ct).ConfigureAwait(false);
                    await Task.Delay(rng.Next(1500, 3500), ct).ConfigureAwait(false);
                }
                userField = await FindFirstVisibleByRectsAsync(mailPage, MsUserSelectors, 15000, ct).ConfigureAwait(false);
            }

            // 1) Username (đã tìm ở bước 0; điền nếu thấy).
            if (userField is not null)
            {
                L("Nhập email đăng nhập hộp thư...");
                (mx, my) = await HumanFillAsync(mailPage, userField, email, mx, my, rng, ct).ConfigureAwait(false);
                var next = await FindFirstVisibleByRectsAsync(mailPage, MsSubmitSelectors, 3000, ct).ConfigureAwait(false);
                if (next is not null)
                {
                    (mx, my) = await HumanMoveAndClickAsync(mailPage, next, mx, my, rng, ct).ConfigureAwait(false);
                }
                await Task.Delay(rng.Next(1500, 3000), ct).ConfigureAwait(false);

                if (await IsSelectorVisibleAsync(mailPage, "#usernameError").ConfigureAwait(false))
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

                passField = await FindFirstVisibleByRectsAsync(mailPage, MsPasswordSelectors, 1500, ct).ConfigureAwait(false);
                if (passField is not null)
                {
                    break;
                }

                // "Sử dụng mật khẩu" (màn chọn cách) HOẶC tile "Nhập mật khẩu" (màn 'các cách khác') — khớp KHÔNG
                // dấu để tránh lỗi NFC/NFD (text MS dạng tổ hợp dấu).
                var usePwd = await FindByNormalizedTextInFramesAsync(mailPage, MsUsePasswordSelectors, new[] { "mat khau", "password", "contrasena" }, ct, 1200).ConfigureAwait(false);
                if (usePwd is not null)
                {
                    L("Chọn 'Dùng mật khẩu' / 'Nhập mật khẩu'...");
                    (mx, my, _) = await TryHumanClickVisibleAsync(mailPage, usePwd, mx, my, rng, ct).ConfigureAwait(false);
                    await Task.Delay(rng.Next(1200, 2200), ct).ConfigureAwait(false);
                    continue;
                }

                // Form mới "Xác minh email của bạn" (Fluent, passwordless): "Các cách khác để đăng nhập" → (vòng sau
                // thấy tile "Nhập mật khẩu"). Quét mọi frame + khớp KHÔNG dấu (tránh lỗi NFC/NFD). Click 1 lần rồi
                // để vòng sau lo tile mật khẩu.
                var otherWays = await FindByNormalizedTextInFramesAsync(mailPage, MsOtherWaysSelectors, new[] { "cach khac de dang nhap", "other ways to sign in", "otras formas de iniciar sesion" }, ct, 1200).ConfigureAwait(false);
                if (otherWays is not null)
                {
                    L("Form 'Xác minh email' — bấm 'Các cách khác để đăng nhập'...");
                    (mx, my, _) = await TryHumanClickVisibleAsync(mailPage, otherWays, mx, my, rng, ct).ConfigureAwait(false);
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
                (mx, my) = await HumanFillAsync(mailPage, passField, password, mx, my, rng, ct).ConfigureAwait(false);
                var signIn = await FindFirstVisibleByRectsAsync(mailPage, MsSubmitSelectors, 3000, ct).ConfigureAwait(false);
                if (signIn is not null)
                {
                    (mx, my) = await HumanMoveAndClickAsync(mailPage, signIn, mx, my, rng, ct).ConfigureAwait(false);
                }
                await Task.Delay(rng.Next(2000, 4000), ct).ConfigureAwait(false);

                if (await IsSelectorVisibleAsync(mailPage, "#passwordError").ConfigureAwait(false))
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
                var onKmsi = await IsAnyVisibleByClientRectsAsync(
                    mailPage, new[] { "[data-testid='kmsiVideo']", "[data-testid='kmsiImage']" }, ct).ConfigureAwait(false);
                var kmsiSelectors = onKmsi
                    ? new[] { "[data-testid='primaryButton']", "#acceptButton", "#idSIButton9" }
                    : MsKmsiYesSelectors;
                var kmsi = await FindFirstVisibleByRectsAsync(mailPage, kmsiSelectors, 1000, ct).ConfigureAwait(false);
                if (kmsi is not null)
                {
                    L("Bấm 'Có' để giữ đăng nhập hộp thư...");
                    (mx, my) = await HumanMoveAndClickAsync(mailPage, kmsi, mx, my, rng, ct).ConfigureAwait(false);
                    await Task.Delay(rng.Next(1500, 3000), ct).ConfigureAwait(false);
                    break;
                }
                await Task.Delay(rng.Next(500, 900), ct).ConfigureAwait(false);
            }

            return true;
        }

        /// <summary>
        /// Trong hộp thư Outlook: ưu tiên tab "Ưu tiên"/"Focused" (không có mail Shopee thì thử "Khác"/"Other"),
        /// DUYỆT các mail <b>"Cảnh báo bảo mật"</b> của Shopee theo thứ tự MỚI NHẤT trước — mở lần lượt, mail nào
        /// có link xác nhận ("TẠI ĐÂY") thì click. Shopee gửi nhiều mail cảnh báo bảo mật khi thử lại nhiều lần;
        /// nếu link mở ra báo HẾT HẠN thì bỏ, tải lại hộp thư + chờ để tìm mail mới hơn. Lặp reload + chờ tới hết
        /// deadline (~6'). Trả <c>true</c> khi đã click được link (đã xác nhận).
        /// </summary>
        private async Task<bool> OpenShopeeMailAndConfirmAsync(
            IPage mailPage, IPage sellerPage, Action<string>? log, Random rng, CancellationToken ct)
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
                await TryClickPivotAsync(mailPage, "focused", FocusedPivotRegex, "Ưu tiên", log, rng, ct).ConfigureAwait(false);
                await Task.Delay(rng.Next(800, 1500), ct).ConfigureAwait(false);
                var rows = await FindAllShopeeMailRowsAsync(mailPage, MaxMailsPerRound, ct).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    await TryClickPivotAsync(mailPage, "other", OtherPivotRegex, "Khác", log, rng, ct).ConfigureAwait(false);
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
                        try { (mx, my, clickedRow) = await HumanMoveAndClickVerifiedAsync(mailPage, rows[i], mx, my, rng, ct).ConfigureAwait(false); }
                        catch { continue; }
                        if (!clickedRow)
                        {
                            L($"Mail Shopee #{i + 1}: danh sách đang xê dịch (quảng cáo?) — chưa click được, thử lại vòng sau.");
                            continue;
                        }
                        await Task.Delay(rng.Next(1200, 2500), ct).ConfigureAwait(false);

                        triedNewMail = true;
                        var outcome = await ClickConfirmLinkInMailAsync(mailPage, sellerPage, log, rng, ct).ConfigureAwait(false);
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
        /// tìm nút theo <see cref="ResendVerifyRegex"/> trong button/a/[role=button] rồi click kiểu người. Trả
        /// <c>true</c> nếu đã bấm được nút.</summary>
        private static async Task<bool> TryResendVerifyEmailAsync(IPage sellerPage, Action<string>? log, Random rng, CancellationToken ct)
        {
            void L(string m) => log?.Invoke(m);
            try { await sellerPage.BringToFrontAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
            await Task.Delay(rng.Next(600, 1400), ct).ConfigureAwait(false);

            var btn = await FindVisibleByTextAsync(
                sellerPage, new[] { "button", "a", "[role='button']" }, ResendVerifyRegex, ct, 6000).ConfigureAwait(false);
            if (btn is null)
            {
                L("Không thấy nút 'Gửi lại' trên trang xác minh Shopee — bỏ qua lần gửi lại này.");
                return false;
            }

            var vp = sellerPage.ViewportSize;
            double mx = vp is not null ? vp.Width / 2.0 : 640;
            double my = vp is not null ? vp.Height / 2.0 : 360;
            var (_, _, clicked) = await TryHumanClickVisibleAsync(sellerPage, btn, mx, my, rng, ct).ConfigureAwait(false);
            return clicked;
        }

        /// <summary>
        /// Trong reading-pane của mail đang mở (thường nằm trong iframe), dò link/nút xác nhận (text vi/en
        /// khớp <see cref="ConfirmLinkRegex"/>) rồi click kiểu người. Link thường mở TAB MỚI (target _blank) →
        /// bắt tab mới bằng snapshot trước/sau (như pattern bắt tab phiếu), chờ tải rồi ĐÓNG tab đó. Trả:
        /// <see cref="ConfirmOutcome.NoLink"/> nếu mail không có link xác nhận; <see cref="ConfirmOutcome.Expired"/>
        /// nếu trang mở ra báo link đã hết hạn/hết hiệu lực (đã đóng tab, caller cần chờ mail MỚI HƠN);
        /// <see cref="ConfirmOutcome.Confirmed"/> nếu Shopee báo thành công HOẶC không rõ kết quả (giữ hành vi lạc
        /// quan cũ để không hồi quy ca xác nhận thật nhưng trang thiếu text thành công).
        /// </summary>
        private async Task<ConfirmOutcome> ClickConfirmLinkInMailAsync(
            IPage mailPage, IPage sellerPage, Action<string>? log, Random rng, CancellationToken ct)
        {
            void L(string m) => log?.Invoke(m);

            // Dò trong MỌI frame (thân mail HTML hay nằm trong iframe reading-pane).
            var confirmEl = await FindVisibleByTextInFramesAsync(
                mailPage, new[] { "a", "button", "[role='button']" }, ConfirmLinkRegex, ct, 6000).ConfigureAwait(false);
            if (confirmEl is null)
            {
                return ConfirmOutcome.NoLink;
            }

            L("Bấm link xác nhận trong mail...");
            var before = _browser.Contexts.SelectMany(c => c.Pages).ToList();

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
                await HumanMoveAndClickAsync(mailPage, confirmEl, mx, my, rng, ct).ConfigureAwait(false);
            }

            // Link thường mở TAB MỚI → bắt tab (poll ≤10s).
            IPage? confirmTab = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (confirmTab is null && DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    confirmTab = _browser.Contexts.SelectMany(c => c.Pages)
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
                        if (ConfirmExpiredRegex.IsMatch(body))
                        {
                            expired = true;
                            break;
                        }
                        if (ConfirmSuccessRegex.IsMatch(body))
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
            var pivot = await FindFirstVisibleByRectsAsync(
                page, new[] { $"button[role='tab'][value='{pivotValue}']", $"[role='tab'][value='{pivotValue}']" }, 2500, ct).ConfigureAwait(false);
            if (pivot is null)
            {
                pivot = await FindVisibleByTextAsync(
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
                await HumanMoveAndClickAsync(page, pivot, mx, my, rng, ct).ConfigureAwait(false);
                log?.Invoke($"Đã mở mục '{label}' trong hộp thư.");
            }
            catch { /* best-effort — bỏ qua */ }
        }

        /// <summary>Danh sách các dòng mail <b>"Cảnh báo bảo mật" của Shopee</b> ĐANG HIỂN THỊ (người gửi khớp
        /// "shopee" VÀ tiêu đề chứa "cảnh báo bảo mật" — xem <see cref="IsSecurityWarningMailRow"/>) theo thứ tự
        /// DOM (trên cùng = MỚI NHẤT), tối đa <paramref name="maxRows"/>. Trả NHIỀU dòng để caller DUYỆT vì
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
                        if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                        {
                            continue;
                        }

                        var txt = await el.InnerTextAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(txt) || !ShopeeSenderRegex.IsMatch(txt))
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
                        if (IsSecurityWarningMailRow(txt) && seenSec.Add(key))
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

        // ===================== Helper dò phần tử theo hiển thị (getClientRects) + text =====================

        /// <summary>True nếu có ÍT NHẤT một phần tử khớp một trong <paramref name="selectors"/> đang HIỂN THỊ
        /// (kiểm bằng <c>getClientRects</c> có kích thước &gt; 0 — KHÔNG dùng offsetParent). Một lượt quét,
        /// không poll (caller tự lặp nếu cần).</summary>
        private static async Task<bool> IsAnyVisibleByClientRectsAsync(IPage page, string[] selectors, CancellationToken ct)
        {
            foreach (var sel in selectors)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var visible = await page.EvaluateAsync<bool>(
                        @"(sel) => { for (const el of document.querySelectorAll(sel)) { const rs = el.getClientRects();"
                        + " for (const r of rs) { if (r.width > 0 && r.height > 0) return true; } } return false; }",
                        sel).ConfigureAwait(false);
                    if (visible)
                    {
                        return true;
                    }
                }
                catch { /* selector không dùng được trên trang này — thử selector kế */ }
            }

            return false;
        }

        /// <summary>True nếu <paramref name="el"/> đang HIỂN THỊ (getClientRects có kích thước &gt; 0). Dùng cho
        /// element handle đơn (kể cả trong iframe — eval chạy trong document của frame đó).</summary>
        private static async Task<bool> IsElementVisibleByClientRectsAsync(IElementHandle el)
        {
            try
            {
                return await el.EvaluateAsync<bool>(
                    "(node) => { const rs = node.getClientRects(); for (const r of rs) { if (r.width > 0 && r.height > 0) return true; } return false; }")
                    .ConfigureAwait(false);
            }
            catch { return false; }
        }

        /// <summary>True nếu <paramref name="selector"/> có phần tử ĐANG HIỂN THỊ (dùng cho error box Microsoft).</summary>
        private static async Task<bool> IsSelectorVisibleAsync(IPage page, string selector)
        {
            try
            {
                var el = await page.QuerySelectorAsync(selector).ConfigureAwait(false);
                return el is not null && await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false);
            }
            catch { return false; }
        }

        /// <summary>Đọc text các <c>div[role='alert']</c> của trang (nối bằng " | "). Lỗi → chuỗi rỗng.</summary>
        private static async Task<string> ReadAlertTextAsync(IPage page)
        {
            try
            {
                return await page.EvaluateAsync<string>(
                    "() => Array.from(document.querySelectorAll(\"div[role='alert']\")).map(a => a.innerText || '').join(' | ')")
                    .ConfigureAwait(false);
            }
            catch { return string.Empty; }
        }

        /// <summary>Dò phần tử ĐẦU TIÊN đang HIỂN THỊ (getClientRects) khớp một trong <paramref name="selectors"/>,
        /// poll tới hết <paramref name="timeoutMs"/>. Giống <see cref="FindFirstVisibleAsync"/> nhưng kiểm hiển
        /// thị bằng getClientRects (không offsetParent) — dùng cho form Microsoft/Outlook.</summary>
        private static async Task<IElementHandle?> FindFirstVisibleByRectsAsync(
            IPage page, string[] selectors, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                foreach (var sel in selectors)
                {
                    try
                    {
                        var el = await page.QuerySelectorAsync(sel).ConfigureAwait(false);
                        if (el is not null && await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                        {
                            return el;
                        }
                    }
                    catch { /* selector không dùng được — thử selector kế */ }
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>Dò phần tử ĐẦU TIÊN đang HIỂN THỊ khớp selector VÀ có innerText khớp <paramref name="textRegex"/>
        /// (vi/en), poll tới hết <paramref name="timeoutMs"/>. Duyệt theo thứ tự selector (ưu tiên phần tử
        /// clickable trước). Chỉ quét frame chính.</summary>
        private static async Task<IElementHandle?> FindVisibleByTextAsync(
            IPage page, string[] selectors, Regex textRegex, CancellationToken ct, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                foreach (var sel in selectors)
                {
                    IReadOnlyList<IElementHandle> els;
                    try { els = await page.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                    catch { continue; }

                    foreach (var el in els)
                    {
                        try
                        {
                            if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                            {
                                continue;
                            }

                            var txt = await el.InnerTextAsync().ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(txt) && textRegex.IsMatch(txt))
                            {
                                return el;
                            }
                        }
                        catch { /* detached / lỗi đọc — bỏ qua phần tử này */ }
                    }
                }
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>Như <see cref="FindVisibleByTextInFramesAsync"/> nhưng so khớp KHÔNG PHÂN BIỆT DẤU: chuẩn hóa
        /// InnerText qua <see cref="NormalizeForMatch"/> (FormD + bỏ dấu + đ→d + lower) rồi kiểm CHỨA một trong
        /// <paramref name="normalizedNeedles"/> (phải ĐÃ ở dạng không dấu, chữ thường). TRỊ lỗi: text tiếng Việt
        /// trên trang MS ở dạng tổ hợp dấu (NFD) khác literal regex dựng sẵn (NFC) → Regex.IsMatch trượt dù mắt
        /// thấy giống. VD "Các cách khác để đăng nhập" NFD KHÔNG khớp regex "cách khác..." NFC.</summary>
        private static async Task<IElementHandle?> FindByNormalizedTextInFramesAsync(
            IPage page, string[] selectors, string[] normalizedNeedles, CancellationToken ct, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                foreach (var frame in page.Frames)
                {
                    foreach (var sel in selectors)
                    {
                        IReadOnlyList<IElementHandle> els;
                        try { els = await frame.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                        catch { continue; }

                        foreach (var el in els)
                        {
                            try
                            {
                                if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                                {
                                    continue;
                                }

                                var txt = NormalizeForMatch(await el.InnerTextAsync().ConfigureAwait(false));
                                if (txt.Length > 0 && Array.Exists(normalizedNeedles, n => txt.Contains(n, StringComparison.Ordinal)))
                                {
                                    return el;
                                }
                            }
                            catch { /* detached — bỏ qua */ }
                        }
                    }
                }
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>Như <see cref="FindVisibleByTextAsync"/> nhưng quét MỌI frame của trang (thân mail HTML của
        /// Outlook thường nằm trong iframe reading-pane).</summary>
        private static async Task<IElementHandle?> FindVisibleByTextInFramesAsync(
            IPage page, string[] selectors, Regex textRegex, CancellationToken ct, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                foreach (var frame in page.Frames)
                {
                    foreach (var sel in selectors)
                    {
                        IReadOnlyList<IElementHandle> els;
                        try { els = await frame.QuerySelectorAllAsync(sel).ConfigureAwait(false); }
                        catch { continue; }

                        foreach (var el in els)
                        {
                            try
                            {
                                if (!await IsElementVisibleByClientRectsAsync(el).ConfigureAwait(false))
                                {
                                    continue;
                                }

                                var txt = await el.InnerTextAsync().ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(txt) && textRegex.IsMatch(txt))
                                {
                                    return el;
                                }
                            }
                            catch { /* detached — bỏ qua */ }
                        }
                    }
                }
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>
        /// Dò phần tử đầu tiên <b>đang hiển thị</b> khớp một trong <paramref name="selectors"/> (thử lần
        /// lượt), poll tới khi hết <paramref name="timeoutMs"/>. Trả <c>null</c> nếu không thấy. Nuốt lỗi
        /// từng selector (selector có thể không hợp lệ trên trang hiện tại).
        /// </summary>
        private static async Task<IElementHandle?> FindFirstVisibleAsync(
            IPage page, string[] selectors, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                foreach (var sel in selectors)
                {
                    try
                    {
                        var el = await page.QuerySelectorAsync(sel).ConfigureAwait(false);
                        if (el is not null && await el.IsVisibleAsync().ConfigureAwait(false))
                        {
                            return el;
                        }
                    }
                    catch
                    {
                        // Selector không dùng được trên trang này — thử selector kế.
                    }
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>
        /// Điền một ô kiểu người: di chuột cong tới ô + click, rồi gõ <b>từng ký tự</b> với delay ngẫu
        /// nhiên (<see cref="HumanTyping.NextCharDelayMs"/>). Trả về vị trí chuột mới (tâm ô).
        /// </summary>
        private static async Task<(double X, double Y)> HumanFillAsync(
            IPage page, IElementHandle el, string text, double mx, double my, Random rng, CancellationToken ct)
        {
            (mx, my) = await HumanMoveAndClickAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);

            // Ô có thể ĐÃ CÓ SẴN text (trình duyệt autofill / thông tin đã lưu sau khi bấm Save) → gõ đè sẽ NỐI
            // vào text cũ. Xóa SẠCH ô trước khi gõ lại: ưu tiên FillAsync("") (clear chuẩn của Playwright); lỗi
            // thì clear bằng phím (đã click nên focus đang ở ô → Ctrl+A chọn hết text TRONG ô rồi Delete).
            try
            {
                await el.FillAsync("").ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await page.Keyboard.PressAsync("Control+A").ConfigureAwait(false);
                    await Task.Delay(rng.Next(40, 100), ct).ConfigureAwait(false);
                    await page.Keyboard.PressAsync("Delete").ConfigureAwait(false);
                }
                catch { /* bỏ qua — vẫn thử gõ ở dưới */ }
            }
            await Task.Delay(rng.Next(60, 160), ct).ConfigureAwait(false);

            foreach (var ch in text)
            {
                ct.ThrowIfCancellationRequested();
                // Gõ TỪNG ký tự (KHÔNG fill/dán) + delay kiểu người.
                await page.Keyboard.TypeAsync(ch.ToString()).ConfigureAwait(false);
                await Task.Delay(HumanTyping.NextCharDelayMs(rng), ct).ConfigureAwait(false);
            }

            return (mx, my);
        }

        /// <summary>
        /// Di chuột theo <b>đường cong</b> từ (<paramref name="mx"/>,<paramref name="my"/>) tới tâm phần tử
        /// (+jitter nhỏ), tự <c>Mouse.MoveAsync</c> <b>từng điểm</b> (KHÔNG dùng <c>steps</c> lớn để đi
        /// thẳng). <b>Chỉ đưa chuột tới đích — KHÔNG click.</b> Trả về (vị trí chuột cuối, có bounding box
        /// hay không): box null → kéo phần tử vào tầm nhìn, GIỮ nguyên vị trí chuột, <c>HasBox=false</c>.
        /// </summary>
        private static async Task<(double X, double Y, bool HasBox)> HumanMoveToAsync(
            IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
        {
            // Handle có thể đã DETACHED (Vue vẽ lại form sau khi map/modal re-render) → BoundingBoxAsync ném.
            // Bọc try: lỗi handle → coi như không có box (HasBox=false), KHÔNG để exception rò lên catch ngoài.
            ElementHandleBoundingBoxResult? box;
            try { box = await el.BoundingBoxAsync().ConfigureAwait(false); }
            catch { box = null; }

            double tx, ty;
            bool hasBox;
            if (box is not null)
            {
                // Tâm ô + jitter nhỏ (không luôn nhấn đúng chính giữa).
                tx = box.X + box.Width / 2.0 + (rng.NextDouble() - 0.5) * Math.Min(box.Width * 0.3, 20);
                ty = box.Y + box.Height / 2.0 + (rng.NextDouble() - 0.5) * Math.Min(box.Height * 0.3, 8);
                hasBox = true;
            }
            else
            {
                // Không lấy được bounding box → kéo phần tử vào tầm nhìn, giữ nguyên vị trí chuột.
                try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                tx = mx;
                ty = my;
                hasBox = false;
            }

            // Số điểm theo khoảng cách (đường dài → nhiều điểm), giới hạn [12, 60] cho mượt.
            var dist = Math.Sqrt((tx - mx) * (tx - mx) + (ty - my) * (ty - my));
            var steps = Math.Clamp((int)(dist / 8) + 10, 12, 60);

            foreach (var (px, py) in HumanMouse.GeneratePath(mx, my, tx, ty, steps, rng))
            {
                ct.ThrowIfCancellationRequested();
                // Đi TỪNG điểm (steps mặc định = 1) để đường thật sự cong theo path đã sinh.
                await page.Mouse.MoveAsync((float)px, (float)py).ConfigureAwait(false);
                await Task.Delay(rng.Next(5, 26), ct).ConfigureAwait(false); // 5–25ms giữa các điểm
            }

            return (tx, ty, hasBox);
        }

        /// <summary>
        /// Di chuột theo <b>đường cong</b> tới tâm phần tử rồi click kiểu người (down + trễ + up). Trả về
        /// vị trí chuột cuối (điểm đích). <b>Click MÙ theo tọa độ — KHÔNG hit-test</b>: CHỈ dùng cho luồng
        /// đăng nhập (<see cref="TryHumanLoginAsync"/> — form login đơn giản, không có submenu cụp/flyout
        /// đè). Mọi thao tác NGHIỆP VỤ (menu/modal) dùng <see cref="HumanMoveAndClickVerifiedAsync"/>.
        /// </summary>
        private static async Task<(double X, double Y)> HumanMoveAndClickAsync(
            IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
        {
            (double tx, double ty, _) = await HumanMoveToAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);

            // Click kiểu người: nhấn giữ một khoảng ngắn rồi nhả.
            await page.Mouse.DownAsync().ConfigureAwait(false);
            await Task.Delay(rng.Next(40, 121), ct).ConfigureAwait(false);
            await page.Mouse.UpAsync().ConfigureAwait(false);

            return (tx, ty);
        }

        /// <summary>True nếu tại điểm (x,y) của viewport, phần tử nhận sự kiện chính là el / con của el /
        /// tổ tiên của el (elementFromPoint trả node TRÊN CÙNG — bị phần tử khác đè thì trả phần tử đè).</summary>
        private static async Task<bool> IsPointOnElementAsync(IElementHandle el, double x, double y)
        {
            try
            {
                return await el.EvaluateAsync<bool>(
                    "(node, pt) => { const hit = document.elementFromPoint(pt.x, pt.y);" +
                    " return !!hit && (node === hit || node.contains(hit) || hit.contains(node)); }",
                    new { x, y }).ConfigureAwait(false);
            }
            catch { return false; }
        }

        /// <summary>
        /// Primitive click <b>kiểu người CÓ HIT-TEST</b> cho thao tác nghiệp vụ: đưa chuột theo đường cong
        /// tới phần tử (<see cref="HumanMoveToAsync"/>), rồi TRƯỚC KHI nhả click <b>kiểm tra
        /// <c>document.elementFromPoint</c></b> tại điểm click có đúng là phần tử đích (hoặc con/tổ tiên
        /// của nó) — chống <b>click nhầm link khác</b> khi submenu bị cụp hoặc flyout/popover đè lên toạ độ.
        /// Poll hit-test tối đa ~2s với chuột ĐỨNG YÊN tại đích (giống người dừng nhìn rồi mới bấm; popover
        /// hover của item khác tự tắt khi chuột rời item đó). Chỉ <c>Down/trễ/Up</c> khi hit-test PASS. Trả
        /// về (vị trí chuột cuối, đã click hay chưa) — <c>Clicked=false</c> khi không có bounding box hoặc
        /// hit-test fail suốt ~2s (KHÔNG bao giờ click mù vào tọa độ).
        /// </summary>
        private static async Task<(double X, double Y, bool Clicked)> HumanMoveAndClickVerifiedAsync(
            IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
        {
            (double tx, double ty, bool hasBox) =
                await HumanMoveToAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);

            // Không có bounding box → thử kéo vào tầm nhìn + move lại MỘT lần; vẫn không có box → KHÔNG click.
            if (!hasBox)
            {
                try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                (tx, ty, hasBox) = await HumanMoveToAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);
                if (!hasBox)
                {
                    return (mx, my, false);
                }
            }

            // Poll hit-test tối đa ~2s: chuột ĐỨNG YÊN tại đích, dừng ngẫu nhiên rồi kiểm — giống người dừng
            // nhìn rồi mới bấm (popover hover của item khác tự tắt vì chuột không còn trên item đó).
            var deadline = DateTime.UtcNow.AddMilliseconds(2000);
            do
            {
                ct.ThrowIfCancellationRequested();
                if (await IsPointOnElementAsync(el, tx, ty).ConfigureAwait(false))
                {
                    // Hit-test PASS → click kiểu người: nhấn giữ một khoảng ngắn rồi nhả.
                    await page.Mouse.DownAsync().ConfigureAwait(false);
                    await Task.Delay(rng.Next(40, 121), ct).ConfigureAwait(false);
                    await page.Mouse.UpAsync().ConfigureAwait(false);
                    return (tx, ty, true);
                }

                await Task.Delay(rng.Next(150, 301), ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            // Poll fail suốt ~2s → điểm click đang thuộc phần tử khác (bị che/cụp) → KHÔNG Down/Up.
            return (tx, ty, false);
        }

        public async Task<string> CaptureCookiesJsonAsync()
        {
            // Không truyền URL = lấy tất cả cookie trong context.
            var raw = await _context.CookiesAsync().ConfigureAwait(false);

            var list = raw
                .Select(c => new StoredCookie(
                    c.Name,
                    c.Value,
                    c.Domain,
                    c.Path,
                    c.Expires,
                    c.HttpOnly,
                    c.Secure,
                    c.SameSite.ToString()))
                .ToList();

            return CookieJson.Serialize(list);
        }

        // ===================== Danh sách shop (/portal/shop) — mô hình 1 subaccount = nhiều shop =====================

        // Regex nhận entry nút mở shop ("Chi tiết"): chuẩn hóa không dấu rồi khớp. GIỮ nhiều biến thể (vi + en).
        private static readonly Regex ShopDetailRegex =
            new(@"chi tiet|detail", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // JS CHỈ-ĐỌC quét bảng shop: mỗi dòng tr[data-row-key] → {rowKey, name, login}. Bọc từng dòng trong try để
        // một dòng lạ KHÔNG phá cả bảng. Trả JSON.stringify(mảng). Tên đăng nhập = span trong ô td thứ 2 (fallback
        // text của td thứ 2). Selector dùng class-contains để bền khi Shopee thêm hậu tố hash vào tên class.
        private const string ScanShopListJs = @"() => {
    const norm = s => (s || '').replace(/\s+/g, ' ').trim();
    const rows = document.querySelectorAll(""tr[data-row-key]"");
    const out = [];
    for (const row of rows) {
        try {
            const rowKey = row.getAttribute('data-row-key') || '';
            const nameEl = row.querySelector(""span[class*='shop-name-text']"");
            const name = nameEl ? norm(nameEl.textContent) : '';
            let login = '';
            const tds = row.querySelectorAll('td');
            if (tds.length >= 2) {
                const span = tds[1].querySelector('span');
                login = norm(span ? span.textContent : tds[1].textContent);
            }
            out.push({ rowKey: rowKey, name: name, login: login });
        } catch (e) { /* dòng lạ — bỏ qua */ }
    }
    return JSON.stringify(out);
}";

        // Deserialize không phân biệt hoa/thường: khóa JSON rowKey/name/login khớp thuộc tính record.
        private static readonly JsonSerializerOptions ShopRowJsonOpts = new() { PropertyNameCaseInsensitive = true };

        private sealed record RawShopRow(string? RowKey, string? Name, string? Login);

        /// <summary>
        /// HÀM THUẦN (test được): chuyển JSON mảng <c>{rowKey,name,login}</c> (do <see cref="ScanShopListJs"/> đọc từ
        /// DOM) thành <see cref="ShopListItem"/>. Trim mọi trường; BỎ dòng không có <c>rowKey</c> (không định vị được
        /// để mở). Dòng thiếu login vẫn nhận (LoginName rỗng). JSON rỗng/hỏng → danh sách rỗng.
        /// </summary>
        internal static IReadOnlyList<ShopListItem> ParseShopListJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<ShopListItem>();
            }

            List<RawShopRow>? raw;
            try { raw = JsonSerializer.Deserialize<List<RawShopRow>>(json, ShopRowJsonOpts); }
            catch { return Array.Empty<ShopListItem>(); }

            if (raw is null)
            {
                return Array.Empty<ShopListItem>();
            }

            var list = new List<ShopListItem>();
            foreach (var r in raw)
            {
                var id = (r.RowKey ?? string.Empty).Trim();
                if (id.Length == 0)
                {
                    continue; // không có mã shop → không định vị được dòng để mở → bỏ
                }
                list.Add(new ShopListItem(id, (r.Name ?? string.Empty).Trim(), (r.Login ?? string.Empty).Trim()));
            }
            return list;
        }

        /// <summary>Chuỗi chẩn đoán 1 trang (title + url) — nuốt lỗi. Không log dữ liệu nhạy cảm.</summary>
        private static async Task<string> PageDiagAsync(IPage p)
        {
            try { return $"title=[{await p.TitleAsync().ConfigureAwait(false)}], url={p.Url}"; }
            catch { return $"url={p.Url}"; }
        }

        /// <summary>True nếu URL là chính trang danh sách shop (<c>/portal/shop</c>, bỏ query/hash/dấu / cuối). Trang
        /// chi tiết shop có path khác (hoặc sâu hơn) → false = "đã điều hướng khỏi danh sách".</summary>
        private static bool UrlIsShopList(string? u)
        {
            if (string.IsNullOrEmpty(u))
            {
                return false;
            }
            var s = u.Split('?')[0].Split('#')[0].TrimEnd('/');
            return s.EndsWith("/portal/shop", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<IReadOnlyList<ShopListItem>> ReadShopListAsync(Action<string>? log = null, CancellationToken ct = default)
        {
            void L(string m) => log?.Invoke(m);
            try
            {
                // Đọc danh sách trên TAB GỐC (Pages[0]) — đặt lại trang làm việc để mọi thứ về Pages[0].
                SetWorkPage(null);
                var page = _context.Pages.Count > 0 ? _context.Pages[0] : null;
                if (page is null)
                {
                    return Array.Empty<ShopListItem>();
                }

                // Goto bảng shop (nuốt lỗi điều hướng — vẫn thử đọc DOM hiện có).
                try
                {
                    await page.GotoAsync(ShopListUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 30000
                    }).ConfigureAwait(false);
                }
                catch { /* nuốt lỗi điều hướng (timeout/context ngắt) — vẫn thử đọc bảng */ }

                // Chờ bảng render: poll tr[data-row-key] tối đa ~20s.
                var deadline = DateTime.UtcNow.AddSeconds(20);
                bool hasRows = false;
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        hasRows = await page.EvaluateAsync<bool>(
                            "() => document.querySelector(\"tr[data-row-key]\") !== null").ConfigureAwait(false);
                    }
                    catch { hasRows = false; }
                    if (hasRows)
                    {
                        break;
                    }
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }

                if (!hasRows)
                {
                    L("Không thấy bảng shop sau 20s (trang có thể bounce về login). " + await PageDiagAsync(page).ConfigureAwait(false));
                    return Array.Empty<ShopListItem>();
                }

                string json;
                try { json = await page.EvaluateAsync<string>(ScanShopListJs).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    L("Lỗi đọc bảng shop: " + ex.Message);
                    return Array.Empty<ShopListItem>();
                }

                var shops = ParseShopListJson(json);
                L($"Đọc được {shops.Count} shop.");
                return shops;
            }
            catch (OperationCanceledException)
            {
                throw; // hủy chủ động → caller xử như HỦY
            }
            catch
            {
                return Array.Empty<ShopListItem>(); // không bao giờ ném (trừ hủy)
            }
        }

        public async Task<bool> OpenShopDetailAsync(ShopListItem shop, Action<string>? log = null, CancellationToken ct = default)
        {
            void L(string m) => log?.Invoke(m);
            try
            {
                var page = _context.Pages.Count > 0 ? _context.Pages[0] : null; // tab danh sách shop
                if (page is null)
                {
                    return false;
                }

                var rng = new Random();
                var vp = page.ViewportSize;
                double mx = vp is not null ? vp.Width / 2.0 : 640;
                double my = vp is not null ? vp.Height / 2.0 : 360;

                // Định vị nút "Chi tiết" TRONG dòng của shop (re-query TƯƠI — bảng React re-render). Khớp text
                // KHÔNG DẤU ("chi tiet") để bền với NFC/NFD; ưu tiên button link của eds-react, fallback button/a.
                var safeId = shop.ShopId.Replace("'", string.Empty);
                var rowSel = $"tr[data-row-key='{safeId}']";
                var detailSelectors = new[]
                {
                    $"{rowSel} button.eds-react-button--link",
                    $"{rowSel} button",
                    $"{rowSel} a",
                };
                var detailBtn = await FindByNormalizedTextInFramesAsync(
                    page, detailSelectors, new[] { "chi tiet", "detail" }, ct, 8000).ConfigureAwait(false);
                if (detailBtn is null)
                {
                    L($"Không thấy nút 'Chi tiết' của shop {shop.ShopId}. " + await PageDiagAsync(page).ConfigureAwait(false));
                    return false;
                }

                // Hứng TAB MỚI: bắt event _context.Page TRƯỚC click + quét _context.Pages sau click. Nếu mở CÙNG tab
                // (page điều hướng khỏi /portal/shop) → coi tab làm việc là chính page.
                var before = _context.Pages.ToList();
                IPage? popped = null;
                void OnNewPage(object? _, IPage p) => popped ??= p;
                _context.Page += OnNewPage;

                IPage? shopTab = null;
                try
                {
                    (mx, my, _) = await TryHumanClickVisibleAsync(page, detailBtn, mx, my, rng, ct).ConfigureAwait(false);
                    L($"Đã bấm 'Chi tiết' shop {(string.IsNullOrWhiteSpace(shop.LoginName) ? shop.ShopId : shop.LoginName)} — chờ tab shop mở...");

                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline)
                    {
                        ct.ThrowIfCancellationRequested();

                        // (a) tab MỚI (event hoặc quét Pages).
                        var candidate = popped ?? _context.Pages.FirstOrDefault(p => !before.Contains(p));
                        if (candidate is not null)
                        {
                            shopTab = candidate;
                            break;
                        }

                        // (b) CÙNG tab: page đã điều hướng khỏi trang danh sách.
                        if (!UrlIsShopList(page.Url))
                        {
                            shopTab = page;
                            break;
                        }

                        await Task.Delay(400, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _context.Page -= OnNewPage;
                }

                if (shopTab is null)
                {
                    L($"Bấm 'Chi tiết' shop {shop.ShopId} xong chờ 30s chưa thấy tab shop. " + await PageDiagAsync(page).ConfigureAwait(false));
                    return false;
                }

                SetWorkPage(shopTab);

                // Chờ DOMContentLoaded best-effort (đừng để hàm flow đọc trang trắng).
                try
                {
                    await shopTab.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                        new PageWaitForLoadStateOptions { Timeout = 15000 }).ConfigureAwait(false);
                }
                catch { /* trang vẫn dùng được — hàm flow tự poll */ }

                try { await shopTab.BringToFrontAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                return true;
            }
            catch (OperationCanceledException)
            {
                throw; // hủy chủ động → caller xử như HỦY
            }
            catch (Exception ex)
            {
                L($"Lỗi khi mở shop {shop.ShopId}: " + ex.Message);
                return false;
            }
        }

        public async Task CloseShopTabAsync(CancellationToken ct = default)
        {
            try
            {
                var wp = _workPage;
                var root = _context.Pages.Count > 0 ? _context.Pages[0] : null;

                // Tab shop là TAB RIÊNG (khác Pages[0]) → đóng (best-effort, retry ≤3). CÙNG-tab (wp == Pages[0]) →
                // KHÔNG đóng (ReadShopListAsync sẽ Goto lại /portal/shop để về danh sách).
                if (wp is not null && !ReferenceEquals(wp, root) && !wp.IsClosed)
                {
                    for (int attempt = 0; attempt < 3 && !wp.IsClosed; attempt++)
                    {
                        try { await wp.CloseAsync().ConfigureAwait(false); } catch { /* thử lại */ }
                        if (wp.IsClosed)
                        {
                            break;
                        }
                        await Task.Delay(300, ct).ConfigureAwait(false);
                    }
                }

                SetWorkPage(null);

                // Quay lại tab danh sách shop (best-effort).
                try
                {
                    if (root is not null && !root.IsClosed)
                    {
                        await root.BringToFrontAsync().ConfigureAwait(false);
                    }
                }
                catch { /* bỏ qua */ }
            }
            catch (OperationCanceledException)
            {
                throw; // hủy chủ động → caller xử như HỦY
            }
            catch
            {
                SetWorkPage(null); // đảm bảo không giữ tham chiếu tab đã đóng
            }
        }

        // Selector to-do box của Seller Centre (thử theo thứ tự; Shopee CÓ THỂ ĐỔI → luôn có fallback,
        // không thấy thì trả null, KHÔNG ném, KHÔNG phá phiên).
        //   - Chính: duyệt các .to-do-box-item tìm cái có .item-desc == "Chờ Lấy Hàng" → đọc .item-title.
        //   - Fallback theo href: a[href*='type=toship'][href*='to_process'] .item-title.
        private const string ToShipItemSelector = ".to-do-box-item";
        private const string ItemDescSelector = ".item-desc";
        private const string ItemTitleSelector = ".item-title";
        private const string ToShipHrefTitleSelector =
            "a[href*='type=toship'][href*='to_process'] .item-title";
        private const string ToShipDescText = "Chờ Lấy Hàng";

        public async Task<int?> GoHomeAndReadToShipCountAsync(CancellationToken ct = default)
        {
            try
            {
                var page = WorkPage(); // tab shop hiện tại (mô hình nhiều-shop) — KHÔNG cứng Pages[0]
                if (page is null)
                {
                    return null;
                }

                // Gate: CHƯA đăng nhập → KHÔNG điều hướng. Goto lúc này phá form đăng nhập/captcha đang dở
                // y hệt reload (xem ghi chú trong ReadToShipCountAsync), và "gõ nửa chừng rồi nhảy trang"
                // là dấu hiệu máy móc lộ liễu. Trả null ngay — tầng App báo "có thể chưa đăng nhập xong".
                if (!ShopeeLoginCookies.IsLoggedIn(await CaptureCookiesJsonAsync().ConfigureAwait(false)))
                {
                    return null;
                }

                // Random nội bộ (app dùng ngẫu nhiên thật, đồng bộ style các thao tác kiểu người).
                var rng = new Random();

                // Dừng "đọc trang" trước khi về trang chủ (giống người gõ URL / bấm bookmark rồi đọc).
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

                // Về trang chủ Seller: Goto như người gõ URL / bấm bookmark (KHÔNG click máy vào element).
                // Nuốt lỗi điều hướng — vẫn thử đọc số bên dưới.
                try
                {
                    await page.GotoAsync(SellerUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 30000
                    }).ConfigureAwait(false);
                }
                catch
                {
                    // Nuốt lỗi điều hướng (timeout/context ngắt) — vẫn thử đọc to-do box.
                }

                // Dừng "đọc trang" sau khi về trang chủ (để to-do box render).
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

                // Trang vừa load → KHÔNG reload nữa (ReadToShipCountAsync tự gate IsLoggedIn + poll to-do box).
                return await ReadToShipCountAsync(reload: false, ct).ConfigureAwait(false);
            }
            catch
            {
                // Bất kỳ lỗi nào (context ngắt, hủy...) → null, KHÔNG phá phiên.
                return null;
            }
        }

        public async Task<int?> ReadToShipCountAsync(bool reload, CancellationToken ct = default)
        {
            try
            {
                // 1) Gate: chưa đăng nhập → to-do box chưa có → null (KHÔNG reload để không phá đăng nhập/captcha).
                if (!ShopeeLoginCookies.IsLoggedIn(await CaptureCookiesJsonAsync().ConfigureAwait(false)))
                {
                    return null;
                }

                var page = WorkPage(); // tab shop hiện tại (mô hình nhiều-shop) — KHÔNG cứng Pages[0]
                if (page is null)
                {
                    return null;
                }

                // 2) Reload nếu cần (nuốt lỗi điều hướng — vẫn thử đọc DOM hiện có).
                if (reload)
                {
                    try
                    {
                        await page.ReloadAsync(new PageReloadOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 30000
                        }).ConfigureAwait(false);
                    }
                    catch { /* bỏ qua lỗi reload/điều hướng */ }
                }

                // 3) Tìm ô "Chờ Lấy Hàng" (poll timeout ngắn), có fallback.
                var titleText = await FindToShipTitleAsync(page, ct).ConfigureAwait(false);

                // 4) Parse số (thuần, test được). Không thấy / không parse được → null.
                return ShopeeDashboard.ParseToShipCount(titleText);
            }
            catch
            {
                // Bất kỳ lỗi nào (selector đổi, context ngắt...) → null, KHÔNG phá phiên.
                return null;
            }
        }

        /// <summary>
        /// Dò text ô <c>item-title</c> của mục "Chờ Lấy Hàng" trong to-do box, poll tới khi hết
        /// <c>~8s</c>. Thử lần lượt: (1) duyệt các <c>.to-do-box-item</c> tìm cái có <c>.item-desc</c>
        /// khớp "Chờ Lấy Hàng"; (2) fallback theo href. Không thấy → <c>null</c> (không ném).
        /// </summary>
        private static async Task<string?> FindToShipTitleAsync(IPage page, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            do
            {
                ct.ThrowIfCancellationRequested();

                // Cách 1: duyệt .to-do-box-item, khớp .item-desc == "Chờ Lấy Hàng" → đọc .item-title.
                try
                {
                    var items = await page.QuerySelectorAllAsync(ToShipItemSelector).ConfigureAwait(false);
                    foreach (var item in items)
                    {
                        var desc = await item.QuerySelectorAsync(ItemDescSelector).ConfigureAwait(false);
                        if (desc is null)
                        {
                            continue;
                        }

                        var descText = await desc.InnerTextAsync().ConfigureAwait(false);
                        if (!IsToShipDesc(descText))
                        {
                            continue;
                        }

                        var title = await item.QuerySelectorAsync(ItemTitleSelector).ConfigureAwait(false);
                        if (title is not null)
                        {
                            return await title.InnerTextAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch { /* selector chưa render / không hợp lệ — thử fallback */ }

                // Cách 2 (fallback theo href).
                try
                {
                    var title = await page.QuerySelectorAsync(ToShipHrefTitleSelector).ConfigureAwait(false);
                    if (title is not null)
                    {
                        return await title.InnerTextAsync().ConfigureAwait(false);
                    }
                }
                catch { /* bỏ qua */ }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>So khớp text <c>.item-desc</c> với "Chờ Lấy Hàng" (chuẩn hóa khoảng trắng, không phân biệt hoa/thường).</summary>
        private static bool IsToShipDesc(string? descText)
        {
            if (string.IsNullOrWhiteSpace(descText))
            {
                return false;
            }

            var normalized = string.Join(' ',
                descText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return string.Equals(normalized, ToShipDescText, StringComparison.OrdinalIgnoreCase);
        }

        // ===== Điều hướng "Cài Đặt Vận Chuyển" → tab "Địa Chỉ" (KIỂU NGƯỜI) =====
        // URL trực tiếp trang Cài đặt vận chuyển — CHỈ dùng ở fallback cuối (khi Shopee đổi DOM menu khiến
        // không tìm được link để click kiểu người).
        private const string ShippingSettingsUrl = "https://banhang.shopee.vn/portal/all-settings/shipping";

        public async Task<ShippingNavResult> OpenShippingAddressSettingsAsync(CancellationToken ct = default)
        {
            try
            {
                var page = WorkPage(); // tab shop hiện tại (mô hình nhiều-shop) — KHÔNG cứng Pages[0]
                if (page is null)
                {
                    return ShippingNavResult.Failed;
                }

                // Random nội bộ (app dùng ngẫu nhiên thật, đồng bộ style với TryHumanLoginAsync).
                var rng = new Random();

                // Con trỏ bắt đầu ở vị trí NGẪU NHIÊN trong viewport (đọc kích thước thật; null → 1280x720).
                var vp = page.ViewportSize;
                double vw = vp is not null ? vp.Width : 1280;
                double vh = vp is not null ? vp.Height : 720;
                double mx = rng.NextDouble() * vw;
                double my = rng.NextDouble() * vh;

                // Dừng kiểu "người đọc trang" trước khi bắt đầu.
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

                // Click mục cha "Quản Lý Đơn Hàng" TỐI ĐA 1 lần/lượt: click lần 2 khi nhóm đang mở sẽ toggle
                // cụp lại (cấm). Cờ dùng chung cho cả nhánh đọc-trạng-thái lẫn nhánh không-thấy-link.
                bool parentClicked = false;
                bool clickedLink = false;

                // 1) Tìm link "Cài Đặt Vận Chuyển" (poll, deadline ~10s).
                var link = await FindShippingLinkAsync(page, 10000, rng, ct).ConfigureAwait(false);

                if (link is not null)
                {
                    // 2) TRƯỚC khi di chuột: đọc trạng thái bung/cụp bằng JS hình học (KHÔNG cần hover) rồi
                    //    xử lý theo trạng thái. Poll nhẹ ~5s để trạng thái nhất thời (popover hover của item
                    //    khác) tự tan; mỗi vòng chờ 300–800ms ngẫu nhiên.
                    var readyDeadline = DateTime.UtcNow.AddMilliseconds(5000);
                    bool scrolledForUnknown = false;
                    while (!clickedLink && DateTime.UtcNow < readyDeadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        var readiness = await GetLinkReadinessAsync(link).ConfigureAwait(false);

                        if (readiness == LinkReadiness.Ready)
                        {
                            // Link nhận click tại tâm → click CÓ HIT-TEST (hàng rào cuối ngay trước Down/Up).
                            (mx, my, clickedLink) =
                                await HumanMoveAndClickVerifiedAsync(page, link, mx, my, rng, ct).ConfigureAwait(false);
                            break;
                        }

                        if (readiness == LinkReadiness.Collapsed)
                        {
                            // Nhóm "Quản Lý Đơn Hàng" đang CỤP (đúng yêu cầu người dùng: kiểm tra rồi bung).
                            // Đã bung THẬT 1 lần rồi mà vẫn cụp → thôi (không click lại kẻo toggle cụp nhóm đang mở).
                            if (parentClicked)
                            {
                                break;
                            }

                            var parent = await FindOrderMenuParentAsync(page, ct).ConfigureAwait(false);
                            if (parent is null)
                            {
                                break;
                            }

                            // Click mục cha CÓ HIT-TEST. CHỈ tiêu "ngân sách bung 1 lần" (parentClicked) khi chuột
                            // THẬT SỰ nhả (Clicked==true): hit-test fail thì chuột CHƯA HỀ nhả → không có nguy cơ
                            // toggle → vòng sau readiness vẫn Collapsed sẽ thử bung lại (còn trong deadline).
                            bool parentActuallyClicked;
                            (mx, my, parentActuallyClicked) =
                                await HumanMoveAndClickVerifiedAsync(page, parent, mx, my, rng, ct).ConfigureAwait(false);
                            if (parentActuallyClicked)
                            {
                                parentClicked = true;
                                // Bung THÀNH CÔNG → cấp lại trọn 5s cho phần còn lại (chờ, tìm lại link, đọc
                                // readiness, click link kiểu người) — bảo đảm sau khi bung LUÔN có ≥1 lượt đọc
                                // readiness + thử click link trước khi được phép rơi xuống Goto (Goto là đường
                                // thoát HIẾM, không phải kết cục của một lượt bung menu thành công).
                                readyDeadline = DateTime.UtcNow.AddMilliseconds(5000);
                            }

                            await Task.Delay(rng.Next(500, 1500), ct).ConfigureAwait(false);

                            // Tìm lại link (instance có thể đổi sau khi submenu bung) rồi đọc lại ở vòng sau.
                            link = await FindShippingLinkAsync(page, 10000, rng, ct).ConfigureAwait(false);
                            if (link is null)
                            {
                                break;
                            }
                            continue;
                        }

                        if (readiness == LinkReadiness.Unknown)
                        {
                            // Không rõ → thử kéo vào tầm nhìn MỘT lần rồi đọc lại; vẫn Unknown → hết cách bằng chuột.
                            if (scrolledForUnknown)
                            {
                                break;
                            }
                            scrolledForUnknown = true;
                            try { await link.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                            await Task.Delay(rng.Next(300, 801), ct).ConfigureAwait(false);
                            continue;
                        }

                        // Covered (bị popover/flyout trong cùng submenu đè) → chờ rồi đọc lại; KHÔNG click mục cha.
                        await Task.Delay(rng.Next(300, 801), ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    // 4) Không thấy link ngay từ đầu (submenu nhiều khả năng chưa render) → click mục cha
                    //    verified (không cần đọc trạng thái) rồi tìm lại & click. Vẫn giữ giới hạn 1 lần click cha.
                    var parent = await FindOrderMenuParentAsync(page, ct).ConfigureAwait(false);
                    if (parent is not null && !parentClicked)
                    {
                        (mx, my, _) =
                            await HumanMoveAndClickVerifiedAsync(page, parent, mx, my, rng, ct).ConfigureAwait(false);
                        parentClicked = true;
                        await Task.Delay(rng.Next(500, 1500), ct).ConfigureAwait(false);
                        link = await FindShippingLinkAsync(page, 10000, rng, ct).ConfigureAwait(false);
                        if (link is not null)
                        {
                            (mx, my, clickedLink) =
                                await HumanMoveAndClickVerifiedAsync(page, link, mx, my, rng, ct).ConfigureAwait(false);
                        }
                    }
                }

                // 3) Click được link → chờ trang cài đặt vận chuyển mở (CHỈ nhận theo URL).
                bool opened = clickedLink
                    && await WaitShippingPageAsync(page, 20000, ct).ConfigureAwait(false);

                // 4b) Chưa mở được (click không ăn / không thấy link / URL không đổi) → fallback Goto MỘT lần
                //     (đường thoát cuối, kém human hơn — hiếm khi tới nếu hit-test click đã ăn).
                if (!opened)
                {
                    try
                    {
                        await page.GotoAsync(ShippingSettingsUrl, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 30000
                        }).ConfigureAwait(false);
                    }
                    catch { /* nuốt lỗi điều hướng — vẫn thử chờ trang bên dưới */ }

                    opened = await WaitShippingPageAsync(page, 20000, ct).ConfigureAwait(false);
                }

                if (!opened)
                {
                    return ShippingNavResult.PageNotOpened;
                }

                // 5) Dừng "đọc trang" rồi tìm & bấm tab "Địa Chỉ".
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

                var tab = await FindAddressTabAsync(page, 10000, rng, ct).ConfigureAwait(false);
                if (tab is null)
                {
                    return ShippingNavResult.AddressTabNotFound;
                }

                // Tab đã active → coi như xong (không click lại). Chưa active → click CÓ HIT-TEST.
                if (IsTabActive(await tab.GetAttributeAsync("class").ConfigureAwait(false)))
                {
                    return ShippingNavResult.Ok;
                }

                bool clickedTab;
                (mx, my, clickedTab) =
                    await HumanMoveAndClickVerifiedAsync(page, tab, mx, my, rng, ct).ConfigureAwait(false);
                return clickedTab ? ShippingNavResult.Ok : ShippingNavResult.AddressTabNotFound;
            }
            catch
            {
                // Bất kỳ lỗi nào (selector đổi, context ngắt, hủy...) → Failed, KHÔNG phá phiên.
                return ShippingNavResult.Failed;
            }
        }

        /// <summary>
        /// Dò link "Cài Đặt Vận Chuyển" trong menu trái, poll tới khi hết <paramref name="timeoutMs"/>.
        /// Thử theo thứ tự: (a) <c>a.sidebar-submenu-item-link[href*='/portal/all-settings/shipping']</c>;
        /// (b) <c>a[test-id='order shipping setting']</c>; (c) duyệt mọi <c>a.sidebar-submenu-item-link</c>
        /// khớp <see cref="ShopeeShippingNav.IsShippingSettingText"/>. Chỉ nhận element đang HIỂN THỊ
        /// (<c>BoundingBoxAsync() != null</c>). Không thấy → <c>null</c> (không ném).
        /// </summary>
        private static async Task<IElementHandle?> FindShippingLinkAsync(
            IPage page, int timeoutMs, Random rng, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();

                var el = await FirstVisibleByBoxAsync(
                             page, "a.sidebar-submenu-item-link[href*='/portal/all-settings/shipping']", ct)
                         .ConfigureAwait(false)
                         ?? await FirstVisibleByBoxAsync(page, "a[test-id='order shipping setting']", ct)
                             .ConfigureAwait(false);
                if (el is not null)
                {
                    return el;
                }

                // Fallback theo text: duyệt mọi link submenu, khớp "Cài Đặt Vận Chuyển".
                try
                {
                    var links = await page.QuerySelectorAllAsync("a.sidebar-submenu-item-link").ConfigureAwait(false);
                    foreach (var a in links)
                    {
                        var text = await a.InnerTextAsync().ConfigureAwait(false);
                        if (ShopeeShippingNav.IsShippingSettingText(text)
                            && await a.BoundingBoxAsync().ConfigureAwait(false) is not null)
                        {
                            return a;
                        }
                    }
                }
                catch { /* selector chưa render / không hợp lệ — thử vòng sau */ }

                await Task.Delay(rng.Next(300, 501), ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>
        /// Đọc <b>trạng thái bung/cụp</b> của link trong submenu bằng <b>JS hình học — KHÔNG cần di chuột</b>
        /// (elementFromPoint là hình học thuần, không cần hover). DOM Shopee không có class trạng thái trên
        /// <c>li.sidebar-menu-box</c> nên phải suy từ chiều cao <c>ul.sidebar-submenu</c> + phần tử nhận
        /// click tại tâm link. Nuốt lỗi → <see cref="LinkReadiness.Unknown"/>. Cho kết quả qua
        /// <see cref="ShopeeShippingNav.ParseLinkReadiness"/>.
        /// </summary>
        private static async Task<LinkReadiness> GetLinkReadinessAsync(IElementHandle link)
        {
            string raw;
            try
            {
                raw = await link.EvaluateAsync<string>(
                    "(node) => {" +
                    " const ul = node.closest('ul.sidebar-submenu');" +
                    " const ulRect = ul ? ul.getBoundingClientRect() : null;" +
                    " if (ulRect && ulRect.height < 2) return 'collapsed';" +
                    " const r = node.getBoundingClientRect();" +
                    " if (r.width === 0 || r.height === 0) return 'collapsed';" +
                    " const cx = r.left + r.width / 2, cy = r.top + r.height / 2;" +
                    " const hit = document.elementFromPoint(cx, cy);" +
                    " if (!hit) return 'covered';" +
                    " if (node === hit || node.contains(hit) || hit.contains(node)) return 'ready';" +
                    " return ul && ul.contains(hit) ? 'covered' : 'collapsed';" +
                    "}").ConfigureAwait(false);
            }
            catch { raw = "unknown"; }

            return ShopeeShippingNav.ParseLinkReadiness(raw);
        }

        /// <summary>
        /// Dò mục cha "Quản Lý Đơn Hàng" ở menu trái (để click mở submenu). Thử: (a)
        /// <c>li.ps_menu_order div.sidebar-menu-item</c>; (b) fallback duyệt mọi <c>.sidebar-menu-item</c>
        /// khớp <see cref="ShopeeShippingNav.IsOrderMenuText"/>. Chỉ nhận element đang hiển thị. Một lượt
        /// (không poll) — không thấy → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> FindOrderMenuParentAsync(IPage page, CancellationToken ct)
        {
            var el = await FirstVisibleByBoxAsync(page, "li.ps_menu_order div.sidebar-menu-item", ct)
                .ConfigureAwait(false);
            if (el is not null)
            {
                return el;
            }

            try
            {
                var items = await page.QuerySelectorAllAsync(".sidebar-menu-item").ConfigureAwait(false);
                foreach (var item in items)
                {
                    var text = await item.InnerTextAsync().ConfigureAwait(false);
                    if (ShopeeShippingNav.IsOrderMenuText(text)
                        && await item.BoundingBoxAsync().ConfigureAwait(false) is not null)
                    {
                        return item;
                    }
                }
            }
            catch { /* bỏ qua */ }

            return null;
        }

        /// <summary>
        /// Chờ trang Cài đặt vận chuyển mở: poll tới khi hết <paramref name="timeoutMs"/>, điều kiện DUY NHẤT
        /// là <c>page.Url</c> chứa <c>/portal/all-settings/shipping</c>
        /// (<see cref="ShopeeShippingNav.IsShippingSettingHref"/>). <b>KHÔNG</b> nhận theo
        /// <c>.eds-tabs__nav-tab</c> nữa: trang khác cũng có thanh eds-tabs → dương tính giả (nhận nhầm là đã
        /// mở rồi fail muộn ở bước tìm tab "Địa Chỉ"). <c>page.Url</c> của Playwright phản ánh cả đổi route
        /// SPA qua history API nên đủ tin (KHÔNG dùng WaitForNavigation). Hết giờ → false.
        /// </summary>
        private static async Task<bool> WaitShippingPageAsync(IPage page, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (ShopeeShippingNav.IsShippingSettingHref(page.Url))
                    {
                        return true;
                    }
                }
                catch { /* điều hướng dở — thử vòng sau */ }

                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return false;
        }

        /// <summary>
        /// Dò tab "Địa Chỉ" trong thanh <c>.eds-tabs__nav-tab</c>, poll tới khi hết
        /// <paramref name="timeoutMs"/>, khớp <see cref="ShopeeShippingNav.IsAddressTabText"/> (InnerText
        /// có thể kèm rác badge). Không thấy → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> FindAddressTabAsync(
            IPage page, int timeoutMs, Random rng, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var tabs = await page.QuerySelectorAllAsync(".eds-tabs__nav-tab").ConfigureAwait(false);
                    foreach (var t in tabs)
                    {
                        var text = await t.InnerTextAsync().ConfigureAwait(false);
                        if (ShopeeShippingNav.IsAddressTabText(text))
                        {
                            return t;
                        }
                    }
                }
                catch { /* chưa render — thử vòng sau */ }

                await Task.Delay(rng.Next(300, 501), ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>True nếu chuỗi class của tab chứa token "active" (tab đang được chọn).</summary>
        private static bool IsTabActive(string? classAttr)
        {
            if (string.IsNullOrWhiteSpace(classAttr))
            {
                return false;
            }

            foreach (var c in classAttr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(c, "active", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Dò phần tử đầu tiên khớp <paramref name="selector"/> đang HIỂN THỊ
        /// (<c>BoundingBoxAsync() != null</c>). Một lượt, nuốt lỗi selector → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> FirstVisibleByBoxAsync(IPage page, string selector, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var el = await page.QuerySelectorAsync(selector).ConfigureAwait(false);
                if (el is not null && await el.BoundingBoxAsync().ConfigureAwait(false) is not null)
                {
                    return el;
                }
            }
            catch { /* selector không hợp lệ / chưa render */ }

            return null;
        }

        // ===== Bước 2: đặt "địa chỉ lấy hàng" theo tỉnh mặc định (KIỂU NGƯỜI) =====

        public async Task<SetPickupResult> SetPickupAddressAsync(string province, CancellationToken ct = default)
        {
            try
            {
                var page = WorkPage(); // tab shop hiện tại (mô hình nhiều-shop) — KHÔNG cứng Pages[0]
                if (page is null)
                {
                    return SetPickupResult.Failed;
                }

                // Random nội bộ + con trỏ bắt đầu ở vị trí ngẫu nhiên (đồng bộ style các thao tác kiểu người).
                var rng = new Random();
                var vp = page.ViewportSize;
                double vw = vp is not null ? vp.Width : 1280;
                double vh = vp is not null ? vp.Height : 720;
                double mx = rng.NextDouble() * vw;
                double my = rng.NextDouble() * vh;

                // Dừng "đọc trang" trước khi bắt đầu.
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

                // 1) Chờ danh sách địa chỉ & tìm địa chỉ khớp tỉnh (đầu tiên theo thứ tự trang).
                var item = await FindMatchingAddressItemAsync(page, province, 15000, ct).ConfigureAwait(false);
                if (item is null)
                {
                    return SetPickupResult.AddressNotFound;
                }

                // 2) Đã là địa chỉ lấy hàng (có tag) → coi như xong, KHÔNG đụng gì.
                if (await ItemHasPickupTagAsync(item).ConfigureAwait(false))
                {
                    return SetPickupResult.Ok;
                }

                // 3) Địa chỉ khớp tỉnh CHƯA là địa chỉ lấy hàng → mở "Sửa" → tick → "Lưu" (khối dùng chung
                //    với SetPickupAddressToOtherAsync; KHÔNG phụ thuộc tỉnh nên tách ra tái dùng).
                return await OpenEditAndSetItemAsPickupAsync(page, item, mx, my, rng, ct).ConfigureAwait(false);
            }
            catch
            {
                // Bất kỳ lỗi nào (selector đổi, context ngắt, hủy...) → Failed, KHÔNG phá phiên. Modal (nếu
                // còn mở) đã được finally trên Hủy trước khi exception rơi tới đây.
                return SetPickupResult.Failed;
            }
        }

        /// <summary>
        /// <b>Khối dùng chung "mở Sửa → tick → Lưu"</b> cho một địa chỉ <paramref name="item"/> đã xác định
        /// (KHÔNG phụ thuộc tỉnh). Tách NGUYÊN VĂN từ <see cref="SetPickupAddressAsync"/> để
        /// <see cref="SetPickupAddressToOtherAsync"/> tái dùng: bấm "Sửa" → chờ modal → tick "Đặt làm địa chỉ
        /// lấy hàng" (re-query TƯƠI, đọc DOM sống vì modal có Google Map load ngầm) → "Lưu" → chờ hoàn tất; mọi
        /// nhánh thất bại (kể cả exception) đều Hủy modal (finally). <b>KHÔNG bọc try/catch ngoài cùng</b> —
        /// exception rò lên caller (giữ đúng hành vi khi khối này còn nằm trong SetPickupAddressAsync).
        /// </summary>
        private async Task<SetPickupResult> OpenEditAndSetItemAsPickupAsync(
            IPage page, IElementHandle item, double mx, double my, Random rng, CancellationToken ct)
        {
            // 3) Tìm & bấm nút "Sửa" của địa chỉ đó (chỉ click khi có bounding box). Không thấy nút /
            //    click không ăn → coi như không mở được modal sửa.
            var editBtn = await FindEditButtonAsync(item).ConfigureAwait(false);
            if (editBtn is null)
            {
                return SetPickupResult.EditModalNotOpened;
            }

            bool clicked;
            (mx, my, clicked) = await TryHumanClickVisibleAsync(page, editBtn, mx, my, rng, ct).ConfigureAwait(false);
            if (!clicked)
            {
                return SetPickupResult.EditModalNotOpened;
            }

            // 4) Chờ modal "Sửa Địa chỉ" mở (shop bị khóa sửa → không mở). Dừng "đọc modal".
            var modal = await WaitEditAddressModalAsync(page, 10000, ct).ConfigureAwait(false);
            if (modal is null)
            {
                return SetPickupResult.EditModalNotOpened;
            }
            await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

            // 5) Thao tác checkbox "Đặt làm địa chỉ lấy hàng". Modal chứa Google Map load bất đồng bộ
            //    → Vue vẽ lại form nên checkbox re-query TƯƠI trước mỗi lần dùng, trạng thái tick đọc
            //    bằng DOM sống (KHÔNG giữ handle qua re-render). Bọc try/finally: kết quả KHÁC Ok mà
            //    modal còn mở → LUÔN Hủy (chốt chặn cuối, kể cả khi có exception rơi lên catch ngoài).
            var result = SetPickupResult.Failed;
            try
            {
                // 5a) Chờ ổn định: thấy label checkbox HAI LẦN LIÊN TIẾP (~400ms) để map/form re-render
                //     xong mới thao tác; deadline ~8s. Không thấy → không có ô cần tick.
                if (!await WaitPickupCheckboxStableAsync(modal, 8000, ct).ConfigureAwait(false))
                {
                    result = SetPickupResult.CheckboxNotFound;
                    return result;
                }

                // 5b) Đã tick sẵn (đọc DOM sống) → trạng thái mong muốn đã có → Hủy (không đổi gì), Ok.
                if (await IsPickupCheckedAsync(modal).ConfigureAwait(false) == true)
                {
                    (mx, my) = await HumanCancelModalAsync(page, modal, mx, my, rng, ct).ConfigureAwait(false);
                    result = SetPickupResult.Ok;
                    return result;
                }

                // 5c) Vòng tick tối đa 3 lần: re-query text span TƯƠI → click kiểu người CÓ HIT-TEST →
                //     chờ → đọc lại trạng thái bằng DOM sống. true → thoát vòng thành công.
                bool ticked = false;
                for (int attempt = 0; attempt < 3 && !ticked; attempt++)
                {
                    var span = await FindPickupClickTargetAsync(modal).ConfigureAwait(false);
                    if (span is not null)
                    {
                        (mx, my, _) = await TryHumanClickVisibleAsync(page, span, mx, my, rng, ct).ConfigureAwait(false);
                    }
                    await Task.Delay(rng.Next(300, 900), ct).ConfigureAwait(false);
                    if (await IsPickupCheckedAsync(modal).ConfigureAwait(false) == true)
                    {
                        ticked = true;
                    }
                }

                if (!ticked)
                {
                    result = SetPickupResult.CheckboxClickFailed;
                    return result;
                }

                // 6) Bấm "Lưu" (GHI lên shop thật). Không tìm / không click được → SaveFailed.
                var saveBtn = await FindSaveButtonAsync(modal).ConfigureAwait(false);
                if (saveBtn is null)
                {
                    result = SetPickupResult.SaveFailed;
                    return result;
                }

                (mx, my, clicked) = await TryHumanClickVisibleAsync(page, saveBtn, mx, my, rng, ct).ConfigureAwait(false);
                if (!clicked)
                {
                    result = SetPickupResult.SaveFailed;
                    return result;
                }

                // 7) Chờ Lưu hoàn tất. Sau khi Lưu, Shopee CÓ THỂ (không chắc) bật thêm hộp xác nhận đổi
                //    địa chỉ lấy hàng — nếu có thì bấm "Đồng ý" kiểu người rồi chờ modal Sửa đóng.
                //    Hoàn tất → Ok; hết giờ (lỗi form/shop khóa / chưa chốt được) → SaveFailed.
                result = await WaitPickupSaveCompletedAsync(page, 15000, mx, my, rng, ct).ConfigureAwait(false)
                    ? SetPickupResult.Ok
                    : SetPickupResult.SaveFailed;
                return result;
            }
            finally
            {
                // Chốt chặn: mọi nhánh thất bại (kể cả exception) mà modal còn mở → Hủy, KHÔNG để modal
                // "Sửa Địa chỉ" mở treo. Re-find modal TƯƠI (handle cũ có thể đã stale). Best-effort.
                if (result != SetPickupResult.Ok)
                {
                    try
                    {
                        var openModal = await FindEditAddressModalAsync(page).ConfigureAwait(false);
                        if (openModal is not null)
                        {
                            await HumanCancelModalAsync(page, openModal, mx, my, rng, ct).ConfigureAwait(false);
                        }
                    }
                    catch { /* best-effort — nuốt lỗi (context ngắt / hủy) */ }
                }
            }
        }

        // ===== Bước 4: sau khi xử lý hết đơn, đặt "địa chỉ lấy hàng" về MỘT ĐỊA CHỈ KHÁC (KIỂU NGƯỜI) =====

        public async Task<SetPickupResult> SetPickupAddressToOtherAsync(CancellationToken ct = default)
        {
            try
            {
                var page = WorkPage(); // tab shop hiện tại (mô hình nhiều-shop) — KHÔNG cứng Pages[0]
                if (page is null)
                {
                    return SetPickupResult.Failed;
                }

                // Random nội bộ + con trỏ bắt đầu ở vị trí ngẫu nhiên (đồng bộ style các thao tác kiểu người).
                var rng = new Random();
                var vp = page.ViewportSize;
                double vw = vp is not null ? vp.Width : 1280;
                double vh = vp is not null ? vp.Height : 720;
                double mx = rng.NextDouble() * vw;
                double my = rng.NextDouble() * vh;

                // Dừng "đọc trang" trước khi bắt đầu.
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

                // 1) Chờ danh sách địa chỉ & tìm địa chỉ ĐẦU TIÊN chắc chắn KHÔNG mang tag "Địa chỉ lấy hàng".
                var item = await FindFirstNonPickupAddressItemAsync(page, 15000, ct).ConfigureAwait(false);
                if (item is null)
                {
                    // Danh sách rỗng / shop chỉ có 1 địa chỉ / mọi địa chỉ đang là pickup → không có "địa chỉ khác".
                    return SetPickupResult.AddressNotFound;
                }

                // 2) Mở "Sửa" → tick → "Lưu" (khối dùng chung với SetPickupAddressAsync).
                return await OpenEditAndSetItemAsPickupAsync(page, item, mx, my, rng, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // hủy chủ động → ném XUYÊN để caller (ProcessOrdersAsync) dừng sạch
            }
            catch
            {
                // Bất kỳ lỗi nào khác (selector đổi, context ngắt...) → Failed, KHÔNG phá phiên. Modal (nếu còn
                // mở) đã được finally trong OpenEditAndSetItemAsPickupAsync Hủy trước khi exception rơi tới đây.
                return SetPickupResult.Failed;
            }
        }

        /// <summary>
        /// Dò địa chỉ (<c>.address-list .address-item-container</c>) ĐẦU TIÊN chắc chắn KHÔNG mang tag "Địa
        /// chỉ lấy hàng" (dùng cho "đặt về địa chỉ khác"), poll tới khi hết <paramref name="timeoutMs"/>
        /// (danh sách render dần), re-query TƯƠI mỗi lượt. Item đọc tag LỖI (<c>null</c>) bị BỎ QUA — thận
        /// trọng, tránh chọn bừa lúc DOM chưa render. Không có item không-tag (shop 1 địa chỉ / mọi địa chỉ
        /// đang là pickup) → <c>null</c> (không ném).
        /// </summary>
        private static async Task<IElementHandle?> FindFirstNonPickupAddressItemAsync(
            IPage page, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var items = await page.QuerySelectorAllAsync(".address-list .address-item-container")
                        .ConfigureAwait(false);
                    foreach (var it in items)
                    {
                        // CHỈ chọn khi đọc tag KHÔNG ném VÀ chắc chắn không có tag (== false). null (đọc lỗi)
                        // hoặc true (đang là pickup) → BỎ QUA.
                        if (await TryReadItemHasPickupTagAsync(it).ConfigureAwait(false) == false)
                        {
                            return it; // địa chỉ KHÔNG mang tag ĐẦU TIÊN theo thứ tự trang
                        }
                    }
                }
                catch { /* chưa render / selector không hợp lệ — thử vòng sau */ }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>
        /// Đọc tag "Địa chỉ lấy hàng" của một địa chỉ, PHÂN BIỆT lỗi đọc (khác <see cref="ItemHasPickupTagAsync"/>
        /// vốn nuốt lỗi thành <c>false</c>): <c>true</c> = chắc chắn CÓ tag; <c>false</c> = đọc được DOM và chắc
        /// chắn KHÔNG có tag; <c>null</c> = đọc lỗi/ném (DOM chưa render / handle stale) → caller KHÔNG được coi
        /// là "không tag".
        /// </summary>
        private static async Task<bool?> TryReadItemHasPickupTagAsync(IElementHandle item)
        {
            try
            {
                var tags = await item.QuerySelectorAllAsync(".address-label").ConfigureAwait(false);
                foreach (var tag in tags)
                {
                    if (ShopeeShippingNav.IsPickupTagText(await tag.InnerTextAsync().ConfigureAwait(false)))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return null; // đọc lỗi — KHÔNG kết luận "không tag"
            }
        }

        /// <summary>
        /// Dò địa chỉ (<c>.address-list .address-item-container</c>) đầu tiên có ô "Địa chỉ" khớp
        /// <paramref name="province"/>, poll tới khi hết <paramref name="timeoutMs"/> (danh sách có thể
        /// render dần). Không có item khớp → <c>null</c> (không ném).
        /// </summary>
        private static async Task<IElementHandle?> FindMatchingAddressItemAsync(
            IPage page, string province, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var items = await page.QuerySelectorAllAsync(".address-list .address-item-container")
                        .ConfigureAwait(false);
                    foreach (var it in items)
                    {
                        var detail = await ReadAddressDetailAsync(it).ConfigureAwait(false);
                        if (ShopeeShippingNav.AddressDetailMatchesProvince(detail, province))
                        {
                            return it; // địa chỉ khớp ĐẦU TIÊN theo thứ tự trang
                        }
                    }
                }
                catch { /* chưa render / selector không hợp lệ — thử vòng sau */ }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>
        /// Đọc InnerText của ô <c>.detail</c> thuộc hàng "Địa chỉ" trong một địa chỉ: duyệt các
        /// <c>div.grid</c>, lấy grid có <c>span.label</c> chuẩn hóa == "địa chỉ" (KHÔNG lấy nhầm
        /// <c>.detail</c> của hàng "Số điện thoại"). Không thấy → <c>null</c>.
        /// </summary>
        private static async Task<string?> ReadAddressDetailAsync(IElementHandle item)
        {
            try
            {
                var grids = await item.QuerySelectorAllAsync("div.grid").ConfigureAwait(false);
                foreach (var grid in grids)
                {
                    var label = await grid.QuerySelectorAsync("span.label").ConfigureAwait(false);
                    if (label is null)
                    {
                        continue;
                    }

                    var labelText = ShopeeShippingNav.NormalizeUiText(
                        await label.InnerTextAsync().ConfigureAwait(false));
                    if (labelText != "địa chỉ")
                    {
                        continue;
                    }

                    var detail = await grid.QuerySelectorAsync(".detail").ConfigureAwait(false);
                    if (detail is not null)
                    {
                        return await detail.InnerTextAsync().ConfigureAwait(false);
                    }
                }
            }
            catch { /* bỏ qua */ }

            return null;
        }

        /// <summary>True nếu địa chỉ có tag "Địa chỉ lấy hàng" (đang là địa chỉ lấy hàng của shop).</summary>
        private static async Task<bool> ItemHasPickupTagAsync(IElementHandle item)
        {
            try
            {
                var tags = await item.QuerySelectorAllAsync(".address-label").ConfigureAwait(false);
                foreach (var tag in tags)
                {
                    if (ShopeeShippingNav.IsPickupTagText(await tag.InnerTextAsync().ConfigureAwait(false)))
                    {
                        return true;
                    }
                }
            }
            catch { /* bỏ qua */ }

            return false;
        }

        /// <summary>
        /// Dò nút "Sửa" trong một địa chỉ: ưu tiên các <c>button</c> trong <c>.operations</c>, fallback mọi
        /// <c>button</c> trong item; khớp <see cref="ShopeeShippingNav.IsEditButtonText"/>. Không thấy → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> FindEditButtonAsync(IElementHandle item)
        {
            try
            {
                var ops = await item.QuerySelectorAsync(".operations").ConfigureAwait(false);
                if (ops is not null)
                {
                    var found = await FindButtonByTextAsync(ops, ShopeeShippingNav.IsEditButtonText).ConfigureAwait(false);
                    if (found is not null)
                    {
                        return found;
                    }
                }

                return await FindButtonByTextAsync(item, ShopeeShippingNav.IsEditButtonText).ConfigureAwait(false);
            }
            catch { /* bỏ qua */ }

            return null;
        }

        /// <summary>
        /// Dò nút "Lưu" trong footer modal: ưu tiên <c>button.eds-button--primary</c> trong
        /// <c>.eds-modal__footer</c>, fallback button có text khớp <see cref="ShopeeShippingNav.IsSaveButtonText"/>.
        /// </summary>
        private static async Task<IElementHandle?> FindSaveButtonAsync(IElementHandle modal)
        {
            try
            {
                var footer = await modal.QuerySelectorAsync(".eds-modal__footer").ConfigureAwait(false);
                var scope = footer ?? modal;

                var primary = await scope.QuerySelectorAsync("button.eds-button--primary").ConfigureAwait(false);
                if (primary is not null)
                {
                    return primary;
                }

                return await FindButtonByTextAsync(scope, ShopeeShippingNav.IsSaveButtonText).ConfigureAwait(false);
            }
            catch { /* bỏ qua */ }

            return null;
        }

        /// <summary>Dò nút "Hủy" trong footer modal (fallback: toàn modal), khớp
        /// <see cref="ShopeeShippingNav.IsCancelButtonText"/>. Không thấy → <c>null</c>.</summary>
        private static async Task<IElementHandle?> FindCancelButtonAsync(IElementHandle modal)
        {
            try
            {
                var footer = await modal.QuerySelectorAsync(".eds-modal__footer").ConfigureAwait(false);
                var scope = footer ?? modal;
                return await FindButtonByTextAsync(scope, ShopeeShippingNav.IsCancelButtonText).ConfigureAwait(false);
            }
            catch { /* bỏ qua */ }

            return null;
        }

        /// <summary>Duyệt mọi <c>button</c> trong <paramref name="scope"/>, trả cái đầu tiên có InnerText
        /// khớp <paramref name="match"/>. Không thấy → <c>null</c>.</summary>
        private static async Task<IElementHandle?> FindButtonByTextAsync(
            IElementHandle scope, Func<string?, bool> match)
        {
            var buttons = await scope.QuerySelectorAllAsync("button").ConfigureAwait(false);
            foreach (var b in buttons)
            {
                if (match(await b.InnerTextAsync().ConfigureAwait(false)))
                {
                    return b;
                }
            }

            return null;
        }

        /// <summary>
        /// Trong modal, dò <b>label</b> (<c>label.eds-checkbox</c>) của checkbox "Đặt làm địa chỉ lấy
        /// hàng": duyệt các label, khớp <c>span.eds-checkbox__label</c> qua
        /// <see cref="ShopeeShippingNav.IsSetPickupCheckboxText"/>. Query <b>TƯƠI mỗi lần gọi</b> — KHÔNG
        /// giữ handle qua re-render form (map load bất đồng bộ khiến Vue vẽ lại). Không thấy / lỗi (label
        /// detached / chưa render) → <c>null</c> (không ném).
        /// </summary>
        private static async Task<IElementHandle?> FindPickupCheckboxLabelAsync(IElementHandle modal)
        {
            try
            {
                var labels = await modal.QuerySelectorAllAsync("label.eds-checkbox").ConfigureAwait(false);
                foreach (var label in labels)
                {
                    var span = await label.QuerySelectorAsync("span.eds-checkbox__label").ConfigureAwait(false);
                    if (span is null)
                    {
                        continue;
                    }

                    if (ShopeeShippingNav.IsSetPickupCheckboxText(await span.InnerTextAsync().ConfigureAwait(false)))
                    {
                        return label;
                    }
                }
            }
            catch { /* modal/label detached hoặc chưa render — coi như chưa thấy */ }

            return null;
        }

        /// <summary>
        /// Đọc "đã tick chưa" của checkbox "Đặt làm địa chỉ lấy hàng" bằng <b>DOM sống</b>: re-query label
        /// TƯƠI (<see cref="FindPickupCheckboxLabelAsync"/>) rồi eval trạng thái <c>checked</c> của
        /// <c>input.eds-checkbox__input</c> bên trong — KHÔNG giữ handle <c>input</c> qua re-render (Vue vẽ
        /// lại input). <c>true</c>/<c>false</c> theo DOM; <c>null</c> khi không đọc được (label detached /
        /// chưa render).
        /// </summary>
        private static async Task<bool?> IsPickupCheckedAsync(IElementHandle modal)
        {
            var label = await FindPickupCheckboxLabelAsync(modal).ConfigureAwait(false);
            if (label is null)
            {
                return null;
            }

            try
            {
                return await label.EvaluateAsync<bool>(
                    "l => l.querySelector('input.eds-checkbox__input')?.checked === true").ConfigureAwait(false);
            }
            catch { return null; }
        }

        /// <summary>
        /// Trong modal, dò <b>mục tiêu click</b> để tick: text span <c>span.eds-checkbox__label</c> của đúng
        /// ô "Đặt làm địa chỉ lấy hàng" (mục tiêu lớn, rõ, hit-test sạch hơn cả thẻ label có <c>input</c>
        /// <c>opacity:0</c> phủ lên). Query <b>TƯƠI mỗi lần gọi</b>. Không thấy / lỗi → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> FindPickupClickTargetAsync(IElementHandle modal)
        {
            var label = await FindPickupCheckboxLabelAsync(modal).ConfigureAwait(false);
            if (label is null)
            {
                return null;
            }

            try
            {
                return await label.QuerySelectorAsync("span.eds-checkbox__label").ConfigureAwait(false);
            }
            catch { return null; }
        }

        /// <summary>
        /// Chờ ô checkbox "Đặt làm địa chỉ lấy hàng" <b>ổn định</b>: modal chứa Google Map load bất đồng bộ
        /// → Vue vẽ lại form; poll <see cref="FindPickupCheckboxLabelAsync"/> tới khi thấy label <b>HAI LẦN
        /// LIÊN TIẾP</b> (cách ~400ms) — để form re-render xong mới thao tác — hoặc hết
        /// <paramref name="timeoutMs"/>. Ổn định → <c>true</c>; hết giờ mà chưa từng thấy 2 lần liên tiếp →
        /// <c>false</c>.
        /// </summary>
        private static async Task<bool> WaitPickupCheckboxStableAsync(
            IElementHandle modal, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            bool seenPrev = false;
            do
            {
                ct.ThrowIfCancellationRequested();
                var seen = await FindPickupCheckboxLabelAsync(modal).ConfigureAwait(false) is not null;
                if (seen && seenPrev)
                {
                    return true;
                }
                seenPrev = seen;
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return false;
        }

        /// <summary>
        /// Chờ modal "Sửa Địa chỉ" xuất hiện (<c>.eds-modal__box</c> có <c>.title</c> khớp
        /// <see cref="ShopeeShippingNav.IsEditAddressModalTitle"/>), poll tới hết <paramref name="timeoutMs"/>.
        /// Không hiện → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> WaitEditAddressModalAsync(IPage page, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                var modal = await FindEditAddressModalAsync(page).ConfigureAwait(false);
                if (modal is not null)
                {
                    return modal;
                }

                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>
        /// Chờ modal "Sửa Địa chỉ" <b>biến mất</b> (bấm Lưu xong), poll tới hết <paramref name="timeoutMs"/>.
        /// Đóng → <c>true</c>; hết giờ (vẫn còn) → <c>false</c>.
        /// </summary>
        private static async Task<bool> WaitEditAddressModalClosedAsync(IPage page, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                if (await FindEditAddressModalAsync(page).ConfigureAwait(false) is null)
                {
                    return true;
                }

                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return false;
        }

        /// <summary>
        /// Chờ thao tác <b>Lưu địa chỉ lấy hàng</b> hoàn tất, xử lý hộp xác nhận (nếu có). Sau khi bấm Lưu,
        /// Shopee CÓ THỂ (không phải lúc nào cũng) bật thêm hộp xác nhận đổi địa chỉ lấy hàng có nút "Đồng
        /// ý" — khi đó modal "Sửa Địa chỉ" KHÔNG đóng cho tới khi bấm "Đồng ý". Poll tới hết
        /// <paramref name="timeoutMs"/> (mỗi vòng ~300ms):
        /// <list type="number">
        /// <item>Ưu tiên: hộp xác nhận hiện → bấm "Đồng ý" <b>kiểu người (verified)</b>
        /// (<see cref="FindConfirmChangePickupButtonAsync"/> + <see cref="TryHumanClickVisibleAsync"/>),
        /// rồi kiểm lại vòng sau. Chỉ bấm MỘT lần (cờ <c>confirmDone</c>).</item>
        /// <item>Modal "Sửa Địa chỉ" đã đóng: nếu đã bấm "Đồng ý" → xong. Nếu CHƯA bấm mà modal biến mất →
        /// chờ <b>ân hạn ~1.2s</b> xem hộp xác nhận có hiện muộn không (Shopee có thể THAY modal Sửa bằng hộp
        /// xác nhận với khe render) — tránh báo Ok GIẢ; có hộp → quay lại bấm "Đồng ý", không → Lưu thẳng, xong.</item>
        /// </list>
        /// Thứ tự QUAN TRỌNG: bấm "Đồng ý" TRƯỚC khi coi "modal đóng = xong" — tránh trả <c>true</c> sớm khi
        /// hộp xác nhận còn treo/hiện muộn (chưa thực sự chốt đổi địa chỉ). Hết giờ → <c>false</c>. Hủy cắt được
        /// mỗi vòng (<see cref="CancellationToken.ThrowIfCancellationRequested"/> + <c>Task.Delay(ct)</c>).
        /// <paramref name="mx"/>/<paramref name="my"/> chỉ dùng nội bộ (bước cuối, không cần trả ra).
        /// </summary>
        private static async Task<bool> WaitPickupSaveCompletedAsync(
            IPage page, int timeoutMs, double mx, double my, Random rng, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            bool confirmDone = false;
            do
            {
                ct.ThrowIfCancellationRequested();

                // 1) Hộp xác nhận đổi địa chỉ lấy hàng có thể hiện (KHÔNG chắc chắn) → bấm "Đồng ý" kiểu người.
                if (!confirmDone)
                {
                    var confirmBtn = await FindConfirmChangePickupButtonAsync(page).ConfigureAwait(false);
                    if (confirmBtn is not null)
                    {
                        bool ok;
                        (mx, my, ok) = await TryHumanClickVisibleAsync(page, confirmBtn, mx, my, rng, ct).ConfigureAwait(false);
                        if (ok)
                        {
                            confirmDone = true;
                            await Task.Delay(rng.Next(300, 900), ct).ConfigureAwait(false); // "đọc" rồi tiếp
                        }
                        continue; // kiểm lại vòng sau (hộp tan / modal đóng)
                    }
                }

                // 2) Modal "Sửa Địa chỉ" đã đóng.
                if (await FindEditAddressModalAsync(page).ConfigureAwait(false) is null)
                {
                    // Đã bấm "Đồng ý" rồi → chốt xong.
                    if (confirmDone)
                    {
                        return true;
                    }

                    // CHƯA bấm "Đồng ý" mà modal Sửa đã biến mất: hoặc (a) không cần xác nhận (Lưu thẳng),
                    // hoặc (b) Shopee THAY modal Sửa bằng hộp xác nhận nhưng hộp CHƯA KỊP render (khe thời
                    // gian). Ân hạn ngắn ~1.2s để hộp xác nhận (nếu có) kịp hiện — TRÁNH báo Ok GIẢ khi thực
                    // ra còn phải bấm "Đồng ý" (thao tác ghi thật). Thấy hộp → quay lại vòng chính bấm; hết
                    // ân hạn vẫn không có → Lưu thẳng, xong.
                    var grace = DateTime.UtcNow.AddMilliseconds(1200);
                    var lateConfirm = false;
                    do
                    {
                        ct.ThrowIfCancellationRequested();
                        if (await FindConfirmChangePickupButtonAsync(page).ConfigureAwait(false) is not null)
                        {
                            lateConfirm = true;
                            break;
                        }
                        await Task.Delay(300, ct).ConfigureAwait(false);
                    }
                    while (DateTime.UtcNow < grace);

                    if (!lateConfirm)
                    {
                        return true;
                    }
                    continue; // hộp xác nhận hiện muộn → vòng chính (block 1) bấm "Đồng ý"
                }

                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return false;
        }

        /// <summary>Tìm modal "Sửa Địa chỉ" hiện có: <c>.eds-modal__box</c> có <c>.title</c> khớp
        /// <see cref="ShopeeShippingNav.IsEditAddressModalTitle"/>. Không có → <c>null</c> (không ném).</summary>
        private static async Task<IElementHandle?> FindEditAddressModalAsync(IPage page)
        {
            try
            {
                var boxes = await page.QuerySelectorAllAsync(".eds-modal__box").ConfigureAwait(false);
                foreach (var box in boxes)
                {
                    var title = await box.QuerySelectorAsync(".title").ConfigureAwait(false);
                    if (title is null)
                    {
                        continue;
                    }

                    if (ShopeeShippingNav.IsEditAddressModalTitle(await title.InnerTextAsync().ConfigureAwait(false)))
                    {
                        return box;
                    }
                }
            }
            catch { /* bỏ qua */ }

            return null;
        }

        /// <summary>
        /// Dò nút <b>"Đồng ý"</b> của <b>hộp xác nhận đổi địa chỉ lấy hàng</b> (modal thứ hai bật lên SAU khi
        /// bấm Lưu — không phải lúc nào cũng hiện). Duyệt mọi <c>.eds-modal__footer</c> đang mở; chỉ nhận
        /// footer nào ĐỒNG THỜI có nút khớp <see cref="ShopeeShippingNav.IsConfirmButtonText"/> ("đồng ý")
        /// LẪN nút khớp <see cref="ShopeeShippingNav.IsCheckDetailButtonText"/> ("kiểm tra chi tiết") — dấu
        /// hiệu riêng để KHÔNG bấm nhầm "Đồng ý" của hộp thoại khác. Nút "Đồng ý" phải đang hiển thị
        /// (<c>BoundingBoxAsync() != null</c>) mới trả về. Không thấy / lỗi (DOM đổi, detached) →
        /// <c>null</c> (không ném). <b>Lưu ý:</b> bấm nút này sẽ TẮT kênh vận chuyển "Trong Ngày".
        /// </summary>
        private static async Task<IElementHandle?> FindConfirmChangePickupButtonAsync(IPage page)
        {
            try
            {
                var footers = await page.QuerySelectorAllAsync(".eds-modal__footer").ConfigureAwait(false);
                foreach (var footer in footers)
                {
                    var confirmBtn = await FindButtonByTextAsync(footer, ShopeeShippingNav.IsConfirmButtonText).ConfigureAwait(false);
                    if (confirmBtn is null)
                    {
                        continue;
                    }

                    // Guard đúng hộp: footer phải có CẢ "Kiểm tra chi tiết" → tránh nhầm hộp thoại khác.
                    var checkDetailBtn = await FindButtonByTextAsync(footer, ShopeeShippingNav.IsCheckDetailButtonText).ConfigureAwait(false);
                    if (checkDetailBtn is null)
                    {
                        continue;
                    }

                    // Chỉ nhận nút "Đồng ý" đang hiển thị.
                    if (await HasBoundingBoxAsync(confirmBtn).ConfigureAwait(false))
                    {
                        return confirmBtn;
                    }
                }
            }
            catch { /* DOM đổi / detached — coi như không thấy */ }

            return null;
        }

        /// <summary>
        /// Bấm nút "Hủy" của modal kiểu người (best-effort) rồi chờ modal đóng — dùng ở các nhánh thoát an
        /// toàn (không ghi gì lên shop). Trả về vị trí chuột mới.
        /// </summary>
        private static async Task<(double X, double Y)> HumanCancelModalAsync(
            IPage page, IElementHandle modal, double mx, double my, Random rng, CancellationToken ct)
        {
            var cancel = await FindCancelButtonAsync(modal).ConfigureAwait(false);
            if (cancel is not null)
            {
                (mx, my, _) = await TryHumanClickVisibleAsync(page, cancel, mx, my, rng, ct).ConfigureAwait(false);
            }

            await WaitEditAddressModalClosedAsync(page, 10000, ct).ConfigureAwait(false);
            return (mx, my);
        }

        /// <summary>
        /// Click <b>kiểu người CÓ HIT-TEST</b> nhưng chỉ khi phần tử đang hiển thị
        /// (<c>BoundingBoxAsync() != null</c>): scroll vào tầm nhìn trước, box vẫn null → KHÔNG click và trả
        /// <c>Clicked=false</c>. Có box → gọi <see cref="HumanMoveAndClickVerifiedAsync"/> (chỉ nhả chuột khi
        /// <c>elementFromPoint</c> tại điểm click đúng là phần tử đích — chống click nhầm link khác khi bị
        /// che/cụp); <c>Clicked</c> lấy từ kết quả verified (hit-test fail → false, KHÔNG click mù). Trả về
        /// vị trí chuột mới + đã click hay chưa.
        /// </summary>
        private static async Task<(double X, double Y, bool Clicked)> TryHumanClickVisibleAsync(
            IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
        {
            try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }

            if (!await HasBoundingBoxAsync(el).ConfigureAwait(false))
            {
                try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                if (!await HasBoundingBoxAsync(el).ConfigureAwait(false))
                {
                    return (mx, my, false);
                }
            }

            bool clicked;
            (mx, my, clicked) = await HumanMoveAndClickVerifiedAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);
            return (mx, my, clicked);
        }

        /// <summary>
        /// Phần tử có <b>bounding box</b> không (đang hiển thị), <b>nuốt lỗi</b> handle DETACHED (Vue vẽ lại
        /// form sau khi map/modal re-render khiến <c>BoundingBoxAsync</c> ném) → <c>false</c> graceful, KHÔNG
        /// để exception rò lên catch ngoài cùng của <see cref="SetPickupAddressAsync"/> (lỗi handle biến
        /// thành "không click được", modal vẫn được Hủy).
        /// </summary>
        private static async Task<bool> HasBoundingBoxAsync(IElementHandle el)
        {
            try { return await el.BoundingBoxAsync().ConfigureAwait(false) is not null; }
            catch { return false; }
        }

        // ===== Bước 3: xử lý ĐƠN ĐẦU TIÊN — Chuẩn bị hàng → tự mang ra bưu cục → In phiếu giao =====
        // URL danh sách đơn "Tất cả" — CHỈ dùng ở fallback cuối (khi không click được link menu kiểu người).
        private const string AllOrdersUrl = "https://banhang.shopee.vn/portal/sale/order";

        // Chờ nút "In phiếu giao" bấm được TỚI 5 phút: Shopee tạo mã vận đơn có thể LÂU (yêu cầu người dùng
        // 16/7 — 40s cũ không đủ, đơn bị PrintFailed oan). Trong lúc chờ chỉ POLL (không click dồn dập) + log
        // tiến trình mỗi ~30s để smoke thấy app đang chờ chứ không treo.
        private const int PrintButtonWaitSeconds = 300;

        public async Task<ArrangeShipmentResult> ProcessFirstOrderAsync(
            string downloadDir, Action<string>? log = null, CancellationToken ct = default)
        {
            void L(string m) => log?.Invoke(m);
            try
            {
                var page = WorkPage(); // tab shop hiện tại (mô hình nhiều-shop) — KHÔNG cứng Pages[0]
                if (page is null)
                {
                    return ArrangeShipmentResult.Failed;
                }

                // Random nội bộ + con trỏ bắt đầu ở vị trí ngẫu nhiên (đồng bộ style các thao tác kiểu người).
                var rng = new Random();
                var vp = page.ViewportSize;
                double vw = vp is not null ? vp.Width : 1280;
                double vh = vp is not null ? vp.Height : 720;
                double mx = rng.NextDouble() * vw;
                double my = rng.NextDouble() * vh;

                // Dừng "đọc trang" trước khi bắt đầu.
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

                // 1) Về danh sách đơn (/portal/sale/order).
                //    Nếu ĐÃ ở trang danh sách (đơn thứ 2+ trong lượt): RELOAD SẠCH bằng GotoAsync thay vì click
                //    menu SPA. Lý do: điều hướng SPA (click menu) KHÔNG dựng lại DOM → xác modal cũ ("Thông Tin
                //    Chi Tiết" của đơn trước, bị Vue giữ lại dạng ẩn/bẹp) tích tụ xuyên các đơn, đẩy nút MA lên
                //    TRƯỚC nút "In phiếu giao" thật → bước in kẹt (đơn đầu lượt DOM sạch nên luôn ngon). GotoAsync
                //    = người gõ URL/F5 → DOM sạch, mỗi đơn khởi đầu sạch. URL khác trang danh sách → vẫn về bằng
                //    GoToAllOrdersAsync (click menu kiểu người).
                //    LƯU Ý: reload/goto đưa Shopee về tab mặc định "Tất cả" (KHÔNG nhớ tab đang chọn như điều
                //    hướng SPA) → sau khi tới trang phải CLICK sang tab "Chờ lấy hàng" (EnsureToShipTabAsync)
                //    mới quét, kẻo đơn cần "Chuẩn bị hàng" bị đẩy khỏi trang 1 của "Tất cả" khi shop đông đơn.
                L("Về danh sách đơn (Tất cả)...");
                if (ShopeeShippingNav.IsAllOrdersHref(page.Url))
                {
                    try
                    {
                        await page.GotoAsync(AllOrdersUrl, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 30000
                        }).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* nuốt lỗi điều hướng — kiểm page.Url ngay dưới */ }
                }
                else
                {
                    (mx, my) = await GoToAllOrdersAsync(page, mx, my, rng, ct).ConfigureAwait(false);
                }
                if (!ShopeeShippingNav.IsAllOrdersHref(page.Url))
                {
                    return ArrangeShipmentResult.OrdersPageNotOpened;
                }

                // 1b) Reload/goto đưa về tab mặc định "Tất cả" → click sang tab "Chờ lấy hàng" trước khi quét
                //     (best-effort: không thấy tab / không đổi được thì quét tab hiện tại, KHÔNG chặn xử lý
                //     đơn). Điểm hội tụ của CẢ nhánh reload lẫn nhánh GoToAllOrdersAsync.
                (mx, my) = await EnsureToShipTabAsync(page, mx, my, rng, L, ct).ConfigureAwait(false);

                // Dừng "đọc trang" + chờ danh sách render.
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

                // 2) QUÉT TẤT CẢ đơn trong danh sách → lấy đơn ĐẦU TIÊN CÓ nút "Chuẩn bị hàng" (đơn đầu có
                //    thể đã arrange / trạng thái khác, KHÔNG có nút này). Không đơn nào cần xử lý → NoOrder.
                var (card, prepareBtn, orderCode) =
                    await FindFirstProcessableOrderAsync(page, 15000, L, ct).ConfigureAwait(false);
                if (card is null || prepareBtn is null)
                {
                    L("Không còn đơn nào cần Chuẩn bị hàng.");
                    return ArrangeShipmentResult.NoOrder;
                }

                // 3) Bấm "Chuẩn bị hàng" trong đơn đó (kiểu người, verified).
                bool clicked;
                (mx, my, clicked) = await TryHumanClickVisibleAsync(page, prepareBtn, mx, my, rng, ct).ConfigureAwait(false);
                if (!clicked)
                {
                    return ArrangeShipmentResult.PrepareNotFound;
                }
                L($"Bấm Chuẩn bị hàng cho đơn {(string.IsNullOrEmpty(orderCode) ? "(không rõ mã)" : orderCode)}.");

                // 4) Chờ modal "Giao Đơn Hàng".
                var shipModal = await WaitModalByTitleAsync(page, ShopeeShippingNav.IsShipOrderModalTitle, 10000, ct)
                    .ConfigureAwait(false);
                if (shipModal is null)
                {
                    return ArrangeShipmentResult.ShipModalNotOpened;
                }
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false); // "đọc modal"

                // 4a) Chọn "Tôi sẽ tự mang hàng tới Bưu cục" (mặc định đã selected → click lại vẫn an toàn).
                var dropoff = await FindDropoffOptionAsync(shipModal).ConfigureAwait(false);
                if (dropoff is not null && !await ElementHasClassTokenAsync(dropoff, "selected").ConfigureAwait(false))
                {
                    (mx, my, _) = await TryHumanClickVisibleAsync(page, dropoff, mx, my, rng, ct).ConfigureAwait(false);
                    L("Chọn tự mang hàng tới Bưu cục.");
                }

                // 4b) Bấm "Xác nhận".
                var confirmBtn = await FindArrangeConfirmButtonAsync(shipModal).ConfigureAwait(false);
                if (confirmBtn is null)
                {
                    return ArrangeShipmentResult.ConfirmFailed;
                }
                (mx, my, clicked) = await TryHumanClickVisibleAsync(page, confirmBtn, mx, my, rng, ct).ConfigureAwait(false);
                if (!clicked)
                {
                    return ArrangeShipmentResult.ConfirmFailed;
                }
                L("Đã xác nhận giao đơn.");

                // 5) Chờ modal "Thông Tin Chi Tiết" (có thể lâu do tạo vận đơn → poll ~15s).
                var detailModal = await WaitModalByTitleAsync(page, ShopeeShippingNav.IsDetailModalTitle, 15000, ct)
                    .ConfigureAwait(false);
                if (detailModal is null)
                {
                    return ArrangeShipmentResult.DetailModalNotOpened;
                }
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false); // "đọc modal"

                // 6) Bấm "In phiếu giao" (kiểu người THẲNG) → CHỜ tab phiếu mở (có thể MUỘN) → tải + in + đóng.
                // Smoke: nút "In phiếu giao" đã được WaitPrintButtonClickableAsync xác nhận CLICKABLE (hit-test
                // pass), NHƯNG nếu bấm qua TryHumanClickVisibleAsync (hit-test LẦN HAI lúc bấm) thì đôi lúc
                // false-negative → không nhả chuột (chuột "không bấm được"). → Sau khi ĐÃ xác nhận clickable,
                // CLICK KIỂU NGƯỜI THẲNG bằng HumanMoveAndClickAsync (chuột cong + down/trễ/up, KHÔNG kiểm
                // hit-test lần hai — vẫn like-human, KHÔNG native). HumanMoveAndClickAsync không trả cờ
                // "clicked" → thành công THẬT xác nhận bằng việc TAB PHIẾU mở ra (PHA 2). Tab có thể mở MUỘN
                // (Shopee gọi API tạo bản in) nên poll MỌI context tới PrintButtonWaitSeconds (5'); chống
                // double-tab bằng cách kiểm tab đã mở TRƯỚC mỗi lần bấm lại. Log tiến trình mỗi ~30s.
                L("Chờ nút In phiếu giao bấm được (Shopee đang tạo vận đơn)...");
                var before = _browser.Contexts.SelectMany(c => c.Pages).ToList();
                IPage? newPage = null;
                bool clickedPrint = false;
                var printStart = DateTime.UtcNow;
                var printDeadline = printStart.AddSeconds(PrintButtonWaitSeconds);
                var lastPrintProgressLog = printStart;
                while (newPage is null && DateTime.UtcNow < printDeadline)
                {
                    ct.ThrowIfCancellationRequested();

                    // Log tiến trình mỗi ~30s (MỘT dòng, không dồn dập) — smoke thấy app đang chờ, không treo.
                    if ((DateTime.UtcNow - lastPrintProgressLog).TotalSeconds >= 30)
                    {
                        lastPrintProgressLog = DateTime.UtcNow;
                        L($"Vẫn chờ nút In phiếu giao (đã {(int)(DateTime.UtcNow - printStart).TotalSeconds}s) — Shopee đang tạo vận đơn...");
                    }

                    // Tab đã mở từ cú click TRƯỚC? (tránh bấm lại → double-tab). Quét MỌI context (tab có thể
                    // mở ở context/cửa sổ khác).
                    try { newPage = _browser.Contexts.SelectMany(c => c.Pages).FirstOrDefault(p => p != page && !before.Contains(p)); }
                    catch { /* context ngắt — thử vòng sau */ }
                    if (newPage is not null)
                    {
                        break;
                    }

                    // Re-find nút TƯƠI + xác nhận bấm được (hit-test MỘT lần ở đây), rồi CLICK KIỂU NGƯỜI THẲNG.
                    var btn = await WaitPrintButtonClickableAsync(page, ShopeeShippingNav.IsPrintSlipButtonText, 8000, ct).ConfigureAwait(false);
                    if (btn is null)
                    {
                        await Task.Delay(rng.Next(500, 1200), ct).ConfigureAwait(false);
                        continue;
                    }

                    (mx, my) = await HumanMoveAndClickAsync(page, btn, mx, my, rng, ct).ConfigureAwait(false);
                    clickedPrint = true;
                    L("Đã bấm In phiếu giao (kiểu người), chờ tab phiếu mở...");

                    // Chờ tab mới ~8s sau cú click này rồi mới thử lại (bấm lần nữa nếu vẫn chưa mở).
                    var waitTab = DateTime.UtcNow.AddSeconds(8);
                    while (newPage is null && DateTime.UtcNow < waitTab)
                    {
                        ct.ThrowIfCancellationRequested();
                        try { newPage = _browser.Contexts.SelectMany(c => c.Pages).FirstOrDefault(p => p != page && !before.Contains(p)); }
                        catch { /* context ngắt — thử vòng sau */ }
                        if (newPage is null)
                        {
                            await Task.Delay(400, ct).ConfigureAwait(false);
                        }
                    }
                }
                if (newPage is null)
                {
                    // Hết 5' vẫn không có tab phiếu → chẩn đoán trạng thái nút (CHỈ ĐỌC DOM: nút có tồn tại?
                    // disabled? text? phần tử che tại tâm nút qua elementFromPoint) để lần sau tinh chỉnh —
                    // best-effort, nuốt lỗi (OCE vẫn ném), chẩn đoán fail KHÔNG được phá luồng.
                    try
                    {
                        // Chẩn đoán CỐ Ý chụp nút ĐẦU theo thứ tự DOM (document.querySelector / vòng dừng ở nút
                        // khớp đầu tiên) — thường là nút MA của modal CŨ (box bẹp, tâm bị vỏ modal khác đè) — để
                        // LOG bằng chứng vì sao bấm kẹt. KHÁC đường bấm THẬT (WaitPrintButtonClickableAsync giờ
                        // duyệt TẤT CẢ ứng viên, thứ tự NGƯỢC): (a) testid trước; (b) fallback theo TEXT "in phiếu giao".
                        const string diagJs = @"() => {
                            const fmt = (btn, via) => {
                                const r = btn.getBoundingClientRect();
                                const cx = r.x + r.width / 2, cy = r.y + r.height / 2;
                                const el = document.elementFromPoint(cx, cy);
                                const cls = el ? (el.getAttribute('class') || '') : '';
                                const cover = el ? (el.tagName.toLowerCase() + (cls ? '.' + cls : '')) : '(khong co)';
                                const txt = (btn.innerText || '').trim().slice(0, 40);
                                return 'via=' + via + ', disabled=' + btn.disabled
                                    + ', aria-disabled=' + btn.getAttribute('aria-disabled')
                                    + ', text=[' + txt + '], box=' + Math.round(r.width) + 'x' + Math.round(r.height)
                                    + ', elementFromPoint=' + cover;
                            };
                            const byId = document.querySelector(""button[data-testid='print-button']"");
                            if (byId) return fmt(byId, 'testid');
                            const norm = s => (s || '').trim().toLowerCase().replace(/\s+/g, ' ');
                            for (const b of document.querySelectorAll('button')) {
                                if (norm(b.innerText) === 'in phiếu giao') {
                                    const r = b.getBoundingClientRect();
                                    if (r.width > 0 && r.height > 0) return fmt(b, 'text');
                                }
                            }
                            return 'khong thay nut In phieu giao (ca testid lan text)';
                        }";
                        var diag = await page.EvaluateAsync<string>(diagJs).ConfigureAwait(false);
                        L("Chẩn đoán nút In phiếu giao: " + diag);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* chẩn đoán fail không được phá luồng */ }

                    L(clickedPrint
                        ? "Đã bấm In phiếu giao nhưng KHÔNG thấy tab phiếu mở ra — kiểm tra tay."
                        : $"Nút In phiếu giao KHÔNG bấm được trong {PrintButtonWaitSeconds}s (chưa bấm được lần nào) — kiểm tra tay.");
                    return ArrangeShipmentResult.PrintFailed;
                }

                // Chờ tab điều hướng tới awbprint (có thể khởi đầu about:blank).
                var urlDeadline = DateTime.UtcNow.AddSeconds(10);
                while (!SafeUrlHasAwbprint(newPage) && DateTime.UtcNow < urlDeadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }
                L($"Đã bắt được tab phiếu ({(SafeUrlHasAwbprint(newPage) ? "awbprint OK" : "URL chưa phải awbprint")}).");

                // Từ đây: LƯU phiếu best-effort có log — KHÔNG hạ kết quả xuống fail (đơn đã được arrange).
                await SaveSlipAsync(newPage, downloadDir, orderCode, L, ct).ConfigureAwait(false);

                // Đóng modal "Thông Tin Chi Tiết" bằng nút X (modal KHÔNG tự đóng sau khi in) rồi mới coi là
                // xong đơn — best-effort, có log; KHÔNG hạ Ok nếu không đóng được (đơn đã arrange/in).
                await CloseDetailModalAsync(page, rng, L, ct).ConfigureAwait(false);
                L("Đã xử lý xong đơn (đóng phiếu chi tiết).");
                return ArrangeShipmentResult.Ok;
            }
            catch (OperationCanceledException)
            {
                // Bị dừng chủ động (bấm Dừng / thoát app) → ném để ProcessOrdersAsync ở App bắt và dừng SẠCH,
                // KHÔNG báo "Xử lý đơn gặp lỗi" (Failed) gây hiểu lầm.
                throw;
            }
            catch (Exception ex)
            {
                // Bất kỳ lỗi bất ngờ nào (selector đổi, context ngắt...) → Failed, KHÔNG phá phiên.
                L("Xử lý đơn gặp lỗi bất ngờ: " + ex.Message);
                return ArrangeShipmentResult.Failed;
            }
        }

        // ===== Sync đơn: duyệt tab "Tất cả" mọi trang, thu thập thông tin đơn (Core CHỈ trả DTO) =====

        // Chốt chặn an toàn số trang quét mỗi lượt sync (tránh lặp vô hạn nếu selector "trang sau" đoán sai
        // hoặc điều kiện dừng không kích hoạt). Chạm cap → dừng + cờ ReachedPageCap.
        // 2026-07-22: hạ 20 → 10 theo yêu cầu (giảm tải mỗi lượt sync; vẫn đọc tab "Tất cả"). Đơn ở trang >10 sẽ
        // không còn được cập nhật trạng thái mỗi lượt (vẫn nằm trong DB từ trước) — chấp nhận tạm.
        private const int MaxSyncPages = 10;

        public async Task<SyncOrdersResult> SyncAllOrdersAsync(
            Action<string>? log = null,
            IReadOnlySet<string>? ordersWithFinalAmount = null,
            CancellationToken ct = default)
        {
            void L(string m) => log?.Invoke(m);

            // Gom dần + khử trùng lặp theo mã đơn (trang có thể trùng khi Shopee đổi dữ liệu giữa các lần quét).
            var collected = new List<SyncedOrder>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Tra object đơn (trong collected) theo mã — để gán "Số tiền cuối cùng" vào ĐÚNG object đã gom.
            var bySn = new Dictionary<string, SyncedOrder>(StringComparer.Ordinal);
            var pages = 0;
            var reachedCap = false;

            try
            {
                var page = WorkPage(); // tab shop hiện tại (mô hình nhiều-shop) — KHÔNG cứng Pages[0]
                if (page is null)
                {
                    // Nói rõ lý do — kẻo tầng App tổng kết "Sync xong: 0 đơn / 0 trang" gây hiểu nhầm hết đơn.
                    L("Không có trang trình duyệt nào đang mở — dừng sync.");
                    return new SyncOrdersResult(collected, 0, false);
                }

                // Random nội bộ + con trỏ bắt đầu ở vị trí ngẫu nhiên (đồng bộ style các thao tác kiểu người).
                var rng = new Random();
                var vp = page.ViewportSize;
                double vw = vp is not null ? vp.Width : 1280;
                double vh = vp is not null ? vp.Height : 720;
                double mx = rng.NextDouble() * vw;
                double my = rng.NextDouble() * vh;

                // Dừng "đọc trang" trước khi bắt đầu.
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

                // 1) Về danh sách đơn (/portal/sale/order). ĐÃ ở đó → reload SẠCH (GotoAsync, như bước 1
                //    ProcessFirstOrderAsync); URL khác → GoToAllOrdersAsync (click menu kiểu người).
                L("Về danh sách đơn (Tất cả) để sync...");
                if (ShopeeShippingNav.IsAllOrdersHref(page.Url))
                {
                    try
                    {
                        await page.GotoAsync(AllOrdersUrl, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 30000
                        }).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* nuốt lỗi điều hướng — kiểm page.Url ngay dưới */ }
                }
                else
                {
                    (mx, my) = await GoToAllOrdersAsync(page, mx, my, rng, ct).ConfigureAwait(false);
                }
                if (!ShopeeShippingNav.IsAllOrdersHref(page.Url))
                {
                    L("Không mở được trang danh sách đơn — dừng sync.");
                    return new SyncOrdersResult(collected, 0, false);
                }

                // 1b) Chuyển sang tab "Tất cả" (best-effort: không đổi được thì quét tab hiện tại).
                (mx, my) = await EnsureOrderListTabAsync(
                    page, "l1-tab-all", ShopeeShippingNav.IsAllTabText, "Tất cả", mx, my, rng, L, ct).ConfigureAwait(false);

                // Dừng "đọc trang" + chờ danh sách render.
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

                // 2) VÒNG QUÉT TRANG: chờ danh sách ổn định → quét JS chỉ-đọc → gom + khử trùng → sang trang sau.
                var pagerDiagLogged = false;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    // Chờ danh sách render ổn định (số card đứng yên 2 lượt liên tiếp) rồi mới quét.
                    await WaitOrderListReadyAsync(page, 15000, ct).ConfigureAwait(false);

                    var scanJson = await page.EvaluateAsync<string>(ScanOrdersJs).ConfigureAwait(false);
                    var pageOrders = ParseOrdersJson(scanJson);
                    pages++;
                    foreach (var o in pageOrders)
                    {
                        if (seen.Add(o.OrderSn))
                        {
                            collected.Add(o);
                            bySn[o.OrderSn] = o;
                        }
                    }
                    L($"Sync trang {pages}: {pageOrders.Count} đơn.");

                    // Chẩn đoán (chỉ trang 1): các trạng thái đọc được + HTML rút gọn cột trạng thái card đầu —
                    // để soi & tinh chỉnh selector nếu Shopee đổi cấu trúc (trạng thái đọc rỗng/sai).
                    if (pages == 1)
                    {
                        var statuses = string.Join(", ", pageOrders
                            .Select(o => string.IsNullOrWhiteSpace(o.Status) ? "(trống)" : o.Status)
                            .Distinct()
                            .Take(12));
                        L($"Trạng thái đọc được (trang 1): {statuses}");
                        try
                        {
                            var statusHtml = await page.EvaluateAsync<string>(
                                @"() => { const c = document.querySelector(""a[data-testid='order-item']""); if (!c) return '(không thấy card)';"
                                + " const col = c.querySelector('.status-info-col') || c; return (col.outerHTML || '').replace(/\\s+/g,' ').slice(0, 700); }")
                                .ConfigureAwait(false);
                            L("HTML cột trạng thái (card đầu): " + statusHtml);
                        }
                        catch { /* chẩn đoán best-effort — bỏ qua nếu lỗi */ }
                    }

                    // 2b) Lấy "Số tiền cuối cùng" cho các đơn của TRANG NÀY: CHỈ đơn "Chuẩn bị giao hàng"/"Chờ lấy
                    //     hàng" (đang cần chuẩn bị) VÀ chưa có final_amount (DB lần trước qua ordersWithFinalAmount,
                    //     hoặc đã lấy ở trang trước qua FinalAmount != null). Mở CHI TIẾT trên tab MỚI → đọc → đóng.
                    //     CHẬM (mỗi đơn 1 tab) nên chỉ làm cho đơn THẬT SỰ cần (bỏ đơn đã giao/đang giao/đã hủy...).
                    var needFinal = pageOrders
                        .Select(o => bySn.TryGetValue(o.OrderSn, out var c) ? c : null)
                        .Where(c => c is not null
                                    && IsPrepareToShipStatus(c!.Status)
                                    && c.FinalAmount is null
                                    && !(ordersWithFinalAmount?.Contains(c.OrderSn) ?? false))
                        .Select(c => c!)
                        .Distinct() // phòng card trùng mã trong cùng trang → không mở chi tiết 2 lần cùng đơn
                        .ToList();
                    if (needFinal.Count > 0)
                    {
                        (mx, my) = await FetchFinalAmountsForPageAsync(page, needFinal, pages, mx, my, rng, L, ct)
                            .ConfigureAwait(false);
                    }

                    // Ký hiệu danh sách hiện tại (số card + mã đơn đầu) — để phát hiện danh sách ĐỔI sau khi bấm.
                    var signatureBefore = await ReadListSignatureAsync(page, ct).ConfigureAwait(false);

                    // Tìm nút "trang sau" (PHÒNG THỦ: nhiều selector, nhận nút có box & KHÔNG disabled).
                    var nextBtn = await FindNextPageButtonAsync(page, ct).ConfigureAwait(false);
                    if (nextBtn is null)
                    {
                        // Không thấy nút trang sau → hết trang (hoặc selector chưa khớp DOM thật). LẦN ĐẦU
                        // log MỘT dòng chẩn đoán pager để tinh chỉnh selector sau lần chạy đầu.
                        if (!pagerDiagLogged)
                        {
                            await LogPagerDiagnosticAsync(page, L, ct).ConfigureAwait(false);
                            pagerDiagLogged = true;
                        }
                        L("Không còn trang sau — sync xong.");
                        break;
                    }

                    // Có trang sau nhưng đã tới chốt chặn → dừng, cờ cap (có thể còn đơn chưa quét).
                    if (pages >= MaxSyncPages)
                    {
                        reachedCap = true;
                        L($"Đã quét {pages} trang — chạm chốt chặn {MaxSyncPages} trang, dừng (có thể còn đơn chưa quét).");
                        break;
                    }

                    // Bấm "trang sau" kiểu người (hit-test).
                    bool clicked;
                    (mx, my, clicked) = await TryHumanClickVisibleAsync(page, nextBtn, mx, my, rng, ct).ConfigureAwait(false);
                    if (!clicked)
                    {
                        L("Không bấm được nút trang sau — dừng sync.");
                        break;
                    }

                    // Chờ danh sách ĐỔI (poll ≤10s: số card / mã đơn đầu khác đi). Không đổi → coi như hết trang.
                    var changed = await WaitListChangedAsync(page, signatureBefore, 10000, ct).ConfigureAwait(false);
                    if (!changed)
                    {
                        L("Danh sách không đổi sau khi bấm trang sau — coi như hết trang, dừng sync.");
                        break;
                    }

                    // Dừng ngẫu nhiên kiểu người giữa các trang.
                    await Task.Delay(rng.Next(1500, 3500), ct).ConfigureAwait(false);
                }

                return new SyncOrdersResult(collected, pages, reachedCap);
            }
            catch (OperationCanceledException)
            {
                // Bị dừng chủ động → ném để tầng App bắt và dừng SẠCH.
                throw;
            }
            catch (Exception ex)
            {
                // Lỗi bất ngờ (selector đổi, context ngắt...) → trả những đơn ĐÃ gom được (không mất dữ liệu).
                L("Sync đơn gặp lỗi bất ngờ: " + ex.Message + " — trả về những đơn đã gom được.");
                return new SyncOrdersResult(collected, pages, reachedCap);
            }
        }

        // ===== Tải LẠI phiếu giao cho đơn ĐÃ arrange nhưng thiếu file PDF (best-effort, kiểu người) =====

        public async Task<int> RedownloadSlipsAsync(
            IReadOnlyList<string> orderSns, string downloadDir, Action<string>? log = null, CancellationToken ct = default)
        {
            void L(string m) => log?.Invoke(m);

            var saved = 0;
            if (orderSns is null || orderSns.Count == 0)
            {
                return 0;
            }

            // Các mã còn cần tìm (đơn tìm thấy card sẽ bị GỠ dù lưu thành công hay không — đã thử, best-effort).
            var remaining = new HashSet<string>(orderSns.Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.Ordinal);
            if (remaining.Count == 0)
            {
                return 0;
            }

            try
            {
                var page = WorkPage(); // tab shop hiện tại (mô hình nhiều-shop) — KHÔNG cứng Pages[0]
                if (page is null)
                {
                    L("Không có trang trình duyệt nào đang mở — bỏ tải lại phiếu.");
                    return 0;
                }

                var rng = new Random();
                var vp = page.ViewportSize;
                double vw = vp is not null ? vp.Width : 1280;
                double vh = vp is not null ? vp.Height : 720;
                double mx = rng.NextDouble() * vw;
                double my = rng.NextDouble() * vh;

                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);
                L($"Tải lại phiếu cho {remaining.Count} đơn thiếu file...");

                // 1) Về danh sách đơn (/portal/sale/order) — reuse cách của SyncAllOrdersAsync/ProcessFirstOrderAsync.
                if (ShopeeShippingNav.IsAllOrdersHref(page.Url))
                {
                    try
                    {
                        await page.GotoAsync(AllOrdersUrl, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 30000
                        }).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* nuốt lỗi điều hướng — kiểm page.Url ngay dưới */ }
                }
                else
                {
                    (mx, my) = await GoToAllOrdersAsync(page, mx, my, rng, ct).ConfigureAwait(false);
                }
                if (!ShopeeShippingNav.IsAllOrdersHref(page.Url))
                {
                    L("Không mở được trang danh sách đơn — bỏ tải lại phiếu.");
                    return saved;
                }

                // 1b) Tab "Tất cả" (đơn Chuẩn bị hàng chắc chắn nằm trong Tất cả).
                (mx, my) = await EnsureOrderListTabAsync(
                    page, "l1-tab-all", ShopeeShippingNav.IsAllTabText, "Tất cả", mx, my, rng, L, ct).ConfigureAwait(false);
                await Task.Delay(rng.Next(800, 2500), ct).ConfigureAwait(false);

                // 2) VÒNG QUÉT TRANG (reuse chốt chặn + phân trang phòng thủ của sync).
                var pages = 0;
                while (remaining.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    await WaitOrderListReadyAsync(page, 15000, ct).ConfigureAwait(false);
                    pages++;

                    // Duyệt các mã còn thiếu — snapshot remaining (RedownloadOneSlipAsync có thể gỡ khỏi remaining).
                    foreach (var sn in remaining.ToList())
                    {
                        ct.ThrowIfCancellationRequested();
                        bool located, ok;
                        (mx, my, located, ok) = await RedownloadOneSlipAsync(page, sn, downloadDir, mx, my, rng, L, ct)
                            .ConfigureAwait(false);
                        if (located)
                        {
                            remaining.Remove(sn); // đã thử đơn này (dù lưu được hay không) — không tìm lại ở trang khác
                            if (ok)
                            {
                                saved++;
                            }
                        }
                    }

                    if (remaining.Count == 0)
                    {
                        break; // đã xử lý hết mã cần tải
                    }

                    // Sang trang sau (reuse signature + FindNextPageButton + WaitListChanged + cap).
                    var signatureBefore = await ReadListSignatureAsync(page, ct).ConfigureAwait(false);
                    var nextBtn = await FindNextPageButtonAsync(page, ct).ConfigureAwait(false);
                    if (nextBtn is null)
                    {
                        L($"Không còn trang sau — dừng tải lại phiếu (còn {remaining.Count} đơn chưa thấy card).");
                        break;
                    }
                    if (pages >= MaxSyncPages)
                    {
                        L($"Đã quét {pages} trang — chạm chốt chặn {MaxSyncPages} trang, dừng tải lại phiếu (còn {remaining.Count} đơn chưa thấy card).");
                        break;
                    }

                    bool clicked;
                    (mx, my, clicked) = await TryHumanClickVisibleAsync(page, nextBtn, mx, my, rng, ct).ConfigureAwait(false);
                    if (!clicked)
                    {
                        L("Không bấm được nút trang sau — dừng tải lại phiếu.");
                        break;
                    }
                    var changed = await WaitListChangedAsync(page, signatureBefore, 10000, ct).ConfigureAwait(false);
                    if (!changed)
                    {
                        L("Danh sách không đổi sau khi bấm trang sau — coi như hết trang, dừng tải lại phiếu.");
                        break;
                    }
                    await Task.Delay(rng.Next(1500, 3500), ct).ConfigureAwait(false);
                }

                if (remaining.Count > 0)
                {
                    L($"Còn {remaining.Count} đơn không tìm thấy card (đơn quá cũ / ngoài phạm vi quét) — bỏ qua.");
                }
                L($"Tải lại phiếu xong: lưu được {saved}/{orderSns.Count} phiếu.");
                return saved;
            }
            catch (OperationCanceledException)
            {
                throw; // dừng chủ động → ném để caller dừng sạch
            }
            catch (Exception ex)
            {
                L("Tải lại phiếu gặp lỗi bất ngờ: " + ex.Message);
                return saved;
            }
        }

        /// <summary>
        /// Tải lại phiếu cho MỘT đơn (mã <paramref name="orderSn"/>) trên TRANG DANH SÁCH hiện tại: định vị card
        /// (<see cref="FindOrderCardBySnAsync"/>); không thấy → trả <c>Located=false</c> (caller tìm ở trang khác).
        /// Thấy → thử bấm "In phiếu giao" (Path 1: nút ngay trong card đơn đã arrange; Path 2: mở CHI TIẾT đơn
        /// rồi tìm nút) → bắt tab phiếu (awbprint) → <see cref="SaveSlipAsync"/> → kiểm file (<see cref="LooksPdf"/>).
        /// Best-effort: mọi lỗi (trừ hủy) chỉ log; trả <c>(mx, my, Located, Saved)</c>.
        /// </summary>
        private async Task<(double X, double Y, bool Located, bool Saved)> RedownloadOneSlipAsync(
            IPage page, string orderSn, string downloadDir, double mx, double my, Random rng, Action<string> L, CancellationToken ct)
        {
            IElementHandle? card;
            try { card = await FindOrderCardBySnAsync(page, orderSn, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { card = null; }
            if (card is null)
            {
                return (mx, my, false, false); // không có trên trang này — chưa "located"
            }

            L($"Tải lại phiếu đơn {orderSn}: định vị card OK, tìm nút In phiếu giao...");

            IPage? detailTab = null;
            IPage? slipTab = null;
            try
            {
                var before = _browser.Contexts.SelectMany(c => c.Pages).ToList();

                // Path 1: nút "In phiếu giao" NGAY trong card (đơn đã arrange thường có sẵn nút này).
                var printBtn = await FindPrintButtonInCardAsync(card).ConfigureAwait(false);
                if (printBtn is not null)
                {
                    bool clicked;
                    (mx, my, clicked) = await TryHumanClickVisibleAsync(page, printBtn, mx, my, rng, ct).ConfigureAwait(false);
                    if (clicked)
                    {
                        L($"Đã bấm In phiếu giao (trong card) đơn {orderSn}, chờ tab phiếu...");
                        slipTab = await WaitNewPageAsync(before, 15, ct).ConfigureAwait(false);
                    }
                }

                // Path 2 (fallback): mở CHI TIẾT đơn (tab mới) rồi tìm nút "In phiếu giao" trên tab đó.
                if (slipTab is null)
                {
                    var detailBtn = await FindViewDetailButtonInCardAsync(card).ConfigureAwait(false);
                    if (detailBtn is not null)
                    {
                        bool clicked;
                        (mx, my, clicked) = await TryHumanClickVisibleAsync(page, detailBtn, mx, my, rng, ct).ConfigureAwait(false);
                        if (clicked)
                        {
                            detailTab = await WaitNewPageAsync(before, 8, ct).ConfigureAwait(false);
                        }
                    }

                    if (detailTab is not null)
                    {
                        try
                        {
                            await detailTab.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                                new PageWaitForLoadStateOptions { Timeout = 15000 }).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { /* chi tiết có thể vẫn đang tải — vẫn thử tìm nút */ }

                        var before2 = _browser.Contexts.SelectMany(c => c.Pages).ToList();
                        var btn2 = await WaitPrintButtonClickableAsync(
                            detailTab, ShopeeShippingNav.IsPrintSlipButtonText, 10000, ct).ConfigureAwait(false);
                        if (btn2 is not null)
                        {
                            var dvp = detailTab.ViewportSize;
                            double dmx = rng.NextDouble() * (dvp is not null ? dvp.Width : 1280);
                            double dmy = rng.NextDouble() * (dvp is not null ? dvp.Height : 720);
                            await HumanMoveAndClickAsync(detailTab, btn2, dmx, dmy, rng, ct).ConfigureAwait(false);
                            L($"Đã bấm In phiếu giao (chi tiết) đơn {orderSn}, chờ tab phiếu...");
                            slipTab = await WaitNewPageAsync(before2, 15, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            L($"Không thấy nút In phiếu giao ở chi tiết đơn {orderSn}.");
                        }
                    }
                }

                var savedOk = false;
                if (slipTab is not null)
                {
                    // Chờ tab điều hướng tới awbprint (có thể khởi đầu about:blank) — reuse như ProcessFirstOrderAsync.
                    var urlDeadline = DateTime.UtcNow.AddSeconds(10);
                    while (!SafeUrlHasAwbprint(slipTab) && DateTime.UtcNow < urlDeadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(300, ct).ConfigureAwait(false);
                    }

                    // Lưu phiếu (SaveSlipAsync tự đóng tab phiếu). KIỂM lại file sau khi lưu.
                    await SaveSlipAsync(slipTab, downloadDir, orderSn, L, ct).ConfigureAwait(false);
                    savedOk = SlipFileLooksReal(downloadDir, orderSn);
                    L(savedOk
                        ? $"Tải lại phiếu đơn {orderSn}: OK (file PDF hợp lệ)."
                        : $"Tải lại phiếu đơn {orderSn}: CHƯA có file PDF hợp lệ — kiểm tra tay.");
                }
                else
                {
                    L($"Không mở được tab phiếu cho đơn {orderSn} — bỏ qua.");
                }

                return (mx, my, true, savedOk);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                L($"Lỗi khi tải lại phiếu đơn {orderSn}: {ex.Message}");
                return (mx, my, true, false); // đã located — không tìm lại
            }
            finally
            {
                // Đóng tab chi tiết (nếu Path 2 mở) — tuyệt đối không đụng tab danh sách gốc.
                if (detailTab is not null)
                {
                    try { await detailTab.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                }
                // Best-effort đóng modal "Thông Tin Chi Tiết" nếu vô tình mở (no-op nếu không có).
                await CloseDetailModalAsync(page, rng, L, ct).ConfigureAwait(false);
                await Task.Delay(rng.Next(400, 1200), ct).ConfigureAwait(false);
            }
        }

        /// <summary>Tìm nút "In phiếu giao" NGAY TRONG card đơn (đơn đã arrange): ưu tiên
        /// <c>button[data-testid='print-button']</c>, fallback button khớp <see cref="ShopeeShippingNav.IsPrintSlipButtonText"/>;
        /// chỉ nhận nút có bounding box. Không thấy → <c>null</c>. Reuse selector của luồng In phiếu (không tự chế mới).</summary>
        private static async Task<IElementHandle?> FindPrintButtonInCardAsync(IElementHandle card)
        {
            try
            {
                var byId = await card.QuerySelectorAsync("button[data-testid='print-button']").ConfigureAwait(false);
                if (byId is not null && await HasBoundingBoxAsync(byId).ConfigureAwait(false))
                {
                    return byId;
                }

                var byText = await FindButtonByTextAsync(card, ShopeeShippingNav.IsPrintSlipButtonText).ConfigureAwait(false);
                if (byText is not null && await HasBoundingBoxAsync(byText).ConfigureAwait(false))
                {
                    return byText;
                }
            }
            catch { /* card DOM lạ — bỏ qua */ }
            return null;
        }

        /// <summary>Poll ≤ <paramref name="seconds"/>s một trang MỚI (Page nào không nằm trong <paramref name="before"/>) —
        /// tab phiếu / tab chi tiết mở MUỘN. Quét MỌI context (tab có thể mở ở cửa sổ khác). Không thấy → <c>null</c>.</summary>
        private async Task<IPage?> WaitNewPageAsync(IReadOnlyCollection<IPage> before, int seconds, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            IPage? found = null;
            while (found is null && DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try { found = _browser.Contexts.SelectMany(c => c.Pages).FirstOrDefault(p => !before.Contains(p)); }
                catch { /* context ngắt — thử vòng sau */ }
                if (found is null)
                {
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }
            }
            return found;
        }

        /// <summary>True nếu file phiếu <c>&lt;downloadDir&gt;/&lt;SanitizeFileName(orderSn)&gt;.pdf</c> tồn tại và
        /// 4 byte đầu là magic <c>%PDF</c> (<see cref="LooksPdf"/>). Đọc TỐI ĐA 8 byte đầu — dùng đếm thành công
        /// sau <see cref="SaveSlipAsync"/>. Mọi lỗi IO → <c>false</c>.</summary>
        private static bool SlipFileLooksReal(string downloadDir, string orderSn)
        {
            try
            {
                var path = Path.Combine(downloadDir, ShopeeShippingNav.SanitizeFileName(orderSn) + ".pdf");
                if (!File.Exists(path))
                {
                    return false;
                }
                using var fs = File.OpenRead(path);
                var head = new byte[8];
                var n = fs.Read(head, 0, head.Length);
                return LooksPdf(n >= 8 ? head : head[..Math.Max(0, n)]);
            }
            catch { return false; }
        }

        // JS CHỈ-ĐỌC quét MỌI card đơn của trang hiện tại theo bảng selector do người dùng cung cấp. Bọc từng
        // card + từng item trong try để một card/item lạ KHÔNG phá cả trang. Trả JSON.stringify(mảng đơn).
        private const string ScanOrdersJs = @"() => {
    const norm = s => (s || '').replace(/\s+/g, ' ').trim();
    const cards = document.querySelectorAll(""a[data-testid='order-item']"");
    const out = [];
    for (const card of cards) {
        try {
            const snEl = card.querySelector('.order-sn');
            const snRaw = snEl ? norm(snEl.textContent) : '';
            const snTokens = snRaw.split(' ');
            const orderSn = snTokens.length ? snTokens[snTokens.length - 1] : '';

            let shopeeOrderId = '';
            const href = card.getAttribute('href') || '';
            const hm = href.match(/\/portal\/sale\/order\/(\d+)/);
            if (hm) shopeeOrderId = hm[1];

            const buyerEl = card.querySelector('.buyer-username');
            const buyer = buyerEl ? norm(buyerEl.textContent) : '';

            const items = [];
            for (const it of card.querySelectorAll('.item')) {
                try {
                    const nameEl = it.querySelector('.item-name');
                    const descEl = it.querySelector('.item-description');
                    const amtEl = it.querySelector('.item-amount');
                    const imgEl = it.querySelector('.item-image');
                    const name = nameEl ? norm(nameEl.textContent) : '';
                    let variation = descEl ? norm(descEl.textContent) : '';
                    variation = variation.replace(/^Variation\s*:?\s*/i, '').trim();
                    let amount = amtEl ? norm(amtEl.textContent) : '';
                    amount = amount.replace(/^[x×]\s*/i, '').trim();
                    let image = '';
                    if (imgEl) image = imgEl.getAttribute('src') || imgEl.getAttribute('data-src') || '';
                    items.push({ name, variation, amount, image });
                } catch (e) { /* item lạ — bỏ qua */ }
            }

            const totalEl = card.querySelector('.total-price');
            const totalText = totalEl ? norm(totalEl.textContent) : '';

            const payEl = card.querySelector('.payment-method');
            const payment = payEl ? norm(payEl.textContent) : '';

            // Trạng thái: LẤY THEO TEXT THẬT của cột (đã giao / đã hủy / chuẩn bị hàng / chờ lấy hàng...), KHÔNG
            // cố định 1 trạng thái. Ưu tiên .status; rỗng thì thử phần tử có class chứa 'status' (bỏ mô tả & cột
            // bao); cuối cùng lấy phần tử con ĐẦU TIÊN có text. Bỏ text của .status-description để không dính mô tả.
            const statusColEl = card.querySelector('.status-info-col');
            let status = '';
            if (statusColEl) {
                let stEl = statusColEl.querySelector('.status');
                if (!stEl) {
                    for (const c of statusColEl.querySelectorAll('[class*=status]')) {
                        const cls = (typeof c.className === 'string') ? c.className : '';
                        if (cls.indexOf('status-description') >= 0 || cls.indexOf('status-info-col') >= 0) continue;
                        if (norm(c.textContent)) { stEl = c; break; }
                    }
                }
                status = stEl ? norm(stEl.textContent) : '';
                if (!status) {
                    for (const ch of statusColEl.children) {
                        const t = norm(ch.textContent);
                        if (t) { status = t; break; }
                    }
                }
            }

            const sdescEl = card.querySelector('.status-description');
            const statusDesc = sdescEl ? norm(sdescEl.textContent) : '';

            let cancelReason = '';
            const statusCol = card.querySelector('.status-info-col') || card;
            const pops = statusCol.querySelectorAll('.eds-popover__content');
            for (const pop of pops) {
                const raw = pop.textContent || '';
                if (raw.indexOf('Lý do hủy') >= 0) {
                    cancelReason = norm(raw).replace(/^.*?Lý do hủy\s*:?\s*/, '').trim();
                    break;
                }
            }

            const channelEl = card.querySelector('.maksed-channel-name');
            const channel = channelEl ? norm(channelEl.textContent) : '';
            const carrierEl = card.querySelector('.fulfilment-channel-name');
            const carrier = carrierEl ? norm(carrierEl.textContent) : '';

            const trackEl = card.querySelector('.tracking-number');
            const tracking = trackEl ? norm(trackEl.textContent) : '';

            out.push({
                orderSn, shopeeOrderId, buyer, items,
                totalText, payment, status, statusDesc, cancelReason,
                channel, carrier, tracking
            });
        } catch (e) { /* card lạ — bỏ qua, không phá cả trang */ }
    }
    return JSON.stringify(out);
}";

        /// <summary>
        /// Parse JSON (chuỗi <see cref="ScanOrdersJs"/> trả về) → danh sách <see cref="SyncedOrder"/>. Bọc
        /// từng phần tử trong try (phần tử lạ không phá cả danh sách); đơn KHÔNG có mã (orderSn rỗng) bị BỎ.
        /// Tổng tiền parse qua <see cref="ShopeeShippingNav.ParseVndAmount"/> (bỏ mọi ký tự không phải số).
        /// </summary>
        internal static List<SyncedOrder> ParseOrdersJson(string? json)
        {
            var result = new List<SyncedOrder>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    try
                    {
                        var orderSn = GetJsonString(el, "orderSn");
                        if (string.IsNullOrWhiteSpace(orderSn))
                        {
                            continue; // không có mã đơn → không làm khóa được, bỏ
                        }

                        var itemsJson = "[]";
                        var itemCount = 0;
                        string? itemSummary = null;
                        string? sku = null;
                        if (el.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        {
                            itemsJson = items.GetRawText();
                            itemCount = items.GetArrayLength();
                            if (itemCount > 0)
                            {
                                itemSummary = NullIfBlank(GetJsonString(items[0], "name"));
                                sku = ShopeeShippingNav.ExtractSku(itemSummary);
                            }
                        }

                        var totalText = GetJsonString(el, "totalText");
                        result.Add(new SyncedOrder
                        {
                            OrderSn = orderSn,
                            ShopeeOrderId = NullIfBlank(GetJsonString(el, "shopeeOrderId")),
                            BuyerUsername = NullIfBlank(GetJsonString(el, "buyer")),
                            ItemsJson = itemsJson,
                            ItemCount = itemCount,
                            ItemSummary = itemSummary,
                            Sku = sku,
                            TotalPriceText = NullIfBlank(totalText),
                            TotalPrice = ShopeeShippingNav.ParseVndAmount(totalText),
                            PaymentMethod = NullIfBlank(GetJsonString(el, "payment")),
                            Status = NullIfBlank(GetJsonString(el, "status")),
                            StatusDescription = NullIfBlank(GetJsonString(el, "statusDesc")),
                            CancelReason = NullIfBlank(GetJsonString(el, "cancelReason")),
                            Channel = NullIfBlank(GetJsonString(el, "channel")),
                            Carrier = NullIfBlank(GetJsonString(el, "carrier")),
                            TrackingNumber = NullIfBlank(GetJsonString(el, "tracking")),
                        });
                    }
                    catch { /* phần tử lạ — bỏ qua, không phá cả danh sách */ }
                }
            }
            catch { /* JSON hỏng — trả những gì đã parse được */ }

            return result;
        }

        /// <summary>Đọc chuỗi từ property JSON (chỉ nhận String; thiếu / kiểu khác → rỗng).</summary>
        private static string GetJsonString(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;

        /// <summary>Rỗng/khoảng-trắng → null (để cột DB để NULL thay vì chuỗi rỗng).</summary>
        private static string? NullIfBlank(string? s)
            => string.IsNullOrWhiteSpace(s) ? null : s;

        // ===== Lấy "Số tiền cuối cùng" từ TRANG CHI TIẾT đơn (cột "Ước tính" ở màn Đơn hàng) =====

        // Prefix URL trang chi tiết đơn (/portal/sale/order/<id>) — dùng cho FALLBACK mở tab bằng
        // NewPageAsync+Goto khi click "Xem chi tiết" không bắt được tab mới (bước CHỈ-ĐỌC nên chấp nhận kém human).
        private const string OrderDetailUrlPrefix = "https://banhang.shopee.vn/portal/sale/order/";

        // JS CHỈ-ĐỌC lấy text "Số tiền cuối cùng" trên trang chi tiết: ưu tiên card [type='FinalAmount'] > .amount;
        // fallback tìm phần tử title KHỚP ĐÚNG "Số tiền cuối cùng" rồi lần lên tối đa 4 cấp cha tìm .amount (tránh
        // vơ nhầm .amount đầu trang khi attribute type đổi). Không thấy → chuỗi rỗng.
        private const string FinalAmountJs = @"() => {
    const norm = s => (s || '').replace(/\s+/g, ' ').trim();
    const card = document.querySelector(""[type='FinalAmount']"");
    if (card) {
        const amt = card.querySelector('.amount');
        if (amt) return norm(amt.textContent);
    }
    const nodes = document.querySelectorAll('div, span, p');
    for (const t of nodes) {
        if (norm(t.textContent) === 'Số tiền cuối cùng') {
            let p = t.parentElement;
            for (let up = 0; up < 4 && p; up++, p = p.parentElement) {
                const amt = p.querySelector('.amount');
                if (amt) return norm(amt.textContent);
            }
        }
    }
    return '';
}";

        /// <summary>True nếu trạng thái đơn là "Đã hủy" (chuẩn hóa khoảng trắng, KHÔNG phân biệt hoa/thường) —
        /// KHỚP ĐÚNG (loại "Đã hủy một phần"). Đơn "Đã hủy" KHÔNG mở chi tiết lấy "Số tiền cuối cùng".</summary>
        private static bool IsCancelledStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            var normalized = string.Join(' ', status.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return string.Equals(normalized, "Đã hủy", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True nếu trạng thái đơn là "Chuẩn bị (giao) hàng"/"Chờ lấy hàng" (đơn đang cần chuẩn bị/giao).
        /// CHỈ các đơn này mới mở CHI TIẾT lấy "Số tiền cuối cùng" — bỏ đơn Đã giao/Đang giao/Đã hủy/Hoàn thành...
        /// Ủy quyền cho <see cref="ShopeeShippingNav.LaChuanBiHang"/> (dùng chung với bộ lọc lưu đơn ở tầng App).</summary>
        private static bool IsPrepareToShipStatus(string? status)
            => ShopeeShippingNav.LaChuanBiHang(status);

        /// <summary>
        /// Lấy "Số tiền cuối cùng" cho DANH SÁCH đơn cần-lấy của trang hiện tại: mỗi đơn → định vị card theo mã →
        /// mở CHI TIẾT (ưu tiên click "Xem chi tiết" kiểu người CÓ HIT-TEST → BẮT tab mới; fallback
        /// <c>NewPageAsync</c>+<c>Goto</c> URL chi tiết) → đọc <c>.amount</c> → parse → gán vào object đơn → ĐÓNG
        /// đúng tab chi tiết vừa mở (KHÔNG đóng tab danh sách <paramref name="page"/>). Best-effort per-đơn (1 đơn
        /// lỗi KHÔNG phá lượt); <see cref="OperationCanceledException"/> ném XUYÊN. Log tiến trình mỗi ~5 đơn. Trả
        /// vị trí chuột mới (để giữ liền mạch chuỗi thao tác kiểu người của lượt sync).
        /// </summary>
        private async Task<(double X, double Y)> FetchFinalAmountsForPageAsync(
            IPage page, IReadOnlyList<SyncedOrder> targets, int pageNo,
            double mx, double my, Random rng, Action<string> L, CancellationToken ct)
        {
            var need = targets.Count;
            var done = 0;
            var got = 0;
            L($"Lấy số tiền cuối cùng: 0/{need} đơn (trang {pageNo})...");

            foreach (var order in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var card = await FindOrderCardBySnAsync(page, order.OrderSn, ct).ConfigureAwait(false);
                    var detailBtn = card is null
                        ? null
                        : await FindViewDetailButtonInCardAsync(card).ConfigureAwait(false);

                    // Chụp tập tab TRƯỚC khi click để nhận ĐÚNG tab mới (không lẫn tab danh sách / tab khác).
                    var before = _browser.Contexts.SelectMany(c => c.Pages).ToList();
                    IPage? detailPage = null;

                    // Cơ chế 1: click "Xem chi tiết" kiểu người (hit-test) → nút mở TAB MỚI → bắt tab (poll ≤8s).
                    if (detailBtn is not null)
                    {
                        bool clicked;
                        (mx, my, clicked) = await TryHumanClickVisibleAsync(page, detailBtn, mx, my, rng, ct).ConfigureAwait(false);
                        if (clicked)
                        {
                            var tabDeadline = DateTime.UtcNow.AddSeconds(8);
                            while (detailPage is null && DateTime.UtcNow < tabDeadline)
                            {
                                ct.ThrowIfCancellationRequested();
                                try { detailPage = _browser.Contexts.SelectMany(c => c.Pages).FirstOrDefault(p => p != page && !before.Contains(p)); }
                                catch { /* context ngắt — thử vòng sau */ }
                                if (detailPage is null)
                                {
                                    await Task.Delay(300, ct).ConfigureAwait(false);
                                }
                            }
                        }
                    }

                    // Cơ chế 2 (fallback): không bắt được tab từ click → mở tab CHỈ-ĐỌC bằng NewPageAsync + Goto.
                    if (detailPage is null && !string.IsNullOrEmpty(order.ShopeeOrderId))
                    {
                        try
                        {
                            detailPage = await _context.NewPageAsync().ConfigureAwait(false);
                            await detailPage.GotoAsync(OrderDetailUrlPrefix + order.ShopeeOrderId, new PageGotoOptions
                            {
                                WaitUntil = WaitUntilState.DOMContentLoaded,
                                Timeout = 30000
                            }).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { /* nuốt lỗi điều hướng — vẫn thử đọc DOM bên dưới (poll tự lo) */ }
                    }

                    if (detailPage is null)
                    {
                        L($"Không mở được chi tiết đơn {order.OrderSn} — bỏ qua số tiền cuối cùng.");
                    }
                    else
                    {
                        try
                        {
                            var (amount, amountText) = await ReadFinalAmountAsync(detailPage, ct).ConfigureAwait(false);
                            if (amount is not null || !string.IsNullOrEmpty(amountText))
                            {
                                order.FinalAmount = amount;
                                order.FinalAmountText = amountText;
                                got++;
                            }
                            else
                            {
                                L($"Chưa đọc được số tiền cuối cùng cho đơn {order.OrderSn}.");
                            }

                            // Chống bot: đọc xong ĐÓNG NGAY là dấu hiệu máy → dừng "đọc trang" kiểu người
                            // 3–5s ngẫu nhiên rồi mới đóng tab. (Hủy giữa chừng → OCE ném xuyên, finally vẫn đóng tab.)
                            await Task.Delay(rng.Next(3000, 5000), ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            // ĐÓNG đúng tab chi tiết vừa mở (click hoặc fallback) — tuyệt đối không đụng tab danh sách gốc.
                            try { await detailPage.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 1 đơn lỗi (selector đổi, tab treo...) KHÔNG phá cả lượt sync.
                    L($"Lỗi khi lấy số tiền cuối cùng đơn {order.OrderSn}: {ex.Message}");
                }

                done++;
                if (done % 5 == 0 && done < need)
                {
                    L($"Lấy số tiền cuối cùng: {done}/{need} đơn (trang {pageNo})...");
                }

                // Dừng ngẫu nhiên kiểu người giữa các đơn.
                await Task.Delay(rng.Next(400, 1200), ct).ConfigureAwait(false);
            }

            L($"Lấy số tiền cuối cùng xong trang {pageNo}: {got}/{need} đơn có số.");
            return (mx, my);
        }

        /// <summary>Định vị card đơn (<c>a[data-testid='order-item']</c>) có token cuối của <c>.order-sn</c> ==
        /// <paramref name="orderSn"/> trên trang danh sách. Không thấy → null. JS/DOM CHỈ ĐỌC.</summary>
        private static async Task<IElementHandle?> FindOrderCardBySnAsync(IPage page, string orderSn, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var cards = await page.QuerySelectorAllAsync("a[data-testid='order-item']").ConfigureAwait(false);
                foreach (var card in cards)
                {
                    var snEl = await card.QuerySelectorAsync(".order-sn").ConfigureAwait(false);
                    if (snEl is null)
                    {
                        continue;
                    }

                    var raw = await snEl.InnerTextAsync().ConfigureAwait(false);
                    var tokens = (raw ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var sn = tokens.Length > 0 ? tokens[^1] : string.Empty;
                    if (string.Equals(sn, orderSn, StringComparison.Ordinal))
                    {
                        return card;
                    }
                }
            }
            catch { /* selector chưa render / không hợp lệ */ }
            return null;
        }

        /// <summary>Tìm nút "Xem chi tiết" trong card đơn: ưu tiên button có TEXT "Xem chi tiết" (tiêu chí chắc
        /// nhất, không lẫn action khác), fallback <c>button[data-testid='action-button-1']</c>; chỉ nhận nút CÓ
        /// bounding box (đang hiển thị). Không thấy → null.</summary>
        private static async Task<IElementHandle?> FindViewDetailButtonInCardAsync(IElementHandle card)
        {
            try
            {
                var buttons = await card.QuerySelectorAllAsync("button").ConfigureAwait(false);
                foreach (var b in buttons)
                {
                    var t = await b.InnerTextAsync().ConfigureAwait(false);
                    if (IsViewDetailText(t) && await HasBoundingBoxAsync(b).ConfigureAwait(false))
                    {
                        return b;
                    }
                }

                var byId = await card.QuerySelectorAsync("button[data-testid='action-button-1']").ConfigureAwait(false);
                if (byId is not null && await HasBoundingBoxAsync(byId).ConfigureAwait(false))
                {
                    return byId;
                }
            }
            catch { /* card DOM lạ — bỏ qua */ }
            return null;
        }

        /// <summary>So khớp text nút với "Xem chi tiết" (chuẩn hóa khoảng trắng, KHÔNG phân biệt hoa/thường).</summary>
        private static bool IsViewDetailText(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return false;
            }

            var normalized = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return string.Equals(normalized, "Xem chi tiết", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Trên tab chi tiết: POLL ≤15s đọc "Số tiền cuối cùng" (JS CHỈ-ĐỌC <see cref="FinalAmountJs"/>);
        /// có text → parse qua <see cref="ShopeeShippingNav.ParseVndAmount"/> + giữ nguyên văn. Hết giờ → (null,
        /// null). <c>EvaluateAsync</c> ném (trang đang điều hướng) → nuốt, thử vòng sau; OCE ném xuyên.</summary>
        private static async Task<(long? Amount, string? Text)> ReadFinalAmountAsync(IPage detailPage, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            do
            {
                ct.ThrowIfCancellationRequested();

                string text;
                try { text = await detailPage.EvaluateAsync<string>(FinalAmountJs).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { text = string.Empty; }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    var raw = text.Trim();
                    return (ShopeeShippingNav.ParseVndAmount(raw), raw);
                }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return (null, null);
        }

        /// <summary>
        /// Chờ danh sách đơn render ỔN ĐỊNH: số card <c>a[data-testid='order-item']</c> &gt; 0 và ĐỨNG YÊN 2
        /// lượt poll liên tiếp (~400ms/lượt). Hết <paramref name="timeoutMs"/> vẫn chưa ổn định (kể cả shop 0
        /// đơn) → về (caller quét tiếp, thấy 0 đơn thì tự dừng). JS CHỈ-ĐỌC.
        /// </summary>
        private static async Task WaitOrderListReadyAsync(IPage page, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var lastCount = -1;
            var stableStreak = 0;
            do
            {
                ct.ThrowIfCancellationRequested();
                int count;
                try
                {
                    count = await page.EvaluateAsync<int>(
                        "() => document.querySelectorAll(\"a[data-testid='order-item']\").length").ConfigureAwait(false);
                }
                catch { count = 0; }

                if (count > 0)
                {
                    if (count == lastCount)
                    {
                        if (++stableStreak >= 2)
                        {
                            return; // ổn định 2 lượt liên tiếp → danh sách đã render xong
                        }
                    }
                    else
                    {
                        lastCount = count;
                        stableStreak = 0;
                    }
                }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);
        }

        /// <summary>Ký hiệu danh sách hiện tại = "&lt;số card&gt;|&lt;mã đơn card đầu&gt;" (JS CHỈ-ĐỌC). Dùng
        /// so sánh để phát hiện danh sách ĐỔI sau khi bấm trang sau. Lỗi → chuỗi rỗng.</summary>
        private static async Task<string> ReadListSignatureAsync(IPage page, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await page.EvaluateAsync<string>(@"() => {
    const cards = document.querySelectorAll(""a[data-testid='order-item']"");
    let first = '';
    if (cards.length > 0) {
        const sn = cards[0].querySelector('.order-sn');
        first = sn ? (sn.textContent || '').replace(/\s+/g, ' ').trim() : '';
    }
    return cards.length + '|' + first;
}").ConfigureAwait(false);
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Dò nút "trang sau" của phân trang (PHÒNG THỦ — chưa có DOM pager thật). Thử lần lượt các selector
        /// khả dĩ; nhận nút ĐẦU TIÊN có bounding box VÀ không disabled (<see cref="IsUsableNextButtonAsync"/>).
        /// Không thấy → <c>null</c> (caller coi là hết trang + log chẩn đoán).
        /// </summary>
        private static async Task<IElementHandle?> FindNextPageButtonAsync(IPage page, CancellationToken ct)
        {
            string[] selectors =
            {
                ".eds-pager button.eds-pager__button-next",
                "li.eds-pager__next button",
                "button[class*='next']",
                "[class*='pager'] button:last-of-type",
            };

            foreach (var sel in selectors)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var candidates = await page.QuerySelectorAllAsync(sel).ConfigureAwait(false);
                    foreach (var el in candidates)
                    {
                        if (await IsUsableNextButtonAsync(el).ConfigureAwait(false))
                        {
                            return el;
                        }
                    }
                }
                catch { /* selector không hợp lệ / chưa render — thử selector kế */ }
            }

            return null;
        }

        /// <summary>Nút "trang sau" DÙNG ĐƯỢC: có bounding box (đang hiển thị) VÀ KHÔNG disabled — kiểm cả
        /// <c>el.disabled</c>, <c>aria-disabled='true'</c>, và class chứa "disabled". Lỗi/handle stale → false.</summary>
        private static async Task<bool> IsUsableNextButtonAsync(IElementHandle el)
        {
            try
            {
                if (await el.BoundingBoxAsync().ConfigureAwait(false) is null)
                {
                    return false;
                }

                return await el.EvaluateAsync<bool>(@"el => {
    if (el.disabled) return false;
    if (el.getAttribute('aria-disabled') === 'true') return false;
    const cls = (el.getAttribute('class') || '').toLowerCase();
    if (cls.split(/\s+/).some(c => c.indexOf('disabled') >= 0)) return false;
    return true;
}").ConfigureAwait(false);
            }
            catch { return false; }
        }

        /// <summary>Chờ danh sách ĐỔI so với <paramref name="signatureBefore"/> (poll ~300ms tới hết
        /// <paramref name="timeoutMs"/>). Bỏ qua trạng thái đang tải (0 card) để không báo "đổi" nhầm lúc danh
        /// sách vừa bị gỡ. Đổi → true; hết giờ vẫn y nguyên → false (coi như hết trang).</summary>
        private static async Task<bool> WaitListChangedAsync(
            IPage page, string signatureBefore, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(300, ct).ConfigureAwait(false);

                var now = await ReadListSignatureAsync(page, ct).ConfigureAwait(false);
                if (now.StartsWith("0|", StringComparison.Ordinal))
                {
                    continue; // đang tải (chưa có card) — chờ danh sách mới ổn định rồi mới so
                }
                if (now.Length > 0 && now != signatureBefore)
                {
                    return true;
                }
            }
            while (DateTime.UtcNow < deadline);

            return false;
        }

        /// <summary>
        /// Log MỘT dòng chẩn đoán DOM pager khi không thấy nút "trang sau": đếm + liệt kê tag.class các phần tử
        /// khớp <c>[class*='pager'],[class*='pagination']</c> (cắt gọn ≤ 12 phần tử) — dữ liệu để tinh chỉnh
        /// selector sau lần chạy đầu. Best-effort, nuốt lỗi (OCE vẫn ném). JS CHỈ-ĐỌC.
        /// </summary>
        private static async Task LogPagerDiagnosticAsync(IPage page, Action<string> L, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                const string diagJs = @"() => {
    const els = document.querySelectorAll(""[class*='pager'],[class*='pagination']"");
    const parts = [];
    let i = 0;
    for (const el of els) {
        if (i++ >= 12) break;
        const c = (el.getAttribute('class') || '').trim().replace(/\s+/g, '.');
        parts.push(el.tagName.toLowerCase() + (c ? '.' + c : ''));
    }
    return els.length + ' phần tử: ' + (parts.join(' | ') || '(không có)');
}";
                var diag = await page.EvaluateAsync<string>(diagJs).ConfigureAwait(false);
                L("Chẩn đoán pager (không thấy nút trang sau): " + diag);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* chẩn đoán fail KHÔNG được phá luồng */ }
        }

        /// <summary>True nếu <paramref name="b"/> mở đầu bằng magic bytes <c>%PDF</c> (0x25 0x50 0x44 0x46) —
        /// nhận đúng file PDF thật, tránh lưu HTML/redirect đăng nhập thành <c>.pdf</c> rác.</summary>
        private static bool LooksPdf(byte[]? b)
            => b is { Length: > 4 } && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46;

        /// <summary>Ngưỡng bytes tối thiểu để coi một PDF render (<c>printToPDF</c>) là "có nội dung thật":
        /// nhỏ hơn ngưỡng này gần như chắc là bản TRẮNG (phiếu nằm trong khung nhúng mà <c>printToPDF</c> của
        /// trang bọc không vẽ được) → KHÔNG tính là đã có PDF thật (bỏ bản render, thử fallback GET URL).</summary>
        private const int MinRealSlipPdfBytes = 3000;

        /// <summary>
        /// Trên TAB PHIẾU (trang HTML "Xem trước bản in"): CHỜ nội dung phiếu thật sự hiện → lấy <b>PDF</b> về
        /// <paramref name="downloadDir"/> → ĐÓNG tab. <b>KHÔNG gửi lệnh in nào</b> (bỏ theo yêu cầu — lưu phiếu
        /// để in sau). <b>Best-effort có log</b>: mọi lỗi chỉ cảnh báo, KHÔNG ném (đơn đã được arrange). Mọi thao
        /// tác trên tab CHỈ ĐỌC DOM (đếm iframe/embed/object + ảnh, printToPDF) — KHÔNG click, KHÔNG vá/hook JS
        /// (quy tắc stealth). PDF thử lần lượt: (e0) nếu khung nhúng là <c>blob:</c> URL (phiếu PDF GỐC Shopee
        /// tạo, nhúng qua trình xem PDF) → <c>fetch</c> blob NGAY trong trang → base64 → giải mã (%PDF-check) —
        /// ĐÂY là file in chuẩn nhất (chỉ phiếu, đúng khổ); (e1) GET src khung nhúng đầu tiên (http[s]) qua
        /// <c>APIRequest</c> — %PDF/content-type check; (e2) CDP <c>Page.printToPDF</c> (<see cref="LooksPdf"/>-check
        /// + ngưỡng <see cref="MinRealSlipPdfBytes"/> chống bản trắng); (e3) GET page-URL fallback. Coi là "đã lưu
        /// phiếu" khi có PDF ≥ ngưỡng.
        /// </summary>
        private async Task SaveSlipAsync(
            IPage newPage, string downloadDir, string orderCode, Action<string> L, CancellationToken ct)
        {
            var rng = new Random();

            // Đọc int an toàn từ 1 property JSON (Number) — mọi giá trị lạ/thiếu → 0 (không ném).
            static int ReadInt(JsonElement e, string prop)
                => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
                    ? (int)v.GetDouble() : 0;

            // a. Chờ load (best-effort; nuốt timeout, OperationCanceledException vẫn ném để dừng sạch).
            try { await newPage.WaitForLoadStateAsync(LoadState.Load,
                new PageWaitForLoadStateOptions { Timeout = 10_000 }).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { /* tab phiếu có thể vẫn đang tải — vẫn thử tải PDF bên dưới */ }

            string url;
            try { url = newPage.Url; } catch { url = string.Empty; }
            L($"Tab phiếu: {url}");

            // Đặt tên file: mã đơn > job_id trong URL > "phieu".
            var jobId = ShopeeShippingNav.ExtractJobId(url);
            var baseName = !string.IsNullOrEmpty(orderCode) ? orderCode
                         : (!string.IsNullOrEmpty(jobId) ? jobId : "phieu");
            var safeName = ShopeeShippingNav.SanitizeFileName(baseName);

            string dir;
            try
            {
                Directory.CreateDirectory(downloadDir);
                dir = downloadDir;
            }
            catch (Exception ex)
            {
                L("Cảnh báo: không tạo được thư mục lưu phiếu: " + ex.Message);
                dir = string.Empty;
            }

            // b. Chờ nội dung phiếu (poll ≤ 25s, bước 500–800ms). Eval CHỈ ĐỌC DOM (không vá/hook — quy tắc
            //    stealth): đếm iframe/embed/object + src NGUYÊN VẸN (để GET PDF thật ở e1), đếm ảnh đã complete
            //    kích thước > 0. Trả JSON string (JSON.stringify) để C# tự parse — "Sẵn sàng" khi có ≥ 1 khung
            //    nhúng HOẶC ≥ 1 ảnh. (Việc cắt ≤ 120 ký tự chỉ làm ở C# khi ghép DÒNG LOG chẩn đoán.)
            const string probeJs = @"() => {
                const val = s => s || '';
                const frames = Array.from(document.querySelectorAll('iframe')).map(e => val(e.src));
                const embeds = Array.from(document.querySelectorAll('embed')).map(e => val(e.src));
                const objects = Array.from(document.querySelectorAll('object')).map(e => val(e.data));
                let imgReady = 0;
                for (const im of document.images) {
                    if (im.complete && im.naturalWidth > 0 && im.naturalHeight > 0) imgReady++;
                }
                return JSON.stringify({
                    iframe: frames.length, embed: embeds.length, object: objects.length,
                    srcs: frames.concat(embeds).concat(objects).filter(s => s), imgReady: imgReady
                });
            }";

            int iframeCount = 0, embedCount = 0, objectCount = 0, imgReady = 0;
            var embedSrcs = new List<string>();
            var contentDeadline = DateTime.UtcNow.AddSeconds(25);
            bool contentReady = false;
            while (DateTime.UtcNow < contentDeadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var json = await newPage.EvaluateAsync<string>(probeJs).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var probe = doc.RootElement;
                    iframeCount = ReadInt(probe, "iframe");
                    embedCount = ReadInt(probe, "embed");
                    objectCount = ReadInt(probe, "object");
                    imgReady = ReadInt(probe, "imgReady");
                    embedSrcs.Clear();
                    if (probe.TryGetProperty("srcs", out var ps) && ps.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in ps.EnumerateArray())
                        {
                            var v = s.GetString();
                            if (!string.IsNullOrEmpty(v)) embedSrcs.Add(v);
                        }
                    }
                    if (iframeCount + embedCount + objectCount >= 1 || imgReady >= 1)
                    {
                        contentReady = true;
                        break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* trang chưa cho eval — thử vòng sau */ }

                await Task.Delay(rng.Next(500, 800), ct).ConfigureAwait(false);
            }

            if (contentReady)
            {
                // Cho khung nhúng kịp vẽ trước khi lấy PDF.
                await Task.Delay(rng.Next(1500, 2500), ct).ConfigureAwait(false);
            }
            else
            {
                L("Không thấy dấu hiệu nội dung phiếu sau 25s — PDF render có thể trắng.");
            }

            // c. Src http(s) ĐẦU TIÊN của khung nhúng (bản ĐẦY ĐỦ để GET PDF thật ở e1) + MỘT dòng chẩn đoán
            //    DOM (dữ liệu tinh chỉnh vòng sau nếu smoke vẫn trắng). Src trong log cắt ≤ 120 ký tự cho gọn.
            var firstHttpSrc = embedSrcs.FirstOrDefault(s =>
                s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            // Src blob ĐẦU TIÊN (bản ĐẦY ĐỦ) — Shopee nhúng phiếu PDF GỐC qua blob: URL (trình xem PDF). Lấy ở
            // e0 (fetch NGAY trong trang) TRƯỚC http/render vì blob này CHÍNH là file in chuẩn (chỉ phiếu, đúng khổ).
            var firstBlobSrc = embedSrcs.FirstOrDefault(s =>
                s.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            var logSrc = firstHttpSrc.Length > 0 ? firstHttpSrc : (embedSrcs.Count > 0 ? embedSrcs[0] : string.Empty);
            if (logSrc.Length > 120) logSrc = logSrc.Substring(0, 120);
            var srcInfo = logSrc.Length > 0 ? $" (src={logSrc})" : string.Empty;
            L($"Tab phiếu DOM: {iframeCount} iframe{srcInfo}, {embedCount} embed, {objectCount} object, {imgReady} ảnh.");

            // Sau vòng chờ nội dung: tab có thể đã điều hướng từ about:blank sang awbprint → đọc lại URL để e3
            // GET đúng trang phiếu (không GET "about:blank"). Đọc lỗi → giữ url cũ.
            try { var u2 = newPage.Url; if (!string.IsNullOrEmpty(u2)) url = u2; } catch { /* giữ url cũ */ }

            // e. Lấy PDF (artifact DUY NHẤT, best-effort) — thử lần lượt tới khi được.
            bool pdfReal = false;
            if (dir.Length > 0)
            {
                var pdfPath = Path.Combine(dir, safeName + ".pdf");

                // e0. Nếu khung nhúng là PDF GỐC Shopee tạo (iframe src = blob:...#toolbar=0...): fetch blob NGAY
                //     TRONG trang (đọc tài nguyên cùng trang — KHÔNG vá/hook) → arrayBuffer → base64 (chunk nhỏ
                //     tránh tràn call stack btoa) → C# giải mã. Đây là FILE PDF GỐC (chỉ phiếu, đúng khổ in) →
                //     ƯU TIÊN số 1. blob đã revoke / CSP chặn → JS trả rỗng → rơi xuống e1/e2/e3.
                if (firstBlobSrc.Length > 0)
                {
                    // Cắt fragment (#toolbar=0&navpanes=0) — fetch theo đúng object URL, không mang fragment.
                    var blobUrl = firstBlobSrc;
                    int hashIdx = blobUrl.IndexOf('#');
                    if (hashIdx >= 0) blobUrl = blobUrl.Substring(0, hashIdx);

                    const string fetchBlobJs = @"async (url) => {
                        try {
                            const resp = await fetch(url);
                            const buf = await resp.arrayBuffer();
                            const bytes = new Uint8Array(buf);
                            let bin = '';
                            const chunk = 0x8000;
                            for (let i = 0; i < bytes.length; i += chunk) {
                                bin += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
                            }
                            return btoa(bin);
                        } catch (e) {
                            return '';
                        }
                    }";
                    try
                    {
                        var b64 = await newPage.EvaluateAsync<string>(fetchBlobJs, blobUrl).ConfigureAwait(false);
                        byte[]? body = null;
                        if (!string.IsNullOrEmpty(b64))
                        {
                            try { body = Convert.FromBase64String(b64); } catch { body = null; }
                        }
                        if (LooksPdf(body))
                        {
                            await File.WriteAllBytesAsync(pdfPath, body!, ct).ConfigureAwait(false);
                            pdfReal = true;
                            L($"Đã tải phiếu PDF GỐC (blob): {pdfPath} ({body!.Length} bytes).");
                        }
                        else
                        {
                            L("Fetch blob KHÔNG phải PDF / rỗng (revoke/CSP?) — bỏ, thử GET src/render.");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        L("Fetch blob chưa được: " + ex.Message);
                    }
                }

                // e1. GET src khung nhúng http(s) ĐẦU TIÊN qua context đã đăng nhập. Token phiếu dùng 1 lần →
                //     GET đúng MỘT lần; CHỈ ghi khi content-type "pdf" HOẶC magic %PDF (không lưu rác).
                if (!pdfReal && firstHttpSrc.Length > 0)
                {
                    try
                    {
                        var resp = await newPage.APIRequest.GetAsync(firstHttpSrc).ConfigureAwait(false);
                        if (resp.Ok)
                        {
                            var body = await resp.BodyAsync().ConfigureAwait(false);
                            var isPdf = body is { Length: > 0 }
                                && ((resp.Headers.TryGetValue("content-type", out var ctype)
                                        && ctype.Contains("pdf", StringComparison.OrdinalIgnoreCase))
                                    || LooksPdf(body));
                            if (isPdf)
                            {
                                await File.WriteAllBytesAsync(pdfPath, body!, ct).ConfigureAwait(false);
                                pdfReal = true;
                                L($"Đã tải phiếu PDF thật (src khung nhúng): {pdfPath} ({body!.Length} bytes).");
                            }
                            else
                            {
                                L("GET src khung nhúng KHÔNG phải PDF — bỏ, thử render PDF.");
                            }
                        }
                        else
                        {
                            L($"GET src khung nhúng trả HTTP {resp.Status} — bỏ, thử render PDF.");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        L("GET src khung nhúng chưa được: " + ex.Message);
                    }
                }

                // e2. CDP Page.printToPDF (render HTML → PDF), có %PDF-check + ngưỡng chống bản trắng.
                if (!pdfReal)
                {
                    try
                    {
                        var cdp = await _context.NewCDPSessionAsync(newPage).ConfigureAwait(false);
                        var res = await cdp.SendAsync("Page.printToPDF", new Dictionary<string, object>
                        {
                            ["printBackground"] = true,
                            ["preferCSSPageSize"] = true,
                        }).ConfigureAwait(false);

                        // SendAsync trả JsonElement? (null nếu lệnh không có payload) → lấy "data" (base64) an toàn.
                        string? data = res is JsonElement je && je.TryGetProperty("data", out var d)
                            ? d.GetString()
                            : null;
                        if (!string.IsNullOrEmpty(data))
                        {
                            var bytes = Convert.FromBase64String(data);
                            if (LooksPdf(bytes))
                            {
                                await File.WriteAllBytesAsync(pdfPath, bytes, ct).ConfigureAwait(false);
                                if (bytes.Length >= MinRealSlipPdfBytes)
                                {
                                    pdfReal = true;
                                    L($"Đã tải phiếu (render PDF): {pdfPath} ({bytes.Length} bytes).");
                                }
                                else
                                {
                                    L($"PDF render nghi TRẮNG ({bytes.Length} bytes) — bỏ, thử GET URL.");
                                }
                            }
                            else
                            {
                                L("Render PDF trả dữ liệu KHÔNG phải PDF — bỏ, thử GET URL.");
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        L("Render PDF chưa được: " + ex.Message);
                    }
                }

                // e3. Fallback GET page-URL qua context đã đăng nhập (%PDF/content-type check).
                if (!pdfReal && url.Length > 0)
                {
                    try
                    {
                        var resp = await newPage.APIRequest.GetAsync(url).ConfigureAwait(false);
                        if (resp.Ok)
                        {
                            var body = await resp.BodyAsync().ConfigureAwait(false);
                            var isPdf = body is { Length: > 0 }
                                && ((resp.Headers.TryGetValue("content-type", out var ctype)
                                        && ctype.Contains("pdf", StringComparison.OrdinalIgnoreCase))
                                    || LooksPdf(body));
                            if (isPdf)
                            {
                                await File.WriteAllBytesAsync(pdfPath, body!, ct).ConfigureAwait(false);
                                pdfReal = true;
                                L($"Đã tải phiếu (GET URL fallback): {pdfPath} ({body!.Length} bytes).");
                            }
                            else
                            {
                                L("GET URL trả nội dung KHÔNG phải PDF (HTML/redirect) — bỏ.");
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        L("Tải qua GET URL chưa được: " + ex.Message);
                    }
                }
            }

            // f. Tổng kết: "đã lưu phiếu" khi có PDF ≥ ngưỡng.
            if (!pdfReal)
            {
                L("Cảnh báo: CHƯA lưu được phiếu PDF — kiểm tra tay trong Brave.");
            }

            // ===== ĐÓNG tab phiếu (KHÔNG gửi lệnh in nào) =====
            try
            {
                await newPage.CloseAsync().ConfigureAwait(false);
                L("Đã đóng tab phiếu.");
            }
            catch (Exception ex)
            {
                L("Đóng tab phiếu chưa được: " + ex.Message);
            }
        }

        /// <summary>
        /// Đóng modal "Thông Tin Chi Tiết" bằng nút <b>X góc phải</b> (modal KHÔNG tự đóng sau khi in) rồi
        /// trang trở về danh sách "Tất cả". <b>Best-effort, KHÔNG ném:</b> re-find modal TƯƠI — đã đóng →
        /// thôi; tìm nút X (nhiều selector, chỉ nhận có bounding box) → click KIỂU NGƯỜI verified → chờ modal
        /// biến mất ~5s. Không tìm/không đóng được → thử <c>Escape</c> MỘT lần (đóng modal, KHÔNG phải click
        /// nghiệp vụ chuột) rồi kiểm lại; vẫn còn → L cảnh báo (KHÔNG hạ Ok — đơn đã arrange/in).
        /// </summary>
        private static async Task CloseDetailModalAsync(IPage page, Random rng, Action<string> L, CancellationToken ct)
        {
            try
            {
                var modal = await FindModalByTitleAsync(page, ShopeeShippingNav.IsDetailModalTitle).ConfigureAwait(false);
                if (modal is null)
                {
                    return; // đã đóng
                }

                // mx/my riêng (viewport page) để chuột cong hợp lệ.
                var vp = page.ViewportSize;
                double mx = (vp is not null ? vp.Width : 1280) * rng.NextDouble();
                double my = (vp is not null ? vp.Height : 720) * rng.NextDouble();

                var closeBtn = await FindDetailModalCloseButtonAsync(modal).ConfigureAwait(false);
                if (closeBtn is not null)
                {
                    await TryHumanClickVisibleAsync(page, closeBtn, mx, my, rng, ct).ConfigureAwait(false);
                    if (await WaitModalGoneAsync(page, ShopeeShippingNav.IsDetailModalTitle, 5000, ct).ConfigureAwait(false))
                    {
                        return;
                    }
                }

                // Fallback: Escape MỘT lần rồi kiểm lại.
                try { await page.Keyboard.PressAsync("Escape").ConfigureAwait(false); } catch { /* bỏ qua */ }
                if (await WaitModalGoneAsync(page, ShopeeShippingNav.IsDetailModalTitle, 3000, ct).ConfigureAwait(false))
                {
                    return;
                }

                L("Không đóng được phiếu chi tiết — modal có thể còn mở, kiểm tra tay.");
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort — nuốt lỗi (context ngắt) */ }
        }

        /// <summary>
        /// Dò nút X đóng trong modal "Thông Tin Chi Tiết" (thử lần lượt, chỉ nhận cái có bounding box):
        /// (a) <c>.eds-modal__close</c>; (b) icon trong <c>.eds-modal__header</c> (<c>.eds-icon</c>/<c>i</c>/
        /// <c>svg</c>); (c) <c>[aria-label='Close'|'close'|'Đóng']</c>. Không thấy → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> FindDetailModalCloseButtonAsync(IElementHandle modal)
        {
            string[] selectors =
            {
                ".eds-modal__close",
                ".eds-modal__header .eds-icon",
                ".eds-modal__header i",
                ".eds-modal__header svg",
                "[aria-label='Close']",
                "[aria-label='close']",
                "[aria-label='Đóng']",
            };

            foreach (var sel in selectors)
            {
                try
                {
                    var el = await modal.QuerySelectorAsync(sel).ConfigureAwait(false);
                    if (el is not null && await el.BoundingBoxAsync().ConfigureAwait(false) is not null)
                    {
                        return el;
                    }
                }
                catch { /* selector không hợp lệ / detached — thử selector kế */ }
            }

            return null;
        }

        /// <summary>Chờ modal có tiêu đề khớp <paramref name="titleMatch"/> BIẾN MẤT (đóng), poll tới hết
        /// <paramref name="timeoutMs"/>. Đóng → <c>true</c>; hết giờ (còn) → <c>false</c>.</summary>
        private static async Task<bool> WaitModalGoneAsync(
            IPage page, Func<string?, bool> titleMatch, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                if (await FindModalByTitleAsync(page, titleMatch).ConfigureAwait(false) is null)
                {
                    return true;
                }

                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return false;
        }

        /// <summary>
        /// Điều hướng về danh sách đơn "Tất cả" (<c>/portal/sale/order</c>) KIỂU NGƯỜI CÓ HIT-TEST: tìm link
        /// "Tất cả" trong menu trái (nhóm "Quản Lý Đơn Hàng") → nếu submenu đang cụp thì click mục cha để
        /// bung → click link verified → chờ URL. Fallback cuối: <see cref="IPage.GotoAsync"/>. Trả về vị trí
        /// chuột mới. Kết quả (tới được trang hay chưa) người gọi tự kiểm qua <c>page.Url</c>.
        /// </summary>
        private static async Task<(double X, double Y)> GoToAllOrdersAsync(
            IPage page, double mx, double my, Random rng, CancellationToken ct)
        {
            bool parentClicked = false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var link = await FindAllOrdersLinkAsync(page, 8000, rng, ct).ConfigureAwait(false);
                if (link is null)
                {
                    // Submenu có thể đang cụp → click mục cha "Quản Lý Đơn Hàng" MỘT lần để bung rồi tìm lại.
                    if (parentClicked)
                    {
                        break;
                    }
                    var parent = await FindOrderMenuParentAsync(page, ct).ConfigureAwait(false);
                    if (parent is null)
                    {
                        break;
                    }
                    (mx, my, _) = await HumanMoveAndClickVerifiedAsync(page, parent, mx, my, rng, ct).ConfigureAwait(false);
                    parentClicked = true;
                    await Task.Delay(rng.Next(500, 1500), ct).ConfigureAwait(false);
                    continue;
                }

                bool clicked;
                (mx, my, clicked) = await HumanMoveAndClickVerifiedAsync(page, link, mx, my, rng, ct).ConfigureAwait(false);
                if (clicked && await WaitAllOrdersPageAsync(page, 15000, ct).ConfigureAwait(false))
                {
                    return (mx, my);
                }
                break;
            }

            // Fallback cuối (kém human hơn — hiếm khi tới nếu click kiểu người đã ăn).
            try
            {
                await page.GotoAsync(AllOrdersUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30000
                }).ConfigureAwait(false);
            }
            catch { /* nuốt lỗi điều hướng — người gọi kiểm page.Url */ }

            await WaitAllOrdersPageAsync(page, 15000, ct).ConfigureAwait(false);
            return (mx, my);
        }

        /// <summary>
        /// Đảm bảo trang danh sách đơn đang ở tab "Chờ lấy hàng" (BEST-EFFORT — thêm 1 click). Giữ NGUYÊN
        /// chữ ký &amp; hành vi cũ của luồng Xử lý đơn: gọi <see cref="EnsureOrderListTabAsync"/> với testid
        /// <c>l1-tab-toship</c>, text khớp <see cref="ShopeeShippingNav.IsToShipTabText"/>, nhãn log "Chờ lấy
        /// hàng".
        /// </summary>
        private static Task<(double X, double Y)> EnsureToShipTabAsync(
            IPage page, double mx, double my, Random rng, Action<string> L, CancellationToken ct)
            => EnsureOrderListTabAsync(
                page, "l1-tab-toship", ShopeeShippingNav.IsToShipTabText, "Chờ lấy hàng", mx, my, rng, L, ct);

        /// <summary>
        /// Đảm bảo thanh tab của trang danh sách đơn đang ở tab <paramref name="tabLabel"/> (BEST-EFFORT —
        /// thêm 1 click). Reload/goto đưa Shopee về tab mặc định → đơn cần xử lý có thể bị đẩy khỏi trang 1
        /// khi shop đông đơn → cần click đúng tab trước khi quét. MỌI nhánh fail chỉ LOG rồi ĐI TIẾP (quét
        /// tab hiện tại) — KHÔNG ném; chỉ hủy (OperationCanceledException) mới ném xuyên. Re-query phần tử
        /// tab TƯƠI mỗi lượt poll (thanh tab re-render khi chuyển tab — chống stale handle); đọc trạng thái
        /// active bằng evaluate CHỈ-ĐỌC; đã active → về ngay KHÔNG click (Shopee đôi khi nhớ tab). Chưa active
        /// → click kiểu người (hit-test) rồi chờ active + settle danh sách vẽ lại. Trả vị trí chuột mới.
        /// </summary>
        /// <param name="tabTestId">Giá trị <c>data-testid</c> của tab (khóa chính, vd <c>l1-tab-toship</c>/<c>l1-tab-all</c>).</param>
        /// <param name="textMatch">So khớp InnerText của <c>.tab-label</c> (fallback khi không có testid).</param>
        /// <param name="tabLabel">Nhãn tab dùng cho log ("Chờ lấy hàng"/"Tất cả").</param>
        private static async Task<(double X, double Y)> EnsureOrderListTabAsync(
            IPage page, string tabTestId, Func<string?, bool> textMatch, string tabLabel,
            double mx, double my, Random rng, Action<string> L, CancellationToken ct)
        {
            // 1) Tìm phần tử tab (poll ≤ 10s, ~400ms/lượt, RE-QUERY tươi mỗi lượt — không giữ handle qua
            //    lượt). testid là khóa chính; text là fallback.
            IElementHandle? tabEl = null;
            var findDeadline = DateTime.UtcNow.AddMilliseconds(10000);
            do
            {
                ct.ThrowIfCancellationRequested();
                tabEl = await FindOrderListTabAsync(page, tabTestId, textMatch, ct).ConfigureAwait(false);
                if (tabEl is not null)
                {
                    break;
                }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < findDeadline);

            if (tabEl is null)
            {
                L($"Không thấy tab {tabLabel} — quét tab hiện tại.");
                return (mx, my);
            }

            // 2) Đã active? (evaluate CHỈ-ĐỌC) → về ngay, không log, không click.
            if (await IsTabActiveAsync(tabEl).ConfigureAwait(false))
            {
                return (mx, my);
            }

            // 3) Chưa active → click kiểu người (hit-test). Click không được → cảnh báo, vẫn đi tiếp.
            bool clicked;
            (mx, my, clicked) = await TryHumanClickVisibleAsync(page, tabEl, mx, my, rng, ct).ConfigureAwait(false);
            if (!clicked)
            {
                L($"Chưa chuyển được sang tab {tabLabel} — quét tab hiện tại.");
                return (mx, my);
            }

            L($"Chuyển sang tab {tabLabel}.");

            // 4) Chờ tab active ≤ 5s (~300ms/lượt, re-query TƯƠI + đọc lại class mỗi lượt — thanh tab
            //    re-render khi chuyển tab). Active → chờ settle 800–1500ms (danh sách vẽ lại) rồi về. Hết 5s
            //    chưa active → cảnh báo, vẫn đi tiếp (quét tab hiện tại).
            var activeDeadline = DateTime.UtcNow.AddMilliseconds(5000);
            do
            {
                ct.ThrowIfCancellationRequested();
                var fresh = await FindOrderListTabAsync(page, tabTestId, textMatch, ct).ConfigureAwait(false);
                if (fresh is not null && await IsTabActiveAsync(fresh).ConfigureAwait(false))
                {
                    await Task.Delay(rng.Next(800, 1500), ct).ConfigureAwait(false);
                    return (mx, my);
                }

                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < activeDeadline);

            L($"Chưa chuyển được sang tab {tabLabel} — quét tab hiện tại.");
            return (mx, my);
        }

        /// <summary>
        /// Dò phần tử tab trên thanh tab danh sách đơn (MỘT lần, query TƯƠI — poll do người gọi). Ưu tiên
        /// <c>[data-testid='{tabTestId}']</c> (khóa chính, từ DOM thật); fallback duyệt mọi <c>.tab-label</c>
        /// có InnerText khớp <paramref name="textMatch"/>. Chỉ nhận phần tử đang hiển thị (có bounding box).
        /// Không thấy → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> FindOrderListTabAsync(
            IPage page, string tabTestId, Func<string?, bool> textMatch, CancellationToken ct)
        {
            var el = await FirstVisibleByBoxAsync(page, $"[data-testid='{tabTestId}']", ct).ConfigureAwait(false);
            if (el is not null)
            {
                return el;
            }

            try
            {
                var labels = await page.QuerySelectorAllAsync(".tab-label").ConfigureAwait(false);
                foreach (var lb in labels)
                {
                    if (textMatch(await lb.InnerTextAsync().ConfigureAwait(false))
                        && await lb.BoundingBoxAsync().ConfigureAwait(false) is not null)
                    {
                        return lb;
                    }
                }
            }
            catch { /* chưa render / selector không hợp lệ — người gọi thử vòng sau */ }

            return null;
        }

        /// <summary>Tab đang active? Evaluate CHỈ-ĐỌC: phần tử tổ tiên gần nhất
        /// <c>.eds-tabs__nav-tab</c> có class <c>active</c>. Lỗi / handle detached → <c>false</c>.</summary>
        private static async Task<bool> IsTabActiveAsync(IElementHandle tabEl)
        {
            try
            {
                return await tabEl.EvaluateAsync<bool>(
                    "el => { const t = el.closest('.eds-tabs__nav-tab'); return !!t && t.classList.contains('active'); }")
                    .ConfigureAwait(false);
            }
            catch { return false; }
        }

        /// <summary>
        /// Dò link "Tất cả" (danh sách đơn) trong menu trái, poll tới hết <paramref name="timeoutMs"/>. Thử:
        /// (a) <c>a[test-id='my orders new'][href*='/portal/sale/order']</c>; (b) duyệt mọi
        /// <c>a.sidebar-submenu-item-link</c> có href là <c>/portal/sale/order</c> và text khớp
        /// <see cref="ShopeeShippingNav.IsAllOrdersText"/>. Chỉ nhận element đang hiển thị. Không thấy → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> FindAllOrdersLinkAsync(
            IPage page, int timeoutMs, Random rng, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();

                var el = await FirstVisibleByBoxAsync(
                             page, "a[test-id='my orders new'][href*='/portal/sale/order']", ct).ConfigureAwait(false);
                if (el is not null)
                {
                    return el;
                }

                try
                {
                    var links = await page.QuerySelectorAllAsync("a.sidebar-submenu-item-link").ConfigureAwait(false);
                    foreach (var a in links)
                    {
                        var href = await a.GetAttributeAsync("href").ConfigureAwait(false);
                        if (!ShopeeShippingNav.IsAllOrdersHref(href))
                        {
                            continue;
                        }
                        if (ShopeeShippingNav.IsAllOrdersText(await a.InnerTextAsync().ConfigureAwait(false))
                            && await a.BoundingBoxAsync().ConfigureAwait(false) is not null)
                        {
                            return a;
                        }
                    }
                }
                catch { /* chưa render / selector không hợp lệ — thử vòng sau */ }

                await Task.Delay(rng.Next(300, 501), ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>Chờ trang danh sách đơn "Tất cả" mở (URL khớp <see cref="ShopeeShippingNav.IsAllOrdersHref"/>),
        /// poll tới hết <paramref name="timeoutMs"/>. Hết giờ → false.</summary>
        private static async Task<bool> WaitAllOrdersPageAsync(IPage page, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (ShopeeShippingNav.IsAllOrdersHref(page.Url))
                    {
                        return true;
                    }
                }
                catch { /* điều hướng dở — thử vòng sau */ }

                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return false;
        }

        /// <summary>
        /// QUÉT TẤT CẢ đơn trong danh sách "Tất cả" → trả đơn ĐẦU TIÊN CÓ nút "Chuẩn bị hàng" đang hiển thị
        /// (kèm nút đó + mã đơn), poll tới hết <paramref name="timeoutMs"/> (danh sách render dần). Lấy toàn
        /// bộ card bằng selector đầu tiên ra &gt;0 phần tử (<c>[data-testid='order-item']</c> → <c>a.order-card</c>
        /// → <c>.order-card</c>). Nhận diện đơn cần xử lý bằng <b>TEXT nút</b> ("chuẩn bị hàng" qua
        /// <see cref="ShopeeShippingNav.IsPrepareOrderButtonText"/>) — KHÔNG dựa <c>data-testid</c> theo vị trí
        /// (đơn trạng thái khác có thể có nút KHÁC ở cùng vị trí). Chỉ nhận nút có bounding box. Log số đơn
        /// thấy được (giúp smoke biết app có thấy đơn không). Danh sách rỗng / không đơn nào có "Chuẩn bị
        /// hàng" → <c>(null, null, "")</c>.
        /// </summary>
        private static async Task<(IElementHandle? Card, IElementHandle? PrepareBtn, string OrderCode)>
            FindFirstProcessableOrderAsync(IPage page, int timeoutMs, Action<string> L, CancellationToken ct)
        {
            string[] cardSelectors = { "[data-testid='order-item']", "a.order-card", ".order-card" };
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            int lastCount = -1;
            do
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Lấy TẤT CẢ card đơn (dùng selector đầu tiên ra >0 phần tử).
                    IReadOnlyList<IElementHandle> cards = System.Array.Empty<IElementHandle>();
                    foreach (var sel in cardSelectors)
                    {
                        var found = await page.QuerySelectorAllAsync(sel).ConfigureAwait(false);
                        if (found.Count > 0)
                        {
                            cards = found;
                            break;
                        }
                    }
                    if (cards.Count != lastCount)
                    {
                        L($"Thấy {cards.Count} đơn trong danh sách.");
                        lastCount = cards.Count;
                    }

                    // Duyệt: đơn ĐẦU TIÊN có nút TEXT "chuẩn bị hàng" (chỉ nhận nút có bounding box).
                    foreach (var card in cards)
                    {
                        var btn = await FindButtonByTextAsync(card, ShopeeShippingNav.IsPrepareOrderButtonText).ConfigureAwait(false);
                        if (btn is not null && await btn.BoundingBoxAsync().ConfigureAwait(false) is not null)
                        {
                            var code = ShopeeShippingNav.ExtractOrderCode(await ReadOrderSnAsync(card).ConfigureAwait(false));
                            return (card, btn, code);
                        }
                    }
                }
                catch { /* chưa render / selector không hợp lệ — thử vòng sau */ }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return (null, null, string.Empty);
        }

        /// <summary>Đọc InnerText của ô <c>.order-sn</c> trong card (để trích mã đơn). Không có → <c>null</c>.</summary>
        private static async Task<string?> ReadOrderSnAsync(IElementHandle card)
        {
            try
            {
                var sn = await card.QuerySelectorAsync(".order-sn").ConfigureAwait(false);
                if (sn is not null)
                {
                    return await sn.InnerTextAsync().ConfigureAwait(false);
                }
            }
            catch { /* bỏ qua */ }

            return null;
        }

        /// <summary>
        /// Dò option "Tôi sẽ tự mang hàng tới Bưu cục" trong modal "Giao Đơn Hàng":
        /// (a) <c>[data-testid='dropoff-option']</c>; (b) fallback phần tử con có text khớp
        /// <see cref="ShopeeShippingNav.IsDropoffTitleText"/>. Không thấy → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> FindDropoffOptionAsync(IElementHandle modal)
        {
            try
            {
                var opt = await modal.QuerySelectorAsync("[data-testid='dropoff-option']").ConfigureAwait(false);
                if (opt is not null)
                {
                    return opt;
                }

                // Fallback: CHỈ duyệt CARD dropoff (class thật "dropoff-method-card selected card") — KHÔNG
                // duyệt div/label/li chung, vì phần tử TỔ TIÊN (div bọc cả modal) cũng "chứa" text title →
                // trả về sẽ click nhắm giữa vùng lớn → chọn nhầm. Không thấy card → null (thà không click).
                var cards = await modal.QuerySelectorAllAsync(".dropoff-method-card").ConfigureAwait(false);
                foreach (var c in cards)
                {
                    if (ShopeeShippingNav.IsDropoffTitleText(await c.InnerTextAsync().ConfigureAwait(false))
                        && await c.BoundingBoxAsync().ConfigureAwait(false) is not null)
                    {
                        return c;
                    }
                }
            }
            catch { /* bỏ qua */ }

            return null;
        }

        /// <summary>
        /// Dò nút "Xác nhận" trong modal "Giao Đơn Hàng": (a) <c>[data-testid='arrange-shipment-confirm']</c>;
        /// (b) fallback button khớp <see cref="ShopeeShippingNav.IsConfirmArrangeButtonText"/>. Không thấy → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> FindArrangeConfirmButtonAsync(IElementHandle modal)
        {
            try
            {
                var btn = await modal.QuerySelectorAsync("[data-testid='arrange-shipment-confirm']").ConfigureAwait(false);
                if (btn is not null)
                {
                    return btn;
                }

                var footer = await modal.QuerySelectorAsync(".eds-modal__footer").ConfigureAwait(false);
                var scope = footer ?? modal;
                return await FindButtonByTextAsync(scope, ShopeeShippingNav.IsConfirmArrangeButtonText).ConfigureAwait(false);
            }
            catch { /* bỏ qua */ }

            return null;
        }

        /// <summary>Ứng viên nút BẤM ĐƯỢC khi có bounding box VÀ tâm box HIT-TEST PASS (<c>elementFromPoint</c>
        /// tại tâm là nút / con / tổ tiên của nút — không bị lớp khác đè). Handle stale (modal re-render) hoặc
        /// lỗi đọc khác → <c>false</c> (KHÔNG ném) để lượt duyệt thử ứng viên KẾ.</summary>
        private static async Task<bool> IsCandidateClickableAsync(IElementHandle cand)
        {
            try
            {
                var box = await cand.BoundingBoxAsync().ConfigureAwait(false);
                if (box is null)
                {
                    return false;
                }
                double cx = box.X + box.Width / 2.0, cy = box.Y + box.Height / 2.0;
                return await IsPointOnElementAsync(cand, cx, cy).ConfigureAwait(false);
            }
            catch { return false; }
        }

        /// <summary>
        /// Chờ nút in (<c>button[data-testid='print-button']</c>) tới khi <b>THẬT SỰ BẤM ĐƯỢC</b> (không chỉ
        /// có bounding box): nút có thể hiện MUỘN và lúc mới hiện còn bị LỚP CHE đè (có box nhưng
        /// <c>elementFromPoint</c> tại nút trả lớp overlay → click không ăn). Poll tới hết
        /// <paramref name="timeoutMs"/> (RE-QUERY tươi mỗi vòng, KHÔNG giữ handle stale qua các lượt).
        /// <para>
        /// QUAN TRỌNG — duyệt <b>TẤT CẢ</b> ứng viên, thứ tự <b>NGƯỢC</b> (từ CUỐI DOM lên): eds-modal của Vue
        /// GIỮ LẠI modal "Thông Tin Chi Tiết" của ĐƠN TRƯỚC trong DOM (dạng ẩn/bẹp) sau khi đóng bằng nút X,
        /// nên DOM có thể chứa ≥2 nút <c>print-button</c> — nút MA của modal CŨ đứng TRƯỚC, nút THẬT của modal
        /// hiện tại append CUỐI body. Nếu chỉ tin PHẦN TỬ ĐẦU (như <see cref="FirstVisibleByBoxAsync"/>) sẽ vớ
        /// đúng nút ma, hit-test fail vĩnh viễn, KHÔNG BAO GIỜ thử nút thật (bằng chứng log 16:47:04 đơn thứ 2:
        /// <c>via=testid, box=191x16, elementFromPoint=div.eds-modal__container</c> — nút bẹp, tâm bị vỏ modal
        /// khác đè). Duyệt ngược → gặp nút THẬT trước; HIT-TEST TỪNG ứng viên (<see cref="IsCandidateClickableAsync"/>),
        /// PASS đầu tiên trả NGAY. Ưu tiên <c>button[data-testid='print-button']</c>, fallback mọi button khớp
        /// <paramref name="textMatch"/>. Giờ CHỈ dùng cho nút "In phiếu giao" (<see cref="ShopeeShippingNav.IsPrintSlipButtonText"/>).
        /// Không ứng viên nào pass tới hết deadline → <c>null</c>.
        /// </para>
        /// </summary>
        private static async Task<IElementHandle?> WaitPrintButtonClickableAsync(
            IPage page, Func<string?, bool> textMatch, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();

                // (a) Ưu tiên nút CÓ data-testid='print-button'. Duyệt TẤT CẢ ứng viên theo thứ tự NGƯỢC
                //     (modal mới nhất append CUỐI body → nút THẬT đứng SAU nút MA của modal cũ). Hit-test
                //     PASS đầu tiên → trả NGAY.
                try
                {
                    var byId = await page.QuerySelectorAllAsync("button[data-testid='print-button']").ConfigureAwait(false);
                    for (int i = byId.Count - 1; i >= 0; i--)
                    {
                        if (await IsCandidateClickableAsync(byId[i]).ConfigureAwait(false))
                        {
                            return byId[i];
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* chưa render / selector — thử vòng sau */ }

                // (b) Chưa ứng viên testid nào pass → fallback quét MỌI button theo TEXT (chuẩn hóa), cũng
                //     duyệt NGƯỢC. Bọc try/catch TỪNG ứng viên: một nút stale (modal cũ vừa bị Vue gỡ giữa
                //     chừng) không được phá cả lượt duyệt các nút còn lại.
                try
                {
                    var buttons = await page.QuerySelectorAllAsync("button").ConfigureAwait(false);
                    for (int i = buttons.Count - 1; i >= 0; i--)
                    {
                        var b = buttons[i];
                        bool match;
                        try { match = textMatch(await b.InnerTextAsync().ConfigureAwait(false)); }
                        catch (OperationCanceledException) { throw; }
                        catch { continue; /* nút stale → bỏ, thử nút kế */ }

                        if (match && await IsCandidateClickableAsync(b).ConfigureAwait(false))
                        {
                            return b;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* chưa render — thử vòng sau */ }

                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>True nếu URL của trang <paramref name="p"/> chứa "awbprint" (tab phiếu giao). Nuốt lỗi
        /// (context ngắt / page đóng) → false. Dùng nhận tab phiếu mở MUỘN để tránh bấm In lần 2 (double-tab).</summary>
        private static bool SafeUrlHasAwbprint(IPage p)
        {
            try { return p.Url.Contains("awbprint", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>
        /// Chờ modal (<c>.eds-modal__box</c>) có tiêu đề khớp <paramref name="titleMatch"/> xuất hiện, poll
        /// tới hết <paramref name="timeoutMs"/>. Tiêu đề đọc từ <c>.eds-modal__title</c> (fallback <c>.title</c>).
        /// Không hiện → <c>null</c>.
        /// </summary>
        private static async Task<IElementHandle?> WaitModalByTitleAsync(
            IPage page, Func<string?, bool> titleMatch, int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            do
            {
                ct.ThrowIfCancellationRequested();
                var modal = await FindModalByTitleAsync(page, titleMatch).ConfigureAwait(false);
                if (modal is not null)
                {
                    return modal;
                }

                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            while (DateTime.UtcNow < deadline);

            return null;
        }

        /// <summary>Tìm modal <c>.eds-modal__box</c> có tiêu đề (<c>.eds-modal__title</c> hoặc <c>.title</c>)
        /// khớp <paramref name="titleMatch"/>. Không có → <c>null</c> (không ném).</summary>
        private static async Task<IElementHandle?> FindModalByTitleAsync(IPage page, Func<string?, bool> titleMatch)
        {
            try
            {
                var boxes = await page.QuerySelectorAllAsync(".eds-modal__box").ConfigureAwait(false);
                foreach (var box in boxes)
                {
                    var title = await box.QuerySelectorAsync(".eds-modal__title").ConfigureAwait(false)
                                ?? await box.QuerySelectorAsync(".title").ConfigureAwait(false);
                    if (title is null)
                    {
                        continue;
                    }

                    if (titleMatch(await title.InnerTextAsync().ConfigureAwait(false)))
                    {
                        return box;
                    }
                }
            }
            catch { /* bỏ qua */ }

            return null;
        }

        /// <summary>True nếu chuỗi class của phần tử chứa token <paramref name="token"/> (tách theo khoảng
        /// trắng, không phân biệt hoa/thường). Nuốt lỗi handle detached → false.</summary>
        private static async Task<bool> ElementHasClassTokenAsync(IElementHandle el, string token)
        {
            try
            {
                var classAttr = await el.GetAttributeAsync("class").ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(classAttr))
                {
                    return false;
                }

                foreach (var c in classAttr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.Equals(c, token, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { /* detached — coi như không có token */ }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            // Ngắt CDP trước (đóng kết nối Playwright ↔ Brave).
            try { await _browser.CloseAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }

            // KILL cả cây tiến trình Brave để không để lại tiến trình mồ côi giữ khóa --user-data-dir
            // (nếu còn, lần mở sau sẽ lỗi khóa hồ sơ).
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* bỏ qua */ }
            // Chờ tiến trình thoát HẲN (giải phóng khóa hồ sơ) trước khi cho phép mở lại cùng hồ sơ.
            try
            {
                using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch { /* hết giờ/lỗi — bỏ qua, tầng gọi có retry */ }
            try { _process.Dispose(); } catch { /* bỏ qua */ }

            try { _playwright.Dispose(); } catch { /* bỏ qua */ }
        }
    }
}
