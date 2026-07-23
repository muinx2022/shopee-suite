# Plan: Khôi phục cầu SSO "Tài khoản của tôi → Kênh Người bán" trước vòng lặp shop

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nhánh:** `feature/gop-don-hang` (cây chính)

## 1. Bối cảnh & mục tiêu

Chạy thật (2026-07-23) phát hiện: sau khi đăng nhập `subaccount.shopee.com` THÀNH CÔNG, app mở thẳng
`https://banhang.shopee.vn/portal/shop` thì **Shopee đá về trang đăng nhập người bán**
(`accounts.shopee.vn/seller/login?next=...banhang.shopee.vn/portal/shop`). Log:
`Không thấy bảng shop sau 20s (trang có thể bounce về login). title=[Đăng Nhập Ngay ... Kênh Người Bán
Shopee ...], url=https://accounts.shopee.vn/seller/login?...`.

**Nguyên nhân:** `subaccount.shopee.com` và `banhang.shopee.vn` là HAI hệ đăng nhập riêng. Đăng nhập
Nền tảng tài khoản phụ KHÔNG tự cấp quyền seller. Vì chưa vào được banhang nên **cookie seller
(SPC_EC/SPC_ST/SPC_U) chưa hề được set → app không có gì để lưu** ("cookie chưa lưu"). Cầu nối là:
click **"Tài khoản của tôi"** rồi **"Kênh Người bán"** trong menu subaccount — bước này chạy SSO, chuyển
phiên sang banhang và set cookie seller. **Người dùng xác nhận: phải click "Tài khoản của tôi" TRƯỚC, rồi
"Kênh Người bán", mới ra được danh sách.**

Ở đợt làm vòng lặp shop, ta ĐÃ BỎ đúng bước này (đổi `TryEnterSellerViaSubaccountAsync` →
`TryLoginSubaccountAsync`, cắt các bước 6–8: nav clicks + chuẩn hóa tab). **Đoạn code cũ còn nguyên trong
git — commit `d4e6916`** (`orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`, các "Bước 6/7/8" của
`TryEnterSellerViaSubaccountAsync`). Việc: **khôi phục cầu SSO đó** vào cuối `TryLoginSubaccountAsync`, để
method chỉ trả `true` khi đã lập được phiên banhang (Pages[0] = banhang). Sau đó vòng lặp shop hiện có
(`ReadShopListAsync` Goto `/portal/shop` trên Pages[0]) sẽ chạy được và cookie tự lưu.

## 2. Phạm vi

- **Làm:** thêm lại bước "Tài khoản của tôi → Kênh Người bán → chờ banhang → chuẩn hóa tab" vào
  `TryLoginSubaccountAsync` (`orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`); cập nhật doc.
- **Không làm:**
  - KHÔNG đụng vòng lặp shop trong `AccountSession.RunAsync` (giữ nguyên — nó Goto `/portal/shop` trên
    Pages[0], sẽ chạy sau khi Pages[0] là banhang).
  - KHÔNG đụng `ReadShopListAsync`/`OpenShopDetailAsync`/`CloseShopTabAsync`/`WorkPage()`.
  - KHÔNG đụng module khác, không đụng suite, không release.

## 3. Các bước thực hiện

### Bước 1 — Khôi phục cầu SSO trong `TryLoginSubaccountAsync`

Hiện `TryLoginSubaccountAsync` (khoảng dòng 1095–1255) kết thúc ở: login reached (nav "Tài khoản của tôi"
hiện) → đóng tab mail → `L("Đã đăng nhập Nền tảng tài khoản phụ."); return true;`.

Sửa: **SAU khi đóng tab mail và TRƯỚC khi return true**, chèn lại nguyên văn các **Bước 6–8** của
`TryEnterSellerViaSubaccountAsync` trong commit `d4e6916` (lấy bằng
`git show d4e6916:orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`), gồm:

1. **Bước 6 (phần click):** đóng tab mail (đã có) → `FindVisibleByTextAsync(page, accountNavSelectors,
   MyAccountNavRegex, sct, 10000)` → không thấy thì log title/url + `return false`; thấy thì
   `TryHumanClickVisibleAsync` (click "Tài khoản của tôi") → delay 1.5–3s.
2. **Bước 7:** `FindVisibleByTextAsync(page, new[]{"span.entry-text",".entry","span","div","[role='button']","a"},
   SellerChannelRegex, sct, 10000)` → không thấy thì log + `return false`; thấy thì hứng tab mới qua event
   `_context.Page`, click "Kênh Người bán", chờ tối đa 90s tới khi **một tab có URL banhang** (dùng
   `UrlIsBanhang`) — cùng-tab (`page` đổi URL) hoặc tab-mới (`popped`/quét `_context.Pages`). Không thấy →
   log các tab + `return false`.
3. **Bước 8:** nếu seller ở TAB MỚI → chờ `WaitForLoadStateAsync(DOMContentLoaded, 15s)` best-effort, rồi
   đóng tab subaccount (retry ≤3) để **Pages[0] = tab banhang**; cảnh báo nếu không đóng được. Cuối cùng
   `L("Đã vào Kênh Người bán."); return true;` (thay cho câu "Đã đăng nhập Nền tảng tài khoản phụ." hiện tại
   — có thể giữ cả hai dòng log cho rõ tiến trình).

Tất cả helper cần dùng đã có sẵn trong method/class hiện tại: `MyAccountNavRegex`, `SellerChannelRegex`,
`accountNavSelectors`, `UrlIsBanhang`, `DiagAsync`, `TryHumanClickVisibleAsync`, `mx/my/rng/sct`. KHÔNG cần
thêm field mới. Giữ `try/catch` OCE (timeout nội bộ → false; hủy người dùng → throw) như hiện có.

### Bước 2 — Cập nhật doc

- Doc comment của `TryLoginSubaccountAsync` (interface `ILoginSession` + impl) hiện ghi "…rồi caller mở
  THẲNG /portal/shop" → sửa: sau đăng nhập subaccount, **click "Tài khoản của tôi" → "Kênh Người bán" để
  SSO sang Seller Centre (lập cookie banhang), chuẩn hóa Pages[0] = banhang**; caller (RunAsync) rồi mới mở
  `/portal/shop` + lặp shop. Bỏ chú thích "không dùng trong luồng mới" ở `SellerChannelRegex`/
  `MatchesSellerChannelEntry` (giờ dùng lại).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` 3 project orders + `dotnet build suite/Shopee.Suite` — 0 error, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh (911, không đỏ test cũ).
- [ ] `TryLoginSubaccountAsync` sau khi login: click "Tài khoản của tôi" → "Kênh Người bán" → chờ banhang →
      **Pages[0] = banhang** rồi mới `return true`; mọi nhánh trượt log `title=…, url=…` + `return false`.
- [ ] KHÔNG đụng vòng lặp shop / ReadShopList / OpenShopDetail.

## 5. Rủi ro & lưu ý

- "Kênh Người bán" có thể mở TAB MỚI hoặc CÙNG tab → phải xử cả hai (đúng như code cũ d4e6916 đã làm).
- Sau chuẩn hóa, Pages[0] có thể là banhang seller-centre landing (KHÔNG hẳn `/portal/shop`) — không sao,
  vòng lặp shop tự Goto `/portal/shop` trên Pages[0] (đã authenticated).
- Giữ graceful: cầu SSO fail (không thấy nút / chờ 90s không thấy banhang) → `return false`, RunAsync giữ
  cửa sổ cho người dùng thao tác tay (không treo).
- Đây là bug chặn cả luồng — sau khi sửa cần chạy thật lại (Fable sẽ publish + cài đè cho user test).

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
