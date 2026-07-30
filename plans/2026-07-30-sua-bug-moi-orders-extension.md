# Plan: Sửa bug mới phía orders + extension shopee-orders (đợt B1)

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

Review đa-agent 30/07 (có xác minh đối kháng) tìm ra loạt bug trong code viết tuần qua ở app Đơn hàng (`orders/` — WPF XuLyDonShopee, xử lý đơn Shopee qua extension bridge `extensions/shopee-orders/`) + vài mảnh code chết sót từ đợt dọn trước. Plan này sửa toàn bộ phần thuộc khu **orders/ + extensions/shopee-orders/**. (Phần hub + thống kê nằm ở plan B2 chạy song song — KHÔNG sửa file trong `server/`, `suite/` ở plan này.)

Bối cảnh nghiệp vụ trả hàng: extension đọc trang "Đơn Trả hàng Hoàn tiền" của Seller Centre; C# (`OrdersBridgeSession.CheckDonTraHangAsync`) so số hiện tại với mốc `return_count_last_tra_hang` (per shop) bằng `TraHangParser.QuyetDinhCheck` — Tăng thì đọc delta dòng mới lấy mã trả hàng vào bảng `return_codes` rồi đẩy Google Sheet; Giảm thì chỉ chốt mốc. Vì vậy **mốc chỉ được phép ghi từ số đã xác nhận là của đúng tab trả hàng** — ghi nhầm số tab "Tất cả" vào là các yêu cầu mới bị nuốt vĩnh viễn.

## 2. Phạm vi

- **Làm:** 3 nhóm dưới. Chỉ đụng `orders/**` và `extensions/shopee-orders/**`.
- **Không làm:** không sửa `server/`, `suite/`, extension khác; không đổi hành vi delay/nhịp thao tác chuột-phím hiện có (anti-bot); không commit (Fable commit); không xoá bảng/dữ liệu SQLite cũ (chỉ bỏ code).

## 3. Các bước thực hiện

### Nhóm A — Bug luồng trả hàng (nặng nhất)

**A1. Mốc trả hàng bị đầu độc khi không chọn được tab** — `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs` (~dòng 981-1042, `CheckDonTraHangAsync`).
Hiện trạng: khi `doc.TabTraHang == false` (extension không click được tab, hoặc field thiếu → ParseKetQua mặc định false) code chỉ log cảnh báo rồi VẪN chạy `QuyetDinhCheck` + `_saveReturnCount!(shopLogin, soMoi)` — ghi số tab "Tất cả" (gộp cả Đơn Hủy/Giao không thành công) vào mốc tab trả hàng. Kịch bản: vòng N mốc 10; vòng N+1 tab trượt → soMoi=141 (Tất cả) → nhánh Tăng chốt mốc 141; vòng N+2 tab OK → 12 → nhánh "Giảm, chỉ cập nhật mốc" → mọi yêu cầu phát sinh giữa chừng bị nuốt, không vào `return_codes`, không lên Sheet.
Sửa: đối xứng với ca `SoYeuCau == null` — khi `!doc.TabTraHang` thì **bỏ lượt** (log cảnh báo + return, mốc giữ nguyên, không gọi QuyetDinhCheck/_saveReturnCount).

**A2. `tabTraHang=true` đặt mù, không verify tab active** — `extensions/shopee-orders/background.js` (~dòng 1943-1953, `doReadReturnRequests` bước 3).
Hiện trạng: sau `trustedClick(ct.x, ct.y)` gán `tabTraHang = true` NGAY, rồi chờ tối đa 8s xem ô tổng/số dòng đổi — hết 8s không đổi vẫn đi tiếp với true → click TRƯỢT không phân biệt được với "hai tab cùng số" → C# không log cảnh báo và tin mốc sai (chính là đầu vào của bug A1).
Sửa: sau vòng chờ, gọi lại `pageLocateReturnCaseTab`; chỉ giữ `tabTraHang = true` khi kết quả `{daDung:true}`; ngược lại đặt `false` (để C# theo A1 bỏ lượt). Đồng thời: trong vòng chờ 8s, break sớm khi `pageLocateReturnCaseTab` trả `daDung:true` (khỏi đốt trọn 8s mỗi shop khi hai tab cùng số — trường hợp phổ biến).

**A3. Retry click "Chuẩn bị hàng" bấm mù không probe lại modal** — `extensions/shopee-orders/background.js` (~dòng 1673-1683).
Hiện trạng: mỗi attempt bấm `trustedClick(prep.x, prep.y)` rồi probe `pageModalHasTitle` 4.5s; hết probe sleep 500ms và BẤM LẠI NGAY không kiểm tra modal. Máy chậm modal dựng ~5s → cú click kế đáp giữa viewport lúc modal ĐANG mở (prep.y ≈ tâm màn do scrollIntoView center) → trúng mask (modal đóng, lặp mở/đóng, báo "không mở được modal" oan → FaultCurrent cả shop) hoặc trúng nút TRONG modal → đơn arrange với phương thức giao mặc định sai. Regression so bản cũ (bấm 1 lần chờ 10s).
Sửa: ngay TRƯỚC mỗi cú re-click, probe `pageModalHasTitle` một lần (true → break, không bấm); kiểm tra thêm không có `.eds-modal__box` đang hiển thị trước khi bấm; tổng thời gian chờ mỗi attempt nâng lại ≥ 10s như hành vi cũ.

### Nhóm B — Notify + đẩy mã trả hàng

**B1. Notify "Có đơn trả hàng" bỏ sót đơn ĐÃ DỌN khỏi app** — `orders/XuLyDonShopee.App/Services/AccountSession.cs` (~dòng 1068-1089, `saveReturnCodes`).
Hiện trạng: notify chỉ bắn khi `kq.DaGhi > 0` với `kq.CapDaGhi` (cặp ghi vào bảng `orders` — đơn còn sống); kết quả `LuuMaTraHang` (mã MỚI thật, gồm đơn đã bị `NenXoaDonKetThuc` dọn) bị vứt — mà đa số mã trả hàng thuộc đơn đã dọn (chính là lý do bảng `return_codes` tồn tại).
Sửa:
- Standalone (không nối Hub): bắn webhook local "đơn trả" theo danh sách cặp (order_sn, mã) MỚI của `LuuMaTraHang` (không phải `kq.CapDaGhi`).
- Nối Hub: gửi `HubClient.ReportOrdersAppAlertAsync` (route `/api/orders/app-alert` có sẵn) với hợp đồng **`Kind = "don_tra"`, `ShopName` = shopLogin, `AccountLabel` như hiện dùng, `Detail` = "SN1=CODE1; SN2=CODE2"** (danh sách cặp mới). Hub-side nhận Kind này do plan B2 làm (chạy song song) — cứ gửi đúng hợp đồng, không sửa server ở plan này. Chống trùng: chỉ gửi các cặp `LuuMaTraHang` báo là mới ghi/đổi trong lượt này.

**B2. Mốc chống spam cảnh báo địa chỉ không được nhả** — `AccountSession.cs` (~dòng 684-731, `StartCanhBaoDiaChiInBackground`).
Hiện trạng: mốc `_mocCanhBaoDiaChi` đặt TRƯỚC khi gửi; nhánh gửi-local-thất-bại có TryRemove nhả mốc, nhưng nhánh `okHub == false` + `GetNotifyWebhookUrlLoiApp()` trống thì return mà KHÔNG nhả → Hub 502 đúng lúc → 60 phút câm dù chưa tin nào được gửi.
Sửa: nhả mốc (TryRemove như dòng ~731) ở cả nhánh đó — quy tắc: **chỉ giữ mốc khi ít nhất MỘT kênh đã nhận tin**.

**B3. Cơ chế "mã đổi → đẩy lại" chết vì Apps Script chỉ ghi ô trống** — `orders/XuLyDonShopee.Core/Data/ReturnCodesRepository.cs` (~dòng 63-71) + `orders/gsheet-apps-script/Code.gs` (ghiTruong → ghiNeuTrong ~dòng 419-426, file phụ ~dòng 241).
Hiện trạng: client reset `gsheet_synced_at=NULL` khi code đổi để đẩy lại, nhưng script `ghiNeuTrong` chỉ ghi ô TRỐNG → mã cũ nằm mãi trên sheet, lượt đẩy vẫn `r.ok=true` → `DanhDauDaDay` → hỏng im lặng.
Sửa phía script (Code.gs trong repo): với payload chỉ-có-mã-trả (`chiDienNeuCo === true`), cột `donTraHang` được phép **GHI ĐÈ khi giá trị KHÁC** (cột này do máy ghi, không phải người gõ) — áp dụng cả tab chính lẫn file phụ. Giữ nguyên hành vi các cột khác. Ghi chú đầu file Code.gs + CHANGELOG: **user phải redeploy Apps Script TAY trên script.google.com TRƯỚC khi release client** (script cũ gặp payload chỉ-mã-trả sẽ append dòng gần-rỗng — lý do bắt buộc thứ tự).

**B4. `DemChuaDay` chết → badge "⏳ Chờ đẩy" thiếu mã trả tồn** — `ReturnCodesRepository.cs:109` + worker outbox.
Sửa: nối `DemChuaDay` vào chỗ tính số tồn của `HubOutboxWorker.DemTon`/`OutboxPending` để badge phản ánh cả mã trả hàng chưa đẩy (tìm chỗ tính hiện tại trong `orders/XuLyDonShopee.Core/Services/HubOutbox.cs`).

### Nhóm C — Dọn code chết sót (đợt 2 cũ)

**C1. Cụm proxy Core mồ côi** (b2310c5 đã gỡ proxy runtime + màn Proxy, cụm dưới hết caller):
- Xoá: `ProxyRotator`, `KiotProxyClient` + `IKiotProxyClient`, `KiotKeyPool`, `ProxyParser` (orders/XuLyDonShopee.Core/Services hoặc lân cận — xác nhận bằng grep 0 caller ngoài test trước khi xoá từng file) + các file test tương ứng (~7 file test proxy).
- `ProxyHealthChecker`: chỉ còn `ToProxyAddress` được `BraveLaunchArgs.cs:89` gọi khi `proxy != null` mà caller sống duy nhất truyền null (`OrdersBridgeSession.cs:440`) → gỡ tham số proxy khỏi đường đó, rồi xoá `ProxyHealthChecker` nếu hết caller.
- `ProxyRepository`: đang khởi tạo ở `AppServices.cs:287` chỉ để đếm status bar (`MainViewModel.cs:109`) → gỡ cả wiring lẫn hiển thị đếm proxy. KHÔNG xoá bảng trong app.db.

**C2. Extension orders — code chết:** trong `extensions/shopee-orders/background.js`: xoá cụm `withDebugger` (~1036-1041), `keyInfo` (~1053), `dbgType` (~1063), `dbgEnter` (~1081) (kiểm tra caller trước — `dbgClick`/`trustedClick` đang SỐNG, không kéo nhầm); gỡ `'hello'` khỏi điều kiện message (content.js chỉ gửi `'wake'`); xoá `releaseDbg` (~1097) + sửa comment ~1089 cho khớp hành vi chủ đích 24f7234 (giữ debugger attach xuyên suốt để banner đứng yên).

**C3.** `orders/XuLyDonShopee.Tests/BraveCleanPocArgsTests.cs:13`: chuỗi ví dụ trỏ `extensions/shopee-orders-test` (thư mục đã xoá) → đổi sang placeholder trung tính.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` 0 lỗi 0 warning; `dotnet test orders/XuLyDonShopee.Tests` xanh, KHÔNG tụt so baseline 1449 pass (test proxy bị xoá thì tổng giảm đúng số test đã xoá chủ đích — ghi rõ số).
- [ ] `node --check extensions/shopee-orders/background.js` sạch.
- [ ] Thêm/điều chỉnh test: (1) CheckDonTraHang bỏ lượt khi TabTraHang=false — mốc không đổi; (2) notify đơn-trả dựa trên kết quả LuuMaTraHang (mock/fake ở tầng repo được); (3) test hiện có của TraHangParser không đổi hành vi.
- [ ] Grep sau dọn: `ProxyRotator|KiotKeyPool|ProxyParser|withDebugger|dbgEnter|releaseDbg` = 0 hit trong source (trừ plans/).
- [ ] Báo cáo: liệt kê từng mục A1→C3 đã làm gì, file+dòng, test nào cover.

## 5. Rủi ro & lưu ý

- `background.js` là code anti-bot nhạy: CHỈ thêm probe/verify như mô tả, không đổi delay/easing/thứ tự thao tác hiện có.
- A1+A2 là một cặp: extension hạ cờ → C# bỏ lượt. Đừng "sửa giúp" bằng cách tự đoán số ở C#.
- B1: đường hub nhận `Kind="don_tra"` do B2 làm — nếu build lúc test thiếu phía hub cũng không sao (client chỉ POST fire-and-forget).
- Xoá file nào phải grep 0-caller ngay trước khi xoá (code đã trôi so với lúc review).
- KHÔNG commit; báo cáo xong để Fable nghiệm thu + commit.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Kết quả kiểm chứng (sau vòng chỉnh theo phản hồi nghiệm thu):** `dotnet build ShopeeSuite.sln` → **0 lỗi,
0 warning**; `dotnet test orders/XuLyDonShopee.Tests` → **1427 pass / 0 fail**;
`node --check extensions/shopee-orders/background.js` sạch (Code.gs cũng check qua bản sao `.js` → sạch). Grep
nghiệm thu `ProxyRotator|KiotKeyPool|ProxyParser|withDebugger|dbgEnter|releaseDbg` = **0 hit** trong source.

**Số test 1449 → 1427 (−22)** = xoá 37 ca + thêm 15 ca:
- Xoá cùng code chết: `ProxyRotatorTests` 5 · `KiotProxyClientTests` 13 · `KiotKeyPoolTests` 7 · `ProxyParserTests` 7 ·
  `ProxyHealthCheckerTests` 2 = **34**; `BraveLaunchArgsTests` bỏ 3 ca proxy (tham số `proxy` đã gỡ) = **37**.
- Thêm: `TraHangBoLuotSaiTabTests` 5 · `NotifyDonTraKhoMaTests` 10 = **15**.

### A1 — bỏ lượt khi không chắc đúng tab
- `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`: thêm `enum SauDocTraHang` (d.63), `record struct
  LuotDocTraHang` (d.78), hàm THUẦN `QuyetDinhLuotTraHang` (d.960, cùng khuôn `QuyetDinhSauDatDiaChi` sẵn có);
  `CheckDonTraHangAsync` nay rẽ theo hàm này — nhánh `BoLuotSaiTab` (d.1018) **log + return TRƯỚC**
  `QuyetDinhCheck`/`_saveReturnCount` ⇒ mốc giữ nguyên. Cảnh báo `SortApplied` giữ nguyên (chỉ log).
  Tách hàm thuần (thay vì `if` trần) để test được mà không cần trình duyệt, và để `soMoi` không còn `.Value`
  (tránh CS8629 khi build cảnh-báo-sạch).
- `orders/XuLyDonShopee.Core/Services/TraHangParser.cs`: sửa doc `KetQuaDocTraHang` — `TabTraHang=false` nay là
  **bỏ lượt**, không còn "chỉ cảnh báo".
- Test: `orders/XuLyDonShopee.Tests/TraHangBoLuotSaiTabTests.cs` (5 ca, gồm ca diễn lại 3 vòng 10→141→12 chứng minh
  mốc rác đẩy vòng sau vào nhánh `Giảm`).

### A2 — extension xác nhận tab thật sự active
- `extensions/shopee-orders/background.js` `doReadReturnRequests` bước 3 (~d.1905-1940): bỏ `tabTraHang = true` đặt mù
  ngay sau `trustedClick`; sau vòng chờ gọi lại `pageLocateReturnCaseTab` (d.1935) và chỉ `true` khi `daDung`
  (d.1936). Trong vòng chờ 8s thêm probe `pageLocateReturnCaseTab` → break sớm khi tab đã active (khỏi đốt trọn 8s
  mỗi shop khi hai tab cùng số). Thông điệp `progress` đổi cho khớp hành vi mới (C# sẽ bỏ lượt).
- KHÔNG đổi delay/easing/thứ tự thao tác nào khác.

### A3 — retry "Chuẩn bị hàng" không bấm mù
- `background.js`: thêm hàm trang `pageAnyModalVisible` (d.551); vòng retry (d.1637-1657): **trước mỗi cú bấm** probe
  `pageModalHasTitle` (d.1641, true → break, không bấm) và probe modal-bất-kỳ — đang có modal thì `sleep(500);
  continue` mà **không tiêu một lượt bấm**; `probeDeadline` nâng 4.5s → **10s** (d.1650, bằng hành vi trước khi có
  retry). Giữ nguyên `shipDeadline` 18s và trần 4 lượt bấm (thực tế ≈2 lượt × 10s).

### B1 — notify đơn trả bám kho mã
- `orders/XuLyDonShopee.Core/Data/ReturnCodesRepository.cs`: `LuuMaTraHang` đổi kiểu trả về `int` →
  `KetQuaLuuMaTraHang(int DaGhi, IReadOnlyList<(OrderSn, Code)> CapMoi)` (d.27, 50-89) — gom đúng cặp vừa thêm/đổi.
- `orders/XuLyDonShopee.App/Services/AccountSession.cs`: callback `saveReturnCodes` nay notify theo `kqMa.CapMoi`
  (không còn `kq.CapDaGhi` làm nguồn, không còn điều kiện `PushOrdersToHub is null`);
  `StartNotifyDonTraInBackground` nhận thêm `capDonConSong` + `shopLogin`, đi hai đường: **có Hub** →
  `ReportAppAlertToHub` với `Kind="don_tra"` (hằng `KindDonTra`), `ShopName=shopLogin`, `AccountLabel`=email tk,
  `Detail` do hàm thuần `MoTaCapDonTra` dựng `"SN1=CODE1; SN2=CODE2"`; **không Hub** → webhook local như cũ (vẫn
  qua `CoNenGuiNotifyLocal`, hành vi + nội dung tin không đổi).
- **Chống hai tin một mã (chốt ở vòng nghiệm thu):** nhánh Hub chỉ gửi các cặp thuộc đơn **ĐÃ BỊ DỌN** khỏi
  `orders` — hàm thuần `LocCapDonDaDon(capMoi, kq.CapDaGhi)` (đối chiếu theo mã đơn); đơn CÒN SỐNG để Hub tự bắn
  qua `ReturnCodeChangedItems` của `orders/push` (`server/.../ClientApiEndpoints.cs:253`, plan B2 siết đường đó).
  Không còn cặp nào cần báo → log một dòng rồi thôi. Nhánh standalone giữ TOÀN BỘ `CapMoi` (không có nguồn trùng).
- Không sửa logic `suite/` — hook `ReportAppAlertToHub` truyền `kind` thẳng qua (`OrdersModuleHost.cs:164`).
- Test: `orders/XuLyDonShopee.Tests/NotifyDonTraKhoMaTests.cs` 10 ca (đơn đã dọn: kho mã có `CapMoi`, đường cũ
  rỗng; mã không đổi → không báo lại; `KindDonTra`; `MoTaCapDonTra`; **3 ca `LocCapDonDaDon`**: bỏ đơn còn sống /
  giữ cả lô khi không đơn nào còn sống / rỗng khi tất cả còn sống; 2 ca badge B4); 4 assert trong
  `MaTraHangDocLapTests` đổi `n` → `kq.DaGhi` + kiểm luôn `CapMoi`.

### B2 — nhả mốc chống spam cảnh báo địa chỉ
- `AccountSession.cs` d.718-724: nhánh `okHub == false` + webhook local trống nay `TryRemove` mốc trước khi return
  (thành 3 lối nhả: d.723, 736, 745). Doc hàm ghi rõ quy tắc "mốc chỉ giữ khi ít nhất MỘT kênh đã nhận tin".

### B3 — Apps Script cho ghi đè mã trả hàng
- `orders/gsheet-apps-script/Code.gs`: thêm `ghiDeNeuKhac` (d.452 — ghi khi giá trị KHÁC, **bỏ qua ô có công thức**);
  `ghiTruong` thêm tham số `choGhiDe` (d.384-396); cột `donTraHang` truyền `don.chiDienNeuCo === true` ở **cả tab
  chính** (d.190) **lẫn file phụ** (d.275). Mọi cột khác + payload đơn thường giữ nguyên `ghiNeuTrong`.
- Ghi chú "SỬA 30/07/2026" ở đầu file + `CHANGELOG.md` mục **"Chưa phát hành"** có khối `⚠` nhắc **phải redeploy
  Apps Script tay TRƯỚC khi phát hành client**.

### B4 — badge "⏳ Chờ đẩy" đếm mã trả hàng
- `orders/XuLyDonShopee.App/Services/AppServices.cs`: `OutboxPending` thêm trường thứ 5 `ReturnCodes` (d.22), vào
  `Tong` + `Cong`.
- `HubOutboxWorker.cs`: `DemTon` đếm `_services.ReturnCodes.DemChuaDay` khi CÓ URL sheet (d.253, 263 — không có đích
  thì 0 để badge không kẹt); dòng log "Vòng chờ" thêm số mã trả (d.192).
- `MainViewModel.cs`: tooltip badge thêm "· N mã trả hàng".
- Test: 2 ca trong `NotifyDonTraKhoMaTests` (đẩy hỏng → badge 1; chưa có URL sheet → badge 0).

### C1 — dọn cụm proxy mồ côi
- Xoá (đã grep 0 caller ngay trước khi xoá): `Services/ProxyRotator.cs`, `Services/KiotProxyClient.cs`,
  `Services/IKiotProxyClient.cs`, `Services/KiotKeyPool.cs`, `Services/ProxyParser.cs`,
  `Services/ProxyHealthChecker.cs` + 5 file test tương ứng.
- `BraveLaunchArgs.BuildBraveArgs`: gỡ tham số `ProxyEntry? proxy` + nhánh `--proxy-server` (và `using
  ...Core.Models` không còn dùng); `ShopeeLoginService.OpenAsync` gỡ tham số `proxy`;
  `OrdersBridgeSession` d.459 gọi theo chữ ký mới. `BraveLaunchArgsTests` bỏ 3 ca proxy, giữ 1 ca chốt
  "không bao giờ có `--proxy-server`".
- `AppServices`: gỡ property + wiring `Proxies` (giữ nguyên bảng `proxies` trong app.db, giữ `ProxyRepository.cs`
  + `ProxyRepositoryTests` vì plan chỉ yêu cầu gỡ wiring/hiển thị). `MainViewModel`: gỡ `StatusProxiesText` +
  dòng đếm proxy. `suite/Shopee.Suite/MainWindow.axaml` (~d.256-266, **được phiên chính mở phạm vi đúng file
  này**): gỡ `<TextBlock ...StatusProxiesText />` + dấu `·` đứng trước + sửa comment cụm — hết binding mồ côi.
- **Giữ lại có chủ đích** (test/khu khác, không thuộc cụm chết): `KiotProxyKeyParser`, `ProxyKeyPoolMigration`,
  `ProxyRepository`, `KiotApiClientTests` + `ProxyFleetWideFailureTests` (hai file này test
  `Shopee.Proxy.Kiot`/`Shopee.Core.Proxy` của **suite**, chỉ nhắc tên KiotProxyClient trong comment).

### C2 — dọn code chết extension
- `background.js`: xoá `withDebugger`, `keyInfo`, `dbgType`, `dbgEnter`, `releaseDbg` (grep 0 caller trước khi xoá;
  `dbgSend`/`dbgAttach`/`dbgDetach`/`dbgClick`/`ensureDbg` còn sống, không đụng); bỏ `'hello'` khỏi điều kiện
  message + 2 comment nhắc `'hello'` (content.js chỉ gửi `'wake'`); viết lại comment cụm `ensureDbg` cho khớp
  hành vi 24f7234 (attach xuyên suốt, chỉ detach khi đổi tab / Chrome tự detach).

### C3
- `orders/XuLyDonShopee.Tests/BraveCleanPocArgsTests.cs` d.13, 32: `C:/ext/shopee-orders-test` → `C:/ext/ext-mau`.

### Đã chốt ở vòng nghiệm thu (phiên chính quyết — đã thi hành)
1. ~~Nguy cơ TRÙNG TIN đơn trả khi đã nối Hub~~ → **ĐÃ SỬA**: nhánh Hub chỉ gửi cặp thuộc đơn **đã dọn**
   (`LocCapDonDaDon(kqMa.CapMoi, kq.CapDaGhi)` — xem mục B1); đơn còn sống để Hub bắn qua `orders/push`
   (`server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs:253`, plan B2 siết). Standalone giữ toàn bộ `CapMoi`. +3 test.
2. ~~`StatusProxiesText` mồ côi~~ → **ĐÃ SỬA** `suite/Shopee.Suite/MainWindow.axaml` (phiên chính mở phạm vi đúng
   file này; không đụng gì khác trong `suite/`).
3. **A3 ngân sách thời gian** → phiên chính **chấp nhận giữ nguyên**: 10s/lượt bấm (cũ 4.5s), `shipDeadline` vẫn
   18s ⇒ thực tế ~2 lượt bấm thay vì 4.

### Điểm còn cần soi
4. **A2 tốn thêm 1 lượt `execInTab`/500ms** trong vòng chờ đổi tab (probe tab-strip). `pageLocateReturnCaseTab` có
   `scrollIntoView` khi tab chưa active — cùng thao tác hàm này vốn đã làm ở lần dò đầu, không thêm click/gõ nào.
5. **B3 chưa chạy được trên sheet thật** (Apps Script chỉ có bản sao trong repo): mới kiểm cú pháp. Trước khi phát
   hành client phải redeploy tay + thử một mã đổi trên sheet nháp.
6. `LuuMaTraHang` **đổi kiểu trả về** (int → record). Chỉ 1 caller production + 10 chỗ trong test; đã cập nhật hết,
   nhưng đây là breaking change trong Core nếu có nhánh khác đang dùng.
