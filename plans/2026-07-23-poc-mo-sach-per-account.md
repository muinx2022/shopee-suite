# Plan: Chuyển nút POC "Mở sạch" sang PER-ACCOUNT (cạnh nút Chạy), mở đúng hồ sơ acc

- **Ngày:** 2026-07-23
- **Trạng thái:** hoàn thành — **GĐ0 ĐẠT (verified 2026-07-23):** bấm "🧪 Mở sạch" mở trình duyệt sạch + nút TRUSTED của extension → "Chi tiết" KHÔNG còn dính captcha. Xác nhận gốc rễ là Playwright/CDP lái trang, không phải cú click.
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nối tiếp:** `plans/2026-07-23-poc-mo-sach-khong-cdp.md` (Core đã xong: `BuildCleanPocArgs` + `PocCleanLauncher` GIỮ NGUYÊN, tái dùng)

## 1. Bối cảnh & mục tiêu

Bản trước đặt nút POC "Mở sạch" ở màn Cài đặt với hồ sơ POC trắng (`poc-clean-profile`) → user phản hồi: **nút này phải là nút chạy cho CHÍNH tài khoản đang chọn**, mở đúng hồ sơ đã đăng nhập của acc đó (khỏi đăng nhập tay), đặt cạnh nút "▶ Chạy" ở màn Tài khoản.

**Mục tiêu:** DỜI nút POC từ Cài đặt sang **màn Tài khoản**, cạnh "▶ Chạy" / "■ Dừng". Bấm = mở trình duyệt SẠCH (không CDP/Playwright, không proxy) với **đúng hồ sơ persistent của tài khoản đang xem** (cùng thư mục mà phiên production dùng) → vào thẳng `/portal/shop` (đã đăng nhập nếu hồ sơ có phiên). Để user bấm nút TRUSTED của extension kiểm chứng né captcha.

Core (`BraveLaunchArgs.BuildCleanPocArgs`, `PocCleanLauncher.Open`) đã có và ĐÚNG — **tái dùng nguyên**, không sửa.

## 2. Phạm vi

- **Làm:**
  - GỠ card POC khỏi màn Cài đặt (SettingsViewModel + SettingsView.axaml) — trả 2 file này về trạng thái trước bản POC.
  - THÊM nút "🧪 Mở sạch" per-account ở `AccountsView.axaml` (cạnh Chạy/Dừng) + command trong `AccountsViewModel` mở POC với hồ sơ của acc đang chọn.
  - Quản lý vòng đời tối thiểu: từ chối mở POC khi phiên production của acc đang chạy; "■ Dừng" đóng luôn cửa sổ POC; "▶ Chạy" kill POC trước (tránh đụng khoá hồ sơ chung).
- **Không làm:**
  - KHÔNG sửa Core (`BuildCleanPocArgs`/`PocCleanLauncher`) — đã đúng.
  - KHÔNG đụng `OpenAsync`/luồng Playwright/`Sessions.Start` production.
  - KHÔNG tạo hồ sơ POC riêng — DÙNG CHUNG hồ sơ persistent của acc (để tận dụng phiên đã đăng nhập).
  - Test `BraveCleanPocArgsTests` giữ nguyên (Core không đổi).

## 3. Các bước thực hiện

### Bước 1 — Gỡ card POC khỏi Cài đặt (hoàn nguyên bản trước POC)

**`orders/XuLyDonShopee.App/ViewModels/SettingsViewModel.cs`** — XÓA những gì bản POC trước thêm:
- Field `_pocProcess`, `[ObservableProperty] _pocMessage`.
- Command `OpenPocCleanAsync` (`OpenPocCleanCommand`), `ClosePoc` (`ClosePocCommand`), helper `TryKillPoc`.
→ File về đúng như trước commit POC (dùng `git show HEAD:...` để đối chiếu nếu cần; hiện các thay đổi POC CHƯA commit nên chỉ cần bỏ phần đã thêm).

**`orders/XuLyDonShopee.App/Views/SettingsView.axaml`** — XÓA block comment + Border card "POC — KIỂM CHỨNG NÉ CAPTCHA (GĐ0)" đã thêm (khối ngay dưới card TRÌNH DUYỆT). Phần còn lại giữ nguyên.

### Bước 2 — Thêm command POC per-account vào `AccountsViewModel.cs`

Thêm field theo dõi tiến trình + command. Dùng ĐÚNG công thức profile-dir mà `AccountSession` dùng (đã xác minh tại `AccountSession.cs` ~dòng 1770-1781):

```csharp
/// <summary>Tiến trình trình duyệt POC "mở sạch" (không CDP) đang mở cho tài khoản đang chọn; null = không có.</summary>
private System.Diagnostics.Process? _pocProcess;

/// <summary>
/// "🧪 Mở sạch (POC)" — mở trình duyệt SẠCH (KHÔNG Playwright/CDP, KHÔNG remote-debugging-port, KHÔNG proxy)
/// với ĐÚNG hồ sơ persistent của tài khoản đang xem (cùng thư mục phiên production dùng → đã đăng nhập nếu có
/// phiên) → /portal/shop. Để kiểm chứng GĐ0: bấm nút TRUSTED của extension xem "Chi tiết" có dính captcha không.
/// Gate như CanRun (đang xem 1 acc đã lưu). Phiên production của acc đang chạy → từ chối (đụng khoá hồ sơ chung).
/// </summary>
[RelayCommand]
private void MoSachPoc()
{
    if (_editingId is not long accountId)
    {
        return;
    }

    var email = _services.Accounts.GetById(accountId)?.Email ?? EditEmail;

    // Đang có phiên production (Playwright) trên hồ sơ này → không mở POC (Chromium chỉ cho 1 tiến trình/hồ sơ).
    if (_services.Sessions.IsRunning(accountId))
    {
        const string msg = "Đang có phiên chạy — bấm ■ Dừng trước khi Mở sạch (POC).";
        _services.Log.Append(email, msg);
        BusyStatus = msg;
        return;
    }

    try
    {
        TryKillPoc(); // đóng cửa sổ POC cũ (nếu còn) trước khi mở mới — tránh khoá hồ sơ

        // Công thức hồ sơ Y HỆT AccountSession: baseDir = thư mục Database.Path; kind theo browserChoice ở Cài đặt.
        var baseDir = System.IO.Path.GetDirectoryName(_services.Database.Path) ?? ".";
        var browserChoice = _services.Settings.GetBrowserChoice();
        var browserKind = BrowserLocator.ResolveBrowserKind(browserChoice);
        var userDataDir = BrowserProfilePaths.ForAccount(baseDir, accountId, browserKind);

        _pocProcess = PocCleanLauncher.Open(userDataDir, browserChoice, ShopeeLoginService.ShopListUrl);
        _services.Log.Append(email,
            "Mở sạch (POC): trình duyệt KHÔNG CDP + hồ sơ của tài khoản này → /portal/shop. Dùng nút TRUSTED của extension để test 'Chi tiết'.");
        BusyStatus = "Đã mở POC (sạch, không CDP) cho tài khoản này.";
    }
    catch (System.Exception ex)
    {
        _services.Log.Append(email, "Lỗi mở POC: " + ex.Message);
        BusyStatus = "Lỗi mở POC: " + ex.Message;
    }
}

/// <summary>Kill tiến trình trình duyệt POC đang mở (nếu có) — giải phóng khoá hồ sơ dùng chung với phiên production.</summary>
private void TryKillPoc()
{
    try { if (_pocProcess is { HasExited: false }) _pocProcess.Kill(entireProcessTree: true); }
    catch { /* bỏ qua */ }
    _pocProcess = null;
}
```

Chỉnh 2 điểm vòng đời (tránh đụng khoá hồ sơ chung giữa POC và production):
- Trong `Run()` (command "▶ Chạy"): gọi `TryKillPoc();` NGAY ĐẦU (trước `Sessions.Start`) — để phiên production launch không vướng khoá hồ sơ do POC giữ.
- Trong `Stop()` (command "■ Dừng"): gọi `TryKillPoc();` (Dừng cũng đóng cửa sổ POC của acc này cho trực giác).

Thêm gate cho nút (dùng lại `CanRun` sẵn có — đúng điều kiện "đang xem 1 acc đã lưu"): không cần property mới, bind `IsEnabled="{Binding CanRun}"`.

(Namespace: `BrowserLocator`, `BrowserProfilePaths`, `PocCleanLauncher`, `ShopeeLoginService` đều ở `XuLyDonShopee.Core.Services` — đã có `using` ở đầu `AccountsViewModel.cs`.)

### Bước 3 — Thêm nút vào `AccountsView.axaml`

Trong `WrapPanel` chứa "▶ Chạy"/"■ Dừng" (khoảng dòng 320-331), thêm nút POC (đặt SAU "■ Dừng" hoặc giữa Chạy/Dừng tùy gọn):

```xml
<!-- Mở sạch (POC GĐ0): mở trình duyệt KHÔNG CDP với hồ sơ acc này để kiểm chứng né captcha ở nút "Chi tiết". -->
<Button Classes="secondary formIcon" Content="🧪 Mở sạch" Margin="8,4,0,4"
        Command="{Binding MoSachPocCommand}"
        IsEnabled="{Binding CanRun}"
        ToolTip.Tip="Mở sạch (POC) — mở trình duyệt KHÔNG điều khiển qua CDP/Playwright với đúng hồ sơ đã đăng nhập của tài khoản này (vào /portal/shop). Dùng nút TRUSTED của extension để test bấm 'Chi tiết' có dính captcha không. Dừng phiên đang chạy trước khi bấm." />
```

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App` XANH, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` XANH (916 pass giữ nguyên — Core không đổi).
- [ ] Card POC KHÔNG còn ở màn Cài đặt (SettingsView/SettingsViewModel về trạng thái trước POC — diff 2 file này so với HEAD = 0 dòng POC).
- [ ] Nút "🧪 Mở sạch" xuất hiện cạnh "▶ Chạy"/"■ Dừng" ở màn Tài khoản, bật khi đang xem 1 acc đã lưu (CanRun), mờ khi chưa chọn/đang tạo mới.
- [ ] Diff đúng phạm vi: `AccountsViewModel.cs`, `AccountsView.axaml` (thêm), `SettingsViewModel.cs`, `SettingsView.axaml` (gỡ POC). Core + test KHÔNG đổi.
- [ ] (Verify tay do user) Bấm "🧪 Mở sạch" khi chọn 1 acc → cửa sổ trình duyệt mở tới `/portal/shop` với hồ sơ acc (không remote-debugging-port); extension hiện panel; "■ Dừng" đóng được cửa sổ POC.

## 5. Rủi ro & lưu ý

- **Khoá hồ sơ dùng chung:** POC và phiên production dùng CHUNG một thư mục hồ sơ (theo acc × trình duyệt). Chromium chỉ cho 1 tiến trình/hồ sơ → đã xử: từ chối mở POC khi `Sessions.IsRunning`; `Run()` kill POC trước; `Stop()` kill POC. Nếu user đóng app khi POC còn mở → cửa sổ POC KHÔNG bị StopAllAsync kill (chấp nhận cho POC; user tự đóng).
- **Hồ sơ chưa đăng nhập:** acc chưa có cookie/phiên (vd hoangdh200392 trong ảnh: "Cookie: Chưa có") → mở /portal/shop sẽ bị đá về đăng nhập → user đăng nhập tay lần đầu; lần sau hồ sơ giữ phiên.
- **Không proxy:** đúng chủ đích (mirror Chrome mở tay). App hiện 0 proxy nên trùng điều kiện production.
- **Extension bản mới:** `ResolveOrdersExtension` phải trỏ bản có nút TRUSTED cạnh exe — đã đồng bộ bản cài; bản build/publish sau phải kèm extension mới.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
