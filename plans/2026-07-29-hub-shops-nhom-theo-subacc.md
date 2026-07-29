# Plan: Hub trang Shop — nhóm theo subacc

- **Ngày:** 2026-07-29
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Auto (Cursor)

## 1. Bối cảnh & mục tiêu

Trang `/shops` hiện `ListShops()` — **list phẳng** mọi shop trong bảng `shops` (khoá = `username` = shop_login).

Mô hình vận hành thật: **subacc (email đăng nhập)** → nhiều shop. Quan hệ đó đã có trên hub ở gương client:

- `orders_accounts` / `orders_account_shops` (`login` = subacc, `shop_login` = shop, `shop_name`)
- Bảng `shops` vẫn là danh bạ hub (id, note, đơn…) — **không** có cột parent subacc.

**Mục tiêu:** UI `/shops` hiển thị **theo nhóm subacc** (giống pattern dòng `acctrow` ở Giao việc), mỗi nhóm liệt kê shop thuộc subacc đó; shop hub mồ côi (có trong `shops` nhưng chưa từng xuất hiện trong gương) gom nhóm riêng.

## 2. Phạm vi

- **Làm:**
  - `HubDatabase`: API gom nhóm subacc → shop (gộp mọi máy trong gương, distinct theo `login`+`shop_login`).
  - `Shops.razor`: render nhóm + giữ Sửa/Xoá/Lưu như cũ; thống kê “N subacc · M shop”.
  - Shop orphan → nhóm `"— Chưa gắn subacc —"` (hoặc tương đương).
  - Giữ `stack-sm` / mobile; dòng nhóm kiểu `acctrow`.
- **Không làm:** Schema mới trên `shops`; đổi API push đơn; trang Dispatch; deploy (user gọi riêng).

## 3. Các bước thực hiện

1. **`HubDatabase.Shops.cs` (hoặc file partial mới cạnh OrdersAccounts)**  
   - Record `ShopGroup(string SubLogin, List<Shop> Shops)` — `Shop` vẫn entity hub; shop chỉ có trong gương chưa có hàng `shops` → tạo view-model tối thiểu (Id=0, Name/Username từ gương) **chỉ để hiện**, nút Sửa/Xoá disable hoặc ẩn (chưa có bản ghi hub).  
   - Hoặc: chỉ hiện shop đã có trong `shops`, map vào nhóm qua `shop_login` = `shops.username`; shop gương chưa push đơn → hiện dòng “chưa có trên hub” (tên từ gương, không nút xoá).  
   - **Chốt:** Mỗi dòng shop trong nhóm = join `orders_account_shops.shop_login` ↔ `shops.username`. Có bản ghi hub → hiện đủ + Sửa/Xoá. Chỉ có gương → hiện tên/login + badge “chưa có đơn trên hub”, không xoá.  
   - Orphan: `shops` có username không thuộc bất kỳ `shop_login` nào trong gương → nhóm `"— Chưa gắn subacc —"`.  
   - Method: `ListShopGroupsBySubAccount()` trả `List<ShopGroup>` sắp xếp subacc NOCASE; trong nhóm shop theo name/login.

2. **`Shops.razor`**  
   - Reload dùng `ListShopGroupsBySubAccount`.  
   - Markup: `@foreach group` → `<tr class="acctrow">` header (login · N shop · tổng đơn nhóm) rồi các `<tr>` shop.  
   - Cột “Tài khoản” trên dòng shop có thể bỏ/đổi thành shop_login (vì nhóm đã hiện subacc). Cột: Tên shop | Login shop | Ghi chú | Đơn | Thao tác.  
   - Hint cập nhật: giải thích nhóm theo gương subacc từ client.

3. **CSS** (nhẹ): tái dùng `table.grid tr.acctrow` nếu đã có; không thì thêm nền xám nhẹ cho header nhóm trên trang shops (class `shops-page` nếu cần tránh đụng Fleet).

4. Build Hub Web.

## 4. Tiêu chí nghiệm thu

- [ ] `/shops` không còn list phẳng toàn bộ; thấy header subacc rồi shop con.
- [ ] Cùng shop_login trên nhiều máy chỉ hiện 1 lần trong đúng subacc.
- [ ] Shop hub chưa có trong gương nằm nhóm “Chưa gắn subacc”.
- [ ] Sửa/Xoá shop hub vẫn hoạt động; shop chỉ-gương không xoá được.
- [ ] `dotnet build` Hub OK.

## 5. Rủi ro

- Gương trống (chưa máy nào push) → mọi shop hub vào “Chưa gắn subacc” (chấp nhận).
- Một shop_login gắn 2 subacc khác nhau trên 2 máy (hiếm) → hiện ở cả hai nhóm (distinct theo cặp login+shop_login).

---

## Báo cáo thực thi

- `ListShopGroupsBySubAccount()`: gom gương `orders_account_shops` theo subacc, join `shops`; orphan → nhóm “Chưa gắn subacc”.
- `Shops.razor`: header `acctrow` + shop con; shop chỉ-gương badge “chưa trên hub”; Sửa/Xoá chỉ khi có bản ghi hub.
- `app.css?v=37` + build Hub Release OK. Chưa deploy.
