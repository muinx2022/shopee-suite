# Plan: Đẩy đơn + phiếu lên Hub THEO SHOP (không theo subaccount)

- **Ngày:** 2026-07-24
- **Trạng thái:** hoàn thành code + build 0 lỗi + 929 test (4 mới); chờ deploy + Fable xóa đơn hub + verify thật
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nền:** Mô hình subaccount → nhiều shop. Hub lưu đơn keyed theo `shop_id` (hub tự cấp từ `ShopUsername`). Client đẩy hiện dùng `ResolveShopUsername(acc)` = **email subaccount** → MỌI shop dồn vào 1 "shop" hub → "sync lên hub chưa được / sai shop".

## 1. Bối cảnh & chẩn đoán

- Hook `OrdersModuleHost.WireHubPush`: `ShopUsername = ResolveShopUsername(acc, accountId)` (= `Account.Email` = subaccount) cho MỌI đơn. `GetForHubPush(accountId)` trả đơn của CẢ account (nhiều shop) → đẩy chung 1 ShopUsername.
- Hook `WireHubSlipPush`: y hệt (slip cũng keyed subaccount) → phiếu không khớp shop trên hub.
- Đây là **cùng lớp lỗi shop-context** đã fix cho GSheet ([orders-extension-migration] commit 8281af5) + cho cột Shop UI. Giờ fix cho HUB.

**Mục tiêu:** đẩy đơn/phiếu lên hub keyed theo **shop_login** (tên đăng nhập shop, vd "alina99.store") — mỗi shop 1 request. Đơn không có shop_login (đơn cũ) → fallback ResolveShopUsername (subaccount) như cũ (không phá).

## 2. Các bước

### B1. `SyncedOrder` mang shop_login
`orders/XuLyDonShopee.Core/Models/SyncedOrder.cs`: thêm `public string? ShopLogin { get; set; }` (tên đăng nhập shop của đơn — để hub push nhóm theo shop; null cho đơn cũ).

### B2. Repo cấp shop_login
`orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`:
- `GetForHubPush`: thêm `shop_login` vào SELECT (cuối) + set `ShopLogin = reader.IsDBNull(n) ? null : reader.GetString(n)` cho mỗi `SyncedOrder`.
- Thêm method `public IReadOnlyDictionary<string, string?> GetShopLoginsByOrderSns(long accountId, IEnumerable<string> orderSns)` — trả map `order_sn → shop_login` (cho slip push nhóm theo shop). Tham số hóa `IN (...)`; rỗng → dict rỗng.

### B3. Hook đẩy hub theo shop
`suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`:
- **`WireHubPush`**: thay vì 1 ShopUsername, **GROUP `orders` theo shop key**:
  `key(o) = string.IsNullOrWhiteSpace(o.ShopLogin) ? ResolveShopUsername(acc, accountId) : o.ShopLogin.Trim()`.
  Với MỖI nhóm → 1 `OrdersPushRequest { ShopUsername = key, ShopName = key, Orders = nhóm.Select(ToPushItem) }` → `PushOrdersAsync`. Trả `true` CHỈ khi MỌI nhóm OK (nhóm nào null → allOk=false; đơn nhóm lỗi không được mark, lượt sau đẩy lại — giữ bất biến "thà đẩy lặp còn hơn mất đơn").
- **`WireHubSlipPush`**: tra `map = services.Orders.GetShopLoginsByOrderSns(accountId, slips.Select(s => s.OrderSn))`; GROUP `slips` theo `key(s) = map[s.OrderSn] rỗng ? ResolveShopUsername(acc) : shop_login`. Với mỗi nhóm → 1 `OrdersSlipPushRequest { ShopUsername = key, ... }` → `PushOrderSlipsAsync`. Gộp kết quả: danh sách order_sn ĐÃ LƯU = hợp các nhóm (mỗi nhóm trừ missing/errors của nhóm đó); nhóm trả null → coi cả nhóm chưa lưu (không mark). Giữ đúng ngữ nghĩa "đã lưu = gửi − missing − errors" per-nhóm.
- Giữ `ResolveShopUsername` làm fallback (đơn/phiếu thiếu shop_login).

## 3. Tiêu chí nghiệm thu

- [ ] `dotnet build suite/Shopee.Suite` + `orders/XuLyDonShopee.App` XANH; `dotnet test orders/XuLyDonShopee.Tests` giữ 925 xanh (thêm test cho GetForHubPush set ShopLogin + GetShopLoginsByOrderSns nếu tiện; nếu sửa test hub-push hiện có do đổi hành vi nhóm → cập nhật cho khớp, KHÔNG xoá).
- [ ] Diff đúng phạm vi 3 file. KHÔNG đụng GSheet/luồng sync/bridge, KHÔNG đụng hub server.
- [ ] (Fable verify) Sau khi Fable XÓA sạch đơn trên hub + deploy: sync 1 subaccount nhiều shop → trên hub (trang /orders hoặc /shops) đơn nằm ĐÚNG từng shop (alina99.store, shop9x.store…), KHÔNG dồn 1 shop subaccount; phiếu khớp đúng shop.

## 4. Rủi ro & lưu ý

- **Fallback shop_login null:** đơn cũ (trước khi có shop_login) → ShopUsername = subaccount (như cũ). Sau khi Fable purge + re-sync, mọi đơn mới có shop_login.
- **Mark per-nhóm:** hook trả bool (orders) / list order_sn (slips) cho tầng `PushPendingToHubAsync`/slip — giữ đúng: chỉ mark đơn thuộc nhóm ĐÃ đẩy OK. Nhóm lỗi giữ chưa-mark → lượt sau đẩy lại (hub idempotent theo shop_id+order_sn).
- **Hub tự đăng ký shop mới:** POST /api/orders/push với ShopUsername mới (shop_login) → hub tạo shop mới (khoá ShopUsername→shop_id). Không cần đụng hub server.
- **Xóa dữ liệu hub (Fable làm, ngoài phạm vi opus):** sau khi deploy fix, Fable xóa bảng `orders` (+ order-shops nếu cần) trong hub.db trên VM (ssh vps-muinx, python3) rồi cho user re-sync sạch.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
