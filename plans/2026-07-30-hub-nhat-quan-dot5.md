# Plan: Hub web — nhất quán đợt 5 (component trùng, hằng, dọn nốt)

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành (chờ phiên chính nghiệm thu + commit)
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

Các món nhất quán còn lại phía `server/Shopee.Hub.Web` (sau B2 + P4-hub):

1. **Cặp component trùng lưới sản phẩm:** `Components/Pages/AllData.razor` ↔ `Components/Shared/ProductGridPanel.razor` trùng khối phân trang + khối nút hành động → tách `ProductGridPager.razor` + `ProductGridActions.razor` dùng chung (markup kết quả PHẢI y hệt từng bên — 2 bên có thể lệch nhẹ, khác biệt tham số hoá).
2. **Hằng `OnlineThreshold` 45s** đang ghi ~3 nơi (grep `45` quanh online/last_seen — xác định chính xác) → 1 hằng dùng chung (vd trong `FleetStateService` hoặc `HubOptions`).
3. **`ProductApiEndpoints`**: ~21 khối lặp guard "Postgres chưa sẵn sàng" → helper/guard clause chung (1 method), hành vi + thông điệp trả về giữ nguyên.
4. **Xoá `FileStoreConfigService.RemoveShopeeAccount`** — hết caller sau khi endpoint `/accounts/remove` bị xoá ở B2 (grep xác nhận 0 caller trước khi xoá; `AppendShopeeAccounts` đã xoá rồi).
5. **`Logs.razor` hardening** (finding review bị bác về mức thấp nhưng vá rẻ): bọc thân vòng `PeriodicTimer` bằng try/catch (giữ `OperationCanceledException` thoát vòng; lỗi DB nhất thời → giữ bản cũ, tick sau thử lại); `DisposeAsync` bắt Exception chung quanh `await _pollTask`.
6. **`?status=all` của `/orders`**: giữ nguyên thiết kế sentinel (P4-hub đã làm đúng), chỉ bổ sung 1 dòng xmldoc giải thích vì sao cần sentinel (rỗng bị loại khỏi query).

## 2. Phạm vi

- Khu: CHỈ `server/Shopee.Hub.Web/**` (+ `server/Shopee.Hub.Web.Tests` nếu thêm test). KHÔNG đụng khu khác. KHÔNG đổi hành vi/markup/URL. KHÔNG commit.

## 3. Nghiệm thu

- [ ] `dotnet build server/ShopeeHub.sln` 0/0; `dotnet test server/Shopee.Hub.Web.Tests` ≥ 44.
- [ ] Grep: khối phân trang/nút chỉ còn 1 bản trong 2 component mới; `RemoveShopeeAccount` = 0 hit; guard Pg lặp = 0 (chỉ còn helper).
- [ ] So markup trước/sau 2 trang dùng lưới (mẫu cách agent P4-hub đã làm — đếm tag + literal class/style): lệch 0.
- [ ] Báo cáo: bảng từng mục + vị trí.

## 5. Rủi ro & lưu ý

- Bước 0: worktree phải ở commit chứa plan này hoặc mới hơn (`git merge --ff-only main` nếu không).
- ProductGridPanel nằm trong workspace master-detail (bẫy tick-clobber) — component mới nhận state qua `[Parameter]`, không giữ state riêng.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

Bước 0: worktree ở `0d7918c` → `git merge --ff-only main` lên `4176590` (chứa plan này). Baseline trước khi sửa:
build 0 warning / 0 error, test 44/44 xanh.

| # | Mục | Kết quả | Vị trí |
|---|-----|---------|--------|
| 1 | Tách khối phân trang + nút hành động | XONG | tạo `Components/Shared/ProductGridPager.razor` (52 dòng) + `Components/Shared/ProductGridActions.razor` (26 dòng); `Pages/AllData.razor` −47, `Shared/ProductGridPanel.razor` −47 |
| 2 | Hằng ngưỡng online 45s | XONG — 3 nơi → 1 | thêm `FleetStateService.OnlineThreshold` + `IsOnline(MachinePresence)`; sửa `MainLayout.razor:109`, `DispatchRowsBuilder.cs:182`, `FleetRowsBuilder.cs:130` |
| 3 | Guard "Pg chưa sẵn sàng" lặp | XONG — 19 khối → 1 helper | `Api/ProductApiEndpoints.cs`: thêm `WithPg(sp, body)`; 19 handler bọc lại |
| 4 | Xoá `RemoveShopeeAccount` | XONG — 0 caller | `Services/FileStoreConfigService.cs` (−17 dòng, kèm comment mục) |
| 5 | `Logs.razor` hardening | XONG | thân vòng `PeriodicTimer` bọc `catch (Exception) when (!ct.IsCancellationRequested)`; `DisposeAsync` đổi `catch (OperationCanceledException)` → `catch (Exception)` |
| 6 | Xmldoc sentinel `?status=all` | KHÔNG CẦN SỬA | `Pages/Orders.razor:120-122` đã có sẵn xmldoc giải thích đúng ý ("chuỗi rỗng bị bỏ khỏi query, rỗng ≠ vắng") — P4-hub đã làm |

### Nghiệm thu

- [x] `dotnet build server/ShopeeHub.sln --no-incremental` → **0 warning / 0 error**; `dotnet test server/Shopee.Hub.Web.Tests` → **44/44 xanh** (= baseline, không thêm test vì thuần refactor).
- [x] Grep: `RemoveShopeeAccount` = 0 hit trong nguồn · `pdb is null || !pdb.IsReady` trong `ProductApiEndpoints.cs` = **1** (trong `WithPg`) · `TotalSeconds < 45` = 0 hit · khối "Trang đầu" của lưới SP chỉ còn trong `ProductGridPager.razor` · nút "Chọn tất cả dòng của trang này" chỉ còn trong `ProductGridActions.razor`.
- [x] So markup trước/sau (script đếm chuỗi thẻ + multiset literal `class`/`style`/`title` + binding + text hiển thị, ghép 2 component lại rồi so với block cũ lấy từ `git show HEAD`): cả 2 trang **33 thẻ / 25 literal / 30 binding — khớp 100%**. Lệch duy nhất: `@onclick` nút xoá đổi từ lambda inline sang tham số `OnDelete` (cha nối lại đúng hàm cũ) — đúng thiết kế.

### Ghi chú cho phiên chính soi lại

- **Hành vi phân trang giữ y nguyên nhưng code chuyển chỗ:** `GoPage` / `OnPageInput` / `OnPageSizeChange` xoá khỏi CẢ 2 trang, giờ nằm trong `ProductGridPager`; sau khi gọi engine nó bắn `OnViewChanged` — `/data` nối vào `UpdateUrl()`, lưới per-shop nối vào `NotifyView()`, đúng như 2 bản cũ (kể cả chi tiết `OnPageSizeChange` gọi callback KỂ CẢ khi parse số hỏng).
- **Bẫy tick-clobber:** 2 component mới không giữ state nào, chỉ đọc `[Parameter] Engine`. Nút chọn/mark/reset/regen giờ do component con xử lý, nhưng mọi hàm đó đều `Raise()` → `Engine.Changed` → trang cha `StateHasChanged` như cũ, nên bảng vẫn vẽ lại (đã đối chiếu `ProductGridEngine`).
- **Khác biệt 2 bên đã tham số hoá:** `ShowTotal` (chỉ `/data` khoe "· N dòng" cạnh số trang) và `DeleteLabel` ("🗑 Xóa nhiều" vs "🗑 Xoá" — giữ nguyên cả cách viết dấu khác nhau của bản cũ).
- **Ngoài phạm vi, KHÔNG đụng:** `HubOptions.StaleMachine = 45s` (`HubDatabase.Assignments.cs:15`) là option cấu hình cho sweep assignment, khác nghĩa với chấm hiện diện UI → để nguyên. Guard `pdb is null || !pdb.IsReady` còn trong `RewriteJobService.cs` (3 chỗ) và `SheetMapService.cs` (1 chỗ) — là service, trả về khác nhau (`null` / `Fail(job,…)`), plan chỉ nhắm `ProductApiEndpoints` → để nguyên.
- 3 file đụng bằng `Write` bị ghi LF, đã chuyển lại **CRLF** (không BOM) cho khớp phần còn lại của repo.
- KHÔNG commit.
