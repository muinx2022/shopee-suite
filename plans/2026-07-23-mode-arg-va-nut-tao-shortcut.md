# Plan: `--mode` khoá chế độ per-shortcut + nút "Tạo shortcut cho chế độ này"

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nhánh:** `feature/gop-don-hang` (cây chính)

## 1. Bối cảnh & mục tiêu

Người dùng muốn **2 shortcut độc lập** (một Workspace, một Shopee) chạy **song song** trên một máy, mỗi
shortcut **khoá cứng một chế độ** — KHÔNG theo `app-mode.json` chung (than phiền hiện tại: 2 cửa sổ đều
theo mode lưu sau cùng). Quan trọng: **giữ nguyên tài khoản/dữ liệu hiện có** (không phải cài lại) — nên
KHÔNG tách kho dữ liệu, chỉ khoá CHẾ ĐỘ.

**Vì sao chỉ cần khoá mode (không cần tách data):** Workspace dùng kho `%AppData%\ShopeeSuite` (BigSeller,
scrape…); Shopee dùng kho `%AppData%\XuLyDonShopee` (đơn hàng). HAI kho RIÊNG → chạy song song không đụng
dữ liệu nhau. Chỉ có `app-mode.json` (trong `ShopeeSuite`) là chung → khoá mode bằng tham số dòng lệnh là đủ.

**Giải pháp:** tham số **`--mode <Full|Workspace|Shopee>`** (ưu tiên hơn `app-mode.json`). Mỗi shortcut một
`--mode`. Một bản cài (vẫn auto-update), N shortcut. Nút trong Cài đặt tạo shortcut này.

**Đã bỏ hướng `--data-dir`** (tách kho — thừa, bắt cài lại tài khoản). Xem plan `2026-07-23-data-dir-*`
(trạng thái: dừng).

**Điểm cần xử để chạy song song sạch:** hiện `App.axaml.cs` chạy `CoordinationRuntime.InitFromConfig()` ở
MỌI chế độ. Nếu bản Workspace và bản Shopee chạy cùng lúc, cả hai đọc chung `machine.json` +
heartbeat/lease trong `%AppData%\ShopeeSuite` → tranh danh tính máy trên Hub + tranh file lease. Bản Shopee
KHÔNG cần điều phối fleet của suite (module đơn hàng có đường đẩy Hub RIÊNG). → **Gate CoordinationRuntime
theo chế độ có Workspace** (đồng bộ với việc đã gate MultiBrave/BraveFleet/AssignmentWorker). Tradeoff: bản
Shopee không nhận "lệnh update fleet" của suite — chấp nhận (auto-update + bấm tay vẫn chạy; bản Workspace
vẫn nhận).

## 2. Phạm vi

- **Làm:** `--mode` override (Shopee.Core/AppModeStore); gate CoordinationRuntime theo workspace
  (App.axaml.cs); AppRestart forward args; nút "Tạo shortcut cho chế độ này" + helper tạo .lnk (Windows).
- **Không làm:**
  - KHÔNG tách kho dữ liệu (`--data-dir` đã bỏ); dữ liệu vẫn ở `%AppData%\ShopeeSuite` + `%AppData%\XuLyDonShopee`.
  - KHÔNG đụng luồng đăng nhập/vòng lặp shop/GSheet/gate-tab đã có.
  - KHÔNG single-instance lock (giữ chạy song song).
  - Linux: bỏ qua tạo shortcut (chỉ Windows) — báo "chỉ hỗ trợ Windows".

## 3. Các bước thực hiện

### Bước 1 — `--mode` override (suite/Shopee.Core/Infrastructure/AppModeStore.cs)

- Thêm parser thuần `internal static AppMode? ParseModeArg(string[] args)`: tìm `--mode <X>` HOẶC
  `--mode=<X>` trong args; parse enum (ignoreCase + `Enum.IsDefined`); không có/không hợp lệ → null.
- `private static AppMode? ModeArg()` = `ParseModeArg(Environment.GetCommandLineArgs())` (try/catch → null).
- Trong ctor `AppModeStore` (sau `Load()`): nếu `ModeArg()` có giá trị → `Current = <đó>` (ARG THẮNG file).
  Thêm `public bool ModeLockedByArg { get; }` = (ModeArg() != null) — UI dùng để hiện "chế độ khoá bởi shortcut".
- `Save(mode)` GIỮ NGUYÊN (vẫn ghi file); nhưng khi ModeLockedByArg, giá trị file không ảnh hưởng instance
  này (arg thắng ở lần chạy sau nếu shortcut vẫn truyền `--mode`). Doc rõ điều này.

### Bước 2 — Gate CoordinationRuntime theo workspace (suite/Shopee.Suite/App.axaml.cs)

- Đưa `Shopee.Core.Coordination.CoordinationRuntime.InitFromConfig()` (+ `HttpCoordinationHub.DiagLog` nếu
  chỉ phục vụ coordination) vào trong `if (ShowsWorkspace(mode))` (cùng khối đã gate MultiBrave/BraveFleet).
  GIỮ luôn chạy mọi chế độ: crash handlers, `StartupJanitor`, **`UpdateService.CheckAsync` (auto-update)**.
- `UpdateService.PrepareShutdownAsync`/`ShutdownRequested`: không đổi (đã guard null).
- Đọc `mode` ở đầu `OnFrameworkInitializationCompleted` — đã có sẵn từ việc che-do; tái dùng.

### Bước 3 — AppRestart forward args (suite/Shopee.Suite/Services/AppRestart.cs)

- `Restart()`: khi relaunch, KÈM lại mọi tham số dòng lệnh gốc để `--mode` (và mọi arg) sống qua restart:
  ```csharp
  var exe = Environment.ProcessPath;
  var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
  foreach (var a in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(a);
  Process.Start(psi);
  ```
  Giữ Shutdown()/Environment.Exit + try/catch hiện có.

### Bước 4 — Nút "Tạo shortcut cho chế độ này"

- Helper mới `suite/Shopee.Suite/Services/ShortcutCreator.cs`:
  `static (bool ok, string message) CreateDesktopShortcut(string name, string targetExe, string args, string? iconPath)`
  — Windows (`OperatingSystem.IsWindows()`): tạo `.lnk` trên Desktop qua COM `WScript.Shell`
  (`Type.GetTypeFromProgID("WScript.Shell")` + `dynamic`): set `TargetPath`, `Arguments`, `WorkingDirectory`
  (= thư mục exe), `IconLocation` (= `targetExe,0`), `Save()`. Trả `(true, đường dẫn .lnk)`. Không phải
  Windows → `(false, "Chỉ hỗ trợ tạo shortcut trên Windows.")`. Try/catch → `(false, lỗi)`. KHÔNG ném.
- `UnifiedSettingsViewModel`:
  - `[RelayCommand] CreateShortcutForMode()`: `var mode = SelectedMode.Mode;`
    `var exe = Environment.ProcessPath;` (null → báo lỗi) → `args = $"--mode {mode}"` →
    `name = $"Shopee Suite ({SelectedMode.Label ngắn})"` (dùng nhãn ngắn: "Workspace"/"Shopee"/"Đầy đủ" — thêm
    field `ShortLabel` vào `AppModeOption` hoặc map switch) → gọi `ShortcutCreator.CreateDesktopShortcut` →
    `Dialogs.InfoAsync` báo kết quả (ok: "Đã tạo shortcut …"; fail: message). KHÔNG cần pre-seed app-mode.json
    (mode đến từ `--mode`, dữ liệu dùng chung).
  - Giữ `SelectedMode`, `SaveModeAndRestart`, `ShowsWorkspaceSettings`, v.v.
  - Thêm `public bool ModeLockedByArg => AppModeStore.Shared.ModeLockedByArg;` để View ẩn/hiện.
- `UnifiedSettingsView.axaml` (section "CHẾ ĐỘ ỨNG DỤNG"): NGAY DƯỚI hàng selector, thêm:
  - Nút **"Tạo shortcut cho chế độ này"** (bind `CreateShortcutForModeCommand`) + caption ngắn: "Tạo shortcut
    ngoài desktop mở thẳng chế độ đang chọn (chạy song song với chế độ khác)."
  - Nút "Lưu & khởi động lại" hiện tại: bọc `IsVisible="{Binding !ModeLockedByArg}"` (khi khoá bởi shortcut,
    đổi mode ở đây vô nghĩa vì restart giữ `--mode`). Khi `ModeLockedByArg` → hiện dòng chú thích "Chế độ đang
    khoá bởi shortcut (--mode)." Selector vẫn cho chọn để TẠO shortcut chế độ khác.

### Bước 5 — Kiểm chứng

- `dotnet build ShopeeSuite.sln` — 0 error, 0 warning mới.
- `dotnet test orders/XuLyDonShopee.Tests` — xanh (911).
- Unit test parser: `ParseModeArg(["--mode","Shopee"])`→Shopee; `["--mode=Workspace"]`→Workspace;
  `["--mode","xxx"]`/`[]`→null (đặt `ParseModeArg` internal + InternalsVisibleTo nếu cần; nếu không có suite
  test project thì bỏ test này, chỉ build — ghi rõ).

## 4. Tiêu chí nghiệm thu

- [ ] Build sạch; test xanh.
- [ ] `--mode Shopee` → app mở chế độ Shopee bất kể `app-mode.json`; `--mode Workspace` → Workspace; không
      có `--mode` → theo `app-mode.json` như cũ.
- [ ] Bản Shopee KHÔNG chạy CoordinationRuntime (gate workspace); auto-update vẫn chạy mọi chế độ.
- [ ] `AppRestart` relaunch KÈM `--mode` (đổi trong Settings không phá; restart giữ chế độ shortcut).
- [ ] Nút "Tạo shortcut cho chế độ này" tạo `.lnk` desktop trỏ `ShopeeSuite.exe --mode <Mode>` (Windows);
      chạy shortcut đó mở đúng chế độ, DÙNG CHUNG tài khoản/dữ liệu hiện có.

## 5. Rủi ro & lưu ý

- Chạy 2 bản (WS + SP) song song: kho dữ liệu RIÊNG (ShopeeSuite vs XuLyDonShopee) nên không đụng; đã gate
  CoordinationRuntime để bản Shopee không tranh danh tính máy. Vẫn nên chia ngân sách CPU/RAM (Hiệu năng) cho
  mỗi bản.
- `WScript.Shell` qua `dynamic` cần `Microsoft.CSharp` (có sẵn trong .NET). Nếu build phàn nàn thiếu, dùng
  reflection `InvokeMember` thay `dynamic`.
- Shortcut trỏ `Environment.ProcessPath`. Bản cài Velopack: path `...\current\ShopeeSuite.exe` ỔN ĐỊNH qua
  update → shortcut vẫn đúng sau auto-update. (Bản chép-phẳng máy dev: path thư mục cài, cũng ổn định.)
- Gate CoordinationRuntime = bản Shopee không nhận lệnh update-fleet của suite (auto-update + tay vẫn chạy) —
  chấp nhận. Nếu sau muốn bản Shopee vẫn nhận, xử riêng.
- Đổi tên `--data-dir` → `--mode` là CỐ Ý (nhẹ hơn, giữ dữ liệu). Đừng khôi phục `--data-dir`.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
