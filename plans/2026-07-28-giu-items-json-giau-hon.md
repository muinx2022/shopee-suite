# Plan: `items_json` bị vòng sync sau ghi đè, mất SKU/phân loại trang chi tiết

- **Ngày:** 2026-07-28
- **Trạng thái:** hoàn thành — ca tái hiện đã kiểm FAIL trên code cũ (chuỗi khác nhau ở `variation`: bản nghèo có đuôi `[A141 A141]`) rồi PASS sau khi sửa. 1376 test xanh. Chờ build local + release.
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh — lỗi THẬT, quan sát trên dữ liệu production

Bước "đọc sản phẩm trang chi tiết" (`plans/2026-07-28-doc-san-pham-tung-don.md`) chạy đúng: sheet đã nhận SKU +
phân loại nhiều dòng cho đơn nhiều sản phẩm. **Nhưng dữ liệu đó bị xoá ở vòng sync kế tiếp.**

Hai lần chụp DB client cách nhau ~1,5 giờ, cùng một đơn:

```
08:17  260728TV9M95PA  khoá = [amount, donGia, image, name, phanLoai, sku, thanhTien, variation]
09:46  260728TV9M95PA  khoá = [amount, image, name, variation]                    ← MẤT donGia/phanLoai/sku/thanhTien
08:17  260728TV14FVU8  3 SP, đủ khoá chi tiết
09:46  260728TV14FVU8  3 SP, MẤT sạch khoá chi tiết (nhưng final_amount thì vừa lấy được: 904174)
```

Đếm toàn DB lúc 09:46: **0/21 đơn** còn dữ liệu sản phẩm, dù chiều nay sheet đã nhận đúng 3 dòng SKU.

### Nguyên nhân

`orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`, câu UPDATE của upsert (~dòng 174):

```sql
shopee_order_id = $shopeeId, buyer_username = $buyer, items_json = $items, item_count = $itemCount,
```

`items_json = $items` — **đè thẳng**. Trong khi mọi cột "đã lấy được" khác đều được bảo vệ:

```sql
final_amount    = COALESCE($finalAmount, final_amount),
final_amount_text = COALESCE($finalText, final_amount_text),
tracking_number = COALESCE($tracking, tracking_number),
shop_id         = COALESCE($shopId, shop_id),
```

Doc ngay trên đó đã lý luận rất kỹ cho `final_amount` / `tracking_number` ("lượt này không lấy được thì GIỮ,
KHÔNG ghi đè NULL làm mất dữ liệu") — **`items_json` đơn giản là bị bỏ sót**.

Vòng sync thường đọc **trang DANH SÁCH**, chỉ có `{name, variation, amount, image}`. Trang CHI TIẾT chỉ được mở
cho đơn **thiếu ước tính**. Nên khi đơn đã có ước tính rồi, nó không được mở lại nữa ⇒ bản `items_json` giàu bị
bản nghèo đè và **mất vĩnh viễn**.

Đây đúng lớp lỗi đã ghi trong ghi nhớ `push-once-flag-stale-state` ("quy tắc COALESCE cột đã-lấy-được").

## 2. Phạm vi

**Làm:** giữ lại bản `items_json` **giàu hơn** khi upsert; `item_count` đi theo bản được giữ.

**Không làm:**
- KHÔNG đổi hành vi các cột khác trong upsert.
- KHÔNG mở thêm trang chi tiết để vá đơn cũ (đơn thiếu ước tính vốn đã được lấy bù, sẽ tự có lại).
- KHÔNG thêm bảng/cột mới.
- KHÔNG commit, KHÔNG deploy, KHÔNG release. KHÔNG đụng `%LOCALAPPDATA%\Programs\ShopeeSuite`.

## 3. Các bước

### Bước 1 — Hàm THUẦN quyết định giữ bản nào

`orders/XuLyDonShopee.Core/Services/SanPhamDonParser.cs` (nơi đã có luật đọc `items_json`), thêm:

```csharp
/// <summary>Chọn bản items_json để LƯU khi upsert. Bản từ trang CHI TIẾT (có khoá sku/phanLoai) GIÀU hơn bản
/// quét ở trang DANH SÁCH — không được để bản nghèo đè bản giàu.</summary>
public static string? ChonItemsJson(string? cu, string? moi);
```

Luật:
- `moi` có dữ liệu chi tiết → lấy `moi` (dữ liệu mới nhất, luôn thắng).
- `moi` KHÔNG có mà `cu` CÓ → **giữ `cu`**. ← chính là chỗ vá.
- Cả hai đều không có → lấy `moi` (bản mới nhất từ danh sách; giữ hành vi cũ).
- `moi` rỗng/null → giữ `cu` (đừng xoá dữ liệu bằng rỗng).
- "Có dữ liệu chi tiết" = **có ít nhất một phần tử mang `sku` hoặc `phanLoai`** — dùng lại `Parse` sẵn có, đừng
  so chuỗi thô (JSON có thể đổi thứ tự khoá).

Test:
- [ ] cu giàu + moi nghèo → **giữ cu** (ca hồi quy của chính lỗi này, dùng đúng 2 chuỗi thật ở mục 1).
- [ ] cu nghèo + moi giàu → lấy moi.
- [ ] cả hai giàu → lấy moi.
- [ ] cả hai nghèo → lấy moi.
- [ ] moi null/rỗng/`"[]"` → giữ cu.
- [ ] cu null → lấy moi.
- [ ] JSON hỏng ở một bên → không ném, chọn bản đọc được.

### Bước 2 — Dùng trong upsert

`OrdersRepository`: câu SELECT đang tìm `existingId` — **đọc code trước**, bổ sung lấy luôn `items_json` (và
`item_count`) của dòng cũ. Rồi:

- `$items` = `SanPhamDonParser.ChonItemsJson(cu, moi)`.
- `$itemCount` phải **đi theo bản được giữ** — giữ `cu` thì cũng giữ `item_count` cũ, kẻo số nói dối.
- Nhánh INSERT không đổi (chưa có dòng cũ để giữ).

**Đừng** làm bằng `LIKE '%"sku"%'` trong SQL — so chuỗi thô trên JSON là mong manh, và luật đã có sẵn ở
`SanPhamDonParser.Parse`.

### Bước 3 — Test ở tầng repository

`orders/XuLyDonShopee.Tests/OrdersRepositoryTests.cs` (hoặc file mới nếu gọn hơn):
- [ ] Upsert đơn có `items_json` GIÀU → upsert lại **cùng đơn** với `items_json` NGHÈO → đọc lại DB thấy **vẫn
      giàu**, và `item_count` không đổi. Đây là ca tái hiện đúng lỗi production.
- [ ] Ngược lại (nghèo → giàu) → thành giàu.
- [ ] Không ảnh hưởng các cột khác (`final_amount`, `tracking_number` vẫn theo luật COALESCE cũ).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh, **không sửa kỳ vọng test cũ nào**.
- [ ] Ca tái hiện ở Bước 3 FAIL trên code hiện tại và PASS sau khi sửa — **nêu rõ trong báo cáo** đã kiểm điều này.

## 5. Rủi ro & lưu ý

- **Dữ liệu đã mất không tự quay lại** cho đơn đã có ước tính (không còn được mở trang chi tiết). Đơn còn thiếu
  ước tính thì nhóm "lấy bù" sẽ mở lại và có luôn sản phẩm. Chấp nhận — không mở thêm trang để vá.
- Cẩn thận `item_count`: giữ json cũ mà cập nhật count mới là số nói dối; hai thứ phải đi cùng nhau.
- Đây là lỗi của phần vừa làm hôm nay, **chưa release ra fleet** — chỉ bản local của người dùng dính. Sửa xong
  build lại local là sạch.

---

## Báo cáo thực thi (Opus điền sau khi xong)
