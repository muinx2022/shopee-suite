# Plan: Orders dùng hạ tầng chung `shared/Shopee.Toolkit` (3F)

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành (điểm chặn `server/**` đã gỡ ở lượt 2, sau khi P4-hub merge)
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

`orders/` không ref `suite/Shopee.Core` (chủ đích — né dây Avalonia). Hệ quả: 4 hạ tầng bị chép tay 2 bản, đã bắt đầu LỆCH (kiểm chứng 30/07):

1. **WebSocketServer:** `orders/XuLyDonShopee.Core/Services/OrdersWebSocketServer.cs` (155 dòng, header tự khai "Chép khuôn WebSocketServer của module Search") vs `suite/Shopee.Module.Search/Engine/WebSocketServer.cs` (126 dòng). **Drift mới:** bản orders có `SendAsync` fail-fast (fix 1B.3) + `SendOptions` — bản Search KHÔNG. Hợp nhất PHẢI giữ fail-fast.
2. **Brave launch args:** `orders/…/Services/BraveLaunchArgs.cs` (150 dòng: BuildArgs + BuildCleanPocArgs, hỗ trợ --load-extension + DisableLoadExtensionCommandLineSwitch) vs `suite/Shopee.Core/Browser/BraveArgsBuilder.cs`.
3. **BrowserLocator:** `orders/…/Services/BrowserLocator.cs` (266 dòng) vs `suite/Shopee.Core/Platform/Windows/WindowsBrowserLocator.cs` + `Linux/LinuxBrowserLocator.cs` — bản suite có registry fallback (đầy đủ hơn).
4. **Bộ selector Microsoft (MS-mail-login):** `orders/…/Services/ShopeeLoginService.cs` `LoginHotmailAsync:~1225` + `Ms*Selectors:~550-571` vs `suite/Shopee.Core/BigSeller/HotmailOtpReader.cs:27-45` (tự khai "PORT từ ShopeeLoginService"). Driver khác nhau (Playwright vs CDP) — thứ TRÙNG là các bộ selector.

Mẫu làm đúng có sẵn: `shared/Shopee.Proxy.Kiot` (cả 2 phía ref).

## 2. Phạm vi

- **Làm:** tạo project mới `shared/Shopee.Toolkit` (net8.0, KHÔNG dep UI/Avalonia/Playwright) chứa 4 hạ tầng trên; orders + suite chuyển sang dùng; xoá bản trùng.
- **Không làm:** không đổi hành vi (trừ Search WebSocketServer NHẬN fail-fast của orders — thay đổi chủ đích, ghi rõ); không đụng `extensions/**`, `server/**`; không gộp driver MS-login (chỉ selector + logic thuần chuỗi).

## 3. Các bước thực hiện

1. Tạo `shared/Shopee.Toolkit/Shopee.Toolkit.csproj`; add vào `ShopeeSuite.sln` (orders và suite project đều nằm sln này — kiểm tra cả `server/ShopeeHub.sln` không cần).
2. **WebSocketServer** → `shared/Shopee.Toolkit/Ws/WebSocketServer.cs`: lấy bản orders làm gốc (có fail-fast + SendOptions); Search chuyển sang dùng; xoá 2 bản cũ. Đối chiếu diff 2 bản trước khi xoá — tính năng nào chỉ bản Search có thì giữ qua option.
3. **BraveArgs** → `shared/Shopee.Toolkit/Browser/BraveArgs.cs`: hợp nhất BuildArgs orders + BraveArgsBuilder suite (tham số hoá khác biệt; --load-extension + DisableLoadExtensionCommandLineSwitch giữ nguyên); 2 phía chuyển sang dùng; xoá bản cũ. So từng flag — thiếu/thừa flag Brave là đổi hành vi anti-bot.
4. **BrowserLocator** → `shared/Shopee.Toolkit/Browser/BrowserLocator.cs`: lấy bản suite (Windows registry fallback + Linux) làm gốc; orders chuyển sang; xoá bản orders. Suite Core giữ wrapper mỏng nếu Platform/* đang là contract nơi khác dùng.
5. **MS selectors** → `shared/Shopee.Toolkit/MsLogin/MsLoginSelectors.cs`: gom các bộ selector (user/pass/sign-in/OTP/stay-signed-in…) thành hằng dùng chung; `HotmailOtpReader` (suite) + `ShopeeLoginService.LoginHotmailAsync` (orders) cùng đọc từ đây; so 2 bộ hiện tại — selector nào chỉ 1 bên có thì đưa vào chung (2 bên đều hưởng), ghi bảng đối chiếu.
6. Build + test cả 2 solution; chạy test orders đầy đủ.

## 4. Tiêu chí nghiệm thu

- [ ] Build 2 solution 0 lỗi 0 warning; test không tụt baseline.
- [ ] Grep: không còn class WebSocketServer/BraveLaunchArgs/BrowserLocator định nghĩa ngoài `shared/Shopee.Toolkit` (trừ wrapper mỏng khai báo rõ); selector Microsoft literal chỉ còn trong MsLoginSelectors.
- [ ] `shared/Shopee.Toolkit.csproj` không ref Avalonia/Playwright/WPF.
- [ ] Báo cáo: bảng diff 2 bản của từng hạ tầng + quyết định giữ gì.

## 5. Rủi ro & lưu ý

- Chạy SAU B1 (chung file orders Services). Số dòng plan là ước lượng — tìm theo symbol.
- Search WS nhận fail-fast: kiểm tra caller Search xử lý exception mới (grep chỗ gọi SendAsync phía Search, bọc catch nếu caller đang dựa vào nuốt-im).
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Kết quả:** 5/5 bước xong (lượt 1 làm 4 bước + bước 5 phía orders; lượt 2 — sau khi P4-hub merge, khu
`server/**` rảnh — làm nốt bước 5 phía suite). Xem "Điểm chặn (ĐÃ XỬ LÝ)".

### Project mới `shared/Shopee.Toolkit` (net8.0, `WarningsAsErrors=CA1416`)
Ref DUY NHẤT: `Microsoft.Win32.Registry` 5.0.0 (registry App Paths cho BrowserLocator — Shopee.Core vốn đã có
package này). KHÔNG Avalonia / Playwright / WPF / ClosedXML. Đã thêm vào `ShopeeSuite.sln` (folder `shared`).
`Shopee.Core` + `XuLyDonShopee.Core` cùng ProjectReference tới nó (orders VẪN không ref Shopee.Core).

| File mới | Nội dung |
|---|---|
| `shared/Shopee.Toolkit/Ws/WebSocketServer.cs` | máy chủ WS loopback dùng chung |
| `shared/Shopee.Toolkit/Browser/BraveArgs.cs` | builder args Brave (2 chế độ: chuỗi-quoted / danh-sách-raw) |
| `shared/Shopee.Toolkit/Browser/BrowserLocator.cs` | dò exe + User Data của Chrome/Edge/Brave, đa HĐH |
| `shared/Shopee.Toolkit/MsLogin/MsLoginSelectors.cs` | bộ selector + needle + regex form đăng nhập Microsoft |

### 1. WebSocketServer — bảng diff & quyết định

| Điểm | orders `OrdersWebSocketServer` | Search `WebSocketServer` | Bản chung |
|---|---|---|---|
| `SendAsync` khi socket chưa mở | ném `InvalidOperationException` (fail-fast) | `return` im lặng | **giữ fail-fast** |
| `JsonSerializerOptions` | static dùng chung | tạo mới mỗi lần gửi | **giữ static** |
| cổng mặc định ctor | không có | `9111` | **giữ `= 9111`** (thứ duy nhất của Search) |
| AcceptLoop / HandleConnection / Dispose | giống hệt nhau (chỉ khác ngôn ngữ comment) | — | giữ bản orders (comment tiếng Việt) |
| message của exception | "Cầu nối Đơn hàng: extension chưa kết nối…" | — | đổi thành "Cầu nối extension chưa kết nối…" (bỏ tên module vì dùng 2 nơi) |

Đã xoá `orders/…/OrdersWebSocketServer.cs` + `suite/Shopee.Module.Search/Engine/WebSocketServer.cs`.
Search nhận qua `global using Shopee.Toolkit.Ws;` trong `GlobalUsings.cs`.

**Rà caller Search với fail-fast** (3 chỗ gọi `_ws.SendAsync`) — không chỗ nào nuốt-im:
- `CdpInputController.cs:132` — đã bọc `try { … } catch { }` sẵn → không đổi gì.
- `SearchOrchestrator.SendStartCommandAsync` — gọi từ `SendPendingSearchOnReady`, đã bọc try/catch →
  `ErrorOccurred` → `SearchRunOutcome.Error`. Trước đây gửi hụt là im lặng rồi treo tới timeout; giờ báo lỗi ngay.
  Chỉ chạy sau khi extension đã báo `ready` nên socket gần như chắc chắn đang mở.
- `SearchOrchestrator.StopAsync` — **không caller nào gọi** (code chết) → vô hại.

### 2. Brave args — bảng diff & quyết định

| Điểm | orders `BraveLaunchArgs` | suite `BraveArgsBuilder` | Bản chung `BraveArgs` |
|---|---|---|---|
| dạng kết quả | `IReadOnlyList<string>` | `string` nối dấu cách | cả hai: `BuildList()` / `Build()` |
| bọc ngoặc kép đường dẫn/URL | KHÔNG (đi vào `ArgumentList`/Playwright) | CÓ (đi vào `Process.Start(exe, args)`) | tham số hoá: `CreateRaw()/WindowRaw()` vs `Create()/Window()` |
| khối 6 cờ cửa sổ | có (sau `--remote-debugging-port`) | `Window()` | `WindowBlock()` — dùng chung, thứ tự y nguyên |
| `--remote-debugging-port` / `--load-extension` / start URL | có | có | dùng chung |
| `--proxy-server` | KHÔNG (orders bỏ hẳn proxy) | có (`ProxyServer`) | có method, orders không gọi |
| cờ giới hạn cache đĩa | không dùng | `DiskCacheLimit()` ← `BraveCachePolicy.DiskLimitArgs` | hằng chuyển sang `BraveArgs.DiskCacheLimitFlags`; `BraveCachePolicy.DiskLimitArgs` giờ TRỎ về đó (tránh vòng ref Core↔Toolkit) |
| 3 cờ chống-treo-nền, `--lang=vi-VN`, `--disable-popup-blocking`, `--disable-features=Translate,…` | chỉ orders | — | **để nguyên trong orders** (không phải cờ trùng) |
| `DisableLoadExtensionCommandLineSwitch` | nối vào `--disable-features` khi có ext | Search/scrape tự `.Add(...)` chuỗi riêng | giữ nguyên từng bên (chuỗi `--disable-features` mỗi nơi một khác) |

`orders/…/BraveLaunchArgs.cs` giữ lại làm **wrapper mỏng** (khai báo rõ trong xmldoc): chỉ còn chính sách riêng
của module Đơn hàng, mọi cờ do `BraveArgs` dựng. `suite/Shopee.Core/Browser/BraveArgsBuilder.cs` đã XOÁ; 4 call
site đổi tên type: `BrowserLauncher`, Search `BraveManager`, MultiBrave `BraveProfileManager`, UpdateProduct
`BigSellerBraveRunner`.

**Kiểm chứng không lệch cờ:** đã chạy snapshot tạm so ĐÚNG TỪNG PHẦN TỬ + ĐÚNG THỨ TỰ cho
`BuildBraveArgs` (có/không extension), `BuildCleanPocArgs`, và chuỗi quoted của khuôn `BrowserLauncher` →
khớp 100% bản trước refactor (4/4 pass). File tạm đã xoá sau khi verify.

### 3. BrowserLocator — bảng diff & quyết định (HỢP của 2 bản)

| Trình duyệt / HĐH | bản suite | bản orders | Bản chung |
|---|---|---|---|
| Brave Win | LocalAppData → PF → PFx86 → registry HKCU → HKLM | PF → PFx86 → LocalAppData | **thứ tự suite** + registry. *Orders đổi: LocalAppData từ hạng 3 lên hạng 1* |
| Brave Linux | /usr/bin ×3 → snap → flatpak ×2 → dò PATH | /usr/bin ×3 → **/opt/brave.com/…** → snap | hợp: /usr/bin ×3 → /opt/brave.com → snap → flatpak ×2 → PATH |
| Brave macOS | không có | có | giữ (suite không chạy macOS) |
| Edge Win | hard-code `C:\Program Files (x86)…` → `C:\Program Files…` | PFx86 → PF → **LocalAppData** (theo biến môi trường) | PFx86 → PF → LocalAppData qua `GetFolderPath`. *Suite đổi: hết hard-code ổ C, thêm LocalAppData* |
| Edge Linux | /usr/bin ×2 → /opt/…/**microsoft-edge** → PATH | /usr/bin ×2 → /opt/…/**msedge** | hợp cả 2 tên file + PATH |
| Chrome | **không hỗ trợ** | Win PF→PFx86→LocalAppData; Linux + chromium; macOS | giữ nguyên bản orders, thêm dò PATH |
| `DetectUserData` | Brave/Edge, Win + Linux + flatpak | không có | giữ nguyên bản suite (`FindBraveUserData`/`FindEdgeUserData`) |
| dò PATH (Linux) | Brave/Edge | không có | áp cho cả 3 (chỉ là fallback CUỐI → không đổi kết quả khi đã khớp đường dẫn cố định) |

Xoá `WindowsBrowserLocator.cs` + `LinuxBrowserLocator.cs`; thay bằng **1 wrapper mỏng**
`suite/Shopee.Core/Platform/ToolkitBrowserLocator.cs` (hiện thực `IBrowserLocator`, chỉ ánh xạ `BrowserKind`)
— contract `IBrowserLocator`/`PlatformServices.BrowserLocator` giữ nguyên cho mọi caller.
`orders/…/BrowserLocator.cs` giữ lại làm wrapper mỏng cho luật `BrowserChoice` (khái niệm chỉ có ở orders).
`FindFirstExisting` chuyển hẳn sang Toolkit (public) → 5 test trong `BrowserLocatorTests` đổi sang alias
`ToolkitLocator`; 15 test còn lại (ResolveExecutableCore/ClassifyExe) không đổi.

### 4. MS selectors — bảng diff (CẢ HAI PHÍA đã xong)

| Bộ | orders `ShopeeLoginService` | suite `HotmailOtpReader` | Bản chung |
|---|---|---|---|
| User / Password / Submit / UsePassword / OtherWays / KmsiYes / SignIn | có | **giống hệt** | gom vào `MsLoginSelectors` |
| `SignInRegex` | có | **giống hệt** | gom (orders dùng lại cho cả nút "Đăng nhập" form subaccount Shopee) |
| needle "mat khau/password/contrasena" + "cach khac de dang nhap/…" | inline tại call site | inline tại call site | `UsePasswordNeedles` / `OtherWaysNeedles` |
| KMSI Fluent (`primaryButton`) + marker `kmsiVideo/kmsiImage` | inline | inline | `KmsiYesFluent` / `KmsiFormMarkers` |
| `#usernameError` / `#passwordError` | literal | literal | `UsernameError` / `PasswordError` |
| `MsPasswordOptionSelectors` | CHỈ orders — và **đang là code chết** (khai báo, không caller) | không có | đưa vào chung `PasswordOption`, ghi rõ hiện chưa ai dùng |
| `MsDomainFamilies` (gate hotmail/outlook/live…) | không có | chỉ suite | **để nguyên ở HotmailOtpReader** — là luật gate domain, không phải selector; đưa sang chung cũng không có bên thứ hai hưởng |
| `NormalizeForMatch` | có (`LoginSession`) | có (bản chép) | **chưa gom** — cùng lý do chặn ở dưới |

### Điểm chặn (ĐÃ XỬ LÝ ở lượt 2)

Vấn đề lượt 1: `server/Shopee.Hub.Web.csproj` **LINK** `HotmailOtpReader.cs` mà project hub không có
ProjectReference nào → thêm `using Shopee.Toolkit.MsLogin;` vào file là gãy `dotnet build server/ShopeeHub.sln`
(CS0246); khu `server/**` khi đó đang có agent P4-hub làm song song nên không được đụng.

Lượt 2 (sau `git merge --ff-only main`, P4-hub đã land) đã làm nốt, ĐÚNG 2 việc đã mô tả:

1. `server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` — thêm 1 dòng Compile-link ngay TRƯỚC dòng link
   `HotmailOtpReader.cs` (đúng khuôn các link sẵn có, kèm comment lý do):
   `<Compile Include="..\..\shared\Shopee.Toolkit\MsLogin\MsLoginSelectors.cs" Link="Shared\MsLogin\MsLoginSelectors.cs" />`
   → hub compile file selector vào assembly của nó (namespace `Shopee.Toolkit.MsLogin`), KHÔNG cần
   ProjectReference tới Shopee.Toolkit nên không kéo thêm dây nào vào hub.
2. `suite/Shopee.Core/BigSeller/HotmailOtpReader.cs` — thay hết bằng thành viên `MsLoginSelectors`:
   7 mảng `Ms*Selectors`, `SignInRegex`, 2 mảng needle inline (`UsePasswordNeedles`/`OtherWaysNeedles`),
   bộ KMSI Fluent + marker (`KmsiYesFluent`/`KmsiFormMarkers`), `#usernameError`/`#passwordError`.
   Đã đối chiếu lại sau merge: 7 mảng + `SignInRegex` GIỐNG HỆT bản chung → thay thẳng, KHÔNG lệch hành vi.
   Sửa thêm xmldoc đầu lớp (bỏ câu "PORT selector từ ShopeeLoginService" — giờ đọc thẳng từ bộ chung).
   `MsDomainFamilies` + `SixDigitRegex` giữ nguyên tại chỗ (luật riêng BigSeller, không phải selector MS).

**Tiêu chí "selector Microsoft literal chỉ còn trong MsLoginSelectors": ĐẠT.** Grep toàn repo
(`idSIButton9|loginfmt|i0116|i0118|acceptButton|idA_PWD_SwitchToPassword|kmsiVideo|kmsiImage|usernameError|
passwordError|data-task='signin'|mat khau|cach khac de dang nhap`) chỉ còn hit trong
`shared/Shopee.Toolkit/MsLogin/MsLoginSelectors.cs`; các hit khác đều là COMMENT/xmldoc giải thích.

KHÔNG làm `NormalizeForMatch` (theo chỉ đạo — orders `LoginSession` đang được agent khác tách, để đợt 5).

### Ngoài phạm vi, cố ý KHÔNG làm

- 3 cờ chống-treo-nền (`--disable-background-timer-throttling` / `-backgrounding-occluded-windows` /
  `-renderer-backgrounding`) lặp ở 4 nơi (orders + Search BraveManager + MultiBrave BraveProfileManager +
  UpdateProduct BigSellerBraveRunner) nhưng **THỨ TỰ khác nhau** giữa các nơi. Gom thành 1 helper sẽ đổi thứ tự
  chuỗi args ở 2/4 nơi — phá bất biến "chuỗi kết quả GIỐNG HỆT bản cũ" mà refactor BraveArgsBuilder đã cam kết.
  Để nguyên, đề xuất tách thành việc riêng.
- `NormalizeForMatch` (trùng byte giữa `LoginSession` và `HotmailOtpReader`): chỉ gom được khi mở khoá được
  điểm chặn ở trên, nếu không thì chuyển sang Toolkit mà chỉ 1 bên dùng = churn vô ích.

### Build / test

| Lệnh | Kết quả |
|---|---|
Lượt 2 (sau khi merge main — baseline Core.Tests đã lên 43 do P4-hub thêm test):

| Lệnh | Kết quả |
|---|---|
| `dotnet build ShopeeSuite.sln` | 0 lỗi, 0 warning |
| `dotnet build server/ShopeeHub.sln` | 0 lỗi, 0 warning |
| `dotnet test suite/Shopee.Core.Tests` | 43/43 pass (đúng baseline) |
| `dotnet test server/Shopee.Hub.Web.Tests` | 44/44 pass (đúng baseline) |
| `dotnet test orders/XuLyDonShopee.Tests` | 1440/1440 pass (chạy thêm cho chắc, không đụng orders lượt này) |

CHƯA commit (để phiên chính review).
