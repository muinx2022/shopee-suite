# Plan GĐ2: Port đăng nhập (subaccount + SSO) sang extension; mail xác nhận giữ Playwright riêng

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm (gộp với GĐ1 theo yêu cầu user)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Phụ thuộc:** `plans/2026-07-23-gd1-cau-noi-extension-orders.md` (cầu nối WS + extension shopee-orders — làm TRƯỚC)

## 1. Bối cảnh & mục tiêu

User chốt gộp GĐ1+GĐ2: nút chạy vừa né captcha vừa **tự đăng nhập** (khỏi login tay khi test). GĐ2 port phần đăng nhập trên domain Shopee sang extension (chạy trong trình duyệt SẠCH của GĐ1), để sau bước này phiên tự tới `/portal/shop` rồi chạy lát cắt của GĐ1.

**Quyết định kiến trúc mail (CHỐT):** luồng Hotmail/Outlook (mở hộp thư, đăng nhập Microsoft, tìm mail "Cảnh báo bảo mật", bấm "TẠI ĐÂY") **GIỮ NGUYÊN Playwright** nhưng chạy trong **một trình duyệt Playwright RIÊNG, bật theo yêu cầu** (không phải trình duyệt sạch của Shopee). Lý do: domain Microsoft (`login.microsoftonline.com`/`outlook.live.com`) KHÔNG bị Shopee anti-bot soi → không cần cơ chế sạch; tái dùng `LoginHotmailAsync`/`OpenMailboxSignedInAsync`/`OpenShopeeMailAndConfirmAsync`/`ClickConfirmLinkInMailAsync` sẵn có (~400 dòng, `ShopeeLoginService.cs:1543-1738+`). Link xác nhận "TẠI ĐÂY" là URL token → bấm ở trình duyệt Playwright riêng vẫn xác nhận server-side; trình duyệt sạch (đang poll) sẽ thấy đăng nhập xong.

**Port sang extension (domain Shopee):** điền form subaccount, chờ user nhập code, phát hiện đã đăng nhập, click "Tài khoản của tôi" → "Kênh Người bán" → `/portal/shop`.

## 2. Phạm vi

- **Làm:**
  - Extension: match thêm `subaccount.shopee.com/*` (và `accounts.shopee.vn/*` nếu luồng cần); thêm lệnh `login{user,pass}`, `waitLoggedIn`, `gotoSellerCentre` (SSO click); thêm khả năng **gõ trusted** qua `chrome.debugger` (`Input.dispatchKeyEvent`, port từ `CdpInputController.TypeAsync`).
  - C# `OrdersBridgeSession`: orchestrate đăng nhập: gửi `login` → nếu Shopee đòi code/verify → bật **MailboxPlaywright** (trình duyệt Playwright riêng) đúng nhánh (mở hộp thư cho user đọc code / auto-confirm) → poll extension `waitLoggedIn` → `gotoSellerCentre` → tiếp lát cắt GĐ1.
  - C# `OrdersMailboxSession` (MỚI, hoặc tách từ code cũ): bọc Playwright riêng chạy các hàm mail sẵn có, KHÔNG dùng chung trình duyệt sạch.
  - Tái dùng hàm thuần: `IsMyAccountNavText`, `IsSellerChannelText`, các regex/selector form subaccount (`SubUserSelectors`/`SubPassSelectors`/`SubSubmitSelectors`/`SignInRegex`).
- **KHÔNG làm (để GĐ3-4):**
  - KHÔNG port đọc đơn/chuẩn bị hàng/in phiếu/sync (GĐ3-4).
  - KHÔNG tự nhập code hộ user (code là thao tác tay — extension chỉ điền user/pass rồi CHỜ user gõ code).
  - KHÔNG đụng `OpenAsync`/nút "▶ Chạy" cũ.

## 3. Các bước (làm SAU khi GĐ1 xong)

### Bước 1 — Extension: gõ trusted + lệnh đăng nhập
- `background.js`: thêm `trustedType(text)` qua `chrome.debugger` `Input.dispatchKeyEvent` (port logic `CdpInputController.TypeAsync`/`KeyInfo`, `CdpInputController.cs:180-277`); thêm `trustedKey(Enter)`.
- Lệnh `login{user,pass}`: trên `subaccount.shopee.com`, executeScript tìm ô user/pass (`SubUserSelectors`/`SubPassSelectors`), focus (trusted click) → `trustedType` user; focus pass → `trustedType` pass; click nút "Đăng nhập". Báo `progress`.
- Lệnh `waitLoggedIn`: poll (executeScript) tìm nav "Tài khoản của tôi" (`IsMyAccountNavText`) hoặc trang verify → báo `loggedIn` / `needVerify` / `needCode`.
- Lệnh `gotoSellerCentre`: click "Tài khoản của tôi" → "Kênh Người bán" (`IsSellerChannelText`) → theo tab/URL `banhang.shopee.vn`, đóng tab subaccount → báo `atSellerCentre`. (Port `TryLoginSubaccountAsync` bước 6-8.)
- manifest: thêm host `subaccount.shopee.com/*` (+ `accounts.shopee.vn/*` nếu cần), giữ `banhang.shopee.vn/*`.

### Bước 2 — C# `OrdersMailboxSession` (Playwright riêng cho mail)
- File MỚI `orders/XuLyDonShopee.Core/Services/OrdersMailboxSession.cs`: bật một Playwright browser riêng (có thể Chromium đóng gói hoặc trình duyệt thật, KHÔNG cần sạch), chạy `LoginHotmailAsync` + `OpenMailboxSignedInAsync` / `OpenShopeeMailAndConfirmAsync` (tái dùng — có thể refactor các hàm này nhận `IPage` từ session mail thay vì WorkPage của Shopee).
- 2 chế độ: (a) `OpenForManualCode(verifyEmail, pass)` — mở hộp thư để user đọc code; (b) `AutoConfirm(verifyEmail, pass)` — tự tìm mail "Cảnh báo bảo mật" + bấm "TẠI ĐÂY".

### Bước 3 — C# orchestrate trong `OrdersBridgeSession`
- Mở rộng `RunSliceAsync` (hoặc thêm `RunLoginThenSliceAsync`): gửi `login{user,pass}` → nghe `waitLoggedIn`:
  - `needCode` → bật `OrdersMailboxSession.OpenForManualCode` (user đọc code, tự gõ vào cửa sổ Shopee) → tiếp tục poll `waitLoggedIn`.
  - `needVerify` (trang verify sau SSO) → nếu cờ "Tự động xác nhận" bật → `OrdersMailboxSession.AutoConfirm`; tắt → mở hộp thư cho user tự bấm → poll.
  - `loggedIn` → `gotoSellerCentre` → chờ `atSellerCentre` → chạy lát cắt GĐ1 (readShopList → openShopDetail → readToShip).
- Đọc `acc.VerifyEmail`/`acc.VerifyEmailPassword`/`GetAutoConfirmEmail()` từ `_services` như luồng cũ.

### Bước 4 — Trigger (App)
- Nút "🧪 Chạy thử (bridge)" của GĐ1 nay chạy `RunLoginThenSliceAsync` (đăng nhập → lát cắt) với acc đang chọn. Đổi nhãn thành "🧪 Chạy thử (đăng nhập + shop)". Log từng bước ra panel.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App` XANH; `dotnet test` 916 giữ nguyên + test thuần mới (nếu có).
- [ ] Diff đúng phạm vi: extension `shopee-orders` (thêm lệnh login/type + host subaccount), Core (`OrdersBridgeSession` mở rộng, `OrdersMailboxSession` mới, có thể refactor hàm mail nhận IPage), App (nút trigger). KHÔNG đụng `OpenAsync`/nút "▶ Chạy".
- [ ] (Verify THẬT do user) Bấm nút với acc chưa đăng nhập → extension điền user/pass → (user gõ code) → nếu verify mail: Playwright riêng mở hộp thư / auto-confirm → tới `/portal/shop` → đọc shop → mở Chi tiết **KHÔNG captcha** → đọc "Chờ Lấy Hàng". Toàn bộ KHÔNG dính captcha ở Seller Centre.

## 5. Rủi ro & lưu ý

- **Điều phối 2 trình duyệt:** trình duyệt sạch (Shopee, extension) + Playwright riêng (mail). C# giữ cả hai; đóng mail-browser sau khi xong. Đảm bảo không nhầm hồ sơ.
- **Link "TẠI ĐÂY" click ở browser khác:** cần verify thật rằng bấm ở Playwright-mail vẫn xác nhận cho phiên Shopee sạch (kỳ vọng token-based → OK; nếu Shopee ràng theo device/session thì phải tính lại — đây là rủi ro chính của GĐ2).
- **Gõ trusted qua chrome.debugger:** port `TypeAsync` (ASCII → dispatchKeyEvent; unicode → insertText). Field Shopee có thể cần focus thật trước khi gõ.
- **Form subaccount đổi selector:** dùng nhiều selector fallback như code cũ.
- **Code là thao tác tay:** GĐ2 KHÔNG tự nhập code — chỉ mở hộp thư cho user đọc + chờ. Rõ trong log.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
