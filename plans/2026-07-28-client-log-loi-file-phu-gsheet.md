# Plan: Client hiện cảnh báo khi ghi FILE PHỤ Google Sheet lỗi (`filePhu.loi`)

- **Ngày:** 2026-07-28
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Apps Script ghi song song sang **file Google Sheet thứ hai** và trả kết quả về trong response:

```
{ "results": [ ... ], "filePhu": { "ghi": <n>, "them": <n>, "loi": <chuỗi hoặc null> } }
```

Nhưng `orders/XuLyDonShopee.Core/Services/GoogleSheetSyncService.cs` → `DocKetQua` **chỉ đọc `results` và
`error`**, KHÔNG đọc `filePhu`. Hệ quả: nếu file phụ lỗi (mất quyền `openById`, ID sai, file bị xoá…) thì
client vẫn báo "GSheet: thêm N dòng" bình thường, `filePhu.loi` bị **nuốt im lặng** → người dùng tưởng đã ghi
file 2 mà thực ra không.

**Mục tiêu:** khi response có `filePhu.loi` khác null, client **log cảnh báo ra nhật ký** (qua callback `log`
sẵn có) để lỗi ghi file phụ hiện ra thay vì im lặng. KHÔNG làm hỏng đường ghi file chính (file phụ lỗi vẫn coi
lượt đẩy file chính là thành công — đúng thiết kế Apps Script).

## 2. Phạm vi

- **Làm:**
  - `GoogleSheetSyncService.cs` — trong `PushAsync`, sau khi parse `results`, đọc thêm `filePhu` từ cùng
    `respBody`; nếu `filePhu.loi` khác null/rỗng thì gọi `log(...)` một cảnh báo rõ (kèm ID/loi). Không đổi
    giá trị trả về (vẫn `IReadOnlyList<GsheetOrderResult>`), không ném vì lỗi file phụ.
- **Không làm:**
  - KHÔNG đổi hợp đồng `results` / cách xử đơn file chính.
  - KHÔNG khiến lượt đẩy THẤT BẠI chỉ vì file phụ lỗi (giữ `ok` của đơn file chính).
  - KHÔNG đụng Apps Script (đã trả `filePhu`).
  - KHÔNG commit / release.

## 3. Các bước thực hiện

1. **`GoogleSheetSyncService.cs`:**
   - Thêm hàm nội bộ `DocFilePhuLoi(string json)` (hoặc mở rộng `DocKetQua` trả kèm): parse `filePhu.loi`
     (string) từ response; thiếu `filePhu` / `loi` null → trả null. JSON rác → null (không ném; `DocKetQua` đã
     xử phần ném cho `results`).
   - Trong `PushAsync`, sau `all.AddRange(parsed)`: đọc `loiPhu = DocFilePhuLoi(respBody)`; nếu khác rỗng thì
     `log($"GSheet (file phụ): lỗi ghi — {loiPhu}")`. Đặt log ở CẤP LÔ (mỗi lô một lần), không nhân theo đơn.
2. **Test** trong `orders/XuLyDonShopee.Tests` (dùng lại `FakeGsheetWebApp` như `HubOutboxGsheetSheet2Tests`):
   - Response có `filePhu.loi = "..."` → `log` nhận đúng một dòng cảnh báo chứa nội dung lỗi; giá trị trả về
     (results) KHÔNG đổi, lượt đẩy vẫn `ThanhCong`.
   - Response `filePhu.loi = null` hoặc KHÔNG có `filePhu` → KHÔNG log cảnh báo file phụ (im lặng như cũ).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh; test cũ của GSheet giữ nguyên.
- [ ] Response giả có `filePhu.loi` → nhật ký có dòng cảnh báo; đơn file chính vẫn `ok`, lượt đẩy vẫn ThanhCong.
- [ ] Response không có `filePhu.loi` → không có cảnh báo thừa.

## 5. Rủi ro & lưu ý

- Chỉ là QUAN SÁT — tuyệt đối không để lỗi file phụ làm đơn file chính bị coi thất bại (kẻo giữ đơn chưa
  settled, đẩy lại vô hạn).
- Log ở cấp lô, tránh spam mỗi đơn một dòng.
- Nhỏ — nên GỘP vào cùng bản release với plan "hiển thị SKU từng sản phẩm" để chỉ release client một lần.

---

## Báo cáo thực thi (Opus điền sau khi xong)
