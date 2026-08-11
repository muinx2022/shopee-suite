# Đợt T1–T12 — nhóm phát hiện TRUNG BÌNH của review check đơn hàng (11/08/2026, tối)

**Bối cảnh:** review toàn luồng 11/08 ra 6 NẶNG (đã sửa, v1.9.1) + 13 TRUNG BÌNH (T13 đã sửa cùng đợt NẶNG).
User lệnh: "làm tiếp nhóm T1-T12 đi". Danh sách gốc lấy lại từ transcript review (đã xác minh LẠI từng mục
trên code hiện tại — mọi mục còn nguyên, chỉ xê dịch số dòng).

**Phạm vi:** client (T1 T2 T3 T5 T10 T11 T12) + Hub (T4 T6 T7 T8 T9). Chia HAI commit: client trước, Hub sau.
Hub deploy thẳng theo quy trình repo (memory: VM sudo không mật khẩu). Client KHÔNG bump/release trong đợt này
trừ khi user bảo — máy này vẫn cập nhật bin để nghiệm thu sống.

## CLIENT

### T1 — `doSetPickupAddress` hết trả `ok:true` mù
- Hiện trạng: [flow-address.js:89](extensions/shopee-orders/flow-address.js:89) log `done/total` xong
  [dòng 105] vẫn `ok:true` vô điều kiện — overlay nuốt cú tick thì vẫn bấm Lưu bừa, C# tin đã đặt địa chỉ,
  hàng đi từ địa chỉ cũ mà không banner.
- Sửa (trong `thuDatDiaChi`): (a) sau vòng tick, `cnt.done < cnt.total` ⇒ `ok:false` (an toàn vì `done` đã
  đếm disabled-là-done — xem pageCheckboxCount); (b) sau Lưu + Đồng ý, POLL ~8s `pageFindAddressEdit(province)`
  đòi `hasTag` — chưa mang tag "Địa chỉ lấy hàng" ⇒ `ok:false` kèm lý do. Lượt thử-lại-1-lần sẵn có của
  `doSetPickupAddress` hưởng nguyên.
- Tiêu chí: node --check sạch; đường thành công không đổi hành vi; hai đường hỏng trả ok:false có lý do.

### T2 — hai chốt "sai tab" thôi vứt mã trả hàng THẬT đã cào được
- Hiện trạng: [ShopFlowRunner.cs:780](orders/XuLyDonShopee.Core/Services/ShopFlowRunner.cs:780) (BoLuotSaiTab)
  + [:796](orders/XuLyDonShopee.Core/Services/ShopFlowRunner.cs:796) (NghiSaiTabTheoDuLieu) `return` trước khối
  lưu → dòng `laTraHang=true` ghép sạch cũng bị vứt. Chúng chỉ cần bảo vệ MỐC.
- Sửa: tách helper lưu-mã (GhepCap → LocTheoCuaSo → `_saveReturnCodes`) gọi ở cả hai chốt trước khi return,
  log rõ "(lượt bỏ vì sai tab — vẫn lưu N mã thật đã cào được)". KHÔNG đụng mốc, KHÔNG đụng cờ còn-sót.
- Tiêu chí: test rig — BoLuotSaiTab + NghiSaiTab đều: mã thật ĐƯỢC lưu, mốc KHÔNG ghi. Thử phá.

### T3 — đơn không đọc được mã: KHÔNG arrange mù
- Hiện trạng: [flow-orders.js:203](extensions/shopee-orders/flow-orders.js:203) `orderCode||""` rồi vẫn bấm
  "Chuẩn bị hàng"; C# [ShopFlowRunner.cs:495-504] dùng mã rỗng → `_onOrderPrepared("")` + phiếu ghi đè
  `phieu.pdf` + log "lưu phiếu OK".
- Sửa hai đầu: (a) extension — mã rỗng ⇒ progress ⚠ "card có nút Chuẩn bị hàng nhưng KHÔNG đọc được mã đơn
  (.order-sn đổi markup?) — DỪNG bước chuẩn bị hàng shop này" + `noOrder`, KHÔNG bấm gì; (b) C# phòng lớp hai —
  `prep.OrderCode` rỗng ⇒ log ⚠, KHÔNG `_onOrderPrepared`, KHÔNG `TrySaveSlip`, `break` vòng.
- Tiêu chí: test rig — orderPrepared mã rỗng ⇒ dừng vòng, 0 phiếu ghi, 0 lượt đếm. Thử phá.

### T5 — giá trị ĐỔI (tracking A→B, final_amount điều chỉnh) phải reset cờ hub/sheet
- Hiện trạng: [OrdersRepository.Sync.cs:120-129] chỉ reset khi NULL→có; A→B ghi đè local mà hub/sheet giữ giá
  trị cũ vĩnh viễn (đơn dọn xong là hết đường sửa).
- Sửa UPDATE: điều kiện tracking thành `($tracking IS NOT NULL AND (tracking_number IS NULL OR
  tracking_number <> $tracking))`; final_amount tương tự; thêm `gsheet_da_co_van_don = 0` khi tracking
  đổi/xuất-hiện (đường re-push "vận đơn mới" của sheet ăn theo cờ này).
- Tiêu chí: test repo — A→B ⇒ hub_synced_at NULL + hub_push_gen+1 + gsheet_da_co_van_don=0; giá trị GIỮ NGUYÊN
  ⇒ không reset gì (chi phí đẩy lại không tăng). Thử phá.

### T10 — cảnh báo "mã QUÁ HẠN 14 ngày" chỉ log khi SỐ ĐỔI
- Hiện trạng: [HubOutbox.cs:632-637] bắn mỗi lượt worker 2' ⇒ ~63k dòng/sự việc.
- Sửa: chốt theo mẫu `_tonDaBao` của HubOutboxWorker — dict tĩnh (accountId → số đã báo), log khi khác lần
  trước và >0; về 0 thì xoá chốt im lặng. Tách quyết định thuần để test.
- Tiêu chí: test thuần — lần đầu log, lặp không log, đổi số log lại, về 0 rồi tăng lại log. Thử phá.

### T11 — chẩn đoán pager trả hàng bắn được ở đúng ca selector hỏng
- Hiện trạng: chẩn đoán nằm trong `latTrang` ([flow-returns.js:86-93]) — selector hỏng thì `coTrangSau=false`
  ⇒ C# không gửi nhịp 2 ⇒ khối đó không bao giờ chạy.
- Sửa: ở NHỊP 1 (`doReadReturnRequests`), khi `list.length > 0 && soOTong > list.length && !coTrangSau` ⇒
  chạy `pageChanDoanPagerTraHang` + progress ⚠ "ô tổng nói X mà chỉ đọc được Y dòng và KHÔNG thấy nút trang
  sau — selector phân trang có thể đổi; khối phân trang: …". Ca thường (một trang đủ) không tốn gì.
- Tiêu chí: node --check + test cú pháp; soi tay điều kiện không nổ ở shop 1 trang.

### T12 — dedupe dòng lặp giữa hai nhịp trước khi báo "1 đơn NHIỀU yêu cầu"
- Hiện trạng: [TraHangParser.cs:443-453] dòng trùng mã đơn + CÙNG mã yêu cầu (trang dịch giữa hai nhịp) vẫn
  báo "giữ X, BỎ X" — nhiễu đúng con số user dùng để quyết đổi layout sheet.
- Sửa: chỉ vào `TrungMaDon` khi mã yêu cầu KHÁC mã đã giữ; giống hệt ⇒ bỏ im lặng (dedupe thuần).
- Tiêu chí: test — (sn,X)+(sn,X) ⇒ TrungMaDon rỗng, Cap 1 phần tử; (sn,X)+(sn,Y) ⇒ vẫn báo. Thử phá.

## HUB

### T4 — `UpsertOrders` chặn chuỗi RỖNG ghi đè
- Hiện trạng: [HubDatabase.Orders.cs:174-182] `COALESCE($x, cột)` không chặn `""` — push mang "" xoá
  tracking/mã trả/final_amount_text/prepared_* trên hub, không mốc, không notify.
- Sửa: 5 cột TEXT (`$fat $tn $rrc $pa $pd`) thành `COALESCE(NULLIF(TRIM($x),''), cột)`.
- Tiêu chí: test hub — upsert có giá trị → upsert lại với ""/khoảng-trắng ⇒ GIỮ giá trị cũ; giá trị mới thật
  vẫn đè được. Thử phá.

### T6 — bộ đếm "đơn chờ" cùng MỘT định nghĩa với cả hệ (`LaChuanBiHang`)
- Hiện trạng: [HubDatabase.Orders.cs:459] `status=$w` exact trong khi [dòng 348] NeedsAction dùng
  `LaChuanBiHang` (contains) — hai màn hai số khi status mang biến thể.
- Sửa: `ShopOrderSummaries` bỏ tham số `waitingStatus`, SELECT thô (shop_id, status, slip_at, synced_at) trong
  khoảng ngày rồi gộp bằng C# `ShopeeShippingNav.LaChuanBiHang` — MỘT định nghĩa, khỏi mô phỏng nửa vời bằng
  LIKE (ngày ~vài trăm dòng, đọc WAL, rẻ). Cập nhật 3 caller (HomeOverview, DailyDigest†, DispatchOrdersTab).
  († DailyDigest sau T7 không còn gọi nữa.)
- Tiêu chí: test hub — status "Chờ lấy hàng (2)" / "Đang chuẩn bị hàng" được đếm; "Đã giao" không. Thử phá.

### T7 — tin tổng kết đếm đơn ĐÃ CHUẨN BỊ hôm nay (PrepareStatsByDay), hết nghịch lý "ngày càng trơn số càng nhỏ"
- Hiện trạng: [DailyDigest.cs:75] đếm ảnh chụp "còn đang chờ lúc 21:00" nhưng lời tin nói "phát sinh hôm nay".
- Sửa: `GomSoLieu` lấy TheoShop từ `db.PrepareStatsByDay(NgayVn(now))`; lời tin
  [OrderNotifyService.cs:470] thành "Đơn đã chuẩn bị hàng hôm nay: …". Trang chủ giữ nguyên thẻ của nó.
- Tiêu chí: test digest — số theo prepared_day, không theo snapshot status; wording khớp. Thử phá.

### T8 — không mất tin khi restart: xả hàng đợi webhook lúc tắt + mốc digest ghi SAU khi gửi
- Hiện trạng: [WebhookQueueService.cs:52-58] shutdown vứt im lặng phần đã xếp hàng;
  [DailyDigestService.cs:81] mốc "đã gửi" ghi TRƯỚC khi gửi — restart đúng lúc là mất hẳn tin của ngày.
- Sửa: (a) queue — sau cancel, DRAIN phần còn lại với ngân sách ~5s (TryRead + SendAsync token ngắn), log số
  gửi kịp / số đành bỏ; (b) digest — `WebhookNotification` thêm callback `OnDone` (mặc định null, caller khác
  không đổi); DailyDigestService: chốt in-flight theo ngày trong bộ nhớ (nhịp 60s không xếp trùng), mốc
  `NotifyTongKetDaGuiNgay` ghi trong OnDone (kể cả gửi fail — giữ đúng hành vi "webhook chết thì mất tin, có
  log", KHÔNG spam 1 tin/phút); TryQueue false ⇒ không đụng gì, nhịp sau thử lại. Restart giữa chừng ⇒ mốc
  chưa ghi ⇒ nhịp đầu sau restart gửi lại — đúng cái T8 đòi.
- Tiêu chí: test — mốc chỉ ghi sau OnDone; nhịp lặp khi in-flight không xếp thêm; TryQueue false không ghi
  mốc; drain: item xếp trước khi stop vẫn được gửi (hoặc được log đành-bỏ). Thử phá.

### T9 — `/api/orders/slip` tôn trọng kết quả `SetOrderSlipAt`
- Hiện trạng: [ClientApiEndpoints.cs:386] bỏ giá trị trả về (số dòng) — đơn bị xoá giữa lô vẫn `saved` ⇒
  client đóng cờ vĩnh viễn, phiếu mồ côi trên đĩa.
- Sửa: `SetOrderSlipAt(...) == 0` ⇒ xếp vào `missing` (client thử lại lượt sau) + xoá best-effort file vừa ghi.
- Tiêu chí: test hub — SetOrderSlipAt đơn không tồn tại trả 0 / có trả 1 (pin hợp đồng). Thử phá.

## Kiểm chứng chung + trình tự

1. Client: sửa → node --check + test orders (đường mặc định) + thử phá từng test mới → build sln OutDir
   scratch 0W (app đang chạy) → **commit client**.
2. Hub: sửa → `dotnet test server/Shopee.Hub.Web.Tests` + thử phá → build hub 0W → **commit hub**.
3. PHẢN BIỆN subagent trên toàn diff (trước 2 commit — sửa theo rồi mới commit).
4. Deploy Hub: publish linux-x64 → scp → install + restart `shopee-hub` → health OK.
5. Máy này: cửa nghỉ giữa vòng kế → build bin → chạy lại app → soi log (đặc biệt: bước địa chỉ vẫn ok:true ở
   shop lành — T1 không được gây banner oan).
6. KHÔNG bump/release client trong đợt — báo user quyết.

## Kết quả phản biện (subagent, 11/08 khuya) + sửa theo

Phản biện ra **2 NẶNG + 3 TRUNG BÌNH + 6 NHẸ** — đã sửa 2 NẶNG + 3 TB + 3 NHẸ đáng sửa:

1. **[NẶNG] T5 thiếu bump `gsheet_push_gen`** khi mở cờ `gsheet_da_co_van_don` — vi phạm bất biến "mở cờ nào
   thì +1 thế hệ ĐÍCH ĐÓ" repo tự ghi ở `DatLaiCoDayLai`; lô sheet fire-and-forget đang bay sẽ đóng lại đúng
   cờ vừa mở ⇒ vận đơn MỚI không bao giờ lên sheet. **Sửa:** thêm vế `gsheet_push_gen + 1` cùng điều kiện; test
   race mới `TrackingDoi_GiuaLucLoSheetDangBay_LoCuKhongDongLaiCoVuaMo` (chụp gen → đổi tracking → Mark với gen
   cũ → cờ phải còn NULL).
2. **[NẶNG] T1 nhánh xác-minh-tag không có lượt thử lại** (lượt thử-lại cũ chỉ chạy khi đóng được modal) và
   nằm trên đường đi chuẩn mọi shop (cuối vòng nào địa chỉ cũng bị trả về chỗ khác) ⇒ render chậm >8s là fail
   oan + banner. **Sửa:** poll 8s→20s; thất bại mang cờ `daBamLuu` để `doSetPickupAddress` LUÔN thử lại một
   lượt ở nhánh này (thuDatDiaChi tải lại trang, mọi bước idempotent); comment C# "mọi lối ok=false đều CHƯA
   bấm Lưu" viết lại cho đúng (nhánh mới CÓ THỂ đã Lưu — không in phiếu nên không hàng nào đi, vòng sau đặt
   lại từ đầu; vẫn không revert).
3. **[TB] T8 OnDone bị nuốt khi token hủy NGAY SAU cú POST thành công** ⇒ gửi trùng sau restart. **Sửa:** báo
   OnDone theo cờ `daXuXong` (GuiMoiUrlAsync chạy trọn không ném) thay vì `!ct.IsCancellationRequested`. Kèm:
   ghi mốc hỏng thì GIỮ chốt in-flight (không nhả — nhả là spam 1 tin/phút); `_ngayDangGui` volatile.
4. **[TB] T3 tín hiệu selector-hỏng tụt thành "Hết đơn cần Chuẩn bị hàng"** y shop khỏe. **Sửa:** protocol thêm
   `prepareBlocked` (extension gửi thay noOrder khi mọi card có nút đều không đọc được mã; app+extension phát
   hành CÙNG gói nên không có lệch phiên bản); channel thêm cờ `PrepareBlockedSeen` (khuôn CaptchaSeen, reset
   đầu mỗi lượt shop); runner log ⛔ đúng bệnh. `pageFindPrepareOrder` nay BỎ QUA card hỏng mã để thử card kế
   (một card lạ không chặn cả shop); flow chỉ chốt `thieuMa` sau trọn cửa sổ poll 12s (né render dở).
5. **[TB] T6 thiếu vế `!LaDonHuy`** — NeedsAction đếm trên đơn không-hủy, thiếu vế này thì đơn "Chờ lấy hàng"
   đã có cancel_reason vẫn lệch giữa hai bộ đếm. **Sửa:** SELECT thêm status_description + cancel_reason, lọc
   `!LaDonHuy(...)` trước `LaChuanBiHang`; test thêm dòng V5 (chờ + có lý do hủy → không đếm).
6. **[NHẸ đã sửa]** comment trỏ hàm ma `MarkForRepush` → `DatLaiCoDayLai`; comment T2 nói quá về client cũ
   (laTraHang=null — lưới còn lại là TachMa) viết lại; `_ngayDangGui` volatile.
7. **[NHẸ ghi nhận, không sửa]** T9 xoá file theo tên sanitize có thể đụng đơn khác (xác suất ~0 với mã Shopee);
   T6 đọc mọi đơn trong ngày mỗi ≤10s (vài nghìn dòng — chấp nhận, WAL); T7 máy chạy client cũ (chưa gửi
   prepared_day) biến khỏi digest — fleet cập nhật cùng nhau nên chấp nhận.

Phản biện cũng TỰ BÁC 11 nghi vấn (bảng trong báo cáo phản biện): NULL-vs-0 của cờ sheet, thứ tự SET trong
UPDATE SQLite, đẩy-lại-vô-ích, done-đếm-disabled của T1, vớt nhầm loại dòng của T2, vòng-lặp-vô-tận của T9,
missing-nghĩa-là-thử-lại phía client, T11 nổ oan, kiểu dữ liệu T4, T10 nuốt cảnh báo, tổng digest lệch.

**Kiểm chứng khoanh vùng:** drain-khi-tắt của webhook queue KHÔNG có unit test (OrderNotifyService._sender
cứng, test HTTP thật chậm/bấp bênh) — nghiệm bằng journalctl ở lượt deploy (restart service là đi qua đúng
đường đó); JS T1/T11 chỉ có node --check + test cú pháp — nghiệm sống ở vòng chạy thật sau khi thay bin.

## Tiến độ
- [x] Client T1 T2 T3 T5 T10 T11 T12 + tests + thử phá
- [x] Hub T4 T6 T7 T8 T9 + tests + thử phá
- [x] Phản biện + sửa theo (2 NẶNG + 3 TB + 3 NHẸ — 13 lượt thử phá tổng cộng, đều đỏ đúng test)
- [x] Commit client (`8840568`) · commit hub (`a7a1c1e`) — đã push `origin/main`
- [x] Deploy hub: publish linux-x64 → scp `Shopee.Hub.Web.dll` (hub LINK source nên 1 DLL chứa trọn đợt, gồm
  cả OrderNotifyService) → backup `.bak-20260811-t1t12` → install → restart — health `{"ok":true,"pg":true}`,
  journal khởi động sạch 23:30
- [x] Bin máy này: dừng app đúng cửa nghỉ sau vòng 23:43 (12/12, 1128 đơn — vòng 2 của v1.9.1) → build bin
  0W/0E → mở lại + tự bấm 23:45 → shop 1–3 chạy sạch trên bản T1–T12 (0 mẫu xấu, 0 câu cảnh báo mới nào nổ
  oan); watcher trông tới hết vòng

**Kiểm chứng sống còn khoanh lại:** đường T1 (cổng tick + xác minh tag) và T3 (prepareBlocked) chỉ chạy khi
shop CÓ đơn Chờ Lấy Hàng — vòng đêm toàn 0 đơn nên mới xác nhận được mặt không-hồi-quy; hai đường dương tính
tự lộ ở vòng ban ngày (đều đã có test + 13 lượt thử phá). Drain-khi-tắt của webhook queue nghiệm ở lượt restart
hub KẾ TIẾP (lượt 23:30 chạy binary cũ lúc dừng).

**TRẠNG THÁI: HOÀN THÀNH** — 12/12 mục T sửa xong, phản biện đã xử, hub đã deploy, máy này chạy bản mới.
**Đã phát hành client v1.9.2** sáng 12/08 (commit `2400c6b` bump + CHANGELOG; vpk pack + upload GitHub thành
công, delta 1.9.1→1.9.2 chỉ 0.5 MB / 17 file vá). Trước khi phát hành, bản T1–T12 đã chạy thật **4 vòng liên
tiếp sạch** trên máy này (23:45 → 05:25: 12/12 shop mỗi vòng, 1128→1130 đơn, 2 phiếu lưu, T1 verify-tag chạy
thật 2 lượt, 0 mẫu xấu cả đêm). Các máy khác nhận qua "Cập nhật & khởi động lại".
