# Plan: Sửa lỗi "Trình duyệt thoát ngay khi khởi động" ở bước đăng nhập Playwright (module Đơn hàng)

- **Ngày:** 2026-08-06
- **Trạng thái:** hoàn thành
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
- **Chi phí:** mỗi vòng thêm 2 lần chạy PowerShell (bước đăng nhập + dọn cuối vòng; cộng với 1 lần vốn có của
  đường cầu nối là 3 lần/vòng), mỗi lần ~0,5–3,2s. Không đáng kể so với vòng vài chục phút.
- **Không nuốt lỗi thật:** chỉ thử lại đúng ca "thoát sớm"; hủy (Dừng) phải thoát ngay, không thử lại.
- Sau khi xong: hỏi user có phát hành client (`version.txt` + Velopack) hay không — plan này không tự release.

---

## Báo cáo thực thi

Phiên chính tự thực thi (2026-08-06). Đã làm đúng 6 bước của plan, không đổi hướng:

- **Mới** `orders/XuLyDonShopee.Core/Services/BrowserProfileGuard.cs`: `FreeProfile(userDataDir, alsoMatchBridgeExtension)`
  + `BuildProcessFilter` (thuần) + `ClearProfileSessionAndLocks` (chuyển nguyên văn từ `OrdersBridgeLauncher`).
- `OrdersBridgeLauncher`: hai hàm private đã chuyển đi, gọi `FreeProfile(..., alsoMatchBridgeExtension: true)` →
  mệnh đề lọc sinh ra **giống hệt** bản cũ (có test khoá nguyên văn chuỗi).
- `LoginBrowserBootstrap`: tách `LaunchOnceAsync`; vòng thử tối đa 2 lần, mỗi lần `FreeProfile(..., false)` trước khi
  phóng, chỉ thử lại khi `BrowserExitedEarlyException` (kiểu riêng, `when (lan == 1)`); thông báo lỗi mới có tên
  trình duyệt + mã thoát + đường dẫn hồ sơ (`MoTaThoatSom`).
- `OrdersBridgeSession.DongTrinhDuyetSach()`: kill handle **rồi** quét theo hồ sơ; `AccountSession` (finally cuối
  vòng) gọi hàm này thay cho kill-theo-handle.
- **Mới** `orders/XuLyDonShopee.Tests/BrowserProfileGuardTests.cs` (8 test).

Kiểm chứng thật (phiên chính tự chạy):

- `dotnet build orders/XuLyDonShopee.App/XuLyDonShopee.App.csproj` → **0 Warning, 0 Error**.
- `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` → **1558/1558 xanh**.
- **Thử phá luật** (4 đột biến cùng lúc: bỏ escape `'`, luôn bật nhánh `shopee-orders`, xóa thêm `Cookies`, bỏ
  tên+mã thoát khỏi thông báo) → đúng **5 test mới đỏ**
  (`…KhongBatExtension…`, `…DuongDanCoNhayDon_DuocEscape`, `…GiuCookies`, 2 test `MoTaThoatSom`), 3 test còn lại
  xanh như mong đợi. Khôi phục → 1558/1558 xanh trở lại.
### Vòng 2 — sau phản biện của `nghiem-thu`

`nghiem-thu` chấm ĐẠT CÓ ĐIỀU KIỆN và nêu 2 điểm nặng cùng vài điểm nhỏ; đã sửa hết những điểm đúng:

1. **Test không canh chính bản vá** (agent chứng minh: gỡ hẳn dòng `FreeProfile` khỏi `LaunchAndConnectAsync`
   mà 1558 test vẫn xanh — 4 đột biến vòng 1 đều rơi vào hàm phụ trợ). → Tách luồng điều khiển thành
   `LoginBrowserBootstrap.PhongVoiDonHoSoAsync` (nhận delegate) + bộ test mới
   `orders/XuLyDonShopee.Tests/PhongVoiDonHoSoTests.cs` (7 test): dọn trước MỖI lần phóng, thử lại đúng 1 lần,
   chỉ thử lại ca thoát sớm, lỗi khác ném ngay, hủy thì chưa kịp dọn/không thử tiếp.
2. **`FreeProfile` nuốt lỗi không dấu vết** (trái `orders/CLAUDE.md`; PowerShell bị chặn / WMI hỏng / hết giờ →
   vòng vẫn chết mà log trắng). → Thêm tham số `log`, rót từ `_log` của phiên qua `ShopeeLoginService.OpenAsync`,
   `OrdersBridgeLauncher.Launch`, `DongTrinhDuyetSach`. Script dọn in `killed=<n>;conlai=<m>`: im lặng khi hồ sơ
   vốn rảnh, báo khi có đóng cửa sổ, **kêu ⚠ khi còn sót / không đọc được kết quả / PS treo**. Hết giờ thì kill
   luôn PowerShell (trước đây rò tiến trình).
3. **Lượt thử lại không có log** → đã log "Mở trình duyệt đăng nhập hỏng ở lần N…".
4. **Escape thiếu ký tự đại diện `-like`** → `EscapeLikePattern`: `` ` `` → ` `` ` rồi `[` → `` `[ `` rồi `'` → `''`
   (đúng thứ tự, có test cả ca tên chứa dấu huyền ngang).
5. **Thông báo thiếu "thử lần mấy"** (plan §3 bước 3) → đã thêm `lần thử N/2`.
6. **Xmldoc sai về Windows** (xóa `Singleton*` không phải thứ chống handoff ở Windows — đó là cửa sổ ẩn + mutex
   theo hồ sơ; thứ thật sự chống handoff là bước KILL) → đã viết lại cảnh báo.
7. Plan §5 ghi sai "+1 lần PowerShell/vòng" → sửa thành 3 lần/vòng.

Không nhận điểm #9 (LF/CRLF) — vô hại, git tự xử theo `core.autocrlf`.

### Kiểm chứng vòng 2

- `dotnet build …App.csproj` → **0 Warning, 0 Error**; `dotnet test` → **1578/1578 xanh**.
- **Đột biến luồng điều khiển** (`when (lan < soLanToiDa)` → `when (false)` + bỏ `ct.ThrowIfCancellationRequested()`)
  → **4/7 test mới đỏ** đúng chỗ (`ThoatSomLanDau…`, `ThoatSomCaHaiLan…`, `DaHuy…`, `HuyTrongLucCho…`). Khôi phục → xanh.
- **Test tích hợp chạy PowerShell THẬT** (`FreeProfile_ChayTHATTrenWindows_HoSoKhongAiGiu_ThiImLang`, đường dẫn có
  khoảng trắng + nháy đơn + `[ ]`) → xanh ⇒ chuỗi lệnh mới không hỏng cú pháp.
- **✅ ĐÃ KIỂM TAY KỊCH BẢN HANDOFF THẬT** (tiêu chí §4 mục 5 — vòng 1 còn nợ). Test tạm `_TempHandoffVerify`
  (đã xóa sau khi chạy) trên máy dev, dùng Chrome thật + hồ sơ tạm:
  - mở 1 cửa sổ giữ hồ sơ → phóng bản CDP vào đúng hồ sơ đó ⇒ `lan1 thoat som = True, exitCode = 0,
    co portFile = False` — **tái hiện chính xác lỗi production** (thoát ngay, mã 0, không có `DevToolsActivePort`);
  - gọi `BrowserProfileGuard.FreeProfile` ⇒ log `đã đóng 13 cửa sổ còn giữ hồ sơ trước khi mở phiên mới`, không có ⚠;
  - phóng lại ⇒ `lan2 co portFile = True` — **mở được cổng CDP**.
  Dọn sạch sau test: không còn tiến trình `handoff-*` nào, thư mục tạm đã xóa.

### Còn nợ / hạn chế (khai rõ)

- Không có test nào bắt được ca "xóa hẳn lời gọi `PhongVoiDonHoSoAsync` khỏi `LaunchAndConnectAsync`" — muốn bắt
  phải trừu tượng hóa cả `BrowserProcessStarter` lẫn Playwright, không đáng cho một dây nối 1 dòng.
- `FreeProfile` là **Windows-only** (`OperatingSystem.IsWindows()`); nhánh `avalonia` cho Ubuntu vẫn mang lỗi cũ.
- Chưa chạy thử trên máy production của user (mới chỉ máy dev).
