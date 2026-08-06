# Plan: Đợt H1 — Tính năng quan sát từ xa qua Hub

- **Ngày:** 2026-08-06
- **Trạng thái:** hoàn thành (code + nghiệm thu; CHƯA deploy/release — xem checklist cuối file)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Mục tiêu gốc của hệ Hub là vận hành đa máy không phải ngồi cạnh máy. Hiện muốn biết "hôm nay ổn không" phải mở lần lượt /orders, /machines, /config/errored, /logs-view; máy chết im lặng không ai báo; dữ liệu banner địa chỉ nằm trên hub nhưng không xem được từ web; backlog "chờ đẩy" của từng client hub không thấy. 4 tính năng đợt này lấp đúng các lỗ đó, tận dụng hạ tầng sẵn: FleetStateService.IsOnline, OrderNotifyService (webhook 3 sự kiện, Settings.razor ~:30–53), bảng `pickup_alerts` (HubDatabase.PickupAlerts + API client sẵn), heartbeat đã mang MaxBrave/AppVersion.

**Trình tự triển khai bắt buộc: hub deploy TRƯỚC, client release SAU** (H1.4 cần cả hai phía; hub phải chịu được client cũ chưa gửi field mới).

## 2. Phạm vi

- **Làm:** 4 mục phần 3.
- **Không làm:** digest cuối ngày + các tính năng đơn hàng (đợt H2); tự cứu acc/OTP (phiên riêng có user); KHÔNG deploy/release trong đợt (phiên chính lo).

## 3. Các bước thực hiện

### H1.1 Dashboard đầu trang chủ
Trang chủ `/` (Fleet.razor, hiện 4 KPI việc BigSeller ~:16–33): thêm **hàng thẻ tổng quan** phía trên, mỗi thẻ là số + link tới trang chi tiết:
- Đơn "chuẩn bị hàng" hôm nay (bảng `orders`, mốc `first_seen_at` theo ngày VN — logic "Đơn chờ hôm nay" đã có từ v1.7.6, tái dùng đúng query đó) → link /orders.
- Máy offline / tổng máy (FleetStateService) → /machines. Chỉ tô đỏ khi có máy offline MÀ đang giữ việc.
- Acc lỗi (bảng errored) → /config/errored.
- Việc gián đoạn/Interrupted (bảng assignments) → /dispatch.
- Cảnh báo địa chỉ đang active (H1.2) → cuộn xuống section.
Thẻ nào = 0 thì hiển thị mờ (không màu cảnh báo). Cấu trúc css theo token hiện có; mobile xuống 2 cột (khuôn m-* sẵn).

### H1.2 Banner lỗi địa chỉ trên hub + đóng từ xa
Section (trong Fleet.razor hoặc trang con — chọn theo bố cục, ưu tiên section ngay dưới hàng KPI) liệt kê `pickup_alerts` đang active: shop, nội dung, tuổi, máy phát hiện. Nút "Đã xử lý" per-dòng → đi ĐÚNG đường dismiss mà client X dùng (rev/tombstone — bấm X luôn thắng, xem memory `pickup-alert-sync-no-clock-compare`; đọc `HubDatabase.PickupAlerts.cs` + endpoint client hiện có, dùng lại logic — TUYỆT ĐỐI không tự chế so-mốc-giờ). Dismiss từ hub phải lan xuống client như dismiss từ client (qua chính hợp đồng rev hiện có).

### H1.3 Webhook "máy rơi offline khi đang giữ việc"
- Nguồn trạng thái: FleetStateService (đã có IsOnline + badge x/y máy). Thêm kiểm tra định kỳ (timer trong service sẵn có hoặc BackgroundService mới): máy có assignment đang chạy/lease sống mà mất nhịp > N phút (N cấu hình, mặc định 10) → gửi webhook qua `OrderNotifyService` (thêm loại sự kiện mới, cùng khuôn 3 sự kiện hiện có).
- Chống spam: mỗi episode offline báo đúng 1 lần; khi máy online lại thì gửi tin "đã trở lại" (1 lần); trạng thái episode giữ trong bộ nhớ service (mất khi restart hub cũng chấp nhận được — ghi rõ).
- Bật/tắt + ngưỡng N trong /settings, cùng section webhook hiện có, lưu cùng chỗ config webhook.

### H1.4 Backlog "chờ đẩy" của client lên heartbeat, hiện ở /machines
- **Client (orders module trong suite):** `OutboxPending` đã tách 5 loại tồn (AppServices ~:21–31). Đưa TỔNG số tồn (1 số int) vào heartbeat: khảo sát đường heartbeat hiện tại (HubClient/HttpCoordinationHub phía suite Core — heartbeat đã mang MaxBrave/AppVersion qua trường mở rộng) + cách orders module cung cấp số cho shell (OrdersModuleHost đã là cầu giữa shell và module — thêm provider/callback theo khuôn MaxBrave). Client không có module orders (chế độ khác) thì không gửi field → hub hiển thị "—".
- **Hub:** nhận field mới (client cũ không gửi → null, không lỗi), lưu vào bảng machines (cột mới, migration theo khuôn cột store_* đã có), hiển thị cột "⏳ Tồn" ở /machines; > 0 tô vàng, > 50 tô đỏ (ngưỡng hằng số, ghi chú).
- Hub phải deploy TRƯỚC client — code hub chấp nhận thiếu field; code client gửi thêm field không phá hub cũ (nhưng thứ tự deploy vẫn như trên cho sạch).

## 4. Tiêu chí nghiệm thu

- [ ] Build 2 solution 0 error 0 warning; 3 bộ test xanh.
- [ ] Test hub mới: (a) query "đơn chờ hôm nay" của dashboard = đúng số theo mốc ngày VN (test theo khuôn test first_seen_at sẵn có); (b) webhook offline: máy giữ việc + mất nhịp giả lập → đúng 1 tin, online lại → 1 tin "trở lại", không lặp; (c) heartbeat mang/thiếu field backlog đều xử lý đúng. Test viết xong PHẢI thử phá (đổi luật → đỏ) rồi khôi phục — ghi vào báo cáo.
- [ ] H1.2: dismiss từ hub đi cùng code-path với dismiss từ client (chỉ ra hàm chung trong báo cáo); KHÔNG có phép so thời gian chéo máy nào mới.
- [ ] Chạy hub local, mở `/`: hàng KPI render, số khớp dữ liệu DB dev; section địa chỉ hiện khi có alert giả.
- [ ] Client build xanh; chế độ không có orders module không gửi field (kiểm bằng test hoặc log).

## 5. Rủi ro & lưu ý

- **Banner địa chỉ là khu từng nhiều sẹo** (memory: cấm so mốc giờ chéo máy, tombstone tự lành, RunOnUi=BeginInvoke làm repush chết câm) — đọc memory + code hiện có TRƯỚC khi viết dòng nào; sai luật rev ở đây là bug lan cả fleet.
- Heartbeat là hợp đồng client↔hub — thêm field phải backward-compatible cả 2 chiều (client cũ→hub mới, client mới→hub cũ).
- Webhook: đừng gửi trong lock của FleetStateService; fire-and-forget có log lỗi.
- KHÔNG commit/deploy/release — phiên chính lo, theo trình tự hub trước client sau.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Ngày làm:** 2026-08-06 · cây sạch tại `2957563` · KHÔNG commit / deploy / release.

### Đã làm

**H1.1 — Hàng thẻ tổng quan trang chủ**
- `server/Shopee.Hub.Web/Components/Pages/Fleet.razor` + `.razor.cs`: thêm `<div class="kpis overview">` PHÍA TRÊN
  hàng KPI cũ, 5 thẻ: Đơn chờ hôm nay → `/orders` · Máy offline (x/tổng) → `/machines` · Acc lỗi →
  `/config/errored` · Việc gián đoạn → `/dispatch?kpi=interrupted` · Cảnh báo địa chỉ → mở/đóng section H1.2.
  Thẻ = 0 → class `dim` + icon `mute` (không màu cảnh báo).
- `server/Shopee.Hub.Web/Services/HomeOverview.cs` (MỚI): `DonChoHomNay(db, now)` = **đúng** truy vấn
  `ShopOrderSummaries("Chờ lấy hàng", KhoangNgayUtc(now))` mà tab Đơn hàng của /dispatch đang dùng (lọc
  `first_seen_at` theo ngày VN). Tách ra khỏi Razor để test được.
- Thẻ "Máy offline" chỉ tô ĐỎ khi có máy offline **mà đang giữ việc** — dùng CHUNG hàm đếm với H1.3
  (`MachineOfflineWatch.SoViecDangGiu`), không định nghĩa lại.
- Nhịp: 2 thẻ đọc thẳng snapshot (tươi 2s); 3 thẻ chạm DB nạp lại tối đa mỗi 10s (`ReloadOverview`).
- CSS `.kpis.overview` (thẻ gọn hơn hàng chính vì trang chủ khoá chiều cao viewport) + `.kpi-ic.mute` + `.kpi.dim`
  + `a.kpi`; mobile ép **2 cột** (override dải cuộn ngang mặc định của `.kpis`). `app.css?v=40 → v41`.

**H1.2 — Banner địa chỉ trên hub + đóng từ xa**
- `HubDatabase.PickupAlerts.cs`: thêm record `PickupAlertActiveRow` + `ActivePickupAlerts()` — hàm **ĐỌC thuần**
  (`WHERE dismissed_at IS NULL`), không sweep, không hạn tuổi, **không so mốc thời gian**.
- Section trong `Fleet.razor` ngay dưới hàng thẻ: tài khoản · shop · địa chỉ định đặt · "phát hiện" · máy báo · nút
  "✓ Đã xử lý" (qua `ConfirmDialog` dùng chung — không `window.confirm`).
- **Hàm chung của dismiss:** nút web gọi thẳng `HubDatabase.DismissPickupAlert(accountLogin, shopLogin, "hub-web")`
  — **cùng một hàm** mà endpoint client `POST /api/orders/pickup-alerts/dismiss` gọi
  (`ClientApiEndpoints.cs:318-329`). Không có đường ghi riêng cho web; rev tăng như mọi lượt ghi khác nên client
  merge theo `rev` (`PickupAlertMerge.QuyetDinh`) sẽ nhận tombstone.
- **KHÔNG thêm phép so mốc giờ nào.** Cột "Phát hiện" chỉ hiển thị và mốc đó do **chính hub** ghi
  (`UpsertPickupAlert` dùng `Iso(UtcNow)` của hub), nên `Ago()` là đồng hồ hub so đồng hồ hub.
- View-state đóng/mở section vào URL (`?al=0`) theo nguyên tắc URL-state của hub.

**H1.3 — Webhook "máy rơi offline khi đang giữ việc"**
- `Services/MachineOfflineWatch.cs` (MỚI, lõi THUẦN): `SoViecDangGiu()` + `Quet()` — mỗi episode offline ra đúng
  1 tin, máy nối lại ra đúng 1 tin, máy bị xoá khỏi fleet thì dọn tập mà KHÔNG bắn "trở lại".
- `Services/MachineOfflineWatchService.cs` (MỚI, BackgroundService, nhịp 30s): đọc `FleetStateService.Snapshot`
  (KHÔNG gọi Refresh, KHÔNG gửi trong lock nào), xếp hàng qua `WebhookQueueService`. Trạng thái episode nằm trong
  bộ nhớ → mất khi restart hub (đã ghi rõ trong xmldoc + hint UI).
- `OrderNotifyService.TaoTinNhanMayMatNhip` / `TaoTinNhanMayTroLai` (thêm vào file đã LINK sang hub, cùng khuôn 3
  tin sẵn có).
- `/settings`: ô URL + checkbox bật/tắt + ô ngưỡng phút (mặc định 10, kẹp 1–120), lưu cùng chỗ webhook.
  `SettingKeys.NotifyWebhookMayOffline / NotifyMayOfflineBat / NotifyMayOfflinePhut`. Mặc định **TẮT**.
- Kênh mới **KHÔNG** lùi về ô legacy `notify.webhooks` (lưới an toàn đó chỉ dành cho 3 kênh tách ra từ nó); thiếu
  URL mà có sự kiện → ghi log warn như khuôn `LogChuaCauHinh`.

**H1.4 — Backlog "chờ đẩy" lên heartbeat + cột /machines**
- DTO: `MachineHeartbeatRequest` thêm `int? OutboxPending = null` (tham số CUỐI, có mặc định) và
  `MachinePresence.OutboxPending` (`int?`). Tương thích 2 chiều: client cũ không gửi → hub đọc null; client mới gửi
  thêm field → hub cũ bỏ qua field lạ.
- Hub: cột `machines.outbox_pending INTEGER` (nullable, KHÔNG default) + `AddColumnIfMissing`; upsert **ghi thẳng**
  (không COALESCE) để máy hạ bản/đổi chế độ không để lại số chết; `/machines` thêm cột "⏳ Tồn" (>0 vàng, >50 đỏ,
  hằng `TonDo`), null → "—".
- Client: `OrdersSlotHeartbeat.OutboxPendingProvider` (hook static, khuôn `MaxBrave`) + `TonChoDay()` (null khi
  chưa rót / hook ném; kẹp số âm về 0); `OrdersModuleHost.WireOrdersMirror` rót
  `() => services.PendingOutbox.Tong`. Chế độ không có module Đơn hàng → hook null → nhịp không mang field.
- `Shopee.Core.csproj` thêm `InternalsVisibleTo Shopee.Core.Tests` (khuôn của `Shopee.Hub.Web.csproj`) để test
  được `TonChoDay` mà không phải nới `public`.

### Kết quả kiểm chứng (chạy thật)

| Lệnh | Kết quả |
|---|---|
| `dotnet build ShopeeSuite.sln --no-incremental` | Build succeeded — **0 Warning, 0 Error** |
| `dotnet build server/ShopeeHub.sln --no-incremental` | Build succeeded — **0 Warning, 0 Error** |
| `dotnet test orders/XuLyDonShopee.Tests` | **1506 passed**, 0 failed |
| `dotnet test suite/Shopee.Core.Tests` | **83 passed**, 0 failed (+7 mới) |
| `dotnet test server/Shopee.Hub.Web.Tests` | **80 passed**, 0 failed (+24 mới) |

**Test mới:** `HomeOverviewTests` (3) · `MachineOfflineWatchTests` (15 gồm 7 ca `[Theory]`) ·
`MachineOutboxHeartbeatTests` (6) · 3 ca thêm vào `PickupAlertsHubTests` · `OrdersOutboxHeartbeatTests` (7, phía client).

**THỬ PHÁ rồi khôi phục (4 lượt, đều xác nhận ĐỎ rồi xanh lại):**

| Luật bị phá | Kết quả khi phá | Sau khôi phục |
|---|---|---|
| `HomeOverview` chia ngày theo **UTC** thay vì giờ VN | 2 test đỏ (`DonChoHomNay_GopMoiShop…`, `…BienNgayTheoGioVietNam…`) | xanh |
| Bỏ chốt chống lặp `daBao.ContainsKey` trong `Quet` | 1 test đỏ (`MayGiuViecMatNhip_DungMotTin…`) | xanh |
| Bỏ nhánh "việc vừa rụng vì mất nhịp" khỏi `SoViecDangGiu` | 2 test đỏ (`ViecVuaRungViMatNhip…`, `SoViecDangGiu_GopBaNguon…`) | xanh |
| `outbox_pending=COALESCE($ob, outbox_pending)` (giữ số cũ) | 1 test đỏ (`DangBaoRoiThoiBao_VeNull…`) | xanh |
| `ActivePickupAlerts` bỏ `WHERE dismissed_at IS NULL` | 2 test đỏ (`ActivePickupAlerts_ChiDongDangMo…`, `…SauDismissRoiUpsertLai…`) | xanh |

*(Lượt phá #1 lần đầu chỉ làm đỏ 1/2 test → đã LÀM MẠNH test biên ngày (thêm ca 20:00Z = 03:00 VN hôm sau) rồi
phá lại: đỏ cả 2. Test cũ xanh vì lý do khác, đúng cái bẫy quy trình nhắc.)*

**Chạy hub local (HUB_DATA_DIR = thư mục tạm trong scratchpad, KHÔNG đụng `hub-data` thật; đã dọn sau khi xong):**
bơm qua ĐÚNG API client (3 heartbeat, 2 lô đơn, 2 banner, 1 acc lỗi) rồi đăng nhập + `GET /`:
- Hàng KPI render: `Đơn chờ hôm nay = 3` (2 shop, đã loại đơn "Đã giao" — khớp dữ liệu bơm) · `Máy offline = 0/3`
  (dim) · `Acc lỗi = 1` · `Việc gián đoạn = 0` (dim) · `Cảnh báo địa chỉ = 2 ▾`.
- Section banner hiện đủ 2 dòng (tài khoản · shop · "Thanh Hóa"/"Hà Nội" · "10s trước" · may-1/may-2 · nút ✓).
- Gọi dismiss (đúng hàm nút web gọi) → `{"rev":2}`; client kéo `GET /api/orders/pickup-alerts` thấy
  `{"dismissed":true,"rev":2}`; tải lại `/` → thẻ còn `1`, section còn 1 dòng.
- `GET /machines`: cột "⏳ Tồn" = `3` (pill warn) / `63` (pill fail, >50) / `—` (máy gửi nhịp kiểu client cũ).
- H1.3 chạy thật: hạ `last_seen` máy PC-02 xuống 40', cho nó giữ 1 việc queued, bật cảnh báo ngưỡng 1' →
  watcher bắn **đúng 1** tin (log `notify "máy offline": có máy PC-02 mất nhịp khi đang giữ 1 việc…`); qua thêm 45s
  vẫn **1**; cho máy nối lại → **đúng 1** tin "đã nối lại", sau 40s nữa vẫn 1/1. Thẻ KPI lúc đó chuyển
  `1/3` + tone **red** + title "CÓ máy offline mà vẫn đang ôm việc".

### Đã đổi hướng so với plan (khai báo)

1. **Thẻ "Cảnh báo địa chỉ" là NÚT đóng/mở section, không phải "cuộn xuống section".** Trang chủ khoá chiều cao
   (`.main:has(.fleetpage){height:100vh;overflow:hidden}`) nên anchor `#` không có chỗ để cuộn. Section vẫn nằm
   ngay dưới hàng thẻ và mặc định MỞ khi có cảnh báo, nên hành vi mặc định đúng ý plan.
2. **"Đang giữ việc" (H1.3) đếm thêm việc vừa bị sweep đánh rụng** vì máy hết nhịp (`last_error` =
   `StaleSweepError`, trong 2 giờ), ngoài queued/running + lease như plan viết. **Lý do bắt buộc:**
   `SweepStaleLocked` đánh 'failed' việc 'running' của máy mất nhịp sau **5 phút** và `ActiveLeases` cũng lọc bỏ
   lease cũ hơn 5 phút — với ngưỡng mặc định **10 phút** của plan, nếu chỉ đếm queued/running+lease thì ĐÚNG ca cần
   báo nhất (máy chết giữa lúc đang chạy việc) luôn ra 0 việc và **không bao giờ có tin nào**. Kèm theo:
   `HubDatabase.StaleSweepError` đổi `private` → `internal` (giá trị chuỗi giữ nguyên tuyệt đối).
3. **Thêm `Services/HomeOverview.cs`** (không có trong plan) để tiêu chí test (a) test đúng code-path của dashboard
   thay vì chép lại truy vấn trong file test.
4. **Thêm `InternalsVisibleTo` cho `Shopee.Core`** để có test phía client cho tiêu chí "chế độ không có orders
   module không gửi field".

### Chưa làm / hạn chế đã biết

- **Chưa bấm thử nút "✓ Đã xử lý" bằng chuột thật** (cần circuit Blazor interactive; kiểm chứng bằng curl chỉ dựng
  được bản prerender). Đường đi của nút đã được phủ ở tầng dưới: unit test `DismissTuWeb_ClientMergeTheoRev_TatBanner`
  + chạy thật endpoint dùng CHUNG hàm đó. Nên bấm tay 1 lần khi deploy.
- **Chưa gửi webhook thật** ra Slack/Discord (không có URL sink trong môi trường test) — đã kiểm chứng tới bước
  "xếp hàng gửi": nhánh chưa-cấu-hình-URL ghi log đúng, `WebhookQueueService` là đường gửi đã dùng cho 3 kênh cũ.
- Trạng thái episode của H1.3 **mất khi restart hub** → lượt quét đầu sau restart có thể báo lại 1 lần cho máy vẫn
  đang offline-giữ-việc (plan chấp nhận; đã ghi trong xmldoc + hint ở /settings).
- `DispatchOrdersTab.WaitingStatus` vẫn là bản sao thứ hai của chuỗi "Chờ lấy hàng" (giờ có thêm
  `HomeOverview.TrangThaiCho`) — cố ý KHÔNG sửa file đó để không mở rộng phạm vi; nếu muốn gom về một chỗ thì làm ở
  đợt dọn sau.

### Đề xuất cho kiến trúc sư

1. **Cân nhắc hạ ngưỡng mặc định H1.3 xuống 5 phút.** Với 10 phút, tin luôn tới SAU khi hub đã tự đánh rụng việc
   (5') — vẫn báo đúng nhờ điểm (2) ở trên, nhưng người trực nhận tin muộn hơn mức cần thiết.
2. **Thứ tự phát hành vẫn phải là hub TRƯỚC, client SAU** (H1.4). Client mới gửi field cho hub cũ thì vô hại (bị bỏ
   qua), nhưng cột "⏳ Tồn" sẽ trắng cho tới khi hub lên bản mới.
3. Nếu sau này muốn cảnh báo máy offline **bền qua restart hub**, phải chuyển tập episode xuống một bảng nhỏ trong
   `hub.db` — hiện cố ý để trong RAM theo plan.

---

## Nghiệm thu (Fable tổng hợp sau phản biện, 2026-08-06)

`nghiem-thu` chấm **ĐẠT CÓ ĐIỀU KIỆN** — 5/5 tiêu chí mục 4 đạt, tự kiểm chứng lại từng cái. Nó tự thử phá
**8 lượt (cả 8 bị test bắt)**, trong đó 3 lượt do chính nó nghĩ ra ngoài danh sách executor: biên `>` vs `>=`
của ngưỡng mất nhịp, trần tuổi 2h của việc rụng, và `DismissPickupAlert` không tăng `rev`. Chạy hub thật xác
nhận: KPI/section render đúng, dismiss web → rev 1→2 → client thấy tombstone → upsert lại rev 3 (banner sống
lại đúng thiết kế), webhook đúng 1 tin mất nhịp + 1 tin trở lại (quét thêm 5 lượt vẫn 1), heartbeat gửi field
lạ → 200 (client mới → hub cũ an toàn).

**Luật rev/tombstone (khu nhiều sẹo) — kết luận:** dismiss web đi đúng cùng hàm client, KHÔNG có mốc giờ
client nào tham gia quyết định (mọi mốc đều do hub ghi), upsert và dismiss cùng đi qua `lock (_gate)` nên
tuần tự — không có ca kẹt vĩnh viễn.

**Phiên chính sửa sau phản biện (4 điểm):**
1. Hub KẸP số âm `outbox_pending` về 0 (trước chỉ client kẹp; số âm lọt vào rơi nhánh màu "đã đẩy hết" →
   pill xanh "-9"). Kèm test mới `GuiSoAm_HubKepVeKhong` — đã thử phá (bỏ clamp → đỏ 1) rồi khôi phục.
   Hub tests: 80 → **81**.
2. Dismiss từ web nay `AppendLog` như đường client → `/logs-view` có vết admin đóng banner.
3. Dòng kết quả `_alertMsg` đưa RA NGOÀI khối section: đóng cảnh báo CUỐI thì section biến mất, dòng báo nằm
   trong đó sẽ mất theo.
4. Chưa cấu hình webhook thì **không quét** (trước vẫn quét → đánh dấu episode → admin điền URL giữa chừng là
   episode đó im vĩnh viễn); log thiếu-URL đúng 1 lần cho tới khi có URL.

**Quyết định ngược đề xuất executor:** GIỮ ngưỡng mặc định **10 phút**, không hạ 5'. Lý do nghiệm thu nêu và
tôi đồng ý: lệnh "⬆ Cập nhật tất cả" làm máy im nhịp 3–6 phút (tải Velopack + restart) trong khi việc/lease
vẫn treo trên hub — để 5' thì mỗi đợt update toàn fleet đẻ một loạt tin giả, mà nhiễu là thứ giết cảnh báo
nhanh nhất. Ô ngưỡng đã có ở /settings (1–120) cho ai cần nhạy hơn.

**Ghi nhận, không sửa:** `SoViecDangGiu` cộng chồng assignment+lease (nhất quán với cột "Việc đang giữ" của
/machines — đừng đọc số đó là số việc thật); state episode in-memory ⇒ **mỗi lần `systemctl restart shopee-hub`
sẽ bắn lại 1 tin cho mọi máy đang offline-giữ-việc**, và máy trở lại sau restart thì không có tin "đã nối lại"
(biết trước để khỏi hoảng lúc deploy); thẻ KPI offline dùng ngưỡng 180s còn webhook dùng phút nên thẻ có thể
đỏ trước tin ~9 phút; thẻ đếm theo SUẤT (máy Full = 2 dòng); chuỗi "Chờ lấy hàng" giờ có 3 bản (mầm lệch số
cho đợt dọn sau).

### Checklist sau deploy (bắt buộc)
1. **Bấm tay nút "✓ Đã xử lý"** trên `/` một lần — rủi ro còn lại lớn nhất của đợt (curl chỉ dựng được
   prerender, không có circuit Blazor; cả 3 tầng đã kiểm gián tiếp nhưng chưa ai bấm chuột thật).
2. Bật kênh webhook "máy offline" với URL Slack thật + ép 1 tin để xác nhận format.
3. **scp cả `wwwroot/app.css`** (bump v41), không chỉ dll.
4. Thứ tự: **hub deploy TRƯỚC, client release SAU** (3/4 tính năng thuần hub, chạy được ngay; client chỉ thêm
   cột ⏳ Tồn).
