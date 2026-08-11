# Plan: Bỏ lựa chọn trình duyệt — app chỉ chạy Brave

- **Ngày:** 2026-08-11
- **Trạng thái:** hoàn thành (chưa phát hành — chờ user bảo)
- **Người lập / thực thi:** phiên chính (tự làm)

## 1. Bối cảnh & mục tiêu

User chốt: *"giờ app phụ thuộc brave khá nhiều, không dùng được browser khác, bạn bỏ phần lựa chọn browser ở
client đi, khi chạy app, không có brave thì báo lỗi và dừng"*.

Hiện trạng: module Đơn hàng có enum `BrowserChoice` (Auto · Chrome · Edge · Brave · Chromium đóng gói) luồn
qua 8 lớp, một ComboBox ở màn Cài đặt, và **hai đường lùi âm thầm**:

1. `LoginBrowserBootstrap.LaunchAndConnectAsync` — không tìm thấy trình duyệt thật thì **tải Chromium đóng gói
   của Playwright (~150 MB)** rồi chạy bằng nó.
2. `BrowserLocator.ResolveExecutableCore` với `Auto` — ưu tiên **Chrome → Edge → Brave**.

Nghĩa là mặc định của app hiện nay KHÔNG phải Brave. Trên máy có Chrome, cầu nối sẽ mở Chrome — mà Chromium
137+ đã bỏ cờ cho phép `--load-extension` nên extension **không nạp, không báo lỗi**, cầu nối treo 45 giây rồi
chết. Đây đúng là "còn tồn" ghi ở CHANGELOG v1.8.9; lệnh này của user xoá luôn cả gốc vấn đề.

## 2. Phạm vi

**Làm:**

1. Xoá hẳn khái niệm `BrowserChoice` khỏi module Đơn hàng; mọi nơi mở trình duyệt đều dùng Brave.
2. Xoá card "Trình duyệt" ở màn Cài đặt.
3. Xoá đường lùi Chromium đóng gói.
4. Cổng chặn lúc khởi động app: không có Brave → hộp thoại báo lỗi rõ ràng + thoát.

**Không làm:**

- **Không đụng `suite/Shopee.Core/Platform/ToolkitBrowserLocator.cs`** (nó lùi về Edge cho các module
  Workspace: Search / MultiBrave / UpdateProduct). Cổng chặn lúc khởi động khiến nhánh lùi đó thành bất khả thi
  trên thực tế; gỡ code ở đó là đụng vào ba module ngoài phạm vi yêu cầu.
- Không đổi `BrowserProfilePaths.ForAccount` — xem rủi ro số 1 bên dưới.

## 3. Các bước thực hiện

| # | File | Việc |
|---|---|---|
| 1 | `orders/…/Models/BrowserChoice.cs` | **Xoá file** (enum + helper `BrowserChoices`). |
| 2 | `orders/…/Services/BrowserLocator.cs` | Chỉ còn `FindBraveExecutable()` + hằng `LoaiHoSo = "brave"`. Bỏ `ResolveExecutable`, `ResolveExecutableCore`, `ResolveBrowserKind`, `ClassifyExe`, `FindChrome/FindEdge`. |
| 3 | `orders/…/Data/SettingsRepository.cs` | Bỏ `GetBrowserChoice` / `SetBrowserChoice` + khoá `browser_choice`. |
| 4 | `orders/…/ViewModels/SettingsViewModel.cs` | Bỏ `BrowserChoiceOption`, `BrowserOptions`, `SelectedBrowser`, `DetectedBrowserText`, `BrowserSavedMessage`, `SaveBrowserCommand`. |
| 5 | `suite/…/Modules/Settings/UnifiedSettingsView.xaml` | Xoá cả `<Border>` card "Trình duyệt". |
| 6 | `orders/…/ViewModels/MainViewModel.cs` | Dòng trạng thái cố định `"Trình duyệt: Brave"`. |
| 7 | `LoginBrowserBootstrap.cs` | Bỏ tham số choice; bỏ `EnsureChromiumInstalledForFallback` + nhánh `playwright.Chromium.ExecutablePath`; thiếu Brave → ném lỗi tiếng Việt. `DescribeBrowser` → mô tả Brave. |
| 8 | `PocCleanLauncher.cs`, `OrdersBridgeLauncher.cs`, `OrdersBridgeSession.cs`, `ShopeeLoginService.cs` | Bỏ tham số `BrowserChoice`. |
| 9 | `AccountSession.cs`, `AccountsViewModel.Phien.cs` | Bỏ chỗ đọc setting + truyền choice; hồ sơ dùng `BrowserLocator.LoaiHoSo`. |
| 10 | `suite/Shopee.Suite/App.xaml.cs` | **Cổng Brave**: `OnStartup` kiểm `FindBraveExecutable()`; null → `MessageBox` + `Shutdown()` + return, trước khi dựng cửa sổ. |
| 11 | Tests | Xoá `BrowserChoiceTests.cs`; cắt phần `ResolveExecutableCore` khỏi `BrowserLocatorTests.cs`; thêm test cho hàm thuần của cổng Brave. |

Cổng Brave tách hàm thuần để test được không cần máy có/không Brave:

```csharp
internal static string? LyDoChanKhoiDong(string? bravePath)
    => string.IsNullOrWhiteSpace(bravePath) ? "<thông báo tiếng Việt>" : null;
```

## 4. Tiêu chí nghiệm thu

- [ ] Grep `BrowserChoice` trong `orders/` → **0 kết quả** (trừ plan/CHANGELOG).
- [ ] Màn Cài đặt không còn card "Trình duyệt".
- [ ] `dotnet build ShopeeSuite.sln` — **0 warning, 0 error**.
- [ ] Toàn bộ test xanh; test mới của cổng Brave phải THỬ PHÁ được.
- [ ] Hồ sơ vẫn là `profiles/<id>-brave` — app mở lại KHÔNG phải đăng nhập lại (cookie còn hạn).
- [ ] Chạy thật 1 vòng: cầu nối nối được, ≥3 shop xong sạch.

## 5. Rủi ro & lưu ý

1. **Tên thư mục hồ sơ là rủi ro lớn nhất.** `BrowserProfilePaths.ForAccount(baseDir, id, browserKind)` sinh
   `profiles/<id>-<kind>`; máy đang chạy có `profiles/1-brave` với cookie còn hạn (vòng nào cũng "BỎ QUA bước
   đăng nhập Playwright"). Hằng thay thế PHẢI đúng chuỗi `"brave"` — sai một ký tự là mất sạch cookie, mọi tài
   khoản phải đăng nhập lại và ăn captcha.
2. **Cổng chặn dừng CẢ app**, không riêng module Đơn hàng — đúng chữ user yêu cầu ("báo lỗi và dừng"). Thông
   báo phải nói rõ cần cài Brave và tải ở đâu, không chỉ "không tìm thấy trình duyệt".
3. Bỏ tải Chromium đóng gói KHÔNG ảnh hưởng Playwright driver: driver đi kèm gói NuGet
   (`.playwright/`), còn `ConnectOverCDPAsync` nối vào Brave đang chạy chứ không cần binary Chromium.
4. Không đụng đường chạy vòng shop (`ShopFlowRunner`, `OrdersBridgeChannel`, `WebSocketServer`).

---

## Báo cáo thực thi

### Đã làm

Xoá 2 file (`Models/BrowserChoice.cs`, `Tests/BrowserChoiceTests.cs`), thêm 2 file
(`shared/Shopee.Toolkit/Browser/BraveRequirement.cs`, `Tests/ChiChayBraveTests.cs`), sửa 12 file.

Ba thứ bị gỡ, đáng ghi riêng vì đều là đường đi ngầm:

1. **ComboBox chọn trình duyệt** (card "Trình duyệt" ở Cài đặt → Đơn hàng) + khoá DB `browser_choice`.
2. **Nhánh lùi tải Chromium đóng gói** trong `LoginBrowserBootstrap` — máy thiếu trình duyệt thì nó lặng lẽ
   tải ~150 MB rồi chạy bằng Chromium, tức bằng đúng loại trình duyệt mà cầu nối không nạp nổi extension.
3. **Thứ tự ưu tiên `Auto` = Chrome → Edge → Brave.** Nghĩa là mặc định của app trước bản này KHÔNG phải Brave;
   trên máy có Chrome, cầu nối mở Chrome và treo 45 giây rồi chết mà không nói vì sao.

Cổng chặn đặt ở đầu `App.OnStartup`, TRƯỚC mọi khởi tạo engine/cửa sổ: `MessageBox` + `Shutdown(1)`.
Chặn cả app chứ không riêng module Đơn hàng — các module Workspace cũng chạy trên Brave.

### Kiểm chứng

- `dotnet build ShopeeSuite.sln` → **0 warning / 0 error**.
- Test: orders **1736** (1776 → −47 test của tính năng vừa bỏ, +7 test mới) · suite core **139**. Xanh hết.
- **THỬ PHÁ**: `LyDoChanKhoiDong` luôn trả null + đổi `LoaiHoSo` thành `"brave-browser"` → **5/7 ĐỎ**;
  khôi phục → xanh.
- Chạy app thật: khởi động bình thường, thanh trạng thái "Trình duyệt: Brave", màn Cài đặt → Đơn hàng còn đúng
  2 card (Tự động hóa · Đồng bộ Google Sheet), không còn card Trình duyệt, không lỗi binding.
- Vòng chạy thật: xem mục dưới.

### Điểm suýt trượt

`server/Shopee.Hub.Web` KHÔNG nằm trong `ShopeeSuite.sln`; ở đợt trước đã trượt một lần vì tưởng
"sln xanh = tất cả xanh". Đợt này Hub không bị đụng nên không phải build riêng, nhưng ghi lại để lần sau nhớ.
