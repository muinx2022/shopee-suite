# Plan: Đăng nhập Shopee qua Nền tảng tài khoản phụ (subaccount.shopee.com)

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Module Đơn hàng (`orders/`) hiện mở thẳng `https://banhang.shopee.vn/` khi chạy phiên tài khoản
(`ShopeeLoginService.OpenAsync` → Goto `SellerUrl`), rồi `AccountSession.RunAsync` điều phối:
detect trạng thái → tự điền form banhang (`TryHumanLoginAsync`) → verify qua email
(`TryVerifyByEmailAsync`) → né captcha.

**Yêu cầu mới của người dùng:** KHÔNG mở trang bán hàng trực tiếp nữa. Luồng mới:

1. Mở `https://subaccount.shopee.com/` ("Nền tảng tài khoản phụ" — trang Vue SPA tiếng Việt).
2. Nếu hiện form đăng nhập → tự điền tài khoản (ô "SĐT/Email/Tên đăng nhập") + mật khẩu
   (ô "Mật khẩu Đăng nhập") kiểu người rồi bấm nút "Đăng nhập".
3. Sau khi bấm Đăng nhập, Shopee **đòi mã code**. App **đăng nhập hộp thư Hotmail/Outlook như cũ**
   (mở tab mới, login Microsoft, vào hộp thư) rồi **DỪNG — KHÔNG tự verify/không tự bấm gì trong mail**.
   Người dùng tự đọc mã trong mail và tự gõ vào trang Shopee.
4. App chờ trên trang subaccount; khi **không còn trang login nữa** (đã nhập code xong, quay về trang
   tài khoản) → click **"Tài khoản của tôi"** (nav trái) → click tiếp **"Kênh Người bán"** (entry góc trên).
5. "Kênh Người bán" đưa vào Seller Centre (banhang.shopee.vn — có thể tab mới hoặc cùng tab). Từ đó
   mọi thứ chạy như cũ (theo dõi đơn, sync, xử lý đơn... đều thao tác trên `_context.Pages[0]`).

DOM thật do người dùng cung cấp (trang login subaccount):
- Card login: `div.card.login-card`, tiêu đề "Nền tảng tài khoản phụ".
- Ô user: `input[type='text'].shopee-input__input` placeholder `"SĐT/Email/Tên đăng nhập"` (KHÔNG có `name`).
- Ô pass: `input[type='password'].shopee-input__input` placeholder `"Mật khẩu Đăng nhập"` (KHÔNG có `name`).
- Nút: `button.shopee-button.shopee-button--primary` (type=`button`, KHÔNG phải submit) chứa `<span>Đăng nhập</span>`.

DOM thật trang sau đăng nhập (nav):
- Nav trái: `li.nav-item.account-nav` (hoặc `li.router-link-active.nav-item.account-nav`) chứa text **"Tài khoản của tôi"**;
  mục khác "Phân bổ chat" (đừng khớp nhầm).
- Entry: `div.entry.sc-entry > span.entry-text` text **"Kênh Người bán"** (kèm icon svg chevron bên trong span).

DOM trang nhập code: **CHƯA soi được** — không detect riêng, chỉ chờ tín hiệu "đã đăng nhập"
(nav "Tài khoản của tôi" hiển thị).

Ràng buộc/quyết định đã chốt:
- Dùng chính field tài khoản hiện có: `Email` + `Password` để login subaccount; `VerifyEmail` +
  `VerifyEmailPassword` để mở hộp thư (như luồng verify cũ).
- Sau khi Seller Centre mở ở TAB MỚI → **đóng tab subaccount** (và tab mail) để tab seller trở thành
  `Pages[0]` — toàn bộ hàm hiện có (DetectPageStateAsync, ReadToShipCountAsync, xử lý đơn…) đều lấy
  `_context.Pages[0]` làm trang thao tác (11 chỗ trong ShopeeLoginService.cs).
- Luồng cũ trên trang banhang (verify email tự động, captcha-reset) GIỮ NGUYÊN làm bước sau: khi đã vào
  được seller, state machine hiện có trong `RunAsync` vẫn chạy để xử verify/captcha nếu banhang đòi thêm.

## 2. Phạm vi

- **Làm:**
  - `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs` — const URL mới, đổi Goto trong
    `OpenAsync`, thêm method interface + triển khai luồng subaccount, tách helper mở hộp thư, regex + forwarder test.
  - `orders/XuLyDonShopee.App/Services/AccountSession.cs` — gọi luồng subaccount trong `RunAsync` trước
    state machine hiện có.
  - `orders/XuLyDonShopee.Tests/` — test mới cho các hàm khớp text thuần; toàn bộ test cũ vẫn xanh.
- **Không làm:**
  - KHÔNG đụng luồng suite/ (Kiểm tra tài khoản, Search…) — chỉ module orders.
  - KHÔNG đổi `SellerUrl` và các URL banhang khác (`AllOrdersUrl`, `ShippingSettingsUrl`…) — vẫn dùng
    cho điều hướng sau khi đã vào seller.
  - KHÔNG tự đọc mã code trong mail / tự điền code — người dùng tự làm (yêu cầu rõ).
  - KHÔNG xóa `TryHumanLoginAsync` / `TryVerifyByEmailAsync` — giữ làm fallback trên trang banhang.
  - KHÔNG bump version / release.

## 3. Các bước thực hiện

### Bước 1 — `ShopeeLoginService.cs`: hằng số + OpenAsync

1. Thêm cạnh `SellerUrl`:
   ```csharp
   /// <summary>URL Nền tảng tài khoản phụ — điểm vào đăng nhập mới (từ đây bấm "Kênh Người bán" để sang Seller Centre).</summary>
   public const string SubaccountUrl = "https://subaccount.shopee.com/";
   ```
2. Trong `OpenAsync` (~dòng 451): `page.GotoAsync(SellerUrl, …)` → `page.GotoAsync(SubaccountUrl, …)`.
   Giữ nguyên nuốt lỗi điều hướng.

### Bước 2 — Interface `ILoginSession`: method mới

Thêm vào `ILoginSession` (kèm doc comment kiểu hiện có — Graceful, không bao giờ ném trừ hủy):

```csharp
Task<bool> TryEnterSellerViaSubaccountAsync(
    string user, string password, string? verifyEmail, string? verifyEmailPassword,
    Action<string>? log = null, CancellationToken ct = default);
```

Trả `true` khi đã đứng ở Seller Centre (banhang.shopee.vn) và tab seller là `Pages[0]`.
Mọi thất bại → log + `false` (caller giữ cửa sổ cho người dùng thao tác tay).

### Bước 3 — Triển khai trong `LoginSession`

Selector/regex mới (đặt cạnh nhóm selector hiện có, nhiều fallback, KHÔNG bám text EN cứng):

```csharp
// Form login subaccount: input KHÔNG có name → dò trong .login-card trước, placeholder sau, type cuối.
private static readonly string[] SubUserSelectors =
    { ".login-card input[type='text']", "input[placeholder*='Tên đăng nhập']", "input[placeholder*='SĐT']", "input[type='text']" };
private static readonly string[] SubPassSelectors =
    { ".login-card input[type='password']", "input[type='password']" };
// Nút "Đăng nhập" là button type=button → KHÔNG dùng button[type='submit']; khớp text bằng SignInRegex có sẵn.
private static readonly string[] SubSubmitSelectors =
    { ".login-card button.shopee-button--primary", "button.shopee-button--primary", "button", "[role='button']" };
// Nav "Tài khoản của tôi" + entry "Kênh Người bán" (vi có dấu / không dấu / en).
private static readonly Regex MyAccountNavRegex =
    new(@"tài khoản của tôi|tai khoan cua toi|my account", RegexOptions.IgnoreCase | RegexOptions.Compiled);
private static readonly Regex SellerChannelRegex =
    new(@"kênh người bán|kenh nguoi ban|seller\s*cent(re|er)|seller\s*channel", RegexOptions.IgnoreCase | RegexOptions.Compiled);
```

Forwarder test (pattern dòng 283–294 hiện có): `internal static bool MatchesMyAccountNav(string?)`,
`internal static bool MatchesSellerChannelEntry(string?)` ở class ngoài `ShopeeLoginService`, gọi
xuống matcher trong `LoginSession` (dùng `Regex.IsMatch` trên text đã qua `NormalizeForMatch` hoặc
regex chứa cả dạng có dấu/không dấu như trên — chọn một cách và test cả hai dạng).

Thân method `TryEnterSellerViaSubaccountAsync` — làm theo đúng khung `TryVerifyByEmailAsync` hiện có
(timeout nội bộ linked CTS, catch OCE phân biệt hủy người dùng, log từng bước, KHÔNG log mật khẩu):

1. `page = _context.Pages[0]`; null → false. Linked CTS cap **20 phút** (chờ người dùng gõ code là
   phần lâu nhất).
2. **Dò trạng thái đầu** (poll tối đa ~15s, SPA còn render): 
   - "Đang ở form login" = ô pass của `SubPassSelectors` HIỂN THỊ (getClientRects — dùng
     `IsAnyVisibleByClientRectsAsync`/`FindFirstVisibleAsync` sẵn có).
   - "Đã đăng nhập" = phần tử khớp `MyAccountNavRegex` hiển thị (`FindVisibleByTextAsync` trên
     `new[] { "li", "a", "div", "span", "[role='menuitem']" }`).
   - Cả hai không thấy sau 15s → log title/url (pattern chẩn đoán hiện có) rồi **cứ thử tiếp nhánh
     "đã đăng nhập"** (bước 6) — nếu bước 6 cũng trượt sẽ tự false.
   - **QUAN TRỌNG:** KHÔNG dùng guard `ShopeeLoginCookies.IsLoggedIn` ở đây — cookie SPC_* của
     shopee.vn trong hồ sơ KHÔNG nói gì về phiên subaccount.shopee.com.
3. **Nếu ở form login:**
   - `password` rỗng → log "Tài khoản chưa có mật khẩu — đăng nhập tay." → false.
   - Re-query handle TƯƠI (Vue re-render): `HumanFillAsync` user rồi pass (hàm sẵn có — đã tự clear
     autofill), tìm nút qua `FindVisibleByTextAsync(page, SubSubmitSelectors, SignInRegex, …)` (khớp
     text "Đăng nhập" — nhớ nút nằm trong `<span>`, InnerText của button vẫn ra "Đăng nhập"), click
     kiểu người (`TryHumanClickVisibleAsync`). Không thấy nút → log title/url → false.
4. **Mở hộp thư cho người dùng lấy code** (chỉ khi `verifyEmail` + `verifyEmailPassword` đủ):
   - Tách từ `TryVerifyByEmailAsync` (BƯỚC 2 hiện có, dòng ~1027–1057) một helper private:
     `Task<(IPage? mailPage, bool loggedIn)> OpenMailboxSignedInAsync(string email, string pass, Action<string>? log, Random rng, CancellationToken ct)`
     — NewPage → Goto `https://login.microsoftonline.com/` (nuốt lỗi) → `LoginHotmailAsync` → Goto
     `https://outlook.live.com/mail/0/` (nuốt lỗi). `TryVerifyByEmailAsync` refactor để GỌI LẠI helper
     này (hành vi cũ không đổi: loggedIn=false → return false như cũ).
   - Trong luồng subaccount: mail login fail → CHỈ log cảnh báo, GIỮ tab mail mở (người dùng login tay);
     KHÔNG return false vì code vẫn có thể lấy bằng cách khác.
   - Thiếu cấu hình email → log "Chưa cấu hình Email xác minh — bạn tự lấy mã và nhập vào trang Shopee."
   - Sau khi mở hộp thư: `page.BringToFrontAsync()` (best-effort) để cửa sổ quay về trang Shopee cho
     người dùng gõ code. Log rõ: "Đã mở hộp thư ở tab bên — lấy mã rồi nhập vào trang Shopee."
5. **Chờ người dùng nhập code** — poll mỗi 3s, tối đa **15 phút**: điều kiện thoát = phần tử khớp
   `MyAccountNavRegex` HIỂN THỊ (đã quay về trang tài khoản). Hết giờ → log → false. KHÔNG tự bấm
   gì trong mail, KHÔNG tự điền code.
6. **Đóng tab mail** (best-effort, chỉ tab mình mở) rồi **click "Tài khoản của tôi"**:
   `FindVisibleByTextAsync` với `MyAccountNavRegex` (selector như bước 2) → `TryHumanClickVisibleAsync`
   → dừng ngẫu nhiên 1.5–3s. Không thấy → log title/url → false.
7. **Click "Kênh Người bán":** target qua `FindVisibleByTextAsync(page, new[] { "span.entry-text", ".entry", "span", "div", "[role='button']", "a" }, SellerChannelRegex, …)`.
   TRƯỚC khi click: hứng tab mới bằng `_context.WaitForPageAsync` (chạy song song, timeout dài) hoặc
   handler event `_context.Page`. Sau click chờ tối đa **90s** cho MỘT trong hai:
   - tab mới có URL chứa `banhang.shopee.vn` (chờ thêm DOMContentLoaded best-effort), HOẶC
   - chính `page` điều hướng sang `banhang.shopee.vn`.
   Không thấy → log title/url mọi tab đang mở → false.
8. **Chuẩn hóa tab:** nếu seller là TAB MỚI → đóng tab subaccount (retry tối đa 3 lần; vẫn fail → log
   cảnh báo RÕ "tab subaccount chưa đóng được — theo dõi đơn có thể đọc nhầm tab"). Mục tiêu bất biến:
   sau bước này `_context.Pages[0]` là trang banhang. Log "Đã vào Kênh Người bán."
9. `return true`. Catch: OCE do timeout nội bộ (ct người dùng chưa hủy) → log + false; OCE hủy thật →
   rethrow; Exception khác → log message + false. `finally`: KHÔNG đóng tab seller/subaccount ở đây
   (đóng có chủ đích ở bước 6/8 rồi).

### Bước 4 — `AccountSession.RunAsync` (App, khối bước 4, ~dòng 1861–1948)

Trong `if (acc is not null)`, TRƯỚC đoạn poll `DetectPageStateAsync` hiện có:

```csharp
SetStatus(SessionState.Running, "Đang vào Nền tảng tài khoản phụ (subaccount)...");
bool entered;
try { entered = await session.TryEnterSellerViaSubaccountAsync(acc.Email, acc.Password, acc.VerifyEmail, acc.VerifyEmailPassword, loginLog, ct).ConfigureAwait(false); }
catch (OperationCanceledException) { throw; }
catch { entered = false; }
```

- `entered == true` → chạy TIẾP toàn bộ state machine hiện có (poll detect → (a) LoginForm fallback →
  (b) Verify → (c) Captcha) — giờ nó soi trang banhang ở `Pages[0]`, thường ra LoggedIn ngay.
- `entered == false` → log `"Không tự vào được Kênh Người bán — GIỮ cửa sổ để bạn thao tác tay."` và
  **BỎ QUA state machine** (bọc khối hiện có trong `if (entered) { … }`) — state machine viết cho trang
  banhang, chạy trên trang subaccount sẽ điền form sai chỗ. Các nhánh dưới (`_readyForActions = true`,
  vòng poll cookie/đơn) giữ nguyên — nhánh false vẫn phải bật `_readyForActions` như mọi đường degrade
  (đã tự nhiên vì `relaunchForCaptcha` vẫn false).
- Cập nhật comment đầu khối bước 4 mô tả luồng mới (subaccount → nav → seller → state machine cũ làm lưới an toàn).

### Bước 5 — Test (`orders/XuLyDonShopee.Tests`, file mới `SubaccountNavMatchTests.cs`)

Test các hàm thuần qua forwarder (pattern các test matcher hiện có):
- `MatchesMyAccountNav`: đúng với `"Tài khoản của tôi"`, `" Tài khoản của tôi "` (space/newline thừa như
  InnerText thật), `"tai khoan cua toi"`, `"My Account"`; sai với `"Phân bổ chat"`, `"Tài khoản"`,
  `""`/null.
- `MatchesSellerChannelEntry`: đúng với `"Kênh Người bán"`, `"Kênh Người bán\n"` (InnerText của span có
  icon), `"kenh nguoi ban"`, `"Seller Centre"`, `"Seller Center"`; sai với `"Kênh"`,
  `"Hiệu quả hoạt động CSKH"`, null.
- Nếu matcher dùng `NormalizeForMatch`: thêm case chữ HOA `"KÊNH NGƯỜI BÁN"`.

Chạy: `dotnet build` 3 project orders + `dotnet test orders/XuLyDonShopee.Tests` — TOÀN BỘ xanh
(hiện ~774 test; không được làm đỏ test cũ).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` các project trong `orders/` 0 error, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh toàn bộ (test cũ + test mới).
- [ ] `OpenAsync` mở `https://subaccount.shopee.com/` (không còn Goto banhang khi mở phiên).
- [ ] `ILoginSession` có `TryEnterSellerViaSubaccountAsync`; triển khai đủ 9 bước ở trên: điền form
      subaccount kiểu người → bấm Đăng nhập → mở hộp thư (không tự verify, không tự bấm trong mail) →
      BringToFront trang Shopee → chờ tối đa 15' người dùng nhập code → click "Tài khoản của tôi" →
      click "Kênh Người bán" → chờ banhang (tab mới HOẶC cùng tab) → tab seller thành `Pages[0]`.
- [ ] `RunAsync`: gọi luồng subaccount trước; `entered=false` → bỏ qua state machine cũ nhưng
      `_readyForActions` vẫn bật; `entered=true` → state machine cũ chạy như trước.
- [ ] `TryVerifyByEmailAsync` sau refactor helper hộp thư: hành vi không đổi (test cũ liên quan vẫn xanh).
- [ ] Không log mật khẩu ở bất kỳ bước nào; mọi nhánh selector trượt đều log `title=…, url=…`.

## 5. Rủi ro & lưu ý

- **DOM trang nhập code CHƯA soi** — chủ đích không detect trang code; tín hiệu duy nhất để đi tiếp là
  nav "Tài khoản của tôi" hiển thị. Nếu chạy thật thấy kẹt, đọc log title/url để tinh chỉnh (pattern đã
  dùng ở luồng verify cũ).
- **"Kênh Người bán" chưa rõ mở tab mới hay cùng tab** → bắt buộc xử cả hai đường như bước 7.
- Trang subaccount là Vue SPA — element re-render: LUÔN re-query handle tươi ngay trước fill/click,
  không giữ handle qua các bước chờ (bài học từ `SetPickupResult`, xem doc comment hiện có).
- Nút "Đăng nhập" là `type='button'` → tuyệt đối không dò `button[type='submit']`.
- KHÔNG dùng `ShopeeLoginCookies.IsLoggedIn` làm guard cho subaccount (cookie shopee.vn ≠ phiên
  subaccount.shopee.com). Vòng lưu cookie trong `RunAsync` giữ nguyên — nó chỉ lưu khi có SPC_* (xuất
  hiện sau khi vào banhang), đúng mong muốn.
- Ô pass subaccount có `max-length=16` phía wrapper — cứ gõ mật khẩu như có, không cắt.
- Chờ code là chờ NGƯỜI THẬT: giữ nguyên nhịp poll thưa (3s), không reload trang trong lúc chờ (reload
  có thể xóa ô code người dùng đang gõ).
- Sau khi vào seller, banhang vẫn có thể đòi verify/captcha → state machine cũ xử; đừng xóa/né nó.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Trạng thái:** Đã triển khai đủ 5 bước. Build 3 project orders sạch (0 error, 0 warning mới); `dotnet test` xanh 929/929.

### Đã làm

**Bước 1 — `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`:**
- Thêm hằng `public const string SubaccountUrl = "https://subaccount.shopee.com/";` cạnh `SellerUrl`.
- `OpenAsync`: đổi `page.GotoAsync(SellerUrl, …)` → `GotoAsync(SubaccountUrl, …)` (giữ nguyên nuốt lỗi điều hướng).
- Thêm 2 forwarder ở class ngoài: `MatchesMyAccountNav`, `MatchesSellerChannelEntry` (gọi xuống `LoginSession`).

**Bước 2 — interface `ILoginSession`:** thêm `TryEnterSellerViaSubaccountAsync(user, password, verifyEmail, verifyEmailPassword, log, ct)` kèm doc comment (Graceful, không ném trừ hủy; true khi seller là Pages[0]).

**Bước 3 — `LoginSession`:**
- Selector mới: `SubUserSelectors`, `SubPassSelectors`, `SubSubmitSelectors` (nhiều fallback, không bám text EN cứng, không dò `button[type='submit']`).
- Regex mới: `MyAccountNavRegex`, `SellerChannelRegex` (chứa cả dạng có dấu + không dấu + en).
- Matcher `internal static bool MatchesMyAccountNav/MatchesSellerChannelEntry`: chuẩn hóa qua `NormalizeForMatch` rồi khớp regex (trị được NFC/NFD, chữ HOA, space thừa).
- Method `TryEnterSellerViaSubaccountAsync`: đủ 9 bước — linked CTS cap 20'; dò trạng thái đầu (pass visible = form / nav "Tài khoản của tôi" visible = đã đăng nhập, KHÔNG dùng `ShopeeLoginCookies.IsLoggedIn`); điền form kiểu người + bấm "Đăng nhập" (khớp `SignInRegex`); mở hộp thư cho người dùng tự lấy mã (KHÔNG tự verify, KHÔNG bấm trong mail) + `BringToFrontAsync` về trang Shopee; chờ code poll 3s tối đa 15'; đóng tab mail + click "Tài khoản của tôi" → "Kênh Người bán"; chờ 90s banhang (tab mới HOẶC cùng tab); chuẩn hóa tab (đóng subaccount, retry 3 lần, cảnh báo nếu fail); catch OCE phân biệt hủy người dùng/timeout nội bộ; không log mật khẩu; mọi nhánh trượt log `title=…, url=…`.
- Helper `OpenMailboxSignedInAsync(email, pass, log, rng, ct)` tách từ BƯỚC 2 của `TryVerifyByEmailAsync`; `TryVerifyByEmailAsync` refactor gọi lại helper (hành vi cũ giữ nguyên: login mail fail → return false, finally đóng tab mail).

**Bước 4 — `orders/XuLyDonShopee.App/Services/AccountSession.cs` (`RunAsync`, khối bước 4):**
- Cập nhật comment mô tả luồng mới (subaccount → nav → seller → state machine cũ làm lưới an toàn).
- Gọi `TryEnterSellerViaSubaccountAsync` TRƯỚC (SetStatus "Đang vào Nền tảng tài khoản phụ (subaccount)..."), catch OCE rethrow / catch khác → `entered=false`.
- `entered=true` → chạy TIẾP toàn bộ state machine cũ (poll detect → LoginForm/Verify/Captcha) — nay soi banhang ở Pages[0].
- `entered=false` → log "Không tự vào được Kênh Người bán — GIỮ cửa sổ để bạn thao tác tay." và BỎ QUA state machine (bọc trong `if (entered) { … }`). Nhánh `_readyForActions = true` vẫn bật (relaunchForCaptcha=false).

**Bước 5 — `orders/XuLyDonShopee.Tests/SubaccountNavMatchTests.cs` (file mới):** 20 case — `MatchesMyAccountNav` (đúng: "Tài khoản của tôi", space/newline thừa, không dấu, HOA, "My Account"; sai: "Phân bổ chat", "Tài khoản", ""/null); `MatchesSellerChannelEntry` (đúng: "Kênh Người bán", "Kênh Người bán\n", không dấu, HOA, "Seller Centre", "Seller Center"; sai: "Kênh", "Hiệu quả hoạt động CSKH", ""/null).

### Kết quả kiểm chứng
- `dotnet build` Core / App / Tests: đều `Build succeeded. 0 Warning(s) 0 Error(s)`.
- `dotnet test XuLyDonShopee.Tests`: `Passed! Failed: 0, Passed: 929, Skipped: 0` (worktree có 929 test; test cũ không đỏ). Riêng filter `SubaccountNavMatchTests`: 20/20 passed.

### Điểm lệch plan (nhỏ, có lý do)
- **Chọn matcher dùng `NormalizeForMatch`** (một trong hai cách plan cho phép) thay vì khớp regex trực tiếp trên text thô — để trị NFC/NFD + chữ HOA; đã test cả hai dạng có/không dấu như plan yêu cầu.
- **Bước 7 hứng tab mới:** dùng CẢ hai cơ chế plan liệt kê — event handler `_context.Page` (bắt popup ngay khi mở, kể cả nhanh) VÀ quét `_context.Pages` trong vòng 90s (bắt cả tab SSO trung gian rồi mới ra banhang, và trường hợp điều hướng cùng tab). KHÔNG dùng `_context.WaitForPageAsync` để tránh `TimeoutException` không quan sát khi "Kênh Người bán" điều hướng cùng tab. Vẫn thỏa điều kiện plan: chờ 90s cho tab-mới-banhang HOẶC page-điều-hướng-banhang.
- **`FindVisibleByTextAsync` (khớp text thô)** dùng đúng theo plan cho nav/entry. Lưu ý rủi ro NFD như plan đã ghi (nếu chạy thật kẹt thì đọc log title/url tinh chỉnh) — regex đã kèm nhánh không dấu để đỡ.

### Chưa kiểm chứng được (ngoài phạm vi test tự động)
- Selector/DOM thật của trang subaccount + trang nhập code CHƯA soi bằng phiên trình duyệt thật (đúng như plan ghi "DOM trang nhập code chưa soi"). Logic chỉ verify qua unit test hàm thuần + build. Cần chạy thật để chốt selector form/nút/nav/entry.
