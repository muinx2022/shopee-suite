# Plan: Hub thêm domain Đơn hàng + báo đơn mới về Slack/Discord/Telegram

- **Ngày:** 2026-07-21
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nơi làm:** cây làm việc CHÍNH `D:\Projects\shopee-suite`, nhánh `feature/gop-don-hang`. KHÔNG đụng `main`.

## 1. Bối cảnh & mục tiêu

App hợp nhất (phase 1) đã có module "Xử lý đơn Shopee". Giờ hub production của suite
(`server/Shopee.Hub.Web`, Blazor + SQLite, chạy VPS `api.schedra.net`) cần:
1. **Domain Đơn hàng**: nhận đơn client đẩy lên (`POST /api/orders/push`), lưu, hiển thị 2 trang
   admin Shops/Orders.
2. **Notify**: khi push mang đơn MỚI (Added>0) → bắn tin về webhook Slack/Discord/Telegram cấu
   hình trên trang Settings của hub.

**Nguyên liệu port**: fork Phase 0 tại `d:\Projects\Xu-ly-don-shopee\hub\XuLyDonShopee.Hub.Web\`
(CHỈ ĐỌC — repo khác, tuyệt đối không sửa) đã có sẵn: `Data/HubDatabase.Shops.cs`,
`Data/HubDatabase.Orders.cs`, `Coordination/OrderDtos.cs`, block endpoint trong
`Api/ClientApiEndpoints.cs:72-95`, `Components/Pages/{Shops,Orders}.razor`, nav trong
`MainLayout.razor:20-22,96-97`, icon "shop"/"orders" trong `Components/HubIcons.cs`.
Port sang phải đổi namespace `XuLyDonShopee.Hub.*` → `Shopee.Hub.Web.*`.

**Khác biệt nếp giữa hub suite và fork (PHẢI theo hub suite):**
- DTO/route của hub suite là FILE LINK từ `suite/Shopee.Core/Coordination/`
  (`Shopee.Hub.Web.csproj:22-53`, mẫu `<Compile Include="..\..\suite\..." Link="Shared\..."/>`).
  → `OrderDtos.cs` mới + hằng route mới đặt ở `suite/Shopee.Core/Coordination/` rồi link vào hub.
- `EnsureSchema()` của hub suite (`Data/HubDatabase.cs:109-144`) gộp 1 string — thêm 2 lời gọi
  `EnsureShopsSchema(); EnsureOrdersSchema();` ở cuối như fork làm.
- Settings hub lưu bảng `settings` key/value qua `Db.GetSetting/SetSetting`, key gom ở
  `Services/HubOptions.cs` → `SettingKeys`.

**Một thay đổi thiết kế so với fork (chủ đích):** fork yêu cầu tạo Shop tay rồi client push theo
`ShopId`. Ở đây đổi thành **hub tự đăng ký shop theo username khi push** — client không cần biết
id trên hub: `OrdersPushRequest { string ShopUsername; string? ShopName; List<OrderPushItem> Orders }`,
hub `GetOrCreateShopByUsername(username, name)` (bảng shops thêm UNIQUE theo username) rồi
`UpsertOrders(shopId, …)`. Trang Shops thành danh bạ shop tự đăng ký (sửa note/xóa được, không cần form tạo đủ trường như fork).

## 2. Phạm vi

- **Làm:**
  - `suite/Shopee.Core/Coordination/OrderDtos.cs` (MỚI): `OrderPushItem` (class, mirror
    `SyncedOrder` như fork `OrderDtos.cs:8-30`), `OrdersPushRequest` (ShopUsername/ShopName/Orders),
    `OrdersPushResult(int Added, int Updated)`. Namespace theo các file Coordination hiện có.
  - `suite/Shopee.Core/Coordination/HubRoutes.cs`: thêm `Shops="/api/shops"`,
    `Orders="/api/orders"`, `OrdersPush="/api/orders/push"` (prefix `/api` BẮT BUỘC — tránh
    `AmbiguousMatchException` với trang Blazor `/shops`,`/orders`; bài học fork `HubRoutes.cs:50-51`).
  - `suite/Shopee.Core/Coordination/HubClient.cs`: thêm
    `Task<OrdersPushResult> PushOrdersAsync(OrdersPushRequest req, CancellationToken ct)` —
    POST `HubRoutes.OrdersPush` theo mẫu `PostBigSellerUpsertAsync` (`HubClient.cs:272-277`),
    dùng `_bulkHttp` (batch đơn có thể lớn).
  - `server/Shopee.Hub.Web/`:
    - `Data/HubDatabase.Shops.cs` + `Data/HubDatabase.Orders.cs` (port từ fork, đổi namespace;
      Shops thêm `UNIQUE` username + `GetOrCreateShopByUsername`; `UpsertOrders` trả thêm
      `List<OrderPushItem> InsertedItems` — cần cho notify); gọi 2 Ensure trong `EnsureSchema()`.
    - `Shopee.Hub.Web.csproj`: link `OrderDtos.cs` (từ suite/Coordination) + link
      `..\..\orders\XuLyDonShopee.Core\Services\OrderNotifyService.cs` và
      `..\..\orders\XuLyDonShopee.Core\Models\SyncedOrder.cs` vào `Shared\Notify\`
      (OrderNotifyService thuần BCL, `TaoTinNhanDonMoi` cần type `SyncedOrder` — link cả hai,
      KHÔNG sửa 2 file gốc đó).
    - `Api/ClientApiEndpoints.cs`: 3 endpoint như fork (`ClientApiEndpoints.cs:72-95` của fork)
      nhưng push nhận `ShopUsername`; sau `UpsertOrders`, nếu `Added>0` → fire-and-forget notify
      (bước notify bên dưới); `AppendLog` kèm `X-Machine-Id` như fork.
    - Notify: `Services/HubOptions.cs` thêm `SettingKeys.NotifyWebhooks = "notify.webhooks"`
      (value = nhiều dòng, MỖI DÒNG 1 webhook URL — Slack/Discord/Telegram tự nhận diện);
      `Components/Pages/Settings.razor` thêm card "Báo đơn mới" (textarea + validate từng dòng
      bằng `OrderNotifyService.KiemTraUrl`, mẫu card token dòng 8-27); trong đường push:
      map `InsertedItems` → `SyncedOrder`, dựng tin `OrderNotifyService.TaoTinNhanDonMoi(tênShop,…)`,
      `Task.Run` gửi `SendAsync` tới TỪNG URL, nuốt lỗi + `ILogger.LogWarning`.
    - `Components/Pages/Shops.razor` + `Orders.razor` (port từ fork, đổi namespace/@using; Shops
      bỏ form tạo tay đủ trường — giữ list + sửa note + xóa); `Components/HubIcons.cs` thêm icon
      "shop"/"orders" (copy path từ fork); `Components/Layout/MainLayout.razor` thêm navsec
      "Đơn hàng" + nhánh `UpdateTitle()`.
- **Không làm:**
  - KHÔNG sửa repo `d:\Projects\Xu-ly-don-shopee` (chỉ đọc fork làm mẫu).
  - KHÔNG sửa gì dưới `orders/` (kể cả OrderNotifyService.cs/SyncedOrder.cs — chỉ LINK).
  - KHÔNG đụng phía client push (plan riêng sau); KHÔNG deploy VPS (Fable làm sau nghiệm thu).
  - KHÔNG commit.

## 3. Kiểm chứng

1. `dotnet build server/ShopeeHub.sln` → 0 lỗi (hub). `dotnet build ShopeeSuite.sln -c Release`
   → 0 lỗi (vì sửa Shopee.Core). `dotnet test orders/XuLyDonShopee.Tests/... ` → 742 pass nguyên.
2. Chạy hub local: env `HUB_API_TOKEN=test-token`, `HUB_ADMIN_USER/HUB_ADMIN_PASSWORD`,
   `HUB_DATA_DIR=<thư mục tạm trong scratchpad>`, `ASPNETCORE_URLS=http://127.0.0.1:18088`
   (cổng lạ tránh đụng) — rồi curl:
   - `POST /api/orders/push` (header `X-Api-Token: test-token`, body 2 đơn, ShopUsername mới)
     → 200 `{added:2, updated:0}`; bảng shops tự có shop mới.
   - push LẠI y nguyên → `{added:0, updated:2}` (không nhân đôi).
   - `GET /api/orders?shopId=1` → thấy 2 đơn; `GET /api/shops` → thấy shop.
   - Không token → 401.
   - Notify: chưa cấu hình webhook → push vẫn 200, không lỗi (đường notify bị bỏ qua im lặng).
     Cấu hình 1 URL giả dạng `https://hooks.slack.com/services/TEST` qua `SetSetting` trực tiếp
     trong DB tạm → push đơn mới → log warning gửi thất bại (chứng minh đường notify có chạy),
     response vẫn 200 nhanh (fire-and-forget không chặn).
3. Dừng hub local, dọn tiến trình.

## 4. Tiêu chí nghiệm thu

- [ ] 2 sln build 0 lỗi; 742 test orders pass nguyên.
- [ ] Bộ curl ở mục 3 đúng như mô tả (push/idempotent/list/401/notify-không-chặn).
- [ ] DTO/route nằm ở `suite/Shopee.Core/Coordination/` và hub dùng qua LINK (không file cục bộ trùng lặp).
- [ ] OrderNotifyService/SyncedOrder được LINK, 2 file gốc không đổi một ký tự.
- [ ] `git status` không có file nào ngoài phạm vi.

## 5. Rủi ro & lưu ý

- `OrderPushItem` phải bind JSON khoan dung (class property settable như fork — lý do fork chọn
  class thay record ghi ở `OrderDtos.cs:8`).
- Route bare `/shops`,`/orders` là trang Blazor — endpoint API bắt buộc prefix `/api`.
- Notify tuyệt đối fire-and-forget: push endpoint phải trả về ngay cả khi webhook chậm/chết.
- `Added>0` mới notify — push lặp (updated) không được spam tin.
- Hub suite có nhánh Postgres (`ProductDb`) trong `/health` — không đụng.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
