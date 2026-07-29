# Plan: Sửa 3 lỗi mất dữ liệu ở module Đơn hàng (SKU bị xóa, mất đếm "Đã bán", sót mã trả hàng)

- **Ngày:** 2026-07-29
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Auto (Cursor)

## 1. Bối cảnh & mục tiêu

Review module `orders/` phát hiện 3 lỗi mất dữ liệu im lặng, liên quan dây chuyền với nhau:

**Lỗi A — cột `orders.sku` bị ghi đè NULL mỗi lượt sync.**
Trong `OrdersRepository.UpsertMany` (nhánh UPDATE, `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs` dòng ~180–197), các cột dễ mất đã được bảo vệ: `final_amount`/`final_amount_text`/`tracking_number`/`shop_id`/`shop_login` dùng `COALESCE($moi, cot_cu)`, còn `items_json` được chọn bản giàu hơn bằng C# (`SanPhamDonParser.ChonItemsJson`, dòng 202–207). Riêng `sku = $sku` và `item_summary = $itemSummary` vẫn ghi đè thẳng.

`SyncedOrder.Sku` sinh ở `ShopeeLoginService.ParseOrdersJson` (dòng ~2352–2364): đoán từ tên sản phẩm ĐẦU qua `ShopeeShippingNav.ExtractSku(itemSummary)`. Nó là **null** khi lượt quét không render được mảng `items` (DOM lazy-render) hoặc tên không có đuôi chữ+số. Lượt quét như vậy sẽ **xóa SKU đã lưu** của đơn, dù `items_json` (bản giàu từ trang chi tiết) vẫn còn khóa `sku` thật trong JSON.

Hệ quả dây chuyền: `GetForSoldCountRetry` (dòng ~854–858) lọc `sku IS NOT NULL AND TRIM(sku) <> ''` → đơn mất SKU rơi khỏi hàng đợi đếm bù "Đã bán".

**Lỗi B — `DetectNewlyDelivered` chỉ tin SKU từ lượt quét, không đọc SKU trong DB.**
`OrdersRepository.DetectNewlyDelivered` (dòng ~756–825): câu SELECT dòng 763 chỉ lấy `order_sn, status, sold_counted_at` — KHÔNG lấy `sku`. Khi đơn chuyển sang đã-giao mà `o.Sku` (từ scan) null, code đưa vào `immediateMark` (dòng 811–814) → caller (`AccountSession.PersistOrdersResult`, dòng ~315–318) gọi `MarkSoldCounted` đóng cờ `sold_counted_at` **mà không +1** lên hub. Cờ đã đóng thì đường đếm bù `GetForSoldCountRetry` cũng loại đơn ra → **mất đếm "Đã bán" vĩnh viễn**, dù DB đang có SKU (cột `sku` hoặc trong `items_json`).

Lưu ý: `DetectNewlyDelivered` được gọi TRƯỚC `UpsertMany` trong cùng lượt (xem doc của hàm) — nên tại thời điểm chạy, DB vẫn còn SKU cũ (nếu chưa bị lỗi A xóa).

**Lỗi C — nhánh TĂNG của check trả hàng không kẹp trần 50 dòng.**
`TraHangParser.QuyetDinhCheck` (`orders/XuLyDonShopee.Core/Services/TraHangParser.cs` dòng 124–140): nhánh `LanDau` kẹp `Math.Min(moi, tranDong)` nhưng nhánh `Tang` trả nguyên `moi - mocCu.Value`. Extension chỉ gửi tối đa `TranDongMoiLuot` = 50 dòng (khớp `MAX_RETURN_ROWS` bên `extensions/shopee-orders/background.js`). Caller (`OrdersBridgeSession.cs` dòng ~1005–1042) `Take(k)` rồi **luôn ghi mốc = soMoi**. Khi shop tăng >50 yêu cầu giữa hai lần check (máy tắt vài ngày), chỉ đọc được 50 mã mới nhất, phần cũ hơn mất và mốc đã nhảy.

**Ràng buộc quan trọng đã chốt:** phần vượt trần KHÔNG THỂ cứu bằng C# — extension cắt danh sách ở 50 dòng trước khi gửi, dòng 51+ không bao giờ tới C#, và giữ mốc thấp để check lại cũng chỉ đọc lại đúng 50 dòng mới nhất. Fix ở đây là kẹp trần cho đúng ý định + **log cảnh báo rõ ràng** khi vượt trần để người dùng biết có thể sót. Phân trang extension là việc khác, ngoài phạm vi.

## 2. Phạm vi

- **Làm:**
  1. `UpsertMany`: `sku` và `item_summary` không bị NULL đè (COALESCE).
  2. `DetectNewlyDelivered`: fallback SKU từ DB khi scan không có.
  3. Backfill MỘT LẦN cột `sku` từ `items_json`/`item_summary` cho dữ liệu đã hỏng sẵn.
  4. `QuyetDinhCheck`: kẹp trần nhánh `Tang` + log cảnh báo vượt trần ở caller.
  5. Test cho từng thay đổi.
- **Không làm:**
  - KHÔNG phân trang trả hàng bên extension (`background.js` giữ nguyên).
  - KHÔNG đổi cách ghi mốc trả hàng (`_saveReturnCount` vẫn ghi `soMoi`).
  - KHÔNG reset `hub_synced_at` trong backfill (tránh sóng re-push; sync thường sẽ tự cập nhật dần).
  - KHÔNG đụng các phát hiện khác của đợt review (captcha thiếu TCS, worker snapshot SKU, WAL…) — sẽ có plan riêng.

## 3. Các bước thực hiện

### Bước 1 — `UpsertMany` giữ `sku` + `item_summary` (file `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`)

Trong SQL UPDATE (dòng ~183–184) đổi:

```sql
item_summary = $itemSummary, sku = $sku,
```

thành:

```sql
item_summary = COALESCE($itemSummary, item_summary), sku = COALESCE($sku, sku),
```

- Hành vi: lượt quét có giá trị mới (khác null) → đè bình thường (cùng nguồn đoán-từ-tên, không có chuyện bản mới "nghèo" hơn); lượt quét null → GIỮ giá trị cũ.
- Cập nhật khối comment phía trên SQL (dòng ~157–179): thêm 1–2 dòng giải thích vì sao `sku`/`item_summary` cũng COALESCE (kịch bản DOM không render items → null xóa SKU → đơn rơi khỏi hàng đợi đếm "Đã bán").
- Nhánh INSERT giữ nguyên.

### Bước 2 — `DetectNewlyDelivered` fallback SKU từ DB (cùng file)

- Câu SELECT dòng 763 thêm cột: `SELECT order_sn, status, sold_counted_at, sku FROM orders WHERE account_id = $a;`
- Dictionary `existing` đổi value tuple thành `(string? Status, bool Counted, string? Sku)`.
- Tại nhánh "chuyển chưa-giao → đã-giao" (dòng ~804–814), đổi logic chọn SKU:

```csharp
var sku = o.Sku?.Trim();
if (string.IsNullOrEmpty(sku))
{
    sku = e.Sku?.Trim(); // scan không có SKU → lùi về SKU đã lưu trong DB
}
if (!string.IsNullOrEmpty(sku)) { skus.Add(sku); pendingMark.Add(o.OrderSn); }
else { immediateMark.Add(o.OrderSn); }
```

- Cập nhật XML doc của hàm (mục "không SKU → chỉ đánh cờ NGAY") thành "không SKU ở CẢ scan LẪN DB → đánh cờ ngay".
- KHÔNG đổi các nhánh grandfather / đơn mới toanh.

### Bước 3 — Backfill một lần cột `sku` (file `orders/XuLyDonShopee.Core/Data/Database.cs`)

Theo đúng khuôn `BackfillHubFinalAmountOnce` (gọi cuối `Initialize` ~dòng 282, thân ~293–316: chốt bằng key trong bảng `settings`):

- Hằng key: `private const string BackfillSkuFromItemsKey = "sku_backfill_from_items_v1";`. Đã có key → return ngay (idempotent).
- Gọi hàm mới `BackfillSkuFromItemsOnce(conn)` ngay sau `BackfillHubFinalAmountOnce(conn)`.
- Chọn các dòng `sku IS NULL OR TRIM(sku) = ''` mà có `items_json` hoặc `item_summary` (không cần lọc theo account).
- Với mỗi dòng, tính SKU theo thứ tự:
  1. `PhanLoaiExtractor.SkuTuItemsJson(items_json)` — đã có sẵn, lấy SKU THẬT từ trang chi tiết (cùng assembly Core, được phép). Chuỗi rỗng = không có.
  2. Không có → `ShopeeShippingNav.ExtractSku(item_summary)` (cùng luật đoán-từ-tên lúc quét).
  3. Vẫn không có → bỏ qua dòng đó.
- UPDATE `sku` cho các dòng tính được, trong **MỘT transaction** cùng với INSERT key settings (tránh trạng thái nửa vời — khác `BackfillHubFinalAmountOnce` hiện không atomic, đừng lặp lại điểm yếu đó).
- JSON hỏng / parse lỗi từng dòng → bỏ qua dòng đó, không phá cả đợt (try/catch quanh từng dòng nếu helper ném; `SkuTuItemsJson`/`ExtractSku` hiện không ném nhưng giữ an toàn).
- `using XuLyDonShopee.Core.Services;` nếu file chưa có.

### Bước 4 — Kẹp trần nhánh `Tang` + cảnh báo (2 file)

`orders/XuLyDonShopee.Core/Services/TraHangParser.cs` dòng 139:

```csharp
return new QuyetDinhTraHang(LuatSoYeuCau.Tang, Math.Min(moi - mocCu.Value, Math.Max(0, tranDong)));
```

Cập nhật XML doc dòng 113–121: nói rõ nhánh Tang cũng kẹp trần, và phần vượt trần là giới hạn cứng của extension.

`orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`, nhánh log `default:` (dòng ~1000–1002) — caller có đủ `mocCu`/`soMoi`, thêm cảnh báo khi vượt trần:

```csharp
default:
    L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — TĂNG {soMoi - mocCu!.Value} so với mốc {mocCuText}, check {quyetDinh.SoDongCanCheck} dòng đầu.");
    if (soMoi - mocCu.Value > TraHangParser.TranDongMoiLuot)
    {
        L($"Check đơn trả hàng [{shopLogin}]: CẢNH BÁO — tăng {soMoi - mocCu.Value} vượt trần {TraHangParser.TranDongMoiLuot} dòng/lượt của extension; {soMoi - mocCu.Value - TraHangParser.TranDongMoiLuot} yêu cầu CŨ HƠN có thể bị sót (kiểm tay trang Trả hàng/Hoàn tiền nếu cần).");
    }
    break;
```

(Chỉnh cho khớp biến thật trong hàm — `mocCu` ở đó là `int?` đã biết khác null trong nhánh Tang.)

### Bước 5 — Test (file trong `orders/XuLyDonShopee.Tests/`)

1. `OrdersRepositoryTests.cs` (hoặc file test mới cùng thư mục):
   - Upsert đơn có `Sku="B01"`, `ItemSummary="Áo B01"` → upsert lại cùng `order_sn` với `Sku=null`, `ItemSummary=null` → đọc lại: `sku` vẫn `"B01"`, `item_summary` vẫn `"Áo B01"`.
   - Upsert lại với `Sku="B02"` → `sku` đổi thành `"B02"` (giá trị mới khác null vẫn đè).
2. `OrdersSoldCountTests.cs`:
   - DB có đơn `sku="B01"`, status "Chờ lấy hàng"; gọi `DetectNewlyDelivered` với scan `Status="Đã giao"`, `Sku=null` → `SkusToIncrement` chứa `"B01"`, mã đơn nằm trong `PendingMarkOrderSns`, `ImmediateMarkOrderSns` rỗng.
   - Cả scan lẫn DB đều không SKU → vẫn vào `ImmediateMarkOrderSns` (hành vi cũ giữ nguyên).
3. `DatabaseMigrationTests.cs`:
   - Tạo DB, chèn đơn `sku=NULL` với `items_json` chứa `[{"name":"x","sku":"B99"}]` và một đơn `sku=NULL`, `items_json="[]"`, `item_summary="Áo Thun B77"` → mở lại `Database` cùng đường dẫn → đơn 1 có `sku="B99"`, đơn 2 có `sku="B77"`; key settings `sku_backfill_from_items_v1` đã ghi; mở lại lần nữa không đổi gì (sửa tay `sku` về NULL rồi mở lại → không bị đè vì key đã chốt).
4. `TraHangParserTests.cs` — bổ sung (file đã có `QuyetDinhCheck_Tang_CheckDungK` cho ca dưới trần; thêm ca vượt trần):
   - `QuyetDinhCheck(mocCu: 10, soMoi: 90)` → `Luat == Tang`, `SoDongCanCheck == 50` (`TranDongMoiLuot`).
   - `QuyetDinhCheck(mocCu: 10, soMoi: 90, tranDong: 5)` → `SoDongCanCheck == 5`.
   - `QuyetDinhCheck(mocCu: 10, soMoi: 40)` → `SoDongCanCheck == 30` (dưới trần giữ nguyên — đã cover bởi Theory hiện có, không bắt buộc thêm nếu Theory còn đủ).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.Core` và `dotnet build orders/XuLyDonShopee.App` không lỗi, không warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` — toàn bộ pass (nền hiện tại: 1440 test pass).
- [ ] Các test mới ở Bước 5 có mặt và pass.
- [ ] Đọc lại diff: nhánh INSERT của `UpsertMany` không đổi; các nhánh grandfather của `DetectNewlyDelivered` không đổi; backfill idempotent (key settings + UPDATE cùng transaction).

## 5. Rủi ro & lưu ý

- **Thứ tự gọi:** `DetectNewlyDelivered` phải TIẾP TỤC chạy trước `UpsertMany` (caller `AccountSession` hiện đúng) — fallback DB sku dựa vào điều đó. Không đổi caller.
- **Đếm trùng?** Fallback DB sku chỉ đổi nhánh `immediateMark` → `pendingMark` cho đơn CHUYỂN trạng thái lần đầu; cờ `sold_counted_at` vẫn chỉ đóng sau khi hub +1 OK — không tạo đường +1 mới cho đơn đã đếm.
- **Backfill:** chỉ ghi vào dòng `sku` đang NULL/rỗng → không thể đè dữ liệu tốt; từng dòng bọc try/catch; toàn đợt + key chốt trong một transaction. Máy dữ liệu lớn: câu SELECT chỉ chạy một lần lúc khởi động, chấp nhận.
- **Trả hàng:** sau fix, phần vượt trần vẫn SÓT (giới hạn extension) — plan này chỉ làm log nói thật thay vì im lặng. Nếu về sau cần đủ 100%, làm plan riêng phân trang `background.js` + bump cặp hằng `MAX_RETURN_ROWS`/`TranDongMoiLuot`.
- **Hai biến thể dấu:** không liên quan các file này nhưng đừng "tiện tay" sửa `CanPrintSlip`/`OrderStatusPillConverter` trong plan này — ngoài phạm vi.

---

## Báo cáo thực thi (điền sau khi xong)

<để trống>
