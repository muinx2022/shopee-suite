# Plan: Thêm field "Mật khẩu email" vào tài khoản BigSeller (model + sync + UI hub)

- **Ngày:** 2026-07-24
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Người dùng muốn: ở **phần hub**, thêm **1 field mật khẩu email** vào tài khoản BigSeller. Mục đích cuối cùng (làm ở Plan B kế tiếp): khi đăng nhập BigSeller trên hub mà BigSeller đòi mã xác thực (OTP 6 số gửi về email tài khoản), hệ thống tự mở Hotmail/Outlook đọc mã thay vì chờ admin gõ tay.

Plan NÀY chỉ làm phần **nền tảng dữ liệu + UI**: thêm field, cho nó đồng bộ đúng qua Hub, và cho admin nhập được trên web. Logic đọc email để ở Plan B.

**Quyết định đã chốt với người dùng:**
- Địa chỉ email để đăng nhập Hotmail = dùng CHÍNH field `Email` đã có của tài khoản BigSeller (BigSeller gửi mã về email đăng nhập). → Chỉ cần thêm **1 field**: mật khẩu của hòm mail đó. Đặt tên `EmailPassword`.
- Field lưu **plain text**, giống `Password` hiện tại (người dùng chấp nhận không mã hoá).

**Hiện trạng đã khảo sát (đường dẫn tương đối từ gốc repo):**
- Model: `suite/Shopee.Core/BigSeller/BigSellerAccount.cs` — đã có `Email` (dòng 12), `Password` (dòng 15). CHƯA có field mật khẩu email.
- Cơ chế đồng bộ Hub↔client dựa trên **SharedSignature** (chuỗi các field DÙNG CHUNG). Field mới muốn lan toàn fleet phải thêm vào **cả 3 chỗ** dưới đây, nếu không sẽ dính lỗi kinh điển "field chung không đồng bộ" (đã lặp nhiều lần trong lịch sử repo):
  1. `SharedSignature(...)` trong `suite/Shopee.Core/Infrastructure/BackupService.cs` (dòng 164-172) — thêm field vào object serialize.
  2. Nhánh cập nhật acc đã tồn tại trong `MergeBigSeller` cùng file (dòng 126-128) — gán `existing.EmailPassword = a.EmailPassword;`.
  3. Phía Hub `server/Shopee.Hub.Web/Services/FileStoreConfigService.cs`:
     - `FreshAccountFromClient` (dòng 213-220) — thêm `EmailPassword = a.EmailPassword,`.
     - `UpdateSharedAccountFields` (dòng 234-248) — thêm so-sánh-rồi-gán như các field khác.
- UI hub: `server/Shopee.Hub.Web/Components/Shared/AccountConfigPanel.razor` — hàng input dòng 14-20 (`Nhãn / Email / Mật khẩu / KiotProxy key / Vùng`). Nút Lưu (dòng 79) gọi `Save()` (trong `@code`) → `ConfigSave.Apply(...)` ghi `config/bigseller.json`.

## 2. Phạm vi

- **Làm:**
  - Thêm property `EmailPassword` vào `BigSellerAccount`.
  - Đưa `EmailPassword` vào cơ chế đồng bộ (SharedSignature + MergeBigSeller + FileStoreConfigService — 3 chỗ nêu trên) để nó là field DÙNG CHUNG, lan từ Hub xuống mọi client.
  - Thêm 1 ô input "Mật khẩu email" vào form cấu hình acc BigSeller trên hub (`AccountConfigPanel.razor`), bind `@bind="_acct.EmailPassword"`.
- **Không làm:**
  - KHÔNG viết logic đọc email / tự mở Hotmail (đó là Plan B).
  - KHÔNG sửa `BigSellerLoginService` (Plan B).
  - KHÔNG thêm field ở UI client desktop (chỉ hub — theo yêu cầu). Model chung tự sync nên client vẫn nhận được giá trị, chỉ là chưa có ô nhập ở client — chấp nhận.
  - KHÔNG mã hoá field.

## 3. Các bước thực hiện

1. **`suite/Shopee.Core/BigSeller/BigSellerAccount.cs`** — thêm property mới ngay sau `Password` (sau dòng 15):
   ```csharp
   /// <summary>Mật khẩu HÒM MAIL (Hotmail/Outlook) của địa chỉ <see cref="Email"/> — để hub TỰ đăng nhập email
   /// đọc mã xác thực 6 số khi BigSeller đòi OTP (thiết bị mới). Plain text như Password. Field DÙNG CHUNG:
   /// sync qua Hub như Email/Password. Trống = không tự đọc mã, phải nhập tay.</summary>
   public string EmailPassword { get; set; } = "";
   ```
   Đặt kề `Password` cho dễ đọc. JSON cũ thiếu field → default "" (không vỡ bản cũ).

2. **`suite/Shopee.Core/Infrastructure/BackupService.cs`**:
   - `SharedSignature` (dòng 164-172): thêm `a.EmailPassword` vào danh sách field của object ẩn danh, ví dụ đổi dòng 166 thành:
     ```csharp
     a.Label, a.Email, a.Password, a.EmailPassword, a.KiotProxyKey, a.Region, a.ProxyType, a.DataSource,
     ```
   - `MergeBigSeller`, nhánh cập nhật acc đã tồn tại (khối dòng 126-128, ngay chỗ gán `existing.Password = a.Password;`): thêm
     ```csharp
     existing.EmailPassword = a.EmailPassword;
     ```

3. **`server/Shopee.Hub.Web/Services/FileStoreConfigService.cs`**:
   - `FreshAccountFromClient` (dòng 213-220): thêm `EmailPassword = a.EmailPassword,` (đặt cạnh `Password = a.Password,` ở dòng 216).
   - `UpdateSharedAccountFields` (dòng 234-248): thêm ngay sau khối so-sánh Password (dòng 239):
     ```csharp
     if (existing.EmailPassword != incoming.EmailPassword) { existing.EmailPassword = incoming.EmailPassword; changed = true; }
     ```

4. **`server/Shopee.Hub.Web/Components/Shared/AccountConfigPanel.razor`** — thêm 1 ô input vào hàng dòng 14-20. Đặt ngay sau ô "Mật khẩu" (dòng 17):
   ```razor
   <label>Mật khẩu email <input @bind="_acct.EmailPassword" /></label>
   ```
   Giữ nguyên kiểu `<input>` thường (đồng bộ với ô Mật khẩu hiện tại — plain text, không `type=password`).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` cả 2 project bị ảnh hưởng đều XANH:
  - `dotnet build suite/Shopee.Core/Shopee.Core.csproj`
  - `dotnet build server/Shopee.Hub.Web/Shopee.Hub.Web.csproj`
  (sửa nhiều project → có thể build cả solution: `dotnet build`).
- [ ] `BigSellerAccount` có property `EmailPassword` (string, default "").
- [ ] `SharedSignature` chứa `EmailPassword`; `MergeBigSeller` (nhánh update) gán `existing.EmailPassword`; `FreshAccountFromClient` + `UpdateSharedAccountFields` đều xử lý `EmailPassword`. (Kiểm bằng đọc lại diff — đủ 4 vị trí: signature, merge, fresh, update.)
- [ ] `AccountConfigPanel.razor` có ô "Mật khẩu email" bind `_acct.EmailPassword`, nằm trong hàng input cùng các field khác.
- [ ] Không phá vỡ serialize: field mới nằm trong `List<BigSellerAccount>` được ghi ra `config/bigseller.json` (PascalCase) — mặc nhiên đạt vì chỉ thêm auto-property.

## 5. Rủi ro & lưu ý

- **Bất biến quan trọng:** `EmailPassword` là field DÙNG CHUNG (giống Email/Password) → PHẢI có trong SharedSignature. KHÔNG được để lọt kiểu "chỉ thêm vào model + UI mà quên sync" — sẽ thành lỗi field-chung-không-đồng-bộ. Ngược lại KHÔNG đưa vào các field riêng-máy (CookieFile/WorkbookPath/RunConfig).
- Không cần đụng endpoint `/bigseller/upsert` (nó dùng chung `UpdateSharedAccountFields`/`FreshAccountFromClient` đã sửa).
- Đây là bước nền cho Plan B; sau khi build xanh + nghiệm thu, Plan B sẽ dùng `_acct.EmailPassword` trong luồng login.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
