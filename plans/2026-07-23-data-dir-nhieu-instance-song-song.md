# Plan: `--data-dir` — chạy nhiều instance song song (kiểu Chrome --user-data-dir)

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nhánh:** `feature/gop-don-hang` (cây chính)

## 1. Bối cảnh & mục tiêu

Chế độ app (Full/Workspace/Shopee) lưu ở `app-mode.json` trong **một** gốc dữ liệu dùng chung
(`%AppData%\ShopeeSuite`). Người dùng muốn **2 shortcut độc lập** trên cùng máy — một bản Workspace + một
bản Shopee — chạy **song song**, mỗi bản mode + dữ liệu cố định RIÊNG (giống Chrome `--user-data-dir`,
VSCode nhiều cửa sổ). Hiện chạy 2 cửa sổ được (app KHÔNG có khoá single-instance) nhưng cả hai đọc chung
`app-mode.json` → **theo mode lưu sau cùng**, không độc lập.

**Giải pháp:** thêm tham số dòng lệnh **`--data-dir <path>`**. Mỗi instance mở với `--data-dir` khác nhau
→ gốc dữ liệu riêng → mode + tài khoản/cookie/profile riêng. Một bản cài (vẫn auto-update) + N shortcut.

**Hai gốc dữ liệu phải phủ (đã khảo sát):**
- **Suite:** `SuitePaths.Root = <base>\ShopeeSuite` (Shopee.Core/Infrastructure/SuitePaths.cs). `<base>` hiện
  = `%AppData%` hoặc đường dẫn trong `data-dir.txt` (đã có sẵn cơ chế marker). CHỖ chứa `app-mode.json`.
- **Đơn hàng:** `orders/XuLyDonShopee.Core/Data/Database.cs` dòng 41–42:
  `Path.Combine(%AppData%, "XuLyDonShopee", "app.db")` — gốc `%AppData%\XuLyDonShopee`, MỌI dữ liệu đơn
  (app.db + profile + cookie + log) suy từ thư mục này. Đây là CHỖ DUY NHẤT orders đọc `%AppData%` (grep
  toàn module xác nhận). Module orders KHÔNG ref Shopee.Core nên KHÔNG gọi SuitePaths được.

**Ngữ nghĩa `--data-dir X`:** base = `X` → `SuitePaths.Root = X\ShopeeSuite`, orders =
`X\XuLyDonShopee\...`. Hai app-data là thư mục anh em dưới `X` (giống layout %AppData% mặc định). Vd shortcut
`--data-dir "%AppData%\Suite-SP"` → suite ở `%AppData%\Suite-SP\ShopeeSuite`, đơn ở `%AppData%\Suite-SP\XuLyDonShopee`.

**BẪY quan trọng:** `AppRestart.Restart()` (nút "Lưu & khởi động lại") hiện relaunch bằng
`Process.Start(Environment.ProcessPath)` — KHÔNG kèm `--data-dir` → sau khi đổi mode + restart, instance
mất `--data-dir`, đọc gốc mặc định → SAI. Phải **forward nguyên args** khi restart.

## 2. Phạm vi

- **Làm:** thêm đọc `--data-dir` ở `SuitePaths` + `Database.cs`; forward args khi restart. Toàn bộ `suite/` +
  `orders/`.
- **Không làm:**
  - KHÔNG đụng logic mode/tab, luồng đăng nhập, vòng lặp shop, GSheet.
  - KHÔNG tạo shortcut / pre-seed app-mode.json trong code (Fable làm bước SETUP sau deploy).
  - KHÔNG single-instance lock (app đang cho chạy nhiều instance — GIỮ vậy).

## 3. Các bước thực hiện

### Bước 1 — Parser `--data-dir` (dùng ở cả 2 assembly, KHÔNG share ref được → mỗi bên một bản nhỏ)

Logic chung (đặt static, thuần): đọc `Environment.GetCommandLineArgs()`, tìm `--data-dir <path>` HOẶC
`--data-dir=<path>`; trả path đã trim (rỗng/không có → null). Bỏ qua lỗi.

- Trong `SuitePaths.cs`: thêm `private static string? DataDirArg()` (parser trên).
- Trong `orders/XuLyDonShopee.Core/Data/Database.cs`: thêm bản parser tương tự (static private) — CHÉP logic
  (không share được vì orders không ref Shopee.Core). Ghi comment "đồng bộ với SuitePaths.DataDirArg".

### Bước 2 — `SuitePaths.ResolveAppDataBase()`

Thứ tự ưu tiên MỚI: **`--data-dir` (nếu có) → `data-dir.txt` cạnh exe → `%AppData%`**. Chèn nhánh
`--data-dir` LÊN ĐẦU `ResolveAppDataBase()`:
```csharp
var arg = DataDirArg();
if (!string.IsNullOrWhiteSpace(arg))
{
    if (!Path.IsPathRooted(arg)) arg = Path.Combine(AppContext.BaseDirectory, arg);
    return Path.GetFullPath(arg);   // Root = <arg>\ShopeeSuite (append ShopeeSuite như hiện tại)
}
// ... giữ nguyên nhánh data-dir.txt + %AppData% cũ
```

### Bước 3 — `orders/.../Database.cs` (path resolver, dòng ~41–42)

```csharp
var baseDir = DataDirArg();               // parser mới ở Bước 1
if (string.IsNullOrWhiteSpace(baseDir))
    baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
else if (!Path.IsPathRooted(baseDir))
    baseDir = Path.Combine(AppContext.BaseDirectory, baseDir);
return Path.Combine(baseDir, "XuLyDonShopee", "app.db");
```
(Mọi dữ liệu đơn khác suy từ thư mục app.db nên tự theo — KHÔNG cần sửa chỗ khác. Rà nhanh
`BrowserProfilePaths`/log/invoice để chắc chúng lấy từ `GetDirectoryName(Database.Path)`, không tự đọc
`%AppData%` lần nữa.)

### Bước 4 — `AppRestart.Restart()` forward args (suite/Shopee.Suite/Services/AppRestart.cs)

Khi relaunch, kèm LẠI mọi tham số dòng lệnh gốc (đặc biệt `--data-dir`) để instance mới giữ đúng gốc dữ liệu:
```csharp
var exe = Environment.ProcessPath;
var args = Environment.GetCommandLineArgs();          // [0] = exe path
var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
Process.Start(psi);
```
Giữ phần Shutdown()/Environment.Exit + try/catch như hiện có.

### Bước 5 — Kiểm chứng

- `dotnet build ShopeeSuite.sln` — 0 error, 0 warning mới.
- `dotnet test orders/XuLyDonShopee.Tests` — xanh (911).
- Nếu tách được parser thành hàm thuần (SuitePaths.DataDirArg / Database parser), thêm 1–2 unit test:
  `--data-dir C:\x` / `--data-dir=C:\x` → "C:\x"; thiếu → null. (Orders test project test được Database
  parser nếu để internal + InternalsVisibleTo.)

## 4. Tiêu chí nghiệm thu

- [ ] Build sạch; test xanh.
- [ ] `--data-dir X`: `SuitePaths.Root = X\ShopeeSuite`; orders app.db = `X\XuLyDonShopee\app.db`. Không có
      `--data-dir` → hành vi CŨ y hệt (`%AppData%` / `data-dir.txt`).
- [ ] `AppRestart.Restart()` relaunch KÈM `--data-dir` (đổi mode trong instance có `--data-dir` → restart vẫn
      đúng gốc, KHÔNG rơi về mặc định).
- [ ] Không thêm khoá single-instance (2 instance vẫn chạy song song).

## 5. Rủi ro & lưu ý

- 2 bản parser (`SuitePaths` + `Database.cs`) phải KHỚP hành vi — cùng hỗ trợ `--data-dir <path>` và
  `--data-dir=<path>`. Ghi comment chéo.
- `Environment.GetCommandLineArgs()` gọi được mọi lúc (process-global) → cả 2 nơi (static/lazy) đọc được.
- Velopack có thể truyền args riêng (`--veloapp-*`) — parser chỉ bắt đúng `--data-dir`, không ảnh hưởng.
- Đường dẫn tương đối trong `--data-dir` quy theo thư mục exe (giống `data-dir.txt`) cho nhất quán.
- Sau deploy, Fable làm bước SETUP (ngoài code): tạo 2 shortcut `ShopeeSuite.exe --data-dir "<X1>"` /
  `"<X2>"`, và pre-seed `app-mode.json` (Workspace/Shopee) trong từng gốc để khỏi phải chọn tay lần đầu.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
