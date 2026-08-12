# Plan: Dọn nốt sau đợt 8 lỗi nặng — skip lên Hub (PB-4) + cào lại dòng bỏ (PB-11) + nhóm VỪA/NHẸ Scrape & Search

- **Ngày:** 2026-08-12 (chốt 2026-08-13)
- **Trạng thái:** hoàn thành (nghiệm thu 28/29 + phản biện 2 vòng; xem Báo cáo thực thi)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`, 3 đợt tuần tự A→B→C) + phiên chính (vá theo phản biện)

## 1. Bối cảnh & mục tiêu

Tiếp nối plan `2026-08-12-sua-loi-nang-scrape-search.md` (đã commit `1d2018f`): 8 lỗi nặng đã sửa, nhưng còn
(a) **một rủi ro mở** phản biện xếp ưu tiên cao: sổ dòng-bỏ-qua (SkippedRows) chỉ sống local, dòng bỏ qua vẫn
publish lên Hub như "đã xong" → máy KHÁC thấy "✔ Hoàn thành toàn bộ" không kèm "bỏ n dòng" — cross-machine vẫn
"báo thành công khi thiếu"; (b) chưa có đường nào cào lại dòng đã bỏ ngoài reset cả sheet; (c) toàn bộ nhóm
VỪA + một số mục NHẸ của lượt review đầu (phần "VỪA/NHẸ" trong plan trước).

### Kiến trúc liên quan (đọc trước khi sửa — người thực thi KHÔNG thấy hội thoại)

- **Ledger Hub:** DTO `WorkLedgerRecord` ở `suite/Shopee.Core/Coordination/ICoordinationHub.cs:56-79`
  (`Completed: List<RowRange>`, `LastRowReached`, `Status`, `MachineIds`…). Server lưu bảng `ledger`
  (`server/Shopee.Hub.Web/Data/HubDatabase.Ledger.cs`) cột `completed_json`, **gộp khoảng phía server** trong
  `PublishLedger`; `SetLedgerStatus(idle)` = XOÁ bản ghi; `AllLedger()` trả toàn bộ. Endpoint:
  `server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs:81-83` (`HubRoutes.Ledger` POST/GET, `LedgerSet`).
  Client: `suite/Shopee.Core/Coordination/HttpCoordinationHub.cs` — `PublishProgress` (:185, đẩy 1 khoảng),
  `TryPublish` (:199, có DiagLog throttle), `SyncIntoProgressAsync` (:~355, fold TOÀN BỘ 1 lần lúc mở app),
  `FoldScrapeLedgerAsync` (:379, fold 1 (acc,sheet) trước resume), `SetLedgerStatusAsync` (:215).
  Null-hub (chạy 1 máy): `Coordination.cs` có bản no-op. `HubClient.cs` + `HubRoutes.cs` là chỗ khai route.
  **Hub KHÔNG nằm trong ShopeeSuite.sln** — build riêng `dotnet build server/Shopee.Hub.Web`; test riêng
  `dotnet test server/Shopee.Hub.Web.Tests`. Hub tham chiếu Shopee.Core (DTO dùng chung).
- **Skip-ledger client (đã có từ plan trước):** `ScrapeProgressStore.SkippedRows` + `MarkSkipped(acc,sheet,row)`
  (dedup + merge [row..row] vào `Completed`); `ScrapeViewModel.cs` handler `runner.RowSkipped` (~:387) hiện
  `MarkSkipped` + `Coordination.Hub.PublishProgress(coordKey, row, row)`; `coordKey = new CoordKey(account.Id,
  shop.Id, sheet, CoordOp.Scrape)` (~:252). `RowRangeMath` (Merge/Normalize/Complement) ở
  `suite/Shopee.Core/Scrape/ScrapeProgressStore.cs:225+`, đã có test trong `Shopee.Core.Tests`.
- **Job scrape:** `ScrapeViewModel` + partial. `JobHandle` (`ScrapeViewModel.Session.cs:74-82`): Target/Seq/
  Cts/Runner/Task/Force — **không lưu sheet/shop của job**. `IsShopScraping` (`ScrapeViewModel.cs:459-466`)
  so `h.Target.SelectedShop` LIVE → mutable, bị `WorkspaceViewModel.PickShop`, `AssignmentWorker.CanLaunch:~285`
  (doc nói "KHÔNG side-effect" nhưng có set `SelectedShop`), `LaunchCore:~304` ghi đè giữa chừng.
  `AssignmentWorker.IsRunningLocally` (~:470) và `LaunchCore` (luôn `return true` dù `StartOneAccount` từ chối).
  `StopSingleAsync` (`ScrapeViewModel.cs:490-498`): `TryGet` → `h.Cts.Cancel()` đua với finally của
  `RunOneJobAsync` (`Jobs.Remove` rồi `Cts.Dispose`) → có thể `ObjectDisposedException` (bị nuốt thành
  "✖ Lỗi dừng scrape" sai). Hub-mode dò sheet bằng `Ordinal` tại `ScrapeViewModel.cs:271` và
  `ScrapeLinkSource.cs:39` trong khi mọi chỗ khác `OrdinalIgnoreCase`.
- **Video scrape:** `Engine/PageCdpHelper.cs:96-118` — 2 nhánh fallback (performance entries + document.scripts)
  trả `duration = null`. `LauncherRunnerLoop.cs:~299` chấp nhận `Duration is null or < 60` nhưng map
  `c.Duration ?? 0` sang `VideoCandidate(Url, double Duration, Label)`; `VideoDownloader.cs:19-25` lọc
  `Duration > 0 && Duration < 60` → **ứng viên fallback (null) LUÔN bị loại → cả đường fallback là code chết**,
  lỗi báo "Không có video ứng viên < 60s" sai sự thật.
- **Khối rỗng scrape:** `LauncherRunnerLoop.cs:82-88` — `items.Count == 0` (mọi dòng trong khối thiếu link cột A
  / tên SP cột F) → `throw InvalidOperationException` TRƯỚC 2 dòng log đếm skip → chunk Errored → stall → mỗi
  dòng tốn 3 lượt mở Brave mới bị bỏ, sheet 30 dòng trống ≈ 90 lượt mở Brave, không thấy lý do.
- **WorkAllocator.TryTake** (`ScrapeRunner.cs:562+`): khi cắt khối từ patch, phần DƯ thừa hưởng nguyên `stall`
  của patch → phần dư chưa kẹt lần nào chỉ cần 1 lần trượt là chạm ngưỡng bỏ dòng.
- **Nút "Tải lại"** (`ScrapeViewModel.Reload`, wire ở Workspace): không guard IsBusy — bấm giữa lúc chạy là
  `_targets.Rebuild()` làm job giữ VM cũ, UI bind VM mới → chip tiến độ đứng im tới hết phiên (trong khi
  `OnStoresChanged` đã cố tình `if (IsBusy) return;`).
- **Search:** `SearchViewModel.cs:147-158` gom link từ mọi file KHÔNG khử trùng → 2 lane cào song song cùng
  link (đè Excel, `_linkCts` ghi đè nhau). `_seenLinks` (`:44`) khử trùng TOÀN CỤC không reset giữa các lượt →
  SP trùng giữa 2 link chỉ hiện ở tab link đầu, chạy lại link thì tab đứng yên. `SearchSession.cs:178-187`:
  login fail → `Error` → `FileRunCoordinator` bỏ link luôn dù còn account rảnh (captcha/network thì có đổi).
  `SearchRunner.ExportSafe` (~:200) `catch { }` nuốt lỗi ghi Excel → catch "Lỗi lưu Excel" ở coordinator là
  code chết; `ExcelExporter.cs` Move fail để rác `.tmp`. Tên file per-link = slug (`CatLabel` cắt `-cat.<id>`)
  → 2 link khác id trùng slug ghi đè nhau. `AppSettingsService.Load()` (`AppSettingsService.cs:27`) KHÔNG ai
  gọi — `SearchRunner.cs:~29` chỉ `new AppSettingsService()` → `settings.json` của user vừa bị bỏ qua vừa bị
  `SaveSettings()` (BraveManager/ShopeeLoginService gọi) ghi đè bằng object mặc định. `extensions/shopee-search/
  extract.js` nhánh Try 4 (script tag) vẫn thiếu field `location` (Try 2 đã vá đợt trước).
  `SearchSession.cs:~91-174`: `PortAllocator.Reserve()` chỉ `Release` ở 2 nhánh — `CreateTask`/`new
  WebSocketServer().Start()` ném là rò chỗ giữ cổng + lane chết không trạng thái.
  `SearchTaskStore.SaveProduct` (:~83-95) mỗi SP = 1 transaction + `COUNT(*)` toàn bảng `task_products` dưới
  lock → O(n²), ack WS trễ, extension timeout 30s. `ExcelCategoryFile.cs:61` `_wb.Save()` ghi thẳng file user
  không tmp+rename. `FileRunCoordinator.StopLink`/`_skippedLinks`/`IsSkipped` — nghi code chết (grep xác minh
  lại trước khi xoá).
- **`ExtensionProgressReader.cs:25` `SheetNameRx`** `[a-zA-Z0-9_\sÀ-ɏ]+` không nhận `-` `.` `(` `)`
  → sheet "Data-Shop A" bị đọc cụt thành "Data" ở nhánh regex-fallback → guard S2 từ chối oan (hướng an toàn
  nhưng cào lại thừa).
- **Quy ước kiểm chứng:** app desktop có thể đang chạy trên máy này và khoá `suite/Shopee.Suite/bin` → build
  full-sln ở cây chính fail MSB3027. KHÔNG kill app. Kiểm chứng qua `git worktree add --detach <tmp> HEAD` +
  `git diff HEAD | git -C <tmp> apply` + chép file untracked; xong `git worktree remove`. Thử phá bằng
  PowerShell 5.1: dùng `[System.IO.File]::ReadAllText/WriteAllText` với UTF8 (KHÔNG `Get-Content`/`Set-Content`
  — phá encoding tiếng Việt); khôi phục xong phải cập nhật LastWriteTime (touch) kẻo MSBuild giữ DLL hỏng.

## 2. Phạm vi

- **Làm:** 3 đợt A/B/C dưới. Hub server ĐƯỢC PHÉP sửa trong plan này (khác plan trước).
- **Không làm:**
  - KHÔNG phát hành client (vpk) — user quyết sau. Deploy hub do PHIÊN CHÍNH làm ở chặng chốt (sau nghiệm thu
    + phản biện), không phải việc của opus-dev.
  - KHÔNG sửa `SaveProduct` sang mô hình batch/queue (đổi kiến trúc) — chỉ thêm index `task_products(task_id)`
    cho COUNT rẻ (đợt C); tối ưu sâu để đợt khác.
  - KHÔNG đổi ngữ nghĩa "dòng kẹt 3 lần = bỏ qua" (đánh đổi đã chốt) — chỉ THÊM đường cào lại chủ động (A3).
  - KHÔNG đụng `Shopee.Module.UpdateProduct`, `orders/`, BigSeller engine.

## 3. Các bước thực hiện

### Đợt A — Skip lên Hub + Cào lại dòng đã bỏ (PB-4 + PB-11)

A1. **DTO + server:** thêm `public List<int> Skipped { get; set; } = [];` vào `WorkLedgerRecord`.
    Server (`HubDatabase.Ledger.cs`): thêm cột `skipped_json` vào bảng `ledger` theo đúng pattern migration
    hiện có của HubDatabase (soát cách các cột được thêm trước đây — nếu chưa có pattern thì
    `ALTER TABLE ... ADD COLUMN` guard bằng pragma như `SearchTaskStore.AddColumnIfMissing`).
    `PublishLedger`: union `Skipped` (dedup + sort) như đang gộp `Completed`; `AllLedger` đọc trả về;
    `SetLedgerStatus(idle)` xoá bản ghi (đã vậy — skipped chết theo, đúng ý reset).
    **Tương thích ngược bắt buộc:** client cũ (v1.9.2) không gửi `Skipped` → server phải coi như rỗng và GIỮ
    skipped đã có (chỉ union, không thay thế); JSON có field lạ không được làm server/client ném.
A2. **Client publish + fold:** `ICoordinationHub` thêm `void PublishSkipped(CoordKey key, int row)`;
    `HttpCoordinationHub` hiện thực = `TryPublish(record { Completed = [row..row], Skipped = [row], Status =
    Running, ... })` (giữ nguyên việc dòng skip được TÍNH VÀO vùng phủ — không đổi ngữ nghĩa complement);
    bản no-op cho null-hub trong `Coordination.cs`. `ScrapeViewModel` handler `RowSkipped` đổi
    `PublishProgress(coordKey,row,row)` → `PublishSkipped(coordKey,row)`.
    `SyncIntoProgressAsync` + `FoldScrapeLedgerAsync`: sau khi fold `Completed`, fold `r.Skipped` →
    `ScrapeProgressStore.Shared.MarkSkipped(...)` (idempotent sẵn) — máy B hiển thị "bỏ n dòng" như máy A.
A3. **Cào lại dòng đã bỏ:**
    - `RowRangeMath.SubtractRows(IReadOnlyList<RowRange> ranges, IReadOnlyCollection<int> rows)` — pure, test.
    - `ScrapeProgressStore.ReopenSkipped(acc, sheet)`: `Completed = SubtractRows(Completed, SkippedRows)`;
      `SkippedRows.Clear()`; nếu Status == completed → stopped (để hiện "chưa xong" + resume nhặt lại);
      Save + Changed; trả về số dòng mở lại.
    - Hub: route mới `HubRoutes.LedgerReopenSkipped` (POST) + DTO `ReopenSkippedRequest(Key, BigsellerId,
      ShopId, Sheet, Op)`; server: đọc bản ghi, `completed = SubtractRows(completed, skipped)`, xoá skipped,
      status → stopped, ghi lại (bản ghi không tồn tại → Ok, không ném). `HubClient` + `HttpCoordinationHub.
      ReopenSkippedAsync(CoordKey)` — **trả về bool thành công** (KHÔNG nuốt lỗi im lặng).
    - UI: `ScrapeStatsViewModel`/`ScrapeStatsWindow.xaml`: mỗi dòng sheet có nút "Cào lại dòng đã bỏ (n)"
      (chỉ hiện khi n > 0): gọi hub reopen TRƯỚC (nếu có Hub) — hub fail thì CẢNH BÁO rõ trong Summary
      ("Hub không liên lạc được — chưa mở lại được trên Hub, lượt Tiếp tục có thể phủ lại các dòng này") và
      DỪNG (không sửa local nửa vời); hub OK (hoặc không có Hub) → `ReopenSkipped` local + Load() lại.
      Tra shop theo sheet như `ScrapeProgressReset` (mọi shop khớp, OrdinalIgnoreCase).
A4. **Test:** `Shopee.Core.Tests`: SubtractRows (giữa khoảng/đầu/cuối/nguyên khoảng 1 dòng/dòng không thuộc);
    ReopenSkipped (mở lại đúng dòng, complement thấy lại, status đổi, sổ sạch).
    `Shopee.Hub.Web.Tests`: PublishLedger union skipped (2 lần publish chồng nhau), record cũ không có
    skipped_json vẫn đọc được (migration), ReopenSkipped subtract + clear + stopped, SetLedgerStatus idle xoá
    sạch. **Thử phá từng test.**

### Đợt B — Scrape client (V1 V3 V4 V2 V6 + NHẸ)

B1. **V1+V3 — trạng thái job không đọc từ state mutable:**
    - `JobHandle` thêm `required string Sheet` + `required string ShopId` (chốt lúc đăng ký job).
    - `IsShopScraping` + `IsShopRunning` + mọi chỗ đang so `h.Target.SelectedShop` → so `h.Sheet`/`h.ShopId`
      (OrdinalIgnoreCase cho sheet).
    - Tách `Task<bool> TryStartSingleAsync(target, resume, silent, ...)`: validate + đăng ký job + phóng task
      nền, trả `true` CHỈ khi job thật sự được đăng ký (TryAdd thành công); `RunSingleAsync` giữ nguyên chữ ký
      cũ = gọi TryStart rồi await task job (mọi caller cũ không đổi). Bất biến TOTAL/fire-and-forget giữ nguyên.
    - `AssignmentWorker`: `CanLaunch` BỎ side-effect `SelectedShop` (đúng doc-comment); `LaunchCore` dùng
      TryStart — `false` → coi như launch FAIL (log lý do + để đường retry/requeue hiện có xử lý, KHÔNG đánh
      dấu "▶ Nhận"); `IsRunningLocally` so theo Sheet/ShopId của JobHandle.
B2. **V4:** `Reload` guard: đang chạy (IsBusy) → không rebuild, log 1 dòng "đang chạy — không tải lại".
    `WorkspaceViewModel.Reload` cũng chặn khi `AnyRunning` (log qua Status).
B3. **V2 — video fallback sống lại:** `VideoCandidate.Duration` → `double?`; `VideoDownloader` lọc
    `Duration is null || (>0 && <60)`; `LauncherRunnerLoop` bỏ `?? 0`, truyền null nguyên vẹn; thông điệp lỗi
    khi vẫn không tải được phải phân biệt "không có ứng viên" vs "tải fail". Soát mọi chỗ khởi tạo
    `VideoCandidate`.
B4. **V6 — khối toàn dòng không input:** `LauncherRunnerLoop` khi `items.Count == 0`: KHÔNG throw; log rõ số
    dòng bị lọc (2 dòng log đếm hiện có phải chạy TRƯỚC), set tiến độ = hết khối (dòng không input coi như
    phủ — nhất quán ngữ nghĩa FetchLinks-filter đã ghi ở docstring SkippedRows), kết thúc chunk bình thường.
B5. **NHẸ:** (i) `WorkAllocator.TryTake`: phần DƯ khi cắt patch nhận `stall = 0` (kèm comment vì sao);
    (ii) `StopSingleAsync`: bọc `h.Cts.Cancel()` trong try/catch `ObjectDisposedException` → coi như đã dừng,
    không báo "✖ Lỗi dừng scrape"; (iii) `ScrapeViewModel.cs:271` + `ScrapeLinkSource.cs:39`: `Ordinal` →
    `OrdinalIgnoreCase` khi so tên sheet; (iv) `SheetNameRx` nhận thêm `- . ( )` (mở char class, giữ nguyên
    phần còn lại).
B6. **Test:** ClaimFrame/JobHandle khó test UI — tối thiểu: test `SubtractRows` đã ở đợt A; thêm test pure cho
    TryTake stall-remainder nếu WorkAllocator test được (internal + InternalsVisibleTo về Shopee.Core.Tests
    nếu cần, hoặc tách hàm pure). Không test được bằng unit thì ghi rõ trong báo cáo mục nào chỉ được bảo vệ
    bằng đọc code.

### Đợt C — Search (VỪA + NHẸ)

C1. **Login fail → đổi account:** thêm outcome `LoginFailed` (SearchRunOutcome); `SearchSession` trả nó khi
    `EnsureLoggedInAsync` false; `FileRunCoordinator` xử như `CaptchaOrVerify`: `MarkErrored(account,
    "Đăng nhập thất bại", link)` + release (rest:false) + đổi account thử lại link (KHÔNG bỏ link).
C2. **Dedup link:** `SearchViewModel` trước khi dựng `items`: khử trùng theo Link (OrdinalIgnoreCase, giữ mục
    đầu), log "bỏ N link trùng giữa các file".
C3. **Excel:** (i) `SearchRunner.ExportSafe` bỏ `catch { }` → để exception nổi lên `SaveLinkExcel` (catch
    "Lỗi lưu Excel" ở coordinator sống lại); (ii) `ExcelExporter`: Move fail → xoá file `.tmp` rồi rethrow;
    (iii) tên file per-link thêm cat id: nhãn truyền cho `SaveLinkExcel` = `CatLabel(link)` + `"-" + CatId`
    khi CatId > 0 (hết 2 link trùng slug đè nhau; file tên cũ user tự xoá — ghi CHANGELOG khi phát hành);
    (iv) `ExcelCategoryFile.Save` → tmp + rename như `ExcelExporter`.
C4. **AppSettings:** mọi chỗ `new AppSettingsService()` phải gọi `Load()` ngay (grep toàn repo caller);
    kiểm lại `SaveSettings()` giờ ghi đè bằng settings ĐÃ load (hết mất cấu hình user).
C5. **`_seenLinks` per-tab:** dedup hiển thị chuyển về TỪNG TAB (mỗi `SearchFileTab` tự giữ tập link SP đã
    thấy): SP trùng giữa 2 link hiện ở CẢ 2 tab; chạy lại link trong cùng phiên không nhân đôi dòng trong
    tab; đóng tab mở lại → nạp lại đủ. `_all`/“Tổng phiên” giữ dedup toàn cục như cũ (chỉ để đếm).
C6. **NHẸ:** (i) `extensions/shopee-search/extract.js` Try 4 (script tag) thêm `location` (đối chiếu schema
    Try 1/2); (ii) `SearchSession`: rò `PortAllocator.Reserve` — bảo đảm Release trên MỌI đường ném giữa
    Reserve→Launch (try/catch quanh đoạn setup); (iii) `SearchTaskStore.Initialize` thêm
    `CREATE INDEX IF NOT EXISTS ix_task_products_task ON task_products(task_id)` (COUNT(*) per-save hết quét
    toàn bảng); (iv) xoá code chết `FileRunCoordinator.StopLink`/`_skippedLinks`/`IsSkipped` SAU khi grep xác
    minh 0 caller (kể cả XAML) — còn caller thì để nguyên + ghi báo cáo.
C7. **Test (Shopee.Module.Search.Tests):** dedup link (hàm tách được thì test hàm; không thì test qua
    coordinator items); ExcelExporter Move-fail dọn tmp (mở khoá file đích bằng FileShare.None rồi Export →
    ném + không còn *.tmp); index tồn tại (pragma) cho task_products; `AppSettingsService.Load` đọc đúng file
    có sẵn. **Thử phá từng test.**

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln -c Debug` **0 error 0 warning**; `dotnet build server/Shopee.Hub.Web -c
      Release` **0 error 0 warning**.
- [ ] `dotnet test` 4 project (Core.Tests, Module.Search.Tests, XuLyDonShopee.Tests, Shopee.Hub.Web.Tests)
      xanh 100%, tổng số test TĂNG (ghi số trước/sau từng project).
- [ ] **A:** `WorkLedgerRecord` có `Skipped`; server union + migration cột (test); fold 2 đường đều
      MarkSkipped; `RowSkipped` handler publish qua `PublishSkipped`; nút "Cào lại dòng đã bỏ (n)" trong
      cửa sổ Thống kê: hub-fail thì KHÔNG sửa local + cảnh báo (đọc diff xác nhận thứ tự).
- [ ] **B:** JobHandle có Sheet/ShopId, `IsShopScraping`/`IsRunningLocally` hết đọc `SelectedShop`;
      `CanLaunch` hết side-effect (grep `SelectedShop =` trong AssignmentWorker chỉ còn chỗ ngay trước
      TryStart); `LaunchCore` phân biệt được start-fail; `Reload` có guard; `VideoDownloader` nhận
      Duration null (đọc diff cả 3 file của chuỗi video); `items.Count == 0` không còn throw; TryTake phần dư
      stall=0; `Ordinal` hết ở 2 chỗ so sheet; SheetNameRx nhận "Data-Shop A" (nêu bằng chứng regex).
- [ ] **C:** outcome `LoginFailed` + nhánh xử lý đổi account (đọc diff); dedup link có log; `ExportSafe` hết
      nuốt; tên file per-link có cat id; mọi `new AppSettingsService()` có Load() kèm theo (grep); dedup
      hiển thị per-tab (đọc diff `SearchViewModel`/`SearchFileTab`); Try 4 có location; port không rò trên
      đường ném (đọc diff); index task_products (test); code chết đã xoá hoặc có lý do giữ.
- [ ] Mỗi test mới có ghi lại lượt THỬ PHÁ (phá gì → test nào đỏ → message).
- [ ] KHÔNG commit; không đụng file ngoài phạm vi (`orders/`, `Shopee.Module.UpdateProduct`, extension
      shopee-orders).

## 5. Rủi ro & lưu ý

- **Tương thích Hub:** client v1.9.2 ngoài kia sẽ nói chuyện với hub MỚI (deploy trước khi client mới phát
  hành). Field JSON mới phải optional 2 chiều; route mới client cũ không gọi. TUYỆT ĐỐI không đổi tên/xoá
  field hiện có của `WorkLedgerRecord`.
- **A3 thứ tự hub-trước-local:** làm ngược (local trước) mà hub fail thì fold sau đó phủ lại dòng vừa mở —
  thành no-op im lặng đúng kiểu S3 cũ. Giữ đúng thứ tự trong plan.
- **B1 là refactor đường nóng** (job đang chạy): giữ bất biến TOTAL của RunSingleAsync/StopSingleAsync
  (try/catch toàn thân, gỡ state trong finally) — đọc comment dài ở `WorkspaceViewModel.cs:304-310` trước khi
  đổi chữ ký. `TryStartSingleAsync` trả false KHÔNG được rò IsBusy/_wsJobs.
- **B4:** "coi dòng không input là phủ" đổi hành vi tiến độ — phải log RÕ số dòng + lý do ngay tại chunk, và
  KHÔNG đưa vào SkippedRows (đã chốt ở docstring: sổ bỏ qua chỉ dành cho dòng có input mà cào fail).
- **C5 đổi chỗ dedup hiển thị:** soát kỹ chỗ xoá `_seenLinks` khi đóng tab / "Xóa dữ liệu" (`SearchViewModel`
  ~:307) kẻo để rò bộ nhớ tập link cũ hoặc mất đường reset.
- Hub deploy (chặng chốt, phiên chính): theo CLAUDE.md repo — publish linux-x64 → scp `Shopee.Hub.Web.dll`
  lên `vps-muinx:/tmp/` → sudo backup + install vào `/opt/shopee-hub` + restart `shopee-hub` → check
  `curl 127.0.0.1:8088/health`. VM có sudo không mật khẩu. Deploy TRƯỚC khi user phát hành client mới.

---

## Báo cáo thực thi

### Thực thi (opus-dev, 3 đợt tuần tự; app đang chạy khoá bin cây chính → mọi số đo trong git worktree tách)

- **Đợt A (Hub skip-ledger + Cào lại dòng đã bỏ):** đúng plan. Lệch có chủ ý: phải thêm `SubtractRows` vào CẢ
  bản copy tay `server/Shopee.Hub.Web/Data/RowRanges.cs` (Hub không dùng file Core — file tự ghi nghĩa vụ đồng
  bộ tay, rủi ro drift đã ghi comment); `LastRowReached` CỐ Ý không hạ khi reopen (mốc "đã tới đâu", mọi quyết
  định resume đi qua Complement); 2 đường fold + handler publish không unit-test được (I/O thật). Test Core +10,
  Hub +7.
- **Đợt B (Scrape client):** đúng plan. `JobHandle` thêm cả `Shop` (object) ngoài Sheet/ShopId;
  `RunSingleAsync` giờ await ĐÚNG job của tk (trước await cả phiên) — mọi caller fire-and-forget nên vô hại;
  đổi chữ ký `CanDispatchUpdate`/`ValidateUpdateTarget` ở `UpdateProductViewModel` (trong Shopee.Suite, không
  đụng project cấm) để bỏ được side-effect ở CanLaunch. Test Core +4 (SplitPatch). Mục chỉ được bảo vệ bằng
  đọc code (không có mặt phẳng test WPF/CDP): toàn bộ B1, guard Reload, chuỗi video, khối rỗng,
  ObjectDisposedException, SheetNameRx.
- **Đợt C (Search):** đúng plan; mở rộng có chủ ý: dedup cả đường Hub giao (`SearchViewModel.Hub.cs`);
  `_linkCts` giữ nguyên (chỉ ghi, không ai đọc — dọn đợt khác); extract.js Try 4 vẫn thiếu rating/image
  (plan chỉ yêu cầu location). Test Search +15.

### Nghiệm thu: 28/29 ĐẠT

Tự build `--no-incremental` (sln + hub, 0 warning), tự dựng baseline, tự chạy đủ 4 project (2120 xanh, cả
XuLyDonShopee 1773), tự thử phá 4 lượt phủ 3 đợt — đỏ đúng chỗ. 1 đạt-một-phần: mục Báo cáo thực thi lúc đó
chưa điền. Phát hiện phụ: `JobHandle.ShopId` là field chết (đã xử ở vòng vá — xem dưới).

### Phản biện 2 vòng → phiên chính vá thêm 8 điểm (chốt 2026-08-13)

Vòng 2 (sau khi ghép A+B+C) — phản biện dựng worktree riêng, đọc rộng ra caller — tìm **2 NẶNG + 3 VỪA + NHẸ**,
phiên chính xác nhận từng cái vào code và vá:

1. **NẶNG N1 — LoginFailed vô hiệu hoá cả kho tk:** `ShopeeLoginService` trả false cả khi CDP/mạng/timeout
   (lỗi hạ tầng), mà nhánh mới `MarkErrored` = `Disabled=true` ghi accounts.json + báo Hub → 1 sự cố proxy
   đốt sạch 20 tk, module Scrape chết theo (kho = count(!Disabled)). Vá: BỎ MarkErrored, chỉ
   `ReleaseAccount(rest:true)` + đổi account thử lại.
2. **NẶNG N2 — nút Cào lại no-op cho dòng bỏ đời cũ + xoá luôn dấu:** cột skipped không backfill → dòng bỏ
   trước deploy có sổ hub rỗng; hub trả OK không khoét, client xoá sổ local, lượt fold phủ lại → mất dấu vĩnh
   viễn. Vá: request mang `Rows` (sổ local), hub union rồi mới khoét; test 4e.
3. **VỪA V1 — khối rỗng đóng dấu phủ không bằng chứng:** hub-mode 2 bộ đếm luôn 0, excel-mode startRow>total
   cũng 0/0 → coi phủ là mất dòng âm thầm (đúng lớp S1). Vá: chỉ phủ khi `SkippedMissing* > 0`; 0/0 → throw
   như cũ (vào đường vá + sổ bỏ qua).
4. **VỪA V2 — mở-nhiều-shop fail giữa chừng báo "chưa đổi gì" trong khi hub đã sửa:** vá gọi hết vòng, fail
   bất kỳ → giữ nguyên local + liệt kê shop; bấm lại an toàn (request mang rows).
5. **VỪA V3 — AppSettings vẫn còn đường mất cấu hình:** parse hỏng → về mặc định rồi SaveSettings đè file
   user. Vá: cờ `_loadFailed` cấm ghi + SaveSettings tmp+move. Test FileHong.
6. **NHẸ — SubtractRows treo Hub:** đi per-row với khoảng rác To=int.MaxValue = vòng lặp vô tận trong lock
   HubDatabase. Vá: cắt-quanh-dòng + `start` long chống tràn, sửa cả 2 bản. Test MaxValue.
7. **NHẸ — DedupLinks giữ link rỗng làm 1 mục việc:** vá loại hẳn rỗng/trắng. Test mới.
8. **NHẸ — JobHandle.ShopId field chết:** `IsShopScraping` so `h.ShopId==shop.Id` thay vì sheet (2 shop trùng
   sheet hết cùng nhấp nháy).

Vòng 2 cũng xác nhận đúng nhiều mục (SplitPatch, union skipped + tương thích client cũ, launched TCS không
đè/không treo, RunSingleAsync đổi ngữ nghĩa vô hại, port-leak fix idempotent, per-tab dedup, Reload guard,
Move-fail dọn tmp, StopOneAccount hẹp đúng ca).

### Phản biện soi lại 8 điểm vá → phiên chính vá tiếp 3 điểm (đều là hệ quả của chính vòng vá)

1. **VỪA — sổ SkippedRows local chỉ-ghi-thêm ("nút ma"):** vá N2 thêm đường gửi rows lên Hub nhưng KHÔNG có
   đường thu hồi → máy A cào lại xong dòng 50, máy B fold về vẫn hiện "bỏ 1 dòng" → bấm Cào lại gửi [50] →
   khoét NHẦM dòng đã xong khỏi vùng phủ Hub, hạ shop về "chưa xong", lan mọi máy. Vá: `UnmarkResolvedSkipped`
   (gỡ dòng Hub-đã-xong khỏi sổ local, gọi trong 2 đường fold, tích luỹ per-sheet để đúng cả ca 2 shop 1
   sheet). KHÔNG gộp vào MarkCompleted (DrainSkippedRows chạy trước RowsCompleted → gộp là xoá ngay dòng vừa
   ghi vào sổ). 2 test + thử phá 2 mutant.
2. **VỪA — đổi IsShopScraping sang ShopId đẻ hồi quy ở RecomputeResumePending:** hàm đó tra shop theo sheet
   (FirstOrDefault) rồi hỏi IsShopRunning → 2 shop cùng sheet, job chạy shop B thì shop A ra false → mục "còn
   dở" hiện oan giữa lúc đang chạy. Vá: thêm vị từ `IsSheetRunning` (so sheet) riêng, RecomputeResumePending
   dùng nó; IsShopRunning (so ShopId) giữ cho chip per-shop.
3. **VỪA — SaveSettings tmp+move ném ở ShopeeLoginService không bọc:** `File.Move` replace nhạy hơn
   WriteAllText (đích bị mở đọc/AV khoá là ném), mà gọi ngay trên đường login THÀNH CÔNG không try/catch →
   chết cả lane + biến login OK thành lỗi. Vá: bọc try/catch (nuốt + log như BraveManager) + phơi
   `LoadFailed` public. Test FileHong assert thêm LoadFailed.

Vòng soi lại cũng xác nhận đúng: bỏ MarkErrored ở LoginFailed (gốc đã tắt), V2 gọi-hết-vòng, SubtractRows 2
bản giống hệt + không treo/tràn, DedupLinks loại rỗng. Ghi nhận không sửa (đồng thuận): V1 hub-mode vẫn đi
đường throw (an toàn, chỉ chưa được lợi "khỏi đốt Brave" — cần API server trả số dòng lọc, đợt sau); nút Cào
lại không chặn khi đang chạy chính sheet đó (sau khi có UnmarkResolvedSkipped thì hết "nút ma", còn lại chỉ là
nhấp nháy hub, không mất dữ liệu).

### Giới hạn ghi nhận, KHÔNG sửa (quyết định có chủ đích)

- Dedup link giữ mục đầu → bản trùng ở file khác không được đánh dấu Processed (cào lại THỪA, không mất dữ
  liệu). Chấp nhận.
- `SheetNameRx` nới `.()-` chạy trên rác nhị phân LevelDB chỉ ở nhánh regex-fallback (JSON parse fail); guard
  `ProgressBelongsToThisBlock` chặn dùng sai. Không có test cho regex này.
- `AssignmentWorker` vẫn set `SelectedShop` ngay trước phóng việc, không hoàn lại khi TryStart false (UX nhỏ).
- File Excel per-link tên cũ (slug) không di trú sang tên mới (slug-catid) — ghi CHANGELOG.
- `SaveProduct` vẫn O(n²) theo số SP/task; chỉ thêm index, mô hình batch để đợt sau.

### Số kiểm chứng cuối (worktree tách `wt-final`; app user đang chạy khoá bin cây chính)

- `dotnet build ShopeeSuite.sln -c Debug`: **0 error 0 warning**; `dotnet build server/Shopee.Hub.Web -c
  Release`: **0 error 0 warning**.
- Test: **Core.Tests 168 · Module.Search.Tests 34 · Hub.Web.Tests 151 · XuLyDonShopee.Tests 1773** = 2126
  (trước plan này: 2084 — +42 test). Fail 0.
- Thử phá tổng: opus-dev (A 5 + B 3 + C 7) + nghiệm thu 4 + phiên chính 6 (vòng 2: 4 + soi-lại: 2) — đều đỏ
  đúng test.
- Việc kế tiếp: deploy hub lên VPS TRƯỚC khi phát hành client mới (cột skipped_json + route reopen); phát
  hành client do user quyết.
