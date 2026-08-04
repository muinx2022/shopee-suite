# Plan: Banner lỗi địa chỉ — bỏ so mốc giờ, dùng `rev` + outbox

- **Ngày:** 2026-08-04
- **Trạng thái:** đang làm
- **Người lập:** phiên chính · **Người thực thi:** phiên chính

## 1. Bối cảnh & mục tiêu

Đồng bộ banner lỗi địa chỉ đa máy hiện quyết định bằng **so mốc thời gian**, mà các máy có đồng hồ độc lập.
Hai lần vá bằng cách so mốc đều tạo ca hỏng nặng hơn lỗi định vá (xem
`plans/2026-08-04-sua-5-lo-hong-banner-dia-chi.md`, mục "Đổi hướng"):

- So `localCreatedAt` với `hubDismissedAt` ⇒ banner **kẹt vĩnh viễn** (mốc local bị mirror-upsert bump mỗi 60s).
- So `$d < created_at` trong SQL dismiss ⇒ Hub **từ chối bấm X vĩnh viễn** khi máy phát hiện lỗi nhanh giờ.

Nên hiện tại đang chấp nhận một lỗ: tombstone Hub cũ xoá nhầm banner của lỗi vừa phát hiện mà Hub chưa kịp
biết (đẩy Hub fail). Tự lành sau 1 vòng shop (3–5'), nhưng vẫn là mất cảnh báo trong khoảng đó.

**Mục tiêu:** bỏ HẲN mọi so sánh thời gian khỏi đường quyết định, thay bằng:

1. **`rev`** — số hiệu bản ghi tăng dần **do Hub cấp** (một đồng hồ duy nhất, là bộ đếm của Hub).
2. **Outbox** — cờ local "thay đổi chưa đẩy được lên Hub"; Hub **không được đè** dòng đang chờ đẩy.

Hệ quả: mất mạng/Hub chết bao lâu cũng không mất cảnh báo; mọi máy hội tụ về đúng một trạng thái; không còn
phụ thuộc đồng hồ máy nào.

## 2. Phạm vi

**Làm:**

- Hub: thêm cột `rev`, mọi ghi đều `rev = rev + 1`; API trả `rev` (cả GET lẫn 2 POST).
- Client: thêm cột `hub_rev` + `cho_day` (outbox) vào `pickup_address_alerts`.
- Viết lại `PickupAlertMerge` theo `rev`/outbox, bỏ mọi tham số thời gian.
- Vòng đẩy outbox: phát hiện lỗi / bấm X ghi `cho_day=1`; đẩy thành công mới hạ cờ + lưu `rev`.
- Test hai phía, gồm ca "Hub chết khi phát hiện lỗi" mà bản hiện tại đang thua.
- Deploy Hub **trước**, release client sau.

**Không làm:**

- KHÔNG đổi hành vi nghiệp vụ: bỏ qua shop lỗi + chạy shop kế, nội dung banner, Slack, `orders/app-alert`.
- KHÔNG bỏ cột `created_at`/`dismissed_at` trên Hub — giữ để đọc/chẩn đoán, chỉ thôi dùng để QUYẾT ĐỊNH.
- KHÔNG backfill lịch sử: dòng cũ nhận `rev=0`, lượt ghi kế nâng lên 1.
- KHÔNG đụng client đời cũ: họ vẫn chạy luật thời gian như trước (Hub tương thích ngược).

## 3. Các bước thực hiện

### Bước 1 — Hub: cột `rev` + trả về rev

File `server/Shopee.Hub.Web/Data/HubDatabase.PickupAlerts.cs`:

- Schema: thêm `rev INTEGER NOT NULL DEFAULT 0`; DB cũ dùng `AddColumnIfMissing("orders_pickup_alerts", "rev", "INTEGER NOT NULL DEFAULT 0")`.
- `UpsertPickupAlert` → `dismissed_at=NULL`, `created_at=$now`, `province=$p`, `rev=rev+1`. **Bỏ** tham số
  `occurredAtIso` và toàn bộ CASE so mốc.
- `DismissPickupAlert` → `dismissed_at=$now`, `rev=rev+1`. Vẫn UPSERT (chưa có dòng vẫn tạo tombstone).
- Cả hai đổi kiểu trả về thành `long` = rev mới (0 = từ chối vì thiếu khoá). Lấy rev bằng
  `RETURNING rev` (SQLite 3.35+; `Microsoft.Data.Sqlite` bản dùng ở đây có) — nếu không, `SELECT rev` ngay
  sau trong cùng `lock (_gate)`.
- `OrdersPickupAlertRow` thêm `long Rev`; `ListPickupAlerts` select thêm cột.

### Bước 2 — Hub API

File `server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs`:

- 2 POST trả `Results.Json(new { rev })` thay vì `Results.Ok()`.
- GET map thêm `Rev`.

File `suite/Shopee.Core/Coordination/OrderDtos.cs`:

- `OrdersPickupAlertItem` thêm `long Rev`.
- Thêm `OrdersPickupAlertAck { long Rev }` cho body trả về của 2 POST.
- Giữ `OccurredAt` trong request (client cũ còn gửi) nhưng đánh dấu **không dùng nữa**.

### Bước 3 — Client: cột outbox

File `orders/XuLyDonShopee.Core/Data/Database.cs`: `EnsureColumn(conn, "pickup_address_alerts", "hub_rev", "INTEGER NOT NULL DEFAULT 0")`
và `"cho_day"` tương tự (đặt trong khối migration sẵn có).

File `orders/XuLyDonShopee.Core/Data/PickupAddressAlertsRepository.cs` — tách rõ hai nguồn ghi:

| Hàm | Dùng khi | Ghi |
|---|---|---|
| `GhiPhatHienTaiCho(accountId, shop, province)` | vòng shop phát hiện lỗi | active, `cho_day=1` |
| `DismissTaiCho(accountId, shop)` | user bấm X | dismissed, `cho_day=1` |
| `ApDungTuHub(accountId, shop, province, dismissed, hubRev)` | merge nhận trạng thái Hub | theo Hub, `cho_day=0`, `hub_rev=hubRev` |
| `DanhDauDaDay(accountId, shop, hubRev)` | đẩy Hub thành công | `cho_day=0`, `hub_rev=hubRev` |
| `ListChoDay(accountId)` | rút hàng đợi outbox | các dòng `cho_day=1` |

`PickupAddressAlert` record thêm `long HubRev`, `bool ChoDay`.

### Bước 4 — Luật merge mới (không đồng hồ)

File `orders/XuLyDonShopee.Core/Services/PickupAlertMerge.cs` — viết lại:

```csharp
public enum MergePickupAlertAction { TheoHub, DayLenHub, GiuNguyen }

public static MergePickupAlertAction QuyetDinh(
    bool localCoDong, bool localChoDay, long localHubRev, long hubRev)
```

- `localChoDay` → `DayLenHub` (thay đổi local chưa tới Hub — Hub **không được** đè).
- `!localCoDong` → `TheoHub` (máy này chưa biết gì về shop đó).
- `hubRev > localHubRev` → `TheoHub`.
- còn lại → `GiuNguyen`.

`PickupAlertHubDong` bỏ `CreatedAt`/`DismissedAt`, thêm `Rev`.

### Bước 5 — Client: vòng đẩy + merge

File `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.KetQua.cs`:

- Tập shop cần xét = shop Hub trả về **∪** shop local `cho_day=1` (dòng chờ đẩy mà Hub chưa có phải được đẩy).
- `TheoHub` → `ApDungTuHub(...)`; `DayLenHub` → đẩy upsert (local active) hoặc dismiss (local dismissed),
  thành công thì `DanhDauDaDay(rev)`; `GiuNguyen` → không làm gì.
- Đẩy vẫn qua `PickupAlertHubGate` + bắn NGAY trong callback UI (bẫy `RunOnUi` = `BeginInvoke`).

File `orders/XuLyDonShopee.App/Services/OrderPersistPipeline.cs` — `GhiBannerLoiDiaChi`: dùng
`GhiPhatHienTaiCho`, đẩy Hub, thành công thì `DanhDauDaDay(rev)`; thất bại **để nguyên cờ** (nhịp sync sau tự
đẩy lại — đây chính là chỗ vá lỗ hiện tại).

Hook trong `AppServices.cs` + `OrdersModuleHost.HubPush.cs`: 2 hook đẩy đổi kiểu trả về từ `Task<bool>` sang
`Task<long?>` (rev mới; `null` = chưa đẩy được). Bỏ tham số `occurredAt`.

### Bước 6 — Test

Hub (`server/Shopee.Hub.Web.Tests/PickupAlertsHubTests.cs`): rev tăng đều qua upsert/dismiss; dismiss khi chưa
có dòng vẫn tạo tombstone rev=1; GET trả rev.

Client (`orders/XuLyDonShopee.Tests/PickupAddressAlertsTests.cs`):

- Ma trận `QuyetDinh` đủ 4 nhánh.
- **Ca hiện tại đang thua:** local phát hiện lỗi lúc Hub chết (`cho_day=1`), Hub còn tombstone cũ →
  `DayLenHub`, banner **không bị xoá**.
- Lan dismiss đa máy: máy A bấm X (Hub rev tăng) → máy B `hubRev > localHubRev` → `TheoHub` → gỡ banner.
- Repository: `GhiPhatHienTaiCho` đặt `cho_day=1`; `DanhDauDaDay` hạ cờ + lưu rev; `ListChoDay` đúng tập.
- Migration: DB cũ (thiếu 2 cột) mở lên không mất dữ liệu, cột mới mặc định 0.

### Bước 7 — Build, deploy, release

- `dotnet build ShopeeSuite.sln` 0 warning; `dotnet test` cả hai project.
- Deploy Hub **trước** (client cũ vẫn chạy được), health 200.
- Bump `version.txt` → **1.7.19** + CHANGELOG, `release-suite.cmd`, cập nhật bản cài local.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` → 0 warning, 0 error.
- [ ] `dotnet test` orders + hub xanh, số test không giảm.
- [ ] `grep -n "DateTime\|Iso\|OccurredAt" orders/XuLyDonShopee.Core/Services/PickupAlertMerge.cs` → **không kết quả** (đường quyết định sạch đồng hồ).
- [ ] Test chứng minh: `cho_day=1` + Hub tombstone → giữ banner + đẩy lên Hub (ca bản hiện tại thua).
- [ ] Test chứng minh: máy khác bấm X → rev tăng → máy này gỡ banner (không hồi quy chức năng chính).
- [ ] Test migration: DB client cũ mở được, không mất dòng.
- [ ] Hub deploy xong: health 200; `strings -el` DLL VM có `rev=rev+1`; bảng có cột `rev`.
- [ ] GitHub Releases có v1.7.19; bản cài local lên 1.7.19.

## 5. Rủi ro & lưu ý

- **Tương thích ngược là bắt buộc**: fleet còn máy chạy ≤ v1.7.18 gửi `OccurredAt` và không hiểu `rev`. Hub
  phải nhận request cũ bình thường; field lạ trong JSON trả về bị client cũ bỏ qua.
- **Bẫy `RunOnUi` = `BeginInvoke`**: không gom biến trong callback rồi đọc sau khối; bắn side-effect NGAY
  trong callback (lỗi này đã làm nhánh repush chết câm suốt v1.7.16).
- **Không để outbox kẹt vĩnh viễn**: mỗi lượt sync phải thử đẩy lại mọi dòng `cho_day=1`, kể cả dòng Hub
  không trả về.
- Migration SQLite: `ALTER TABLE ADD COLUMN` với `NOT NULL DEFAULT 0` hợp lệ; dòng cũ nhận 0.
- Đổi kiểu trả về hook `Task<bool>` → `Task<long?>` chạm nhiều nơi — build sẽ chỉ ra hết, giữ 0 warning.

---

## Báo cáo thực thi (điền sau khi xong)
