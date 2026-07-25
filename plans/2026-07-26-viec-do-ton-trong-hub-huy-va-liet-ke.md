# Plan: "Việc dở" tôn trọng Hub-hủy (Task 0) + liệt kê chi tiết (Task 1)

- **Ngày:** 2026-07-26
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Tiếp nối tính năng banner "việc dở" ở Workspace (plan 2026-07-25-huy-viec-do-workspace.md — đã xong: banner
+ nút Tiếp tục/Hủy). Người dùng yêu cầu thêm:

- **Task 0 — Hub hủy thì client BỎ luôn:** đúng tinh thần hub-client — Hub là nguồn sự thật. Khi người dùng
  hủy việc từ Hub, client KHÔNG được tiếp tục coi việc đó là "việc dở". (Chỉ khi client mất kết nối thì Hub mới
  giữ lại để máy khác làm — phần đó Hub đã có, không thuộc plan này.)
- **Task 1 — Liệt kê chi tiết việc dở:** hiện chỉ thấy CON SỐ (N việc) + tooltip; người dùng "không biết dở
  cái gì". Cần danh sách rõ từng mục: op · tài khoản · shop · tiến độ (bao nhiêu dòng/SP đã xong) · lần chạy cuối.

**Hiện trạng code (đã khảo sát kỹ):**
- `suite/Shopee.Suite/Modules/Workspace/WorkspaceViewModel.cs`:
  - `RecomputeResumePending()` (~333) gom `_resumePending` từ `ScrapeProgressStore.Shared.All()` (status
    running/stopped) + `OpProgressStore.Shared.Snapshot()` (import/update, running/stopped), LOẠI: acc/shop đã
    xoá, `HubManages(...)`, và đang-chạy-thật.
  - `HubManages(accId, shopId, op)` (~319-328): `fleet.Assignments.Any(Match && status is "queued" or "running")
    || fleet.Interrupted.Any(Match)`. Đọc `CoordinationRuntime.Hub?.CurrentFleet`. **KHÔNG** coi
    `canceled`/`dismissed` là hub-quản → đó chính là lý do việc bị Hub hủy vẫn trồi lên "việc dở".
  - `record ResumeItem(string Op, WorkspaceAccountViewModel Acct, BigSellerShop Shop)` (~295) — hiện KHÔNG mang
    tiến độ.
  - `RecomputeResumePending` được gọi ở: store `Changed` (`OnProgressStoresChanged`), `NotifyAnyRunning`,
    `Rebuild()`. **KHÔNG chạy theo nhịp cập nhật fleet** → hub-hủy không được nhận ra kịp.
- `ScrapeProgressStore` (Core/Scrape): `ScrapeProgress` có `AccountId, Sheet, Completed:List<RowRange>,
  LastRowReached, TotalRowsAtLastRun, Status, LastRunAt`. `Clear(accountId, sheet)` xoá + bắn `Changed`.
- `OpProgressStore` (Core/Progress): `OpProgress` có `AccountId, Sheet, Op, Done:Dictionary<string,string?>,
  Status, LastRunAt`. `Clear(accountId, sheet, op)` xoá + bắn `Changed`. `Snapshot()` trả (acc,sheet,op,status).
- `Assignment` (Core/Coordination/HubDtos.cs): có `Status` (queued|running|done|failed|canceled), `Dismissed`
  (bool), khớp (acc,shop,op) qua BigsellerId/ShopId/Op. `FleetSnapshot.Assignments` chứa cả bản đã kết thúc
  trong ~2h.
- `WorkspaceView.axaml` (~64-86): banner hiện `ResumePendingCount` + 2 nút; chi tiết chỉ ở `ResumeTooltip`.

## 2. Phạm vi

- **Làm:** Task 0 (client tự bỏ + Clear việc dở khi Hub đã hủy/dismiss op đó) + Task 1 (banner thành DANH SÁCH
  chi tiết từng việc dở).
- **KHÔNG làm:** Task 2 (giao sang máy khác) — plan riêng. Không đụng logic Hub/server. Không đổi cơ chế resume
  của việc HUB-GIAO (AssignmentWorker). Không đụng module khác.

## 3. Các bước thực hiện

### Bước 1 — Task 0: client tôn trọng Hub-hủy (`WorkspaceViewModel.cs`)

1. Thêm hàm phụ `HubCanceled(accId, shopId, op)` (cạnh `HubManages`): trả `true` nếu trong
   `fleet.Assignments` có mục khớp (acc,shop,op) mà `Status == "canceled"` HOẶC `Dismissed == true`, và KHÔNG
   có mục nào khớp đang `queued`/`running` (tức Hub đã chốt hủy, không phải vừa giao lại). Dùng cùng `Match`
   như `HubManages`.
2. Trong `RecomputeResumePending`, với MỖI ứng viên (cả nhánh scrape lẫn import/update), TRƯỚC khi add: nếu
   `HubCanceled(...)` → **KHÔNG add** và **Clear tiến độ local** tương ứng:
   - scrape → `ScrapeProgressStore.Shared.Clear(accId, sheet)`
   - import/update → `OpProgressStore.Shared.Clear(accId, sheet, op)`
   - (Clear bắn `Changed` → có thể tái nhập RecomputeResumePending; để tránh đệ quy khó chịu, gom danh sách cần
     clear vào 1 list rồi Clear SAU vòng quét, hoặc đặt cờ `_recomputing` bỏ qua Changed khi đang recompute.
     Người thực thi chọn cách an toàn, giải thích trong báo cáo.)
   - An toàn khi offline: nếu mất kết nối Hub thì `fleet.Assignments` rỗng/cũ → `HubCanceled` = false → KHÔNG
     xoá nhầm (chỉ xoá khi THẤY RÕ Hub đã hủy).
3. **Trigger theo fleet:** đảm bảo `RecomputeResumePending` được gọi lại KHI fleet cập nhật (để nhận ra hub-hủy
   kịp thời, không phải đợi đổi store). Khảo sát `CoordinationRuntime.Hub` / `HttpCoordinationHub` xem có event
   "fleet changed" để subscribe không; nếu không có, thêm một nhịp nhẹ (vd hook vào chỗ VM đã nhận cập nhật
   fleet sẵn, hoặc timer nhẹ) — nêu rõ cách chọn trong báo cáo. TRÁNH gọi quá dày gây Clear liên tục.

### Bước 2 — Task 1: danh sách chi tiết (`WorkspaceViewModel.cs` + `WorkspaceView.axaml`)

1. Tạo lớp hiển thị `ResumePendingRow` (ObservableObject hoặc record hiển thị) với:
   `OpLabel` (Scrape/Import/Update/Tên SP), `AccountName`, `ShopName`, `ProgressText`, `LastRunText`.
   - `ProgressText`:
     - scrape: từ `ScrapeProgress` → vd `"đã cào {LastRowReached}/{TotalRowsAtLastRun} dòng"` (nếu Total=0 thì
       `"đã cào tới dòng {LastRowReached}"`).
     - import/update: từ `OpProgress.Done.Count` → vd `"đã xong {n} SP"`.
   - `LastRunText`: từ `LastRunAt` (định dạng ngắn `HH:mm dd/MM`; null → "").
2. Đổi `_resumePending` để giữ đủ dữ liệu dựng row (mở rộng `ResumeItem` thêm tham chiếu bản ghi progress, HOẶC
   dựng thêm `ObservableCollection<ResumePendingRow> ResumeRows` cập nhật cùng lúc trong `RecomputeResumePending`).
   Giữ nguyên `ResumePendingCount`/`HasResumePending`/lệnh Tiếp tục/Hủy.
3. `WorkspaceView.axaml` — trong banner (khi `HasResumePending`): thay dòng chữ đếm bằng:
   - 1 dòng tiêu đề: "Có {ResumePendingCount} việc đang dở dang từ lần trước:"
   - 1 `ItemsControl ItemsSource="{Binding ResumeRows}"` liệt kê mỗi việc 1 dòng: `[OpLabel]` · tài khoản ·
     shop · tiến độ · (giờ chạy cuối). Gọn, cỡ chữ caption, có thể cuộn nếu nhiều (giới hạn chiều cao ~160,
     `ScrollViewer`).
   - Giữ 2 nút Tiếp tục tất cả (xanh) / Hủy bỏ (đỏ) như hiện tại.
   - (Chừa chỗ: mỗi dòng sau này Task 2 sẽ thêm nút "Giao máy khác" — không cần làm ở plan này, nhưng dựng layout
     mỗi việc 1 dòng để dễ chèn.)

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` toàn solution 0 error; `dotnet test` XuLyDonShopee.Tests xanh.
- [ ] Banner liệt kê từng việc dở: op · tài khoản · shop · tiến độ (dòng/SP) · giờ chạy cuối — không còn chỉ mỗi con số.
- [ ] Khi Hub đã hủy 1 op (assignment canceled/dismissed) mà máy này có tiến độ dở tương ứng: sau khi client
      nhận fleet mới, việc đó **tự biến mất** khỏi banner và tiến độ local bị Clear (kiểm bằng cách: tạo tiến độ
      dở → hủy ở Hub → chờ client cập nhật fleet → banner bớt đúng mục đó).
- [ ] Offline Hub (không lấy được fleet) → KHÔNG xoá nhầm việc dở nào.
- [ ] Chỉ đụng `WorkspaceViewModel.cs` + `WorkspaceView.axaml` (+ lớp row nếu tách file trong cùng thư mục Workspace).

## 5. Rủi ro & lưu ý

- **Đệ quy Clear→Changed→Recompute:** xử lý bằng gom-rồi-clear-sau hoặc cờ `_recomputing` (bắt buộc, nêu cách chọn).
- **Chỉ xoá khi THẤY RÕ Hub hủy** (có assignment canceled/dismissed trong fleet). Không suy diễn từ "vắng mặt".
- **Trigger fleet:** không gọi RecomputeResumePending quá dày (mỗi 12s poll là đủ). Nếu subscribe được event thì tốt.
- Việc chạy-tay THUẦN (chưa từng có assignment ở Hub) KHÔNG bị Task 0 đụng (không có assignment canceled để khớp)
  → vẫn phải dùng nút "Hủy bỏ" thủ công. Đây là hành vi ĐÚNG (Hub chưa từng quản việc đó).
- Bất biến resume (memory resume-interrupted-tasks): KHÔNG đụng resume-mine/AssignmentWorker; chỉ thao tác phía
  hiển thị việc-chạy-tay + Clear 2 store local.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa thực thi>
