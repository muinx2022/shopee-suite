# Plan: Affinity tài khoản↔máy cho Scrape (giữ trusted-device, hết lặp profile đa máy)

- **Ngày:** 2026-07-25
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

**Vấn đề (đã chẩn đoán từ code):** Kho tk Shopee dùng CHUNG toàn fleet (mọi máy sync cùng danh sách từ Hub). Khi bắt đầu scrape, [`ScrapeViewModel.ClaimFrame`](../suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs#L769-L797) lấp đủ N tk bằng cách **bốc NGẪU NHIÊN** trong kho (chỉ *resume* mới giữ khung cũ qua `preferIds`, [dòng 447](../suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs#L447)). Lease xuyên máy ([`HubDatabase.ReserveAccounts`](../server/Shopee.Hub.Web/Data/HubDatabase.Leases.cs#L118-L149)) CHỈ chống dùng ĐỒNG THỜI (chặn khi lease tươi <5'), và [`ReleaseAccounts` xóa hẳn dòng](../server/Shopee.Hub.Web/Data/HubDatabase.Leases.cs#L151-L164) khi xong → không lưu "tk từng chạy ở máy nào". Profile + trusted-device là **cục bộ** (`persistent-data/profiles/{Id}` trên từng máy, KHÔNG sync).

Hậu quả: Client A dựng profile tin cậy cho tk3 và chạy tốt; sau đó Client B bốc trúng tk3 → tạo profile MỚI trên B → **mất trusted-device** → captcha/login lại (và 1 tk lộ trên 2 thiết bị).

**Quyết định người dùng đã chốt (qua hỏi đáp):** làm **Affinity tự động** — Hub ghi "nhà" (home machine) của mỗi tk khi 1 máy chạy nó; khi dựng khung, mỗi máy **ƯU TIÊN tk của mình** (đã có profile) và **TRÁNH tk đang thuộc máy khác còn online**; máy nhà offline lâu (mặc định 45') thì tk mới cho máy khác **tiếp quản** (dựng trust lại 1 lần rồi dính máy mới).

**Nguyên tắc thiết kế:**
- "Home binding" (ràng buộc, máy khác phải tránh) dựa vào **nhịp sống của MÁY nhà** (`machines.last_seen`), KHÔNG cần heartbeat riêng cho từng tk. Máy nhà còn online (last_seen trong ngưỡng `HomeTakeoverAfter`) ⇒ home ràng buộc; quá ngưỡng ⇒ tk tự do, ai dùng thì re-home.
- Affinity là lớp **cố vấn** để chọn khung; **lease xuyên máy vẫn là khóa loại trừ thật** (giữ nguyên). Chỉ SetAccountHome cho các tk đã được lease cấp (nên không đụng tk máy khác đang giữ).
- Nếu chạy **không có Hub** (1 máy, `accHub == null`): BỎ QUA affinity, giữ hành vi cũ y nguyên.

Mục tiêu: hết cảnh 2 máy tranh nhau 1 tk qua các lượt chạy rời; mỗi tk ổn định ở 1 máy → tái dùng profile bền, ít captcha/login lại.

## 2. Phạm vi

- **Làm:**
  1. Hub: bảng `account_home` (bền, KHÔNG xóa khi nhả lease) + method set/query + dọn khi reset/xóa máy + ngưỡng tiếp quản cấu hình được.
  2. Hub API + Core contracts: nhân bản dây nối kiểu `AccountsReserve` cho 2 endpoint `POST /accounts/home` (ghi home) và `GET /accounts/home` (đọc affinity + cờ binding).
  3. Client Scrape: trước khi dựng khung → hỏi Hub affinity (mine/blocked); `ClaimFrame` ưu tiên `mine`, loại `blocked`; sau khi lease cấp khung → ghi home = máy này.
- **Không làm:**
  - KHÔNG đụng module **Search** (per-account borrow) trong plan này — để plan sau (dữ liệu Hub thiết kế module-agnostic để Search dùng lại được). Ghi chú rủi ro bên dưới.
  - KHÔNG sync profile/trusted-device giữa các máy (mong manh; đã loại ở bước hỏi đáp).
  - KHÔNG đổi cơ chế lease xuyên máy hiện có (`ReserveAccounts`/`ReleaseAccounts`/`HeartbeatAccounts`) — chỉ THÊM lớp home song song.
  - KHÔNG đổi luồng captcha→xóa profile (feature khác). Tk dính captcha vẫn GIỮ home (lần sau cùng máy tự login lại) — không cần xử lý gì thêm.

## 3. Các bước thực hiện

> Đường dẫn tương đối từ gốc repo. Mẫu để nhân bản: đường `AccountsReserve` (client→API→DB). Kiểm lại số dòng vì có thể lệch.

### Bước 1 — Hub DB: bảng `account_home` + method + dọn + cấu hình

- **Bảng** (thêm vào chỗ khởi tạo schema trong `server/Shopee.Hub.Web/Data/HubDatabase.cs`, cạnh nơi tạo `account_leases`):
  ```sql
  CREATE TABLE IF NOT EXISTS account_home(
    account_id TEXT PRIMARY KEY,
    machine_id TEXT NOT NULL,
    hostname   TEXT,
    updated_at TEXT NOT NULL
  );
  ```
- **Cấu hình** (thêm property vào `HubDatabase.cs` cạnh `StaleAccount`):
  ```csharp
  /// <summary>Máy "nhà" offline quá ngưỡng này → home hết ràng buộc, tk cho máy khác tiếp quản. Chỉnh tại đây.</summary>
  public TimeSpan HomeTakeoverAfter { get; init; } = TimeSpan.FromMinutes(45);
  ```
- **Method** (đặt trong `server/Shopee.Hub.Web/Data/HubDatabase.Leases.cs` — cùng file account-lease, hoặc partial mới `HubDatabase.AccountHome.cs`):
  - `public void SetAccountHome(SetAccountHomeRequest r)` — với mỗi id (Distinct, Ordinal):
    ```sql
    INSERT INTO account_home(account_id,machine_id,hostname,updated_at)
    VALUES($id,$m,$h,$t)
    ON CONFLICT(account_id) DO UPDATE SET machine_id=$m, hostname=$h, updated_at=$t;
    ```
    (Chỉ gọi cho tk đã được lease cấp cho máy này ⇒ ghi đè an toàn: tk mồ côi → thành của mình; tk nhà-offline → tiếp quản; tk của mình → refresh.)
  - `public List<AccountHomeItem> AccountHomes()` — đọc `machines(machine_id,last_seen)` vào dict, rồi đọc `account_home`; mỗi dòng tính `Binding = seen.TryGetValue(mid, out var ls) && (now - ls) < HomeTakeoverAfter`. Trả `AccountHomeItem(AccountId, MachineId, Hostname, Binding)`. Bọc `lock(_gate)`, dùng helper `S`/`D` sẵn có.
- **Dọn theo máy** (để máy chết/ngắt nhả home ngay):
  - Trong `ResetMachineWork` ([HubDatabase.Machines.cs:93](../server/Shopee.Hub.Web/Data/HubDatabase.Machines.cs#L93)): thêm `"account_home"` vào mảng `foreach (var tbl in new[] { "leases", "account_leases" })` → thành `{ "leases", "account_leases", "account_home" }` (vẫn trong transaction).
  - Trong `DeleteMachineLocked` ([HubDatabase.Machines.cs:215](../server/Shopee.Hub.Web/Data/HubDatabase.Machines.cs#L215)): thêm `DELETE FROM account_home WHERE machine_id=$m;` vào câu lệnh gộp (khi máy chủ động "Ngắt kết nối").

### Bước 2 — Hub API: routes + endpoints

- `server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs`, cạnh 3 dòng account-lease ([dòng 62-64](../server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs#L62-L64)), thêm:
  ```csharp
  api.MapPost(HubRoutes.AccountsHome, (SetAccountHomeRequest? r) =>
  {
      if (r?.AccountIds is null) return Results.BadRequest();
      db.SetAccountHome(r);
      return Results.Ok();
  });
  api.MapGet(HubRoutes.AccountsHome, () => Results.Json(db.AccountHomes()));
  ```

### Bước 3 — Core contracts (client)

- `suite/Shopee.Core/Coordination/HubRoutes.cs` (cạnh [dòng 26-30](../suite/Shopee.Core/Coordination/HubRoutes.cs#L26-L30)):
  ```csharp
  public const string AccountsHome = "/accounts/home";
  ```
- `suite/Shopee.Core/Coordination/HubDtos.cs` (cạnh [dòng 212-214](../suite/Shopee.Core/Coordination/HubDtos.cs#L212-L214)):
  ```csharp
  public sealed record SetAccountHomeRequest(List<string> AccountIds, string MachineId, string Hostname);
  public sealed record AccountHomeItem(string AccountId, string MachineId, string Hostname, bool Binding);
  ```
- `suite/Shopee.Core/Coordination/HubClient.cs` (cạnh [dòng 61-68](../suite/Shopee.Core/Coordination/HubClient.cs#L61-L68)):
  ```csharp
  public Task SetAccountHomeAsync(SetAccountHomeRequest req, CancellationToken ct = default) => PostAsync(HubRoutes.AccountsHome, req, ct);
  public async Task<List<AccountHomeItem>> GetAccountHomesAsync(CancellationToken ct = default)
      => await _http.GetFromJsonAsync<List<AccountHomeItem>>(HubRoutes.AccountsHome, ct) ?? [];
  ```
- `suite/Shopee.Core/Coordination/ICoordinationHub.cs`: thêm
  ```csharp
  Task SetAccountHomeAsync(IEnumerable<string> ids);
  /// <summary>Trả (tk homed về máy NÀY, tk homed máy khác CÒN binding). Rỗng nếu offline/lỗi.</summary>
  Task<(HashSet<string> Mine, HashSet<string> Blocked)> GetAccountAffinityAsync();
  ```
- `suite/Shopee.Core/Coordination/HttpCoordinationHub.cs` (cạnh `ReserveAccountsAsync` [dòng 210-233](../suite/Shopee.Core/Coordination/HttpCoordinationHub.cs#L210-L233)):
  ```csharp
  public async Task SetAccountHomeAsync(IEnumerable<string> ids)
  {
      var list = ids.Distinct(StringComparer.Ordinal).ToList();
      if (list.Count == 0) return;
      try { await _client.SetAccountHomeAsync(new SetAccountHomeRequest(list, _machineId, Host)); } catch { }
  }
  public async Task<(HashSet<string> Mine, HashSet<string> Blocked)> GetAccountAffinityAsync()
  {
      var mine = new HashSet<string>(StringComparer.Ordinal);
      var blocked = new HashSet<string>(StringComparer.Ordinal);
      try
      {
          var homes = await _client.GetAccountHomesAsync();
          foreach (var h in homes)
          {
              if (string.Equals(h.MachineId, _machineId, StringComparison.Ordinal)) mine.Add(h.AccountId);
              else if (h.Binding) blocked.Add(h.AccountId);
          }
      }
      catch { }   // Hub lỗi → degrade: không mine/blocked → hành vi như cũ
      return (mine, blocked);
  }
  ```
  Nếu có bản `NullCoordinationHub`/no-op implement `ICoordinationHub`, thêm 2 method trả rỗng để build xanh.

### Bước 4 — Client Scrape: dùng affinity khi dựng khung + ghi home

Trong `suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs`, hàm `RunOneJobAsync` quanh chỗ dựng khung ([dòng 443-467](../suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs#L443-L467)):

1. **Lấy affinity TRƯỚC `ClaimFrame`** (chỉ khi có Hub):
   ```csharp
   HashSet<string> mineIds = new(StringComparer.Ordinal), blockedIds = new(StringComparer.Ordinal);
   if (accHub is not null)
       (mineIds, blockedIds) = await accHub.GetAccountAffinityAsync().ConfigureAwait(false);
   ```
2. **Đổi chữ ký + logic `ClaimFrame`** thành `ClaimFrame(int n, IReadOnlyList<string>? preferIds, IReadOnlyCollection<string> mineIds, IReadOnlyCollection<string> blockedIds)`:
   - Luôn **loại `blockedIds`** khỏi cả vòng prefer lẫn vòng lấp ngẫu nhiên (thêm điều kiện `&& !blockedIds.Contains(x.Id)`).
   - Thứ tự ưu tiên: (a) `preferIds` (resume — giữ khung cũ) → (b) `mineIds` (tk máy này đã "nhà", tái dùng profile) → (c) lấp ngẫu nhiên phần còn lại (tk mồ côi / nhà-offline). Cụ thể: sau vòng `preferIds` hiện có, thêm 1 vòng y hệt lặp qua `mineIds` (cùng điều kiện `!Disabled && !IsHubLeased && TryReserve`, `Available.Remove`), rồi mới tới vòng `while (frame.Count < n)` ngẫu nhiên (đã loại blocked).
   - Cập nhật lời gọi tại [dòng 448](../suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs#L448): `s.ClaimFrame(frameSize, preferIds, mineIds, blockedIds)`.
3. **Ghi home sau khi lease cấp khung**: ngay sau khối `ReserveHubAsync` ([dòng 457-466](../suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs#L457-L466)), khi đã có `frame` cuối (các tk máy này thực sự giữ), thêm:
   ```csharp
   if (accHub is not null && frame.Count > 0)
       await accHub.SetAccountHomeAsync(frame.Select(a => a.Id)).ConfigureAwait(false);
   ```
   (Chỉ ghi cho tk đã qua lease-grant ⇒ không ghi đè nhầm tk máy khác đang giữ.)
4. **Log rõ** khi affinity thu hẹp khung: nếu vì `blockedIds` mà khung < `frameSize`, log kiểu `"… X tk đang thuộc máy khác (còn online) → nhường, dùng tk của máy này/mồ côi; khung còn {frame.Count}."` để user hiểu vì sao ít cửa sổ hơn.

### Bước 5 — Build + test

- Build cả solution: `dotnet build ShopeeSuite.sln` — phải **0 error**, không warning mới đáng kể.
- Nếu có project test cho Hub (vd `*.Tests` chứa test lease): thêm test cho `account_home`:
  - `SetAccountHome` upsert đúng (ghi rồi ghi đè machine khác → dòng cập nhật).
  - `AccountHomes().Binding` = true khi máy nhà `last_seen` mới; = false khi quá `HomeTakeoverAfter` (test dựng `HubDatabase` với `HomeTakeoverAfter` nhỏ + chèn machines.last_seen cũ).
  - `ResetMachineWork`/`RemoveMachine` xóa sạch `account_home` của máy đó.
- Không có test project phù hợp → build xanh + tự rà logic là đủ; ghi rõ trong báo cáo.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` thành công (0 error).
- [ ] Đọc code xác nhận: bảng `account_home` được TẠO ở schema init; `SetAccountHome` upsert theo `account_id`; `AccountHomes` tính `Binding` theo `machines.last_seen` vs `HomeTakeoverAfter`.
- [ ] `ResetMachineWork` và `DeleteMachineLocked` đều xóa `account_home` của máy (grep thấy `account_home` trong cả 2).
- [ ] 2 endpoint `POST`/`GET /accounts/home` map đúng; DTO + `HubRoutes.AccountsHome` + 2 method `HubClient` + `ICoordinationHub` + `HttpCoordinationHub` khớp nhau (build chứng minh).
- [ ] `ClaimFrame` mới: LOẠI `blockedIds`, ưu tiên `preferIds` → `mineIds` → ngẫu nhiên; lời gọi tại RunOneJobAsync truyền đủ 4 tham số.
- [ ] `GetAccountAffinityAsync` gọi TRƯỚC ClaimFrame; `SetAccountHomeAsync(frame ids)` gọi SAU `ReserveHubAsync`. Khi `accHub == null` (không Hub): KHÔNG gọi affinity, hành vi cũ y nguyên (mine/blocked rỗng).
- [ ] Hub lỗi/timeout khi GetAccountAffinity → nuốt lỗi, trả rỗng → không chặn scrape (degrade êm).
- [ ] (Nếu có test project) test account_home xanh.

## 5. Rủi ro & lưu ý

- **Ngưỡng `HomeTakeoverAfter` (mặc định 45'):** đây là điểm đánh đổi throughput↔trust. Để 1 hằng số dễ chỉnh trên `HubDatabase`. Fable sẽ hỏi lại user nếu muốn giá trị khác (vd giữ affinity qua đêm → đặt lớn hơn). KHÔNG hardcode rải rác.
- **Máy online-nhưng-idle vẫn giữ home:** binding theo máy online, không theo "đang chạy". Nếu máy A online mà không chạy, tk của A vẫn bị B tránh → B có thể được khung nhỏ/rỗng nếu mọi tk đều của A. Đúng theo affinity; **phải log rõ** (Bước 4.4) để user không tưởng lỗi. Không tự nới ngưỡng để "chữa".
- **Race GET-affinity ↔ SET-home:** lành tính — lease xuyên máy (`ReserveHubAsync`) mới là khóa loại trừ thật; SetAccountHome chỉ ghi cho tk đã lease-grant. Không cần lock chéo.
- **Search chưa theo affinity:** trong plan này Search vẫn bốc tk tự do → có thể cướp tk scrape-trusted của máy khác. Dữ liệu Hub (`account_home`) để module-agnostic; mở plan sau cho Search dùng `GetAccountAffinity`/`SetAccountHome` tương tự. Ghi rõ hạn chế này cho user.
- **Trạng thái ban đầu:** chưa có dòng `account_home` → mọi tk mồ côi → vài lượt đầu bốc + tự home → ổn định dần. Không cần migration dữ liệu.
- **Dòng home mồ côi khi xóa tk khỏi kho:** vô hại (không khớp candidate). Không cần dọn trong plan này.
- **Đừng thu hẹp `_targetSize`/cơ chế bù tk:** khung nhỏ do affinity vẫn hợp lệ; pool bù tk (`AcquireReplacementAsync`) giữ nguyên — nhưng lưu ý tk bù cũng nên tránh `blocked`. Nếu dễ, cho `AccountReplenisher`/`TryAcquireSpareAsync` loại `blockedIds` (truyền xuống); nếu phức tạp, để nguyên và ghi chú (bù hiếm khi xảy ra, lease vẫn chặn dùng-trùng). **Không** mở rộng ngoài ghi chú nếu tốn công.

---

## Báo cáo thực thi

Hoàn tất 5 bước. Opus triển khai, Fable review diff thật + build lại độc lập nghiệm thu.

**File tạo/sửa (9):** `HubDatabase.cs` (property `HomeTakeoverAfter`=45' + bảng `account_home` trong EnsureSchema), `HubDatabase.AccountHome.cs` (MỚI: `SetAccountHome` upsert + `AccountHomes` tính `Binding` theo `machines.last_seen`), `HubDatabase.Machines.cs` (dọn `account_home` trong `ResetMachineWork` + `DeleteMachineLocked`), `ClientApiEndpoints.cs` (POST/GET `/accounts/home`), `HubRoutes.cs` + `HubDtos.cs` + `HubClient.cs` (route + DTO + client method), `HttpCoordinationHub.cs` (`SetAccountHomeAsync` + `GetAccountAffinityAsync` → (Mine,Blocked), nuốt lỗi), `ScrapeViewModel.cs` (fetch affinity TRƯỚC ClaimFrame; `ClaimFrame(n,preferIds,mineIds,blockedIds)` loại blocked + ưu tiên preferIds→mineIds→ngẫu nhiên; ghi home SAU ReserveHubAsync; log khi thu hẹp khung).

**Nghiệm thu (Fable):** `dotnet build ShopeeSuite.sln` → 0 warning/0 error (build lại độc lập). `dotnet test orders/XuLyDonShopee.Tests` → 899 pass, không hồi quy. Review diff: logic binding/upsert/dọn-máy/ClaimFrame đúng; degrade êm khi offline/không-Hub.

**Lệch plan (chấp nhận):**
1. KHÔNG thêm vào `ICoordinationHub` — `accHub` là concrete `HttpCoordinationHub?`, toàn bộ mẫu `AccountsReserve` cũng chỉ nằm trên concrete; thêm interface sẽ lệch mẫu + buộc NoOp implement thừa. Đúng tinh thần "bám mẫu AccountsReserve".
2. Không có Hub test project (chỉ orders tests) → build xanh + rà logic tay (đúng plan mục 5).

**Còn ngỏ (ghi chú, chưa làm — đúng phạm vi):** đường bù tk (`AccountReplenisher.TryAcquireSpareAsync`) CHƯA loại `blocked`; module **Search** chưa theo affinity. Dữ liệu `account_home` đã module-agnostic, mở plan sau nếu cần.
