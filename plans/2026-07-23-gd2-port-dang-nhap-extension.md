# Plan GĐ2: Port đăng nhập (subaccount + SSO) sang extension; mail xác nhận giữ Playwright riêng

- **Ngày:** 2026-07-23
- **Trạng thái:** hoàn thành — **ĐẠT, verified end-to-end 2026-07-24** (login Playwright → clean+extension → Chi tiết KHÔNG captcha). **PIVOT 2026-07-24 (user chốt):** BỎ port login sang extension (kẹt selector/gõ-trusted/dò-tab). Thay bằng: **đăng nhập bằng Playwright code cũ (`OpenAsync`+`TryLoginSubaccountAsync`, đã chín, gồm cả mở hộp thư+chờ mã+SSO) tới `/portal/shop` → `DisposeAsync` đóng hẳn (nhả khoá hồ sơ) → mở lại CÙNG hồ sơ bằng trình duyệt SẠCH+extension chạy lát cắt Seller Centre**. Lý do: subaccount login + /portal/shop KHÔNG bị captcha (chỉ "Chi tiết" mới dính) nên login qua CDP an toàn. Bỏ `OrdersMailboxSession` + toàn bộ lệnh login/checkLogin/gotoSellerCentre + gõ-trusted trong extension. Extension quay về phạm vi GĐ1 (readShopList/openShopDetail/readToShip) + giữ robustness (cổng cố định 47821, wake, ensureListTab theo banhang). Chi tiết ở prompt giao Opus.
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Phụ thuộc:** `plans/2026-07-23-gd1-cau-noi-extension-orders.md` (cầu nối WS + extension shopee-orders — làm TRƯỚC)

## 1. Bối cảnh & mục tiêu

User chốt gộp GĐ1+GĐ2: nút chạy vừa né captcha vừa **tự đăng nhập** (khỏi login tay khi test). GĐ2 port phần đăng nhập trên domain Shopee sang extension (chạy trong trình duyệt SẠCH của GĐ1), để sau bước này phiên tự tới `/portal/shop` rồi chạy lát cắt của GĐ1.

**ĐƠN GIẢN HOÁ (user chốt 2026-07-23): BỎ HẲN xác nhận qua email / "TẠI ĐÂY".** Luồng chỉ còn: đăng nhập subaccount → nếu Shopee đòi CODE thì mở + đăng nhập hộp thư để **NGƯỜI DÙNG tự đọc code** → user tự gõ code vào form. KHÔNG tự tìm mail, KHÔNG click link xác nhận, KHÔNG auto-confirm.

**Quyết định kiến trúc mail (CHỐT):** phần Hotmail/Outlook chạy trong **một trình duyệt Playwright RIÊNG, bật theo yêu cầu** (không phải trình duyệt sạch của Shopee — domain Microsoft không bị Shopee soi). CHỈ tái dùng `LoginHotmailAsync` + `OpenMailboxSignedInAsync` (đăng nhập + mở hộp thư). KHÔNG dùng `OpenShopeeMailAndConfirmAsync`/`ClickConfirmLinkInMailAsync`/`TryVerifyByEmailAsync` (nhánh verify-email — BỎ).

**Port sang extension (domain Shopee):** điền form subaccount, chờ user nhập code, phát hiện đã đăng nhập, click "Tài khoản của tôi" → "Kênh Người bán" → `/portal/shop`.

## 2. Phạm vi

- **Làm:**
  - Extension: match thêm `subaccount.shopee.com/*` (và `accounts.shopee.vn/*` nếu luồng cần); thêm lệnh `login{user,pass}`, `waitLoggedIn`, `gotoSellerCentre` (SSO click); thêm khả năng **gõ trusted** qua `chrome.debugger` (`Input.dispatchKeyEvent`, port từ `CdpInputController.TypeAsync`).
  - C# `OrdersBridgeSession`: orchestrate đăng nhập: gửi `login` → nếu Shopee đòi CODE → bật **MailboxPlaywright** (trình duyệt Playwright riêng) mở hộp thư cho user tự đọc code → poll extension `waitLoggedIn` → `gotoSellerCentre` → tiếp lát cắt GĐ1. (KHÔNG có nhánh auto-confirm/verify.)
  - C# `OrdersMailboxSession` (MỚI): bọc Playwright riêng, CHỈ `OpenForManualCode` (đăng nhập Hotmail + mở hộp thư), KHÔNG dùng chung trình duyệt sạch.
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
- File MỚI `orders/XuLyDonShopee.Core/Services/OrdersMailboxSession.cs`: bật một Playwright browser riêng (Chromium đóng gói hoặc trình duyệt thật, KHÔNG cần sạch), chạy `LoginHotmailAsync` + `OpenMailboxSignedInAsync` (tái dùng — có thể refactor nhận `IPage` từ session mail thay vì WorkPage của Shopee).
- CHỈ MỘT chế độ: `OpenForManualCode(verifyEmail, pass)` — đăng nhập Hotmail + mở hộp thư rồi DỪNG để user tự đọc code. KHÔNG AutoConfirm, KHÔNG tìm mail, KHÔNG click link.

### Bước 3 — C# orchestrate trong `OrdersBridgeSession`
- Thêm `RunLoginThenSliceAsync`: gửi `login{user,pass}` → nghe `waitLoggedIn`:
  - `needCode` (ô nhập code hiện) → bật `OrdersMailboxSession.OpenForManualCode` (user đọc code, tự gõ vào cửa sổ Shopee) → tiếp tục poll `waitLoggedIn`.
  - `loggedIn` (nav "Tài khoản của tôi") → `gotoSellerCentre` → chờ `atSellerCentre` → chạy lát cắt GĐ1 (readShopList → openShopDetail → readToShip).
- Đọc `acc.VerifyEmail`/`acc.VerifyEmailPassword` từ `_services` (để đăng nhập hộp thư). KHÔNG dùng `GetAutoConfirmEmail()` / nhánh verify — đã bỏ.

### Bước 4 — Trigger (App)
- Nút "🧪 Chạy thử (bridge)" của GĐ1 nay chạy `RunLoginThenSliceAsync` (đăng nhập → lát cắt) với acc đang chọn. Đổi nhãn thành "🧪 Chạy thử (đăng nhập + shop)". Log từng bước ra panel.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App` XANH; `dotnet test` 916 giữ nguyên + test thuần mới (nếu có).
- [ ] Diff đúng phạm vi: extension `shopee-orders` (thêm lệnh login/type + host subaccount), Core (`OrdersBridgeSession` mở rộng, `OrdersMailboxSession` mới, có thể refactor hàm mail nhận IPage), App (nút trigger). KHÔNG đụng `OpenAsync`/nút "▶ Chạy".
- [ ] (Verify THẬT do user) Bấm nút với acc chưa đăng nhập → extension điền user/pass → nếu đòi code: Playwright riêng mở hộp thư (user tự đọc code + gõ) → tới `/portal/shop` → đọc shop → mở Chi tiết **KHÔNG captcha** → đọc "Chờ Lấy Hàng". Toàn bộ KHÔNG dính captcha ở Seller Centre.

## 5. Rủi ro & lưu ý

- **(Rủi ro "TẠI ĐÂY ở browser khác" ĐÃ BỎ)** — luồng verify-email bị loại nên không còn lo link xác nhận. Mail chỉ để user đọc code.
- **Điều phối 2 trình duyệt:** trình duyệt sạch (Shopee, extension) + Playwright riêng (mail). C# giữ cả hai; đóng mail-browser sau khi user xong. Đảm bảo không nhầm hồ sơ.
- **Gõ trusted qua chrome.debugger:** port `TypeAsync` (ASCII → dispatchKeyEvent; unicode → insertText). Field Shopee có thể cần focus thật trước khi gõ.
- **Form subaccount đổi selector:** dùng nhiều selector fallback như code cũ.
- **Code là thao tác tay:** GĐ2 KHÔNG tự nhập code — chỉ mở hộp thư cho user đọc + chờ. Rõ trong log.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
