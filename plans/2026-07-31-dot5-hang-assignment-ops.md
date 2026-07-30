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

### Nguồn hằng (mới)

`suite/Shopee.Core/Coordination/AssignmentConsts.cs` — 4 lớp, giá trị chuỗi GIỮ NGUYÊN từng byte:

| Lớp | Hằng → giá trị | Nơi dùng |
|---|---|---|
| `AssignmentOps` | `Scrape="scrape"` `Import="import"` `Update="update"` `Rewrite="rewrite"` `Search="search"` `Orders="orders"` | `Assignment.Op`, đuôi khoá lease/ledger |
| `AssignmentStatus` | `Queued="queued"` `Running="running"` `Done="done"` `Failed="failed"` `Canceled="canceled"` `Requeue="requeue"` | `Assignment.Status` (Requeue = động từ wire, không lưu DB) |
| `LedgerStatus` | `Idle="idle"` `Running="running"` `Stopped="stopped"` `Completed="completed"` | `WorkLedgerRecord.Status` + `ScrapeProgress/OpProgress.Status` |
| `LeaseStatus` | `Running="running"` `Finishing="finishing"` `Released="released"` | `LeaseRecord.Status` |

Link vào hub: `server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` (khuôn `HubDtos.cs`).

### Nghiệm thu

- [x] Build 2 solution `--no-incremental`: **0 warning / 0 error** cả hai.
- [x] Test: orders **1471**, Core **61**, hub **44** — khớp baseline, 0 fail.
- [x] Giá trị chuỗi không đổi byte nào — **kiểm chứng máy**, không phải đọc mắt: 23 câu SQL (bản TRƯỚC khi sửa) + 16 từ op/status đều tìm thấy nguyên văn trong `Shopee.Hub.Web.dll` sau build (Roslyn gấp hằng nội suy `$"…{const}…"` thành literal duy nhất) → mọi chỗ tách `"A " + $"B"` ghép lại đúng từng ký tự.
- [x] Grep literal op/status trong so-sánh/gán C# = 0 ngoài file hằng, TRỪ `OpLanes.cs` (xem "điểm lệch").

### Điểm lệch so với spec

1. **`OpLanes.RequiredBraves` GIỮ literal** (đã revert + thêm comment `// = AssignmentOps.X`). Lý do: `OpLanes.cs` được Compile-link vào `orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` (khuôn "thuần BCL"); dùng `AssignmentOps` làm vỡ build project đó, mà `orders/**` thuộc vùng cấm của đợt này. Muốn triệt để: thêm 1 dòng `<Compile Include="..\..\suite\Shopee.Core\Coordination\AssignmentConsts.cs" …>` vào csproj đó ở đợt sau.
2. **File bị loại vì agent khác đang sửa** — còn literal, để đợt chót: `suite/Shopee.Module.UpdateProduct/Engine/BigSellerProductUpdateRunner.cs:429` (`OpProgressStore.Shared.MarkDone(…, "update", …)`) — chỗ DUY NHẤT còn sót thuộc phạm vi. (`SearchTaskStore.cs`, `HotmailOtpReader.cs`, `orders/**`, `shared/**`, file MB: kiểm tra lại — KHÔNG chứa literal op/status assignment nào.)
3. **Ngoài phạm vi — value space KHÁC, cố ý không đụng**: trạng thái login BigSeller trên hub (`idle|running|needsOtp|success|failed`), `RewriteState.Status` (việc rewrite hub), `AccountError.Status` (`captcha|failed`), `RunnerPhase` của MultiBrave, `OrdersSessionStates`/`OrdersCommandStatuses` (đã là hằng sẵn), tên lớp CSS trùng chữ (`"run"`, `"done"`, `"warn"`, `"idle"`, `"fail"`), khoá tab/KPI nằm trong URL (`DispatchViewLogic.KpiCards`, `_tab == "orders"`), bảng DB tên `orders`, thư mục module `ModuleDir("search")`, và động từ tiếng Việt trong `WorkspaceShopViewModel.ToggleTip("scrape", …)` (là chữ hiển thị "Đang scrape — bấm để dừng", không phải khoá op).

### Thay đổi kèm theo (không thuần thay literal)

- `FleetRowsBuilder`: đổi tên method private `LedgerStatus(...)` → `LedgerCell(...)` — BẮT BUỘC, vì trùng tên lớp hằng mới thì `LedgerStatus.Completed` trong chính file đó không phân giải được.
- `MachineRoles.Scrape/Import/Update/Search` giờ trỏ thẳng `AssignmentOps.*` (giá trị y hệt) — chỗ nào so sánh `Assignment.Op` mà trước đây mượn `MachineRoles.Search` đã sửa về `AssignmentOps.Search`.
- Thêm `using Shopee.Core.Coordination;` vào 5 file (2 progress store Core, `ScrapeStatsViewModel`, `ScrapeTargetViewModel`, `UpdateProductRunner`).
