# Plan: Tự động login Shopee + verify qua email Hotmail + xử lý captcha (module Đơn hàng)

- **Ngày:** 2026-07-21
- **Trạng thái:** hoàn thành (code + test; luồng verify/captcha thật chưa chạy tay — chờ chạy thử với tài khoản thật)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Màn **Tài khoản** (module Đơn hàng, tab Shopee trong suite): khi bấm **Sync** (hoặc Sync đã chọn / Chạy tự động), phiên mở trang bán hàng `https://banhang.shopee.vn/`. Hiện nay nếu Shopee đá về form login thì đã có auto-login điền user/pass (`TryHumanLoginAsync`), nhưng **không xử lý** trang verify (xác minh) và captcha — phiên đứng im chờ người dùng làm tay.

Yêu cầu người dùng:
1. Thêm 2 thông tin vào tài khoản: **Email (xác minh)** và **Mật khẩu email** — là hộp thư Hotmail/Outlook nhận mail xác minh của Shopee.
2. Khi mở trang bán hàng mà bị chuyển sang login → tự login với user/mật khẩu tài khoản (đã có). Sau đó nếu ra trang **verify** → tự click lựa chọn **verify by email**.
3. Mở **tab mới** vào Hotmail: nhập username (= email xác minh mới thêm) → bấm tiếp → chọn **"Use your password" / "Sử dụng mật khẩu"** → nhập mật khẩu email → vào hộp thư, ưu tiên xem mục **"Khác"/"Other"** → tìm mail Shopee **mới nhất** → mở mail → click nút/link **xác nhận** trong mail. Xong đóng tab, quay lại tab trang bán hàng; verify xong thì sync tiếp bình thường.
4. Nếu sau khi vào trang chính bị chuyển sang **captcha** → đóng phiên sync, **xóa profile**, tạo profile mới và **chạy lại luồng login** từ đầu.

### Hiện trạng code (đã khảo sát — mỏ neo file:dòng)

Engine: Playwright nối qua CDP vào Brave/Chrome/Edge thật; mỗi tài khoản 1 phiên độc lập.

- `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`
  - `SellerUrl = "https://banhang.shopee.vn/"` (~217); `OpenAsync` phóng browser + `ConnectOverCDPAsync` (~306–374).
  - Inner class `LoginSession : ILoginSession` (~586) giữ `IPlaywright/IBrowser/IBrowserContext/Process`; page chính = `_context.Pages[0]`.
  - **Auto-login sẵn có**: `TryHumanLoginAsync` (~657) — selector `UserSelectors` (`input[name='loginKey']`…, ~636), `PasswordSelectors` (~644), `SubmitSelectors` (~650); gõ human (`HumanTyping`/`HumanMouse`); ghi rõ "KHÔNG xử lý captcha/OTP".
  - Nhận biết đã-login DUY NHẤT qua cookie: `ShopeeLoginCookies.IsLoggedIn` (SPC_EC/SPC_ST/SPC_U) — `orders/XuLyDonShopee.Core/Services/ShopeeLoginCookies.cs:11-19`.
  - Đa-tab: `_context.NewPageAsync()` (~364, ~3017), `page.CloseAsync()` (~3055), pattern bắt tab mới mở bởi trang (snapshot `before` rồi poll page mới, ~2446–2496).
- `orders/XuLyDonShopee.App/Services/AccountSession.cs`
  - `RunAsync` (~1198): đọc account (~1203) → tính profile path (~1223) → nếu bật cờ "Xóa profile và tạo lại" thì `ProfileJanitor.TryResetDirectory` (~1230) → chọn proxy (~1249) → vòng relaunch: `OpenAsync` (~1306) → `TryHumanLoginAsync` (~1327) → `_readyForActions = true` (~1333) → vòng poll cookie + đọc "Chờ Lấy Hàng".
  - `SyncFullAsync` (~742): Kiểm tra → Xử lý đơn → Sync.
- `orders/XuLyDonShopee.Core/Services/ProfileJanitor.cs` — `TryResetDirectory(dir, log, attempts=3)` (~27): xóa recursive + tạo lại, retry khi khóa, sanity đường dẫn phải chứa segment `profiles`.
- Profile: `BrowserProfilePaths.ForAccount(baseDir, accountId, kind)` → `<baseDir>/profiles/<id>-<kind>`.
- **Reference phát hiện verify/captcha** (suite, chỉ để CHÉP LOGIC, không reference project): `suite/Shopee.Module.CheckAccount/ShopeeAccountChecker.cs` — outcome qua URL chứa `/verify` hoặc `captcha`, alert chứa `otp`/`mã xác`/`xác minh` (~283–310).
- Model/DB: `orders/XuLyDonShopee.Core/Models/Account.cs` (Id, Email, Password, Phone, Cookie, Note, ProxyKey, PickupAddress, Status, CreatedAt, UpdatedAt); schema `orders/XuLyDonShopee.Core/Data/Database.cs` CREATE TABLE accounts (~66–78) + pattern migration **`EnsureColumn(conn, table, column, type)`** (~163, ví dụ ~131–132); repo `orders/XuLyDonShopee.Core/Data/AccountRepository.cs` (`GetAll:20`, `GetById:34`, `Insert:42`, `Update:63`, `BindWritableFields:89`, `Map:101`); test migration mẫu `orders/XuLyDonShopee.Tests/DatabaseMigrationTests.cs`.
- Form: `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs` — `Edit*` (~139–162), `Save` (~485, nhánh new ~517 / existing ~541), `LoadIntoForm` (~1108), `ClearForm` (~1129); view `orders/XuLyDonShopee.App/Views/AccountsView.axaml` card "THÔNG TIN ĐĂNG NHẬP" (~286–328).
- **Automation email: toàn repo CHƯA CÓ** — phần Hotmail viết mới hoàn toàn.

### Quyết định đã chốt

- Tên cột/field mới: `VerifyEmail` (TEXT), `VerifyEmailPassword` (TEXT) — nghĩa: hộp thư nhận mail xác minh. Nhãn UI: "Email xác minh" / "Mật khẩu email".
- Luồng verify-email và captcha-retry cài ở **Core (`LoginSession`)** + điều phối trong **`AccountSession.RunAsync`** (vòng relaunch sẵn có) → tự áp dụng cho MỌI đường mở phiên (Sync, Sync đã chọn, Chạy tự động) — không sửa từng nút.
- Captcha retry: tối đa **2 lần** xóa profile + chạy lại (tổng 3 lượt thử). Hết lượt → ghi log + StatusText rõ ràng, giữ cửa sổ cho người dùng xử lý tay (hành vi hôm nay).
- Không cấu hình email xác minh (field trống) mà gặp trang verify → ghi log hướng dẫn + để người dùng verify tay (như hôm nay), KHÔNG lỗi.
- Nhận diện lựa chọn "verify by email" và nút xác nhận trong mail: **không bám text tiếng Anh cứng** — dò cả vi/en (regex `email` / `xác nhận|verify|confirm`), ưu tiên cấu trúc DOM trước text (bài học guide ngôn ngữ BigSeller).
- Mail Shopee tìm ở tab **"Khác"/"Other" trước**, không thấy thì sang "Ưu tiên"/"Focused"; chỉ lấy mail **mới nhất**.
- Màn hình "Stay signed in?" của Microsoft → bấm **Yes** (giữ đăng nhập trong profile, lần sau đỡ login lại).
- Mỗi bước automation ghi `ActivityLog` (qua log callback sẵn có của phiên) để người dùng theo dõi được trên panel Nhật ký.

## 2. Phạm vi

- **Làm:**
  - 2 cột DB + model + repo + migration + form UI (card mới trong màn Tài khoản).
  - Phát hiện trạng thái trang sau khi mở seller URL: LoginForm / Verify / Captcha / LoggedIn.
  - Tự click "verify by email" + automation Hotmail end-to-end trong tab mới cùng profile.
  - Captcha → đóng browser, xóa profile, mở lại, login lại (tối đa 2 lần reset).
  - Test: migration 2 cột + roundtrip repo; build 3 project + toàn bộ test hiện có xanh.
- **Không làm:**
  - Không đụng luồng BigSeller/Hub/suite modules khác; không reference project CheckAccount (chỉ chép logic).
  - Không xử lý OTP điện thoại, không giải captcha tự động (captcha chỉ né bằng profile mới).
  - Không hỗ trợ nhà mail khác ngoài Hotmail/Outlook web (Gmail… để sau).
  - Không sync 2 field mới lên Hub.

## 3. Các bước thực hiện

### Bước 1 — DB + model + form (2 field mới)

1. `orders/XuLyDonShopee.Core/Models/Account.cs`: thêm `public string VerifyEmail { get; set; } = "";` và `public string VerifyEmailPassword { get; set; } = "";` (kèm doc-comment: hộp thư Hotmail nhận mail xác minh Shopee).
2. `orders/XuLyDonShopee.Core/Data/Database.cs`: thêm 2 cột vào CREATE TABLE accounts + `EnsureColumn(conn, "accounts", "VerifyEmail", "TEXT")` và `VerifyEmailPassword` (cạnh các EnsureColumn accounts sẵn có ~131–132).
3. `orders/XuLyDonShopee.Core/Data/AccountRepository.cs`: cập nhật đủ 5 chỗ — SELECT của `GetAll` + `GetById`, INSERT, UPDATE, `BindWritableFields`, `Map` (giá trị null → "").
4. `AccountsViewModel.cs`: thêm `[ObservableProperty] _editVerifyEmail`, `_editVerifyEmailPassword`, `_showVerifyEmailPassword` + `ToggleShowVerifyEmailPasswordCommand`; gán ở `LoadIntoForm`, reset ở `ClearForm`, đọc ở `Save` cả 2 nhánh (new + existing). 2 field KHÔNG bắt buộc (cho phép trống — không validate).
5. `AccountsView.axaml`: thêm card mới **"EMAIL XÁC MINH"** ngay dưới card "COOKIE ĐĂNG NHẬP", layout giống card "THÔNG TIN ĐĂNG NHẬP" (2 cột: Email xác minh | Mật khẩu email, password có nút 👁 riêng bind `ShowVerifyEmailPassword`), kèm 1 dòng chú thích nhỏ TextMuted: "Hộp thư Hotmail/Outlook nhận mail xác minh khi Shopee yêu cầu verify đăng nhập".
6. Test: `DatabaseMigrationTests` thêm case DB cũ (không có 2 cột) mở lên có đủ 2 cột; `AccountRepositoryTests` roundtrip Insert/Update/GetById giữ 2 field.

### Bước 2 — Core: phát hiện trạng thái trang (`ShopeeLoginService.cs`)

1. Thêm `public enum ShopeePageState { LoggedIn, LoginForm, Verify, Captcha, Unknown }` (file mới `orders/XuLyDonShopee.Core/Services/ShopeePageState.cs` hoặc trong ShopeeLoginService.cs).
2. Trong `LoginSession` thêm `Task<ShopeePageState> DetectPageStateAsync()`:
   - Cookie `ShopeeLoginCookies.IsLoggedIn` → ưu tiên nhưng CHƯA đủ (cookie có thể còn mà vẫn bị bắt verify) — kiểm URL trước:
   - URL page chính chứa `captcha` (hoặc `verify/captcha`) → `Captcha`.
   - URL chứa `/verify` → `Verify`.
   - Có ô login (`UserSelectors` đầu tiên visible qua `getClientRects` — không dùng offsetParent) → `LoginForm`.
   - Cookie logged-in + không rơi các nhánh trên → `LoggedIn`; còn lại `Unknown`.
   - Chép logic từ `ShopeeAccountChecker.WaitOutcomeAsync` (~283–310) có điều chỉnh cho seller site.
3. Expose trên `ILoginSession` (~12): `DetectPageStateAsync`, và method mới bước 3. Chỉnh chữ ký `TryHumanLoginAsync` nếu cần (giữ tương thích chỗ gọi cũ).

### Bước 3 — Core: verify qua email Hotmail (`LoginSession`, method mới `TryVerifyByEmailAsync`)

`Task<bool> TryVerifyByEmailAsync(string verifyEmail, string verifyEmailPassword, Action<string>? log)` — chạy khi `DetectPageStateAsync() == Verify`:

1. **Trang verify Shopee**: dò các lựa chọn phương thức xác minh, click phần tử có text khớp regex `email` (case-insensitive, vi/en; ưu tiên phần tử dạng button/row trong danh sách lựa chọn, kiểm visible bằng `getClientRects`). Chờ 2–5s xem trang đổi (thường sang màn "đã gửi link xác minh, kiểm tra email"). Không thấy lựa chọn email → return false (log rõ).
2. **Mở tab Hotmail**: `_context.NewPageAsync()` → goto `https://outlook.live.com/mail/0/`. Đường login Microsoft (login.live.com / login.microsoftonline.com):
   - Ô username: `input[type=email]` / `input[name=loginfmt]` → gõ human `verifyEmail` → submit (`input[type=submit]` / `button[type=submit]` / `#idSIButton9`).
   - Màn kế: nếu hiện lựa chọn passwordless → tìm và click **"Use your password" / "Sử dụng mật khẩu"** (dò link/button text regex `password|mật khẩu`, id lịch sử `#idA_PWD_SwitchToPassword`); nếu ô `input[name=passwd]`/`input[type=password]` đã hiện thì bỏ qua bước này.
   - Gõ mật khẩu → submit.
   - Màn "Stay signed in?"/KMSI (id `#acceptButton`/`#idSIButton9`, checkbox KMSI) → bấm Yes.
   - Nếu đã đăng nhập từ trước (profile giữ phiên) → các bước trên tự skip (mỗi bước đều "chờ có selector thì làm, timeout ngắn thì bỏ qua sang bước sau" — KHÔNG fail cứng).
   - Login lỗi (sai pass — dò alert/error box) → log + đóng tab + return false.
3. **Tìm mail Shopee**: chờ list mail load (`[role=listbox]`/`.hcptT` — dò structural: vùng danh sách message). Nếu có tab "Khác"/"Other" (pivot) → click tab đó TRƯỚC; tìm dòng mail đầu tiên (mới nhất) có text `Shopee` (sender/subject). Không thấy → thử tab "Ưu tiên"/"Focused". Vẫn không thấy → reload 1 lần chờ 10s (mail có thể tới chậm) rồi thử lại, tối đa 3 vòng. Hết → log + đóng tab + return false.
4. **Đọc mail + click xác nhận**: click dòng mail → chờ reading pane → trong body tìm link/button text regex `xác nhận|verify|confirm` (lấy phần tử `a` visible đầu tiên khớp) → click. Link thường mở TAB MỚI (target _blank) → dùng pattern bắt-tab-mới sẵn có (~2446–2496) chờ tab xác nhận load xong (networkidle hoặc 10s) → đóng tab xác nhận. Nếu link mở cùng tab thì chờ load rồi thôi.
5. **Dọn + quay lại**: đóng tab Hotmail, quay lại page chính (seller). Reload trang seller; poll `DetectPageStateAsync` tối đa 90s chờ `LoggedIn` (hết verify). Đạt → return true; timeout → return false.
6. Toàn bộ method bọc try/catch, mọi bước log qua `log` callback; timeout tổng ~4 phút; LUÔN đóng các tab đã mở dù lỗi (finally).

### Bước 4 — AccountSession: điều phối login → verify → captcha-retry (`RunAsync`)

Sửa đoạn sau `OpenAsync` (~1306) trong vòng relaunch:

1. Thay khối `TryHumanLoginAsync` đơn lẻ (~1327) bằng chuỗi:
   - `state = DetectPageStateAsync()`.
   - `LoginForm` → `TryHumanLoginAsync(user, pass)` (sẵn có) → chờ 5–10s → detect lại.
   - `Verify` → nếu account có `VerifyEmail` + `VerifyEmailPassword` → `TryVerifyByEmailAsync(...)`; thành công → detect lại (kỳ vọng LoggedIn); thất bại/thiếu cấu hình → log hướng dẫn, GIỮ phiên như hôm nay (người dùng verify tay), `_readyForActions = true` và đi tiếp vòng poll như cũ.
   - `Captcha` → xử lý mục 2 dưới đây.
   - `LoggedIn`/`Unknown` → như hôm nay (`_readyForActions = true`, vào vòng poll).
2. **Captcha-retry** (đếm `captchaResets`, tối đa 2):
   - Log "Gặp captcha — đóng phiên, xóa profile, thử lại (lần {n}/2)".
   - Đóng browser của phiên (đường dispose/close sẵn có của vòng relaunch — đảm bảo process Brave chết trước khi xóa profile).
   - `ProfileJanitor.TryResetDirectory(userDataDir, log)`.
   - `continue` vòng relaunch (OpenAsync lại → chuỗi detect/login lại chạy từ đầu).
   - Quá 2 lần → log "Captcha lặp lại — cần xử lý tay", `_readyForActions = true`, giữ cửa sổ (không đóng phiên).
3. KHÔNG đổi hành vi các bước sau (`SyncFullAsync`… giữ nguyên); không đổi `AutoRunService` (nó dùng chung `Start`/`RunAsync` nên tự hưởng).
4. Lưu ý thread/log: dùng đường log sẵn có của `AccountSession` (StatusText/ActivityLog theo Email tài khoản).

### Bước 5 — Build + test

- `dotnet build orders/XuLyDonShopee.App` ; `dotnet build orders/XuLyDonShopee.Core` (theo solution phụ thuộc)
- `dotnet test orders/XuLyDonShopee.Tests` (toàn bộ, gồm test mới bước 1)
- `dotnet build suite/Shopee.Suite`

## 4. Tiêu chí nghiệm thu

- [ ] Build 3 project xanh; toàn bộ test (cũ + mới) pass.
- [ ] Migration: mở DB cũ không có 2 cột → tự thêm, không mất dữ liệu (test tự động).
- [ ] Form Tài khoản có card "EMAIL XÁC MINH" với 2 ô; Lưu/nạp lại giữ giá trị; để trống vẫn lưu bình thường.
- [ ] Code-path: mở phiên → LoginForm → auto-login → Verify → click verify-by-email → tab Hotmail login (username → use password → pass → KMSI Yes) → tab "Khác" → mail Shopee mới nhất → click xác nhận → đóng tab → quay lại seller → LoggedIn → sync chạy tiếp. (Kiểm bằng chạy tay với 1 tài khoản thật — ghi rõ trong báo cáo là chưa/đã chạy tay được.)
- [ ] Captcha giả lập (không chạy tay được thì review logic): gặp Captcha → browser đóng, thư mục profile bị xóa tạo lại, phiên mở lại; quá 2 lần → dừng thử, log rõ, phiên giữ nguyên cho xử lý tay.
- [ ] Thiếu email xác minh mà gặp Verify → log hướng dẫn, phiên vẫn ready như hành vi cũ (không crash, không đóng).
- [ ] Không đụng hành vi khi mọi thứ bình thường (đã login sẵn → vào thẳng vòng poll như hôm nay).

## 5. Rủi ro & lưu ý

- **Selector Microsoft/Outlook đổi thường xuyên** → mọi bước Hotmail phải "dò nhiều selector + timeout ngắn + bỏ qua được", log từng bước; đừng fail cứng cả chuỗi vì 1 selector.
- **Không bám text EN cứng** — UI có thể vi/en (bài học BigSeller guide: dùng structural + regex đa ngôn ngữ, visible check bằng `getClientRects`).
- **Xóa profile phải chắc Brave đã chết** (file lock) — dùng đường đóng browser sẵn có của relaunch loop trước khi `TryResetDirectory`; janitor đã retry 3 lần.
- **Không giữ tham chiếu page qua các bước dài** — page có thể navigate; lấy lại `_context.Pages` khi cần.
- Câu chữ selector verify-page của Shopee seller có thể khác buyer — viết dò linh hoạt (danh sách lựa chọn chứa từ `email`), và log DOM đoạn quyết định (title/url) khi không khớp để lần sau tinh chỉnh nhanh.
- `_readyForActions` phải ĐƯỢC set ở mọi nhánh thoát (kể cả degrade) — nếu không, `WaitForSessionReadyAsync` treo 5 phút.
- 2 field mới chứa mật khẩu email dạng plain trong SQLite — chấp nhận (đồng mức với `Password` hiện tại), KHÔNG log giá trị mật khẩu ra ActivityLog.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Trạng thái:** đã code xong 5 bước + build 3 project xanh + 774 test pass. Chưa chạy tay với tài khoản thật (cần môi trường có Shopee/Hotmail thật — xem mục "kiểm bằng chạy tay").

### File đã sửa/tạo

**Bước 1 — DB + model + repo + form + test**
- `orders/XuLyDonShopee.Core/Models/Account.cs` — thêm 2 property `VerifyEmail` / `VerifyEmailPassword` (kiểu `string`, mặc định `""`, có doc-comment).
- `orders/XuLyDonShopee.Core/Data/Database.cs` — thêm 2 cột vào CREATE TABLE accounts + 2 `EnsureColumn(conn,"accounts","VerifyEmail"/"VerifyEmailPassword","TEXT")`.
- `orders/XuLyDonShopee.Core/Data/AccountRepository.cs` — cập nhật 5 chỗ: SELECT (GetAll + GetById), INSERT, UPDATE, BindWritableFields (bind `a.VerifyEmail ?? ""`), Map (chỉ số dịch: VerifyEmail=8, VerifyEmailPassword=9, Status=10, CreatedAt=11, UpdatedAt=12; null → `""`).
- `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs` — thêm `[ObservableProperty] _editVerifyEmail`, `_editVerifyEmailPassword`, `_showVerifyEmailPassword` + `ToggleShowVerifyEmailPasswordCommand`; gán ở LoadIntoForm, reset ở ClearForm, đọc ở Save cả 2 nhánh (new + existing). Không validate (cho phép trống).
- `orders/XuLyDonShopee.App/Views/AccountsView.axaml` — thêm card "EMAIL XÁC MINH" ngay dưới card "COOKIE ĐĂNG NHẬP": 2 cột Email xác minh | Mật khẩu email (password có nút 👁 bind `ShowVerifyEmailPassword`) + dòng chú thích TextMuted.
- `orders/XuLyDonShopee.Tests/DatabaseMigrationTests.cs` — thêm 2 test migration (`KhoiTao_DbCu_ThieuVerifyEmail_DuocThemCot_KhongMatDuLieu`, `KhoiTao_DbCu_SauMigration_GhiDocVerifyEmailBinhThuong`) + bổ sung assert 2 cột mới vào test idempotent.
- `orders/XuLyDonShopee.Tests/AccountRepositoryTests.cs` — thêm 3 test roundtrip (`Insert_CoVerifyEmail_...`, `Insert_KhongCoVerifyEmail_TraVeRong`, `Update_ThayDoiVerifyEmail_LuuDung_KhongLanChiSoCot`).

**Bước 2 — phát hiện trạng thái trang**
- `orders/XuLyDonShopee.Core/Services/ShopeePageState.cs` — MỚI: enum `ShopeePageState { LoggedIn, LoginForm, Verify, Captcha, Unknown }`.
- `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs` — thêm `DetectPageStateAsync` + `TryVerifyByEmailAsync` vào `ILoginSession`; cài `DetectPageStateAsync` trong `LoginSession` (URL captcha→Captcha; URL `/verify`→Verify; ô login hiển thị qua `getClientRects`→LoginForm; alert text otp/xác minh→Verify; cookie phiên→LoggedIn; còn lại Unknown; catch-all→Unknown không bao giờ ném).

**Bước 3 — verify qua email Hotmail** (cùng file `ShopeeLoginService.cs`)
- `TryVerifyByEmailAsync` + các helper riêng-tư: `LoginHotmailAsync`, `OpenShopeeMailAndConfirmAsync`, `ClickConfirmLinkInMailAsync`, `TryClickPivotAsync`, `FindShopeeMailRowAsync`, `IsAnyVisibleByClientRectsAsync`, `IsElementVisibleByClientRectsAsync`, `IsSelectorVisibleAsync`, `ReadAlertTextAsync`, `FindFirstVisibleByRectsAsync`, `FindVisibleByTextAsync`, `FindVisibleByTextInFramesAsync`; các mảng selector Microsoft/Outlook + regex vi/en. Cap tổng 4' bằng linked CTS (timeout nội bộ ≠ hủy người dùng — phân biệt ở catch); LUÔN đóng tab Hotmail (finally) + đóng tab xác nhận; KHÔNG log giá trị mật khẩu.

**Bước 4 — điều phối** `orders/XuLyDonShopee.App/Services/AccountSession.cs`
- Thêm `captchaResets`/`MaxCaptchaResets=2` (ngoài vòng relaunch) + `relaunchForCaptcha` (trong vòng). Thay khối `TryHumanLoginAsync` đơn lẻ bằng chuỗi: poll `DetectPageStateAsync` ~12s → LoginForm thì `TryHumanLoginAsync` + chờ 8s + detect lại → Verify thì `TryVerifyByEmailAsync` (nếu có cấu hình; thất bại/thiếu → log + giữ phiên) → Captcha thì `relaunchForCaptcha=true` (nếu còn lượt). Poll loop có thêm điều kiện `!relaunchForCaptcha`; sau finally (đã dispose = Brave chết) mới `ProfileJanitor.TryResetDirectory` + `continue` mở lại. `_readyForActions=true` ở MỌI nhánh trừ captcha-relaunch.

### Kết quả build/test (nguyên văn)

- `dotnet build orders/XuLyDonShopee.Core` → `Build succeeded. 0 Warning(s) 0 Error(s)`
- `dotnet build orders/XuLyDonShopee.App` → `Build succeeded. 0 Warning(s) 0 Error(s)`
- `dotnet test orders/XuLyDonShopee.Tests` → `Passed! - Failed: 0, Passed: 774, Skipped: 0, Total: 774`
- `dotnet build suite/Shopee.Suite` → `Build succeeded. 0 Warning(s) 0 Error(s)`

### Tiêu chí CHỈ kiểm được bằng chạy tay (chưa chạy)

- Luồng end-to-end verify: trang verify Shopee → click "verify by email" → tab Hotmail login → tab "Khác" → mail Shopee → click xác nhận → về seller LoggedIn. **Chưa chạy tay** (cần tài khoản Shopee bị verify + hộp thư Hotmail thật). Selector Shopee-verify / Outlook viết dò linh hoạt nhiều selector + regex vi/en + timeout ngắn bỏ qua được + log DOM (title/url) khi không khớp để tinh chỉnh — nhưng câu chữ/DOM thật của trang verify seller Shopee và layout Outlook hiện tại CHƯA xác nhận, cần chạy thật soi log để tinh chỉnh selector nếu trượt.
- Captcha thật: logic đã review (đóng browser qua DisposeAsync = kill Brave + WaitForExit TRƯỚC khi TryResetDirectory; đếm 2 lần; hết lượt giữ cửa sổ). Chưa ép được captcha thật để chạy tay.

### Điểm tự chốt thêm ngoài plan (cần phiên chính soi)

1. **DetectPageStateAsync có thêm tín hiệu phụ alert-text cho Verify** (otp/mã xác/xác minh) đặt SAU bước kiểm login-form — để không nhận nhầm alert "sai mật khẩu" trên form login thành Verify. Plan chỉ nêu URL `/verify`; mình thêm alert-text theo đúng "chép logic từ ShopeeAccountChecker" cho chắc.
2. **Poll DetectPageStateAsync ~12s ở đầu điều phối** (thay vì detect 1 lần) — chống race SPA chưa render form login → nếu detect 1 lần trả Unknown thì sẽ KHÔNG auto-login (regression so với hôm nay, vì code cũ `TryHumanLoginAsync` tự poll ô login 5s). Poll tới khi state ≠ Unknown hoặc hết 12s. Hồ sơ đã login → trả LoggedIn ngay, không đợi.
3. **Cap 4' bằng linked CTS trong TryVerifyByEmailAsync**: timeout nội bộ ném OCE nhưng được lọc `when (!ct.IsCancellationRequested)` → degrade `return false` (KHÔNG ném lên làm dừng oan cả phiên); chỉ OCE do người dùng Dừng mới ném xuyên.
4. **Click link xác nhận trong mail dùng click MÙ (không hit-test)** vì link thường nằm trong iframe reading-pane của Outlook → `document.elementFromPoint` lệch hệ tọa độ giữa main-frame và iframe khiến hit-test luôn fail; thân mail đơn giản (không submenu/flyout đè) nên click thẳng an toàn. Dò link qua `FindVisibleByTextInFramesAsync` (quét mọi frame).
5. **Regex "verify by email" = chỉ từ `email`** (case-insensitive) theo plan; ưu tiên selector cấu trúc (button/a/[role=button]) trước div → giảm khớp nhầm container mô tả. Nếu trang verify seller Shopee dùng câu chữ khác (vd chỉ "Liên kết Email") vẫn khớp; nếu không có từ "email" (khó xảy ra) sẽ trượt và log DOM.
