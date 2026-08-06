# Plan: Sửa lỗi "Trình duyệt thoát ngay khi khởi động" ở bước đăng nhập Playwright (module Đơn hàng)

- **Ngày:** 2026-08-06
- **Trạng thái:** đang làm
- **Người lập:** phiên chính (Opus 5) · **Người thực thi:** phiên chính, `nghiem-thu` phản biện

## 1. Bối cảnh & mục tiêu

Nhật ký production (máy user, tài khoản `hoangdh200392:muinx`, 06/08 09:01:03):

```
Đăng nhập Nền tảng tài khoản phụ bằng trình duyệt điều khiển (Playwright)...
09:01:03 Cầu nối lỗi: ... Trình duyệt thoát ngay khi khởi động (thường do hồ sơ đang bị một cửa sổ Brave khác khóa)
09:01:03 Vòng cầu nối chưa trọn: ...
09:01:03 Vòng kế tiếp dự kiến bắt đầu lúc 10:01:03.
```

Stack: `LoginBrowserBootstrap.WaitForDevToolsPortAsync` ← `LaunchAndConnectAsync` ←
`ShopeeLoginService.OpenAsync` ← `OrdersBridgeSession.LoginAndReachPickerAsync` ← `RunAllShopsAsync`.
⇒ Hỏng ngay bước ĐẦU của vòng: mở trình duyệt điều khiển (Playwright/CDP) để đăng nhập subaccount.
Cả vòng chết → **mất trọn 1 tiếng** không xử đơn nào.

### Nguyên nhân (đã chứng minh bằng log cũ)

`WaitForDevToolsPortAsync` ném thông báo đó khi `process.HasExited` == true ngay sau khi phóng. Chromium
(Brave/Chrome/Edge) **thoát ngay lập tức** khi đã có một tiến trình khác đang giữ đúng `--user-data-dir` đó:
cơ chế *process singleton* chuyển dòng lệnh cho tiến trình cũ (kèm `--new-window` → cửa sổ mới ở tiến trình
cũ) rồi tiến trình mới tự thoát ⇒ không bao giờ ghi `DevToolsActivePort` ⇒ ta báo lỗi.

Bằng chứng trong `%APPDATA%\XuLyDonShopee\logs\hoatdong-20260729.log`:

```
01:35:33  Nghỉ ~4' trước shop kế...            ← vòng CŨ đang chạy, trình duyệt sạch ĐANG MỞ trên hồ sơ đó
01:40:26  [Hàng loạt] Đã mở phiên chạy cho 1 tài khoản đã chọn.
01:40:26  Đăng nhập Nền tảng tài khoản phụ bằng trình duyệt điều khiển (Playwright)...
01:40:27  Cầu nối lỗi: ... Trình duyệt thoát ngay khi khởi động ...   ← đúng 1 GIÂY sau = handoff, không phải timeout
```

Lỗ hổng cấu trúc: đường **trình duyệt sạch** (`OrdersBridgeLauncher.Launch`) ĐÃ có bước dọn đường bắt buộc —
`KillBrowsersOnProfile` (kill theo dòng lệnh + poll tới khi chết hẳn) rồi `ClearProfileSessionAndLocks` — nên
nó tự lành. Đường **đăng nhập Playwright** (`LoginBrowserBootstrap.LaunchAndConnectAsync`) chạy TRƯỚC đó lại
**không dọn gì cả**, phóng thẳng vào hồ sơ ⇒ hễ còn sót cửa sổ nào của hồ sơ là chết vòng.

Nguồn "sót cửa sổ" đã biết:
- User bấm Dừng rồi Chạy lại ngay (kill là bất đồng bộ — 29/07 chính là ca này).
- Trình duyệt sạch của vòng trước không chết: `AccountSession` (dòng ~485) chỉ
  `if (p is { HasExited: false }) p.Kill(entireProcessTree: true)`. Chrome/Brave có thể **fork tiến trình
  browser THẬT sang PID khác rồi stub thoát** (chính `suite/Shopee.Core/Browser/BraveJobObject.cs:26` ghi nhận
  hành vi này) ⇒ handle ta giữ đã `HasExited` ⇒ **bỏ qua kill** ⇒ browser thật sống mồ côi cả tiếng, giữ hồ sơ.
- App crash/force-kill trước đó để lại mồ côi (Job Object đỡ được phần lớn nhưng không phải mọi ca).

Mục tiêu: bước đăng nhập Playwright **tự dọn hồ sơ trước khi phóng** (như đường trình duyệt sạch đã làm),
có **thử lại 1 lần** nếu vẫn thoát sớm, và thông báo lỗi nói đúng trình duyệt + mã thoát + đường dẫn hồ sơ.
Đồng thời **bịt nguồn sót** ở cuối mỗi vòng (kill theo dòng lệnh chứ không chỉ theo handle).

## 2. Phạm vi

- **Làm:**
  - Tách phần dọn hồ sơ của `OrdersBridgeLauncher` ra lớp dùng chung, cho đường Playwright dùng lại.
  - `LoginBrowserBootstrap`: dọn hồ sơ trước khi phóng + thử lại 1 lần khi trình duyệt thoát sớm + thông báo
    lỗi có mã thoát/tên trình duyệt/đường dẫn hồ sơ.
  - Cuối mỗi vòng (`AccountSession`): đóng trình duyệt sạch bằng đường dọn theo hồ sơ (không chỉ theo handle).
  - Test cho phần thuần + phần đụng file (thư mục tạm).
- **Không làm:**
  - Không đổi luồng nghiệp vụ (login → SSO → lặp shop), không đổi bộ cờ trình duyệt, không đổi
    `OrdersBridgeChannel`/extension.
  - Không đổi nhịp nghỉ giữa hai vòng (vẫn `GetOrderIntervalMinutes()`), không thêm cơ chế thử-lại-sớm cho cả
    vòng — nêu ra cho user quyết sau.
  - Không đụng đường Dừng ở `AccountsViewModel.Phien.cs` (đã được đường dọn mới che).
  - Không bump version / release / deploy trong plan này.

## 3. Các bước thực hiện

1. **`orders/XuLyDonShopee.Core/Services/BrowserProfileGuard.cs`** (mới, `internal static`)
   - `FreeProfile(string userDataDir, bool alsoMatchBridgeExtension)`: gọi `KillBrowsersOnProfile` rồi
     `ClearProfileSessionAndLocks` — chuyển nguyên văn thân hàm từ `OrdersBridgeLauncher` (giữ y hệt hành vi:
     PowerShell + CIM, vòng 8 lần × 400ms, `WaitForExit(10000)`, Windows-only, best-effort nuốt lỗi).
   - `BuildProcessFilter(string userDataDir, bool alsoMatchBridgeExtension)`: **hàm thuần** dựng mệnh đề
     `Where-Object` (escape `'` thành `''`); có `alsoMatchBridgeExtension` mới thêm nhánh `*shopee-orders*`.
     → test được không cần chạy PowerShell.
   - `ClearProfileSessionAndLocks` giữ nguyên danh sách file (Current/Last Session|Tabs, thư mục Sessions,
     Singleton{Lock,Cookie,Socket}); **KHÔNG** đụng `Cookies` (giữ đăng nhập).
2. **`orders/XuLyDonShopee.Core/Services/OrdersBridgeLauncher.cs`**
   - Bỏ hai hàm private đã chuyển, gọi `BrowserProfileGuard.FreeProfile(userDataDir, alsoMatchBridgeExtension: true)`
     đúng chỗ cũ (sau `PrepareFreshExtensionCopy`, trước `PocCleanLauncher.Open`). Hành vi bridge KHÔNG đổi.
3. **`orders/XuLyDonShopee.Core/Services/LoginBrowserBootstrap.cs`**
   - Trước khi phóng: `BrowserProfileGuard.FreeProfile(userDataDir, alsoMatchBridgeExtension: false)`.
     Vì sao KHÔNG match `shopee-orders` ở đây: bước này chỉ cần hồ sơ của CHÍNH tài khoản đang chạy được rảnh;
     giết theo tên extension sẽ đụng cửa sổ của tài khoản khác (blast radius rộng hơn cần thiết).
   - Tách phần "phóng + chờ CDP + nối" thành hàm cục bộ, bọc vòng **thử tối đa 2 lần**: lần 1 thoát sớm →
     `FreeProfile` lại + chờ ~1s → phóng lại. Chỉ thử lại đúng ca "thoát sớm" (dùng exception riêng
     `BrowserExitedEarlyException : InvalidOperationException`), KHÔNG thử lại khi hủy (`OperationCanceledException`)
     hay lỗi khác. Giữ nguyên khối dọn dẹp trong `catch` (đóng CDP + kill cây + dispose) cho MỌI lần thử.
   - Thông báo lỗi thoát sớm ghi rõ: tên file trình duyệt, **mã thoát**, đường dẫn hồ sơ, và đã thử lại mấy lần.
4. **`orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`**
   - Thêm `public void DongTrinhDuyetSach()`: kill `Process` (nếu còn) **và** gọi
     `BrowserProfileGuard.FreeProfile(_userDataDir, alsoMatchBridgeExtension: false)` để quét cả tiến trình
     browser thật đã fork sang PID khác. Nuốt lỗi (best-effort).
5. **`orders/XuLyDonShopee.App/Services/AccountSession.cs`** (~dòng 485)
   - Thay `try { var p = bridge.Process; if (p is {HasExited:false}) p.Kill(...); }` bằng
     `bridge.DongTrinhDuyetSach()`.
6. **Test** `orders/XuLyDonShopee.Tests/BrowserProfileGuardTests.cs` (mới)
   - `BuildProcessFilter`: chứa đường dẫn hồ sơ; có/không nhánh `shopee-orders` theo cờ; escape `'` thành `''`.
   - `ClearProfileSessionAndLocks` trên thư mục tạm: xóa `SingletonLock`/`Current Session`/thư mục `Sessions`,
     **giữ** `Default\Cookies`.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App/XuLyDonShopee.App.csproj` — 0 warning, 0 error.
- [ ] `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` — xanh toàn bộ; test mới có mặt.
- [ ] Thử phá luật để chứng minh test có canh thật: đảo cờ `alsoMatchBridgeExtension` / bỏ escape `'` →
      test mới PHẢI đỏ; khôi phục → xanh.
- [ ] Đọc lại diff: đường bridge (`OrdersBridgeLauncher`) sinh ra **đúng** mệnh đề lọc như trước khi tách
      (có `shopee-orders`), tức refactor không đổi hành vi.
- [ ] Kiểm tay trên máy dev: mở một Chrome/Brave với `--user-data-dir=<hồ sơ test>` rồi chạy nhánh login →
      trước khi sửa: lỗi "thoát ngay khi khởi động"; sau khi sửa: cửa sổ cũ bị đóng, phiên mới mở được.
      (Nếu không dựng được kịch bản này thì ghi rõ là chưa kiểm, không nhận bừa.)

## 5. Rủi ro & lưu ý

- **Giết nhầm trình duyệt cá nhân:** bộ lọc khớp theo `--user-data-dir` của hồ sơ trong
  `%APPDATA%\XuLyDonShopee\profiles\<id>-<kind>` nên không đụng Chrome/Brave cá nhân (khác thư mục). Giữ
  nguyên logic cũ, không nới rộng.
- **Đa tài khoản trên cùng máy:** cầu nối hiện là 1 tài khoản/lúc (cổng 47821 cố định), nhưng vẫn chọn
  `alsoMatchBridgeExtension: false` ở đường login + cuối vòng để không cướp cửa sổ của phiên khác.
- **Chi phí:** mỗi vòng thêm 1 lần chạy PowerShell (~0,5–3,2s). Không đáng kể so với vòng vài chục phút.
- **Không nuốt lỗi thật:** chỉ thử lại đúng ca "thoát sớm"; hủy (Dừng) phải thoát ngay, không thử lại.
- Sau khi xong: hỏi user có phát hành client (`version.txt` + Velopack) hay không — plan này không tự release.

---

## Báo cáo thực thi

<điền sau khi xong>
