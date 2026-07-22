# Plan: Outlook login — xử lý form mới "Xác minh email của bạn" + check hộp thư Ưu tiên trước

- **Ngày:** 2026-07-22
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Module Đơn hàng (`orders/`) có luồng verify email tự động: khi Shopee bắt xác minh, app mở tab mới đăng nhập Hotmail/Outlook rồi đọc mail xác nhận (`ShopeeLoginService.TryVerifyByEmailAsync` → `LoginHotmailAsync` → `OpenShopeeMailAndConfirmAsync` trong `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`).

**Vấn đề 1 — Microsoft đổi UI đăng nhập.** Sau khi nhập username, Microsoft KHÔNG còn hiện màn passwordless có link "Sử dụng mật khẩu của bạn" nữa, mà hiện form mới (Fluent UI, class `fui-*`) tiêu đề **"Xác minh email của bạn"** — đòi gửi mã đến email khôi phục (ô nhập `#proof-confirmation-email-input`, nút submit **"Gửi mã"** là `button[type='submit'][data-testid='primaryButton']`). Footer form có 2 link dạng `span[role='button']` class `fui-Link` (nằm trong `span[data-testid='viewFooter']`):
- "Bạn đã nhận được mã?"
- **"Các cách khác để đăng nhập"** ← cần click link này.

Người dùng xác nhận luồng đúng: click "Các cách khác để đăng nhập" → ra trang/màn mới liệt kê các cách đăng nhập → click lựa chọn **mật khẩu** (text chứa "mật khẩu"/"password") → ô mật khẩu hiện ra → nhập pass và check mail như cũ.

Code hiện tại (bước 2 trong `LoginHotmailAsync`, ~dòng 1029–1041) chỉ tìm link "Sử dụng mật khẩu" theo `UsePasswordRegex` — trên form mới không có text nào khớp → `usePwd` null → `passField` null → bước 3 (nhập pass) bị skip. **Nguy hiểm hơn:** bước 4 (KMSI) dùng `MsKmsiYesSelectors = { "#acceptButton", "#idSIButton9", "button[type='submit']" }` — trên form mới, `button[type='submit']` chính là nút **"Gửi mã"** → code click nhầm, gửi mã đến email khôi phục (hành vi sai hoàn toàn).

**Vấn đề 2 — thứ tự check hộp thư.** `OpenShopeeMailAndConfirmAsync` (~dòng 1092–1106) hiện click pivot **"Khác"/Other trước**, không thấy mail Shopee mới thử "Ưu tiên"/Focused. Người dùng yêu cầu **đảo lại: check "Ưu tiên"/Focused TRƯỚC, rồi mới check "Khác"/Other**.

## 2. Phạm vi

- **Làm:** sửa duy nhất file `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs` (3 hạng mục A/B/C dưới đây); build + chạy test project `orders/XuLyDonShopee.Tests`.
- **Không làm:** không đổi luồng Shopee seller (bước 1 click "xác minh qua email"), không đổi logic đọc mail/click link "TẠI ĐÂY", không đổi API/interface public, không sửa file nào khác, không sờ vào `suite/` hay `server/`.

## 3. Các bước thực hiện

Tất cả trong `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`.

### A. Xử lý form "Xác minh email của bạn" trong `LoginHotmailAsync` (bước 2)

1. Thêm 2 hằng mới cạnh nhóm selector/regex Microsoft hiện có (~dòng 752–787):
   - ```csharp
     // Link "Các cách khác để đăng nhập" trên form mới "Xác minh email của bạn" (Fluent UI):
     // span[role='button'] class fui-Link trong span[data-testid='viewFooter'].
     private static readonly string[] MsOtherWaysSelectors =
         { "span[role='button']", "[role='button']", "a", "button" };
     private static readonly Regex OtherWaysRegex =
         new(@"cách khác để đăng nhập|cach khac de dang nhap|other ways to sign in", RegexOptions.IgnoreCase | RegexOptions.Compiled);
     ```
2. Trong bước 2 của `LoginHotmailAsync` (khối `if (passField is null)` ~dòng 1031): sau nhánh tìm `usePwd` hiện có, thêm nhánh mới **khi `usePwd` null**:
   - Tìm link "Các cách khác" bằng `FindVisibleByTextAsync(mailPage, MsOtherWaysSelectors, OtherWaysRegex, ct, 4000)`.
   - Nếu thấy: log `"Form 'Xác minh email' mới của Microsoft — bấm 'Các cách khác để đăng nhập'..."`, click bằng `TryHumanClickVisibleAsync`, delay ngẫu nhiên 1200–2500ms.
   - Rồi tìm lựa chọn mật khẩu trên màn danh sách cách đăng nhập: `FindVisibleByTextAsync` với selectors `{ "button", "[role='button']", "[role='radio']", "[role='listitem']", "[role='link']", "div[data-testid]", "span" }` (clickable trước — thứ tự selector là thứ tự ưu tiên) + regex `UsePasswordRegex` sẵn có (nhánh `mật khẩu` trần sẽ khớp tile "Mật khẩu"), timeout 8000ms.
   - Nếu thấy: log `"Chọn phương thức 'Mật khẩu'..."`, click, delay 1000–2500ms. Không thấy: log rõ để chẩn đoán (kèm `mailPage.Url`), KHÔNG ném.
   - Giữ nguyên dòng chốt hiện có: `passField = await FindFirstVisibleByRectsAsync(mailPage, MsPasswordSelectors, 8000, ct)` — chạy sau cả nhánh cũ lẫn nhánh mới.
3. Cập nhật doc-comment của `LoginHotmailAsync` (~dòng 981–986) mô tả thêm nhánh form mới.

### B. Chống click nhầm "Gửi mã" ở bước 4 (KMSI)

1. Bỏ `"button[type='submit']"` khỏi `MsKmsiYesSelectors` (giữ `{ "#acceptButton", "#idSIButton9" }` — UI cũ dùng id vì nút là `input` có `value="Yes"`, không có innerText).
2. Thêm regex `KmsiYesRegex = ^\s*(yes|có|co)\s*$` (IgnoreCase|Compiled).
3. Bước 4 (~dòng 1062–1070) thành 2 nhịp: (a) tìm theo id `FindFirstVisibleByRectsAsync(mailPage, MsKmsiYesSelectors, 4000, ct)`; (b) nếu null → `FindVisibleByTextAsync(mailPage, new[] { "button[type='submit']", "button" }, KmsiYesRegex, ct, 2500)` (UI Fluent mới — nút "Có"). Chỉ click khi tìm thấy một trong hai. Mục đích: nút submit generic KHÔNG bao giờ được click khi không chắc là nút Yes/Có — trên form "Xác minh email" nó là nút "Gửi mã".
4. Thêm comment tại chỗ giải thích ràng buộc này (vì sao không dùng `button[type='submit']` trần).

### C. Đảo thứ tự pivot hộp thư trong `OpenShopeeMailAndConfirmAsync`

1. ~Dòng 1097–1106: click `FocusedPivotRegex` ("Ưu tiên") TRƯỚC, quét `FindAllShopeeMailRowsAsync`; nếu `rows.Count == 0` → click `OtherPivotRegex` ("Khác") rồi quét lại. (Hoán đổi 2 lệnh gọi `TryClickPivotAsync` + label log tương ứng.)
2. Cập nhật doc-comment của hàm (~dòng 1076) và comment inline (~dòng 1097) cho khớp thứ tự mới.

### D. Build + test

- `dotnet build` solution/các project trong `orders/` (Core + App + Tests).
- `dotnet test orders/XuLyDonShopee.Tests` — toàn bộ test phải xanh (hiện ~774 test).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` các project `orders/` không lỗi, không warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh 100%.
- [ ] Đọc diff: bước 2 có nhánh mới "Các cách khác để đăng nhập" → chọn "Mật khẩu", chỉ chạy khi không thấy ô password lẫn link "Sử dụng mật khẩu"; luồng cũ (còn link "Sử dụng mật khẩu" hoặc ô password hiện sẵn) giữ nguyên hành vi.
- [ ] `MsKmsiYesSelectors` không còn `button[type='submit']`; nút submit generic chỉ được click khi text khớp `^(yes|có|co)$`.
- [ ] `OpenShopeeMailAndConfirmAsync` check "Ưu tiên" trước, "Khác" sau; log label khớp.
- [ ] Không file nào khác bị sửa.

## 5. Rủi ro & lưu ý

- **Không có test tự động cho luồng browser** — nghiệm thu hành vi thật phải chạy tay với tài khoản thật (người dùng sẽ test). Vì vậy log từng bước phải rõ (đã có pattern `L(...)`) để soi khi chạy thật.
- Màn "các cách đăng nhập" sau khi click "Các cách khác" **chưa có DOM thật** — chỉ suy đoán tile có text chứa "mật khẩu"/"password". Nếu không tìm thấy, code phải log chẩn đoán (URL + không ném) rồi rơi về nhánh chờ ô password như cũ — thất bại mềm, caller giữ phiên cho verify tay.
- `FindVisibleByTextAsync` khớp InnerText cả phần tử cha — thứ tự selector đã ưu tiên phần tử clickable (`button`, `[role='button']`…) trước `div`/`span` to; giữ đúng thứ tự trong plan.
- KHÔNG log giá trị mật khẩu (quy tắc sẵn có của file).
- Form mới nằm cùng SPA (`routeAnimationFluent`) — sau click link KHÔNG có navigation; chỉ chờ delay + poll selector, không `WaitForNavigation`.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
