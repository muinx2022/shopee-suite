# Plan: Hub tự mở Hotmail đọc mã verify khi đăng nhập BigSeller

- **Ngày:** 2026-07-24
- **Trạng thái:** chờ (làm sau khi Plan A `hub-field-matkhau-email-bigseller` nghiệm thu xong)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Khi admin bấm "Đăng nhập trên hub" cho 1 tài khoản BigSeller, hub (`BigSellerLoginService`) chạy Chromium **headless** (Playwright) để login. Nếu BigSeller coi là thiết bị mới → hiện trang "Account Verification" đòi **mã 6 số gửi về EMAIL của tài khoản**. Hiện tại luồng này **dừng chờ admin gõ tay** mã đó (`HandleOtpAsync`).

**Mục tiêu:** thay vì chờ admin gõ tay, hub **tự mở Hotmail/Outlook** (đăng nhập bằng `Email` + `EmailPassword` của acc — field `EmailPassword` do Plan A thêm), đọc **mã 6 số** mới nhất do BigSeller gửi, rồi tự điền vào 6 ô + submit. Nếu tự đọc thất bại (email không phải hotmail, sai mật khẩu, không thấy mail…) thì **fallback về đường cũ**: vẫn `needsOtp`, chờ admin gõ tay.

**Đã chốt:** chỉ làm cho **luồng đăng nhập trên Hub** (`BigSellerLoginService`). KHÔNG đụng auto-login client desktop.

### Hiện trạng code (đường dẫn tương đối từ gốc repo)

- **Điểm chèn:** `server/Shopee.Hub.Web/Services/BigSellerLoginService.cs`
  - `Start(acctId, email, password)` (dòng 65) → `RunAsync(acctId, email, password, s)` (dòng 117) → `FillLoginLoopAsync(page, email, password, ai, s, ct)` (dòng 216) → truyền callback `onSecurityChallenge: (p, c) => HandleOtpAsync(p, s, c)` (dòng 220).
  - `HandleOtpAsync(IPage page, Session s, CancellationToken ct)` (dòng 227): bấm "Send Code" (dòng 231-236) → set `needsOtp` + `await s.Otp.Task` chờ admin (dòng 238-247) → điền 6 ô `.verification-input-box input` (dòng 254-268) → chờ Turnstile + nút Confirm bật (dòng 270-284) → click Confirm + kiểm URL đã qua (dòng 285-294).
  - Browser: `_browser` (Playwright Chromium headless, launch ở `EnsureBrowserAsync` dòng 98). Context tạo ở `RunAsync` dòng 125 với `Locale=en-US`, UA Chrome 131. `page.Context` chính là context này → mở tab mail bằng `page.Context.NewPageAsync()`.
- **Code đăng nhập Hotmail có sẵn để THAM KHẢO** (KHÔNG tham chiếu trực tiếp được — khác assembly `XuLyDonShopee.Core`, phải PORT): `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`
  - `LoginHotmailAsync(mailPage, email, password, log, rng, ct)` (dòng 1607-1746): username → (form Fluent mới "Xác minh email" → "Các cách khác để đăng nhập" → tile "Nhập mật khẩu") → password → KMSI "Có". Đã tôi luyện qua thực tế: xử lý redirect nhiều bước, form passwordless mới, KMSI, đa ngôn ngữ.
  - `OpenMailboxSignedInAsync` (dòng 1560): mở tab → goto `login.microsoftonline.com` → `LoginHotmailAsync` → goto `outlook.live.com/mail/0/`.
  - Selector Microsoft: dòng 931-954 (`MsUserSelectors`, `MsPasswordSelectors`, `MsSubmitSelectors`, `MsUsePasswordSelectors`, `MsOtherWaysSelectors`, `MsKmsiYesSelectors`, `MsSignInSelectors`). Regex đa ngôn ngữ: dòng 956-966.
  - Điểm KHÁC với nhu cầu của ta: Shopee gửi mail có **link "TẠI ĐÂY"** → code cũ **click link**. BigSeller gửi **mã 6 số** → ta cần **đọc số**, KHÔNG click. Nên phần "mở mail + lấy nội dung" phải viết mới (regex `\b\d{6}\b`), KHÔNG dùng lại `OpenShopeeMailAndConfirmAsync`.
- Core đã tham chiếu `Microsoft.Playwright` (xem `suite/Shopee.Core/BigSeller/BigSellerLoginForm.cs` dùng `IPage`), nên đặt reader mới trong `suite/Shopee.Core/BigSeller/` là hợp lệ.

## 2. Phạm vi

- **Làm:**
  1. Tạo class mới `HotmailOtpReader` trong `suite/Shopee.Core/BigSeller/HotmailOtpReader.cs`: nhận 1 `IBrowserContext` (hoặc `IPage` mail đã tạo), email, mật khẩu email → đăng nhập Outlook → đọc mã 6 số BigSeller mới nhất → trả `string?` (null nếu không lấy được).
  2. Sửa `BigSellerLoginService` để truyền `emailPassword` xuống và gọi reader trong `HandleOtpAsync` TRƯỚC khi chờ admin; ra mã thì tự điền, thất bại thì fallback chờ admin (giữ nguyên hành vi cũ).
  3. Sửa `AccountConfigPanel.razor` (`StartLogin`) + `BigSellerLoginService.Start` để chuyền `EmailPassword` vào phiên login.
- **Không làm:**
  - KHÔNG sửa luồng auto-login client desktop.
  - KHÔNG giải quyết Cloudflare Turnstile ở headless (xem Rủi ro) — ngoài phạm vi; nếu Turnstile chặn thì việc tự-điền-mã vẫn kẹt ở Confirm y như khi gõ tay, đó là vấn đề riêng.
  - KHÔNG port nguyên `OpenShopeeMailAndConfirmAsync` (logic click-link của Shopee).

## 3. Các bước thực hiện

### Bước 1 — `suite/Shopee.Core/BigSeller/HotmailOtpReader.cs` (mới)

Class `public static class HotmailOtpReader` với API chính:

```csharp
/// <summary>Trả mã 6 số BigSeller gửi về hòm mail Hotmail/Outlook của <paramref name="email"/>, hoặc null nếu
/// không lấy được (email không phải outlook/hotmail/live, sai mật khẩu, form MS đổi, không thấy mail trong hạn…).
/// Mở 1 tab mail MỚI trong context đang có (khác domain BigSeller nên không ảnh hưởng cookie muc_token), đọc xong
/// tự đóng tab. KHÔNG ném (trừ hủy) — mọi lỗi → null để caller fallback chờ admin. KHÔNG log giá trị mật khẩu.</summary>
public static async Task<string?> TryReadCodeAsync(
    IBrowserContext context, string email, string emailPassword,
    Action<string>? log, CancellationToken ct)
```

Yêu cầu triển khai:
- **Gate domain:** nếu `email` không thuộc `outlook.com/hotmail.com/live.com/live.vn/msn.com` (so đuôi sau `@`, IgnoreCase) → log "Email không phải Hotmail/Outlook → không tự đọc mã" và trả `null` ngay (để fallback gõ tay). Mật khẩu rỗng → cũng trả null.
- **Đăng nhập Outlook:** PORT logic từ `LoginHotmailAsync` (ShopeeLoginService.cs 1607-1746) + selector/regex Microsoft (931-966). ĐƠN GIẢN HOÁ cho headless server: thay các helper "human-like" (`HumanFillAsync`/`HumanMoveAndClickAsync`/`TryHumanClickVisibleAsync`/`FindFirstVisibleByRectsAsync`…) bằng Playwright locator API thẳng:
  - Tìm/điền bằng `page.Locator(sel).First`, `WaitForAsync(new(){ Timeout=... , State=Visible })`, `FillAsync`, `ClickAsync`. Bọc mỗi bước trong try/catch timeout ngắn "thấy thì làm, không thấy thì sang bước sau" (đúng tinh thần code gốc).
  - GIỮ NGUYÊN các nhánh điều hướng khó của Microsoft (đây là phần giá trị): (a) landing → bấm "Đăng nhập" nếu chưa thấy ô email; (b) poll tới ~45s để đưa về ô mật khẩu, xử form Fluent mới "Xác minh email" bằng cách bấm "Các cách khác để đăng nhập" (`MsOtherWaysSelectors` + `OtherWaysRegex`) rồi chọn tile "Nhập mật khẩu" (`MsUsePasswordSelectors` + khớp không dấu "mat khau"/"password"); (c) KMSI "Duy trì đăng nhập?" → bấm "Có" (nhận diện form qua testid `kmsiVideo`/`kmsiImage` rồi bấm `primaryButton`/`#acceptButton`/`#idSIButton9`).
  - Phát hiện sai tài khoản/mật khẩu qua `#usernameError` / `#passwordError` → trả null sớm.
  - KHÔNG log `emailPassword`.
- **Vào hộp thư:** goto `https://outlook.live.com/mail/0/` (nuốt lỗi điều hướng). Nếu bị Microsoft đẩy sang `m365.cloud.microsoft` thì goto lại outlook (giống xử lý ở `OpenShopeeMailAndConfirmAsync` dòng 1776-1788).
- **Đọc mã 6 số BigSeller:**
  - Poll tối đa ~3 phút (mã có thể tới sau vài chục giây), mỗi vòng reload/chờ.
  - Chiến lược ưu tiên ĐƠN GIẢN & BỀN: đọc `innerText` toàn trang danh sách mail (`document.body.innerText`) và/hoặc mở mail trên cùng nếu là từ BigSeller. Tiêu chí nhận mail BigSeller: dòng/preview chứa "bigseller" (sender) HOẶC chứa từ khoá mã ("verification"/"code"/"mã xác"). Trích mã bằng regex `\b(\d{6})\b` (nếu nhiều, ưu tiên mã gần cụm "code"/"verification"/"mã").
  - Tham khảo cấu trúc DOM danh sách mail Outlook từ `FindAllShopeeMailRowsAsync` (ShopeeLoginService.cs ~dòng 2090) để biết cách tìm/mở dòng mail, NHƯNG đổi tiêu chí lọc sang BigSeller và đổi hành động từ "click link" sang "đọc text + regex số".
  - Lấy được mã hợp lệ (đúng 6 chữ số) → trả về; hết deadline → null.
- **Dọn dẹp:** đóng tab mail (`mailPage.CloseAsync()`) trong `finally`, best-effort.

### Bước 2 — `server/Shopee.Hub.Web/Services/BigSellerLoginService.cs`

- `Start(string acctId, string email, string password)` → thêm tham số `string emailPassword`:
  `public bool Start(string acctId, string email, string password, string emailPassword)`. Lưu `emailPassword` để dùng ở phiên (thêm field vào `Session` hoặc truyền chuỗi xuống `RunAsync`).
- `RunAsync(acctId, email, password, s)` → thêm `emailPassword`; truyền tiếp vào `FillLoginLoopAsync`.
- `FillLoginLoopAsync(page, email, password, ai, s, ct)` → thêm `emailPassword`; đổi callback thành `onSecurityChallenge: (p, c) => HandleOtpAsync(p, s, emailPassword, c)`.
- `HandleOtpAsync(IPage page, Session s, string emailPassword, CancellationToken ct)`:
  - Sau khi bấm "Send Code" (giữ nguyên khối dòng 231-236) và TRƯỚC khi set `needsOtp`/chờ admin:
    - Nếu `emailPassword` không rỗng → thử tự đọc:
      ```csharp
      Say(s, "Thử tự mở Hotmail đọc mã…");
      string? auto = null;
      try { auto = await HotmailOtpReader.TryReadCodeAsync(page.Context, /*email*/ ???, emailPassword, m => Say(s, m), ct); }
      catch (OperationCanceledException) { throw; }
      catch (Exception ex) { Say(s, "• Tự đọc mã lỗi: " + ex.Message); }
      ```
      **Lưu ý về `email`:** `HandleOtpAsync` hiện KHÔNG có sẵn `email`. Truyền `email` xuống `HandleOtpAsync` (thêm tham số, lấy từ `RunAsync`) — vì reader cần địa chỉ email để đăng nhập Hotmail. → Chữ ký cuối: `HandleOtpAsync(IPage page, Session s, string email, string emailPassword, CancellationToken ct)`.
  - Nếu `auto` ra mã (6 số) → đi THẲNG nhánh điền mã (tái dụng khối điền 6 ô + chờ Turnstile + Confirm hiện có, dòng 250-294) với `digits = auto`. KHÔNG set `needsOtp`, KHÔNG chờ admin.
  - Nếu `auto` null (không tự đọc được) → GIỮ NGUYÊN hành vi cũ: set `needsOtp`, `Say(...)` hướng dẫn nhập tay, `await s.Otp.Task` (dòng 238-247), rồi điền như cũ.
  - Refactor gợi ý: tách khối "điền digits + chờ Confirm + kiểm URL" (dòng 250-294) thành hàm `private async Task<bool> SubmitCodeAsync(IPage page, Session s, string digits, CancellationToken ct)` để cả 2 nhánh (auto & tay) dùng chung, tránh lặp code.

### Bước 3 — `server/Shopee.Hub.Web/Components/Shared/AccountConfigPanel.razor`

- Tìm `StartLogin` trong `@code` (gọi `Login.Start(...)`). Sửa lời gọi truyền thêm `_acct.EmailPassword`:
  `Login.Start(acct.Id, acct.Email, acct.Password, acct.EmailPassword)`.
- (Không đổi UI ở plan này — ô "Mật khẩu email" đã có từ Plan A.)

## 4. Tiêu chí nghiệm thu

- [ ] Build XANH: `dotnet build server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` (kéo theo `Shopee.Core`). Nếu tiện: `dotnet build` cả solution.
- [ ] `HotmailOtpReader.TryReadCodeAsync` tồn tại, chữ ký đúng, **không ném** (mọi lỗi trả null), có gate domain outlook/hotmail/live, không log mật khẩu.
- [ ] `BigSellerLoginService.HandleOtpAsync`: khi `EmailPassword` có giá trị → gọi reader trước; ra mã thì tự điền không chờ admin; null thì fallback `needsOtp` + chờ admin y như cũ. Đọc lại diff xác nhận nhánh fallback KHÔNG bị mất (admin vẫn gõ tay được khi tự đọc hỏng).
- [ ] `Start` + `RunAsync` + `FillLoginLoopAsync` + `HandleOtpAsync` đã chuyền `email` và `emailPassword` xuyên suốt; `AccountConfigPanel.StartLogin` truyền `_acct.EmailPassword`.
- [ ] Khối điền 6 ô/Confirm dùng chung cho cả 2 nhánh (không lặp).
- [ ] (Không bắt buộc trong nghiệm thu tự động) — **cần user test thật trên VM** với 1 acc BigSeller có email Hotmail thật để xác nhận selector Outlook + parse mã còn đúng (xem Rủi ro).

## 5. Rủi ro & lưu ý

- **Selector Outlook/Microsoft đổi liên tục + chưa test thật:** Đây là rủi ro cao nhất. Form đăng nhập Microsoft (đặc biệt form Fluent "Xác minh email" mới) và DOM hộp thư Outlook thay đổi thường xuyên; code port sang mà chưa chạy với tài khoản thật thì KHÔNG chắc đúng lần đầu (giống ghi nhận ở module Đơn hàng: selector "CHƯA soi thật"). → Bắt buộc dựng log DOM khi thất bại (đã có `DumpDomAsync`; reader cũng nên log URL + trạng thái mỗi bước) để user gửi lại DOM mà chỉnh selector. Nghiệm thu build-xanh + đọc-diff là đủ cho bàn giao code; ĐÚNG-THỰC-TẾ phải chờ user chạy VM.
- **Cloudflare Turnstile ở headless:** Ngay cả khi đọc mã tự động, trang OTP BigSeller có Turnstile; ở Chromium headless nút Confirm có thể KHÔNG bật (comment cảnh báo sẵn trong `HandleOtpAsync` dòng 282). Tự-đọc-mã KHÔNG khắc phục được điều này — nếu gặp, cần chạy Chromium có màn hình ảo (Xvfb). Ngoài phạm vi plan; chỉ cần bảo đảm nhánh auto không làm hỏng nhánh tay.
- **Cookie/đăng nhập chéo domain:** Mở tab Outlook trong CÙNG context với BigSeller là chấp nhận được — `CaptureAndSaveAsync` lọc cookie theo domain chứa "bigseller" nên cookie Microsoft không lẫn vào file cookie acc. Vẫn đóng tab mail sau khi xong.
- **Không log mật khẩu email** (giống code gốc). Log chỉ nêu bước/kết quả.
- **Fallback là bất biến:** đường admin-gõ-tay PHẢI còn nguyên vẹn để dùng khi tự đọc hỏng — đừng thay thế, chỉ THÊM nhánh tự đọc phía trước.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
