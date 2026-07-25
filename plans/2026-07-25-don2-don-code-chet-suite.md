# Plan: Đợt 2 — Dọn code chết Suite desktop (shell + Core + modules)

- **Ngày:** 2026-07-25
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Plan cha:** `plans/2026-07-25-ke-hoach-refactor-toan-app.md` (mục 2B + 2C + 2D)
- **Điều kiện tiên quyết:** plan `2026-07-25-don1-sua-bug-core-suite.md` đã nghiệm thu + commit.

## 1. Bối cảnh & mục tiêu

App desktop giờ là client-only (`SettingsViewModel.cs:143-149` ép role "client"); hub nhúng đã xoá từ đợt don-dep-2 → `HubServerConfigStore.Shared.Current.Enabled` không còn đường nào bật. Nhiều khối code sống nhờ cờ đó là code chết. Review 2026-07-25 đã kiểm chứng từng mục dưới bằng Grep toàn repo (gồm .axaml). QUY TẮC: trước khi xoá từng khối, grep lại lần nữa (code lẫn .axaml/.xaml binding) — plan đợt-1 vừa merge có thể đã đổi vài dòng.

## 2. Phạm vi

- **Làm:** các khối xoá dưới, trong `suite/`.
- **Không làm:** KHÔNG đụng `orders/`, `server/`, `extensions/`; không hợp nhất trùng lặp (đợt 3); không tách class (đợt 4).

## 3. Các bước thực hiện

### Bước 1 — FleetViewModel: xoá nhánh hub-board + Search-đa-máy (~550 dòng)

`suite/Shopee.Suite/Modules/Fleet/FleetViewModel.cs`. `FleetView.axaml` hiện chỉ bind: `Rows/Status/Machines/ForceNextRun` (tab Theo dõi), `MyJobs/MyRole/Receive*/Interrupted*` (client), `Logs/ClearLogs` (Log); tab "Search (đa máy)" chỉ còn TextBlock thông báo (`FleetView.axaml:135-150`). Xoá:
- `IsHubBoard` (49); `MachineRows/Queue/SelectedQueue/PinOp/PinMachine/PinBusy/CancelBusy/ActionStatus` (54-97); `DispatchEnabled/DispatchButtonText/AutoMode/ManualMode/AutoSyncHandoff` (99-117).
- Khối Search đa máy: properties 119-151 + methods `ChooseSearchFileAsync/SelectAllLinks/UnselectAllLinks/RunSearchForClient/RecomputePartition/UpdateSearchRows/RefreshMerged/ExportMerged/ClearMerged` (538-773).
- Methods `SyncMachines/BuildQueue/OpCell/AssignManual/CancelSelected/SetLedger/StateOptions/ReconcilePinPending/ReconcileCancelPending/UpdateMachinePinnability` (219-536) — dọn cả các lời gọi trong `Refresh()` (nhánh gate Enabled không bao giờ chạy).
- 5 class: `FleetMachineRow` (1017-1061), `FleetQueueRow` (1064-1104), `FleetStateOption` (1108-1115), `FleetSearchClientRow` (1118-1145), `FleetSearchLinkRow` (1148-1159); cửa sổ `SearchExportFilterWindow` (caller duy nhất là `ExportMerged`).
- Ctor không tham số `FleetViewModel()` (191) — 0 caller.
- GIỮ: `PauseReceiving/ForceNextRun` và toàn bộ phần tab Theo dõi/client/Log đang bind.

### Bước 2 — Xoá cả class `HubDispatcher`

`suite/Shopee.Suite/Infrastructure/HubDispatcher.cs`: điểm khởi động duy nhất `ShellViewModel.cs:115` gate bằng `HubServerConfigStore...Enabled` (luôn false); consumer còn lại là phần FleetViewModel vừa xoá ở bước 1. Xoá class + dòng khởi động + using thừa. (Logic dispatcher đã có bản web ở `server/Shopee.Hub.Web` — không mất gì.)

### Bước 3 — AccountsViewModel: 2 khối chết (~200 dòng)

`suite/Shopee.Suite/Modules/Accounts/AccountsViewModel.cs`:
- Panel "Acc client báo lỗi": dòng 445-499 (`ClientErrorReports/RefreshReports/DeleteReportedAccount/DismissReport/IsHubMode/StartReportsPolling`) + class `ClientErrorRow` (677-686) — không binding nào trong .axaml; polling gate `IsHubMode` không bao giờ chạy.
- Flow "Kiểm tra & dọn tk lỗi": dòng 163-312 (`IsChecking/IsCheckIdle/_rng/CheckErrored` + `CheckErroredCommand`) — không bind, không caller. (Chức năng tương đương đi qua double-click `OpenForCheck` — GIỮ.)

### Bước 4 — UpdateProductViewModel: đường batch chết

`suite/Shopee.Suite/Modules/UpdateProduct/UpdateProductViewModel.cs`: xoá `SelectAllTargets/UnselectAllTargets` (129-133), `RunImport/RunUpdate/RunNameRewrite` batch (158-167), `RunWorkflowAsync` (339-401), `RunOneAsync` (478-497), `Stop` (499-506), `_cts/_runners/_runnersLock`, `BrowseVideoFolderAsync` (143-147), `OpenMapAsync` (150-156) + `ColumnMapWindow` (map cột giờ sửa inline trong BigSellerShopViewModel — xác nhận bằng grep trước khi xoá window). ⚠ GIỮ `IsRunning` (`WorkspaceViewModel.AnyRunning:49` đọc) và `BrowseImageCommand` (bind ở `WorkspaceView.axaml:307`) + `StopAllSingle` và mọi đường single per-shop.

### Bước 5 — ScrapeViewModel: lệnh mồ côi

`suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs`: xoá `SelectAllTargets/UnselectAllTargets` (103-107), `Run/Resume` commands (110-115 — GIỮ `StopCommand`, Shell/Workspace dùng), `ToggleAccountDuringRun` (619-641). Field `RunSession.Resume` (798, gán ở 169 nhưng không bao giờ đọc) — xoá. `PoolCount` (33) không bind — hạ xuống biến thường (LƯU Ý: `SearchViewModel.PoolCount` thì SỐNG, `AssignmentWorker.cs:254` đọc — đừng đụng).

### Bước 6 — Core: cụm hub-nhúng + helper mồ côi

`suite/Shopee.Core/`:
- `Coordination/ICoordinationHub.cs`: interface `IHubSync` (98-105), `ILeaseStore`, `ILedgerStore` (chỉ khai báo, 0 implement/caller); `Coordination/Coordination.cs`: `NoOpHubSync` (21-31) + property `Coordination.Sync` (39).
- `BigSeller/CookieFileHelper.cs`: `ParseCookiesRoot(string)` + `TryWriteCookieFile(...)` (602-638) — 0 caller (bản trùng WriteAtomic; LƯU Ý plan đợt-1 có thể đã thêm `TryWriteCookieFileBytes` vào BigSellerCookieEngine — không liên quan, vẫn xoá bản CookieFileHelper).
- `Scrape/ScrapeWorkbook.cs`: `ListSheets` (21-33) — UI dùng `WorkbookSheets.ListSheetNames`.
- `Infrastructure/SuitePaths.cs`: `RepoRoot` + `ResolveRepoRoot` (68-79), `ResolveHubRelative` (57-58).
- `Browser/BrowserLauncher.cs`: `DetectExePath` (28). `Browser/BraveFleet.cs`: `AutoMaxWindows` (73).
- `Coordination/HubRoutes.cs`: hằng `ProductsImportXlsx`/`ProductsExportXlsx` — endpoint phía hub đã xoá ở plan hub; grep 0 caller thì xoá.
- GIỮ: `UpdateProductUiSettings.OpenAiKeyFile` (legacy có chủ đích, đọc JSON cũ).

### Bước 7 — Modules: di sản API Python + lặt vặt

- `suite/Shopee.Module.MultiBrave/Engine/ApiNotRunningException.cs` (cả class, không được throw ở đâu) + nhánh catch nó trong `BraveInstanceSession.cs:502-511`.
- `MB/Engine/AppSession.cs:24` + `UP/Engine/AppSession.cs:22`: `ApiBase`; `UP/Engine/AppSession.cs:17`: `RepoRootDirectory` + `FindRepoRoot` (158-170).
- `suite/Shopee.Module.UpdateProduct/Engine/PortAllocator.cs`: `AllocateInstancePort`/`AllocateCookiePort` (26,30) + `_instancePorts`/`_cookiePorts` + 2 nhánh `Release` tương ứng.
- `suite/Shopee.Module.Search/Engine/SearchTaskStore.cs`: `GetShopProducts(long shopId)` (495-526).
- `MB/Engine/ExtensionRunnerAutomation.cs:1134`: biến local `payloadExpr` không dùng; `:1567-1574`: tham số `allowProfileFallback` không caller nào truyền false → bỏ tham số + nhánh chết.
- `suite/Shopee.Module.Search/SearchRunner.cs:150`: bỏ lời gọi `UpsertCategories` lặp (`SetShopProductCategories` dòng trên đã tự gọi — đọc xác nhận rồi xoá dòng 150).

### Bước 8 — Build + báo cáo

`dotnet build ShopeeSuite.sln` sạch. Báo cáo: danh sách symbol đã xoá theo khối, tổng số dòng giảm, các mục QUYẾT ĐỊNH GIỮ LẠI kèm lý do (nếu grep phát hiện caller mới).

## 4. Tiêu chí nghiệm thu

- [ ] Build sạch, 0 warning mới, không lỗi binding Avalonia khi mở app (Fable smoke test tab Fleet/Accounts/Workspace sau).
- [ ] Grep từng symbol đã xoá = 0 hit toàn repo (kể cả .axaml).
- [ ] `FleetViewModel.cs` còn ~450-650 dòng; `HubDispatcher.cs` không còn tồn tại.
- [ ] Các thứ phải GIỮ còn nguyên: `IsRunning` (UpdateProduct), `BrowseImageCommand`, `StopCommand` (Scrape), `SearchViewModel.PoolCount`, `PauseReceiving/ForceNextRun`, `OpenForCheck`.

## 5. Rủi ro & lưu ý

- Binding Avalonia là runtime — build xanh không bảo đảm; vì vậy tiêu chí "grep cả .axaml" là bắt buộc với TỪNG property/command xoá.
- Mỗi bước 1-7 giữ diff tách bạch (mỗi khối một commit riêng khi Fable commit).
- Nếu một symbol trong danh sách hoá ra CÓ caller sống (do plan đợt-1 vừa thêm) → GIỮ và ghi rõ trong báo cáo, không tự ý xoá caller.

---

## Báo cáo thực thi (Opus điền sau khi xong)
