# Plan: khóa tài khoản cho module Đơn hàng (chống hai máy cùng chạy một subaccount)

- **Ngày:** 2026-07-27
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Người dùng: *"giờ nhiều client chạy cùng 1 acc được. vd local đang test, nhưng client khác cũng chạy acc đó,
tranh nhau đơn chuẩn bị hàng"*.

Đúng — **module Đơn hàng hiện KHÔNG có khóa nào cả.** Scrape và Search đều xin *account-lease* từ Hub trước khi
chạy (`AccountLeaseScope` → `HttpCoordinationHub.ReserveAccountsAsync`), riêng Đơn hàng chưa nối vào. Hai máy
cùng chạy một subaccount không chỉ tranh đơn: chúng đăng nhập song song vào cùng tài khoản Shopee → dễ bị đá
phiên, ăn captcha, và cùng bấm "Chuẩn bị hàng" trên một đơn.

### Hạ tầng đã có (dùng lại, KHÔNG dựng mới)

- Hub: bảng `account_leases(account_id TEXT PRIMARY KEY, machine_id, hostname, heartbeat_at)` — **khóa là chuỗi
  BẤT KỲ**, không ràng buộc phải là tài khoản Shopee của kho scrape.
- `suite/Shopee.Core/Coordination/HttpCoordinationHub.cs`: `ReserveAccountsAsync(ids)` (dòng ~210, trả tập ĐƯỢC
  CẤP), `ReleaseAccountsAsync(ids)` (~222), `HeartbeatAccountsAsync(ids)` (~259), `ActiveAccountLeases()` (~54,
  trả danh sách lease đang sống kèm máy giữ).
- `CoordinationRuntime.Active` / `CoordinationRuntime.Client` — cách `OrdersModuleHost` đang kiểm hub.
- Seam hook: module Đơn hàng KHÔNG tham chiếu `Shopee.Core`; mọi thứ dính hub đi qua `Func<…>` trong
  `orders/XuLyDonShopee.App/Services/AppServices.cs`, được `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`
  rót vào.
- Vòng đời phiên: `orders/XuLyDonShopee.App/Services/AccountSession.cs` — `StartAsync()` (dòng ~143),
  `StopAsync()` (~185); `AccountSessionManager` tạo phiên theo `accountId`.

### QUYẾT ĐỊNH thiết kế (đã chốt, không tự đổi)

1. **KHÔNG dùng lại `AccountLeaseScope`.** Lớp đó gắn chặt với `ShopeeAccountUsage` — kho tài khoản Shopee của
   Scrape/Search (đánh dấu per-máy, bù tk, đóng khung). Tài khoản của module Đơn hàng là thực thể KHÁC
   (subaccount trong `app.db` riêng của module). Dùng lại sẽ làm bẩn dấu per-máy của kho scrape.
   ⇒ Gọi THẲNG 3 API lease của `HttpCoordinationHub` từ `OrdersModuleHost`.
2. **Khóa = `"orders:" + login subaccount** (trim + `ToLowerInvariant`). **TUYỆT ĐỐI KHÔNG dùng `accountId` cục
   bộ** — Id của cùng một tài khoản LỆCH giữa các máy (lỗi đã gặp: mỗi client tự tạo bản ghi nên Id khác nhau).
   Tiền tố `orders:` để không đụng khóa của kho tài khoản Scrape/Search.
3. **Bị từ chối → BỎ QUA tài khoản đó**, không xếp hàng chờ. Log rõ tên máy đang giữ.
4. **Hub không kết nối được → VẪN CHẠY** (degrade như một máy). Không có hub thì cũng không phối hợp được với ai;
   chặn sẽ làm app vô dụng khi mất mạng. Đây là cách Scrape/Search đang xử (`catch → coi như được cấp`).

## 2. Phạm vi

- **Làm:** xin/nhả/heartbeat lease theo tài khoản cho module Đơn hàng; phiên bị từ chối thì không chạy và báo rõ.
- **Không làm:**
  - KHÔNG đụng `AccountLeaseScope`, Scrape, Search, kho `ShopeeAccountUsage`.
  - KHÔNG thêm route hub mới (3 route lease đã có đủ).
  - KHÔNG làm hàng đợi/tự thử lại khi bị từ chối.
  - KHÔNG đụng phần số liệu "chuẩn bị hàng" vừa làm (plan `2026-07-27-so-don-chuan-bi-hang-chung-qua-hub.md`).
  - KHÔNG khóa ở mức shop (một subaccount = một khóa, dù nó có 12 shop).

## 3. Các bước thực hiện

### Bước 1 — `AppServices`: 2 hook mới + kiểu kết quả

Trong `orders/XuLyDonShopee.App/Services/` thêm kiểu nhỏ (module tự định nghĩa, KHÔNG dùng type của `Shopee.Core`):

```csharp
/// <summary>Kết quả xin khóa chạy một tài khoản. Ok=false ⇒ máy khác đang chạy tài khoản này;
/// HolderMachine = tên máy đang giữ (null nếu hub không nói được là máy nào).</summary>
public sealed record OrdersLeaseResult(bool Ok, string? HolderMachine);
```

Hook trong `AppServices` (doc theo văn phong các hook sẵn có):

```csharp
public Func<string, CancellationToken, Task<OrdersLeaseResult>>? AcquireAccountLease { get; set; }
public Func<string, Task>? ReleaseAccountLease { get; set; }
```

Tham số thứ nhất của cả hai là **login subaccount THÔ** (chưa thêm tiền tố, chưa hạ chữ) — việc chuẩn hóa khóa
làm ở một chỗ duy nhất phía suite (Bước 2), để module không phải biết quy ước khóa của hub.

Hook `null` (bản chạy không có hub) → phiên chạy như hiện nay.

### Bước 2 — `OrdersModuleHost`: rót hook + heartbeat

Thêm `WireAccountLease(services)`:

- **Chuẩn hóa khóa:** `static string LeaseKey(string login) => "orders:" + login.Trim().ToLowerInvariant();`
  Login rỗng → coi như không khóa được gì: trả `new OrdersLeaseResult(true, null)` (chạy bình thường), và
  `ReleaseAccountLease` bỏ qua.
- **Acquire:**
  - `!CoordinationRuntime.Active || Client is null` → trả `(true, null)` (degrade như một máy).
  - `granted = await Client.ReserveAccountsAsync(new[]{ key })`.
    - `granted.Contains(key)` → ghi key vào tập đang giữ (`HashSet<string>` dưới `lock`), bảo đảm timer heartbeat
      đang chạy, trả `(true, null)`.
    - không được cấp → tra `Client.ActiveAccountLeases()` tìm dòng có `account_id == key` để lấy tên máy/hostname
      → trả `(false, tênMáy)`. Không tra được → `(false, null)`.
  - **Mọi exception → trả `(true, null)`** (degrade), log `Trace`. Trừ `OperationCanceledException` chủ động thì
    cho xuyên.
- **Release:** bỏ key khỏi tập, `await Client.ReleaseAccountsAsync(new[]{ key })` (nuốt lỗi). Tập rỗng → **dừng
  timer heartbeat**.
- **Heartbeat:** một `System.Threading.Timer` duy nhất cho cả module, chu kỳ **60s**, gọi
  `Client.HeartbeatAccountsAsync(snapshot của tập)`; tập rỗng → không gọi. Bám đúng khuôn heartbeat trong
  `AccountLeaseScope.StartHeartbeat()` (chu kỳ, snapshot-under-lock, nuốt lỗi) — **đọc hàm đó trước khi viết**.
  Lý do bắt buộc có: lease hết hạn sau ~5' mà phiên đơn hàng chạy hàng giờ (có đoạn nghỉ 3–4' giữa hai shop).
- Gọi `WireAccountLease` cùng chỗ các `Wire*` khác đang được gọi.

### Bước 3 — `AccountSession`: xin khóa trước khi chạy, nhả khi dừng

- `StartAsync()`: **TRƯỚC khi mở trình duyệt / đổi trạng thái sang đang chạy**, lấy login của tài khoản
  (`_services.Accounts.GetById(_accountId)` — dùng ĐÚNG trường login mà module đang dùng làm tên đăng nhập
  subaccount; đọc code để lấy đúng tên trường, đừng đoán) rồi gọi `AcquireAccountLease`.
  - `Ok = false` → ghi log phiên: `"Tài khoản đang chạy ở máy {HolderMachine} — bỏ qua lượt này."`
    (không biết máy nào thì `"Tài khoản đang chạy ở máy khác — bỏ qua lượt này."`), **không** mở trình duyệt,
    **không** chuyển sang trạng thái đang chạy, kết thúc êm (giống như người dùng bấm dừng).
  - Hook null → chạy tiếp như hiện nay (không log gì thêm).
- Nhả khóa: gọi `ReleaseAccountLease` khi phiên kết thúc — **mọi lối ra**, kể cả lỗi/huỷ. Đặt ở chỗ đang dọn dẹp
  của phiên (`StopAsync` và/hoặc `finally` của vòng chạy) sao cho **nhả đúng MỘT lần** và không nhả khi chưa từng
  giành được. Dùng một cờ trong phiên (vd `_dangGiuKhoa`) để bảo đảm điều đó.
- `MarkQueued()` và các đường không thực sự chạy trình duyệt: KHÔNG xin khóa.

### Bước 4 — Test

Dùng stub hook (không cần hub, không cần trình duyệt). Nếu `StartAsync` không test được vì mở trình duyệt thật
thì **tách phần quyết định** thành một hàm nhỏ testable (vd `internal static bool DuocPhepChay(OrdersLeaseResult?)`
+ hàm dựng câu log) và test hàm đó — **KHÔNG refactor lớn chỉ để test**; nếu vẫn không test được, ghi rõ trong
báo cáo thay vì bịa test.

Các ca bắt buộc:
1. Chuẩn hóa khóa: `"  Alina99.Store "` → `"orders:alina99.store"`.
2. Hook trả `Ok=false` + tên máy ⇒ phiên KHÔNG chạy, câu log chứa tên máy đó.
3. Hook trả `Ok=false` + tên máy null ⇒ câu log dạng "máy khác".
4. Hook trả `Ok=true` ⇒ phiên chạy bình thường.
5. Hook `null` (chưa rót) ⇒ phiên chạy bình thường, không ném.
6. Nhả khóa gọi ĐÚNG một lần khi dừng; và **không** gọi nếu chưa từng giành được khóa.

### Bước 5 — Build & test

- `dotnet build ShopeeSuite.sln` + `dotnet build server/ShopeeHub.sln` → 0 error, 0 warning.
- `dotnet test` → 100% xanh (mốc hiện tại **1034 test**).

## 4. Tiêu chí nghiệm thu

- [ ] Build cả 2 solution 0 error/0 warning; `dotnet test` xanh, số test > 1034.
- [ ] Khóa dựng từ **login**, có tiền tố `orders:`, hạ chữ + trim — KHÔNG dùng `accountId`.
- [ ] Mất hub ⇒ `AcquireAccountLease` trả `Ok = true` (không chặn người dùng làm việc).
- [ ] Có heartbeat 60s cho các khóa đang giữ; tập rỗng thì timer dừng.
- [ ] Nhả khóa chạy cả trên đường lỗi/huỷ, và đúng một lần.
- [ ] KHÔNG đụng `AccountLeaseScope` / `ShopeeAccountUsage` / Scrape / Search (kiểm bằng `git diff --stat`).

## 5. Rủi ro & lưu ý

- **Nhả khóa sót là lỗi nặng nhất**: tài khoản sẽ bị coi là "đang chạy ở máy X" tới khi lease hết hạn (~5'), máy
  khác không chạy được. Ưu tiên chắc chắn nhả hơn là tối ưu.
- **Đừng khóa theo `accountId`** — Id cùng một tài khoản khác nhau giữa các máy, khóa sẽ vô tác dụng mà không ai
  biết.
- Một máy chạy nhiều tài khoản cùng lúc là bình thường → tập khóa phải là tập hợp, không phải một biến.
- Heartbeat phải chụp snapshot dưới `lock` rồi mới gọi mạng (đừng giữ lock qua `await`).
- Sau khi merge: **deploy hub KHÔNG bắt buộc** cho việc này (không thêm route), nhưng đợt release chung với việc
  số liệu thì vẫn deploy hub trước. Agent KHÔNG commit, KHÔNG deploy, KHÔNG bump version.

---

## Báo cáo thực thi (Opus điền sau khi xong)
