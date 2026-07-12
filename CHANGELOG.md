# Ghi chú phát hành (CHANGELOG)

App desktop phát hành qua Velopack + GitHub Releases (kênh `win`). Client cài qua
`ShopeeSuite-win-Setup.exe` một lần, từ đó tự tải delta và cập nhật bằng nút
"Cập nhật & khởi động lại" trong Settings → Hiệu năng. Quy trình ra bản mới: sửa
`version.txt` → chạy `release-suite.cmd` (cần `GITHUB_TOKEN`).

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
