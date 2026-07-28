# Plan: Hub chống ĐÈ `items_json` — không cho bản nghèo đè bản giàu

- **Ngày:** 2026-07-28
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Client có lỗi: vòng sync đọc TRANG DANH SÁCH (`items_json` chỉ `name/variation/amount/image`) đè lên bản
TRANG CHI TIẾT (giàu, có `sku`+`phanLoai`). Đã vá phía client ở commit `ec79aff`
(`SanPhamDonParser.ChonItemsJson`: bản nghèo KHÔNG được đè bản giàu; `item_count` đi theo bản được giữ).

**Hub còn nguyên lỗi tương tự.** Upsert của hub ghi đè thẳng `items_json`:

```
server/Shopee.Hub.Web/Data/HubDatabase.Orders.cs, câu UPSERT:
  ON CONFLICT(shop_id,order_sn) DO UPDATE SET
    ... items_json=$ij, item_count=$ic, ...   <- ghi đè THẲNG (các cột khác như final_amount,
    tracking_number, return_request_code đều COALESCE bảo vệ, riêng items_json thì không)
```

Rủi ro THẬT với mô hình nhiều máy: máy A đọc trang chi tiết đẩy bản giàu lên hub; máy B (chỉ đọc trang danh
sách của cùng shop/đơn) đẩy lại bản nghèo → hub mất `sku`/`phanLoai`. `ec79aff` KHÔNG đụng hub nên chưa che.

**Mục tiêu:** hub chỉ ghi đè `items_json` khi bản mới GIÀU hơn (hoặc bản cũ không có gì), dùng CHUNG luật
`SanPhamDonParser.ChonItemsJson` với client. `item_count` đi theo bản `items_json` được giữ (khỏi count nói dối).

## 2. Phạm vi

- **Làm:**
  - Link `SanPhamDonParser.cs` vào hub (`server/Shopee.Hub.Web/Shopee.Hub.Web.csproj`) — file thuần BCL
    (`System.Text.Json`), link được. (Trước đây ghi "chỉ có bên client" chỉ là ghi chú thiết kế, không phải chặn.)
  - `HubDatabase.Orders.cs` — trong `UpsertOrders`, trước khi ghi mỗi đơn ĐÃ TỒN TẠI: đọc `items_json` (và
    `item_count`) hiện có, chọn bản giữ bằng `ChonItemsJson(cu, moi)`, và bind `$ij`/`$ic` theo bản được chọn.
- **Không làm:**
  - KHÔNG đổi hành vi các cột khác của upsert.
  - KHÔNG đụng client (đã vá ở `ec79aff`) hay Google Sheet.
  - KHÔNG commit / deploy (Fable làm sau nghiệm thu; hub cần deploy riêng).

## 3. Các bước thực hiện

1. **csproj:** thêm
   `<Compile Include="..\..\orders\XuLyDonShopee.Core\Services\SanPhamDonParser.cs" Link="Shared\Notify\SanPhamDonParser.cs" />`
   cạnh dòng link `PhanLoaiExtractor.cs`. Build thử để chắc không kéo phụ thuộc lạ (chỉ cần `System.Text.Json`).
2. **`HubDatabase.Orders.cs` — `UpsertOrders`:**
   - Với mỗi đơn: xác định bản `items_json` + `item_count` sẽ ghi:
     - Đơn CHƯA tồn tại → ghi thẳng như hiện tại.
     - Đơn ĐÃ tồn tại (`exists`) → SELECT `items_json` cũ của `(shop_id, order_sn)`; tính
       `chon = SanPhamDonParser.ChonItemsJson(cuItemsJson, o.ItemsJson)`. Nếu `chon` == bản CŨ (bản mới nghèo bị
       loại) → giữ luôn `item_count` cũ; nếu `chon` == bản MỚI → dùng `o.ItemCount`.
   - Bind `$ij = chon`, `$ic = itemCountTuongUng`. Giữ nguyên mọi cột khác.
   - Lưu ý khoá `_gate` sẵn có: đọc bản cũ và ghi trong CÙNG một giao dịch/khoá, tránh đua giữa hai lượt push.
3. **Test** (nếu có project test cho hub, hoặc test thuần cho `ChonItemsJson` đã có ở client — bổ sung ca hub
   nếu khả thi): đẩy bản giàu rồi đẩy lại bản nghèo cùng `(shop_id, order_sn)` → hub GIỮ bản giàu; `item_count`
   theo bản giàu. Đẩy bản giàu HƠN lên bản cũ → cập nhật.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` sạch, 0 warning mới.
- [ ] Kịch bản: upsert đơn với `items_json` giàu (có `sku`/`phanLoai`) → sau đó upsert lại cùng đơn với
      `items_json` nghèo (chỉ `name/variation/amount/image`) → đọc lại DB: `items_json` VẪN giàu, `item_count`
      không bị đổi theo bản nghèo.
- [ ] Upsert bản giàu HƠN đè bản cũ nghèo → cập nhật đúng.
- [ ] Đơn mới (chưa tồn tại) → ghi như cũ, không hồi quy.

## 5. Rủi ro & lưu ý

- Thêm một lượt SELECT `items_json` cũ mỗi đơn ĐÃ tồn tại — chấp nhận được (lô push nhỏ), và phải nằm trong
  cùng khoá `_gate`/transaction để không đua.
- `ChonItemsJson` nhận biết "giàu" bằng `Parse` (có `sku` hoặc `phanLoai`), KHÔNG so chuỗi thô — JSON có thể
  đổi thứ tự khoá. Đúng như bản client.
- Thay đổi ở hub → chỉ có hiệu lực sau **deploy** (systemd `shopee-hub`).

---

## Báo cáo thực thi (Opus điền sau khi xong)
