# Plan: Khôi phục cầu SSO "Tài khoản của tôi → Kênh Người bán" trước vòng lặp shop

- **Ngày:** 2026-07-23
- **Trạng thái:** hoàn thành (911 test; build sạch)
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

**Ngày:** 2026-07-23 · **Người thực thi:** Opus

### File đã sửa

- `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs` — 6 chỗ:
  1. **Thêm local static `UrlIsBanhang`** ở đầu `TryLoginSubaccountAsync` (cạnh `DiagAsync`) — hàm này
     KHÔNG có sẵn trong method hiện tại (xem điểm lệch dưới), lấy nguyên văn từ `d4e6916`. Là **local
     function**, không phải field mới.
  2. **Chèn cầu SSO (Bước 6 phần click → 7 → 8)** vào cuối `TryLoginSubaccountAsync`, ngay sau đoạn đóng
     tab mail (đã có) và THAY cho `return true` cũ. Nguyên văn từ `d4e6916`:
     - Bước 6: `FindVisibleByTextAsync(...MyAccountNavRegex...10000)` → null thì log `title/url` + `return
       false`; thấy thì `TryHumanClickVisibleAsync` (click "Tài khoản của tôi") + delay 1.5–3s.
     - Bước 7: `FindVisibleByTextAsync(...SellerChannelRegex...)` → null thì log + `return false`; hứng tab
       mới qua `_context.Page +=`, click "Kênh Người bán", vòng 90s chờ `UrlIsBanhang` (cùng-tab qua
       `page.Url`, hoặc tab-mới qua `popped`/quét `_context.Pages`). Không thấy → log các tab + `return
       false`.
     - Bước 8: tab-mới → `WaitForLoadStateAsync(DOMContentLoaded,15s)` best-effort + đóng tab subaccount
       (retry ≤3) để `Pages[0]=banhang` (cảnh báo nếu không đóng được / không về Pages[0]). Cuối cùng
       `L("Đã vào Kênh Người bán."); return true;`.
     - GIỮ lại dòng `L("Đã đăng nhập Nền tảng tài khoản phụ.")` làm mốc tiến trình (plan cho phép giữ cả 2
       dòng log).
  3. Sửa **comment cuối method** (stale: "KHÔNG đóng tab subaccount ở finally — giữ tab gốc") → phản ánh
     Bước 8 đóng tab subaccount CÓ CHỦ ĐÍCH (nguyên văn `d4e6916`).
  4. **Doc interface `TryLoginSubaccountAsync`** (`ILoginSession`): bỏ "KHÔNG còn click ... mở thẳng
     /portal/shop" → mô tả bắc cầu SSO sang Seller Centre (lập cookie seller) + chuẩn hóa `Pages[0]=banhang`;
     `true` nghĩa là đã bắc cầu SSO (Pages[0] là banhang.shopee.vn). Impl KHÔNG có doc comment nên không sửa.
  5. **Comment field `SellerChannelRegex`**: bỏ "KHÔNG DÙNG trong luồng mới ... — GIỮ để test cũ còn xanh"
     → "Dùng ở bước bắc cầu SSO cuối TryLoginSubaccountAsync".
  6. **Doc forwarder `MatchesSellerChannelEntry`** (cấp `ShopeeLoginService`): bỏ "KHÔNG DÙNG trong luồng
     mới ... — GIỮ để test cũ còn xanh" → "dùng ở bước bắc cầu SSO cuối TryLoginSubaccountAsync".

**KHÔNG đụng:** `RunAsync`/vòng lặp shop, `ReadShopListAsync`, `OpenShopDetailAsync`, `CloseShopTabAsync`,
`WorkPage()`, các catch-block (giữ nguyên thông điệp lỗi cũ), module/suite khác. Không thêm field mới.

### Kết quả build / test

| Bước | Kết quả |
|---|---|
| `dotnet build XuLyDonShopee.Core` | Build succeeded — 0 Warning, 0 Error |
| `dotnet build XuLyDonShopee.App` | Build succeeded — 0 Warning, 0 Error |
| `dotnet build XuLyDonShopee.Tests` | Build succeeded — 0 Warning, 0 Error |
| `dotnet build suite/Shopee.Suite` | Build succeeded — 0 Warning, 0 Error |
| `dotnet test XuLyDonShopee.Tests` | Passed! Failed: 0, Passed: **911**, Skipped: 0 |

### Điểm lệch so với plan

- **`UrlIsBanhang` KHÔNG "đã có sẵn"** như plan/note ghi. Grep toàn file xác nhận nó không tồn tại ở đâu
  trong `ShopeeLoginService.cs` hiện tại — trong `d4e6916` nó là **local static function** ở đầu
  `TryEnterSellerViaSubaccountAsync`. Đã khôi phục lại đúng dạng local static function (nguyên văn) ở đầu
  `TryLoginSubaccountAsync`, KHÔNG thêm field. Đây là phần bắt buộc để code Bước 7/8 biên dịch được, và
  đúng tinh thần "lấy nguyên văn từ d4e6916".
- Không dùng `<see cref="RunAsync"/>` trong doc (RunAsync ở lớp `AccountSession` khác, cref có thể không
  resolve → cảnh báo CS1574); viết "caller (RunAsync)" dạng text thường để giữ **0 warning**.
- Ngoài ra không có điểm lệch. Chưa commit (chờ phiên chính nghiệm thu + publish).
