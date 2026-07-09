# Ghi chú phát hành (CHANGELOG)

App desktop phát hành qua Velopack + GitHub Releases (kênh `win`). Client cài qua
`ShopeeSuite-win-Setup.exe` một lần, từ đó tự tải delta và cập nhật bằng nút
"Cập nhật & khởi động lại" trong Settings → Hiệu năng. Quy trình ra bản mới: sửa
`version.txt` → chạy `release-suite.cmd` (cần `GITHUB_TOKEN`).

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
