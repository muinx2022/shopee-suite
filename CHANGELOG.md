# Ghi chú phát hành (CHANGELOG)

App desktop phát hành qua Velopack + GitHub Releases (kênh `win`). Client cài qua
`ShopeeSuite-win-Setup.exe` một lần, từ đó tự tải delta và cập nhật bằng nút
"Cập nhật & khởi động lại" trong Settings → Hiệu năng. Quy trình ra bản mới: sửa
`version.txt` → chạy `release-suite.cmd` (cần `GITHUB_TOKEN`).

## v1.7.7 — 2026-08-03

- **Bỏ chữ in đậm toàn app (suite + Đơn hàng):** mọi tiêu đề, nhãn, header lưới, badge, tên tài khoản chuyển
  từ đậm (Bold/SemiBold) sang nét vừa (Medium) cho êm mắt; đồng thời làm đậm 2 sắc xám chữ phụ để chữ không bị nhạt.
- **Đồng bộ 2 danh sách tài khoản (Workspace ↔ Cấu hình BigSeller):** cùng kiểu thẻ (nền trắng, chọn = nền cam
  nhạt), chấm tròn trạng thái, cỡ chữ 13.5 và độ rộng lưới 300px; gộp style dùng chung trong theme.
- **Dọn lề XAML:** vá lại các dòng bị rớt thụt lề từ đợt sửa trước.

## v1.7.6 — 2026-08-03

- **Google Sheet khớp layout mẫu A–K mới:** payload đẩy đủ mã đơn, mã vận đơn, ảnh/PDF, mã đơn trả hàng,
  tiền bán, ngày đặt và phân loại; **Shop ở cột F, SKU ở cột J, Phân Loại Đơn Hàng ở cột K**.
- **Vòng Đơn hàng chạy liên tục cho tới khi người dùng dừng:** bỏ giới hạn cứng 12 giờ và ghi rõ giờ dự kiến
  chạy vòng kế tiếp trong nhật ký để dễ theo dõi lúc app đang nghỉ giữa hai vòng.
- **Hub → Giao việc → Đơn hàng hiển thị “Đơn chờ hôm nay” theo ngày Việt Nam:** chỉ đếm đơn xuất hiện trong
  ngày hiện tại bằng mốc `first_seen_at`, không cộng dồn đơn cũ. Đây là thay đổi source Hub và cần deploy Hub
  riêng; bản phát hành client này không tự triển khai Hub.

## v1.7.5 — 2026-08-02

- **Sửa lỗi module Đơn hàng đứng ở "Chờ extension nối cầu" rồi báo "hết thời gian chờ phản hồi từ extension"**
  — đăng nhập xong là dừng, không đọc được shop/đơn nào. Nguyên nhân: mỗi lần chạy app chép extension cầu nối
  ra một thư mục tạm mới, nhưng chỉ chép các file ngoài cùng nên thư mục `shared/` (thêm từ 30/07) bị rụng →
  extension chết ngay lúc nạp, không bao giờ báo "sẵn sàng". Nay chép cả thư mục con.

## v1.7.4 — 2026-07-31

- **Chữ trên MỌI dải tab về nét thường hoàn toàn** — bản trước mới bỏ in đậm ở tab đang xem, nhưng nền chữ
  của các dải tab vẫn hơi đậm (SemiBold) cho mọi ô. Nay cả dải tab "viên" trắng (*Workspace · Cài đặt*) lẫn
  dải tab gạch chân cam (*Search · Trạng thái · Check tài khoản*) và dải tab chi tiết tài khoản của module
  Đơn hàng đều dùng nét chữ thường ở mọi trạng thái (bình thường · rê chuột · đang xem). Tab đang xem vẫn
  nhận ra ngay nhờ viên trắng / gạch cam + màu chữ.

## v1.7.3 — 2026-07-31

- **Chữ trên dải tab TRÊN CÙNG to hơn** (*Workspace · Cấu hình BigSeller · Shopee · Cài đặt*) — dễ đọc hơn ở
  màn hình lớn; dải tab cao thêm 4px, các phần bên dưới không đổi.
- **Tab đang xem không in đậm chữ nữa** — ở cả ba loại dải tab (tab trên cùng, dải tab "viên" trắng của
  Workspace/Cài đặt, dải tab gạch chân cam trong các màn Search/Fleet/Check tài khoản). Tab đang xem vẫn nhận
  ra ngay nhờ màu chữ + gạch cam / viên trắng, nhưng chữ giữ nguyên nét nên dải tab không bị xê dịch mỗi lần
  đổi tab.
- **Nét chữ và viền của 3 hộp thoại tròn pixel như các cửa sổ khác** (hộp thoại thông báo của suite, hộp thoại
  *Xác nhận* và *Thông tin đơn hàng* của module Đơn hàng) — trước còn thiếu nên chữ/viền có thể lệch nửa pixel,
  trông mờ hơn phần còn lại của app.

## v1.7.2 — 2026-07-31

- **Dải tab màn "Cài đặt" đổi sang đúng kiểu tab của màn Workspace** — thay vì hàng chữ gạch chân cam, 4 tab
  nay nằm trong một khay xám bo tròn, tab đang xem là "viên" trắng nổi lên (y hệt dải *Shop & cấu hình ·
  Thống kê · Dữ liệu · Theo dõi Scrape · Theo dõi Update* ở màn Workspace). **Thứ tự tab sắp lại theo mức
  hay dùng:** *Chế độ ứng dụng · Đơn hàng · Hiệu năng & Đồng bộ · Phiên bản & cập nhật*. Nội dung từng tab
  giữ nguyên, không nút/ô nhập nào bị bỏ; màn Workspace không đổi gì.
- **Đổi font chữ toàn app về Segoe UI chuẩn — nét đều, sắc hơn.** Bản trước dùng *Segoe UI Variable* (phông
  "biến thiên") nên khi WPF vẽ ra thì độ dày nét chữ không đều, chỗ mảnh chỗ đậm; nay dùng *Segoe UI* bản
  tĩnh cho cả suite lẫn module Đơn hàng. Các cửa sổ phụ (hộp thoại xác nhận, Import tài khoản, Check tài
  khoản, Thống kê scrape, Sửa dòng dữ liệu, Chi tiết đơn) cũng được đặt cùng chế độ vẽ chữ như cửa sổ chính
  để chữ nhỏ nét đều nhau ở mọi cửa sổ.

## v1.7.1 — 2026-07-31

- **Màn "Cài đặt" chia TAB — mỗi phần một tab, hết cuộn dài.** Thay vì một cột dọc phải cuộn qua tất cả các
  nhóm, màn Cài đặt nay có dải tab ngay dưới tiêu đề: *Chế độ ứng dụng · Phiên bản & cập nhật · Hiệu năng &
  Đồng bộ · Đơn hàng*. Hai nhóm cũ **Hiệu năng** và **Đồng bộ nhiều máy** gộp chung MỘT tab, xếp 2 cột (trái:
  tài nguyên → trần cửa sổ Brave + thông tin máy; phải: tên máy + kết nối Hub). Tab ẩn/hiện theo chế độ như
  trước: chế độ Shopee không có tab *Hiệu năng & Đồng bộ*, chế độ Workspace không có tab *Đơn hàng*. Không nút
  hay ô nhập nào bị bỏ — chỉ đổi chỗ đứng.
- **Gỡ nốt chữ nhắc webhook ở màn Cài đặt:** ô cấu hình webhook đã bỏ từ bản trước nhưng dòng mô tả dưới tiêu
  đề vẫn còn nhắc; nay viết lại cho đúng (cấu hình AI/prompt vẫn đặt trên Hub).

## v1.7.0 — 2026-07-31

- **Toàn bộ giao diện app dựng lại trên WPF — từ bản này app là bản CHỈ chạy Windows.** Bố cục, màu sắc và cách
  thao tác giữ **nguyên như cũ** (dải tab trên cùng, ribbon, các màn Workspace / Đơn hàng / Cài đặt, mọi nút và ô
  nhập ở đúng chỗ); thay đổi nằm ở tầng vẽ giao diện bên dưới: bỏ bộ dựng giao diện đa nền tảng (Avalonia), dùng
  thẳng WPF của Windows. Những gì người dùng nhận thấy:
  - **Chữ sắc nét hơn** (ClearType của Windows) và app dùng đúng phông hệ thống Windows 11 — *Segoe UI Variable* —
    thay cho phông nhúng riêng trước đây.
  - Cửa sổ, hộp thoại, menu chuột phải, thanh cuộn, ô chọn ngày… hành xử **giống mọi app Windows khác**.
  - **Ô chọn (combo box) nay phẳng đúng tông app** — nền trắng, viền mảnh bo góc, viền cam khi mở, mục đang chọn
    tô cam nhạt — thay cho kiểu nút xám bóng mặc định của Windows.
  - Hai chỗ chữ bị cắt cụt đã nới cho đủ: nút **"Thêm tài khoản"** (màn Tài khoản của Đơn hàng) và nhãn trạng thái
    **"Trả hàng/Hoàn tiền"** trong bảng đơn (nhãn quá dài nay cắt có dấu "…" và rê chuột xem đủ chữ).
  - Màn **Search** ở cửa sổ thấp không còn nuốt mất khung kết quả: danh sách link tự co lại (vẫn cuộn được) để
    nhường chỗ cho phần kết quả bên dưới.
  - **Lưu ý:** từ bản này **bản cho Ubuntu/Linux phát hành riêng từ nhánh `avalonia`**; bản Windows (kênh `win`)
    không còn kèm đường build Linux. Máy Windows cập nhật như thường lệ, **không phải cài lại**.
- **Màn "Cài đặt" làm lại từ đầu:** trước đây màn này ghép hai màn cài đặt cũ lại với nhau nên có **hai dòng chữ
  "Cài đặt"** chồng nhau, ba kiểu thẻ/phông/màu trộn lẫn và mỗi khối thụt lề một kiểu. Nay chỉ còn **một tiêu đề,
  một hệ giao diện** và các mục xếp thành 5 nhóm rõ ràng theo thứ tự dùng: *Chế độ ứng dụng · Phiên bản & cập nhật ·
  Hiệu năng · Đồng bộ nhiều máy · Đơn hàng* (nhóm Hiệu năng/Đồng bộ ẩn ở chế độ Shopee, nhóm Đơn hàng ẩn khi không
  có module). Không có nút hay ô nhập nào bị bỏ — chỉ sắp lại chỗ.
- **Bỏ ô cấu hình webhook trên máy client:** ba ô webhook (*đơn mới · lỗi app · đơn trả hàng*) không còn ở màn Cài
  đặt của app, vì webhook giờ là cấu hình **dùng chung đặt trên Hub** (Hub → Cài đặt) và chính Hub gửi tin. Máy đã
  lưu webhook từ trước vẫn gửi như cũ khi chạy độc lập — chỉ là không sửa được từ app nữa.

## v1.6.17 — 2026-07-31

- **Không còn nuốt yêu cầu trả hàng khi app chọn nhầm tab:** trước đây nếu app không bấm được tab *"Đơn Trả hàng
  Hoàn tiền"*, nó vẫn lấy con số của tab *"Tất cả"* (gộp cả Đơn Hủy / Giao không thành công) làm **mốc** của shop —
  vòng sau đọc đúng tab thì số nhỏ hơn mốc rác nên bị coi là *"đã xử xong"*, và mọi yêu cầu phát sinh giữa chừng
  **mất vĩnh viễn**, không lên Google Sheet. Nay app **xác nhận tab đang chọn** sau cú bấm; không chắc thì **bỏ lượt,
  giữ nguyên mốc** (chậm một vòng, không mất mã).
- **Hết bấm "Chuẩn bị hàng" chồng lên hộp thoại đang mở:** máy chậm dựng hộp thoại ~5 giây thì cú bấm lại rơi vào
  giữa màn hình lúc hộp thoại vừa hiện — trúng nền mờ (đóng hộp thoại, lặp mở/đóng rồi báo *"không mở được"* oan) hoặc
  trúng nút bên trong (đơn bị giao với **phương thức mặc định sai**). Nay app kiểm tra hộp thoại **trước mỗi lần bấm
  lại** và chờ đủ 10 giây mỗi lượt như bản cũ.
- **Thông báo "có đơn trả hàng" không còn im lặng:** phần lớn mã trả hàng thuộc đơn app **đã dọn**, mà tin báo lại
  chỉ dựa trên đơn còn trong máy nên gần như không bao giờ bắn. Nay tin dựa trên **kho mã trả hàng**: máy chạy độc
  lập gửi webhook local, máy đã nối Hub thì báo lên Hub.
- **Badge "⏳ Chờ đẩy" đếm cả mã trả hàng còn tồn** (trước đây hiện 0 dù hàng chục mã đang kẹt).
- **Cảnh báo "không đặt được địa chỉ" không còn câm 60 phút vô cớ:** khi Hub không nhận tin mà máy cũng chưa cấu
  hình webhook lỗi app, mốc chống spam vẫn bị giữ dù **chưa ai được báo**. Nay mốc chỉ giữ khi ít nhất một kênh đã
  nhận tin.

- **Số "chuẩn bị hàng" chung không còn thiếu/lệch:** sửa cuộc đua khiến đơn đổi trạng thái/mã trả trong lúc đang đẩy
  lên Hub bị "niêm phong" không bao giờ đẩy lại (thêm cột thế hệ `hub_push_gen`); Hub gộp các shop trùng tên khác
  HOA/thường (trước đây cùng một đơn bị đếm 2 lần, lọc theo shop trả 0); app cộng dồn thay vì đè khi Hub trả 2 dòng
  cùng shop; đơn gửi bù kèm mốc "thấy lần đầu" của máy nên không rơi sai ngày.
- **Giờ trong tin webhook từ Hub đúng giờ Việt Nam** (máy chủ chạy UTC từng làm tin lệch 7 tiếng); Hub mới dựng /
  khôi phục backup không còn bắn loạt tin "đơn trả" cho dữ liệu lịch sử.
- **Hub web nhanh và gọn hơn:** thống kê/danh sách đọc qua kết nối riêng (hết nghẽn khi nhiều máy cùng đẩy đơn);
  trang Giao việc + BigSeller tách nhỏ; các trang `/orders`, `/logs-view`, `/config/accounts` giữ nguyên bộ lọc khi
  F5/chia sẻ link.
- **Dọn nền lớn toàn repo (không đổi tính năng):** hợp nhất code trùng lặp về thư viện chung (đăng nhập Shopee,
  thao tác chuột-phím, WebSocket, dò trình duyệt, selector Microsoft, tiện ích extension); tách các file khổng lồ
  thành phần nhỏ có chủ đích; thêm ~90 test mới (lần đầu có test cho cầu nối extension Đơn hàng).

> ⚠ **Thứ tự cập nhật đợt này (bắt buộc): deploy Hub web lên VM TRƯỚC, rồi mới phát hành bản client.** Client mới
> gửi thêm mốc "thấy lần đầu" của đơn + tin "đơn trả" qua Hub; Hub cũ không hiểu sẽ bỏ qua các cải thiện đó.
> Lần khởi động đầu sau deploy, Hub tự gộp shop trùng tên (chạy một lần) — **backup `hub.db` trước khi deploy**.

> ⚠ **Cần làm một lần trên Google Sheet, TRƯỚC khi cài bản client này:** Apps Script cũ chỉ điền vào **ô trống**, nên
> khi Shopee tạo lại yêu cầu trả hàng với **mã khác**, ô *"Mã đơn trả hàng"* giữ mãi mã cũ trong khi app vẫn coi là
> đẩy thành công — hỏng **im lặng**. Dán bản mới ở `orders/gsheet-apps-script/Code.gs` vào Apps Script rồi
> **Triển khai → Phiên bản mới** (chỉ Lưu là chưa đủ): bản mới cho **ghi đè** đúng cột *"Mã đơn trả hàng"* khi mã
> khác, các cột còn lại giữ nguyên luật cũ.

## v1.6.16 — 2026-07-30

- **Thống kê đơn dùng chung từ Hub** (mọi máy nhìn cùng một số), kèm 4 lỗi đã sửa trước khi phát hành:
  đổi ngày không còn làm app đứng hình tới 8 giây (số của máy hiện ngay, số chung về sau); khoảng ngày
  không còn lệch 7 tiếng do máy chủ chạy giờ UTC; đơn cũ đồng bộ lại không còn bị đếm nhầm vào hôm nay
  (Hub ghi thêm mốc "lần đầu thấy đơn"); và tab Thống kê nay **nói rõ** đang xem số chung toàn hệ thống
  hay số của riêng máy này.
- **Nhật ký (tab Shopee → Tài khoản) hết đơ khi chạy nhiều tài khoản:** không còn ghi file chặn luồng
  từng dòng, gom nhóm cập nhật giao diện, và mỗi tài khoản có **200 dòng gần nhất của riêng mình** —
  tài khoản chạy ồn không còn đẩy văng nhật ký của tài khoản đang xem. File log trên đĩa vẫn ghi đủ,
  tự xoay vòng khi quá 8MB.
- **Màn Cấu hình BigSeller gọn lại trên màn nhỏ:** không phải cuộn mới thấy hết, cột "Batch" hết cụt
  chữ, bỏ cột "Sheet" (kho dữ liệu đã ở Hub, không còn dùng workbook Excel).

## v1.6.15 — 2026-07-30

- **Cửa sổ vừa màn hình nhỏ (1440×900):** trước đây app luôn mở cứng 1500×940 nên trên màn 1440×900 nó tràn
  ra ngoài — mất thanh trạng thái dưới đáy, cắt mép trái/phải. Nay lúc mở app tự đo **vùng làm việc** (màn
  hình trừ taskbar) của màn đang chứa cửa sổ, nếu không đủ chỗ thì thu kích thước lại cho vừa **rồi phóng to
  toàn màn hình**. Bấm nút khôi phục sẽ ra cửa sổ vừa màn chứ không bật lại kích thước tràn. Máy màn to (Full
  HD trở lên) mở y như cũ, không đổi gì. Các cửa sổ phụ (Kiểm tra TK, Nhập tài khoản, Thống kê scrape) cũng
  tự thu cho vừa màn.

## v1.6.14 — 2026-07-29

- **Kéo tài khoản (sub-acc) Đơn hàng từ Hub về máy mới:** màn Tài khoản có nút **"Kéo TK từ Hub"** — hỏi Hub
  danh bạ sub-acc gộp từ mọi máy (login + shop) rồi tạo sẵn các tài khoản máy **chưa có** (mật khẩu để trống,
  ghi chú "Kéo từ Hub — cần nhập mật khẩu"). Tài khoản đã có ở máy **giữ nguyên** (không đè mật khẩu/cookie).
  Vì lý do bảo mật, Hub **không** lưu/truyền mật khẩu — mở từng tài khoản nhập mật khẩu rồi bấm Chạy như thường.

## v1.6.13 — 2026-07-29

- **Check đơn trả hàng bấm đúng tab + lần đầu phải check:** mở đúng tab **"Đơn Trả hàng/Hoàn tiền"**, lần đầu
  của mỗi shop luôn check, giới hạn cửa sổ **7 ngày**, và **mã yêu cầu trả hàng đọc độc lập với mã đơn** (thêm
  4 chốt chặn để không ghép nhầm / bỏ sót).
- **Tách 3 kênh notify do Hub quyết định:** webhook **đơn mới** / **lỗi app** / **đơn trả** tách riêng, chặn
  gửi trùng, hết lỗi hỏng im lặng (log rõ), và tự lùi về webhook cũ nếu Hub chưa cấu hình — chỉnh trong
  Settings.
- **File Google Sheet phụ:** không nhận đơn hủy mới, và tô đỏ đơn bị hủy sau khi đã ghi.

## v1.6.12 — 2026-07-29

- **Xóa ribbon "Đơn toàn hệ thống":** tab Đơn hàng chỉ còn **Đơn hàng** / **Thống kê** (đơn toàn hệ thống xem trên Hub).
- **Brave Đơn hàng chết theo app:** trình duyệt mở bởi module Đơn hàng vào Job Object — tắt cứng / crash app cũng
  dọn Brave; lúc mở app quét thêm profile `XuLyDonShopee\profiles` mồ côi.
- **Tab Kết quả khi click tài khoản:** chọn acc trong danh sách → mở tab **Kết quả** ngay; nhãn
  **Tổng đã chuẩn bị hàng (trong ngày): xx đơn**.

## v1.6.11 — 2026-07-29

- **SKU nhiều sản phẩm trên app + Hub:** đơn có nhiều SP (vd 2 giày) nay hiện đủ SKU từng sản phẩm nối bằng
  `" · "` (giống cột Phân loại), không còn chỉ hiện SKU sản phẩm đầu. Đơn cũ không có khóa `sku` trong
  `items_json` vẫn hiện field SKU cũ như trước.
- **Không mất SKU/phân loại sau sync:** vòng sync trang danh sách không còn đè mất `items_json` giàu (đã đọc ở
  trang chi tiết). Hub cũng giữ bản giàu khi máy khác đẩy lại bản nghèo.
- **Google Sheet thứ hai:** ghi song song A/B/C/E sang file phụ (cấu hình URL/ID trên hub + client); lỗi ghi file
  phụ được log cảnh báo thay vì im lặng — không làm hỏng đường ghi file chính.
- **Ước tính:** ưu tiên bảng doanh thu trang chính; vá mất ước tính khi đơn rời trạng thái chuẩn bị hàng.
- **Đọc sản phẩm trang chi tiết:** SKU thật + phân loại sạch theo từng SP, đẩy đủ lên Google Sheet.
- **Hub trang Đơn hàng:** nút **Hủy lọc**, chọn số dòng mỗi trang, phân trang số trang rõ hơn.
- **Thống kê đơn hàng:** lọc theo khoảng ngày (đã có từ bản trước khi chưa release).

## v1.6.10 — 2026-07-28

- **Check đơn trả hàng ở cuối mỗi shop:** sau khi xử xong đơn của một shop, app mở trang **Trả hàng/Hoàn tiền/Hủy**
  của chính shop đó, đổi sắp xếp sang *"Ngày yêu cầu (Mới - Cũ)"* rồi đọc **mã yêu cầu trả hàng** ghép với **mã đơn
  hàng**, đẩy lên cột **"Mã đơn trả hàng"** trên Google Sheet và lên Hub. App **nhớ số yêu cầu của lần check trước
  theo từng shop**: số không đổi thì bỏ qua, tăng thêm bao nhiêu thì chỉ đọc bấy nhiêu dòng đầu — không quét lại cả
  danh sách mỗi vòng. Bước này chạy cả khi shop không có đơn chờ lấy hàng, và lỗi/timeout/captcha ở đây **không** phá
  phần chuẩn bị hàng đã xong, cũng không dừng vòng.
- **Cột "Phân loại":** phân loại hàng (vd `Nâu Be,39`) nay hiện ở màn **Đơn hàng** trên app, trang **Đơn hàng** trên
  Hub, và đẩy lên cột **Phân loại** của Google Sheet. Lấy từ dữ liệu đã quét sẵn ở trang danh sách nên **không** tốn
  thêm lượt mở trang chi tiết; đuôi SKU lặp lại (`[A322 A322]`) được cắt bỏ vì SKU đã có cột riêng.
- **Màn "Thống kê đơn hàng" mới** trong module Đơn hàng: chọn khoảng ngày rồi xem tổng đơn, cần xử lý, đã giao, đã
  hủy, doanh thu ước tính (không tính đơn hủy), số sản phẩm, trung bình mỗi đơn, tỉ lệ có mã vận đơn / đủ số tiền
  cuối, phân bổ trạng thái và lần đồng bộ gần nhất.

> ⚠ **Cần làm một lần trên Google Sheet:** Apps Script cũ ghi theo **số cột cứng** nên từ khi cột *"Mã đơn trả hàng"*
> được chèn vào, **mọi dòng thêm mới bị lệch một cột** mà không báo lỗi, và hai trường mới (`Phân loại`,
> `Mã đơn trả hàng`) bị bỏ đi. Dán bản mới ở `orders/gsheet-apps-script/Code.gs` vào Apps Script rồi **Triển khai →
> Phiên bản mới** (chỉ Lưu là chưa đủ). Bản mới tra cột **theo tên tiêu đề**, điền bù cả những ô còn trống của đơn cũ,
> và báo về `canhBao` nếu không tìm thấy tiêu đề thay vì ghi bừa.

## v1.6.9 — 2026-07-28

- **Không đặt được địa chỉ lấy hàng thì DỪNG, không in phiếu nữa:** trước đây khi app không mở được modal "Sửa Địa
  chỉ", nó tự ghi cảnh báo *"phiếu có thể sai địa chỉ"* rồi **vẫn in phiếu và giao đơn cho đơn vị vận chuyển** — kết
  quả là shipper tới sai chỗ lấy hàng mà không ai biết cho tới lúc đó. Nay app **dừng cả vòng của tài khoản** ngay
  tại chỗ (bỏ luôn các shop còn lại), **không in phiếu nào**, và **gửi cảnh báo ra Slack / Discord / Telegram** theo
  webhook đã cấu hình ở Cài đặt — kèm máy, tài khoản, shop, địa chỉ định đặt và việc cần làm.
  Chặn spam 1 tin/tài khoản/giờ; webhook chưa cấu hình hay mạng hỏng thì **vẫn dừng** (dừng không phụ thuộc gửi được
  tin hay không). Vòng tự chạy lại theo chu kỳ thường lệ sau khi bạn sửa xong địa chỉ trên Shopee.

## v1.6.8 — 2026-07-28

- **Số "chuẩn bị hàng" tự sang ngày mới:** máy chạy xuyên đêm (đúng cách module Đơn hàng vận hành — vòng lặp liên
  tục) thì trước đây lúc 00:00 ô ngày ở tab **Kết quả** vẫn đứng ở hôm qua, số đóng băng, và **đơn chuẩn bị của ngày
  mới không hiện ra nữa** cho tới khi đóng/mở lại app. Nay ô ngày tự chuyển sang ngày mới — ngay từ **đơn đầu tiên**
  của ngày, không phải chờ. Đang mở một ngày cũ để xem lại thì app **không giật** ngày khỏi tay bạn.

## v1.6.7 — 2026-07-27

- **Điều khiển Đơn hàng từ Hub:** trang Giao việc trên Hub có tab **Đơn hàng** làm việc theo đúng lối của tab
  BigSeller — chọn máy, thấy danh sách **tài khoản Shopee của máy đó**, bấm **▶ Chạy** / **✖ Dừng** ngay trên dòng
  tài khoản, khỏi phải ra tận máy. Shop con nằm dưới tài khoản của nó (bung ra xem đơn chờ + lần sync cuối), không
  còn đổ phẳng mọi shop của mọi tài khoản vào một bảng.
  *Để Hub thấy được, mỗi máy tự đẩy lên danh bạ tài khoản + shop + trạng thái phiên — **không đẩy mật khẩu, cookie
  hay hòm thư xác minh**; Hub chỉ soi và ra lệnh, tài khoản vẫn nằm ở máy.*
  Tài khoản đang chạy ở máy khác thì nút Chạy khoá lại kèm tên máy đang giữ (một tài khoản chỉ chạy một máy).
  Hai nút *Đồng bộ một lượt* và *Đăng nhập lại* tạm khoá — bản client hiện chưa có điểm vào cho hai lệnh này.

## v1.6.6 — 2026-07-27

- **Dừng hẳn khi key proxy hết hạn, thay vì âm thầm bỏ dòng:** trước đây key KiotProxy hết hạn thì mọi tài khoản
  Shopee đều xin proxy hỏng như nhau, nhưng app coi đó là lỗi tạm — cho tài khoản nghỉ, đổi tài khoản khác, và sau
  3 lần kẹt thì **bỏ qua dòng** rồi chạy tiếp. Kết quả: job chạy hết sheet, báo "xong", mà dữ liệu thủng lỗ chỗ (một
  lượt thật đã bỏ trắng 17 dòng trong 6 phút). Nay gặp lỗi loại "key/tài khoản proxy chết", app **dừng cả job ngay**,
  không bỏ dòng nào, và báo lên Hub kèm lý do đọc được — ô việc trên Hub thành ✕ Lỗi với dòng
  *"key proxy hết hạn — gia hạn key rồi chạy lại"*. Lỗi proxy lẻ (rớt mạng, IP xấu) vẫn xử như cũ.
- **Bị BigSeller đòi mã verify thì nhờ Hub đăng nhập hộ:** trước đây gặp màn hình verify là job chết tại chỗ, phải
  ra tận máy đăng nhập tay. Nay client tự nhờ Hub đăng nhập lại tài khoản đó — Hub giải captcha và **tự đọc mã từ
  hòm thư** — rồi cookie mới về máy và việc tự chạy tiếp. Trong lúc chờ, việc nằm ở hàng chờ chứ không bị báo hỏng.
  Hub bí quá mới cần người nhập mã, ngay trên web.
- **Import luôn chạy 1 cửa sổ:** import nhiều cửa sổ song song đụng nhau ở Material Center và tab "Đã nhận" của
  BigSeller. Trước đây import mượn số cửa sổ của Update (mặc định 2) nên Hub giao import là máy mở 2 cửa sổ. Nay
  import cố định 1 cửa sổ, tham số "Số process" trên Hub không ghi đè được. Việc import cũng chỉ chiếm 1 suất trong
  quỹ cửa sổ, nên việc khác có thêm chỗ chạy.
- **Máy chạy chế độ Shopee giờ hiện trên Hub:** trước đây bản chế độ Shopee (chỉ module Đơn hàng) không báo nhịp
  nên Hub hoàn toàn không thấy nó — không biết máy còn sống hay không, và **lệnh cập nhật app từ Hub luôn bỏ sót
  máy đó**. Nay mỗi máy có hai "suất": suất Workspace (việc BigSeller) và suất Đơn hàng; máy chạy chế độ Full chiếm
  cả hai. Hub hiện rõ chế độ + suất của từng máy, và không cho giao nhầm việc BigSeller vào suất Đơn hàng.
- **Tab "Kết quả" có dòng tổng:** hiện ngay trên lưới tổng số đơn đã chuẩn bị hàng của mọi shop trong ngày đang
  lọc, khỏi phải cộng tay hay cuộn hết danh sách.

## v1.6.5 — 2026-07-27

- **Số "chuẩn bị hàng" chung toàn hệ thống:** trước đây mỗi máy tự đếm phần việc của chính nó, nên máy chạy trước
  hiện 2 đơn còn máy chạy sau hiện 0 (Shopee đã hết đơn để chuẩn bị). Nay mỗi đơn được đóng dấu thời điểm chuẩn
  bị rồi đẩy lên Hub; Hub đếm trên bảng đơn nên **mọi máy thấy cùng một con số**, không thể cộng trùng. Lưới cập
  nhật sau mỗi shop xong. Mất Hub thì vẫn hiện số đang có kèm ghi chú "Chưa gộp được từ Hub".
  *Đơn chuẩn bị TRƯỚC bản này không có dấu thời điểm nên không vào số Hub — số khớp dần từ đơn mới trở đi.*
- **Khóa tài khoản chống hai máy tranh đơn:** nhiều máy chạy cùng một subaccount sẽ tranh đơn "chuẩn bị hàng" và
  đăng nhập song song vào một tài khoản Shopee (dễ bị đá phiên, ăn captcha). Nay máy phải xin khóa từ Hub trước
  khi mở trình duyệt; máy khác đang chạy thì bỏ qua tài khoản đó kèm báo **"Tài khoản đang chạy ở máy X"**. Khóa
  tự hết hạn nếu máy tắt đột ngột, và mất Hub thì vẫn chạy bình thường.

## v1.6.4 — 2026-07-26

- **Thứ tự shop theo đúng subaccount:** lưới tab "Kết quả" trước đây sắp shop theo bảng chữ cái, khác thứ tự
  người dùng thấy trên trang subaccount của Shopee. Nay app lưu lại vị trí shop đọc được từ `/portal/shop` và
  hiện y hệt thứ tự đó. (Shop đã lưu từ bản cũ tạm xếp cuối cho tới lượt chạy kế — chạy một lượt là đúng vị trí.)
- **Dấu tick shop đã kiểm tra:** cột tiến độ giờ mỗi dòng một biểu tượng — **vòng quay** khi đang kiểm tra shop
  đó, **dấu tick xanh** khi đã kiểm tra xong trong lượt chạy, để trống khi chưa tới. Nhìn lưới là biết lượt chạy
  đã đi qua những shop nào. Tick sống theo lượt chạy, tự xoá khi phiên đọc lại danh sách shop. Thay cho chấm tròn.

## v1.6.3 — 2026-07-26

- **Sửa lỗi Hub kẹt trạng thái đơn:** đơn đã đẩy lên Hub một lần thì mọi thay đổi trạng thái sau đó
  ("Đã hủy", "Đã giao"…) **không bao giờ** lên tới Hub — Hub hiển thị mãi trạng thái lúc đẩy lần đầu. Nay trạng
  thái đổi là đơn được đẩy lại. (Đơn kết thúc đã bị dọn khỏi máy từ trước không sửa lại được trên Hub.)
- **Sửa lỗi GSheet không tô đỏ đơn hủy:** khi Shopee hủy đơn, danh sách không còn hiện mã vận đơn nên app xoá mã
  đã lưu, khiến đơn rơi vào nhánh "bỏ qua đơn hủy chưa có vận đơn" — dòng cũ trên sheet nằm trắng vĩnh viễn. Nay
  mã vận đơn đã có được giữ lại, và đơn hủy **đã có dòng trên sheet** luôn được gửi lại để tô đỏ.
- **Chống mất dữ liệu trên Hub:** đơn đẩy lại mà không kèm số tiền cuối cùng / mã vận đơn thì Hub giữ giá trị
  đang có thay vì xoá trắng.

## v1.6.2 — 2026-07-26

- **Tab "Kết quả" — cột tiến độ:** thêm cột hẹp ở đầu lưới cho biết phiên đang chạy tới shop nào: shop **đang
  kiểm tra** có vòng quay + chữ "đang kiểm tra…" thay cho số; kiểm xong thì số đơn hiện lại và **chấm xanh ở lại**
  shop đó cho tới khi shop kế bắt đầu. Shop lỗi/captcha vẫn tắt vòng quay (không quay mãi).
- **Sửa lỗi lưới shop trống:** mở app rồi chọn tài khoản **trước** khi phiên kịp đọc danh sách shop thì lưới
  "Kết quả" đứng rỗng mãi (phải bấm sang tài khoản khác rồi bấm lại mới thấy). Nay đọc xong danh sách shop là
  lưới hiện ngay.

## v1.6.1 — 2026-07-26

- **Đơn toàn hệ thống (mới):** thêm màn xem đơn của **mọi máy** trong tab Shopee — lọc theo shop / trạng thái /
  tìm kiếm và phân trang, tất cả chạy ở phía Hub. Màn này **chỉ để xem**: đơn đọc thẳng từ Hub, **không chép về
  máy**, nên không ảnh hưởng gì tới đơn và các luồng xử lý của máy này. Hub chết hay chưa kết nối đều có thông
  báo riêng, không phải lưới trống câm.
- **Tab "Kết quả":** số chuẩn bị hàng nay **tự cập nhật ngay sau mỗi đơn** — trước đây phải đổi tài khoản hoặc
  đổi ngày mới thấy số mới (số vẫn được ghi đúng, chỉ là màn không đọc lại).

## v1.6.0 — 2026-07-26

Thiết kế lại toàn bộ giao diện theo bộ design mới, cộng một số tính năng và sửa lỗi mất dữ liệu âm thầm.

- **Giao diện (đổi lớn):** toàn app chuyển sang tông ẤM (nền `#F7F5F3`, chữ `#2C2724`) thay tông xám lạnh.
  Dải tab trên nền trắng, tab đang mở đánh dấu bằng gạch cam dưới đáy; ribbon icon đen, chuyển cam khi đang mở;
  thanh trạng thái mới cao 32px có chấm báo job nhấp nháy, số phiên bản, và số việc **chờ đẩy**. Thêm phím tắt
  **Ctrl + 1…4** chuyển tab.
- **Nút — đồng nhất toàn app:** mọi nút giờ CÙNG một dáng (nền trắng, viền luôn thấy rõ, cao 30); màu chỉ nằm ở
  ICON (chính = cam, xóa = đỏ, thành công = xanh). Thay toàn bộ emoji/ký tự (🗑 💾 ▶ ■ ↻ …) bằng **một bộ icon
  vector dùng chung cho cả Workspace lẫn Đơn hàng** — mỗi hành động đúng một icon, hết cảnh "nút Lưu mỗi nơi một kiểu".
- **Workspace:** dải đầu màn + dòng gợi ý gọn lại; danh sách tài khoản dạng thẻ có chấm trạng thái; các tab con
  chuyển sang dạng *segmented* (khay xám, ô đang chọn nổi nền trắng). Bảng shop bỏ cột "Tiến độ" — 4 nút thao tác
  tự kể trạng thái: trắng = chờ · **cam = đang chạy** · dấu ✓ xanh góc = đã xong.
- **Việc dở:** banner giờ LIỆT KÊ rõ từng việc (thao tác · tài khoản · shop · tiến độ) thay vì chỉ đếm số; và khi
  Hub đã hủy hẳn việc thì client tự bỏ, không giữ lại nữa.
- **Vòng chờ đẩy (mới):** thêm luồng đẩy chạy nền độc lập cho Hub / Google Sheet / "Đã bán" — Hub sống lại lúc máy
  đang nghỉ giữa 2 vòng là tự đẩy bù, không phải đợi shop kế tiếp; lượt đẩy bị hủy do dừng phiên cũng được nhặt lại.
- **Sửa lỗi mất dữ liệu âm thầm:**
  - Đếm "Đã bán" trước đây **mất vĩnh viễn** khi Hub lỗi (lượt sau không còn thấy đơn chuyển trạng thái) → nay có
    hàng đợi riêng, đếm bù được.
  - Cột "Cuối cùng" trên Hub bị trống vĩnh viễn với đơn lên Hub trước khi lấy được số tiền → nay tự đẩy lại; đơn cũ
    đang hỏng được sửa một lần khi mở app.
  - Máy chưa cấu hình Google Sheet trước đây bị **bỏ qua hoàn toàn im lặng** → nay có dòng log báo rõ; Hub đẩy
    thất bại cũng không còn im lặng.
- **Google Sheet:** cột tiền bán nay ghi số **"Ước tính"** (số tiền cuối cùng) thay vì tổng tiền niêm yết; đơn ghi
  lúc chưa có ước tính sẽ được tự điền lại sau. Thêm **đồng bộ cấu hình GSheet giữa Hub và mọi máy** (link Web App
  + tab): điền một lần trên Hub `/config/orders`, các máy tự nhận trong ~1 phút, không cần khởi động lại.
- **Đơn hàng:** màn chi tiết tài khoản thêm tab **"Kết quả"** — lưới Shop | Chuẩn bị hàng, cộng dồn theo ngày, có
  lịch chọn ngày.
- **Dọn:** bỏ nút "Sync shop → Đơn hàng" (chạy subaccount nên module Đơn hàng tự đọc danh sách shop) và bỏ nút
  Proxy khỏi ribbon Shopee.

## v1.5.1 — 2026-07-25

Đợt sửa lỗi + dọn dẹp lớn sau tổng review toàn app (không thêm tính năng mới).

- **Đơn hàng:** nút "Tải phiếu" hoạt động trở lại (tải lại phiếu qua cầu nối extension — chỉ tải được đơn thuộc
  shop mà phiên đang mở); chạy nhiều tài khoản giờ XẾP HÀNG tự động ("Chờ đến lượt") thay vì giết trình duyệt
  của nhau + tranh cổng cầu nối; mất kết nối extension báo lỗi NGAY thay vì chờ timeout 30-300s với thông điệp
  sai; bấm Dừng hiển thị "Đang dừng…" và chỉ cho chạy lại khi phiên cũ tháo dỡ xong; gỡ màn Proxy (đường cầu
  nối không dùng proxy).
- **Workspace/BigSeller:** máy bấm "Ngắt kết nối" không còn âm thầm heartbeat lên hub; đồng bộ cookie từ hub ghi
  file NGUYÊN TỬ (hết nguy cơ cookie hỏng lan đa máy); vá lỗi hiếm có thể làm sập app từ vòng giám sát trình
  duyệt; chặn được ca 2 vòng cào chạy đè nhau trên cùng hồ sơ; vòng nhận việc hub có log lỗi (hết "máy không
  nhận việc" câm lặng).
- **Extension Search/Scrape:** hết ca run tự khởi động lại khi WebSocket chập chờn; kết quả scrape gắn mã lượt —
  hết báo đúng/sai NHẦM DÒNG khi thử lại; nút "Bán chạy" chọn theo chữ trên nút (đổi thứ tự nút không còn bấm nhầm).
- **Dọn ~7.800 dòng code chết** (di sản Playwright cũ của Đơn hàng, hub nhúng cũ của desktop, extension POC) —
  app nhẹ và dễ bảo trì hơn, không đổi hành vi.

## v1.5.0 — 2026-07-25

- **Đơn hàng chạy qua CẦU NỐI EXTENSION (né captcha):** thay Playwright/CDP bằng cầu nối trình duyệt sạch +
  extension (chrome.debugger cho click TRUSTED) ở Seller Centre → hết dính captcha "Lỗi tải" khi bấm Chi tiết.
  Nút "▶ Chạy" chạy LIÊN TỤC qua mọi shop: đăng nhập subaccount → duyệt từng shop → đọc đơn → chuẩn bị hàng →
  in phiếu → đặt/hoàn địa chỉ; nghỉ giữa shop; lặp chu kỳ.
- **Mô hình subaccount → NHIỀU SHOP:** đơn thuộc về SHOP (không phải subaccount). Màn Đơn hàng hiển thị + lọc
  theo tên shop; đẩy DB/GSheet/hub keyed theo từng shop (mỗi shop riêng biệt).
- **Cột "Ước tính" (Số tiền cuối cùng)** đọc từ trang chi tiết đơn, và **mã vận đơn bắt NGAY lúc chuẩn bị hàng**
  (đọc ở modal trước khi in phiếu) → app + Google Sheet + hub có đủ ngay trong lượt, khỏi chờ chu kỳ sau.
- **Thanh trạng thái (footer) theo CHẾ ĐỘ ứng dụng:** Shopee (tài khoản · đơn · proxy · trình duyệt) và Workspace
  (tài khoản BigSeller · shop · acc Shopee · proxy · máy online · trình duyệt); chế độ Full hiện cả 2 phần.
- **Hub:** nhận đơn/phiếu/mã vận đơn đúng từng shop (đẩy lại khi mã vận đơn xuất hiện; phiếu đẩy sau khi đơn đã
  lên hub); trang /orders mặc định lọc "Chờ lấy hàng"; hiển thị ngày giờ theo múi giờ Việt Nam.
- **Chỉnh UI/nhỏ:** log rõ khi bấm Dừng (kể cả lúc nghỉ giữa chu kỳ); cột Shop rộng hơn; gộp các nút hành động
  về Ribbon (bỏ nút trùng ở danh sách + chi tiết tài khoản).

## v1.4.0 — 2026-07-22

- **Gộp 2 app vào cùng 1 app**: app xử lý Đơn hàng Shopee nay được tích hợp thẳng vào Shopee Suite
  thành **module Đơn hàng** — không còn chạy 2 app riêng. Từ mốc này, sửa lỗi/chỉnh nhỏ đi theo 1.4.x,
  thêm tính năng mới sẽ lên 1.5+.
- **Module Đơn hàng**: đồng bộ đơn Shopee; đẩy đơn mới lên hub sau mỗi lần sync; hub thêm domain
  Đơn hàng và thông báo "đơn mới" về Slack / Discord / Telegram.
- **Auto-login Shopee + xác nhận qua email**: tự đăng nhập, mở hộp thư Hotmail/Outlook, tìm mail xác
  nhận của Shopee và bấm link "Tại đây", né captcha bằng cách đổi hồ sơ trình duyệt.
- **Hồ sơ trình duyệt riêng theo từng browser** (mỗi tài khoản × mỗi browser) để tránh lẫn phiên.
- **Ribbon menu 4 tab kiểu Office** + gộp 2 màn Cài đặt vào một chỗ.
- Quản lý tài khoản module Đơn hàng: xóa nhiều tài khoản đã tick bằng nút thùng rác; nới rộng combobox
  cỡ trang cho khỏi bị mũi tên dropdown che số.

## v1.3.6 — 2026-07-13

- **Hub ra lệnh cập nhật app cho toàn fleet**: trang Máy client trên hub có nút ⬆ Cập nhật (từng máy /
  tất cả máy online lệch bản) — client tự tải bản mới, dừng êm rồi khởi động lại, báo tiến trình về hub;
  apply hỏng thì báo failed đúng 1 lần (không lặp restart). Nút tay trong Settings → Hiệu năng vẫn dùng
  được, đi chung một đường với lệnh hub.
- LƯU Ý: máy đang chạy bản ≤1.3.5 chưa có handler nhận lệnh — đợt này vẫn bấm tay "Cập nhật & khởi động
  lại" lần cuối; từ v1.3.6 trở đi hub điều khiển hoàn toàn.

## v1.3.5 — 2026-07-13

- Lưới **Dữ liệu** (màn Dữ liệu + tab Dữ liệu trong Workspace): phân trang chuyển **xuống dưới lưới**
  (căn phải); hàng trên lưới chỉ còn các nút hành động (căn phải) — hết cảnh 2 cụm che nhau khi cửa sổ hẹp.

## v1.3.4 — 2026-07-13

- Sửa tab **📊 Thống kê** (Workspace) bị trắng trơn ở v1.3.3: DataContext đặt trên TabItem không
  truyền xuống nội dung tab (bug Avalonia #10958) — chuyển vào root content, tab hiện số liệu bình thường.

## v1.3.3 — 2026-07-13

- Workspace có tab **📊 Thống kê** mới (ngay sau "Shop & cấu hình"): thống kê từng shop × từng việc
  (Scrape / Import / Update / Tên SP) y như tab Thống kê trên web Hub — trạng thái (⏳ đang chạy · máy nào,
  ✓ xong, ■ dừng dở, • đã xếp, ✘ lỗi), số dòng đã làm, tới dòng nào, các khoảng dòng, máy chạy gần nhất
  và các máy đã tham gia; đầu tab có 4 ô tổng theo việc của tài khoản (tổng dòng + x/y shop ✓).
  Số liệu đọc từ sổ hoàn thành trên Hub, tự làm mới theo nhịp ~12–15s.

## v1.3.2 — 2026-07-13

- Các lưới Dữ liệu (app + web Hub): thêm nút **☑ Chọn tất cả** — chọn mọi dòng của trang đang xem
  (dòng đã chọn ở trang khác giữ nguyên; bỏ chọn dùng nút ✖ Bỏ chọn như cũ).

## v1.3.1 — 2026-07-13

- Các lưới Dữ liệu (app + web Hub): **bấm bất kỳ đâu trên dòng = tick/untick chọn** — không cần
  nhắm đúng ô checkbox; bấm nút ✏ hoặc chính checkbox vẫn hoạt động riêng như cũ.

## v1.3.0 — 2026-07-13

Chủ đề: **Tab "Dữ liệu" ngay trong Workspace + tab Dữ liệu từng shop trên web Hub đủ thao tác — một lõi logic dùng chung cho cả hub lẫn app.**

- Workspace có tab **Dữ liệu** mới (ngay cạnh "Shop & cấu hình"): xem/lọc/thêm/sửa/xoá/đã bán/reset
  đã bán/cấp SKU cho kho sản phẩm của tài khoản đang chọn — không cần rời màn chạy hay mở web Hub.
- Nút **↺ Đã bán = 0**: đặt lại số "đã bán" về 0 cho các dòng chọn (xoá lịch sử bán) — có ở cả tab
  Dữ liệu trên app lẫn trang /data + tab per-shop trên web Hub.
- Tab 📋 Dữ liệu của từng shop trên web Hub (Fleet) giờ đủ thao tác như trang /data: ✔ Đã bán,
  ↺ Đã bán = 0, 🆕 Sinh SKU mới, 🗑 Xoá nhiều, ✏ sửa qua form chung (bỏ sửa-trong-ô), thêm cột
  Đã bán; **lưới giãn hết chiều cao trang** thay vì lọt thỏm giữa trang.
- Dưới nắp: toàn bộ logic lưới (lọc, phân trang, chọn nhiều, thao tác, thông báo/xác nhận) rút về
  MỘT lõi dùng chung (`ProductGridEngine`) cho cả web Hub lẫn app — hành vi 2 nơi y hệt, sửa 1 chỗ
  ăn cả hai; ô Tìm per-shop nay tìm đa trường (SKU / itemId / tên / link) ngay trên kho.

## v1.2.0 — 2026-07-13

Chủ đề: **Tab "Dữ liệu" quản lý kho sản phẩm ngay trên app + việc gián đoạn chạy TIẾP phần còn thiếu (resume), không làm lại từ đầu.**

- Tab mới **Dữ liệu** (giữa Workspace và Cấu hình): quản lý kho sản phẩm Hub ngay trên app —
  lọc theo tài khoản/shop/SKU/khoảng giá/đã bán/SKU trùng trong shop, phân trang, thêm/sửa dòng
  (đủ 17 cột, SKU để trống tự sinh `B#####`), đánh dấu ✔ đã bán, 🆕 cấp SKU mới, 🗑 xoá nhiều dòng —
  y như trang "Dữ liệu" trên web Hub (nhập Excel vẫn làm trên web).
- Việc hub-giao bị dừng/lỗi giữa chừng giờ **tiếp tục được**: tiến độ import/update nhớ theo TỪNG
  sản phẩm — bấm **▶ Tiếp tục** (tab Trạng thái hoặc Workspace) là chạy nốt phần thiếu; máy khởi động
  lại tự nhận lại việc dở của chính nó; việc đã bấm Huỷ sẽ KHÔNG bị hub tự giao lại (muốn chạy lại thì
  bấm Tiếp tục); chuột phải shop → xoá tiến độ import/update để chạy lại từ đầu.
- Nút "Cập nhật & khởi động lại" **dừng êm** mọi việc đang chạy trước khi cập nhật (ghi sổ + nhả khoá
  tài khoản ngay) — hết cảnh update xong khoá acc còn treo tới 5 phút.

## v1.1.0 — 2026-07-12

Chủ đề: **Kho sản phẩm chuyển từ file Excel sang Postgres trên Hub — client đọc/ghi dữ liệu qua API, không còn đồng bộ workbook.**

- Tài khoản BigSeller có chế độ kho dữ liệu: **Kho Hub (Postgres)** — scrape/import/update/rewrite
  đọc dòng sản phẩm thẳng từ Hub theo từng khối/lượt chạy, không cần file Excel trên máy;
  acc excel-mode cũ vẫn chạy từ file local (đường chuyển tiếp) nhưng KHÔNG còn đồng bộ workbook qua Hub.
- Rewrite tên: kết quả AI ghi lên Hub theo batch kèm **journal chống mất** (mất mạng giữa chừng
  không mất tiền AI — tự đẩy lại khi có kết nối). Với acc Kho Hub, rewrite có thể chạy NGAY TRÊN HUB
  (bấm từ web, không cần máy client).
- Thêm acc/shop ngay trên client giờ **tự đẩy lên Hub** (~2s, không bao giờ xoá gì trên Hub);
  nút "Đồng bộ acc" đẩy-lên-trước-kéo-về-sau nên acc mới tạo không bị mất; acc tạo mới mặc định Kho Hub.
- UI acc Kho Hub ẩn toàn bộ khái niệm Excel (workbook/sheet/ánh xạ cột); quản lý dữ liệu
  (xem/sửa/thêm/xoá/nhập Excel/đã bán/SKU) làm trên web Hub — trang "Dữ liệu".
- SKU chuẩn `B#####`, duy nhất trong từng shop (DB cưỡng chế bằng unique index); nhập Excel
  thiếu SKU tự sinh mã.

## v1.0.16 — 2026-07-11

Chủ đề: **Workspace tách log theo từng tài khoản BigSeller + nút dừng việc của acc đang chọn**.

- 2 tab "Theo dõi Scrape" / "Theo dõi Update" giờ hiển thị log RIÊNG của tài khoản
  đang chọn bên trái — chạy nhiều acc song song không còn trộn dòng của 6 acc vào một
  ô. Mỗi acc có file log riêng (`logs\workspace-update-{tên}.log`, `workspace-scrape-{tên}.log`);
  nút "📂 Log acc này" mở file riêng, "📂 Log gộp" mở file trộn chung như cũ (vẫn ghi
  đầy đủ). Mỗi lượt chạy mới, ô log của acc đó tự bắt đầu tươi (file giữ trọn lịch sử).
- Thêm nút "■ Dừng việc shop này" ở góc phải hàng tab: dừng scrape / import / update /
  tên SP đang chạy của tài khoản đang chọn (acc khác chạy tiếp). Acc không có việc nào
  chạy → nút tự ẩn.

## v1.0.15 — 2026-07-11

Chủ đề: **Brave mở bình thường — bỏ hẳn thu nhỏ + hết nhấp nháy**.

- Mọi cửa sổ Brave automation (Update/Import, Scrape, Search, Xóa Medias) giờ mở
  BÌNH THƯỜNG theo yêu cầu. Trước đây mở thu-nhỏ kèm một watchdog quét ~10 giây liên
  tục đè cửa sổ xuống taskbar — Brave tự bung lên, watchdog lại đè xuống → chính là
  hiện tượng "nhấp nháy mở lên mở xuống" thấy ở các bản gần đây.

## v1.0.14 — 2026-07-11

Chủ đề: **kho đầy phát hiện trong vài giây đầu mỗi SP + không còn nhánh fail im lặng ở bước Lưu**.

- Đổi thứ tự bước Update: Sửa tên → tick radio Upload Image → **Import ảnh NGAY** →
  MD5 → SKU/thương hiệu → tồn/giá → vận chuyển/cân → video → AI → Lưu. Import ảnh là
  bước làm bật toast "kho đầy" nên đặt lên đầu — dính là dừng SP tức thì, chuyển sang
  dọn Media Center, không tốn công điền form + đốt AI cho SP chắc chắn không lưu nổi
  (bước Lưu có tiên quyết ảnh-đã-lên: ảnh không lên thì save không bao giờ được bấm).
- 3 lớp phát hiện kho đầy, từ nhanh tới chắc: (1) check 0,5 giây ngay sau khi OK chọn
  ảnh; (2) đọc sổ máy-ghi-toast sau timeout chờ ảnh; (3) 2 SP liên tiếp không lên ảnh
  mà không thấy toast → vẫn NGHI kho đầy → chủ động dừng-toàn-bộ + dọn (tín hiệu này
  không phụ thuộc BigSeller báo kiểu gì — đổi giao diện cũng không thoát).
- Bước Lưu hết nhánh câm: mọi đường thất bại đều in lý do — ảnh không lên (kẹt ở bước
  nào: spc_box / menu upload / file chooser / chờ ảnh hiện), bấm Lưu theo nhánh
  dropdown hay nút thường, BigSeller báo lỗi gì khi lưu, exception gì, timeout mà
  không có dialog nào hiện (kèm URL). Hết cảnh "▶ Lưu sản phẩm" rồi im bặt → "fail
  2 lần" không rõ vì sao.

## v1.0.13 — 2026-07-11

Chủ đề: **khóa SP tách 2 mảng "đang sửa" / "đã sửa xong" — SP sửa hỏng không còn bị khóa oan**.

- Trước: một SP "fail 2 lần" (ví dụ vì kho media đầy đúng lúc đó) bị GIỮ KHÓA vĩnh viễn
  trong lượt chạy — không cửa sổ nào khác được thử lại, kể cả sau khi kho đã được dọn.
- Giờ khóa tách 2 mảng: (1) "đang có worker sửa" — sửa FAIL thì nhả khóa để cửa sổ khác
  còn cơ hội (mỗi cửa sổ có 2 lượt thử riêng nên không lặp vô hạn); (2) "đã sửa THÀNH
  CÔNG" — khóa vĩnh viễn trong lượt, không ai mở lại (kể cả cửa sổ vừa khởi động lại),
  không lo update trùng / báo dòng trùng lên Hub.

## v1.0.12 — 2026-07-11

Chủ đề: **bắt được toast "kho media đầy" thật sự (v1.0.11 luôn trượt vì check trễ hơn vòng đời toast)**.

- Toast báo đầy kho tự ẩn sau ~3 giây, trong khi mọi điểm kiểm tra đều tới muộn hơn
  (đợi ảnh hiện 5s, chờ MD5 xong 10s) → v1.0.11 không bao giờ nhìn thấy toast, worker
  vẫn cố import ảnh, không kích hoạt dừng-toàn-bộ. Fix: cài "máy ghi toast"
  (MutationObserver) vào mọi tab edit ngay từ lúc mở — toast nào từng hiện, dù chỉ 1
  giây, cũng được ghi lại; các điểm kiểm tra đọc lại sổ ghi thay vì phải canh đúng
  khoảnh khắc. Mỗi SP một tab mới nên sổ tự sạch, không dính toast của SP trước.
- Thợ dọn chờ các lane khác tạm dừng (tối đa 180s) giờ log tiến độ mỗi 15s
  ("⏳ chờ các lane khác đậu: 1/4 lane · đã chờ 30s…") + "🧹 Bắt đầu dọn Material
  Center…" — trước đây khoảng chờ im lặng hoàn toàn, nhìn như treo nên dễ bấm dừng oan
  ngay sau dòng "⛔ TẠM DỪNG toàn bộ lane".
- Dòng log tự khai toast lạ giờ đọc cả sổ ghi (trước chỉ đọc DOM sống nên cũng trượt).

## v1.0.11 — 2026-07-11

Chủ đề: **Update báo được dòng lên Thống kê Hub (gốc bệnh 0 dòng) + kho media đầy thì dừng cả cụm để dọn**.

- Update: viết lại xác nhận "lưu thành công" — nhận qua 3 tín hiệu (modal thành công /
  URL rời trang edit / BigSeller tự đóng tab), thay vì bám cứng 1 selector modal (DOM
  BigSeller đang chuyển sang Vue + dialog tự đóng nhanh → hụt tín hiệu, SP publish thật
  nhưng bị coi là fail → Hub 0 dòng dù update cả buổi). Sự kiện báo dòng bắn ĐÚNG thời
  điểm đóng tab; toàn bộ selector gom về 1 helper — DOM đổi lần nữa chỉ sửa 1 file, và
  khi không nhận diện được thì log tự dump class/text dialog đang hiện để chỉnh ngay.
- Lane chết hết đổ oan "Shopee chặn (captcha)" — thông báo giờ kèm lỗi thật (lỗi edit
  không phục hồi nào, hay click bị modal chặn 9 lần).
- Media Center: bộ đếm "10 SP thì dọn kho" chuyển về đếm TOÀN account (trước đếm riêng
  từng cửa sổ → chạy 5 cửa sổ phải ~50 SP mới dọn lần đầu, cửa sổ restart lại mất đếm
  → gần như không bao giờ dọn); đếm sống xuyên restart, chỉ reset sau khi dọn xong.
- Kho media ĐẦY (toast "The Media Center space is insufficient…" / "Dung lượng lưu trữ
  của Trung tâm Media không đủ…" — cả EN lẫn VN đã xác nhận từ DOM thật, hoặc popup
  modal) → TẠM DỪNG toàn bộ cửa sổ, một cửa sổ dọn sạch kho, xong tất cả quay lại quét
  Listing. SP dính lúc kho đầy được làm lại sau khi dọn — không còn bị "fail 2 lần →
  bỏ qua oan". Toast bắt ngay tại nguồn (đồng bộ MD5, import ảnh/video — toast tự ẩn
  sau ~3s nên không đợi tới lúc lưu); toast lỗi lạ chưa nhận diện sẽ được log nguyên
  văn để bổ sung bộ nhận diện.

## v1.0.10 — 2026-07-10

Chủ đề: **soi được vì sao Thống kê Hub 0 dòng update/import + không đốt giờ khi workbook chưa sẵn sàng**.

- Update: workbook không có dòng nào đủ điều kiện (cột G "Tên đã sửa" trống hết — chưa
  chạy Tên SP) → DỪNG NGAY trước khi mở Brave kèm hướng dẫn, thay vì mở từng SP để bỏ
  qua hàng giờ rồi vẫn báo "✓ xong". Cuối mỗi lane log tổng kết "Σ update OK X · bỏ
  qua Y (không-trong-sheet Z) · đã báo Thống kê N dòng" — nhìn 1 dòng biết lỗi ở
  workbook/sheet hay ở đường báo cáo.
- Import: SP import xong mà không khớp được dòng sheet (id crawl ≠ id sheet) giờ được
  log + đếm (trước im lặng, Thống kê thiếu dòng không ai biết); cuối lượt log tổng kết.
- Đẩy dòng lên Thống kê Hub thất bại (mạng/hub lỗi) giờ hiện cảnh báo ở tab Log
  (throttle 1 dòng/60s) — trước nuốt im lặng mọi lỗi.
- Hub web (deploy riêng): thẻ Thống kê ưu tiên trạng thái "⏳ đang chạy · máy X" theo
  lease đang sống — hết cảnh shop đang chạy mà thẻ báo "chưa chạy" khi ledger chưa có
  bản ghi (per-row chưa về / operator vừa reset).

## v1.0.9 — 2026-07-10

Chủ đề: **gộp cấu hình chạy về mức tài khoản + Hub giao việc kèm tham số + quỹ Brave**.

- Workspace: 2 khối cấu hình (SCRAPE / UPDATE) gộp thành 1 — "Cấu hình CHẠY (tài khoản
  này · máy này)": Từ dòng, Đến dòng, Dòng/lần, Số process (áp dụng MỌI op — import
  hết bị ép 1 lane), Số tk/khung, Reload(s), Ảnh Update, Thư mục video (1 ô chung cho
  cả scrape lẫn update — trước đây ô video scrape không được lưu, mở lại app chạy nhầm
  D:\videos). Cấu hình theo TÀI KHOẢN, shop không set riêng nữa; tự chuyển từ cấu hình
  cũ 1 lần. Riêng-máy, không bị sync Hub đè.
- Settings → Hiệu năng: bấm Lưu báo Hub ngay trần cửa sổ Brave của máy (hiện cột
  "Brave max" ở trang Máy client trên Hub); heartbeat cũng luôn kèm số này.
- Hub giao việc kèm tham số Số process / Số tk·khung / Reload(s) — client chạy theo
  tham số Hub (0 = dùng cấu hình máy); thư mục video/ảnh luôn dùng của máy client.
- Quỹ Brave phía client: tổng cửa sổ các việc hub-giao không vượt trần máy; việc cuối
  được cấp phần còn thiếu (max − đã dùng), hết quỹ thì việc nằm "đã xếp" chờ nhả quỹ
  — không còn bị đánh "failed" oan vì chờ lâu.
- Hub web (deploy riêng): DB tự migrate 4 cột mới (machines.max_brave +
  assignments.processes/frame_size/reload_seconds), tương thích client cũ 2 chiều.

## v1.0.8 — 2026-07-10

Chủ đề: **hủy việc Import/Update/Tên SP giữa chừng bị báo nhầm "✓ xong"**.

- Hủy việc hub-giao (hoặc bấm ■ dừng) khi shop đang chạy → engine thoát êm không ném
  exception (vòng ngoài check IsCancellationRequested ở đầu vòng; supervisor đa-lane
  cố ý nuốt OperationCanceledException để lane nghỉ hưu) → tầng workflow tưởng chạy
  trọn vẹn, đẩy ledger `completed` → ô trên Hub hiện "✓ xong" oan. Nguy hiểm hơn:
  auto-dispatch coi op đã xong nên nhảy sang op kế (vd Update dở → chạy Tên SP), bỏ
  sót SP chưa làm. Giờ sau khi engine trả về, kiểm tra token hủy: bị hủy → báo
  `stopped` ("■ dừng dở") thay vì `completed`; áp cho cả 3 op Import/Update/Tên SP.

## v1.0.7 — 2026-07-10

Chủ đề: **popup ngôn ngữ BigSeller tái phát khi UI đã là tiếng Việt — đổi cách nhận diện**.

- Update/Import/Material Center: sau khi ta chọn "Tiếng Việt" (fix v1.0.5), BigSeller
  chuyển CẢ popup guide sang tiếng Việt → cách nhận diện cũ theo text tiếng Anh
  ("switch the language") không thấy nữa → dropdown ngôn ngữ bị guide banh ra vẫn
  đè cột Thao tác, không click được Edit. Giờ nhận diện theo CẤU TRÚC DOM (class
  `language_switch_guide`/`guide_mask` + menu `sub_lang_nav_setting_list` đang hiện,
  check visible bằng `getClientRects` — bắt được cả mask position:fixed); text tiếng
  Anh chỉ còn là fallback.
- UI chưa phải tiếng Việt → vẫn chọn hẳn "Tiếng Việt" như trước. UI ĐÃ là tiếng Việt
  (click lại vô nghĩa, menu vẫn treo) → click X của guide nếu có, gỡ hẳn node
  guide/mask và ép ẩn dropdown đang banh (`display:none !important`, thắng CSS hover)
  — không phụ thuộc handler của BigSeller nên chắc chắn trả lại nút Edit.

## v1.0.6 — 2026-07-09

Chủ đề: **mất session BigSeller giữa chừng tự đăng nhập lại + Xóa Medias hết báo trống oan**.

- Update/Import: khi BigSeller đá phiên GIỮA lúc chạy (lane restart), trước đây guard
  TTL 4h coi phiên "còn tươi" nên không đăng nhập lại → lane quay vòng vô ích tới hết
  TTL. Giờ `EnsureCookieAsync` phát hiện cả phiên profile lẫn cookie file đều chết →
  `Invalidate` dấu TTL → bước auto-login ngay sau đó ĐĂNG NHẬP LẠI thật (cần Email +
  Mật khẩu; captcha giải bằng AI như đầu phiên).
- Xóa Medias / dọn Material Center: mạng chậm làm grid render trễ → script đọc nhầm
  trạng thái loading thành "hết file để xóa" rồi tự đóng. Fix 3 lớp: chờ list sẵn sàng
  tối đa 30s trước khi tin dấu "trống"; veto khi vẫn đếm được checkbox item; mọi kết
  luận "trống" phải xác nhận 2 lần liên tiếp (có reload giữa 2 lần). Popup "Guide:
  switch the language" ở Material Center cũng được đóng/chọn Tiếng Việt.

## v1.0.5 — 2026-07-09

Chủ đề: **hết kẹt "nhấp nháy" ở Listing vì popup chọn ngôn ngữ BigSeller**.

- Update/Import: popup "Guide: Click here to switch the language" (không phải
  ant-modal) chặn click Edit khiến vòng update retry mãi ở Listing. Thêm
  `DismissLanguageGuideAsync`: ưu tiên **chọn hẳn "Tiếng Việt"** khi menu ngôn ngữ
  đang hiện (BigSeller nhớ lựa chọn → lần sau không hiện), không thì đóng X → ESC.
  Nối ở 4 điểm: vào Listing, click Edit bị chặn (Update), mở tab "Đã nhận" và nút
  Import to Stores bị chặn (Import).
- Brave automation thêm `--noerrdialogs`: chặn dialog "Brave Browser quit
  unexpectedly / send diagnostic" (browser-chrome, không click tự động được) hiện
  đè sau lần crash trước.

## v1.0.4 — 2026-07-09

Chủ đề: **Brave tự động không còn cướp focus màn hình**.

- Mọi cửa sổ Brave AUTOMATION (Scrape xoay vòng, Search, Update/Import, mở lại khi
  hồi phục) giờ mở **thu nhỏ dưới taskbar, không cướp focus** app bạn đang dùng.
  Fix 3 lớp: Scrape + Search trước đây phóng cửa sổ bình thường (thiếu cờ thu nhỏ);
  thêm watchdog `BraveWindowMinimizer` quét ~10s sau mỗi lần phóng để hạ cả cửa sổ
  do Brave fork/mở lại (STARTUPINFO chỉ ép được cửa sổ đầu của stub) + trả focus
  về cửa sổ đang làm việc. Đã verify bằng harness thật: cửa sổ nằm taskbar, foreground
  không rời app.
- Thêm cờ chống throttle (`--disable-backgrounding-occluded-windows` …) cho Search +
  Update/Import để chạy nền thu nhỏ không bị Chromium bóp timer/renderer (Scrape đã có).
- Cửa sổ TƯƠNG TÁC (mở profile giải captcha, đăng nhập tay) vẫn hiện + focus bình thường.

## v1.0.3 — 2026-07-09

Chủ đề: **xóa media BigSeller theo yêu cầu + đếm dọn media theo lần bắt đầu sửa**.

- Trang cấu hình BigSeller thêm nút **🗑 Xóa Medias**: xóa toàn bộ thư viện ảnh
  (Material Center) của tk đang chọn theo yêu cầu — mở Brave riêng bằng cookie tk
  (profile `-mediaclean` + port riêng, không đụng update đang chạy), dọn xong tự
  đóng; có nút ■ Dừng, log về khung log của trang.
- Update sản phẩm: dọn Material Center sau **10 lần BẮT ĐẦU sửa SP** (đánh dấu
  ngay khi vào sửa, kể cả lưu fail — vì ảnh đã bị đẩy vào kho từ lúc đó) thay vì
  10 lần lưu thành công như trước; SP mở ra rồi bỏ qua không tính.
- Hub web (deploy riêng, không thuộc gói client): luật giao việc mới —
  **1 acc = 1 client + 1 việc tại 1 thời điểm (bất kể scrape/import/update/tên SP,
  vì chung cookie); 1 client chạy NHIỀU acc song song** (bỏ luật "1 client = 1 acc"
  từng khiến ghim 3 shop × 3 acc vào 1 máy mà chỉ 1 cái chạy); việc cùng acc xếp
  hàng chạy nối tiếp thay vì failed oan sau 60s.

## v1.0.2 — 2026-07-09

Chủ đề: **chuyển cấu hình AI từ desktop lên Hub** (quản lý tập trung, các máy tự đồng bộ).

- Cấu hình AI (provider đang dùng, model, API key, batch, system prompt) chuyển từ
  Settings desktop lên **Hub web** (trang Cấu hình AI, tách tab Cấu hình/Prompt).
  Client tự kéo về qua `HubConfigSync`; thêm `Shopee.Core/Ai/HubAiConfig.cs`.
- Settings desktop **bỏ tab "Nhà cung cấp AI"** — còn Hiệu năng, máy/Hub, và card
  "Phiên bản & cập nhật".
- Các engine dùng AI (auto-login đọc captcha BigSeller, rewrite tên sản phẩm,
  update field) đọc config AI theo nguồn mới từ Hub.
- Delta so với v1.0.1: 10 file thay đổi.

## v1.0.1 — 2026-07-09

Bản đầu tiên chứng minh trọn vòng tự cập nhật qua GitHub (client 1.0.0 tự phát hiện,
tải **delta** và nâng cấp). Nội dung chính (main tới `260cc33`):

- **Đợt dọn dẹp 3 — phía suite**: mổ `BigSellerProductUpdateRunner` thành partial
  4 file + `MaterialCenterCleaner` + base `BigSellerBraveRunner` (Playwright);
  5 ViewModel kế thừa `ModuleViewModelBase` + `AccountLeaseScope`; Core thêm
  `BraveArgsBuilder`, `PrepareProfileForLaunch`, bảng route `HubRoutes`,
  retry AI gộp vào `AiChat.ExecuteWithRetryAsync`.
- **Đợt dọn dẹp 3 — hub web**: fix XSS, `FleetPageBase`, `HubIcons`/`ConfigSave`,
  `HubDatabase` tách 8 partial, stream file, chuẩn hoá confirm UX, responsive;
  xoá trang /locks + /config/scrape; `SheetMapService`.
- **Đợt dọn dẹp 4**: gộp login BigSeller về Core (`BigSellerLoginForm`),
  `ObservableProjection` (Store.Changed→Reload giữ selection), build 0 warning.
- Client: Settings bỏ sao lưu thủ công (dùng Hub sync); ledger thêm `MachineIds`;
  UI BigSeller tách khung log riêng.
- Delta so với v1.0.0: 12 file thay đổi, 141 file gỡ bỏ (kết quả dọn dẹp).

## v1.0.0 — 2026-07-08

Bản Velopack đầu tiên — nền tảng tự cập nhật:

- Cài qua bộ cài Velopack (`Setup.exe`), tự kiểm tra + tải bản mới ở nền lúc mở app,
  áp dụng khi bấm "Cập nhật & khởi động lại" (không tự restart giữa job).
- Version tập trung tại `version.txt` (nướng vào assembly lúc build); heartbeat gửi
  `AppVersion` lên Hub để soi version từng máy trong fleet.
- Hub /stats: thống kê dòng ledger theo shop; `LogBuffer` chống đơ log workspace.
- Đã gồm kết quả đợt dọn dẹp 1+2 trước đó: xoá hub nhúng WPF (thay bằng
  `server/Shopee.Hub.Web` độc lập), hợp nhất cookie engine về Core
  (`BigSellerCookieEngine`/`CdpClient`/`KiotProxyClient`).
- Scaffold ký số Azure Trusted Signing (`signing/`) — tuỳ chọn, chỉ cần cho máy
  bật Smart App Control.
