# Plan: Dọn hậu quả việc "số lượng vào phân loại" (v1.7.14/15)

- **Ngày:** 2026-08-04
- **Trạng thái:** đang làm
- **Người lập:** phiên chính · **Người thực thi:** phiên chính

## 1. Bối cảnh & mục tiêu

Audit độc lập tính năng "thêm số lượng vào phần phân loại" (commit `870cbf4` + `df55ec7`) cho kết quả: luồng
chính đúng yêu cầu, nhưng còn 4 hạng mục phải dọn. Đã tự kiểm chứng từng cái:

**(a) Đơn thử nghiệm lọt vào production.** Truy `%APPDATA%\XuLyDonShopee\app.db` và `hub.db` trên VM:

```
order_sn=TEST-SL-20260804114245  shop_login=alina99.store  status='Chờ lấy hàng'
gsheet_synced_at=2026-08-04T04:42:55Z   gsheet_tab='Tháng 08-2026'
hub_synced_at  =2026-08-04T04:42:45Z    (Hub cũng có đúng 1 dòng này)
```

Đơn giả này do `tools/PushTestOrderSl/` (chưa commit, đã build + đã chạy) bắn thẳng vào đường production để
verify. Nó **đã ghi vào Google Sheet thật** tab "Tháng 08-2026" và vào Hub.

**(b) App và GSheet lệch luật với sản phẩm KHÔNG có phân loại.**
`PhanLoaiExtractor.TuItemsJson` (app + hub) **bỏ qua** sản phẩm có phân loại rỗng, còn
`SanPhamDonParser.CotGsheet` vẫn gọi `GanSoLuong(pl, sp.SoLuong)` với `pl = ""` → ra `"SL: 1"`.

Hậu quả thật: đơn 1 sản phẩm không biến thể, SL=1 → app + hub hiện **trống**, nhưng GSheet nhận `"SL: 1"`.
Trước v1.7.14 chuỗi này rỗng nên `HubOutbox` gửi `null` và Apps Script **không đụng ô** (hợp đồng "chỉ điền ô
trống"); nay nó **ghi đè ô Phân loại người dùng có thể đã tự điền**.

**(c) Code chết sau `df55ec7`** (bỏ cột "Số lượng" khỏi lưới): property `OrderRowViewModel.SoLuong` không còn
binding nào (`grep SoLuong` trong `orders/**/*.xaml` = 0 kết quả), nhưng constructor vẫn gọi
`PhanLoaiExtractor.SoLuongTuItemsJson` ⇒ **thêm một lần `JsonDocument.Parse` cho MỖI dòng lưới**, kết quả vứt
đi — trái đúng ý comment ngay phía trên ("Parse items_json MỘT LẦN"). Đã xác nhận `SoLuongTuItemsJson` chỉ còn
được gọi bởi property chết đó + test + tool ở (a); hub không dùng.

**(d) Thay đổi hành vi không khai báo:** `870cbf4` gỡ luôn nhánh khử trùng lặp liên tiếp trong `TuItemsJson`.
Hợp lý khi có SL, nhưng khi thiếu `amount` thì 2 dòng giống hệt hiện thành `"Kem,36 · Kem,36"` (trước là
`"Kem,36"`). CHANGELOG v1.7.14 không nhắc.

## 2. Phạm vi

**Làm:**

- Xoá đơn `TEST-SL-20260804114245` khỏi Hub + DB local; xoá thư mục `tools/PushTestOrderSl/`.
- Sửa (b): SP không có phân loại → GSheet để **dòng trống**, KHÔNG gắn `SL: N`.
- Xoá code chết (c) + test tương ứng.
- Bổ sung CHANGELOG dòng khử trùng lặp (d).
- Test cho ca (b) + ca đối chiếu app vs GSheet.
- Release client v1.7.18.

**Không làm:**

- KHÔNG xoá dòng trên Google Sheet (không có quyền ghi sheet — **user tự xoá tay**).
- KHÔNG deploy lại Hub: `CotGsheet` chỉ được `HubOutbox` phía client gọi (đã grep), hub không dùng → hành vi
  hub không đổi.
- KHÔNG đụng `.kilo/worktrees/vivacious-monkey/` (worktree của agent khác, có thể còn việc dở).
- KHÔNG sửa các ca biên mức thấp audit nêu (`amount` dạng Number chỉ đi nửa đường; `DocSoTuChuoi` nuốt dấu âm /
  dấu phân cách; `GanSoLuong` không idempotent) — chưa nổ trong thực tế, để đợt refactor riêng.

## 3. Các bước thực hiện

### Bước 1 — Dọn dữ liệu test

- Hub: backup `hub.db` rồi `DELETE FROM orders WHERE order_sn='TEST-SL-20260804114245'` (đọc lại xác nhận
  đúng 1 dòng, các đơn thật còn nguyên).
- Local: đóng app trước (SQLite đang mở), `DELETE` cùng điều kiện trên `%APPDATA%\XuLyDonShopee\app.db`.
- Xoá `tools/PushTestOrderSl/` (chưa từng commit nên không mất lịch sử).

### Bước 2 — Sửa lệch app/GSheet (file `orders/XuLyDonShopee.Core/Services/SanPhamDonParser.cs`)

Trong `CotGsheet`, chỉ gắn số lượng khi phân loại KHÔNG rỗng:

```csharp
var pl = PhanLoaiExtractor.DonGian(sp.PhanLoai);
phanLoai.Add(pl.Length == 0 ? string.Empty : PhanLoaiExtractor.GanSoLuong(pl, sp.SoLuong));
```

**Bắt buộc để dòng TRỐNG chứ không bỏ phần tử** — hai cột Sku/PhanLoai nối bằng `"\n"` theo cùng vòng lặp,
bỏ phần tử là lệch cặp SKU ↔ Phân loại. Khi MỌI sản phẩm đều không có phân loại thì chuỗi ra toàn `"\n"` →
`HubOutbox` (`IsNullOrWhiteSpace`) gửi `null` → Apps Script không đụng ô, đúng như trước v1.7.14.

### Bước 3 — Xoá code chết

- `orders/XuLyDonShopee.App/ViewModels/OrderRowViewModel.cs`: bỏ property `SoLuong` + lời gọi ở constructor.
- `orders/XuLyDonShopee.Core/Services/PhanLoaiExtractor.cs`: xoá `SoLuongTuItemsJson`.
- Test: xoá `PhanLoaiExtractorTests.SoLuongTuItemsJson_NoiBangDauCham`; sửa `OrdersViewModelTests` bỏ assert
  `row.SoLuong` (giữ assert `PhanLoai`).

### Bước 4 — CHANGELOG

Thêm vào mục v1.7.18 dòng ghi rõ từ v1.7.14 phân loại **không còn khử trùng lặp** liên tiếp, và ghi việc sửa
GSheet ở Bước 2.

### Bước 5 — Test

`orders/XuLyDonShopee.Tests/SanPhamDonParserTests.cs`:

- SP có sku, KHÔNG có phân loại → `CotGsheet.PhanLoai` là chuỗi rỗng (không `SL: 1`).
- Đơn 2 SP, một có phân loại một không → đúng 2 dòng, dòng 2 trống, vẫn khớp cặp với cột SKU.
- Ca đối chiếu: cùng `items_json`, `TuItemsJson` và `CotGsheet` không mâu thuẫn (bên nào có `SL` thì bên kia
  cũng có; SP không phân loại thì cả hai đều không sinh `SL`).

### Bước 6 — Build + release

- `dotnet build ShopeeSuite.sln` 0 warning; `dotnet test` orders xanh.
- Bump `version.txt` → **1.7.18**, chạy `release-suite.cmd`, cập nhật bản cài local.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` → 0 warning, 0 error.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh; số test không giảm ngoài 1 test đã cố ý xoá.
- [ ] Test mới chứng minh SP không phân loại → GSheet **rỗng**, không còn `SL: 1`.
- [ ] Test chứng minh khớp cặp SKU ↔ Phân loại khi có SP thiếu phân loại ở giữa.
- [ ] `grep -rn "SoLuongTuItemsJson\|\.SoLuong" orders/ --include=*.cs` chỉ còn `sp.SoLuong` của parser.
- [ ] Hub + DB local không còn dòng `order_sn LIKE 'TEST%'`; đơn thật không giảm.
- [ ] `tools/PushTestOrderSl/` không còn trong cây.
- [ ] GitHub Releases có v1.7.18; bản cài local lên 1.7.18.

## 5. Rủi ro & lưu ý

- **Xoá dữ liệu production**: backup `hub.db` trước; `DELETE` có `WHERE order_sn=` chính xác, đếm dòng trước/sau.
- **Đóng app trước khi xoá DB local**, kẻo SQLite khoá hoặc app ghi đè lại.
- **Không được bỏ phần tử trong `CotGsheet`** — lệch cặp SKU/Phân loại là lỗi âm thầm, khó phát hiện trên sheet.
- Dòng `TEST-SL-...` trên Google Sheet **user phải tự xoá**; ghi rõ trong báo cáo cuối.

---

## Báo cáo thực thi (điền sau khi xong)
