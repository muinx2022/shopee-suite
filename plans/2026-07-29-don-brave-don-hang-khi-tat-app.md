# Plan: Dọn Brave đơn hàng khi tắt app (kể cả force-kill)

- **Ngày:** 2026-07-29
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Auto (Cursor)

## 1. Bối cảnh & mục tiêu

Khi đóng app bình thường, `ShutdownRequested` → `OrdersModuleHost.StopAsync()` → `Sessions.StopAllAsync()` có kill Brave phiên đơn hàng. Scrape/Update phóng qua `BraveJobObject` (Job Object `KILL_ON_JOB_CLOSE`) nên OS giết Brave khi app crash/force-kill.

Brave **module Đơn hàng** hiện phóng bằng `Process.Start` thường tại:
- `orders/XuLyDonShopee.Core/Services/PocCleanLauncher.cs`
- `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`

→ force-kill / crash → Brave sót (vd `%APPDATA%\XuLyDonShopee\profiles\*-brave`).

`BraveFleet.StartupSweep` chỉ quét `persistent-data` của Suite (`ManagedRoot`), **không** phủ `XuLyDonShopee\profiles`, và chỉ chạy khi chế độ có Workspace.

**Mục tiêu:** Brave/Chrome/Edge do module Đơn hàng mở phải (1) chết theo app khi force-kill (Job Object), (2) bị dọn lúc khởi động nếu còn mồ côi từ lần trước. Giữ ràng buộc: module Đơn hàng **không** tham chiếu `Shopee.Core` (pattern hook như hub push).

## 2. Phạm vi

- **Làm:**
  - Hook phóng trình duyệt từ Core; Suite rót `BraveJobObject.Start`.
  - `BraveFleet.AddManagedRoot` cho `%APPDATA%\XuLyDonShopee\profiles` (+ sweep chrome/msedge dưới managed root).
  - Rót hook + đăng ký root + `StartupSweep` trong `OrdersModuleHost.TryCreate`.
  - Unit test helper nối args (quoting đường dẫn có khoảng trắng).
- **Không làm:**
  - Không đổi hành vi đóng êm (`StopAsync`) đang có.
  - Không đổi `KillBrowsersOnProfile` (PowerShell) của bridge.
  - Không bump version / release / deploy.
  - Không dọn Brave cá nhân (`%LocalAppData%\BraveSoftware`).

## 3. Các bước thực hiện

1. **`orders/XuLyDonShopee.Core/Services/BrowserProcessStarter.cs`** (mới)
   - Static hook: `Func<string fileName, IReadOnlyList<string> arguments, Process>? Start`.
   - `StartOrFallback(fileName, args)`: nếu hook null → `Process.Start` + `ArgumentList` như hiện tại; có hook → gọi hook.
   - Helper nội bộ/public testable `JoinArguments(IReadOnlyList<string>)`: nối thành chuỗi `Arguments` an toàn (quote token có khoảng trắng / `"`), dùng khi Suite rót Job Object.

2. **`PocCleanLauncher.Open`** và **`ShopeeLoginService`** (chỗ `Process.Start`): đổi sang `BrowserProcessStarter.StartOrFallback(exe, args)`.

3. **`suite/Shopee.Core/Browser/BraveFleet.cs`**
   - `AddManagedRoot(string path)`: thêm root đã normalize vào tập root phụ (thread-safe).
   - `IsUnderManagedRoot`: true nếu nằm dưới `ManagedRoot` **hoặc** bất kỳ root phụ.
   - `EnumerateOurBrave` → quét `brave.exe`, `chrome.exe`, `msedge.exe` (vẫn chỉ giết khi `--user-data-dir` dưới managed root → không đụng trình duyệt cá nhân).

4. **`suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs`** — trong `TryCreate` sau `new AppServices()`:
   - `WireBrowserProcessStarter()`: gán hook → `BraveJobObject.Start(exe, BrowserProcessStarter.JoinArguments(args), startMinimized: false)`.
   - `AddManagedRoot(Path.Combine(dir của Database.Path, "profiles"))`.
   - Gọi `BraveFleet.StartupSweep()` (bổ sung sau sweep workspace nếu có; lần đầu ở chế độ chỉ Shopee).

5. **Test** `orders/XuLyDonShopee.Tests`: `JoinArguments` — path có space được quote; token thường không quote thừa.

6. Build `suite/Shopee.Suite` + `dotnet test` project `XuLyDonShopee.Tests` (và test Core/Suite liên quan nếu có).

## 4. Tiêu chí nghiệm thu

- [ ] `PocCleanLauncher` / `ShopeeLoginService` không còn `Process.Start` trực tiếp cho Brave (đi qua starter).
- [ ] `OrdersModuleHost` gán hook Job Object + `AddManagedRoot` + `StartupSweep`.
- [ ] Grep: không có ProjectReference `Shopee.Core` từ `XuLyDonShopee.Core` / `.App`.
- [ ] Unit test `JoinArguments` xanh.
- [ ] `dotnet build` Suite Release hoặc Debug OK; test orders xanh.
- [ ] (Thủ công nếu được) Mở phiên đơn hàng → Task Manager End Task `ShopeeSuite` → không còn `brave.exe` với `--user-data-dir=...\XuLyDonShopee\profiles\...`.

## 5. Rủi ro & lưu ý

- Quote args sai → Brave không mở / path gãy trên user có khoảng trắng trong tên (`Ng Xuan Mui`) — test JoinArguments bắt buộc.
- Job Object fallback nếu tạo job lỗi vẫn `Process.Start` thường (hành vi cũ) — chấp nhận.
- `StartupSweep` + `IsSoleAppInstance`: nhiều instance ShopeeSuite → không quét (giữ an toàn hiện có).
- Chrome/Edge dưới managed root mới bị sweep; không mở rộng ra profile mặc định của hệ thống.

---

## Báo cáo thực thi (điền sau khi xong)

_(chưa)_
