# Plan: GSheet ghi vào tab "Tháng mm-yyyy" tự động theo tháng

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Sync đơn hiện đẩy đơn lên Google Sheet qua Web App Apps Script
(`GoogleSheetSyncService.PushAsync(url, tabName, rows, …)` — body `{"tab":"…","orders":[…]}`).
Tên tab lấy từ Cài đặt: `SettingsRepository.GetGsheetTabName()` — trống thì trả mặc định `"tháng 4"`
(`DefaultGsheetTabName`). Gọi tại `AccountSession.PushOrdersToGsheetAsync` (~dòng 1492).

**Yêu cầu mới:** tab đích tự động theo tháng — `"Tháng MM-yyyy"` (vd `"Tháng 07-2026"`). Script phía
Google sẽ được người dùng cập nhật riêng để TỰ TẠO sheet khi chưa có (không thuộc phạm vi plan này).

**Vấn đề phải xử cho đúng:** một đơn được đẩy LẶP LẠI nhiều lần (bổ sung vận đơn cột B, link phiếu cột C,
đổi trạng thái hủy để tô màu — xem điều kiện chọn gửi ~dòng 1455–1466). Script tìm dòng theo mã đơn
TRONG TAB ĐÍCH, không thấy thì THÊM dòng mới. Nếu tab đổi theo tháng mà đơn cũ (đã ghi tab tháng trước)
có cập nhật → ghi sang tab tháng mới = NHÂN ĐÔI dòng + dòng cũ thành mồ côi. Giải pháp: **nhớ tab đã ghi
lần đầu của từng đơn** (cột DB mới `gsheet_tab`), mọi lần đẩy lại dùng đúng tab đó.

**Quyết định đã chốt với người dùng:**
- Ô "Tab ghi vào" ở Cài đặt GIỮ làm override: điền tên cụ thể → ghi cố định vào tab đó (đơn MỚI);
  để trống → tự động `"Tháng MM-yyyy"` theo tháng hiện tại lúc đẩy.
- Đơn đẩy lại LUÔN về tab đã nhớ (`gsheet_tab`), bất kể override/tháng hiện tại.
- Định dạng tên tab: `"Tháng "` + `MM-yyyy` (MM có số 0 đứng đầu), vd `Tháng 01-2026`, `Tháng 12-2026`.

## 2. Phạm vi

- **Làm:** đổi cách tính tab đích + cột DB nhớ tab + backfill dữ liệu cũ + UI Cài đặt + test, toàn bộ
  trong `orders/`.
- **Không làm:**
  - KHÔNG sửa Apps Script / không thêm logic tạo sheet phía app (script của người dùng lo).
  - KHÔNG đổi hợp đồng JSON với script (`{"tab":…,"orders":…}` giữ nguyên).
  - KHÔNG đụng luồng đẩy hub, dọn đơn kết thúc, hay các phần khác của `PushOrdersToGsheetAsync`.

## 3. Các bước thực hiện

### Bước 1 — Helper tên tab theo tháng (Core, file mới)

`orders/XuLyDonShopee.Core/Services/GsheetTabName.cs`:

```csharp
namespace XuLyDonShopee.Core.Services;

/// <summary>Tên tab Google Sheet theo tháng: "Tháng MM-yyyy" (vd "Tháng 07-2026").</summary>
public static class GsheetTabName
{
    public static string ForMonth(DateTime date) => $"Tháng {date:MM-yyyy}";
}
```

### Bước 2 — `SettingsRepository` (Core/Data/SettingsRepository.cs)

- `GetGsheetTabName()` hiện trả `"tháng 4"` khi trống → đổi thành trả **`""` (chuỗi rỗng)** khi
  thiếu/trống (giữ trim). Cập nhật doc comment: rỗng = tự động theo tháng; caller tự resolve.
- GIỮ const `DefaultGsheetTabName = "tháng 4"` — chỉ còn dùng cho backfill migration (bước 3) — sửa
  doc comment nói rõ vai trò legacy.
- `SetGsheetTabName` giữ nguyên (trống → xóa key).

### Bước 3 — Cột DB `gsheet_tab` + backfill (Core/Data)

- `Database.cs`: thêm `gsheet_tab TEXT` vào `CREATE TABLE orders` (DB mới) + `EnsureColumn(conn,
  "orders", "gsheet_tab", "TEXT")` cạnh nhóm cột gsheet hiện có (~dòng 161).
- **Backfill một lần** (chạy ngay sau EnsureColumn, idempotent tự nhiên vì điều kiện `IS NULL`):

  ```sql
  UPDATE orders SET gsheet_tab = COALESCE(
      (SELECT value FROM settings WHERE key = 'gsheet_tab_name'), 'tháng 4')
  WHERE gsheet_synced_at IS NOT NULL AND gsheet_tab IS NULL;
  ```

  Lý do: đơn đã ghi sheet TRƯỚC bản này nằm ở tab cũ (tên trong setting, mặc định cũ "tháng 4") —
  phải nhớ lại kẻo lần đẩy cập nhật sau nhân đôi dòng ở tab tháng mới. Đối chiếu tên bảng/cột settings
  thật trong `Database.cs`/`SettingsRepository` trước khi viết (key: `gsheet_tab_name`); giá trị setting
  có thể có khoảng trắng → `TRIM(value)` và coi chuỗi rỗng như NULL:
  `COALESCE(NULLIF(TRIM((SELECT …)), ''), 'tháng 4')`.
- `OrdersRepository`:
  - `GsheetPendingOrder` record: thêm field `string? GsheetTab`.
  - `GetForGsheetPush`: SELECT thêm `gsheet_tab`, map vào field mới.
  - `MarkGsheetSynced(…)`: thêm tham số `string tab` — ghi `gsheet_tab = COALESCE(gsheet_tab, $tab)`
    (giữ tab lần đầu, không đổi khi đẩy lại). Cập nhật mọi caller + test.

### Bước 4 — `AccountSession.PushOrdersToGsheetAsync` (App, ~dòng 1412–1531)

- Tính tab cho TỪNG đơn:

  ```csharp
  var overrideTab = _services.Settings.GetGsheetTabName();           // "" = tự động
  var autoTab = GsheetTabName.ForMonth(DateTime.Now);
  var defaultTab = string.IsNullOrEmpty(overrideTab) ? autoTab : overrideTab;
  // per đơn: p.GsheetTab (đã nhớ) ?? defaultTab
  ```

- Vì `PushAsync` nhận MỘT tab/lượt: **gộp rows theo tab đích** (`Dictionary<string, List<GsheetOrderRow>>`,
  thứ tự đơn trong mỗi nhóm giữ nguyên), gọi `PushAsync` lần lượt cho từng nhóm (thường 1–2 nhóm).
  Kết quả các nhóm gộp lại xử lý như cũ (đếm added/updated/withFile/errors chung một dòng log tổng).
  Một nhóm ném lỗi → log lỗi và DỪNG các nhóm sau (mạng đang hỏng — giữ hành vi "đơn định-gửi chưa
  settled, lượt sau đẩy lại"); OCE ném xuyên như cũ.
- `MarkGsheetSynced` truyền tab của nhóm mà đơn đó được gửi.
- Nhớ map mã đơn → tab đã gửi để truyền đúng (tương tự `daHuyByMaDon`).

### Bước 5 — UI Cài đặt (App)

- `SettingsView.axaml` (~dòng 152–159): watermark ô Tab đổi `"tháng 4"` → `"tự động: Tháng 07-2026"`
  (chuỗi TĨNH mô tả — đừng bind DateTime vào watermark cho phức tạp; dùng ví dụ cố định
  `"để trống = tự động Tháng MM-yyyy"` nếu muốn trung tính). Text mô tả dưới card sửa thành: để trống =
  tự ghi vào tab "Tháng MM-yyyy" theo tháng hiện tại (tự tạo bởi script); điền tên = ghi cố định tab đó.
- `SettingsViewModel` (~dòng 52–54, 96, 151–170): cập nhật doc comment + hành vi phản ánh sau Lưu
  (trống GIỮ trống — không còn hiện "tháng 4").

### Bước 6 — Test (`orders/XuLyDonShopee.Tests`)

- Mới `GsheetTabNameTests.cs`: `ForMonth(2026-01-05)` → `"Tháng 01-2026"`; `ForMonth(2026-12-31)` →
  `"Tháng 12-2026"`; culture-độc-lập (chạy được trên máy locale bất kỳ — format MM-yyyy không phụ thuộc culture).
- `SettingsRepositoryTests`: case trống giờ trả `""` (sửa test cũ đang expect "tháng 4").
- `OrdersRepositoryTests`: `GetForGsheetPush` map `GsheetTab`; `MarkGsheetSynced` ghi tab lần đầu +
  KHÔNG đè khi gọi lại với tab khác.
- `DatabaseMigrationTests`: DB cũ có đơn `gsheet_synced_at NOT NULL` + setting `gsheet_tab_name` có/không
  → sau migration `gsheet_tab` = tên setting / `"tháng 4"`; đơn chưa đẩy → `gsheet_tab` vẫn NULL.
- Chạy `dotnet test orders/XuLyDonShopee.Tests` — toàn bộ xanh, không làm đỏ test cũ.

## 4. Tiêu chí nghiệm thu

- [ ] Build 3 project orders 0 error; `dotnet test orders/XuLyDonShopee.Tests` xanh toàn bộ.
- [ ] Setting trống → đơn MỚI ghi tab `"Tháng MM-yyyy"` theo tháng hiện tại; setting có giá trị → ghi
      tab đó. Đơn đẩy LẠI luôn về tab đã nhớ trong `gsheet_tab`.
- [ ] Migration: DB cũ lên bản mới, đơn đã ghi sheet được backfill `gsheet_tab` đúng; chạy lại lần 2
      không đổi gì (idempotent).
- [ ] Nhiều tab trong một lượt đẩy → gọi PushAsync theo từng nhóm, kết quả gộp, lỗi nhóm giữa chừng
      không đánh dấu settled các đơn chưa gửi.
- [ ] UI Cài đặt phản ánh hành vi mới (trống = tự động, không tự điền "tháng 4" nữa).

## 5. Rủi ro & lưu ý

- Script phía Google CHƯA update thì tab tháng mới chưa tồn tại → script trả `{"error":"Không tìm thấy
  tab …"}` → app log lỗi, đơn giữ chưa-settled, lượt sau đẩy lại (hành vi sẵn có, chấp nhận trong lúc
  chuyển tiếp; người dùng có thể điền override "tháng 4" để dùng tạm).
- `DateTime.Now` đọc MỘT lần mỗi lượt push (không đọc lại giữa vòng lặp) — lượt đẩy vắt qua nửa đêm
  cuối tháng thì cả lượt vẫn một tab, đơn giản và nhất quán.
- Đừng quên `NULLIF(TRIM(…), '')` trong backfill — setting từng lưu chuỗi có khoảng trắng sẽ thành tab sai.
- `MarkGsheetSynced` đổi chữ ký — rà mọi caller (App + test) kẻo sót.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
