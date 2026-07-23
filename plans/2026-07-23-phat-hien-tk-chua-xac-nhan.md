# Plan: Phát hiện tài khoản KHÔNG tự xác minh được + nhãn đỏ + danh sách "TK chưa xác nhận"

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Khi Chạy tự động, có tài khoản KHÔNG bấm được xác nhận (verify email/KMSI) — phiên kẹt ở trang verify của Shopee nhưng app vẫn chạy tiếp flow (Xử lý/Sync no-op), log không có gì bất thường, không ai biết. Người dùng chốt:

1. **Phát hiện:** cuối lượt chạy tự động của mỗi lô, TRƯỚC khi đóng phiên do autorun mở — kiểm tra trang hiện tại của từng phiên; nếu vẫn nằm ở trang verify/login/captcha (chưa đăng nhập được) → xác định "TK chưa xác nhận được".
2. **Nhãn đỏ** trên dòng tài khoản: "TK chưa xác nhận".
3. **Danh sách lọc** ở màn Tài khoản: nút "Những TK chưa xác nhận (N)" → hiện list tài khoản lỗi; mỗi tài khoản có nút **"Truy cập TK"** → (đã chốt AskUserQuestion) **nhảy tới chi tiết tài khoản đó trong màn Tài khoản VÀ tự mở phiên trình duyệt** để người dùng xác minh tay trên cửa sổ Brave.

Hạ tầng sẵn có (đã khảo sát):
- `ILoginSession.DetectPageStateAsync` (`ShopeeLoginService.cs:925`) trả `ShopeePageState.{LoggedIn, LoginForm, Verify, Captcha, Unknown}` — graceful, không ném.
- `AutoRunService.RunBatchAsync` (`orders/XuLyDonShopee.App/Services/AutoRunService.cs:210-225`): sau `Task.WhenAll` các account → vòng `foreach` `Sessions.Stop(id)` cho phiên do autorun mở (`_openedByMe`) — ĐÂY là điểm cắm kiểm tra.
- Màn Tài khoản: `AccountsViewModel`/`AccountRowViewModel`/`AccountsView.axaml` (list dòng acc + trạng thái/log per-acc); lệnh mở phiên sẵn có (khảo sát tên chính xác — RunOrAutoStart/Open… trong `AccountsViewModel`).

## 2. Phạm vi

- **Làm:** cờ bền `verify_failed_at` trên tài khoản + đánh dấu cuối lô autorun + tự xóa cờ khi đăng nhập OK + nhãn đỏ + bộ lọc "TK chưa xác nhận" + nút "Truy cập TK".
- **Không làm:**
  - KHÔNG đổi luồng tự-xác-minh email hiện có (chỉ THÊM phát hiện sau cùng; không sửa selector verify).
  - KHÔNG tự động thử lại xác minh — việc sửa là của người dùng (mở phiên xác minh tay).
  - KHÔNG đụng hub/GSheet/vòng đời đơn.

## 3. Các bước thực hiện

### DB + model

1. `orders/XuLyDonShopee.Core/Data/Database.cs`: migration cột `verify_failed_at TEXT` (NULL = bình thường) vào bảng `accounts` (pattern EnsureColumn + test migration như các cột trước).
2. `Account` model (`orders/XuLyDonShopee.Core/Models/Account.cs`) + repo accounts (`_services.Accounts` — khảo sát class): đọc/ghi cột mới; thêm `MarkVerifyFailed(long id, DateTime at)` và `ClearVerifyFailed(long id)` (UPDATE gọn, khóa theo id).

### Phát hiện cuối lô autorun

3. `AccountSession` (+ `IAccountSession`): thêm `Task<ShopeePageState?> ProbePageStateAsync()` — trả trạng thái trang hiện tại của phiên ĐANG chạy qua `_session.DetectPageStateAsync` (read-only, không chuột). Graceful: phiên null/chưa Running/lỗi → null. KHÔNG chiếm `_navigating` (detect chỉ evaluate JS đọc); nhưng nếu `_navigating` đang bật thì trả null (đang giữa thao tác — không kết luận).
4. `AutoRunService.RunBatchAsync`: TRƯỚC vòng `Sessions.Stop(id)` cho từng phiên do mình mở:
   - Gọi `ProbePageStateAsync` (qua manager — thêm đường lấy `IAccountSession` theo id nếu chưa có).
   - Kết quả `Verify`/`LoginForm`/`Captcha` → `MarkVerifyFailed(id, UtcNow)` + log per-acc: `"KHÔNG tự xác minh được — phiên vẫn ở trang {state} khi kết thúc lượt. Đánh dấu: TK chưa xác nhận."` + log LogSource autorun 1 dòng tổng: `"Lô này có {n} TK chưa xác nhận: {emails}"`.
   - `LoggedIn` → `ClearVerifyFailed(id)` (phiên tốt thì gỡ nhãn cũ nếu có).
   - `Unknown`/null → KHÔNG đổi cờ (không kết luận bừa).
   - Toàn khối best-effort try/catch: lỗi probe KHÔNG chặn việc đóng phiên.
5. **Tự xóa cờ khi đăng nhập OK ngoài autorun:** trong `AccountSession.RunAsync`, chỗ phiên đạt đăng nhập thành công lần đầu (đọc được số "Chờ Lấy Hàng" đầu tiên / state LoggedIn sau login — khảo sát chọn đúng 1 điểm đã có sẵn) → nếu account đang có `verify_failed_at` → `ClearVerifyFailed` + log "Đã xác minh được — gỡ nhãn TK chưa xác nhận." (Để nhãn tự lành khi người dùng xác minh tay xong.)

### UI màn Tài khoản

6. `AccountRowViewModel`: property `IsVerifyFailed` (từ `Account.VerifyFailedAt`); refresh khi danh sách reload / cờ đổi (dùng cơ chế Changed/reload sẵn có của màn).
7. `AccountsView.axaml`: trên dòng tài khoản thêm **badge đỏ "TK chưa xác nhận"** (style theo badge/pill sẵn có của màn; đỏ nền nhạt chữ đậm, không phá layout dòng).
8. Bộ lọc: `AccountsViewModel` thêm `UnverifiedCount` + toggle `ShowOnlyUnverified`; `AccountsView.axaml` thêm nút/link **"Những TK chưa xác nhận (N)"** ở khu đầu danh sách (chỉ hiện khi N > 0; đang lọc → nút thành "Hiện tất cả"). Lọc client-side trên list rows.
9. Nút **"Truy cập TK"** trên dòng (hiện khi `IsVerifyFailed`, hoặc chỉ trong chế độ lọc — chọn cách gọn theo layout):
   - Chọn/scroll tới đúng tài khoản trong màn (hiện chi tiết + nhật ký của acc — dùng cơ chế select sẵn có).
   - Tự mở phiên trình duyệt bằng ĐÚNG lệnh mở phiên sẵn có của màn Tài khoản (RunOrAutoStart/Open… — không viết đường mở mới), để cửa sổ Brave hiện trang verify cho người dùng bấm tay.
   - Phiên đang chạy sẵn → chỉ select + báo "Phiên đang mở — xác minh trên cửa sổ Brave của tài khoản này."

### Test + build

10. Test: migration cột `verify_failed_at`; repo Mark/Clear; logic phân loại state→failed (hàm pure nhỏ nếu tách được, vd `static bool LaTrangThaiChuaXacNhan(ShopeePageState s)` → Verify/LoginForm/Captcha true, LoggedIn/Unknown false). KHÔNG test UI.
11. `dotnet build ShopeeSuite.sln` 0 lỗi; `dotnet test orders/XuLyDonShopee.Tests` toàn bộ xanh (888 hiện có + mới).

## 4. Tiêu chí nghiệm thu

- [ ] Build + toàn bộ test xanh.
- [ ] Cuối mỗi lô autorun, phiên do autorun mở còn ở Verify/LoginForm/Captcha → acc bị đánh dấu `verify_failed_at` + log rõ; LoggedIn → cờ được gỡ; Unknown → giữ nguyên. Việc probe lỗi KHÔNG chặn đóng phiên.
- [ ] Dòng tài khoản có nhãn đỏ "TK chưa xác nhận" khi cờ bật; gỡ tự động khi phiên đăng nhập OK (kể cả xác minh tay xong rồi app đọc được số đơn).
- [ ] Màn Tài khoản có nút "Những TK chưa xác nhận (N)" (ẩn khi N=0) → lọc đúng; "Truy cập TK" → select acc + mở phiên (hoặc chỉ select nếu phiên đang chạy).
- [ ] Luồng verify-email/login/autorun hiện có không đổi hành vi (chỉ thêm probe + cờ).

## 5. Rủi ro & lưu ý

- **Probe không chiếm `_navigating`:** DetectPageStateAsync chỉ evaluate đọc; nhưng nếu phiên đang navigating thì trả null (không kết luận) — tránh đánh dấu oan khi phiên đang giữa thao tác.
- **Không đánh dấu oan `Unknown`:** trang trắng/đang load → Unknown → bỏ qua, đừng gán lỗi.
- **Cờ bền qua restart** (nằm trong DB accounts) — người dùng mở app hôm sau vẫn thấy danh sách TK lỗi.
- Nút "Truy cập TK" tái dùng lệnh mở phiên sẵn có — KHÔNG chế đường mở phiên mới.

---

## Báo cáo thực thi

**Người thực thi:** Opus · **Ngày:** 2026-07-23 · **Kết quả:** build + 909 test xanh.

### Đã làm theo từng hạng mục plan

**DB + model**
- `orders/XuLyDonShopee.Core/Data/Database.cs`: thêm cột `verify_failed_at TEXT` vào `CREATE TABLE accounts` (DB mới) + `EnsureColumn(conn, "accounts", "verify_failed_at", "TEXT")` (migration DB cũ, idempotent).
- `orders/XuLyDonShopee.Core/Models/Account.cs`: thêm `public DateTime? VerifyFailedAt { get; set; }` (null = bình thường).
- `orders/XuLyDonShopee.Core/Data/AccountRepository.cs`:
  - Thêm `verify_failed_at` vào SELECT của `GetAll`/`GetById` + Map (index 13, IsDBNull → null).
  - `MarkVerifyFailed(long id, DateTime at)`: `UPDATE ... SET verify_failed_at=$at WHERE Id=$id`.
  - `ClearVerifyFailed(long id)`: `UPDATE ... SET verify_failed_at=NULL WHERE Id=$id AND verify_failed_at IS NOT NULL` → trả số dòng thực đổi (>0 = vừa gỡ cờ đang bật).
  - **Insert/Update KHÔNG đụng cột này** (form CRUD sửa tài khoản không clobber cờ) — có test kiểm chứng.

**Phát hiện cuối lô autorun**
- `orders/XuLyDonShopee.App/Services/IAccountSession.cs`: thêm `Task<ShopeePageState?> ProbePageStateAsync()` (+ `using XuLyDonShopee.Core.Services`).
- `orders/XuLyDonShopee.App/Services/AccountSession.cs`:
  - `ProbePageStateAsync()`: read-only qua `_session.DetectPageStateAsync`; phiên null/chưa Running/`_navigating` đang bật → trả null; KHÔNG chiếm `_navigating`; mọi lỗi → null.
  - `TryClearVerifyFailedAfterLogin()` gọi tại điểm **đọc số "Chờ Lấy Hàng" lần đầu** trong `RunAsync` (`if (firstOrderCheck)`), chỉ log + `RaiseAccountsChanged` khi thực sự gỡ được cờ.
- `orders/XuLyDonShopee.App/Services/AutoRunService.cs`:
  - `RunBatchAsync`: TRƯỚC vòng `Sessions.Stop(id)` gọi `ProbeUnverifiedBeforeCloseAsync(mine)` (bọc try/catch bao trùm — lỗi KHÔNG chặn đóng phiên).
  - `ProbeUnverifiedBeforeCloseAsync`: per-acc try/catch; Verify/LoginForm/Captcha → `MarkVerifyFailed` + log per-acc `"KHÔNG tự xác minh được — phiên vẫn ở trang {…} khi kết thúc lượt. Đánh dấu: TK chưa xác nhận."`; LoggedIn → `ClearVerifyFailed`; Unknown/null → không đổi. Cuối lô: log tổng `"Lô này có {n} TK chưa xác nhận: {emails}"` + `RaiseAccountsChanged` một lần khi có thay đổi.
  - `public static bool LaTrangThaiChuaXacNhan(ShopeePageState)` (hàm pure) + `MoTaTrang` (mô tả trang cho log).

**UI màn Tài khoản**
- `orders/XuLyDonShopee.App/ViewModels/AccountRowViewModel.cs`: `public bool IsVerifyFailed => Account.VerifyFailedAt is not null` (row dựng lại mỗi lần reload nên không cần notify riêng).
- `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`: `ShowOnlyUnverified` (ObservableProperty) + `UnverifiedCount` + `IsUnverifiedFilterVisible` + `UnverifiedButtonText` + lệnh `ToggleShowUnverified`; `ApplyFilter` lọc thêm theo cờ; `Reload` notify lại các property đếm; phương thức `TruyCapTk(row)` = chọn acc (đổ chi tiết + log) + tự mở phiên qua `Sessions.Start` (idempotent), phiên đang chạy thì chỉ chọn + báo.
- `orders/XuLyDonShopee.App/Views/AccountsView.axaml`: style `visitTk` + `unverifiedFilter`; nút lọc "Những TK chưa xác nhận (N)"/"Hiện tất cả" ở đầu danh sách (ẩn khi N=0 và không đang lọc); badge đỏ "TK chưa xác nhận" + nút "Truy cập TK" trong template dòng (hiện khi `IsVerifyFailed`).
- `orders/XuLyDonShopee.App/Views/AccountsView.axaml.cs`: handler `OnTruyCapTkClick` (gọi `vm.TruyCapTk(row)`, `e.Handled=true` để không toggle tick) + `using Avalonia.Interactivity`.

**Test**
- `DatabaseMigrationTests.cs`: migration `verify_failed_at` (thêm cột, không mất dữ liệu, mặc định null) + Mark sau migration; bổ sung assert cột vào test idempotent.
- `AccountRepositoryTests.cs`: 6 test Mark/Clear (ghi/đọc, không đụng cột khác, Clear trả 1/0 đúng, Update không clobber cờ, Insert mặc định null).
- `AutoRunUnverifiedTests.cs` (mới): 5 case cho `LaTrangThaiChuaXacNhan`.
- Cập nhật 2 stub `IAccountSession` (AccountSessionManagerTests, AccountRowViewModelTests) thêm `ProbePageStateAsync`.

### Kết quả kiểm chứng (lệnh đã chạy)
- `dotnet build orders/XuLyDonShopee.App/XuLyDonShopee.App.csproj` → **Build succeeded, 0 Warning, 0 Error**.
- `dotnet build ShopeeSuite.sln` → **Build succeeded, 0 Warning, 0 Error**.
- `dotnet test orders/XuLyDonShopee.Tests` → **Passed! Failed 0, Passed 909, Skipped 0** (896 cũ + 13 mới).

### Quyết định tự chọn (ngoài chi tiết plan)
- **Điểm gỡ cờ trong RunAsync**: chọn điểm "đọc số Chờ Lấy Hàng LẦN ĐẦU" (`firstOrderCheck`) — đây là tín hiệu đăng-nhập-OK chắc chắn nhất; KHÔNG dùng `_readyForActions=true` vì cờ đó vẫn bật ở nhánh degrade (verify/captcha thất bại) nên sẽ gỡ oan.
- **ClearVerifyFailed trả `int`** (số dòng thực đổi, điều kiện `IS NOT NULL`) để caller chỉ log/`RaiseAccountsChanged` khi thật sự có cờ được gỡ → tránh làm mới UI thừa mỗi lần mở phiên.
- **Nút "Truy cập TK" hiện khi `IsVerifyFailed`** (không phụ thuộc chế độ lọc) — bám theo phương án chính của plan, chỉ dùng binding cấp-row (không cần cross-context binding với ShowOnlyUnverified). Đánh đổi: dòng TK-lỗi hơi chật ngang (email bị cắt bớt do badge+nút chiếm cột phải), chấp nhận vì trạng thái này ngoại lệ/tạm thời và ít tài khoản lỗi cùng lúc.
- **Nút lọc hiển thị khi `UnverifiedCount>0 || ShowOnlyUnverified`** (rộng hơn "chỉ khi N>0" của plan) để khi đang lọc mà số về 0 vẫn còn nút "Hiện tất cả" thoát ra, tránh kẹt ở danh sách rỗng.
- **RaiseAccountsChanged để cập nhật nhãn/bộ lọc**: tái dùng cơ chế sẵn có (`AccountsChanged → OnAccountsChanged → RunOnUi(Reload)`), phát 1 lần/lô khi có thay đổi cờ.

### Vướng mắc / chưa kiểm chứng bằng tay
- Luồng probe cuối lô + mở phiên "Truy cập TK" là code đụng Brave/Playwright thật nên KHÔNG chạy end-to-end trong môi trường này (đúng như quy ước dự án: luồng browser kiểm ở smoke test tay). Đã phủ bằng hàm pure + repo + migration test. Cần smoke tay: (1) chạy tự động với 1 acc verify-fail → cuối lô có badge đỏ + log; (2) verify tay xong → lượt sau nhãn tự gỡ; (3) nút "Truy cập TK" mở đúng cửa sổ Brave.
- Chưa release client (Velopack) — theo plan không yêu cầu; để Fable quyết định.
