# Plan: Đợt H2 — Tính năng đơn hàng (digest, lọc, ZIP phiếu, chẩn đoán đơn kẹt)

- **Ngày:** 2026-08-06
- **Trạng thái:** hoàn thành (code + nghiệm thu; CHƯA deploy/release — xem checklist cuối file)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

6 tính năng vòng đơn hàng, đều xây trên hạ tầng sẵn: webhook OrderNotifyService, kho phiếu `/slips/{shopId}/{orderSn}`, `OrdersRepository.Query/Count` đã hỗ trợ lọc ngày, `OutboxPending` 5 loại tồn, cờ `NenXoaDonKetThuc`, `hub_push_gen` chống đua. Đêm vẫn có đơn (user xác nhận 06/08) nên mọi thứ chạy 24/7 — digest là để NGƯỜI nghỉ mà vẫn nắm tình hình, không phải để máy nghỉ.

## 2. Phạm vi

- **Làm:** 6 mục phần 3 (hub: H2.1/H2.3/H2.4; client orders app: H2.2/H2.5/H2.6).
- **Không làm:** không đụng vòng scrape/update; không deploy/release (phiên chính lo).

### 2b. HIỆN TRẠNG CÂY (cập nhật 06/08 sau các đợt A–G + H1 — dò theo symbol, plan viết trước các đợt đó)

- **`OrdersRepository` đã tách 5 partial** (đợt D): `.Sync/.Gsheet/.Hub/.SoldCount/.Query.cs`. Hàm mới phải đặt
  đúng partial theo mảng, KHÔNG dồn về file gốc (gốc chỉ giữ record + ctor + hàm bắc nhiều mảng).
- **`OrderNotifyService` vừa được H1.3 thêm 2 hàm dựng tin webhook** (máy mất nhịp / trở lại) — H2.1 thêm tin
  digest theo ĐÚNG khuôn đó, đừng chế khuôn thứ hai.
- **`/orders` của hub đã có toggle ẩn cột `?hide=` + pattern UrlState** (đợt F5): lọc "có mã trả" của H2.3 phải
  vào URL cùng cách (mặc định = vắng key), và nhớ cột bị ẩn vẫn phải khớp `colspan` khối mobile.
- **`window.confirm` đã bị xoá sạch khỏi hub** (đợt F3): mọi xác nhận mới dùng `Shared/ConfirmDialog.razor`
  (`AskAsync`), nút nguy hiểm `danger: true`.
- **`OrdersView.xaml` (client) vừa đổi ở đợt G6**: `FrozenColumnCount=2` + ContextMenu ẩn/hiện 6 cột phụ (state
  trong `OrdersViewModel.ShowCol*`, lưu key `orders_hidden_columns`). Thanh lọc ngày của H2.2 thêm vào hàng lọc
  hiện có, KHÔNG phá bố cục cột.
- **`OrdersView.xaml.cs` đăng ký `PropertyChanged` ở `Loaded`/gỡ ở `Unloaded`** (vá rò rỉ đợt G) — nếu đụng
  code-behind này thì giữ nguyên vòng đời đó.
- **Màn Thống kê đã có chip preset ngày** (G8, `ApplyDatePresetCommand`, dùng `DateTime.Today` để khớp
  `TryBuildCreatedRange`). H2.2 làm ở màn ĐƠN HÀNG — nếu tái dùng được style `dateChip` thì dùng lại.
- **Cột `machines.outbox_pending` + `MachinePresence.OutboxPending`** đã có từ H1.4 (null = máy không báo).
  H2.6 (tooltip breakdown) là phía CLIENT, đọc `OutboxPending` 5 field của `AppServices` — không đụng hub.
- **Test hiện tại (nền để so):** orders 1506 · Core 83 · hub 80. Số chỉ được TĂNG.

## 3. Các bước thực hiện

### H2.1 (Hub) Tin tổng kết cuối ngày qua webhook
- BackgroundService gửi 1 tin/ngày lúc giờ VN cấu hình (mặc định 21:00; bật/tắt + giờ trong /settings cùng section webhook). Nội dung gộp: tổng đơn "chuẩn bị hàng" phát sinh hôm nay THEO SHOP (top + tổng, mốc `first_seen_at` ngày VN như v1.7.6), số mã trả hàng mới hôm nay, shop còn cảnh báo địa chỉ active, máy đang offline. Format theo khuôn tin OrderNotifyService hiện có (Slack markdown).
- Chống gửi trùng khi hub restart quanh giờ gửi: lưu mốc "đã gửi ngày d" (bảng config/settings của hub), so ngày VN.

### H2.2 (Client) Lọc khoảng ngày ở màn Đơn hàng
- OrdersView: thêm 2 DatePicker Từ/Đến vào thanh lọc (cạnh lọc shop/trạng thái hiện có) → truyền `createdFromUtc/createdBeforeUtc` vào `OrdersRepository.Query/Count` (đã hỗ trợ — màn Thống kê đang dùng, OrderStatisticsViewModel ~:172). Đến = hết-ngày (cộng 1 ngày, so <). Nút ✕ xóa nhanh 2 ô. Đổi filter reset về trang 1 (khớp cơ chế phân trang hiện có).

### H2.3 (Hub) /orders lọc "đơn có mã trả hàng"
- Thêm checkbox/toggle "Có mã trả" vào thanh lọc Orders.razor (WHERE `return_request_code IS NOT NULL` — thêm tham số vào `Db.QueryOrdersPage`/Count). Trạng thái vào URL theo pattern UrlState. Cột "Đơn trả hàng" đã có sẵn để đối chiếu.

### H2.4 (Hub) Tải ZIP phiếu theo bộ lọc hiện tại
- Nút "⬇ ZIP phiếu" trên /orders: tải mọi phiếu PDF của các đơn khớp BỘ LỌC hiện tại có phiếu trên hub. Endpoint mới (admin-auth như trang) stream `ZipArchive` (entry = `{shop}/{orderSn}.pdf`, dùng kho `/slips/...` hiện có — đọc cách endpoint slip hiện phục vụ file để dùng đúng đường dẫn vật lý). Trần 500 phiếu/lượt — quá trần trả 400 kèm thông báo thu hẹp bộ lọc. Stream trực tiếp (không dựng file tạm to trong RAM/disk; ZipArchive trên response stream, CompressionLevel.NoCompression vì PDF nén sẵn).

### H2.5 (Client) Màn chẩn đoán "đơn kết thúc chưa dọn được" + nút đẩy lại
- Hiện chỉ có log đếm tổng (HubOutbox ~:512 "N đơn kết thúc chờ lượt sau"). Làm cửa sổ/panel mở từ badge ⏳ (hoặc menu): liệt kê từng đơn terminal chưa xóa được, mỗi đơn kèm nghĩa vụ còn thiếu suy từ ĐÚNG các điều kiện trong `NenXoaDonKetThuc` (chưa ghi sheet / chưa lên hub / phiếu chưa đẩy / chưa đếm Đã bán / mã trả chưa đẩy) — viết hàm thuần `ChanDoanDonKetThuc(order) -> danh sách nghĩa vụ thiếu` trong Core CẠNH `NenXoaDonKetThuc` để 2 luật không trôi lệch nhau (tốt nhất: NenXoaDonKetThuc gọi lại hàm chẩn đoán hoặc cùng nguồn điều kiện), + test ma trận ca.
- Nút "Đẩy lại" per-đơn: reset các cờ đã-đẩy của đơn đó (hub_synced_at + cờ gsheet_da_co_* — soi chính xác bộ cờ theo `UpsertMany` reset-conditions hiện có; `hub_push_gen` đã chống đua) để lượt outbox sau đẩy lại. Confirm trước khi reset.

### H2.6 (Client) Tooltip breakdown badge ⏳ Chờ đẩy
- Badge hiện chỉ số tổng; `OutboxPending` đã tách 5 field (AppServices ~:21–31). Tooltip liệt kê 5 dòng: đơn hub / phiếu / dòng sheet / lượt đếm Đã bán / mã trả hàng (ẩn dòng = 0). Click badge mở màn H2.5.

## 4. Tiêu chí nghiệm thu

- [ ] Build 2 solution 0 warning; 3 bộ test xanh.
- [ ] Test mới: (a) hàm chẩn đoán H2.5 ma trận ca khớp `NenXoaDonKetThuc` (đơn mà NenXoa=false ⇔ chẩn đoán ra ≥1 nghĩa vụ thiếu — property-test đơn giản trên các tổ hợp cờ); (b) digest H2.1: gửi đúng 1 lần/ngày kể cả restart (test logic mốc-đã-gửi); (c) H2.3 query lọc mã trả. Test thử phá rồi khôi phục, ghi vào báo cáo.
- [ ] H2.4: chạy hub local, tạo dữ liệu phiếu giả → tải ZIP đúng nội dung; quá trần trả 400.
- [ ] H2.2: màn Đơn hàng lọc ngày đúng (kiểm bằng DB dev có đơn nhiều ngày), reset trang 1.
- [ ] H2.5: nút Đẩy lại làm outbox lượt sau đẩy lại đơn (kiểm bằng test repository: reset cờ → GetForGsheetPush/GetForHubPush lại trả đơn đó).

## 5. Rủi ro & lưu ý

- **H2.5 nút Đẩy lại là thao tác ghi DB theo lệnh user** — reset đúng BỘ CỜ tối thiểu, đừng reset `gsheet_tab` (chống nhân đôi dòng khi sang tháng — memory `gsheet-tab-theo-thang`), đừng đụng `first_seen_at`.
- Luật "cờ đã-đẩy kẹt trạng thái cũ" từng dính (memory `push-once-flag-stale-state`) — đọc kỹ reset-conditions trong UpsertMany trước khi viết reset tay.
- H2.1 giờ gửi theo Asia/Ho_Chi_Minh — dùng đúng helper giờ VN hiện có trong repo (đã có cho first_seen_at), không tự quy đổi.
- KHÔNG commit/deploy/release.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Người thực thi:** `opus-executor` · **Ngày:** 2026-08-06 · Nền cây: `0907d24`. KHÔNG commit, KHÔNG deploy/release.

### Đã hoàn thành (6/6 hạng mục)

**H2.1 — Tin tổng kết cuối ngày (hub)**
- `orders/XuLyDonShopee.Core/Services/OrderNotifyService.cs`: thêm `TaoTinNhanTongKetNgay(...)` theo ĐÚNG khuôn 5 tin
  sẵn có (text thuần + emoji, `HoacDauHoi`, cắt top 10 shop + dòng "… và N shop nữa"). Không đẻ khuôn thứ hai.
- `server/Shopee.Hub.Web/Services/DailyDigest.cs` (mới): lõi THUẦN — `KepGio`, `NgayVn`, `DenLuotGui` (chống gửi
  trùng), `GomSoLieu`. Số "đơn chuẩn bị hàng hôm nay theo shop" DÙNG LẠI `ShopOrderSummaries` + `HomeOverview.TrangThaiCho`
  (cùng truy vấn với thẻ "Đơn chờ hôm nay" của trang chủ) nên tin và trang chủ không thể nói hai số.
- `server/Shopee.Hub.Web/Services/DailyDigestService.cs` (mới): BackgroundService nhịp 60s, xếp hàng qua
  `WebhookQueueService` — khuôn y hệt `MachineOfflineWatchService` của H1.3.
- `HubOptions.cs`: 4 khoá mới (`notify.webhook_tong_ket`, `notify.tong_ket_bat`, `notify.tong_ket_gio`,
  `notify.tong_ket_da_gui_ngay`). Mốc "đã gửi ngày d" nằm ở **bảng `settings`** (bền qua restart) chứ không phải
  bộ nhớ như cảnh báo máy offline — đúng yêu cầu plan.
- `Components/Pages/Settings.razor`: ô webhook + toggle bật + ô giờ (0–23), cùng section webhook, validate qua
  `OrderNotifyService.KiemTraUrl` như 4 kênh kia. `Program.cs`: đăng ký hosted service.

**H2.2 — Lọc khoảng ngày ở màn Đơn hàng (client)**
- `ViewModels/OrdersViewModel.cs`: `FromDate`/`ToDate`/`DateWarning`, hàm thuần `BuildCreatedRange` (Đến = hết ngày,
  hai đầu mút độc lập, quy đổi qua `TimeZoneInfo.Local` **cùng luật với `OrderStatisticsViewModel.TryBuildCreatedRange`**),
  `ClearDateFilterCommand`, đổi ngày → `CurrentPage = 1`. `CurrentFilter()` trả thêm 2 biên nên **Xuất CSV và In nhiều
  đơn tự động cùng phạm vi** với lưới.
- `Views/OrdersView.xaml`: thêm HÀNG LỌC THỨ HAI (Từ ngày · Đến ngày · ✕ · cảnh báo) ngay dưới hàng lọc cũ.
  KHÔNG đụng Grid 6 cột hiện có, KHÔNG đụng `FrozenColumnCount`/ContextMenu ẩn cột của đợt G6, KHÔNG đụng
  `OrdersView.xaml.cs` (vòng đời `PropertyChanged` ở Loaded/Unloaded giữ nguyên).

**H2.3 — /orders lọc "Có mã trả" (hub)**
- `HubDatabase.Orders.cs`: `WhereClause` thêm `coMaTra` (`return_request_code IS NOT NULL AND TRIM(...) <> ''`),
  `QueryOrdersPage` nhận tham số tuỳ chọn (caller cũ `/api/orders` không đổi).
- `Orders.razor`: checkbox "Có mã trả", vào URL bằng key **`comatra`** theo pattern `UrlState` (cố ý KHÁC key `tra`
  của `?hide=`). Vắng key = mặc định. `ClearFilters` reset luôn. Không thêm cột nên `ColCount`/colspan mobile không đổi.

**H2.4 — Tải ZIP phiếu theo bộ lọc (hub)**
- `server/Shopee.Hub.Web/Api/OrdersZipEndpoint.cs` (mới): `GET /orders/zip` (`RequireAuthorization("Web")` như
  `/slips`), stream `ZipArchive` THẲNG lên response, `CompressionLevel.NoCompression`, entry = `{shop}/{orderSn}.pdf`,
  đọc đúng kho `<DataDir>/slips/<shopId>/<sanitize(order_sn)>.pdf`. Trần 500 → quá trần trả **400** kèm lời nhắc.
- `HubDatabase.OrdersWithSlip(...)` dùng CHUNG `WhereClause` với lưới nên tổng trên trang và số phiếu trong gói
  luôn nói về cùng tập đơn. Nút "⬇ ZIP phiếu" trên `/orders` là thẻ `<a>` mang đúng bộ lọc đang xem.

**H2.5 — Màn chẩn đoán đơn kẹt + nút Đẩy lại (client)**
- `Services/OrderPersistPipeline.cs`: thêm `LaDonKetThuc`, enum `NghiaVuDonKetThuc`, `MoTaNghiaVu`, và hàm thuần
  `ChanDoanDonKetThuc(...)`. **`NenXoaDonKetThuc` nay gọi THẲNG hàm chẩn đoán** (`LaDonKetThuc(p) && ChanDoan(...).Count == 0`)
  — hai luật là MỘT, không thể trôi lệch (có property-test canh, xem mục thử phá).
- `Services/HubOutbox.cs`: tách hàm thuần `ConNghiaVuGhiSheet(p, coFileBoSung)` từ chính nhánh quyết-định-gửi của
  `PushOrdersToGsheetAsync` và dùng lại nó tại chỗ → màn chẩn đoán hỏi ĐÚNG hàm mà lượt đẩy sheet dùng.
- `ViewModels/ChanDoanDonViewModel.cs` + `Views/ChanDoanDonDialog.xaml(.cs)` (mới): quét mọi tài khoản, liệt kê đơn
  kết thúc còn nợ nghĩa vụ kèm lý do, nút "Đẩy lại" per-đơn (có `DialogService.ConfirmAsync` trước khi ghi).
- `Core/Data/OrdersRepository.cs`: `DatLaiCoDayLai(accountId, orderSn)` — đặt ở partial GỐC vì bắc qua hai mảng
  (hub + gsheet). Bộ cờ reset: `hub_synced_at=NULL`, **`hub_push_gen+1`**, `gsheet_synced_at=NULL`,
  `gsheet_da_huy`/`gsheet_da_co_van_don`/`gsheet_da_co_uoc_tinh`/`gsheet_da_co_don_tra_hang`=NULL.
  **KHÔNG đụng `gsheet_tab`, `sold_counted_at`, `gsheet_file_url`, `hub_slip_synced_at`, `created_at`** (xmldoc ghi rõ lý do từng cột).

**H2.6 — Tooltip badge ⏳ + click mở màn H2.5 (client)**
- `ViewModels/MainViewModel.cs`: hàm thuần `MoTaTonTooltip(OutboxPending)` — mỗi loại một dòng, **ẩn dòng = 0**;
  thêm `MoChanDoanDonCommand`. `DialogService.ShowChanDoanDon`.
- `suite/Shopee.Suite/MainWindow.xaml` + `Themes/Theme.xaml`: badge từ `Border` → `Button` với style mới
  `statusSegBtn` (nhìn y hệt `statusSeg`, kể cả nền hover) gắn command trên.

### Kết quả kiểm chứng (chạy thật, dán nguyên văn)

| Lệnh | Kết quả |
|---|---|
| `dotnet build ShopeeSuite.sln --no-incremental` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `dotnet build server/ShopeeHub.sln --no-incremental` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `dotnet test orders/XuLyDonShopee.Tests` | `Passed! - Failed: 0, Passed: 1550` (nền 1506 → **+44**) |
| `dotnet test suite/Shopee.Core.Tests` | `Passed! - Failed: 0, Passed: 83` (nền 83, không đổi) |
| `dotnet test server/Shopee.Hub.Web.Tests` | `Passed! - Failed: 0, Passed: 108` (nền 81 → **+27**) |

**Hub chạy thật** (`HUB_DATA_DIR` = thư mục tạm trong scratchpad, đã xoá sau khi xong;
`server/Shopee.Hub.Web/hub-data/` của repo vẫn nguyên mốc 2026-07-28 — KHÔNG bị đụng):

- **H2.3** — bơm 4 đơn (A1/A2/A3 shop-a, B1 shop "shop/b danger"), gắn mã trả cho A2 + A3:

  | URL | tổng | mã đơn hiện ra |
  |---|---|---|
  | `/orders` | 3 | A1 A2 B1 |
  | `/orders?comatra=1` | 1 | A2 |
  | `/orders?status=all` | 4 | A1 A2 A3 B1 |
  | `/orders?status=all&comatra=1` | 2 | A2 A3 |

  F5/deep-link giữ nguyên trạng thái (giá trị đọc từ URL lúc prerender); link ZIP sinh ra kèm đúng bộ lọc, vd
  `href="/orders/zip?status=Ch%E1%BB%9D%20l%E1%BA%A5y%20h%C3%A0ng&comatra=1"`.

- **H2.4** — `GET /orders/zip` → `HTTP=200 type=application/zip`,
  `Content-Disposition: attachment; filename=phieu-20260806-0805.zip` (mốc **giờ VN**, lúc đó 01:05 UTC):
  ```
  shop-a/A2.pdf · shop_b_danger/B1.pdf · shop-a/A1.pdf
  ```
  Nội dung từng entry đúng nguyên văn file đã đẩy (`%PDF-1.4 / PHIEU A1 / %%EOF`…). Tên shop `shop/b danger`
  được khử thành `shop_b_danger` (không có `/` lọt vào đường dẫn entry).
  `?comatra=1` → đúng 1 entry `shop-a/A2.pdf`. Bộ lọc không khớp phiếu nào → **404** kèm câu tiếng Việt.
  **Biên trần đo chính xác:** 500 phiếu → `HTTP=200`, gói có đủ **500 files**; 501 phiếu → **`HTTP=400`** +
  "Bộ lọc hiện tại khớp hơn 500 phiếu — hãy thu hẹp bộ lọc…".

- **H2.1** (kiểm thêm, ngoài tiêu chí bắt buộc) — bật tổng kết, đặt giờ = giờ VN hiện tại, webhook trỏ **loopback
  về chính hub** (URL dạng Telegram nên `NhanDienKenh` nhận, KHÔNG có request nào ra internet):
  ```
  moc da_gui: ('2026-08-06',)
  ('2026-08-06T01:07:04Z', 'warn', 'notify "tổng kết ngày": gửi 0/1 webhook OK — tổng kết ngày 2026-08-06: 1004 đơn chuẩn bị hàng')
  ```
  1004 = đúng tổng đơn "Chờ lấy hàng" đã bơm (3 + 501 + 500). Chờ thêm ~130s (2 nhịp) → **vẫn đúng 1 dòng**.
  **Tắt hub → bật lại → chờ thêm 1 nhịp → vẫn đúng 1 dòng** (mốc nằm trong DB, đúng yêu cầu "không gửi trùng khi
  hub restart quanh giờ gửi"). Trang `/settings` render đủ section mới và ô giờ hiện `value="8" min="0" max="23"`.

### Thử phá test rồi khôi phục (mỗi ca đều xác nhận ĐỎ, sau đó trả code về nguyên trạng)

| # | Phá gì | Kết quả chạy | Đã khôi phục |
|---|---|---|---|
| 1 | Bỏ mệnh đề "chưa đếm Đã bán" trong `ChanDoanDonKetThuc` | ĐỎ 5 test, gồm cả test CŨ `AccountSessionCleanupTests.DaGiao_CoSku_ChuaDem_Giu` (chứng minh luật dọn thật sự đi qua hàm chẩn đoán) | ✔ |
| 2 | Viết lại `NenXoaDonKetThuc` bằng điều kiện riêng, thiếu 1 vế (mô phỏng "trôi lệch") | ĐỎ `ChanDoanRong_TuongDuong_NenXoa_TrenMoiToHop` | ✔ |
| 3 | `DatLaiCoDayLai` reset thêm `gsheet_tab` + `sold_counted_at` | ĐỎ `DayLai_KHONG_DungTab_DemDaBan_LinkPhieu_VaCoPhieuHub` | ✔ |
| 4 | `DenLuotGui` bỏ so mốc "đã gửi ngày d" | ĐỎ `DaGuiHomNay_ThiThoi_DuGoiLaiBaoNhieuLan` | ✔ |
| 5 | `BuildCreatedRange` bỏ `AddDays(1)` ở biên "Đến" | ĐỎ 5 test của `OrdersDateFilterTests` | ✔ |
| 6 | `WhereClause` bỏ `TRIM(...) <> ''` ở lọc mã trả | ĐỎ `MaTraRong_KhongTinhLaCoMaTra` | ✔ |
| 7 | Tooltip luôn in dòng "đơn lên Hub" kể cả khi = 0 | ĐỎ `HetTon_ChiConDongTongVaDongNhac`; nhân đó đã **siết thêm** `ChiLietKeLoaiKhac0` (đổi ca sang `Orders=0`) để dòng đầu tiên cũng được canh | ✔ |

### Vướng mắc / khác plan (2 điểm — cần kiến trúc sư duyệt)

1. **H2.1 phải thêm 1 cột DB `orders.return_code_at`** (`HubDatabase.cs` + `HubDatabase.Orders.cs`). Plan yêu cầu
   "số mã trả hàng mới hôm nay" nhưng hub KHÔNG có mốc thời gian nào cho `return_request_code` — đếm bằng
   `first_seen_at` sẽ ra ~0 mãi (yêu cầu trả hàng đến sau khi đơn phát sinh nhiều ngày), còn đếm bằng `synced_at`
   thì đơn cũ đẩy lại nhảy vào hôm nay. Đã thêm cột theo đúng pattern `AddColumnIfMissing` sẵn có, ghi mốc ở
   **ĐÚNG điều kiện mà `UpsertOrders` dùng để bắn notify "đơn trả"** (nhánh UPDATE + mã khác rỗng + khác mã cũ,
   so sau TRIM). **KHÔNG backfill** dòng cũ (NULL = không biết ghi lúc nào → không đếm vào ngày nào), cố ý để tin
   tổng kết đầu tiên không dồn cả kho mã cũ.
2. **H2.5 chỉ có 4 nghĩa vụ, KHÔNG có "mã trả chưa đẩy"** như plan liệt kê (plan ghi 5). Lý do: `NenXoaDonKetThuc`
   chưa bao giờ có điều kiện mã trả — mã trả sống trong bảng `return_codes` ĐỘC LẬP với vòng đời đơn (đơn đã dọn
   vẫn đẩy được mã). Thêm mục thứ 5 sẽ phá đúng cái plan đòi ("2 luật không trôi lệch"). Phần mã trả CÓ ảnh hưởng
   tới đơn còn sống thì đã nằm trong nghĩa vụ `GhiSheet` (mã mới làm `ConNghiaVuGhiSheet` trả true để đẩy lại điền ô)
   — đã ghi rõ trong xmldoc của `ChanDoanDonKetThuc`.

### Bug thật bắt được lúc chạy hub (đã sửa trong đợt này)

`ZipArchive.Dispose()` ghi "central directory" bằng lệnh **ghi đồng bộ**, mà Kestrel mặc định cấm ghi đồng bộ lên
response stream → lượt tải đầu tiên trả **HTTP 500** ở đúng byte cuối (`Synchronous operations are disallowed`).
Build xanh + test xanh KHÔNG bắt được ca này. Đã sửa bằng cách bật `IHttpBodyControlFeature.AllowSynchronousIO`
cho **riêng request đó** (feature theo request, không nới cấu hình toàn server), không phải bằng cách gom cả gói
vào RAM (plan cấm).

### Còn lại / rủi ro dư

- **XAML mới chỉ được kiểm bằng BUILD, chưa chạy app WPF thật**: hàng lọc ngày trong `OrdersView.xaml`, cửa sổ
  `ChanDoanDonDialog.xaml`, và badge `Button` ở `MainWindow.xaml`. Đã đối chiếu từng khoá tài nguyên
  (`DangerBrush`/`ghostIcon`/`h1`/`proxy`/`btnLabel`/`SuccessBrush`/`TextMuted` đều tra được qua
  `ModuleResources.xaml` → `Controls.xaml` → `Colors.xaml`) và dùng lại pattern có sẵn trong repo cho binding
  command của dòng lưới (`DataContext.XCommand` + `RelativeSource AncestorType={x:Type Window}` — y hệt
  `ScrapeStatsWindow.xaml` đang chạy production). Dự án chưa có harness dựng view WPF trong test (thiếu
  `Application` của suite nên không dựng được view độc lập) → **đề nghị bấm tay 1 lượt** trước khi phát hành:
  mở màn Đơn hàng (2 ô ngày + nút ✕), bấm badge "⏳ Chờ đẩy" (mở cửa sổ chẩn đoán, thử nút "Đẩy lại").
- **Deploy hub PHẢI đi trước release client** như mọi đợt có đổi schema: cột `return_code_at` do hub tự thêm lúc
  khởi động (`MigrateSchema`), client không cần biết.
- Nút "⬇ ZIP phiếu" tải qua thẻ `<a>` (không qua circuit Blazor) nên gói lớn không giữ circuit; nhưng trần 500
  phiếu ~150MB là tải một mạch, chưa có thanh tiến trình — chấp nhận trong phạm vi plan.

---

## Nghiệm thu (Fable tổng hợp sau phản biện, 2026-08-06)

`nghiem-thu` chấm **ĐẠT CÓ ĐIỀU KIỆN** — 6/6 hạng mục có thật, số liệu executor khai đều đúng khi chạy lại.
Nó **dựng lại được harness XAML** (tạo `Shopee.Suite.App` + InitializeComponent để có Application.Resources
thật, KHÔNG chạy app) và chứng minh 3 file XAML mới parse được, mọi StaticResource phân giải — gỡ đúng rủi ro
executor tự khai. Tự đo lại biên ZIP: 500 phiếu → 200 (đủ 500 entry), 501 → 400, lọc rỗng → 404, chưa đăng
nhập → 302 (auth đúng như trang). Tên shop `shop/a danger` khử thành `shop_a_danger` — không có `/` lọt vào
đường dẫn.

**Phiên chính sửa sau phản biện:**
1. **[TRUNG BÌNH — đã sửa]** Dòng "mã trả hàng mới hôm nay" của tin tổng kết đếm hụt gần hết: mã trả của đơn
   ĐÃ BỊ APP DỌN đi đường `app-alert kind=don_tra` mà endpoint đó chỉ ghi log, không chạm bảng `orders` —
   trong khi HUB VẪN GIỮ dòng đơn. Thêm `HubDatabase.ApplyReturnCodesFromAppAlert` (dùng `TachCapDonTra` sẵn
   có), gọi trong `FireNotifyDonTraTuAppAlert` trước khi bắn tin. Chỉ ghi khi mã THẬT SỰ mới/khác (so sau
   TRIM) ⇒ gửi lại cùng lô không cộng số. Lợi thêm: cột "Đơn trả hàng" ở /orders và bộ lọc `comatra` giờ phủ
   cả đơn đã dọn. Kèm **5 test mới** (`ReturnCodeFromAppAlertTests`), đã thử phá (bỏ điều kiện mã-mới → đỏ 1)
   rồi khôi phục. Hub tests: 108 → **113**.
2. **[THẤP — đã sửa]** `ChanDoanDonViewModel.Reload` quét đồng bộ trên UI thread (duyệt mọi tài khoản × mọi
   đơn + mở file phiếu) → cửa sổ đứng vài giây với kho đơn lớn. Tách phần quét thuần `QuetDonKet()` + thêm
   `ReloadAsync` chạy `Task.Run`; ctor vẫn quét đồng bộ (mở ra là có dữ liệu ngay, không nháy bảng trống),
   nút Làm mới + sau khi Đẩy lại đi đường nền. Test tương ứng đổi sang `await ExecuteAsync`.

**Ghi nhận, KHÔNG sửa đợt này (việc cho đợt sau):**
- Ca đua "Đẩy lại" ↔ lượt đẩy SHEET đang bay: phía hub an toàn nhờ `hub_push_gen`, nhưng `MarkGsheetSynced`
  không có guard thế hệ nên reset có thể bị lượt đang bay ghi đè (cửa sổ vài giây trong mỗi 2 phút), mà VM
  vẫn báo "đã xếp vào hàng chờ". Sửa đúng = thêm cột thế hệ cho sheet.
- "Đẩy lại" trên đơn HỦY chưa từng có vận đơn là no-op im lặng (rơi vào lối tắt "không thuộc sổ ghi sheet").
- Lối vào màn chẩn đoán chỉ có badge ⏳, mà badge ẩn khi `Tong == 0` — đúng lớp đơn kẹt-nhưng-không-được-đếm
  thì không mở được màn. Lỗ này CÓ TRƯỚC H2 (nguồn đếm `CountForGsheetPush` bỏ sót). Nên thêm lối vào menu.
- `TRIM()` của SQLite chỉ cắt dấu cách, `string.Trim()` của C# cắt mọi whitespace (lệch lý thuyết).

### Checklist trước/khi phát hành
1. **Deploy hub TRƯỚC release client** (cột `return_code_at` do hub tự thêm lúc khởi động, không backfill).
2. Thử **một lượt tải ZIP qua `api.schedra.net` thật**: 500 phiếu ~150MB đi một mạch qua Cloudflare Tunnel,
   không có thanh tiến trình/nút hủy; tunnel đứt giữa chừng = file zip hỏng không báo lỗi (response đã bắt đầu).
3. Bấm tay 1 lượt trước khi phát hành client: hàng lọc ngày (2 DatePicker + ✕), bấm badge ⏳ mở cửa sổ chẩn
   đoán, và **thử đúng nút "Đẩy lại" trên một đơn thật** rồi xem lượt sheet kế có cập nhật ĐÚNG DÒNG CŨ không
   (Apps Script nằm ngoài repo).
4. Bật tin tổng kết lần đầu SAU giờ hẹn trong ngày sẽ bắn ngay một tin cho hôm đó — đúng thiết kế.
