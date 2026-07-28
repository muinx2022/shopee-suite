# Plan: Hiển thị SKU đủ theo TỪNG sản phẩm ở app + hub (như Google Sheet)

- **Ngày:** 2026-07-28
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Đơn nhiều sản phẩm (vd `260728UN59FAXP`, 2 sản phẩm) trên **Google Sheet** hiện đủ mỗi sản phẩm một dòng
SKU + phân loại:

```
xxx  A521                    BỐT 1900 ĐEN,37
     A357-Đen Full LOLITA-36 Đen Full LOLITA,36
```

Nhưng trong **app desktop** (màn Đơn hàng) và **hub** (trang `/orders`), cột **Phân loại hiện ĐỦ cả 2**
(nối bằng `" · "`), còn cột **SKU chỉ hiện MỘT mã** (`A521`).

**Đã soi DB hub cho đơn này — dữ liệu KHÔNG mất, đây là lỗi HIỂN THỊ thuần túy:**

```
item_count = 2 | cột sku = A521
items_json có 2 sản phẩm, cả hai đủ khóa sku + phanLoai:
  [0] sku = A521                     | phanLoai = BỐT 1900 ĐEN,37
  [1] sku = A357-Đen Full LOLITA-36  | phanLoai = Đen Full LOLITA,36
```

Nguyên nhân: cột Phân loại được suy từ `items_json` theo TỪNG sản phẩm
(`PhanLoaiExtractor.TuItemsJson`), còn cột SKU lại đọc field DB **đơn-giá-trị** `Sku`/`o.Sku` (chỉ mang SKU
của sản phẩm ĐẦU). Google Sheet thì dựng SKU nhiều dòng từ `items_json` (`SanPhamDonParser.CotGsheet`).

**Mục tiêu:** cột SKU ở app + hub đọc SKU của TỪNG sản phẩm trong `items_json` rồi nối `" · "` (một dòng,
đồng bộ cách cột Phân loại đang làm). Đơn cũ không có khóa `sku` trong `items_json` → GIỮ hành vi cũ (hiện
field đơn `Sku`), tuyệt đối không làm trống cột đang có.

## 2. Phạm vi

- **Làm:**
  - Thêm hàm thuần bóc SKU từng sản phẩm từ `items_json` trong `orders/XuLyDonShopee.Core/Services/PhanLoaiExtractor.cs`
    (file này ĐÃ được link sang hub qua `server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` — đặt hàm ở đây để app
    và hub dùng CHUNG một luật, khỏi thêm link mới).
  - App: `orders/XuLyDonShopee.App/ViewModels/OrderRowViewModel.cs` — cột `Sku` ưu tiên chuỗi SKU-nhiều-sản-phẩm,
    rỗng thì lùi về `_row.Sku`.
  - Hub: `server/Shopee.Hub.Web/Components/Pages/Orders.razor` — cột SKU (dòng ~75 `@(o.Sku ?? "—")`) đổi tương tự.
- **Không làm:**
  - KHÔNG đụng Google Sheet / Apps Script (đã đúng).
  - KHÔNG đổi cột "Sản phẩm" (vẫn "tên SP đầu (+n)") và cột "Phân loại" (đã đúng).
  - KHÔNG đổi field DB `sku`/`o.Sku` hay luồng đẩy — chỉ đổi cách HIỂN THỊ đọc từ `items_json`.
  - KHÔNG dùng `SanPhamDonParser` cho hub (file đó CHỈ có bên client, KHÔNG link sang hub).
  - KHÔNG commit / deploy / release (Fable làm sau khi nghiệm thu).

## 3. Các bước thực hiện

1. **`PhanLoaiExtractor.cs` — thêm hàm `SkuTuItemsJson(string? itemsJson)`** (public static, thuần BCL):
   - Parse `items_json` như `TuItemsJson`: rỗng / `"[]"` / JSON hỏng / không phải mảng → chuỗi rỗng (KHÔNG ném).
   - Với mỗi phần tử là object, đọc khóa `sku` (string). Bỏ phần tử không có `sku` hoặc `sku` rỗng.
   - **KHÔNG khử trùng** (khác `TuItemsJson`): SKU từng sản phẩm là riêng biệt, nối đúng thứ tự mảng bằng `" · "`.
   - Không có sản phẩm nào mang khóa `sku` → trả **chuỗi rỗng** (báo hiệu caller lùi về field `Sku` cũ).
   - Đặt hằng nối dùng lại `" · "` cho khớp cột Phân loại.
2. **App `OrderRowViewModel.cs`:**
   - Trong constructor, tính sẵn `SkuNhieu = PhanLoaiExtractor.SkuTuItemsJson(row.ItemsJson)` (parse MỘT LẦN,
     giống `PhanLoai`).
   - Sửa property `Sku` (dòng ~63): trả `SkuNhieu` nếu KHÁC rỗng, ngược lại `_row.Sku ?? string.Empty`.
   - Giữ nguyên chú thích/summary hoặc cập nhật cho đúng hành vi mới.
3. **Hub `Orders.razor`:**
   - Trong vòng lặp `@foreach (var o in _orders)` (đã có biến `phanLoai`), thêm
     `var sku = PhanLoaiExtractor.SkuTuItemsJson(o.ItemsJson);` rồi `if (string.IsNullOrEmpty(sku)) sku = o.Sku;`
   - Ô SKU: `<td class="m-hide" title="@sku">@(string.IsNullOrEmpty(sku) ? "—" : Trim(sku))</td>` (giữ `Trim` +
     `title` như cột Phân loại để không vỡ layout khi chuỗi dài).
4. **Test** trong `orders/XuLyDonShopee.Tests`:
   - Bổ sung `PhanLoaiExtractorTests` (hoặc file test mới) cho `SkuTuItemsJson`:
     - items_json 2 sản phẩm có `sku` → `"A521 · A357-Đen Full LOLITA-36"`.
     - items_json chỉ có `name/variation/amount/image` (đơn cũ, KHÔNG có `sku`) → chuỗi rỗng.
     - 1 sản phẩm có `sku` → đúng 1 mã, không có dấu nối.
     - null / `""` / `"[]"` / JSON hỏng → chuỗi rỗng.
     - Có sản phẩm thiếu `sku` xen giữa → chỉ nối các mã có, giữ thứ tự.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` và `dotnet build server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh; các test cũ của `PhanLoaiExtractor` giữ nguyên.
- [ ] Đơn `260728UN59FAXP`: cột SKU (app + hub) hiện `A521 · A357-Đen Full LOLITA-36`, cột Phân loại vẫn
      `BỐT 1900 ĐEN,37 · Đen Full LOLITA,36`.
- [ ] Đơn cũ có `items_json` chỉ gồm `name/variation/amount/image` → cột SKU vẫn hiện `Sku` đơn như trước (không trống).

## 5. Rủi ro & lưu ý

- **Không làm trống cột đang có:** đơn cũ (`items_json` nghèo, không khóa `sku`) phải lùi về `o.Sku`; test khoá ca này.
- Đặt hàm trong `PhanLoaiExtractor.cs` để tận dụng link csproj sẵn có — nếu Opus muốn tách class riêng thì
  PHẢI thêm `<Compile Include=... Link=...>` vào `Shopee.Hub.Web.csproj`, kẻo hub không build được.
- SKU nối `" · "` một dòng (không xuống dòng như sheet) cho khớp lưới bảng của app/hub — CỐ Ý khác sheet.
- Đây là thay đổi **client + hub** → sau nghiệm thu cần **release client** và **deploy hub** mới thấy hiệu lực thật.

---

## Báo cáo thực thi (Opus điền sau khi xong)
