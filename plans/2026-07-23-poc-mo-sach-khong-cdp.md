# Plan: Nút POC "Mở sạch (không CDP-navigation)" để kiểm chứng GĐ0 né captcha

- **Ngày:** 2026-07-23
- **Trạng thái:** hoàn thành — Core (`BuildCleanPocArgs` + `PocCleanLauncher`) GIỮ; phần UI (nút ở Cài đặt) đã DỜI sang per-account theo `plans/2026-07-23-poc-mo-sach-per-account.md`. GĐ0 ĐẠT.
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Module Đơn hàng bấm "Chi tiết" vào Seller Centre bị Shopee đá sang captcha (captcha không load). Chrome mở tay thì bình thường. Điều tra xác định **gốc rễ là cách mở profile**, không phải cú click:

Trong `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs` (`OpenAsync`, ~dòng 489–526), sau khi `Process.Start` mở Brave, app **luôn**:
- Dòng 498–499: `playwright.Chromium.ConnectOverCDPAsync(...)` — nối Playwright vào trình duyệt qua CDP.
- Dòng 522: `page.GotoAsync(SubaccountUrl)` — Playwright tự lái điều hướng.

Kênh CDP-điều-khiển-trang này chính là dấu vết anti-bot Shopee soi ở cửa Seller Centre. Ngoài ra `BraveLaunchArgs.BuildBraveArgs` luôn thêm `--remote-debugging-port=<port>`, mở endpoint CDP suốt phiên. Bản POC extension hiện có (nút TRUSTED dùng `chrome.debugger`) **không attach được** vì Playwright đã chiếm debugger của tab → nút "không tác dụng gì".

**Mô hình đúng (giống module Search/Scrape đang chạy ổn định):** trình duyệt do extension tự điều hướng; CDP (nếu có) CHỈ để bơm input trusted, KHÔNG `ConnectOverCDP` + `GotoAsync` lái trang.

**Mục tiêu việc này (GĐ0 — kiểm chứng, là CỔNG cho cả dự án port sang extension):** thêm một **đường mở "sạch"** — mở Brave/Chrome với `--load-extension` **NHƯNG KHÔNG** `--remote-debugging-port`, **KHÔNG** `ConnectOverCDP`, **KHÔNG** `page.GotoAsync`, **KHÔNG** proxy. Người dùng đăng nhập tay tới `/portal/shop`, để **extension `shopee-orders-test` tự điều hướng + tự bắn TRUSTED click** (lúc này `chrome.debugger` mới attach được vì không ai giữ). Nếu mở shop KHÔNG dính captcha → xác nhận cơ chế extension đúng, mở khoá port toàn bộ. Nếu VẪN dính → dấu vết nằm sâu hơn, tính hướng khác.

**Kích hoạt:** một **nút POC riêng trong UI** (user đã chốt) — đặt ở màn **Cài đặt** (`SettingsView`), tách hẳn khỏi luồng "Kênh Người bán" production. **KHÔNG đụng `OpenAsync`/luồng Playwright hiện có** (giữ đường lui).

## 2. Phạm vi

- **Làm:**
  - Core: một hàm dựng args "sạch" (không remote-debugging-port, không proxy, có load-extension, có start URL) + một launcher nhỏ tự chứa `Process.Start` trả về `Process` (KHÔNG Playwright, KHÔNG CDP).
  - App: thêm card "POC — KIỂM CHỨNG (GĐ0)" ở màn Cài đặt với nút "Mở sạch (không CDP)" + nút "Đóng cửa sổ POC" + dòng thông báo.
  - Test: unit test cho hàm dựng args (khẳng định KHÔNG có remote-debugging-port/proxy, CÓ load-extension + start URL).
- **Không làm:**
  - KHÔNG sửa `ShopeeLoginService.OpenAsync` / `BraveLaunchArgs.BuildBraveArgs` (đường production giữ nguyên).
  - KHÔNG port các thao tác Seller Centre sang extension (đó là GĐ1+, làm sau khi GĐ0 đạt).
  - KHÔNG xử lý proxy cho đường POC (mở trực tiếp IP máy — đúng như Chrome tay đang chạy tốt; proxy-auth vốn cần CDP nên loại khỏi POC).
  - KHÔNG hỗ trợ fallback Chromium đóng gói của Playwright cho đường POC (để tránh kéo Playwright vào); yêu cầu có Brave/Chrome/Edge thật — không có thì báo lỗi rõ ràng.

## 3. Các bước thực hiện

### Hạng mục A — Core: hàm dựng args sạch + launcher (không đụng file production)

**A1. `orders/XuLyDonShopee.Core/Services/BraveLaunchArgs.cs`** — thêm hàm MỚI `BuildCleanPocArgs` (KHÔNG sửa `BuildBraveArgs` cũ):

```csharp
/// <summary>
/// Dựng args cho đường POC "mở sạch": KHÔNG --remote-debugging-port (không mở endpoint CDP), KHÔNG proxy,
/// CÓ --load-extension + start URL ở cuối. Mục tiêu: trình duyệt giống hệt bản mở tay (không có kênh CDP để
/// anti-bot soi / để Playwright attach), extension tự điều hướng + tự bắn trusted click qua chrome.debugger.
/// </summary>
public static IReadOnlyList<string> BuildCleanPocArgs(string userDataDir, string extensionPath, string startUrl)
```

Nội dung args (KHÁC `BuildBraveArgs` ở chỗ **bỏ** `--remote-debugging-port` và **bỏ** nhánh proxy, **thêm** startUrl cuối):
- `--user-data-dir={userDataDir}`
- `--profile-directory=Default`
- `--new-window`, `--no-first-run`, `--no-default-browser-check`, `--hide-crash-restore-bubble`
- nhóm chống-treo-nền: `--disable-background-timer-throttling`, `--disable-backgrounding-occluded-windows`, `--disable-renderer-backgrounding`
- `--disable-features=Translate,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling,DisableLoadExtensionCommandLineSwitch` (luôn có `DisableLoadExtensionCommandLineSwitch` vì POC luôn nạp extension)
- `--lang=vi-VN`
- `--disable-popup-blocking`
- `--load-extension={extensionPath}`
- `startUrl` (positional arg cuối cùng — mở URL kiểu người dùng, KHÔNG phải CDP navigation)

Yêu cầu bất biến (được test kiểm): danh sách trả về **KHÔNG chứa** chuỗi nào bắt đầu `--remote-debugging-port` và **KHÔNG chứa** `--proxy-server`.

**A2. Tạo file MỚI `orders/XuLyDonShopee.Core/Services/PocCleanLauncher.cs`** — launcher tự chứa, KHÔNG dùng Playwright:

```csharp
namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Mở trình duyệt cho đường POC GĐ0 "mở sạch": Process.Start Brave/Chrome/Edge thật với args từ
/// BraveLaunchArgs.BuildCleanPocArgs — KHÔNG Playwright, KHÔNG ConnectOverCDP, KHÔNG remote-debugging-port.
/// Trả về Process để tầng UI theo dõi/kill. Ném InvalidOperationException (message tiếng Việt) nếu thiếu
/// trình duyệt thật hoặc thiếu extension POC.
/// </summary>
public static class PocCleanLauncher
{
    public static System.Diagnostics.Process Open(string userDataDir, BrowserChoice browserChoice, string startUrl)
    {
        var exe = BrowserLocator.ResolveExecutable(browserChoice)
            ?? throw new InvalidOperationException(
                "POC 'Mở sạch' cần Brave/Chrome/Edge thật đã cài trên máy (không dùng Chromium đóng gói). " +
                "Hãy cài một trình duyệt và chọn ở Cài đặt → Trình duyệt.");

        var extPath = BraveLaunchArgs.ResolveOrdersExtension()
            ?? throw new InvalidOperationException(
                "Không tìm thấy thư mục extension 'shopee-orders-test' (cạnh app hoặc trong repo). " +
                "POC cần extension này để tự điều hướng + bắn trusted click.");

        System.IO.Directory.CreateDirectory(userDataDir);

        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in BraveLaunchArgs.BuildCleanPocArgs(userDataDir, extPath, startUrl))
            psi.ArgumentList.Add(arg);

        return System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Không khởi chạy được tiến trình trình duyệt POC.");
    }
}
```

(Kiểm tra namespace/enum `BrowserChoice`, `BrowserLocator.ResolveExecutable`, `BraveLaunchArgs.ResolveOrdersExtension` — đều đã có trong `XuLyDonShopee.Core.Services`/`.Models`. Nếu `BrowserChoice` ở namespace Models thì thêm `using`.)

### Hạng mục B — App: nút POC ở màn Cài đặt (phụ thuộc A)

**B1. `orders/XuLyDonShopee.App/ViewModels/SettingsViewModel.cs`**:
- Thêm field theo dõi tiến trình POC: `private System.Diagnostics.Process? _pocProcess;`
- Thêm `[ObservableProperty] private string? _pocMessage;`
- Thêm command mở:

```csharp
/// <summary>Nút "Mở sạch (không CDP)" — mở trình duyệt POC GĐ0 KHÔNG Playwright/CDP để kiểm chứng né captcha.
/// Dùng hồ sơ POC riêng (poc-clean-profile cạnh app.db), KHÔNG proxy, mở thẳng /portal/shop. Đăng nhập tay,
/// rồi dùng nút TRUSTED của extension. Áp lựa chọn trình duyệt đang lưu ở Cài đặt.</summary>
[RelayCommand]
private async Task OpenPocCleanAsync()
{
    try
    {
        // Đóng tiến trình POC cũ nếu còn (tránh khoá hồ sơ).
        TryKillPoc();
        var baseDir = System.IO.Path.GetDirectoryName(_services.Database.Path) ?? ".";
        var profileDir = System.IO.Path.Combine(baseDir, "poc-clean-profile");
        var choice = _services.Settings.GetBrowserChoice();
        var startUrl = ShopeeLoginService.ShopListUrl; // https://banhang.shopee.vn/portal/shop
        _pocProcess = await Task.Run(() => PocCleanLauncher.Open(profileDir, choice, startUrl));
        PocMessage = "Đã mở trình duyệt POC (sạch, không CDP). Đăng nhập tay tới /portal/shop rồi bấm nút đỏ TRUSTED của extension.";
    }
    catch (System.Exception ex)
    {
        PocMessage = "Lỗi mở POC: " + ex.Message;
    }
}

/// <summary>Nút "Đóng cửa sổ POC" — kill tiến trình trình duyệt POC đang mở (giải phóng khoá hồ sơ).</summary>
[RelayCommand]
private void ClosePoc()
{
    TryKillPoc();
    PocMessage = "Đã đóng cửa sổ POC (nếu đang mở).";
}

private void TryKillPoc()
{
    try { if (_pocProcess is { HasExited: false }) _pocProcess.Kill(entireProcessTree: true); }
    catch { /* bỏ qua */ }
    _pocProcess = null;
}
```

- Đảm bảo có `using System.Threading.Tasks;` (đã có) và `using System.Diagnostics;` nếu cần (hoặc dùng tên đầy đủ như trên).

**B2. `orders/XuLyDonShopee.App/Views/SettingsView.axaml`** — thêm card POC ở CỘT TRÁI, ngay dưới card "TRÌNH DUYỆT" (trong `StackPanel Grid.Column="0"`, sau `</Border>` của card Trình duyệt, còn trong cùng StackPanel):

```xml
<!-- ===== POC — KIỂM CHỨNG NÉ CAPTCHA (GĐ0) ===== -->
<TextBlock Classes="section" Text="POC — KIỂM CHỨNG NÉ CAPTCHA (GĐ0)" Margin="0,24,0,12" />
<Border Classes="card" Padding="24,22">
    <StackPanel Spacing="14">
        <TextBlock TextWrapping="Wrap" FontSize="11.5" Foreground="{StaticResource TextMuted}"
                   Text="Mở trình duyệt SẠCH: có nạp extension nhưng KHÔNG điều khiển qua CDP/Playwright (không remote-debugging-port, không proxy). Đăng nhập tay tới /portal/shop rồi bấm nút đỏ TRUSTED của extension để test bấm 'Chi tiết' có dính captcha không. Dùng để kiểm chứng cơ chế extension trước khi port toàn bộ." />
        <StackPanel Orientation="Horizontal" Spacing="12">
            <Button Classes="accent" Content="🧪 Mở sạch (không CDP)" Command="{Binding OpenPocCleanCommand}" />
            <Button Classes="secondary" Content="Đóng cửa sổ POC" Command="{Binding ClosePocCommand}" />
        </StackPanel>
        <TextBlock Text="{Binding PocMessage}" Foreground="{StaticResource SuccessBrush}" VerticalAlignment="Center"
                   TextWrapping="Wrap"
                   IsVisible="{Binding PocMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
    </StackPanel>
</Border>
```

(Kiểm tra tên command sinh bởi CommunityToolkit.Mvvm: `OpenPocCleanAsync` → `OpenPocCleanCommand`; `ClosePoc` → `ClosePocCommand`. Class/brush `card`/`section`/`accent`/`secondary`/`SuccessBrush`/`TextMuted` đều đã dùng sẵn trong file này.)

### Hạng mục C — Test

**C1. `orders/XuLyDonShopee.Tests/`** — thêm file test (vd `BraveCleanPocArgsTests.cs`) cho `BuildCleanPocArgs`:
- Gọi `BraveLaunchArgs.BuildCleanPocArgs("C:/tmp/prof", "C:/ext/shopee-orders-test", "https://banhang.shopee.vn/portal/shop")`.
- Assert: KHÔNG phần tử nào `StartsWith("--remote-debugging-port")`.
- Assert: KHÔNG phần tử nào `StartsWith("--proxy-server")`.
- Assert: CÓ phần tử `== "--load-extension=C:/ext/shopee-orders-test"`.
- Assert: CÓ chứa `--user-data-dir=C:/tmp/prof` và phần tử cuối `== "https://banhang.shopee.vn/portal/shop"`.
- Assert: `--disable-features=...` CÓ chứa `DisableLoadExtensionCommandLineSwitch`.

(Theo mẫu test thuần sẵn có trong `XuLyDonShopee.Tests` — cùng framework, không cần dựng trình duyệt.)

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App` (hoặc build solution orders) **xanh**, không warning mới liên quan.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` **xanh** — toàn bộ test cũ (774+) vẫn qua + test mới `BuildCleanPocArgs` qua.
- [ ] `BuildBraveArgs` cũ và `OpenAsync`/luồng Playwright **giữ nguyên** (git diff không chạm 2 chỗ này ngoài việc thêm hàm mới trong cùng file `BraveLaunchArgs.cs`).
- [ ] Diff đúng phạm vi: chỉ 2 file Core (`BraveLaunchArgs.cs` thêm hàm, `PocCleanLauncher.cs` mới) + 2 file App (`SettingsViewModel.cs`, `SettingsView.axaml`) + 1 file test.
- [ ] (Verify tay do Fable/user sau) Chạy app → Cài đặt → card POC → "Mở sạch": cửa sổ trình duyệt mở tới `/portal/shop`, KHÔNG có endpoint remote-debugging (kiểm bằng dòng lệnh tiến trình không có `--remote-debugging-port`), extension hiện panel nút; nút "Đóng cửa sổ POC" kill được cửa sổ.

## 5. Rủi ro & lưu ý

- **Namespace/using:** `BrowserChoice` có thể ở `XuLyDonShopee.Core.Models`. Kiểm tra và thêm `using` trong `PocCleanLauncher.cs`.
- **Hồ sơ POC bị khoá:** nếu cửa sổ POC cũ còn mở, lần "Mở sạch" sau dùng cùng `poc-clean-profile` sẽ xung đột khoá → đã xử lý bằng `TryKillPoc()` đầu mỗi lần mở + nút "Đóng cửa sổ POC".
- **Extension phải là bản MỚI:** đường POC dựa vào `BraveLaunchArgs.ResolveOrdersExtension()` (đi từ thư mục exe ngược lên tìm `extensions/shopee-orders-test` đầu tiên). Bản đã cài/publish có thể còn bản extension cũ (2 nút). Đây là vấn đề deploy, KHÔNG thuộc code lần này; Fable đã đồng bộ tay bản cài. Khi test thật cần đảm bảo extension cạnh exe là bản có nút TRUSTED.
- **KHÔNG proxy trong POC:** đúng chủ đích (mirror Chrome mở tay chạy tốt). Nếu sau này cần proxy cho GĐ1, xử lý riêng (proxy-auth cần cơ chế khác vì bỏ CDP).
- **Process mồ côi:** POC không gắn vào `AccountSessionManager.StopAllAsync`. Chấp nhận cho POC; user đóng cửa sổ tay hoặc bấm "Đóng cửa sổ POC". Không cần thêm hook shutdown ở lần này.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
