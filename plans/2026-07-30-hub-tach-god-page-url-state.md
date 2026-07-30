# Plan: Hub web — tách god-page (Fleet/Dispatch) + UrlState dùng chung + URL-state 3 trang còn thiếu

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

Đo 30/07 trong `server/Shopee.Hub.Web`: `Components/Pages/Dispatch.razor` **1.270 dòng** (vẫn god-page dù đã tách `DispatchOrdersTab.razor` 381 dòng), `Components/Pages/Fleet.razor` **925 dòng**. Ngoài ra khuôn URL-state (want-tuple + `QueryHelpers.ParseQuery` + `NavigateTo(replace:true)`) bị chép tay **3 bản**: `Fleet.razor` UpdateUrl ~727-751 + RestoreSelectionFromUrl ~755-790; `AllData.razor` UpdateUrl ~248-269 + restore ~229-243; `Dispatch.razor` UpdateUrl ~1209-1229 + RestoreFromUrl ~1231-1270. Và 3 trang **chưa có** URL-state (vi phạm nguyên tắc đã chốt "mọi view-state phải vào URL" — F5/share phải giữ lọc/trang/tab): `/orders` (`Orders.razor` giữ `_shopId/_status/_search/_page` trong field), `/logs-view` (`Logs.razor` `_machineFilter/_levelFilter`), `/config/accounts` (`ConfigAccounts.razor` `_page/_pageSize`).

Lưu ý nền: main hiện đã gộp đợt B2 (Orders.razor có sửa hiển thị giờ, `HubDatabase.Orders/Shops` đổi nhiều, endpoint legacy đã xoá) — số dòng bên trên có thể lệch nhẹ, tìm theo symbol.

## 2. Phạm vi

- **Làm:** chỉ trong `server/Shopee.Hub.Web/**`.
- **Không làm:** KHÔNG đổi hành vi/nghiệp vụ, KHÔNG đổi giao diện (markup kết quả render phải như cũ), KHÔNG đổi TÊN param URL đang có (`p`/`ps` của Fleet, `page`/`size` của AllData — đổi tên là gãy link đã share; sự lệch tên này chấp nhận, chỉ hợp nhất phần máy móc parse/build), KHÔNG đụng `suite/**`, `orders/**`, `extensions/**`, `shared/**`.

## 3. Các bước thực hiện

1. **Helper `UrlState`** (`server/Shopee.Hub.Web/Components/UrlState.cs`): API kiểu `UrlState.Restore(NavigationManager, params (string key, Action<string> apply)[])` + `UrlState.Update(NavigationManager, bool replace, params (string key, string? value)[])` (bỏ key khi value null/rỗng/mặc định) — đúng phần MÁY MÓC chung của 3 bản hiện có; phần "muốn gì vào URL" của từng trang giữ ở trang đó. Đổi 3 trang Fleet/AllData/Dispatch sang helper, giữ nguyên TÊN param + ngữ nghĩa từng trang (đối chiếu từng key trước/sau trong báo cáo).
2. **Tách `Fleet.razor` (925):**
   - Bước 1 (0 rủi ro): chuyển toàn bộ `@code` sang code-behind `Fleet.razor.cs` (partial class).
   - Bước 2: tách component con theo cấu trúc UI hiện có (đề xuất theo plan 25/07: `ShopActionTab.razor`, `AcctDashboard.razor`, `ShopStatsCards.razor`, `WorkspaceShopList.razor` — đặt trong `Components/Shared/`); model row + logic `Rebuild()` sang class thường `FleetRowsBuilder.cs`. Tách đến đâu build + soi render đến đó; component nào tách ra làm markup đổi thì DỪNG và giữ trong trang, ghi lại.
3. **Tách `Dispatch.razor` (1270):** cùng cách — code-behind `Dispatch.razor.cs` trước, rồi tách các khối tab/panel lớn thành component (tuỳ cấu trúc thật, ước 2-4 component; `DispatchOrdersTab` đã có sẵn làm mẫu — chú ý mẫu truyền state qua tham số cha như `omach/oacc` đang dùng).
4. **URL-state 3 trang thiếu** (dùng helper mục 1): `/orders` → `shop`, `status`, `q`, `page`; `/logs-view` → `machine`, `level`; `/config/accounts` → `page`, `size`. Khớp mẫu Restore-khi-OnInitialized + Update-khi-đổi-filter (replace:true) như Fleet đang làm; F5 giữ nguyên trạng thái, link share mở ra đúng màn.
5. Mục tiêu độ dài: `Fleet.razor` + `Dispatch.razor` (phần .razor markup) mỗi file ≤ ~400 dòng; code-behind ≤ ~500; không file mới nào > 800.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build server/ShopeeHub.sln` 0 lỗi 0 warning; `dotnet test server/Shopee.Hub.Web.Tests` ≥ 44 pass (baseline mới).
- [ ] Diff render: với cùng state, markup ra như cũ (kiểm bằng đọc code + chạy được thì càng tốt; KHÔNG đổi class CSS — mobile responsive vừa làm 13/07 phải còn nguyên).
- [ ] Grep `QueryHelpers.ParseQuery` chỉ còn trong `UrlState.cs`.
- [ ] 3 trang mới có URL-state: đổi filter → URL đổi (replace), mở URL có query → state khôi phục.
- [ ] Bảng đối chiếu param URL trước/sau từng trang (tên + ngữ nghĩa không đổi) trong báo cáo.

## 5. Rủi ro & lưu ý

- **Bẫy tick-clobber:** Fleet/Dispatch có vòng tick vẽ lại theo FleetState — khi tách component con, chú ý pattern `FleetPageBase`/`ShouldTickRender` hiện có; đừng để tick của trang cha đè state panel con (bug đã từng dính, ghi trong memory dự án).
- Blazor `@code` → code-behind: giữ nguyên tên member để markup không phải đổi nhiều; component tách ra nhận tham số qua `[Parameter]` + callback, tránh dựng service mới.
- Trang dùng `@inject` gì thì code-behind phải chuyển đủ (inject qua property `[Inject]`).
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
