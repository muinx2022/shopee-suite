# Plan: Đồng bộ cấu hình GSheet (URL + Tab) giữa Hub và client + hết "hỏng im lặng"

- **Ngày:** 2026-07-26
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — CÂY CHÍNH

## 1. Bối cảnh & mục tiêu

**Sự cố thật:** máy client `Hoang` chạy 12 tiếng, chuẩn bị hàng xong, Hub đẩy OK nhưng **không ghi Google Sheet
dòng nào** — vì máy đó **chưa điền URL Web App**. Code coi URL trống = "người dùng không dùng GSheet" và bỏ qua
**hoàn toàn im lặng** (không log) → không ai biết đang mất dữ liệu.

**Mục tiêu:**
1. **Đồng bộ cấu hình GSheet dùng chung** (URL Web App + Tab override) giữa Hub và mọi client — sửa được từ CẢ
   client LẪN hub; client chưa có thì tự nhận về.
2. **Hết "hỏng im lặng"**: log rõ khi bỏ qua ghi sheet do chưa cấu hình, và khi đẩy hub không thành.

**QUYẾT ĐỊNH ĐÃ CHỐT VỚI NGƯỜI DÙNG:**
- **Hub luôn thắng** (hub là nguồn sự thật) — NHƯNG xem bất biến #1 bên dưới (hub RỖNG thì KHÔNG đè).
- Đồng bộ **URL + Tab**. Cấu hình riêng-máy (thư mục phiếu/video/ảnh, trình duyệt) **TUYỆT ĐỐI không đụng**.
- **Không cần khởi động lại app** — client nhận nóng (đã xác nhận `AccountSession` đọc lại setting từ SQLite ở
  mỗi lượt đẩy nên chỉ cần ghi vào SQLite là có hiệu lực ngay từ shop kế tiếp).

## 2. Hiện trạng (đã khảo sát kỹ — bám theo, đừng dò lại)

**Cấu hình GSheet nằm ở CSDL local của module Đơn hàng** (`%APPDATA%\XuLyDonShopee\app.db`, bảng
`settings(key,value)` — KHÔNG có cột thời gian):
- `orders/XuLyDonShopee.Core/Data/SettingsRepository.cs`: key `gsheet_webapp_url` (:25) + `gsheet_tab_name` (:29);
  `GetGsheetWebAppUrl()` :91 (trống → **null = TẮT đồng bộ**), `GetGsheetTabName()` :106 (trống → **"" = TỰ ĐỘNG
  theo tháng**), `Get`/`Set` :172/:183 đọc-ghi thẳng SQLite (không cache).
- UI sửa: `orders/XuLyDonShopee.App/Views/SettingsView.axaml:128-171` (card "ĐỒNG BỘ GOOGLE SHEET"),
  VM `orders/XuLyDonShopee.App/ViewModels/SettingsViewModel.cs` — `Reload()` :91, `SaveGsheetUrl()` :155
  (validate `https://script.google.com/`, ghi 2 key, có ô `GsheetSavedMessage` :171).
- Nơi tiêu thụ: `orders/XuLyDonShopee.App/Services/AccountSession.cs:814` (đọc URL) và `:848-849` (đọc tab
  override + `GsheetTabName.ForMonth(now)`).

**Precedent gần nhất — `ai.json` hub-owned (BẮT CHƯỚC CÁI NÀY):**
- Hub lưu file-store: `server/Shopee.Hub.Web/Services/FileStoreConfigService.cs:21` `AiFile = "config/ai.json"`,
  đọc `Ai()` :62-68, ghi `Save(name, value, ifMatch)` :50-55 (UTF-8 **không BOM**, **PascalCase**, có `version`
  + `If-Match` chống race → `"version-conflict"`).
- Trang hub: `server/Shopee.Hub.Web/Components/Pages/ConfigAi.razor` (`@page "/config/ai"`), lưu qua
  `Services/ConfigSave.cs:9-15` (`ConfigSave.Apply(...)`). Nav: `Components/Layout/MainLayout.razor:33` + tiêu đề `:107`.
- Client đọc: `suite/Shopee.Core/Ai/HubAiConfig.cs` — **TTL 60s** (:14), **backoff 30s** khi lỗi (:17),
  **FetchTimeout 10s** (:20, vì `DownloadAsync` dùng `_bulkHttp` timeout 5'), offline → trả cache (:44).

**Cầu nối suite ↔ orders (BẮT BUỘC đi đường này):** module Đơn hàng **KHÔNG tham chiếu `Shopee.Core`/hub** —
mọi thứ liên quan hub phải qua hook `Func<...>` rót từ `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`
(`TryCreate()` :33-49 gọi `WireHubPush` :60, `WireIncrementSoldBySku` :116, `WireHubSlipPush` :151 — mỗi hook đều
guard `if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null) return <giá trị tắt>;` và nuốt lỗi
về `Trace`). Hook khai ở `orders/XuLyDonShopee.App/Services/AppServices.cs:43/:53/:63` (mặc định `null` = TẮT).

**Hub-web đã LINK code của module Đơn hàng** (`server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` link
`OrderNotifyService.cs`, `SyncedOrder.cs`) → được phép LINK thêm file thuần BCL nếu cần.

**Route:** `suite/Shopee.Core/Coordination/HubRoutes.cs` — chưa có route nào cho cấu hình module Đơn hàng.
`ClientApiEndpoints.cs:43-47`: client PUT `/files/config/*` bị **403** khi `AllowClientConfigPush=false` → phải
dùng route riêng NGOÀI tiền tố `config/`.

## 3. Phạm vi

- **Làm:** đồng bộ 2 setting GSheet (URL + Tab) hai chiều theo luật "hub thắng khi hub có giá trị"; trang cấu
  hình trên Hub; nhận nóng ở client; 2 dòng log chống "hỏng im lặng".
- **KHÔNG làm:** không đụng cấu hình riêng-máy; không đổi luồng sync đơn/đẩy hub hiện có; không đụng redesign
  giao diện đang dở (GĐ4); không đổi hành vi khi app Đơn hàng chạy ĐỘC LẬP (hook null → y như cũ).

## 4. BẤT BIẾN (làm sai là hỏng cả fleet — đọc kỹ)

1. **Hub RỖNG thì KHÔNG được đè client.** URL trống = *công tắc TẮT* đồng bộ GSheet. Nếu hub chưa ai điền mà
   client vẫn kéo-đè thì **cả fleet tắt ghi sheet trong 3 phút** mà không ai hay. ⇒ Chỉ áp bản hub khi
   **`GsheetWebAppUrl` của hub NON-EMPTY**.
2. **Xử lý gọn bài toán "tab rỗng ≠ chưa cấu hình":** coi khối GSheet là **một đơn vị**. Hub có URL non-empty
   ⇒ hub ĐÃ cấu hình ⇒ áp **CẢ HAI** field (tab rỗng lúc này mang đúng nghĩa "TỰ ĐỘNG theo tháng"). Hub URL rỗng
   ⇒ bỏ qua cả hai. **KHÔNG** dùng sentinel kiểu `"(auto)"`.
3. **Client rỗng thì KHÔNG push đè hub** (tránh 1 máy chưa cấu hình xoá mất cấu hình của cả fleet).
4. **Máy HUB không tự pull đè chính nó** — giữ guard `HubServerConfigStore.Shared.Current.Enabled` như
   `HttpCoordinationHub.cs:119` / `HubConfigSync.cs:151`.
5. **JSON: PascalCase + UTF-8 KHÔNG BOM** (hub ghi), client đọc `NoBom` + `PropertyNameCaseInsensitive` —
   đã từng gây bug "kéo về = 0". Xem `FileStoreConfigService.cs:12-15,29-31` và `HubAiConfig.NoBom` :68.
6. **Bọc timeout riêng khi tải** (`CancellationTokenSource.CancelAfter(10s)`) vì `DownloadAsync` timeout 5'.
7. App Đơn hàng chạy độc lập: hook `null` → mọi thứ y như cũ, KHÔNG gọi HTTP blocking trong `Reload()` (UI thread).

## 5. Các bước thực hiện

### Bước 1 — Model dùng chung
Tạo `suite/Shopee.Core/Coordination/OrdersSharedConfig.cs`:
```csharp
public sealed class OrdersSharedConfig
{
    public string? GsheetWebAppUrl { get; set; }
    public string? GsheetTabName { get; set; }   // "" = tự động theo tháng
}
```
LINK vào `server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` (theo mẫu link `OrderNotifyService.cs` sẵn có).

### Bước 2 — Hub: store + trang cấu hình
- `FileStoreConfigService`: thêm `public const string OrdersFile = "config/orders.json";` + `Orders()` (copy y `Ai()` :62-68).
- Trang `server/Shopee.Hub.Web/Components/Pages/ConfigOrders.razor` (`@page "/config/orders"`) — copy khuôn
  `ConfigAi.razor`: 2 ô nhập (URL Web App, Tab override) + nút Lưu qua `ConfigSave.Apply(...)`, hiển thị version.
  - Validate URL: bắt đầu `https://script.google.com/` (dùng CÙNG luật với client — nếu tiện thì tách hàm
    validate thuần trong `XuLyDonShopee.Core` rồi LINK, theo mẫu `OrderNotifyService.KiemTraUrl`).
  - Ghi chú UI (BẮT BUỘC, tránh bị báo là bug): "Tab để trống = tự động `Tháng MM-yyyy`" và "Đổi tab chỉ áp cho
    đơn CHƯA ghi sheet — đơn cũ vẫn về tab đã ghi lần đầu."
- Nav + tiêu đề: `Components/Layout/MainLayout.razor` (thêm NavLink ~:33 và title ~:107).

### Bước 3 — Route cho client (đọc + ghi ngược)
- `HubRoutes.cs`: `public const string OrdersConfig = "/orders-config";` (ĐẶT NGOÀI tiền tố `config/` để không
  dính chặn `AllowClientConfigPush`).
- `ClientApiEndpoints.cs`: `GET` trả `OrdersSharedConfig` (từ `FileStoreConfigService.Orders()`);
  `POST` nhận `OrdersSharedConfig` → **chỉ ghi field non-empty, KHÔNG xoá field khác**; retry khi
  `"version-conflict"` (tối đa 3 lần, đọc lại rồi ghi lại).
- `HubClient.cs`: thêm `GetOrdersConfigAsync(ct)` + `PostOrdersConfigAsync(cfg, ct)` (theo mẫu các method sẵn có).

### Bước 4 — Client: lớp đọc có TTL
Tạo `suite/Shopee.Core/Coordination/HubOrdersConfig.cs` — **copy khuôn `Ai/HubAiConfig.cs`**: TTL 60s, backoff
30s khi lỗi, FetchTimeout 10s, offline/lỗi → trả `null` (nghĩa là "không biết" → caller KHÔNG đụng local).

### Bước 5 — Áp về module Đơn hàng (nhận nóng, không restart)
Trong `OrdersModuleHost`:
- Thêm `WireGsheetConfig(Services)` gọi trong `TryCreate()` (cạnh 3 hook cũ), gồm:
  - **Kéo & áp**: hàm `ApplyFromHubAsync()` — gọi `HubOrdersConfig.GetAsync()`; nếu trả về non-null **và**
    `GsheetWebAppUrl` non-empty **và** khác giá trị local → ghi `Services.Settings.SetGsheetWebAppUrl(...)` +
    `SetGsheetTabName(...)` (áp cả 2 theo bất biến #2). Guard máy Hub (bất biến #4). Nuốt lỗi → `Trace`.
  - **Gọi lúc nào:** (a) ngay sau `TryCreate()` (client vừa mở app đã có cấu hình); (b) định kỳ — đăng ký vào
    event fleet đã có (`Coordination.Hub.Changed`, nhịp 12s) và để TTL 60s của `HubOrdersConfig` tự chặn gọi HTTP
    quá dày. ⇒ **Client nhận cấu hình mới trong ~1 phút, KHÔNG cần khởi động lại.**
- Rót hook đẩy ngược vào `AppServices` (khai thêm ở `AppServices.cs` cạnh :63):
  `public Func<string?, string?, CancellationToken, Task<bool>>? PushGsheetConfigToHub { get; set; }`
  → `OrdersModuleHost` cài đặt bằng `HubClient.PostOrdersConfigAsync` (guard `CoordinationRuntime` như 3 hook cũ).

### Bước 6 — Client UI: lưu là đẩy lên Hub
`orders/XuLyDonShopee.App/ViewModels/SettingsViewModel.cs`:
- Trong `SaveGsheetUrl()` (:155) sau khi ghi 2 key local → gọi `PushGsheetConfigToHub` (nếu hook non-null **và**
  URL local non-empty — bất biến #3). Cập nhật `GsheetSavedMessage` cho biết đã đẩy lên Hub hay chưa
  (vd "✔ Đã lưu + đồng bộ lên Hub" / "✔ Đã lưu (Hub chưa kết nối — sẽ dùng bản của Hub khi có)").
- KHÔNG chặn UI: gọi async, không `.Result`/`.Wait()`.

### Bước 7 — Hết "hỏng im lặng" (`orders/XuLyDonShopee.App/Services/AccountSession.cs`)
- Trong `PushOrdersToGsheetAsync`, nhánh `string.IsNullOrWhiteSpace(url)` (:822): thêm **1 dòng log**, vd
  `log($"GSheet: chưa cấu hình Web App URL — bỏ qua ghi sheet ({pending.Count} đơn chờ).")`.
  **Chống spam:** chỉ log 1 lần mỗi phiên (cờ bool trong AccountSession) — mỗi shop 1 dòng sẽ rất ồn.
- Trong `PushOrdersToHubAsync` (:564): hiện chỉ log khi `marked > 0` → hub chết là im re. Thêm nhánh
  `else` log `$"Hub: đẩy 0/{pending.Count} đơn — hub không phản hồi, sẽ thử lại lượt sau."`.

## 6. Tiêu chí nghiệm thu

- [ ] `dotnet build` toàn solution 0 error; `dotnet test XuLyDonShopee.Tests` xanh.
- [ ] **Test bất biến #1 (quan trọng nhất):** hub CHƯA cấu hình (file `config/orders.json` không có/URL rỗng) +
      client ĐÃ có URL → sau vài nhịp poll, URL của client **KHÔNG bị xoá**.
- [ ] Hub điền URL + tab → client (chưa có) tự nhận trong ~1 phút, **không cần khởi động lại**; ghi sheet chạy
      từ shop kế tiếp.
- [ ] Sửa ở client → lưu → Hub thấy giá trị mới (mở `/config/orders`); client thứ 2 nhận theo.
- [ ] Client rỗng → KHÔNG đẩy đè hub (hub giữ nguyên).
- [ ] Log: máy chưa cấu hình GSheet hiện đúng 1 dòng "chưa cấu hình Web App URL — bỏ qua ghi sheet (N đơn chờ)";
      hub chết hiện "Hub: đẩy 0/N đơn…".
- [ ] App Đơn hàng chạy ĐỘC LẬP (không hub): hành vi y như cũ, không lỗi.
- [ ] Thư mục phiếu/video/ảnh + trình duyệt của từng máy KHÔNG bị đụng.
- [ ] Thêm unit test cho phần thuần logic tách được (vd hàm quyết định "có áp bản hub không" theo bất biến #1/#2).

## 7. Rủi ro & lưu ý

- Bẫy lớn nhất là **bất biến #1** — làm sai thì cả fleet âm thầm tắt ghi sheet. Ưu tiên viết test cho nhánh này.
- Đổi tab **không hồi tố** đơn đã ghi (`orders.gsheet_tab` nhớ tab lần đầu — `OrdersRepository.cs:355`,
  `AccountSession.cs:905`). Phải ghi chú trên UI hub.
- Hub-web là project riêng: đổi hub xong cần **deploy VM** (xem CLAUDE.md mục Deploy) — nhưng plan này CHỈ code,
  KHÔNG deploy. Fable quyết lịch deploy sau.
- Giữ nguyên khuôn guard/nuốt-lỗi của 3 hook cũ trong `OrdersModuleHost` cho hook mới (đừng để lỗi hub làm chết
  luồng đơn hàng).

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa thực thi>
