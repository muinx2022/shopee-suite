# Plan: Màn Đơn hàng hiển thị + lọc theo SHOP (thay vì subaccount)

- **Ngày:** 2026-07-24
- **Trạng thái:** hoàn thành code + build 0 lỗi + 925 test xanh (thêm ~9 test); CHỜ deploy (app đang chạy flow — deploy khi user cho phép) + verify thật
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nền:** Mô hình subaccount → nhiều shop. Đơn THUỘC VỀ shop, nhưng màn Đơn hàng đang hiển thị + lọc theo **email subaccount** (di sản 1-account-1-shop). GSheet đã đúng (đẩy tên shop qua `_currentShopLogin`). DB vừa purge (0 đơn).

## 1. Bối cảnh & mục tiêu

Hiện `orders` gắn `shop_id` (SỐ, vd `1843718137`) per-đơn nhưng **không lưu TÊN shop** (login, vd `alina99.store`), và màn Đơn hàng:
- Cột "Tài khoản" ([OrdersView.axaml:154](../orders/XuLyDonShopee.App/Views/OrdersView.axaml)) bind `AccountLabel` = **email account** (`Apply` line ~246: `labels[a.Id]=a.Email`).
- Ô lọc (watermark "Gõ tên shop") thực chất **lọc theo account_id/email**.
⇒ Mọi đơn của 1 subaccount hiện CÙNG email, không phân biệt shop.

**Mục tiêu:** màn Đơn hàng **hiển thị TÊN SHOP per-đơn** + **lọc theo shop**. Giữ nguyên khoá nghiệp vụ `(account_id, order_sn)` (đơn vẫn thuộc account để sync/gsheet/hub; chỉ THÊM thuộc tính shop để hiển thị/lọc).

**Nguyên tắc: CỘNG THÊM, không phá.** Giữ nguyên đường lọc theo account trong repo (tham số cũ default null, các test cũ vẫn xanh); chỉ THÊM lọc theo shop và cho màn Đơn hàng dùng đường shop.

## 2. Các bước

### B1. DB — thêm cột `shop_login`
`orders/XuLyDonShopee.Core/Data/Database.cs`: cạnh chỗ `EnsureColumn(conn,"orders","shop_id","TEXT")` (line ~197), thêm:
`EnsureColumn(conn, "orders", "shop_login", "TEXT");` + comment ("tên đăng nhập shop để hiển thị/lọc màn Đơn hàng; sync gắn từ `_currentShopLogin`; đơn cũ NULL").

### B2. Repo — lưu + đọc + lọc theo shop
`orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`:
- **`UpsertMany`**: thêm tham số `string? shopLogin = null` (SAU `shopId`). Mirror y hệt `shop_id`:
  - INSERT: thêm cột `shop_login` vào danh sách cột + `$shopLogin` vào VALUES; bind `(object?)shopLogin ?? DBNull.Value` (cả nhánh insert).
  - UPDATE: thêm `shop_login = COALESCE($shopLogin, shop_login),` (giữ khi lượt này null); bind `$shopLogin` ở nhánh update.
- **`OrderRow`** (model, cuối file / file riêng): thêm `public string? ShopLogin { get; init; }`.
- **`Query`**: thêm `shop_login` vào SELECT (thêm cuối, sau `synced_at`); `MapRow` đọc cột mới → `ShopLogin = r.IsDBNull(n) ? null : r.GetString(n)` (n = index cuối). Thêm 2 tham số optional CUỐI chữ ký: `string? shopLogin = null, bool shopExact = false`; truyền vào `AppendFilter`.
- **`Count`**: thêm cùng 2 tham số optional `shopLogin`/`shopExact`; truyền vào `AppendFilter`.
- **`AppendFilter`**: thêm 2 tham số `string? shopLogin, bool shopExact` (CUỐI). Khi `!string.IsNullOrWhiteSpace(shopLogin)`:
  - `shopExact` → `sql.Append(" AND shop_login = $shop"); cmd.Parameters.AddWithValue("$shop", shopLogin.Trim());`
  - else → `sql.Append(@" AND shop_login LIKE $shopLike ESCAPE '\'"); cmd.Parameters.AddWithValue("$shopLike", "%" + EscapeLike(shopLogin.Trim()) + "%");`
  - (Đường account cũ GIỮ NGUYÊN — không xoá. Không cần short-circuit vì LIKE không khớp thì tự 0 dòng.)
- **`AllStatuses`**: thêm nạp chồng/tham số `string? shopLogin = null`; khi có → thêm `AND shop_login = $shop`. (Giữ tham số `accountId` cũ.)
- **`AllShopLogins`** (MỚI): `SELECT DISTINCT shop_login FROM orders WHERE shop_login IS NOT NULL AND TRIM(shop_login) <> '' ORDER BY shop_login;` → `List<string>`. (Nguồn cho ComboBox lọc shop.)

### B2b. Sync gắn `shop_login` khi lưu (THIẾT YẾU)
`orders/XuLyDonShopee.App/Services/AccountSession.cs` — `PersistSyncedOrdersAsync` hiện gọi
`_services.Orders.UpsertMany(_accountId, toUpsert, DateTime.UtcNow, shopId)` (line ~828). **THÊM đối số**
`_currentShopLogin`: `... UpsertMany(_accountId, toUpsert, DateTime.UtcNow, shopId, _currentShopLogin)`.
(`_currentShopLogin` đã được callback cầu nối set ngay trước — xem [orders-extension-migration] commit 8281af5.)
Kiểm các chỗ gọi `UpsertMany` KHÁC (đường Playwright cũ nếu còn) — truyền `_currentShopLogin` hoặc để trống (param optional). Nếu THIẾU bước này, cột `shop_login` luôn NULL → màn Đơn hàng không có gì để hiện.

### B3. ViewModel — lọc/hiển thị theo shop
`orders/XuLyDonShopee.App/ViewModels/OrdersViewModel.cs` (GIỮ tên biến `AccountOptions/AccountFilterText/SelectedAccount` để bớt churn XAML — chỉ đổi NGUỒN + ĐÍCH lọc sang shop; label option = shop_login, `Id` để null):
- Chỗ dựng options (line ~138-142): thay `_services.Accounts.GetAll()...a.Email` bằng:
  `AccountOptions.Add(new AccountFilterOption(null, "Tất cả shop"));`
  `foreach (var s in _services.Orders.AllShopLogins()) AccountOptions.Add(new AccountFilterOption(null, s));`
- **`CurrentFilter`** (line ~185): đổi trả về sang shop. Text nguồn-sự-thật:
  - trống → `shopLogin=null` (mọi shop);
  - khớp ĐÚNG 1 option label (Ordinal-ignorecase) → `shopLogin=text, shopExact=true`;
  - gõ dở → `shopLogin=text, shopExact=false` (LIKE).
  - Trả `(shopLogin, shopExact, status, search)`; KHÔNG dùng accountId/accountIds nữa (truyền null cho 2 tham số account của Query/Count).
- **`Apply`** (line ~220-250) + `QueryPage` khác (line ~443-451, ~508): bỏ `labels` theo email; nhãn dòng = `row.ShopLogin` (fallback `"(shop ?)"` nếu null — đơn cũ). Gọi `_services.Orders.Query(null, status, search, null, PageSize, offset, shopLogin, shopExact)` + `Count(...)` tương ứng.
- **`ReloadStatuses`**: nhận `shopLogin` (thay `accountId`) → `AllStatuses(shopLogin: ...)` (khớp trạng thái theo shop đang lọc). Cập nhật các chỗ gọi.
- **Tên file CSV** (line ~548): `{shopLabel|tatca}` thay `{email|tatca}`.
- Cập nhật comment các chỗ còn ghi "tài khoản/email" cho khớp ngữ nghĩa shop.

### B4. Cột hiển thị "Shop"
- `orders/XuLyDonShopee.App/ViewModels/OrderRowViewModel.cs`: đổi property `AccountLabel` → `ShopLabel` (+ tham số ctor `accountLabel`→`shopLabel`, + field trong record `FilterKey`/tuple line ~101). (Grep xác nhận `AccountLabel` chỉ dùng ở axaml + file này — churn nhỏ.)
- `orders/XuLyDonShopee.App/Views/OrdersView.axaml:154`: `Header="Tài khoản"` → `Header="Shop"`; `Binding AccountLabel` → `Binding ShopLabel`.

### B5. CSV export (nhất quán)
`orders/XuLyDonShopee.Core/Services/OrderCsvExporter.cs`: `Headers[0]` "Tài khoản" → "Shop". Giá trị cột đầu (`OrderExportRow` field đầu) đã do ViewModel bơm = nhãn shop (từ B3). Nếu `OrderExportRow` map từ `OrderRow`, đảm bảo dùng `ShopLogin`.

## 3. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App` XANH.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` XANH. Test cũ về **lọc theo account** trong repo GIỮ nguyên (đường account không xoá). Các test PHẢI cập nhật (sửa cho khớp, KHÔNG xoá): `OrderCsvExporterTests` (header "Tài khoản"→"Shop"); test nào assert `AccountLabel` (nếu có) → `ShopLabel`. THÊM test mới: `UpsertMany` lưu+giữ `shop_login` (COALESCE), `Query`/`Count` lọc theo `shopLogin` (exact + LIKE), `AllShopLogins` distinct. Báo lại số test cuối.
- [ ] Diff đúng phạm vi 6 file trên (+ file test). KHÔNG đụng luồng sync/bridge/gsheet/hub, KHÔNG xoá đường lọc account trong repo.
- [ ] (Fable verify thật sau deploy) Sync 1 subaccount nhiều shop → màn Đơn hàng: cột **Shop** hiện `alina99.store`/`shop9x.store`… (KHÔNG phải email subacc); ô lọc gõ tên shop → đúng đơn của shop đó; "Tất cả shop" → mọi đơn.

## 4. Rủi ro & lưu ý

- **Đơn cũ `shop_login=NULL`:** sau purge DB rỗng nên không còn; vẫn để fallback `"(shop ?)"` cho an toàn. Sync mới luôn set (callback đã có `_currentShopLogin` — xem [orders-extension-migration]).
- **`_currentShopLogin` → UpsertMany:** hiện `PersistSyncedOrdersAsync` gọi `UpsertMany(_accountId, toUpsert, now, shopId)` — **THÊM tham số `shopLogin`**: truyền `_currentShopLogin` (field đã set ở callback bridge). Kiểm cả các chỗ khác gọi `UpsertMany` (đường Playwright cũ nếu còn) — truyền `_currentShopLogin` hoặc null; đừng để lệch tham số.
- **Giữ churn XAML thấp:** không đổi tên `AccountOptions/AccountFilterText/SelectedAccount/AccountFilterOption` (chỉ đổi ngữ nghĩa + nguồn dữ liệu); tránh sửa nhiều binding.
- **Không đổi khoá đơn:** vẫn `(account_id, order_sn)`. `shop_login` chỉ là thuộc tính hiển thị/lọc.
- Mỗi bước build; B2 (repo) test kỹ trước khi lên VM UI.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
