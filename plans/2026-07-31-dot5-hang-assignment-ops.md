# Plan: Đợt 5 — hằng AssignmentOps/AssignmentStatus dùng chung client + hub

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh

Op giao việc (`scrape`, `update`, `search`, `orders`, `rewrite`, `import`…) và status assignment (`queued`, `claimed`, `running`, `done`, `failed`, `interrupted`…) đang là magic string rải ~54 chỗ/8 file phía suite + ~40 chỗ phía hub (đếm 25/07 — giờ có thể lệch sau refactor). Phía Core đã có `CoordOp` nhưng dùng lẫn literal. Hub KHÔNG ref project Core mà Compile-link từng file (mẫu: `HubRoutes.cs`, `MsLoginSelectors.cs` trong `server/Shopee.Hub.Web/Shopee.Hub.Web.csproj`).

## 2. Các việc

1. Kiểm kê thật: grep các literal op + status ở `suite/**` và `server/**` (cả trong SQL string). Liệt kê bảng trong báo cáo.
2. Chuẩn hoá về MỘT nguồn trong `suite/Shopee.Core/Coordination/` (mở rộng `CoordOp` hiện có + thêm `AssignmentStatus` — hoặc file mới `AssignmentConsts.cs` nếu gọn hơn; giá trị chuỗi GIỮ NGUYÊN từng byte — đây là hợp đồng wire + dữ liệu DB đang sống).
3. Hub: thêm Compile-link file hằng vào csproj (đúng khuôn sẵn có) + thay literal phía `server/**`.
4. Suite: thay literal phía `suite/**` (Core, Suite Infrastructure — AssignmentWorker/OrdersModuleHost partials, module VMs).
5. Chỗ nào literal nằm trong chuỗi SQL dài → dùng interpolation const (`$"... status = '{AssignmentStatus.Queued}' ..."`) chỉ khi KHÔNG làm SQL khó đọc hơn; nếu chuỗi SQL thuần tĩnh và rõ ràng, được phép giữ literal + thêm comment `// = AssignmentStatus.X` — ưu tiên an toàn hơn triệt để, nhưng mọi chỗ C# so sánh/gán biến thì PHẢI dùng const.

## 3. Phạm vi & nghiệm thu

- Khu: `suite/Shopee.Core/Coordination/**`, `suite/Shopee.Suite/Infrastructure/**`, các file suite có literal op/status, `server/**`. KHÔNG đụng `orders/**`, `extensions/**`, `shared/**`, `suite/Shopee.Module.Search|UpdateProduct/Engine` phần ngoài op-literal (2 agent khác đang chạy song song — nếu literal nằm đúng file họ sửa (`SearchTaskStore.cs`, `BigSellerProductUpdateRunner.cs`) thì BỎ QUA file đó + ghi lại để đợt chót).
- [ ] Build 2 solution 0/0; test orders 1471 + Core 61 + hub 44 giữ nguyên.
- [ ] Grep literal op/status trong so-sánh/gán C# = 0 ngoài file hằng (SQL tĩnh cho phép, có comment).
- [ ] Giá trị chuỗi không đổi byte nào (bảng đối chiếu).
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
