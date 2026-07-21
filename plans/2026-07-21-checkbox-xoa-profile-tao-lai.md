# Plan: Checkbox "Xóa profile và tạo lại" khi chạy sync (module Đơn hàng)

- **Ngày:** 2026-07-21
- **Trạng thái:** hoàn thành (2026-07-21 — Fable nghiệm thu trong worktree: build 0 lỗi 0 warning, 754/754 test, soi ProfileJanitor sanity-check đạt; lưu ý: đường ghép RunAsync chưa chạy end-to-end với Brave thật)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nơi làm:** worktree riêng của repo shopee-suite (nhánh `task/xoa-profile-tao-lai` tách từ
  `feature/gop-don-hang`). Đường dẫn trong plan tương đối từ gốc worktree. KHÔNG đọc/ghi cây
  làm việc chính (một agent khác đang làm phần hub ở đó) hay repo khác.

## 1. Bối cảnh & mục tiêu

Người dùng muốn: khi chạy sync cho các shop trong module Đơn hàng, có tùy chọn **checkbox
"Xóa profile và tạo lại"** — bật lên thì phiên trình duyệt mở mới sẽ XÓA thư mục hồ sơ hiện có
của (tài khoản × trình duyệt) rồi tạo lại sạch → đăng nhập lại từ đầu (cookie đã lưu trong DB
vẫn được luồng login sẵn có dùng như bình thường).

Bối cảnh code (sau việc "hồ sơ riêng theo trình duyệt", commit `ad49995`):
- Hồ sơ tính tại `orders/XuLyDonShopee.App/Services/AccountSession.cs` (~dòng 1079-1090):
  `browserChoice` → `browserKind = BrowserLocator.ResolveBrowserKind(...)` →
  `userDataDir = BrowserProfilePaths.ForAccount(baseDir, _accountId, browserKind)` →
  `Directory.CreateDirectory(userDataDir)` — tất cả TRƯỚC vòng relaunch.
- Settings key-value: `orders/XuLyDonShopee.Core/Data/SettingsRepository.cs` (mẫu getter/setter
  bool/string sẵn có).
- Checkbox UI đặt ở màn Tài khoản: `orders/XuLyDonShopee.App/Views/AccountsView.axaml` +
  `ViewModels/AccountsViewModel.cs` (nơi có các nút Sync/Sync trọn gói).

**Ngữ nghĩa chốt:**
- Cờ là **cài đặt toàn cục** của module (lưu SettingsRepository), mặc định TẮT.
- Khi cờ BẬT: mỗi **phiên mở mới** (bấm Mở/Sync/Sync trọn gói/AutoRun mở phiên) xóa
  `userDataDir` (đúng thư mục của trình duyệt đang dùng) rồi tạo lại. **Relaunch giữa phiên**
  (đổi proxy) KHÔNG xóa — chỉ xóa 1 lần lúc phiên bắt đầu.
- Xóa thất bại (thư mục bị khóa bởi Brave mồ côi…) → retry ngắn; vẫn thất bại → ghi log cảnh báo
  rồi TIẾP TỤC với hồ sơ cũ (không chặn sync).

## 2. Phạm vi

- **Làm:**
  - `orders/XuLyDonShopee.Core/Services/ProfileJanitor.cs` (MỚI): helper thuần
    `public static bool TryResetDirectory(string dir, Action<string>? log = null, int attempts = 3, int delayMs = 300)`
    — Directory.Exists → Delete(recursive) với retry/delay (IOException/UnauthorizedAccess),
    rồi CreateDirectory; trả bool thành công. KHÔNG phụ thuộc gì ngoài BCL (test được với thư mục tạm).
  - `orders/XuLyDonShopee.Core/Data/SettingsRepository.cs`: key mới `sync_fresh_profile`
    (bool, mặc định false) + `GetSyncFreshProfile()/SetSyncFreshProfile(bool)` theo mẫu sẵn có.
  - `orders/XuLyDonShopee.App/Services/AccountSession.cs`: sau khi tính `userDataDir`, nếu
    `_services.Settings.GetSyncFreshProfile()` → gọi `ProfileJanitor.TryResetDirectory(userDataDir, log)`
    (đặt TRƯỚC `Directory.CreateDirectory(userDataDir)` hiện có — giữ nguyên dòng CreateDirectory
    để nhánh cờ-tắt không đổi); log rõ "Đã xóa và tạo lại hồ sơ" / cảnh báo khi thất bại.
  - `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`: property
    `bool XoaProfileTaoLai` (đọc từ Settings lúc khởi tạo, setter lưu ngay qua
    `SetSyncFreshProfile`) — theo mẫu property-persist sẵn có trong codebase nếu có.
  - `orders/XuLyDonShopee.App/Views/AccountsView.axaml`: CheckBox "Xóa profile và tạo lại"
    đặt cạnh cụm nút Sync trên toolbar, kèm ToolTip giải thích ("Phiên mở mới sẽ xóa hồ sơ trình
    duyệt của tài khoản rồi tạo lại — phải đăng nhập lại"). Style hòa với toolbar hiện có.
  - Tests (`orders/XuLyDonShopee.Tests/`): `ProfileJanitorTests` (thư mục tạm: có file con →
    reset xong rỗng + tồn tại; thư mục chưa tồn tại → tạo mới, trả true; file đang mở khóa →
    trả false không ném — nếu mô phỏng được trên Windows bằng FileShare.None); SettingsRepository
    round-trip key mới; AccountsViewModel persist cờ.
- **Không làm:**
  - KHÔNG xóa hồ sơ ở relaunch giữa phiên; KHÔNG xóa hồ sơ của trình duyệt KHÁC (chỉ đúng
    `userDataDir` phiên này).
  - KHÔNG đụng `suite/`, `server/`, `shared/`, lớp proxy, `OrdersRepository`.
  - KHÔNG commit (Fable commit trong worktree rồi merge).

## 3. Kiểm chứng

- `dotnet build ShopeeSuite.sln -c Release` → 0 lỗi, 0 warning mới.
- `dotnet test orders/XuLyDonShopee.Tests/... -c Release` → 742 baseline + test mới pass.
  (Fail đồng loạt 0x800711C7 = WDAC → báo cáo.)

## 4. Tiêu chí nghiệm thu

- [ ] Build sạch; toàn bộ test pass (≥742).
- [ ] Cờ tắt (mặc định): hành vi y hệt cũ (không xóa gì).
- [ ] Cờ bật: phiên mở mới xóa + tạo lại đúng thư mục `profiles/<id>-<kind>`; relaunch không xóa.
- [ ] Xóa thất bại không làm phiên chết — log cảnh báo, chạy tiếp hồ sơ cũ.
- [ ] Checkbox hiển thị ở màn Tài khoản, trạng thái bền qua restart app (lưu settings).

## 5. Rủi ro & lưu ý

- Đây là thao tác PHÁ HỦY dữ liệu hồ sơ — tuyệt đối chỉ xóa `userDataDir` vừa tính (không cha,
  không anh em). Viết code phòng thủ: chỉ Delete khi path chứa segment `profiles` (sanity check
  trong ProfileJanitor).
- Thư mục có thể bị Brave mồ côi giữ khóa — đã có retry + degrade êm; KHÔNG kill process ở đây
  (việc dọn tiến trình là của SessionManager).
- Sau khi xóa hồ sơ, luồng login sẵn có tự lo (cookie DB/typing like-human); KHÔNG sửa gì ở
  ShopeeLoginService.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Trạng thái: HOÀN THÀNH** — build sạch (0 warning/0 error), 754/754 test pass (742 baseline + 12 test mới). Không commit (theo plan).

### Đã làm (từng hạng mục theo Phạm vi §2)

1. **`orders/XuLyDonShopee.Core/Services/ProfileJanitor.cs` (MỚI)** — helper thuần BCL:
   `public static bool TryResetDirectory(string dir, Action<string>? log = null, int attempts = 3, int delayMs = 300)`.
   - Sanity check phòng thủ: chỉ xóa khi `dir` có một **segment** tên đúng `profiles` (tách theo cả `\` và `/`,
     so khớp không phân biệt hoa/thường) — không thỏa hoặc rỗng → trả `false` NGAY, không đụng đĩa.
   - `Directory.Exists` → `Directory.Delete(recursive)` → `Directory.CreateDirectory`; retry `attempts` lần,
     nghỉ `delayMs` ms khi gặp `IOException`/`UnauthorizedAccessException`; hết lượt → log + trả `false`
     (KHÔNG ném).

2. **`orders/XuLyDonShopee.Core/Data/SettingsRepository.cs`** — thêm key + getter/setter:
   - Const `SyncFreshProfileKey = "sync_fresh_profile"`.
   - `GetSyncFreshProfile()`: nhận `"true"/"1"` (bất kể hoa/thường) → true; thiếu/rỗng/lạ → false.
   - `SetSyncFreshProfile(bool)`: lưu `"true"/"false"`.

3. **`orders/XuLyDonShopee.App/Services/AccountSession.cs`** — trong `RunAsync`, ngay sau khi tính
   `userDataDir` và **TRƯỚC** dòng `Directory.CreateDirectory(userDataDir)` hiện có (đặt trong scope bao cả
   vòng relaunch nên chỉ chạy 1 LẦN lúc phiên mở mới, relaunch KHÔNG xóa): nếu
   `_services.Settings.GetSyncFreshProfile()` → gọi `ProfileJanitor.TryResetDirectory(userDataDir, log)`;
   thành công log `"Đã xóa và tạo lại hồ sơ trình duyệt: …"`, thất bại log `"CẢNH BÁO: không xóa được hồ sơ
   trình duyệt — chạy tiếp với hồ sơ cũ (…)"` rồi chạy tiếp (không chặn sync). Dòng `CreateDirectory` cũ giữ
   nguyên → nhánh cờ-tắt không đổi hành vi.

4. **`orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`** — thêm property
   `[ObservableProperty] bool XoaProfileTaoLai`:
   - Nạp từ Settings trong constructor (có cờ `_loadingSettings` chặn ghi ngược lúc nạp).
   - `partial void OnXoaProfileTaoLaiChanged` lưu ngay qua `SetSyncFreshProfile` (trừ lúc đang nạp).

5. **`orders/XuLyDonShopee.App/Views/AccountsView.axaml`** — thêm `CheckBox` "Xóa profile và tạo lại"
   `IsChecked="{Binding XoaProfileTaoLai}"` đặt ngay dưới cụm nút Sync (DockPanel.Dock=Top), kèm ToolTip giải
   thích. Không thêm style mới (hòa với toolbar hiện có).

6. **Tests** (`orders/XuLyDonShopee.Tests/`):
   - `ProfileJanitorTests.cs` (MỚI): thư mục có file con → reset xong rỗng + tồn tại; chưa tồn tại → tạo mới,
     trả true; file khóa `FileShare.None` → trả false không ném (file khóa vẫn còn); đường dẫn KHÔNG có segment
     `profiles` → trả false, KHÔNG xóa file "keep.txt"; đường dẫn rỗng/space → false. (6 test)
   - `SettingsRepositoryTests.cs`: `GetSyncFreshProfile` chưa đặt → false; round-trip Theory true/false;
     bền sau khi mở lại DB. (4 test)
   - `AccountsViewModelTests.cs`: mặc định TẮT + đổi thì lưu ngay vào Settings; nạp lại từ Settings khi khởi
     tạo VM. (2 test)

### Kết quả kiểm chứng (lệnh đã chạy)

- `dotnet build ShopeeSuite.sln -c Release` → **Build succeeded. 0 Warning(s), 0 Error(s)**.
- `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj -c Release --no-build` →
  **Passed! Failed: 0, Passed: 754, Skipped: 0, Total: 754** (742 baseline + 12 test mới). Không gặp
  FileLoadException 0x800711C7 (WDAC) lần này.

### Đối chiếu tiêu chí nghiệm thu §4

- [x] Build sạch; toàn bộ test pass (754 ≥ 742).
- [x] Cờ tắt (mặc định): nhánh cờ-tắt không đổi — chỉ giữ `Directory.CreateDirectory` như cũ (đã kiểm bằng
      test mặc-định-TẮT + không có test nào cũ hồi quy).
- [x] Cờ bật: xóa + tạo lại đúng `userDataDir` (`profiles/<id>-<kind>`) đặt trước vòng relaunch → relaunch
      không chạy lại (chỉ 1 lần lúc phiên mở mới). Xác thực logic reset+sanity bằng `ProfileJanitorTests`.
- [x] Xóa thất bại không làm phiên chết — `TryResetDirectory` trả false, AccountSession log cảnh báo rồi
      `CreateDirectory` chạy tiếp (test file-khóa chứng minh trả false không ném).
- [x] Checkbox hiển thị ở màn Tài khoản (AccountsView), trạng thái bền qua restart (lưu Settings) — test
      persist + nạp-lại-VM.

### Vướng mắc / bỏ dở

- Không có. Toàn bộ hạng mục trong plan đã thực hiện.
- Lưu ý: hành vi "cờ bật khi phiên mở thật sẽ xóa hồ sơ" chỉ kiểm gián tiếp qua unit test (ProfileJanitor +
  Settings + VM) vì đường mở phiên thật cần Brave/CDP (không chạy trong test/CI). Logic ghép trong
  `AccountSession.RunAsync` được kiểm bằng đọc code + build sạch, chưa chạy end-to-end với trình duyệt thật.
