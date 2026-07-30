# Plan: Hub web — tách god-page (Fleet/Dispatch) + UrlState dùng chung + URL-state 3 trang còn thiếu

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành
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

**Kết quả build/test:** `dotnet build server/ShopeeHub.sln` → 0 lỗi 0 warning · `dotnet test server/Shopee.Hub.Web.Tests` → 44/44 pass (đúng baseline). KHÔNG commit.

### File tạo mới (11)

| File | Dòng | Nội dung |
|---|---|---|
| `Components/UrlState.cs` | 56 | `UrlState.Read/Restore/Update` + struct `UrlQuery` (indexer trả rỗng khi thiếu key, `Int(key, fallback, min)`, `Flag`). `Update` lấy path từ `new Uri(nav.Uri).AbsolutePath` (app không dùng `UsePathBase` → bằng đúng chuỗi "/" · "/dispatch" · "/data" hằng cũ). |
| `Components/FleetRowsBuilder.cs` | 182 | `FleetShopRow` / `FleetOpStat` / `FleetAcctGroup` / `FleetSummary` (public vì là kiểu tham số component) + `BuildRows` · `OrderedGroups` · `Filter` · `Summarize` · `HostName` · `ShortId` (port nguyên `Rebuild`/`RecomputeSummary`/`OrderedGroups`/`Filtered` cũ). |
| `Components/DispatchRowsBuilder.cs` | 357 | `DispatchShopRow` / `DispatchRowGroup` / `DispatchRowSet` / `MachineBudget` / `DispatchGridContext` + `BuildRows` · `UsedByMachine` · `BuildBudgets` · `Holds` · `Filter` · `MatchState` · `Cell` · `OpenAsn` · `OnlineMachines` · `BuildWorkLists` (+ `RunningItem`/`QueuedItem`/`OrdersItem`) · `HostLabel` · `NameLabel` · `Short` · `RowRange` · `LastFailReason`; kèm `internal static DispatchButtons` (`Btn`/`AcctBtn`/`RunnableCount`) để trang và lưới tính nút bằng CÙNG một hàm. |
| `Components/Pages/Fleet.razor.cs` | 355 | Code-behind trang BigSeller (partial class, `@inherits`/`@inject` vẫn ở .razor). |
| `Components/Pages/Dispatch.razor.cs` | 521 | Code-behind trang Giao việc. |
| `Components/Shared/WorkspaceShopList.razor` | 79 | Danh sách acc→shop bên trái + glyph/tooltip. |
| `Components/Shared/ShopStatsCards.razor` | 60 | Tab 📊 Thống kê mức shop. |
| `Components/Shared/AcctDashboard.razor` | 124 | Dashboard mức acc (4 ô KPI + lease + ma-trận shop + lối sang /dispatch). |
| `Components/Shared/DispatchKpiPanel.razor` | 246 | Bảng chi tiết 4 thẻ KPI (máy online / đang chạy / đang chờ / gián đoạn). |
| `Components/Shared/DispatchShopsGrid.razor` | 124 | Lưới shop × op + cụm nút mức tài khoản. |

### File sửa (7)

- `Components/Pages/Fleet.razor` **925 → 228** — bỏ `@code` (sang code-behind), thay 3 vùng bằng `<WorkspaceShopList>` / `<ShopStatsCards>` / `<AcctDashboard>`, bỏ `@using Microsoft.AspNetCore.WebUtilities` + `Microsoft.Extensions.DependencyInjection`.
- `Components/Pages/Dispatch.razor` **1270 → 175** — bỏ `@code`, thay 2 vùng bằng `<DispatchKpiPanel>` / `<DispatchShopsGrid>`, bỏ `@using Microsoft.AspNetCore.WebUtilities`.
- `Components/Pages/AllData.razor` — `ParseUrl`/`UpdateUrl` chuyển sang `UrlState` (−22 dòng), tên param giữ nguyên.
- `Components/DispatchViewLogic.cs` — `DispatchWorkItem` `internal` → `public` (bắt buộc: là kiểu `[Parameter]` của `DispatchKpiPanel`, mà component Razor sinh ra là class public). `DispatchKpiCard` / `OpBtn` / `DispatchViewLogic` giữ `internal`.
- `Components/Pages/Orders.razor` · `Logs.razor` · `ConfigAccounts.razor` — thêm URL-state (chi tiết ở bảng dưới).

### Bảng đối chiếu param URL

| Trang | Param TRƯỚC | Param SAU | Ghi chú |
|---|---|---|---|
| `/` (Fleet) | `acc` `shop` `tab` `q` `p` `ps` | **y nguyên** | `p` chỉ ghi khi >1, `ps` khi ≠100, `tab` rỗng khi chưa chọn gì; `q/p/ps` chỉ khi đang ở tab `data` của 1 shop. |
| `/dispatch` | `tab` `f` `acct` `mach` `omach` `oacc` `q` `kpi` | **y nguyên** | `tab` bỏ khi = `bs`, `f` bỏ khi = `todo`. |
| `/data` (AllData) | `acc` `shop` `sku` `pmin` `pmax` `sold` `dup` `page` `size` | **y nguyên** | `sold/dup` chỉ khi bật, `page` khi >1, `size` khi ≠100. Vẫn cố ý lệch tên với Fleet (`page/size` vs `p/ps`) — không đổi để không gãy link đã share. |
| `/orders` | *(không có)* | `shop` `status` `q` `page` | **MỚI.** `shop` = id shop (bỏ khi 0/không còn trên hub); `status` bỏ khi = mặc định "Chờ lấy hàng", ghi token **`all`** khi user chọn "— tất cả —" (chuỗi rỗng bị query loại nên "vắng" ≠ "rỗng"); `page` khi >1. `pageSize` KHÔNG vào URL (đúng spec). |
| `/logs-view` | *(không có)* | `machine` `level` | **MỚI.** `machine` bỏ qua nếu không còn dòng log nào của máy đó; `level` chỉ nhận info/ok/warn/error. |
| `/config/accounts` | *(không có)* | `page` `size` | **MỚI.** `page` **1-based** trong URL (trong code `_page` vẫn 0-based), bỏ khi = 1; `size` chỉ nhận 10/50/100, bỏ khi = 50. |

### Kiểm chứng markup không đổi

Ngoài đọc đối chiếu, đã so **khung HTML** (đếm tag mở/đóng + mọi `class` / `style` / `data-label` / `colspan` / `title` / `href` / `type` / `placeholder` literal) giữa bản HEAD và bản mới:
- `/dispatch`: khác biệt = **0** (chỉ thêm 2 thẻ component `<DispatchKpiPanel>` / `<DispatchShopsGrid>`).
- `/`: khác biệt = **0** (thêm 3 thẻ component; 2 giá trị đổi vì đổi TÊN BIẾN chứ không đổi giá trị render: `class="wsacct @(_selRow…)"` → `@(SelectedRow…)`, `href="@DispatchLink(_selAcctId!)"` → `href="@DispatchHref"` với `DispatchHref` do cha truyền chính giá trị đó).

Cũng đã đối chiếu từng dòng lệnh của `@code` cũ với bản mới: mọi dòng "không khớp chuỗi" đều là đổi tên kiểu/thêm tham số `snap`/`ctx` khi biến hàm thành static, hoặc là phần plumbing `QueryHelpers` đã thay bằng `UrlState`.

### Độ lệch so với spec / điểm cần phiên chính soi lại

1. **`Fleet.razor` không tách `ShopActionTab.razor`** (plan gợi ý 4 component, làm 3). Lý do: vùng tab ⚡ Hành động ôm state có vòng đời riêng (`_rwPending`/`_rwPendingKey`/`_rwActionMsg`/`_rwWatch`) mà `SelectShop` (mọi tab) và `MaybeAutoRefreshRewrite` (mỗi nhịp 2s) đang chạy **bất kể đang mở tab nào**. Đưa xuống con thì chúng chỉ chạy khi tab đó mở → **đổi hành vi**; giữ ở cha rồi truyền ~13 tham số xuống thì code-behind không giảm. Mốc độ dài đã đạt mà không cần tách nó (markup 228 ≤ 400, code-behind 355 ≤ 500) nên dừng đúng theo câu "component nào tách ra làm markup/hành vi đổi thì DỪNG và ghi lại".
2. **`Dispatch.razor.cs` = 521 dòng**, hơi quá mốc "≤ ~500". Phần dôi là cụm ghi xuống hub (tạo/huỷ assignment, lệnh Đơn hàng) + `UpdateUrl`/`RestoreFromUrl` — đều cần `Db`/`Nav`/state của trang nên không phải chỗ tách tiếp cho sạch.
3. **`DispatchWorkItem` phải mở `public`** (xem trên). Đây là nới visibility duy nhất; nếu phiên chính muốn giữ `internal` thì phải bỏ tách `DispatchKpiPanel`.
4. **Chọn thiết kế cần review: token `all` cho `?status=`** ở `/orders`. Trạng thái mặc định là "Chờ lấy hàng" còn "— tất cả —" là chuỗi RỖNG, mà `UrlState.Update` loại key rỗng → phải có sentinel để phân biệt "vắng key" với "chọn tất cả". Rủi ro lý thuyết: Shopee có trạng thái tên đúng là `all` (thực tế toàn tiếng Việt).
5. **`UrlState.Update` suy path từ `Nav.Uri` thay vì nhận path** (chữ ký trong plan không có path). Đã kiểm: `Program.cs` không gọi `UsePathBase` nên `AbsolutePath` == chuỗi hằng cũ. Nếu sau này hub chạy dưới path base thì cách này còn ĐÚNG HƠN bản cũ, nhưng đáng ghi nhớ.
6. **Chưa chạy thật trên trình duyệt** — nghiệm thu bằng build + test + đối chiếu khung HTML/dòng lệnh. Đáng soi tay khi có dịp: F5 giữ trạng thái ở 3 trang mới, và nhịp tick 2s không đè state của `DispatchOrdersTab` (component con duy nhất còn giữ state nội bộ; KHÔNG đụng tới trong đợt này).
7. Hai file `Fleet.razor` / `Dispatch.razor` được ghi lại với xuống dòng LF (repo `autocrlf` sẽ chuẩn hoá lúc commit) — chỉ là ghi chú, không ảnh hưởng nội dung.
