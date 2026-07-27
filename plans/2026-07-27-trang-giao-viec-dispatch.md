# Plan: Trang Giao việc (/dispatch) trên Hub web

- **Ngày:** 2026-07-27
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Hiện việc giao việc cho fleet chỉ có ở trang Fleet (`/`, `Components/Pages/Fleet.razor`, 1241 dòng) theo kiểu
master-detail: **chọn 1 shop ở cột trái → panel "Ghim việc" bên phải → chọn Op + Máy + dòng → bấm 📌 Ghim**.
Người dùng phản ánh cách này khó dùng khi có nhiều shop. Khảo sát xác nhận 5 điểm đau:

1. **Mỗi lượt giao chỉ được 1 shop × 1 op.** N shop × 3 op = N×3 lượt bấm sâu, không có chọn nhiều.
2. **Operator phải tự cân tải trong đầu.** Dropdown máy (Fleet.razor:199-208) chỉ hiện 🟢/⚪ online/offline, không
   hiện máy đang chạy mấy việc hay còn bao nhiêu quỹ Brave — dù `MachinePresence.MaxBrave` đã có sẵn từ heartbeat.
3. **Ràng buộc lộ ra quá muộn.** Chọn máy sai rồi mới hiện cảnh báo `_pinBlocked` "máy này KHÁC máy đang giữ acc".
4. **Footgun thật:** `SelectShop` (Fleet.razor:779-792) KHÔNG reset `_pinStart/_pinEnd/_pinOp/_pinMachine` → đổi
   sang shop khác vẫn giữ dải dòng của shop trước, bấm Ghim là giao nhầm phạm vi.
5. **Không có toàn cảnh.** Muốn biết "shop nào chưa scrape" phải bấm từng shop xem bảng 4 op.

Ngoài ra, **phần Đơn hàng (Shopee) chưa có giao việc gì cả**: hub chỉ NHẬN dữ liệu lên (`POST /api/orders/push`,
`/api/orders/slip`, account-lease qua `ReserveAccounts` với khoá `orders:<login>`) chứ không giao việc xuống;
`OrdersModuleHost` phía client không hề poll assignment.

**Đã chốt với người dùng:**
- Làm **trang mới `/dispatch`** + **một mục nav bên trái** để truy cập; Fleet giữ nguyên làm trang xem sâu 1 shop.
- Đơn vị việc của phần Đơn hàng là **tài khoản Shopee (login subaccount)**, KHÔNG phải shop — vì shop chỉ lộ ra
  sau khi đăng nhập subaccount, chia shop cho 2 máy = 2 máy cùng login một tài khoản, xung đột với khoá lease
  v1.6.5 và giới hạn cầu nối WS (1 tài khoản/lúc/máy).
- Mock tham chiếu đã duyệt: bảng + tick nhiều dòng + thanh hành động hàng loạt có "⚖ Tự cân tải" kèm xem trước.

**Mục tiêu plan này (đợt 1):** trang `/dispatch` chạy được với **dữ liệu thật** cho phần BigSeller (giao việc thật),
và tab Đơn hàng hiển thị read-only trung thực. Backend giao việc cho Đơn hàng để plan sau.

## 2. Phạm vi

**Làm:**
- Trang mới `Components/Pages/Dispatch.razor` (`@page "/dispatch"`), 2 tab: BigSeller (theo shop) + Đơn hàng (theo tài khoản).
- Tab BigSeller: lọc + chọn nhiều dòng + thanh hành động hàng loạt + tự cân tải (có xem trước) + **giao việc thật**
  (tạo assignment như `Fleet.Pin()`), menu hành động ngay tại ô trạng thái.
- Service thuần `Services/DispatchBalancer.cs` (thuật toán chia máy, không phụ thuộc Blazor/DB → test được).
- Tab Đơn hàng: read-only từ dữ liệu hub đang có + banner nói rõ chưa giao việc được.
- Mục nav "🎯 Giao việc" trong `MainLayout.razor` + tiêu đề trang.
- CSS cho trang mới trong `wwwroot/app.css` + bump `app.css?v=N` ở `Components/App.razor`.
- Test đơn vị cho `DispatchBalancer`.

**Không làm (đợt sau):**
- KHÔNG sửa `Fleet.razor` (kể cả footgun dải dòng — sẽ vá ở plan riêng để đợt này không đụng trang đang chạy production).
- KHÔNG làm backend giao việc cho Đơn hàng (endpoint đẩy danh bạ tài khoản, assignment `op='orders'`, client poll).
- KHÔNG làm view "theo máy" (lane) — chờ người dùng đánh giá bảng phẳng trước.
- KHÔNG deploy lên VM (Fable tự deploy sau khi nghiệm thu).

## 3. Các bước thực hiện

### Bước 1 — `Services/DispatchBalancer.cs` (mới)

Thuật toán chia việc, viết thuần (static, không inject gì) để test được:

```csharp
public sealed record DispatchTarget(string AccountId, string ShopId, string Sheet, string ShopName);
public sealed record MachineBudget(string MachineId, string Hostname, bool Online, int Free, int Running);
public sealed record BalancePlan(
    Dictionary<string, List<DispatchTarget>> ByMachine,   // machineId -> shop được giao
    List<string> Skipped);                                 // lý do bỏ qua, hiển thị cho operator
```

`public static BalancePlan Balance(IReadOnlyList<DispatchTarget> targets, IReadOnlyList<MachineBudget> machines,
IReadOnlyDictionary<string,string> holds, IReadOnlyDictionary<string,string> homes)`

Luật (theo đúng thứ tự):
1. **Nhóm target theo `AccountId`** — một tài khoản BigSeller chỉ chạy trên MỘT máy tại một thời điểm, nên cả nhóm
   phải về cùng một máy.
2. Máy đích của nhóm, chọn theo thứ tự ưu tiên:
   a. `holds[accountId]` (máy đang giữ acc — từ `account_leases`) nếu máy đó **online**;
   b. `homes[accountId]` (affinity trusted-device — từ `account_home`) nếu online và còn quỹ (`Free > 0`);
   c. máy online có `Free` lớn nhất (tie-break: `MachineId` ordinal, để kết quả **tất định** — test dựa vào điều này).
3. Không có máy nào thoả → thêm vào `Skipped` với lý do người-đọc-được, ví dụ
   `"kho1 (4 shop): không máy nào online còn quỹ"` hoặc `"kho2 (2 shop): máy PC-02 đang giữ acc nhưng đang offline"`.
4. Mỗi nhóm acc được giao thì trừ **1** khỏi `Free` của máy đó (một acc chiếm một slot, không phải mỗi shop một slot).
5. Nhánh (a) KHÔNG kiểm tra `Free`: máy đang giữ acc là máy DUY NHẤT hợp lệ, hết quỹ vẫn phải xếp vào đó
   (client tự xếp hàng) — nhưng ghi thêm cảnh báo vào `Skipped` dạng `"kho1: PC-01 đã hết quỹ Brave, việc sẽ phải chờ"`.

### Bước 2 — `Components/Pages/Dispatch.razor` (mới)

`@page "/dispatch"`, `@attribute [Authorize(Policy = "Web")]`, `@inherits FleetPageBase` (tự bám snapshot 2s),
inject `FileStoreConfigService Config`, `HubDatabase Db`, `NavigationManager Nav`.

**Nguồn dữ liệu (dùng lại, KHÔNG chép logic Rebuild của Fleet):**
- Dòng bảng BigSeller = duyệt `Config.BigSellerAccounts()` → mỗi `acct.Shops` có `ShopeeDataSheet` khác rỗng.
  (Shop chưa gán sheet thì không giao việc được → không đưa vào bảng này; hiện đếm số bị ẩn ở dòng hint.)
- Trạng thái mỗi op: `FleetStateService.OpCell(Snap, accountId, shopId, op)` — trả `Text/Css/Kind`, `Locked` =
  đang có máy chạy.
- Máy: `Snap.Machines` (`MachineId`, `Hostname`, `LastSeen`, `MaxBrave`), offline check bằng
  `FleetStateService.MachineOffline(Snap, machineId)`.
- Quỹ máy: `Free = (MaxBrave > 0 ? MaxBrave : 2) - (số assignment status queued/running gắn máy đó trong Snap.Assignments)`.
  `MaxBrave = 0` nghĩa là máy chưa báo → coi như 2 (ghi comment giải thích).
- Hold: `Snap.AccountLeases` → `accountId -> machineId`. **Bỏ qua các bản ghi có `AccountId` bắt đầu bằng `"orders:"`**
  — đó là khoá của module Đơn hàng, không phải acc BigSeller.
- Home: `Db.AccountHomes()` → `accountId -> machineId`.

**Bố cục** (theo mock đã duyệt, dùng lại class sẵn có của `app.css`: `.kpis/.kpi`, `.tabs`, `.bar`, `.btn`,
`.tablewrap`, `table.grid`, `.pill`, `.hint`):

1. Hàng KPI 4 thẻ: Máy online / Việc đang chạy / Việc chờ / Việc gián đoạn (đếm từ `Snap`).
2. `.tabs`: `🛒 BigSeller — theo shop` | `📦 Đơn hàng — theo tài khoản`.
3. Thanh lọc: select Tài khoản, select Máy, 4 chip `Chưa xong | Đang chạy | Gián đoạn | Tất cả` (mặc định
   **Chưa xong**), ô tìm shop.
4. Hàng chọn nhanh: `Mọi shop chưa scrape` · `Chưa import` · `Mọi việc gián đoạn` · `Bỏ chọn hết`.
   (Nút chọn theo op cũng set luôn `_bulkOp` cho khớp.)
5. Bảng: cột `[checkbox] | Shop | Scrape | Import | Update | Máy đang giữ | Cập nhật`, có **dòng nhóm tài khoản**
   (`tr.acctrow`) ghi `tên acc · N shop · đang do <máy> giữ`. Checkbox "chọn tất cả" ở header chỉ áp cho **dòng
   đang hiện sau bộ lọc**.
6. Ô op là `<button class="opcell">` chứa pill; bấm → mở menu ngay dưới ô (Blazor state, KHÔNG dùng JS):
   `▶ Giao cho <máy>` (chỉ liệt kê máy hợp lệ với acc đó) · `✓ Đánh dấu xong` · `↺ Reset về chưa chạy` ·
   `📄 Xem log` (đợt này chỉ điều hướng sang `/logs-view`). Hai mục đặt-tay dùng lại đúng API mà Fleet dùng cho
   combo `.cellset` (`SetLedger` → tìm trong Fleet.razor, gọi cùng phương thức `HubDatabase`); nếu ô đang `Locked`
   thì ẩn hai mục đặt-tay.
7. Thanh hành động hàng loạt (sticky đáy, chỉ hiện khi có ≥1 dòng tick): số dòng đã chọn · select Op ·
   select Máy (mục đầu `⚖ Tự cân tải`) · nút `⚙ Tuỳ chọn` (gập/mở hàng tham số: từ dòng, đến dòng, số process,
   số tk/khung, reload — mặc định 0) · `Bỏ chọn` · `📌 Giao việc`.
   - Dòng **xem trước** ngay dưới, cập nhật theo lựa chọn: `PC-01 ← 3 shop · KHO-03 ← 2 shop · bỏ qua: …`.
   - Select máy: mỗi option hiện tải thật `🟢 PC-01 · 2/6 brave · 1 việc`; option **disabled kèm lý do ngay trong
     text** khi máy offline, hết quỹ, hoặc vi phạm 1-acc-1-máy với tập đang chọn
     (`— acc kho1 đang do PC-01 giữ`).
   - Nút `📌 Giao việc` dùng **xác nhận 2 bước** theo pattern repo (bấm lần đầu đổi thành
     `Bấm lần nữa để giao N việc cho M máy`), tránh giao nhầm.
8. **Mọi view-state vào URL** (nguyên tắc đã chốt của hub): `?tab=&f=&acct=&mac=&q=`. F5/chia sẻ link phải giữ
   nguyên. Làm theo đúng pattern `UpdateUrl()`/`RestoreSelectionFromUrl()` trong `Fleet.razor`
   (dùng `Nav.GetUriWithQueryParameters`, `replace: true`, KHÔNG navigate trong `OnInitialized` vì còn prerender).

**Giao việc thật** — mirror `Fleet.Pin()` (Fleet.razor:1077-1095) cho từng dòng đã tick:

```csharp
Db.CreateAssignment(new CreateAssignmentRequest(
    row.AccountId, row.ShopId, row.Sheet, op, machineId, Pinned: true,
    Math.Max(0, start), Math.Max(0, end), payload,
    Processes: Math.Max(0, procs),
    FrameSize: op == "scrape" ? Math.Max(0, frame) : 0,
    ReloadSeconds: op is "import" or "update" ? Math.Max(0, reload) : 0));
```
`payload` = `JsonSerializer.Serialize(new ImportJobPayload { FromClaimedTab = ... })` khi op = import, ngược lại "".
Sau vòng lặp gọi `FleetState.Refresh()` một lần (KHÔNG gọi trong vòng lặp), bỏ tick, và hiện dòng kết quả
`✔ Đã giao N việc <op>: PC-01 ← 3, KHO-03 ← 2` (kèm `⚠` cho máy offline như Fleet đang làm).

**Hiệu năng:** bảng vẽ lại mỗi nhịp fleet 2s. Danh sách tick giữ trong `HashSet<string>` key `acct__shop`, và
`OnFleetTick` phải **giữ nguyên** tick khi dòng vẫn còn sau khi rebuild; dòng biến mất khỏi bộ lọc thì bỏ khỏi tick.

### Bước 3 — Tab Đơn hàng (read-only, trung thực)

Hub CHƯA có danh bạ tài khoản Shopee (chỉ biết **shop con** qua đường push). Vì vậy đợt này tab hiển thị:

- Banner `.warn`: "Giao việc cho Đơn hàng chưa bật — client hiện chỉ đẩy đơn lên hub, chưa nhận việc từ hub.
  Đơn vị việc sẽ là **tài khoản Shopee**; phần giao việc làm ở đợt sau."
- Khối 1 — **Tài khoản đang chạy**: đọc `Snap.AccountLeases` lọc `AccountId.StartsWith("orders:")` → hiện
  `login (bỏ tiền tố) · máy · nhịp cuối`. Rỗng thì hiện dòng "hiện không có tài khoản nào đang chạy".
- Khối 2 — **Shop & đơn chờ**: `Db.ListShops()` + số đơn "Chờ lấy hàng" mỗi shop
  (dùng lại API đếm sẵn có trong `HubDatabase.Orders.cs` — tìm `PrepareStatRow`/`CountOrders`; nếu chưa có hàm
  đếm theo shop thì thêm một hàm đọc thuần trong `HubDatabase.Orders.cs`, KHÔNG sửa hàm đang dùng).
  Cột: Shop · Đơn chờ lấy hàng · Đơn có phiếu · Sync cuối.
- Không có checkbox, không có thanh bulk ở tab này.

### Bước 4 — Nav + tiêu đề + icon

- `Components/Layout/MainLayout.razor`: thêm dưới mục Fleet trong nhóm **Điều khiển**:
  `<NavLink class="nav" href="/dispatch"><span class="nav-ic">@HubIcons.Svg("dispatch")</span><span>Giao việc</span></NavLink>`
- Thêm `"dispatch" => "Giao việc"` vào `UpdateTitle()`.
- `Components/HubIcons.cs`: thêm key `"dispatch"` — icon stroke 24x24 hợp bộ (gợi ý: hình bia/target hoặc mũi tên
  rẽ nhánh), theo đúng định dạng chuỗi path của các key sẵn có.

### Bước 5 — CSS

Thêm vào cuối `wwwroot/app.css` một khối có comment mở đầu `/* ===== Trang Giao việc (/dispatch) ===== */`:
`.dispatch`, `.opcell`, `.opmenu`, `.bulkbar`, `.bulkbar .preview`, `.plan`, `.chip`, `tr.picked`.

Ràng buộc:
- **Bám token sẵn có** (`--card/--stroke/--primary/--info-bg/…`), không hardcode màu mới ngoài token.
- Phải chạy đúng cả nền tối: theme tối của hub dùng `html.dark` → mọi override tối viết dạng `html.dark .xxx`.
- `.bulkbar` sticky đáy: `position: fixed; left: 250px; right: 0; bottom: 0` — và **có media query ≤900px**
  đưa `left: 0` (sidebar ẩn ở mobile), thêm `padding-bottom` cho `.content` khỏi bị thanh che dòng cuối.
- Bảng nằm trong `.tablewrap` (đã có `overflow-x: auto`) — body trang KHÔNG được cuộn ngang.
- **Không** dùng selector `:has(.fleetpage)` hay đụng vào khối CSS của Fleet.
- Bump `app.css?v=N` trong `Components/App.razor` (tăng 1).

### Bước 6 — Test

Thêm test cho `DispatchBalancer` (đặt cùng project test đang có của hub nếu có; chưa có thì tạo tối thiểu, hỏi lại
trước khi thêm project mới):
- Nhóm 2 shop cùng acc → **cùng một máy**.
- Acc đang bị `holds` giữ bởi PC-01 → luôn ra PC-01 kể cả PC-02 rảnh hơn.
- Acc có `homes` PC-02, không hold → ra PC-02 khi PC-02 online còn quỹ.
- Máy nhà offline → rơi xuống máy online quỹ nhiều nhất.
- Không máy nào online → `ByMachine` rỗng, `Skipped` có lý do.
- Trừ quỹ theo **nhóm acc** chứ không theo shop (2 acc × 3 shop vào máy `Free = 2` → cả 2 nhóm vào được).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build server/Shopee.Hub.Web` sạch, **0 warning mới**.
- [ ] `dotnet test` (project test của hub) xanh, gồm các test `DispatchBalancer` mới.
- [ ] Chạy `dotnet run --project server/Shopee.Hub.Web`, mở `/dispatch`: nav trái có mục **Giao việc**, bấm vào ra
      đúng trang, tiêu đề topbar hiện "Giao việc".
- [ ] Tick vài shop → thanh hành động hiện ở đáy; đổi giữa `⚖ Tự cân tải` và máy cụ thể → dòng xem trước đổi theo.
- [ ] Với tập chọn chứa acc đang bị máy khác giữ: option máy vi phạm **disabled và có lý do trong text option**.
- [ ] Bấm `📌 Giao việc` → phải bấm lần thứ hai mới chạy; sau khi chạy, sang trang Fleet thấy đúng các shop đó có
      việc `queued` gắn đúng máy (kiểm bằng `Snap.Assignments`/giao diện Fleet).
- [ ] F5 sau khi đổi tab/bộ lọc → giữ nguyên trạng thái (state nằm trong URL).
- [ ] Thu hẹp cửa sổ xuống ~400px: bảng cuộn ngang trong khung của nó, **body không cuộn ngang**, thanh hành động
      không che mất dòng cuối bảng.
- [ ] Bật nền tối: mọi thành phần mới đọc được, không có mảng trắng/chữ chìm.
- [ ] Tab Đơn hàng hiển thị banner + 2 khối dữ liệu thật, không crash khi bảng `orders` rỗng.
- [ ] `Fleet.razor` **không bị sửa** (`git diff --stat` không có tên file này).

## 5. Rủi ro & lưu ý

- **Đây là trang giao việc THẬT vào production fleet.** Nút Giao việc tạo assignment thật → bắt buộc xác nhận 2 bước.
  Khi tự test, chỉ giao cho máy đang offline hoặc huỷ ngay sau khi kiểm tra để không làm phiền fleet đang chạy.
- **Đừng chép logic `Rebuild()` của Fleet.** Fleet phải overlay ledger để hiện cả shop mồ côi; trang Giao việc chỉ
  cần shop trong config có sheet. Chép sang sẽ tạo bản sao logic thứ hai phải nuôi song song.
- `Snap.AccountLeases` chứa CẢ khoá của module Đơn hàng (`orders:<login>`) — quên lọc là ràng buộc 1-acc-1-máy của
  BigSeller sẽ tính sai.
- `MaxBrave = 0` là "máy chưa báo", không phải "không có quỹ" — coi như 2 và ghi comment.
- Trang bám nhịp fleet 2s: mọi state người dùng đang thao tác (tick, ô đang gõ, menu đang mở) phải **sống sót** qua
  `OnFleetTick`. Nếu menu ô bị nháy/đóng theo nhịp thì override `ShouldTickRender()` trả `false` khi menu đang mở
  (đúng cách Fleet làm với modal heatmap).
- Repo có bẫy đã dính nhiều lần với `<select>` uncontrolled trong vòng lặp Blazor (xem comment ở Fleet.razor:149-151):
  nếu dùng select trong ô bảng thì phải `@key` theo khoá dòng.
- Không tự deploy lên VM; Fable deploy sau khi nghiệm thu.

---

## Báo cáo thực thi

**File tạo mới:** `Services/DispatchBalancer.cs`, `Components/Pages/Dispatch.razor` (830 dòng),
`orders/XuLyDonShopee.Tests/DispatchBalancerTests.cs` (12 test).
**File sửa:** `Data/HubDatabase.Orders.cs` (thêm `ShopOrderSummary` + `ShopOrderSummaries`), `Layout/MainLayout.razor`,
`HubIcons.cs`, `wwwroot/app.css`, `Components/App.razor` (v=28), `XuLyDonShopee.Tests.csproj` (LINK file balancer).
`Fleet.razor` không bị sửa (đã kiểm bằng `git status`).

**Nghiệm thu (Fable tự chạy, không dựa vào báo cáo):**
- `dotnet build ShopeeSuite.sln` → Build succeeded, **0 Warning, 0 Error**.
- `dotnet test orders/XuLyDonShopee.Tests` → **Passed! 1056/1056**.
- Đọc lại toàn bộ `DispatchBalancer.cs` + `Dispatch.razor`: đúng luật đã mô tả trong plan.

**3 chỗ plan SAI, Opus sửa đúng — đã kiểm chứng lại trong code:**
1. **Nguồn `holds`.** Plan bảo lấy từ `Snap.AccountLeases` (lọc tiền tố `orders:`). SAI: bảng `account_leases` khoá
   **acc Shopee** (và khoá `orders:<login>` của module Đơn hàng), khác hẳn không gian Id acc BigSeller. Luật đúng là
   `Snap.Leases` + assignment `running` — chính là `HubDatabase.AccountOwnersLocked` (Assignments.cs:323) và
   `Fleet.OwnerOf` (Fleet.razor:1194). Opus đã dùng đúng nguồn này.
2. **Breakpoint mobile.** Plan ghi ≤900px sidebar ẩn → `left: 0`. SAI: repo co sidebar thành **icon-rail 64px** ở
   **920px** (app.css:338-342). Opus dùng đúng 920px/64px.
3. **Menu ô op.** Plan ngụ ý popover định vị tuyệt đối. Không khả thi: `.tablewrap` có `overflow-x: auto` sẽ cắt cụt
   popover, mà plan lại cấm dùng JS định vị. Opus làm **dòng phụ ngay dưới dòng shop** — hợp lý hơn.

**Điểm còn tồn (không chặn phát hành):** nhánh (b) `homes` của balancer gần như không bao giờ bắn — bảng
`account_home` keyed theo acc **Shopee** (`ScrapeViewModel` đẩy `ShopeeAccount.Id`), không phải acc BigSeller. Code
vẫn wire + có test, chờ quyết định: bỏ wiring hay làm bảng affinity riêng cho acc BigSeller.
Hub chưa có project test riêng → test balancer đặt nhờ ở `orders/XuLyDonShopee.Tests` bằng cách LINK file nguồn
(đúng khuôn `Shopee.Hub.Web.csproj` đang dùng). Cân nhắc lập `server/Shopee.Hub.Tests` ở đợt sau.
