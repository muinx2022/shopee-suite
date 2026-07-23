# Plan: Đổi nút "Sync" → "Chạy" + XÓA hẳn màn "Chạy tự động"

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nhánh:** `feature/dang-nhap-subaccount` (worktree `d:\Projects\shopee-suite-wt-subaccount`)

## 1. Bối cảnh & mục tiêu

Đây là phần UI của mô hình mới "1 subaccount = nhiều shop" (engine đã xong ở
`plans/2026-07-23-chay-vong-lap-shop.md`). Trong mô hình mới, **mở phiên = tự đăng nhập subaccount rồi
lặp qua các shop** (RunAsync tự chạy vòng lặp shop). Nên:

- Nút hành động chính không còn là "Sync trọn gói" nữa mà là **"Chạy"** — bấm = **mở phiên** (khởi động
  trình duyệt → đăng nhập → tự chạy vòng lặp shop). KHÔNG chạy `SyncFullAsync` như một hành động thủ công
  (vòng lặp đã tự làm Check/Process/Sync; chạy thêm SyncFull sẽ giẫm vòng lặp).
- Màn **"Chạy tự động"** (AutoRun theo lô) không còn ý nghĩa (vòng lặp shop đã thay vai trò chạy nền lặp)
  → **xóa hẳn** tính năng.

**Người dùng chốt:** "phần chạy tự động bỏ", "sửa chữ sync và icon thành chữ chạy và icon chạy; click vào
đó sẽ thực hiện việc đăng nhập". Đã chốt trước đó (AskUserQuestion): **xóa hẳn** AutoRun (không chỉ ẩn).

## 2. Phạm vi

- **Làm:** đổi nhãn+icon+hành vi nút "Sync"→"Chạy" (nút form tài khoản + nút ribbon "Sync đã chọn"); xóa
  toàn bộ màn/tính năng "Chạy tự động" (code + nav + ribbon + test); dọn dead-code kéo theo.
- **Không làm:**
  - KHÔNG đụng engine vòng lặp shop (đã xong ở plan trước).
  - KHÔNG xóa badge/lọc "TK chưa xác nhận" + nút "Truy cập TK" ở màn Tài khoản (GIỮ UI — dù hết nguồn
    nuôi sau khi bỏ probe autorun; đây là tradeoff người dùng đã đồng ý). GIỮ `AccountRepository.MarkVerifyFailed`
    /`ClearVerifyFailed` + cột `verify_failed_at` + test của chúng.
  - KHÔNG đụng Apps Script / GSheet / nhánh khác.

## 3. Các bước thực hiện

### Bước 1 — Xóa tính năng "Chạy tự động" (AutoRun)

**Xóa file** (toàn bộ):
- `orders/XuLyDonShopee.App/Services/AutoRunService.cs`
- `orders/XuLyDonShopee.App/Services/AutoRunBatcher.cs`
- `orders/XuLyDonShopee.App/Services/AutoRunPlan.cs`
- `orders/XuLyDonShopee.App/ViewModels/AutoRunViewModel.cs`
- `orders/XuLyDonShopee.App/Views/AutoRunView.axaml` + `AutoRunView.axaml.cs`
- `orders/XuLyDonShopee.Core/Models/AutoRunSettings.cs`
- Test: `orders/XuLyDonShopee.Tests/AutoRunBatcherTests.cs`, `AutoRunPlanTests.cs`,
  `AutoRunSettingsTests.cs`, `AutoRunUnverifiedTests.cs`.

**Gỡ tham chiếu:**
- `MainViewModel.cs`: bỏ field `_autoRunVm`, property `AutoRunVm`, mục NavItems `"Chạy tự động"` (index 2),
  nhánh `case 2` trong `OnSelectedNavIndexChanged`. **Dồn index:** Proxy hiện là index 3 → thành index 2
  (NavItems còn: Tài khoản=0, Đơn hàng=1, Proxy=2). Cập nhật `case 3`→`case 2` cho Proxy; xóa case cũ.
- `AppServices.cs`: bỏ property `AutoRun` (dòng ~70) + khởi tạo `AutoRun = new AutoRunService(this)`
  (dòng ~115) + doc liên quan.
- `ShellViewModel.cs`:
  - Bỏ `RibbonScreenItem "Chạy tự động"` (`oAuto`, dòng ~146) khỏi nhóm "Màn hình" của tab Shopee
    (dòng ~174) → nhóm còn `{ oAccounts, oOrders, oProxy }`. Vì `RibbonScreenItem` của đơn hàng dùng
    tham số index màn (0/1/3) khớp `SelectedNavIndex` → cập nhật index oProxy 3→2 cho khớp MainViewModel.
  - Xóa đoạn shutdown gọi `AutoRun.StopAsync` (dòng ~90 "dừng vòng Chạy tự động…") — chỉ còn kill phiên
    Brave như cũ. Rà mọi chỗ khác gọi `_services.AutoRun` / `AutoRun.` để gỡ.

**Dead-code kéo theo (probe autorun):**
- `IAccountSession.ProbePageStateAsync` + impl trong `AccountSession.cs` (dòng ~215) CHỈ được AutoRunService
  gọi → xóa khỏi interface + impl. Cập nhật 2 test-stub implement interface: `AccountRowViewModelTests.cs`
  (dòng ~57), `AccountSessionManagerTests.cs` (dòng ~63) — bỏ dòng `ProbePageStateAsync`.
- GIỮ `TryClearVerifyFailedAfterLogin` trong `AccountSession` (vẫn dùng trong nhánh poll degrade của RunAsync).
- GIỮ `MarkVerifyFailed`/`ClearVerifyFailed`/`verify_failed_at` + badge UI (không có writer mới — chấp nhận).

### Bước 2 — Đổi nút "Sync" → "Chạy" (nhãn + icon + hành vi)

**Hành vi mới:** "Chạy" = **mở phiên** (idempotent). Vòng lặp shop tự chạy trong `RunAsync` sau đăng nhập
→ KHÔNG gọi `SyncFullAsync` thủ công.

- `AccountsViewModel.cs`:
  - Thêm command `Run` (RelayCommand) thay cho `SyncFull` ở nút chính: chụp `accountId` từ `_editingId`
    (mẫu SyncFull hiện có), rồi `_services.Sessions.Start(accountId)` (idempotent — đang chạy thì thôi),
    cập nhật trạng thái nút. Nếu phiên đã `IsShopLoopRunning` → log "Đang chạy rồi." rồi thôi.
  - `SyncSelected` (ribbon "Sync đã chọn") → đổi thành `RunSelected`: với mỗi tài khoản đang tick,
    `_services.Sessions.Start(id)` (mỗi phiên tự lặp shop của nó). Bỏ dùng `RunOrAutoStartAsync`
    +`SyncFullAsync` cho đường này (không chạy hành động thủ công nữa).
  - GIỮ `SyncFullAsync` (engine — vòng lặp shop gọi `ChayFlowMotShopAsync`=`SyncFullAsync`) và
    `RunOrAutoStartAsync` + guard `IsShopLoopRunning` cho các nút thủ công KHÁC (Kiểm tra/Xử lý đơn ở màn
    Đơn hàng) nếu còn — KHÔNG xóa.
  - Đổi tên property gate `CanSyncOrders`→`CanRun` (hoặc giữ tên, chỉ đổi nhãn) — miễn nút bật khi đang
    xem tài khoản đã lưu; đối chiếu chỗ dùng.
- `Views/AccountsView.axaml` (dòng ~322): `Content="⇊ Sync"` → `Content="▶ Chạy"`;
  `Command="{Binding SyncFullCommand}"` → `Command="{Binding RunCommand}"`;
  `IsEnabled="{Binding CanSyncOrders}"` → theo tên gate mới; ToolTip đổi: "Chạy — Mở trang, đăng nhập
  Nền tảng tài khoản phụ rồi tự lặp qua các shop (kiểm tra → chuẩn bị hàng → sync về máy + Google Sheet),
  nghỉ 3–5' giữa các shop, lặp tới khi Dừng."
- `ShellViewModel.cs` (dòng ~155): `RibbonActionItem("Sync đã chọn", "⇊", acc.SyncSelectedCommand, …)` →
  `RibbonActionItem("Chạy đã chọn", "▶", acc.RunSelectedCommand, "Mở + đăng nhập + tự lặp shop cho các
  tài khoản đang tick")`. GIỮ "Chọn tất cả"/"Dừng đã chọn"/"Dừng tất cả".
- Rà mọi nhãn "Sync trọn gói"/"Sync đã chọn" còn sót trong tooltip/log của các file trên cho nhất quán
  (không bắt buộc đổi log kỹ thuật, ưu tiên nhãn người dùng thấy).

### Bước 3 — Test

- Xóa 4 file test AutoRun (bước 1). Rà test còn tham chiếu `AutoRun*`/`ProbePageStateAsync`/`SyncFullCommand`
  /`SyncSelectedCommand` → cập nhật theo tên mới hoặc bỏ.
- `AccountRowViewModelTests` / `AccountSessionManagerTests`: bỏ stub `ProbePageStateAsync`; thêm stub
  `IsShopLoopRunning => false` nếu interface đòi (engine đã thêm — kiểm build).
- Nếu có test cho `SyncSelectedAsync`/`SyncFull` hành vi cũ → chuyển sang kiểm `Run`/`RunSelected` gọi
  `Sessions.Start` (hoặc bỏ nếu không còn hành vi tương ứng).
- `dotnet build` 3 project orders + `dotnet test orders/XuLyDonShopee.Tests` — XANH toàn bộ (baseline 954
  trừ số test AutoRun đã xóa; KHÔNG được đỏ test còn lại).

## 4. Tiêu chí nghiệm thu

- [ ] Build orders 0 error; `dotnet test` xanh (đã trừ test AutoRun bị xóa, không đỏ test khác).
- [ ] KHÔNG còn file/tham chiếu AutoRun nào (grep `AutoRun` trong `orders/` = 0 kết quả code, chỉ còn có
      thể trong plan cũ). Nav còn 3 mục (Tài khoản/Đơn hàng/Proxy); ribbon tab Shopee nhóm Màn hình còn 3.
- [ ] Nút form "▶ Chạy" (thay "⇊ Sync") gọi `RunCommand` = mở phiên (Start), KHÔNG chạy SyncFull thủ công.
- [ ] Ribbon "▶ Chạy đã chọn" (thay "Sync đã chọn") mở phiên cho các tài khoản tick.
- [ ] Badge/lọc "TK chưa xác nhận" + nút "Truy cập TK" vẫn còn (UI), build không lỗi dù hết writer.
- [ ] Không còn `ProbePageStateAsync` trong interface/impl; app build + chạy được (khởi tạo MainViewModel
      không lỗi index nav).

## 5. Rủi ro & lưu ý

- **Dồn index nav (Proxy 3→2)** là điểm dễ sai: MainViewModel `OnSelectedNavIndexChanged` switch + ShellViewModel
  `RibbonScreenItem` index của đơn hàng phải KHỚP nhau và khớp thứ tự NavItems. Gate `SyncOrdersActionGroups`
  (onAccounts = SelectedNavIndex==0) KHÔNG đổi (Tài khoản vẫn index 0).
- Đừng để "Chạy" gọi `SyncFullAsync` (giẫm vòng lặp). Chỉ `Sessions.Start`. Guard `IsShopLoopRunning` là
  lớp chắn phụ.
- Grep kỹ `AutoRun` sau khi xóa để không sót tham chiếu (AppServices, ShellViewModel shutdown, MainViewModel,
  DI/khởi tạo) — sót một chỗ là không build.
- `AutoRunView.axaml` có thể được ViewLocator ánh xạ theo tên — kiểm `ViewLocator.cs` không hardcode
  `AutoRunViewModel` (nếu có → gỡ).
- GIỮ engine (`SyncFullAsync`, `ChayFlowMotShopAsync`, guard) — chỉ đổi ĐƯỜNG NÚT gọi vào.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
