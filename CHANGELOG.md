# Ghi chú phát hành (CHANGELOG)

App desktop phát hành qua Velopack + GitHub Releases (kênh `win`). Client cài qua
`ShopeeSuite-win-Setup.exe` một lần, từ đó tự tải delta và cập nhật bằng nút
"Cập nhật & khởi động lại" trong Settings → Hiệu năng. Quy trình ra bản mới: sửa
`version.txt` → chạy `release-suite.cmd` (cần `GITHUB_TOKEN`).

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
