# Plan: Xóa nhiều tài khoản đã tick ở màn Tài khoản (module Đơn hàng)

- **Ngày:** 2026-07-21
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Màn **Tài khoản** của module Đơn hàng (tab Shopee trong suite) có cơ chế tick nhiều dòng (checkbox từng dòng + nút ✓ "Chọn toàn bộ") nhưng tick này hiện chỉ phục vụ **⇊ Sync đã chọn** và **■ Dừng đã chọn**. Nút xóa 🗑 (góc dưới danh sách, cạnh "+ Thêm tài khoản") chỉ xóa **một** tài khoản — dòng đang bôi đậm (`SelectedRow`), bỏ qua hoàn toàn các dòng đang tick. Người dùng muốn: tick nhiều tài khoản → bấm 🗑 → xóa tất cả các tài khoản đã tick một lần.

Hiện trạng code (các mỏ neo đã khảo sát):

- View: `orders/XuLyDonShopee.App/Views/AccountsView.axaml`
  - Nút 🗑 ở "Hàng nút dưới" (~dòng 147–154): `Command="{Binding DeleteCommand}"`, `IsEnabled="{Binding SelectedRow, Converter={x:Static ObjectConverters.IsNotNull}}"`, chưa có tooltip.
- ViewModel: `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`
  - `DeleteAsync()` (~dòng 580–605): guard `SelectedRow is null` → return; confirm qua `DialogService.ConfirmAsync("Xóa tài khoản", $"Bạn có chắc muốn xóa tài khoản \"{target.Email}\"? …")`; rồi `_services.Accounts.Delete(target.Id)`; reset form (`IsNew=false`, `SelectedRow=null` dưới cờ `_isRefreshing`, `IsEditing=false`, `ClearForm()`, `Reload()`).
  - Tick từng dòng: `AccountRowViewModel.IsSelected`; tập bền `_selectedIds` (HashSet\<long\>, ~dòng 31) khôi phục tick khi `Reload()` dựng lại danh sách (~dòng 376–399).
  - Mọi mutate tick đều đi qua ViewModel: `ToggleRowTick` (~dòng 445), `SelectAll()` (~dòng 430–438), và `Reload()` (gán `IsSelected` khi dựng row). KHÔNG có chỗ nào khác set `IsSelected`.
  - `OnSelectedRowChanged` partial đã có (~dòng 240).
  - `_services.Sessions.Stop(id)` (`AccountSessionManager.Stop`) là **no-op an toàn** khi tài khoản không có phiên (TryGetValue rồi fire-and-forget StopAsync).
  - `_services.Accounts.Delete(id)` = `AccountRepository.Delete` (DELETE FROM accounts WHERE Id=...), xóa từng id.
- Mẫu confirm xóa nhiều đã có trong codebase: `ProxiesViewModel` (~dòng 139–141) dùng câu "Xóa toàn bộ {N} proxy trong danh sách? Thao tác này không thể hoàn tác.".

Quyết định đã chốt:

- **Ưu tiên tick**: bấm 🗑 khi có ≥1 dòng **đang hiển thị** được tick → xóa toàn bộ các dòng tick đó. Không dòng nào tick → giữ hành vi cũ (xóa `SelectedRow`).
- Chỉ xóa dòng tick **đang hiển thị** (`Accounts`, sau lọc) — nhất quán với ngữ nghĩa "Sync đã chọn"/"Dừng đã chọn" (hai lệnh này cũng chỉ đọc `Accounts`). Tick bền của dòng đang bị ẩn do ô tìm kiếm KHÔNG bị xóa.
- Trước khi xóa mỗi tài khoản: gọi `Sessions.Stop(id)` để không mồ côi cửa sổ Brave của tài khoản bị xóa (kể cả đường xóa 1 tài khoản — thêm luôn cho nhất quán).

## 2. Phạm vi

- **Làm:**
  - Sửa `DeleteAsync` trong `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs` thành xóa hàng loạt theo tick (fallback dòng đang chọn).
  - Thêm property `CanDelete` + notify để nút 🗑 sáng khi có tick dù chưa bôi đậm dòng nào.
  - Cập nhật binding `IsEnabled` + thêm tooltip cho nút 🗑 trong `orders/XuLyDonShopee.App/Views/AccountsView.axaml`.
- **Không làm:**
  - Không đổi cơ chế tick/`_selectedIds`, không đổi các lệnh Sync/Dừng.
  - Không thêm nút mới, không đổi bố cục màn.
  - Không đụng các màn khác (Proxies, Orders…), không đụng suite/.

## 3. Các bước thực hiện

1. **`AccountsViewModel.cs` — xóa hàng loạt.** Viết lại `DeleteAsync()`:
   - Chụp danh sách target **trước mọi await** (bài học sẵn trong file — không giữ tham chiếu row qua await): `var targets = Accounts.Where(r => r.IsSelected).Select(r => (r.Id, r.Email)).ToList();` nếu rỗng và `SelectedRow != null` → `targets = [(SelectedRow.Id, SelectedRow.Email)]`; vẫn rỗng → return.
   - Confirm:
     - 1 tài khoản → giữ nguyên câu cũ: `Bạn có chắc muốn xóa tài khoản "{email}"? Thao tác này không thể hoàn tác.`
     - N > 1 → tiêu đề "Xóa tài khoản", nội dung dạng: `Bạn có chắc muốn xóa {N} tài khoản đã tick?\n{tối đa 5 email đầu, phân cách xuống dòng hoặc ", "}` + nếu N > 5 thêm `… và {N-5} tài khoản khác` + câu `Thao tác này không thể hoàn tác.`
   - Người dùng đồng ý → với từng target: `_services.Sessions.Stop(id);` rồi `_services.Accounts.Delete(id);` và `_selectedIds.Remove(id);`.
   - Reset form như cũ (nguyên khối hiện tại): `IsNew=false`; `_isRefreshing=true; SelectedRow=null; _isRefreshing=false;` `IsEditing=false; ClearForm(); Reload();`.
   - Cập nhật doc-comment của method mô tả ngữ nghĩa mới.
2. **`AccountsViewModel.cs` — property `CanDelete`.**
   - Thêm `public bool CanDelete => SelectedRow is not null || Accounts.Any(r => r.IsSelected);` (kèm doc-comment: nguồn sáng/tắt của nút 🗑).
   - Raise `OnPropertyChanged(nameof(CanDelete))` tại **mọi** điểm tick/lựa chọn đổi: cuối `ToggleRowTick`, cuối `SelectAll()`, cuối `Reload()` (sau vòng dựng rows), và trong `OnSelectedRowChanged` (đặt TRƯỚC guard `_isRefreshing` để không sót đường refresh — cạnh chỗ gọi `RebuildFilteredLog()`).
3. **`AccountsView.axaml` — nút 🗑.**
   - Đổi `IsEnabled` sang `{Binding CanDelete}`.
   - Thêm `ToolTip.Tip="Xóa các tài khoản đang tick — không tick dòng nào thì xóa tài khoản đang chọn. Hỏi xác nhận trước khi xóa."`.
   - Cập nhật comment khối "Hàng nút dưới" nếu cần.
4. **Build + test:**
   - `dotnet build orders/XuLyDonShopee.App`
   - `dotnet test orders/XuLyDonShopee.Tests`
   - Build cả suite tham chiếu module: `dotnet build suite/Shopee.Suite`

## 4. Tiêu chí nghiệm thu

- [ ] Build 3 project trên xanh, test XuLyDonShopee.Tests pass toàn bộ.
- [ ] Tick ≥2 tài khoản → nút 🗑 sáng (kể cả khi không bôi đậm dòng nào) → bấm → dialog xác nhận ghi đúng số lượng + tên → OK → tất cả tài khoản tick biến mất khỏi danh sách (kiểm tra lại bằng `Reload`/tìm kiếm), form bên phải về placeholder.
- [ ] Không tick gì, chỉ bôi đậm 1 dòng → 🗑 xóa đúng 1 tài khoản đó với câu confirm cũ (hành vi cũ giữ nguyên).
- [ ] Không tick, không bôi đậm → nút 🗑 xám (disabled).
- [ ] Bấm Cancel trên dialog → không xóa gì, tick giữ nguyên.
- [ ] Tài khoản bị xóa đang có phiên chạy → phiên được Stop (không còn cửa sổ Brave mồ côi thuộc tài khoản đã xóa).

## 5. Rủi ro & lưu ý

- **Không giữ tham chiếu `SelectedRow`/row qua `await`** — quy ước sẵn của file (xem comment trong `ClearLog`); chụp `(Id, Email)` trước `ConfirmAsync`.
- `OnSelectedRowChanged` có guard `_isRefreshing` return sớm — notify `CanDelete` phải đặt trước guard, giống `RebuildFilteredLog()`.
- KHÔNG xóa theo `_selectedIds` (tập bền) — chỉ theo `Accounts` đang hiển thị, tránh xóa nhầm dòng đang bị ẩn do lọc mà người dùng không nhìn thấy.
- `ConfirmDialog` hiển thị message nhiều dòng: nếu message dài (nhiều email) dialog có thể cao — giới hạn 5 email là đủ.

---

## Báo cáo thực thi (Opus điền sau khi xong)

> **Nghiệm thu (Fable):** đạt. Bổ sung thêm sau review: untick các dòng vừa xóa trước `Reload()` trong `DeleteAsync` — chặn vòng đồng bộ đầu của `RefreshList` nạp lại id đã xóa vào `_selectedIds` (id SQLite cấp lại → tài khoản mới thêm tự dưng tick sẵn). Build + 769 test xanh sau khi bổ sung.

**Ngày:** 2026-07-21 · **Người thực thi:** Opus

### File đã sửa

1. `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`
   - Thêm property `public bool CanDelete => SelectedRow is not null || Accounts.Any(r => r.IsSelected);` (kèm doc-comment), đặt ngay sau `CanSyncOrders`.
   - `OnSelectedRowChanged`: thêm `OnPropertyChanged(nameof(CanDelete));` đặt TRƯỚC guard `_isRefreshing` (cạnh `RebuildFilteredLog()`).
   - `SelectAll()`: thêm `OnPropertyChanged(nameof(CanDelete));` ở cuối.
   - `ToggleRowTick`: đổi từ expression-body sang block, thêm `OnPropertyChanged(nameof(CanDelete));`.
   - `Reload()`: thêm `OnPropertyChanged(nameof(CanDelete));` sau `RefreshList(selectId)`.
   - Viết lại `DeleteAsync()`: chụp `targets = Accounts.Where(r => r.IsSelected).Select(r => (r.Id, r.Email)).ToList()` trước await; rỗng + có `SelectedRow` → fallback `[(SelectedRow.Id, SelectedRow.Email)]`; vẫn rỗng → return. Confirm 1 câu (1 tài khoản giữ nguyên câu cũ; N>1 ghi số lượng + tối đa 5 email, thừa thì "… và {N-5} tài khoản khác", + "Thao tác này không thể hoàn tác."). OK → foreach target: `Sessions.Stop(id)` → `Accounts.Delete(id)` → `_selectedIds.Remove(id)`. Reset form nguyên khối cũ + `Reload()`. Thêm doc-comment mô tả ngữ nghĩa mới.

2. `orders/XuLyDonShopee.App/Views/AccountsView.axaml`
   - Nút 🗑: đổi `IsEnabled` từ `{Binding SelectedRow, Converter=...IsNotNull}` sang `{Binding CanDelete}`; thêm `ToolTip.Tip="Xóa các tài khoản đang tick — không tick dòng nào thì xóa tài khoản đang chọn. Hỏi xác nhận trước khi xóa."`.

### Kết quả build/test

- `dotnet build orders/XuLyDonShopee.App` → **Build succeeded, 0 Warning, 0 Error**.
- `dotnet test orders/XuLyDonShopee.Tests` → **Passed! Failed: 0, Passed: 769, Skipped: 0, Total: 769**.
- `dotnet build suite/Shopee.Suite` → **Build succeeded, 0 Warning, 0 Error**.

### Tiêu chí nghiệm thu — trạng thái

- [x] Build 3 project xanh, test pass toàn bộ (769/769). — kiểm bằng lệnh ở trên.
- [~] Các tiêu chí về hành vi UI (nút sáng khi tick, dialog ghi đúng số lượng/tên, xóa hết tick, form về placeholder, hành vi 1 dòng cũ, disabled khi không tick/không chọn, Cancel không xóa, phiên được Stop) — **không có test tự động, chỉ kiểm được bằng chạy tay**. Logic đã cài đúng theo plan; chưa chạy app thực để mắt thấy.

### Điểm cần phiên chính soi lại (lệch nhẹ so với ý ngầm của plan, KHÔNG lệch chữ)

- `_selectedIds.Remove(id)` trong vòng foreach (đúng theo plan) bị `ApplyFilter` (gọi từ `Reload`) **thêm lại**: khi `Reload` chạy, các dòng đã-xóa-và-đang-tick VẪN còn trong `Accounts` với `IsSelected=true` (vòng reset form không untick chúng), nên vòng đồng bộ đầu `ApplyFilter` re-add id đó vào `_selectedIds` trước khi Clear. Hệ quả: id đã xóa còn sót lại trong `_selectedIds` như "rác chết". **Vô hại về mặt tính năng**: account đã xóa không còn trong `_all` nên không dựng thành row → không hiện lại tick, danh sách sạch, tiêu chí nghiệm thu "tất cả tài khoản tick biến mất" vẫn đạt. Rủi ro lý thuyết duy nhất: nếu SQLite tái dùng đúng rowid đó cho một account MỚI (INTEGER PRIMARY KEY không AUTOINCREMENT có thể cấp lại max+1 khi bản ghi max bị xóa), account mới sẽ hiện tick sẵn. Muốn triệt để thì phải untick các row đã xóa trước `Reload` (ngoài phạm vi câu chữ plan) — để phiên chính quyết có cần không.
