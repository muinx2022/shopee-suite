# Plan: Chế độ ứng dụng (Full / Workspace / Shopee) — gọn app theo cấu hình

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nhánh:** `feature/che-do-app` (worktree `d:\Projects\shopee-suite-wt-chedo`)

## 1. Bối cảnh & mục tiêu

App là MỘT exe duy nhất `Shopee.Suite` (`ShopeeSuite.exe`) — vỏ ribbon 4 tab: Workspace · Cấu hình
BigSeller · Shopee (đơn hàng) · Cài đặt. `ShellViewModel` (ctor) dựng toàn bộ module VM + tab; `App.axaml.cs`
`OnFrameworkInitializationCompleted` khởi tạo các engine rồi tạo `MainWindow { DataContext = new ShellViewModel() }`.

**Yêu cầu người dùng:** KHÔNG tách thành nhiều bản build. Vẫn build/cập nhật **một bản Full**, nhưng thêm
**cấu hình "Chế độ ứng dụng"** để mỗi máy chỉ hiện module cần dùng cho **gọn**:
- **Full**: tất cả (Workspace + Cấu hình BigSeller + Shopee + Cài đặt).
- **Workspace**: Workspace + Cấu hình BigSeller + Cài đặt (ẩn Shopee/đơn hàng).
- **Shopee**: Shopee + Cài đặt (ẩn Workspace + Cấu hình BigSeller).

**Quyết định đã chốt với người dùng:**
- Chọn chế độ **trong tab Cài đặt**; đổi xong **yêu cầu restart app ngay** để áp dụng (không hot-swap).
- Lần đầu (chưa cấu hình) **mặc định Full**.
- Config lưu **riêng, không bị xóa khi update**.

**Hạ tầng có sẵn (đã khảo sát):**
- `SuitePaths.Root` = `%AppData%\ShopeeSuite` (Shopee.Core/Infrastructure/SuitePaths.cs) — NGOÀI thư mục bản
  cài Velopack ⇒ update KHÔNG xóa. `RootFile(name)` cho file ngay dưới Root. → nơi lưu config chế độ.
- `ShellViewModel` ctor: dựng VM (bigSeller/scrape/update/search/workspace/data/accounts/worker/fleet +
  ordersVm qua `OrdersModuleHost.TryCreate()`), rồi `Tabs.Add(...)` (dòng ~209–212). Đã có sẵn nhánh
  `if (ordersTab is not null)` → ẩn tab Shopee khi module null.
- `App.axaml.cs`: init `MultiBraveRuntime`, `UpdateProductRuntime`, `BraveFleet`, `StartupJanitor`,
  `CoordinationRuntime`, `UpdateService.CheckAsync` (auto-update), rồi tạo ShellViewModel.
- `UnifiedSettingsViewModel` (Modules/Settings): wrapper mỏng giữ `Suite` (SettingsViewModel) + `Orders`
  (đơn hàng, nullable) — chỗ đặt selector chế độ (luôn hiện ở mọi chế độ).
- `UpdateService.ApplyAndRestart()` dùng Velopack cho bản cập nhật; restart-thường (áp chế độ) cần relaunch
  exe hiện tại.

## 2. Phạm vi

- **Làm:** store cấu hình chế độ (bền vững), gate dựng module + tab theo chế độ ở `ShellViewModel`, gate
  init engine workspace theo chế độ ở `App.axaml.cs`, selector chế độ + nút "Lưu & khởi động lại" trong Cài
  đặt, helper restart. Toàn bộ ở `suite/`.
- **Không làm:**
  - KHÔNG tách bản build / KHÔNG đụng pipeline phát hành Velopack (update vẫn Full).
  - KHÔNG đụng logic nghiệp vụ các module (chỉ gate dựng/hiện).
  - KHÔNG đụng module `orders/` nội bộ, KHÔNG đụng nhánh/worktree khác.
  - KHÔNG hot-swap chế độ (đổi = restart).

## 3. Các bước thực hiện

### Bước 1 — Store cấu hình chế độ (Shopee.Core, file mới)

`suite/Shopee.Core/Infrastructure/AppModeStore.cs`:
- `enum AppMode { Full, Workspace, Shopee }`.
- Singleton `AppModeStore.Shared` (mẫu như `PerformanceSettingsStore.Shared` — đối chiếu file đó để theo
  đúng phong cách load/save + JSON).
- Lưu JSON tại `SuitePaths.RootFile("app-mode.json")` dạng `{ "mode": "Full" }`. Đọc lỗi/thiếu/không hợp lệ
  → `AppMode.Full` (mặc định an toàn). `Current` (đọc), `Save(AppMode)` (ghi, cập nhật Current).
- Thuần I/O + parse — không phụ thuộc Avalonia (để dễ dùng ở cả App.axaml lẫn ShellViewModel).

### Bước 2 — Helper "chế độ nào hiện gì" (thuần, dễ đọc)

Trong `AppModeStore.cs` (hoặc file cạnh): 2 hàm thuần tĩnh cho mọi nơi dùng CHUNG một nguồn sự thật:
- `static bool ShowsWorkspace(AppMode m) => m is AppMode.Full or AppMode.Workspace;`
- `static bool ShowsShopee(AppMode m) => m is AppMode.Full or AppMode.Shopee;`
(Workspace mode gồm cả Cấu hình BigSeller — coi chung nhóm "workspace".)

### Bước 3 — Gate dựng module + tab ở `ShellViewModel` (suite/Shopee.Suite/ViewModels/ShellViewModel.cs)

- Đầu ctor: `var mode = AppModeStore.Shared.Current;` `bool ws = AppModeStore.ShowsWorkspace(mode);`
  `bool sp = AppModeStore.ShowsShopee(mode);`
- **Chỉ dựng VM khi cần** (gọn RAM + không chạy nền thừa):
  - Khối workspace (`bigSeller, scrape, update, search, workspace, data, accounts, worker, fleet` +
    `HubDispatcher.Start()`) → chỉ khi `ws`. Khi `!ws`, để các biến `null` (đổi kiểu sang nullable cục bộ) và
    KHÔNG start dispatcher/worker.
  - `ordersVm = sp ? OrdersModuleHost.TryCreate() : null;`
- **Tab**: `Tabs.Add(workspaceTab)` + `Tabs.Add(bigSellerTab)` chỉ khi `ws` (và chỉ dựng 2 tab đó khi `ws`);
  `ordersTab` giữ nhánh `if (ordersTab is not null)` sẵn có (đã tự ẩn khi `!sp` vì ordersVm null); `settingsTab`
  LUÔN thêm. Đối chiếu đoạn dựng `workspaceTab`/`bigSellerTab`/`ordersTab` (dòng ~99–177) — bọc theo cờ.
- `unifiedSettings = new UnifiedSettingsViewModel(settings, ordersVm?.SettingsVm)` giữ nguyên (orders null ⇒
  section đơn hàng tự ẩn).
- **SelectedTab mặc định**: hiện `ordersTab ?? workspaceTab`. Sửa an toàn: chọn tab đầu tiên có trong `Tabs`
  ưu tiên Shopee → Workspace → (settings nếu chỉ còn nó). Không để null.
- **`PrepareShutdownAsync`** (dòng ~84–92) tham chiếu `update/scrape/search/worker` → **guard null** từng cái
  (chỉ gọi khi đã dựng). `OrdersModuleHost.StopAsync()` giữ (static, no-op nếu module chưa tạo — kiểm nhanh).
- Giữ `RequestNavigate`/`workspace.RequestNavigate` chỉ khi `ws` (workspace null thì bỏ dòng đó).

### Bước 4 — Gate init engine workspace ở `App.axaml.cs`

Đọc `var mode = AppModeStore.Shared.Current;` ngay đầu `OnFrameworkInitializationCompleted` (sau khối
try/catch logger). **Chỉ khi `ShowsWorkspace(mode)`** mới init các engine workspace: `MultiBraveRuntime.Initialize()`,
`UpdateProductRuntime.Initialize()`, khối `BraveFleet` (MaxConcurrentWindows/JobLimits/StartupSweep/Maintenance).
GIỮ LUÔN CHẠY (mọi chế độ): lưới bắt lỗi (UiThread/AppDomain/Task), `StartupJanitor`, `CoordinationRuntime.InitFromConfig`
(NoOp khi chưa cấu hình — rẻ), `HttpCoordinationHub.DiagLog`, **`UpdateService.Shared.CheckAsync()` (auto-update phải luôn chạy)**.
- `desktop.ShutdownRequested` (dòng ~68–75): `MultiBraveRuntime.Cleanup()` chỉ gọi khi `ShowsWorkspace(mode)`
  (đã init mới cleanup); `OrdersModuleHost.StopAsync()` + `BigSellerStore.Save()` giữ (an toàn/gọn).
- Mỗi thay đổi vẫn nằm trong try/catch sẵn có — boot được cả khi một phần lỗi.

### Bước 5 — Selector chế độ + restart trong Cài đặt

- `UnifiedSettingsViewModel.cs`: chuyển thành `ObservableObject` (CommunityToolkit.Mvvm) — thêm:
  - `AppMode SelectedMode` (khởi tạo = `AppModeStore.Shared.Current`).
  - Danh sách chế độ để bind ComboBox (Full/Workspace/Shopee) + nhãn tiếng Việt ("Đầy đủ"/"Chỉ Workspace"/"Chỉ Shopee").
  - `[RelayCommand] SaveModeAndRestart()`: nếu `SelectedMode != AppModeStore.Shared.Current` → `AppModeStore.Shared.Save(SelectedMode)`
    → hỏi xác nhận (Dialogs.Confirm nếu có; không thì thông báo) → `AppRestart.Restart()`. Nếu không đổi → thông báo "Chế độ không đổi".
  - Giữ `Suite`/`Orders`/`HasOrders` như cũ.
- `UnifiedSettingsView.axaml`: thêm **section TRÊN CÙNG "Chế độ ứng dụng"** (LUÔN hiện, mọi chế độ): ComboBox
  chọn chế độ + nút "Lưu & khởi động lại" (bind `SaveModeAndRestartCommand`) + dòng chú thích "Đổi chế độ sẽ
  khởi động lại app; cập nhật vẫn tải bản đầy đủ." Đặt trước 2 section Suite/Đơn hàng hiện có.
- Helper restart `suite/Shopee.Suite/Services/AppRestart.cs`:
  - `static void Restart()`: relaunch exe hiện tại rồi thoát. `var exe = Environment.ProcessPath;`
    `if (!string.IsNullOrEmpty(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });`
    rồi đóng app qua `IClassicDesktopStyleApplicationLifetime.Shutdown()` (nếu lấy được) hoặc `Environment.Exit(0)`.
  - Chạy từ dev/bin vẫn relaunch đúng exe hiện tại (chấp nhận). Bọc try/catch, lỗi → thông báo, không crash.
  - Trước khi thoát, gọi đường dừng-êm nếu tiện (không bắt buộc — restart chủ động khác update; tối thiểu để
    ShutdownRequested chạy như đóng thường). Ưu tiên đơn giản: Shutdown() sẽ kích ShutdownRequested (đã dừng module).

### Bước 6 — Kiểm chứng

- KHÔNG có project test cho suite (solution chỉ có `XuLyDonShopee.Tests`) → verify bằng **build**:
  `dotnet build ShopeeSuite.sln` 0 error, 0 warning mới.
- `dotnet test orders/XuLyDonShopee.Tests` vẫn xanh (không đụng orders — chỉ để chắc không vỡ solution).
- Tự rà logic: 3 chế độ dựng đúng tập tab; `SelectedTab` không null ở mọi chế độ; không NRE ở
  `PrepareShutdownAsync`/`ShutdownRequested` khi module null.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` sạch; `dotnet test orders/XuLyDonShopee.Tests` xanh.
- [ ] `AppModeStore` lưu/đọc `%AppData%\ShopeeSuite\app-mode.json`; thiếu/hỏng → Full.
- [ ] ShellViewModel: Full = 4 tab; Workspace = Workspace+Cấu hình BigSeller+Cài đặt (không Shopee, không dựng
      ordersVm); Shopee = Shopee+Cài đặt (không dựng khối workspace, không start HubDispatcher/worker).
- [ ] App.axaml không init MultiBrave/UpdateProduct/BraveFleet khi chế độ Shopee; auto-update vẫn chạy mọi chế độ.
- [ ] Cài đặt có selector chế độ (luôn hiện) + "Lưu & khởi động lại" → đổi chế độ → app khởi động lại, vào lại
      thấy đúng tab theo chế độ mới; file config còn nguyên sau khi (giả lập) update.
- [ ] Không NRE khi đóng app ở chế độ Workspace/Shopee (module null được guard).

## 5. Rủi ro & lưu ý

- **Nhiều biến VM cục bộ thành nullable** trong ShellViewModel — rà mọi chỗ dùng lại chúng bên dưới ctor
  (ráp command ribbon, RequestNavigate, PrepareShutdownAsync) để guard null; sót một chỗ là NRE lúc boot chế
  độ thu gọn.
- `HubDispatcher.Start()` hiện đã có điều kiện `HubServerConfigStore...Enabled` — thêm điều kiện `ws` (chỉ máy
  Hub + chế độ có workspace).
- Đọc `AppModeStore.Shared.Current` phải nhất quán giữa App.axaml và ShellViewModel (cùng một lần chạy —
  singleton đọc 1 lần, không đổi giữa chừng vì đổi chế độ = restart).
- Restart: `Environment.ProcessPath` dưới Velopack trỏ đúng exe hiện hành → relaunch đúng. Đừng dùng
  `Assembly.Location` (rỗng khi single-file).
- Selector chế độ phải hiện Ở MỌI CHẾ ĐỘ (kể cả Shopee, nơi section suite-settings có thể ẩn bớt) — đặt ở
  UnifiedSettings cấp trên, không nhét trong section Suite.
- Auto-update (`UpdateService.CheckAsync`/`ApplyAndRestart`) tuyệt đối KHÔNG gate theo chế độ — mọi máy vẫn
  nhận bản Full.
- Đây là nhánh off `feature/gop-don-hang`; nhánh `feature/dang-nhap-subaccount` cũng sửa `ShellViewModel`
  (bỏ tab "Chạy tự động" + đổi nút) → khi cả hai merge về main sẽ có xung đột nhỏ ở ShellViewModel, Fable xử
  lúc merge (không phải việc của Opus).

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
