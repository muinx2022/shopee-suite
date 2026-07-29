# Plan: Kéo DANH BẠ sub-acc module Đơn hàng từ Hub về client mới (login + shop, KHÔNG kèm mật khẩu)

- **Ngày:** 2026-07-29
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Người dùng cài **thêm một client mới**, nối Hub, nhưng các **sub-acc của module Đơn hàng** (Shopee — bản ghi
`Account`: email/mật khẩu/cookie trong SQLite cục bộ) **không** tự về máy mới → phải thêm tay từng tài khoản.

**Đây là thiết kế cố ý hiện tại**, KHÔNG phải bug:

- Sub-acc Đơn hàng lưu **local từng máy** (`orders/XuLyDonShopee.Core/Data/AccountRepository.cs`, bảng SQLite
  `accounts`). Mỗi máy là nguồn sự thật cho danh sách của chính nó.
- Client chỉ đẩy **một chiều LÊN Hub một BẢN GƯƠNG** (`OrdersModuleHost.PushOrdersMirrorAsync` →
  `POST /orders/accounts`), payload **cố tình KHÔNG có mật khẩu/cookie** (xem `HubDatabase.OrdersAccounts.cs`
  dòng 36-37 và `OrdersAccountsPushRequest`).
- Hub lưu gương ở bảng `orders_accounts` + `orders_account_shops`, **keyed theo `machine_id`** (mỗi máy một
  danh bạ riêng). **Không có** endpoint `GET` cho client kéo credential về, và Hub **không hề giữ** mật khẩu.

**Quyết định người dùng đã chốt (qua AskQuestion):** làm kiểu **bán tự động** — Hub đẩy xuống **danh sách
sub-acc (login + shop)**, client tạo sẵn bản ghi tài khoản rỗng-mật-khẩu; người dùng **tự nhập lại mật khẩu /
đăng nhập** trên từng tài khoản. **KHÔNG** để Hub lưu hay truyền mật khẩu/cookie (giữ nguyên mô hình bảo mật
hiện tại: Hub không bao giờ chứa credential Đơn hàng).

**Mục tiêu cụ thể:** Trên máy mới, người dùng bấm **một nút** ở màn Tài khoản (module Đơn hàng) → app hỏi Hub
danh bạ sub-acc **gộp từ mọi máy** (distinct theo login, gộp shop), rồi **tạo mới trong SQLite cục bộ** các
login CHƯA có (mật khẩu để trống, trạng thái "Chưa kiểm tra", ghi chú "Kéo từ Hub — cần nhập mật khẩu"). Login
đã có ở máy → **giữ nguyên** (tuyệt đối không đè mật khẩu/cookie/ghi chú local). Sau đó người dùng mở từng tài
khoản, nhập mật khẩu (+ email xác minh nếu cần) rồi bấm Chạy như bình thường.

## 2. Phạm vi

- **Làm:**
  - **Hub:** thêm truy vấn gộp danh bạ distinct-theo-login (union shop) + endpoint `GET` mới trả về danh bạ đó
    (chỉ `login` + `shops`, KHÔNG mật khẩu/cookie — Hub vốn không có).
  - **DTO dùng chung** (`suite/Shopee.Core/Coordination/`): kiểu response cho danh bạ kéo về + route mới.
  - **Client `HubClient`:** method gọi `GET` danh bạ.
  - **Client module Đơn hàng:** hook (suite làm cầu nối, giống các hook khác trong `OrdersModuleHost`) để màn
    Tài khoản gọi được Hub; nút "Kéo tài khoản từ Hub" ở `AccountsView` + lệnh trong `AccountsViewModel` tạo
    bản ghi cục bộ cho login mới.
  - **Test** cho truy vấn gộp (Hub) và cho logic merge tạo mới không-đè (client, tách hàm thuần).
- **Không làm:**
  - KHÔNG cho Hub lưu/truyền **mật khẩu, cookie, email xác minh, mật khẩu email xác minh** của sub-acc Đơn hàng
    (giữ nguyên hợp đồng gương "KHÔNG credential").
  - KHÔNG đổi luồng đẩy gương LÊN Hub hiện có (`PushOrdersMirrorAsync`) — chỉ THÊM đường kéo XUỐNG.
  - KHÔNG tự động kéo khi khởi động/kết nối — CHỈ khi người dùng bấm nút (bán tự động, tránh bất ngờ).
  - KHÔNG đè/xóa tài khoản local đã có; KHÔNG đụng module Scrape/Search (`ShopeeAccount`) hay BigSeller.
  - KHÔNG đổi cơ chế lệnh hub → suất đơn hàng (run/stop) hiện có.

## 3. Các bước thực hiện

### A. Hub — truy vấn gộp danh bạ distinct-theo-login

1. **`server/Shopee.Hub.Web/Data/HubDatabase.OrdersAccounts.cs`** — thêm method:
   ```csharp
   public List<OrdersMirrorAccount> AllOrdersAccountsDistinct()
   ```
   - Gộp danh bạ của **MỌI máy** thành danh sách **distinct theo `login`** (so khớp `COLLATE NOCASE` /
     ignore-case — cùng quy ước khoá login đang dùng ở `OrdersAccountsOf`).
   - Với mỗi login: **union shop** từ mọi máy theo `shop_login` (distinct theo `shop_login` ignore-case; tên
     hiển thị lấy bản không rỗng đầu tiên). `SessionState`/`VerifyFailed`/`LastSyncAt` **không quan trọng cho
     mục đích kéo về** → có thể để mặc định (rỗng/false/null) hoặc lấy bản mới nhất; ĐƠN GIẢN: để rỗng, vì
     client chỉ cần login + shops.
   - Trả list rỗng khi bảng trống (không ném). Bọc trong `lock (_gate)` như các method khác cùng file.
   - Có thể tái dùng `OrdersMirrorAccount` record sẵn có (Login, SessionState, VerifyFailed, LastSyncAt,
     UpdatedAt, Shops) cho gọn — set các field không dùng về mặc định.

### B. DTO + route dùng chung

2. **`suite/Shopee.Core/Coordination/HubRoutes.cs`** — thêm hằng route (đặt cạnh `OrdersAccounts`):
   ```csharp
   /// <summary>GET: client MỚI kéo DANH BẠ sub-acc Đơn hàng gộp từ mọi máy (login + shop con; KHÔNG
   /// mật khẩu/cookie). Để tạo sẵn bản ghi tài khoản rỗng-mật-khẩu trên máy mới, người dùng tự nhập mật khẩu.</summary>
   public const string OrdersAccountsDirectory = "/orders/accounts/directory";
   ```
3. **`suite/Shopee.Core/Coordination/HubDtos.cs`** — thêm record response (đặt cạnh `OrdersAccountItem`):
   ```csharp
   /// <summary>Một sub-acc Đơn hàng trong DANH BẠ gộp toàn Hub (chỉ để tạo sẵn bản ghi trên máy mới).
   /// KHÔNG mang mật khẩu/cookie — Hub không hề giữ. Khoá là Login (email đăng nhập).</summary>
   public sealed record OrdersDirectoryAccount(string Login, List<OrdersShopItem> Shops);
   ```
   - Dùng lại `OrdersShopItem(string Login, string Name)` đã có.

### C. Hub endpoint

4. **`server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs`** — đăng ký GET (đặt cạnh `MapPost(HubRoutes.OrdersAccounts…)`
   dòng ~207):
   ```csharp
   // GET /orders/accounts/directory → DANH BẠ sub-acc Đơn hàng gộp từ MỌI máy (login + shop). KHÔNG mật khẩu/
   // cookie (Hub không giữ). Máy mới kéo về để tạo sẵn bản ghi tài khoản rỗng-mật-khẩu.
   api.MapGet(HubRoutes.OrdersAccountsDirectory, () =>
       Results.Json(db.AllOrdersAccountsDistinct()
           .Select(a => new OrdersDirectoryAccount(a.Login, a.Shops)).ToList()));
   ```

### D. Client — HubClient

5. **`suite/Shopee.Core/Coordination/HubClient.cs`** — thêm method (cạnh `PushOrdersAccountsAsync` dòng ~336):
   ```csharp
   /// <summary>Kéo DANH BẠ sub-acc Đơn hàng gộp từ mọi máy trên Hub (login + shop; KHÔNG mật khẩu/cookie).
   /// Hub cũ chưa có route / lỗi → trả null (caller phân biệt "không hỏi được" với "danh bạ rỗng").</summary>
   public async Task<List<OrdersDirectoryAccount>?> GetOrdersAccountsDirectoryAsync(CancellationToken ct = default)
   ```
   - Dùng đúng khuôn GET khoan dung của các method khác (bắt lỗi → null; 404 hub cũ → null). Tham khảo cách
     `GetPrepareStatsAsync` / `Orders()` xử lý null trong file này để đồng bộ phong cách.

### E. Client — cầu nối + UI màn Tài khoản

6. **`orders/XuLyDonShopee.App/Services/AppServices.cs`** — thêm một hook nullable (giống
   `QueryPrepareStats`, `PushOrdersToHub`…), ví dụ:
   ```csharp
   /// <summary>Hỏi Hub DANH BẠ sub-acc Đơn hàng (login + shop) để tạo sẵn bản ghi trên máy mới. null =
   /// Hub chưa kết nối / lỗi / hub cũ. Suite rót hook (module không tham chiếu Shopee.Core).</summary>
   public Func<CancellationToken, Task<IReadOnlyList<OrdersDirectoryItem>?>>? QueryOrdersDirectory { get; set; }
   ```
   - Vì module Đơn hàng KHÔNG tham chiếu `Shopee.Core`, **không dùng trực tiếp** `OrdersDirectoryAccount`.
     Định nghĩa một kiểu nhẹ trong module Đơn hàng (vd `record OrdersDirectoryItem(string Login,
     IReadOnlyList<(string Login, string Name)> Shops)` hoặc DTO tương đương) để hook trả về; suite map
     `OrdersDirectoryAccount` → kiểu này khi rót hook. (Theo đúng pattern các hook hiện có đều dùng kiểu
     nguyên thủy/kiểu của module, không lộ DTO Shopee.Core vào module.)
7. **`suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`** — thêm `WireOrdersDirectory(services)` (gọi
   trong `TryCreate` cạnh các `Wire...`), rót `services.QueryOrdersDirectory`:
   - Hub chưa kết nối (`CoordinationRuntime.Client is null`) → trả null.
   - Gọi `CoordinationRuntime.Client.GetOrdersAccountsDirectoryAsync(ct)`, map sang kiểu của module, trả về.
   - Nuốt lỗi (log `Trace`) trả null; hủy chủ động (ct) cho xuyên — đúng khuôn `WirePrepareStatsRead`.
8. **`orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`** — thêm:
   - Lệnh `[RelayCommand] private async Task KeoTuHubAsync()`:
     - `QueryOrdersDirectory` null → log "Hub chưa kết nối." rồi thôi.
     - Gọi hook; null → báo "Không kéo được danh bạ từ Hub (Hub offline / bản Hub cũ)."; rỗng → báo
       "Hub chưa có tài khoản nào."
     - Tính tập login đã có ở máy (từ `_all`/`_services.Accounts.GetAll()`, so khớp `Email`
       `OrdinalIgnoreCase`, đã Trim). Với mỗi login Hub trả về mà CHƯA có → `Insert` một `Account` mới:
       `Email = login`, `Password = ""`, `Status = ChuaKiemTra`, `Note = "Kéo từ Hub — cần nhập mật khẩu"`,
       các field khác để mặc định/null.
     - (Tùy chọn, nên làm) seed shop: nếu login mới có shops → ghi `account_shops` cho account vừa tạo qua
       `ResultsRepository.UpsertShops` (kiểm tra chữ ký hàm) để tab "Kết quả" hiện shop ngay; nếu phức tạp thì
       BỎ (shop sẽ tự đọc lại khi đăng nhập) — ghi rõ trong báo cáo nếu bỏ.
     - Sau khi thêm: `Reload()` + log/`BusyStatus` báo "Đã kéo N tài khoản mới từ Hub — hãy mở từng tài khoản
       nhập mật khẩu rồi bấm Chạy." (kể cả N=0: "Không có tài khoản mới (đã có đủ).")
   - **Tách hàm thuần để test:** `public static (List<string> ToAdd) TinhLoginCanThem(IEnumerable<string>
     hubLogins, IEnumerable<string> localEmails)` — distinct hub logins (ignore-case, bỏ rỗng), loại các login
     đã có local. Lệnh gọi hàm này rồi Insert.
9. **`orders/XuLyDonShopee.App/Views/AccountsView.axaml`** (+ code-behind nếu cần bind command) — thêm nút
   **"Kéo TK từ Hub"** ở khu thao tác danh sách (cạnh nút Thêm/Xóa). Bind `KeoTuHubCommand`. Giữ style nút
   đồng bộ các nút hiện có.

### F. Test

10. **Hub:** thêm test cho `AllOrdersAccountsDistinct()` (tạo file cạnh test HubDatabase hiện có, nếu có):
    - 2 máy cùng có login `a@x` với shop khác nhau → 1 dòng `a@x`, shops là UNION.
    - Login chỉ ở 1 máy → vẫn có.
    - Khác hoa/thường (`A@X` vs `a@x`) → gộp 1 dòng.
    - Bảng rỗng → list rỗng.
11. **Client:** test `AccountsViewModel.TinhLoginCanThem`:
    - Hub có {a,b,c}, local có {a} → ToAdd = {b,c}.
    - Trùng hoa/thường (hub `B`, local `b`) → không thêm.
    - Hub rỗng → rỗng; login rỗng/space bị loại.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` và `dotnet build server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh; test Hub (nếu có project test hub) xanh.
- [ ] `GET /orders/accounts/directory` trả JSON list `{login, shops:[{login,name}]}` gộp từ mọi máy, distinct
      theo login, KHÔNG có field password/cookie.
- [ ] Kịch bản tay: máy A đã có sub-acc `x@shop` (đang chạy, đã đẩy gương). Máy B (mới) bấm **"Kéo TK từ Hub"**
      → xuất hiện `x@shop` trong danh sách với **mật khẩu trống**, ghi chú "Kéo từ Hub — cần nhập mật khẩu";
      mở ra nhập mật khẩu + Chạy đăng nhập được. Bấm lần 2 → báo "không có tài khoản mới".
- [ ] Máy B đã có sẵn `x@shop` (mật khẩu người dùng nhập) → kéo lại **KHÔNG đè** mật khẩu/ghi chú local.
- [ ] Hub KHÔNG lưu và KHÔNG trả mật khẩu/cookie ở bất kỳ đường nào (đọc lại code endpoint + payload để chắc).

## 5. Rủi ro & lưu ý

- **Bảo mật — ràng buộc CỨNG:** endpoint mới CHỈ trả `login` + `shops`. Tuyệt đối không thêm password/cookie
  vào `OrdersDirectoryAccount` hay truy vấn — Hub vốn không có, đừng vô tình JOIN sang bảng khác.
- **Không đè dữ liệu local:** chỉ Insert login CHƯA có (so khớp `Email` ignore-case). Đây là chốt quan trọng
  nhất — làm sai là ghi đè mật khẩu người dùng đã nhập ⇒ hỏng đăng nhập hàng loạt. Test khoá ca này.
- **Mật khẩu trống + Save form:** `Save` của màn hiện yêu cầu mật khẩu không rỗng — ĐÚNG ý (ép người dùng nhập
  trước khi lưu tay). Bản ghi kéo về được Insert THẲNG qua repository (bỏ qua validate của Save), nên vẫn tạo
  được; khi người dùng mở ra Save sẽ bị buộc nhập mật khẩu. Không cần nới lỏng validate.
- **Gương đẩy ngược:** sau khi Insert, worker gương (`PushOrdersMirrorAsync`) sẽ đẩy các login mới này LÊN Hub
  (chỉ login, không mật khẩu) — vô hại, đúng hợp đồng.
- **Chạy được ở cả chế độ Shopee-thuần lẫn Full/Workspace:** cổng kiểm là `CoordinationRuntime.Client` (giống
  các hook đẩy đơn) nên máy chạy riêng module Đơn hàng vẫn kéo được.
- **Hub cũ chưa có route:** client bắt 404/lỗi → null → báo "bản Hub cũ / offline", KHÔNG crash. Đây là thay
  đổi **client + hub** ⇒ sau nghiệm thu cần **deploy Hub** rồi mới **release client** thì máy mới mới kéo được.
- **Danh bạ có thể lẫn máy đã ngừng dùng:** gộp mọi máy nên có thể kéo cả login của máy cũ không còn dùng.
  Chấp nhận (người dùng xem lại, xóa tay nếu thừa) — giữ đơn giản, không lọc theo `updated_at` ở bản này.

---

## Báo cáo thực thi (2026-07-29, làm trực tiếp trong Cursor)

Đã làm đúng plan:

- **Hub `HubDatabase.OrdersAccounts.cs`:** thêm `AllOrdersAccountsDistinct()` — gộp mọi máy, distinct login
  (ignore-case), union shop theo `shop_login` (tên lấy bản không rỗng đầu). Trả `OrdersMirrorAccount` với các
  field phiên để mặc định (chỉ login + shops có nghĩa).
- **DTO/route:** `HubRoutes.OrdersAccountsDirectory = "/orders/accounts/directory"`; record
  `OrdersDirectoryAccount(Login, Shops)` trong `HubDtos.cs`.
- **Hub endpoint:** `GET /orders/accounts/directory` trong `ClientApiEndpoints.cs` (KHÔNG mật khẩu/cookie).
- **Client `HubClient.GetOrdersAccountsDirectoryAsync`** — GET khoan dung, lỗi/hub cũ → null.
- **`AppServices`:** kiểu module `OrdersDirectoryItem` + hook `QueryOrdersDirectory`.
- **`OrdersModuleHost.WireOrdersDirectory`** — rót hook, map DTO hub → kiểu module; cổng kiểm `Client` (chạy cả
  chế độ Shopee-thuần lẫn Full/Workspace).
- **`AccountsViewModel`:** lệnh `KeoTuHubCommand` (Insert login CHƯA có, mật khẩu trống, ghi chú "Kéo từ Hub —
  cần nhập mật khẩu", seed shop best-effort) + hàm thuần `TinhLoginCanThem` (chốt không đè local).
- **`AccountsView.axaml`:** nút "Kéo TK từ Hub" (icon IconSync) cạnh Thêm/Xóa.
- **Test:** `KeoTuHubTests` (6 ca cho `TinhLoginCanThem`). KHÔNG có project test hub nên phần
  `AllOrdersAccountsDistinct` chưa có unit test — cần kiểm chứng tay/qua endpoint.

Nghiệm thu:
- `dotnet build ShopeeSuite.sln` → 0 warning, 0 error.
- `dotnet build server/Shopee.Hub.Web` → compile 0 warning/0 error (build vào thư mục tạm; build vào `bin`
  thường bị KHÓA FILE do có instance hub đang chạy local — không phải lỗi biên dịch).
- `dotnet test orders/XuLyDonShopee.Tests` → 1440 passed, 0 failed.

Còn lại (ngoài phạm vi code): cần **deploy Hub** rồi **release client** thì máy mới mới kéo được (thay đổi cả
hai phía). Kịch bản tay 2 máy chưa chạy (cần môi trường thật) — đã có test cho nhánh logic then chốt.
