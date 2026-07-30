# Plan: Sửa bug mới phía orders + extension shopee-orders (đợt B1)

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
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

(chưa)
