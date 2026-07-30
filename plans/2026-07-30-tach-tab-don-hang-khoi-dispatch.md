# Plan: Tách tab "Đơn hàng" ra khỏi Dispatch.razor

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành (đã nghiệm thu 31/07)
- **Người lập:** Fable · **Người thực thi:** opus-dev
- **Loại việc:** refactor thuần — **KHÔNG đổi một hành vi nào**

## 1. Bối cảnh

`server/Shopee.Hub.Web/Components/Pages/Dispatch.razor` dài **1600 dòng**, gấp đôi file lớn thứ nhì của
hub. Đợt refactor `1a5a7e6` đã bỏ qua nó với lý do *"callback/state/DB surface còn quá chặt"*.

**Đo lại thì lý do đó không đúng với tab Đơn hàng.** Vùng phương thức của tab này (dòng 1089–1271) và
markup của nó (dòng 428–539) dùng **đúng 0 lần** state của tab BigSeller (`_selMachine`, `_rows`,
`_visible`, `_budgets`, `_holds`, `_optStart`, `_fState`, `_fAcct`, `_confirmAcct`). Nó chỉ chạm:

- `Db` — đã inject sẵn, con tự inject được;
- `Snap` — snapshot fleet từ `FleetPageBase`;
- `HostName(machineId)` — hàm thuần của `(Snap, machineId)`;
- `OpBtn` — record dùng chung với tab BigSeller;
- `DispatchViewLogic` — lớp static dùng chung, đã có test.

Tức là **tách sạch được**, không phải gỡ rối.

### Mục tiêu

Đưa ~310 dòng của tab Đơn hàng sang component riêng. `Dispatch.razor` còn ~1290 dòng.
**Người dùng không được thấy bất kỳ khác biệt nào.**

## 2. Phạm vi

### Làm

- **Tạo** `server/Shopee.Hub.Web/Components/Shared/DispatchOrdersTab.razor`.
- **Sửa** `server/Shopee.Hub.Web/Components/Pages/Dispatch.razor` (bỏ phần đã chuyển đi, gọi component con).
- **Sửa** `server/Shopee.Hub.Web/Components/DispatchViewLogic.cs` — nhận `OpBtn`.
- **Sửa** `server/Shopee.Hub.Web/Components/FleetViewProjection.cs` — nhận `HostName`.

### Không làm

- **KHÔNG tách phần KPI** (markup dòng 16–241 + các hàm KPI). Phần đó tổng hợp số từ **cả hai** tab và có
  4 hành động gọi ngược — user đã chốt để lại.
- **KHÔNG đụng tab BigSeller.**
- Không đổi CSS, không đổi chữ hiển thị, không đổi route, không thêm/bớt tính năng.
- Không bump version, **không commit**, **không deploy**.

## 3. Ranh giới tách — chính xác từng thứ

### Chuyển sang component con

**Trường state** (tất cả đều chỉ tab Đơn hàng dùng):
`_shops`, `_orderSums`, `_shopIdByLogin`, `_ordersLoadedAt`, `_oHost`, `_oMachineMsg`, `_oResult`,
`_oMachines`, `_oAccounts`, `_oCmds`, `_oCounts`, `_oHolders`.

**Record:** `OrdersMachineCard`.

**Phương thức:** `ReloadOrders`, `ReloadOrdersLive`, `BuildOrdersMachines`, `DropOrdersMachine`,
`OrdersMachineTitle`, `SelectOrdersMachine`, `ToggleOrdersShops`, `OrdersStateCss`, `OrdersStateLabel`,
`OrdersActionLabel`, `OrdersWaitingOfShop`, `OrdersWaiting`, `PendingOrdersCmd`, `LastOrdersCmdError`,
`OrdersHolder`, `OrdersRunBtn`, `OnOrdersRunClick`, `OrdersEmptyText`.

**Markup:** nhánh `else` của `@if (_tab == "bs")` — dòng 428–539.

### Ở LẠI file cha (đừng chuyển nhầm)

- `_ordersRunning`, `ReloadOrdersRunning()`, `OrdersItem(...)` — **thuộc phần KPI**, không thuộc tab.
  Tên na ná nhau nên rất dễ chuyển nhầm; chuyển nhầm là KPI đứng ở tab BigSeller sẽ sai số.
- `_oMach` và `_oOpen` — xem mục URL bên dưới.
- Toàn bộ state/hàm của tab BigSeller và của KPI.

### Chuyển sang lớp dùng chung

- `OpBtn` → `DispatchViewLogic` (cả hai tab dùng; đổi thành `public`).
- `HostName(string)` → `FleetViewProjection` dưới dạng static thuần
  `HostName(FleetSnapshot snap, string machineId)` — đây đúng là chỗ chứa projection dùng chung giữa
  Dispatch và Fleet. Cha gọi lại chỗ cũ qua hàm mới, không giữ 2 bản.

## 4. Hợp đồng giữa cha và con

### Tham số

| Tham số | Kiểu | Vì sao |
|---|---|---|
| `Snap` | `FleetSnapshot` | Con không kế thừa `FleetPageBase`; cha truyền xuống mỗi nhịp |
| `SelectedMachine` | `string` | **State URL** (`omach`) — cha giữ, con nhận |
| `SelectedMachineChanged` | `EventCallback<string>` | Con đổi máy → báo cha để cha ghi URL |
| `OpenAccount` | `string` | **State URL** (`oacc`) — cha giữ, con nhận |
| `OpenAccountChanged` | `EventCallback<string>` | Con bung/thu tài khoản → báo cha |

Con tự `@inject HubDatabase Db`.

### BẤT BIẾN — làm hỏng là hỏng thật

1. **Mọi view-state phải nằm trong URL.** `?omach=` và `?oacc=` hiện có trong `UpdateUrl()` và
   `RestoreFromUrl()`. Sau khi tách, F5 hoặc dán link vẫn phải giữ **đúng máy đang chọn** và **đúng tài
   khoản đang bung**. Đây là nguyên tắc user đã chốt cho toàn bộ hub — không được làm rơi.
2. **Nhịp làm tươi giữ nguyên.** Hiện `Rebuild()` (chạy mỗi nhịp fleet 2s) làm:
   - nguồn chậm (`ReloadOrders` — bảng shop + bảng đơn): **10 giây một lượt**;
   - `ReloadOrdersLive()` rồi `BuildOrdersMachines()`: **mỗi nhịp**.
   Chuyển sang con thì đặt trong `OnParametersSet` (cha truyền `Snap` mới mỗi nhịp → con được gọi lại).
   Giữ nguyên cả ngưỡng 10 giây lẫn **thứ tự bắt buộc: gương trước, thẻ máy sau** (thẻ máy đọc số đếm
   của gương) — comment trong code đã ghi rõ, đọc rồi giữ.
3. **Chỉ chạy khi tab đang mở.** Hiện `if (_tab != "orders") return;` chặn phần này. Sau khi tách, cha
   chỉ render con khi `_tab == "orders"` là đủ — nhưng phải chắc con **không** tự nạp dữ liệu khi chưa
   hiện (đừng vô tình bỏ chặn rồi mỗi 2 giây lại nã DB dù đang ở tab kia).
4. **`SetTab("orders")` nạp ngay**, không chờ nhịp 2s (hiện gọi thẳng 3 hàm). Sau khi tách, con hiện ra
   là phải có dữ liệu ngay — không được chớp một nhịp rỗng rồi mới đầy.
5. **Không đổi chuỗi hiển thị, không đổi class CSS, không đổi thứ tự DOM.** Đây là refactor thuần.

## 5. Kiểm chứng

### Build & test

```text
dotnet build server/ShopeeHub.sln -c Debug
dotnet test  server/Shopee.Hub.Web.Tests
```

30 test hiện có phải xanh. Nếu chuyển `OpBtn`/`HostName` làm test hiện có phải sửa thì sửa cho khớp,
**không** nới lỏng khẳng định.

### Kiểm bằng mắt — BẮT BUỘC, vì trang này KHÔNG có test giao diện nào

Bộ test hub chỉ phủ tầng logic đã tách (`DispatchViewLogic`, `FleetViewProjection`) và tầng dữ liệu —
**markup của `Dispatch.razor` không có gì che**. Nên phải chạy thật:

1. Chạy hub **cục bộ**, `DataDir` trỏ thư mục **tạm** — **TUYỆT ĐỐI KHÔNG** đụng VM và không đụng DB
   thật. Tự tạo vài tài khoản/shop/máy giả trong DB tạm để trang có dữ liệu mà xem.
2. Mở `/dispatch`, chụp **tab BigSeller** và **tab Đơn hàng**. Đối chiếu với ảnh chụp **TRƯỚC** khi sửa
   (chụp trước, ở cùng dữ liệu) — bố cục phải **giống hệt**.
3. **Kiểm URL** (bất biến số 1): chọn một máy ở tab Đơn hàng, bung một tài khoản → URL phải có
   `?tab=orders&omach=…&oacc=…`; **F5** → vẫn đúng máy đó, đúng tài khoản đang bung.
4. **Kiểm nhịp**: đứng ở tab BigSeller ~30 giây, xác nhận **không** có truy vấn dữ liệu tab Đơn hàng nào
   chạy (thêm log tạm rồi bỏ đi, hoặc đặt breakpoint/đếm). Đây là bất biến số 3.
5. **Kiểm KPI**: đứng ở tab BigSeller, thẻ KPI vẫn đếm cả phiên Đơn hàng đang chạy (bất biến — chỗ dễ
   hỏng nhất nếu chuyển nhầm `ReloadOrdersRunning`).

### Đếm dòng

Báo số dòng `Dispatch.razor` trước/sau và số dòng file mới.

## 6. Tiêu chí nghiệm thu

- [ ] Build xanh, 30 test xanh.
- [ ] Ảnh chụp trước/sau của **cả hai** tab, bố cục không đổi.
- [ ] F5 giữ đúng `omach` + `oacc` (có ảnh hoặc mô tả từng bước đã bấm).
- [ ] Ở tab BigSeller không có truy vấn của tab Đơn hàng chạy nền (nêu cách đã kiểm).
- [ ] KPI vẫn đếm phiên Đơn hàng khi đang đứng ở tab BigSeller.
- [ ] `git status` chỉ có đúng 4 file trong phạm vi.
- [ ] `Dispatch.razor` giảm còn khoảng 1290 dòng.

## 7. Rủi ro & lưu ý

- **Không có lưới an toàn tự động cho markup** → phần "kiểm bằng mắt" là bắt buộc, không được báo cáo
  suông. Không chạy được thì nói thẳng là không chạy được và vì sao.
- **Bẫy dễ vấp nhất:** `ReloadOrdersRunning` (KPI) vs `ReloadOrdersLive` (tab). Tên gần giống, chuyển
  nhầm thì KPI sai mà build vẫn xanh, test vẫn xanh.
- **Bẫy thứ hai:** để con tự nạp dữ liệu khi chưa hiện → mỗi 2 giây nã DB vô ích.
- **Bẫy thứ ba:** quên `EventCallback` → chọn máy xong URL không đổi, F5 mất lựa chọn. Đúng cái nguyên
  tắc user đã chốt cho hub.
- Nếu thấy plan sai so với code thật (số dòng lệch, tên khác) thì **báo lại rồi mới làm**.

---

## Báo cáo thực thi

**Người thực thi:** opus-dev · **Ngày:** 2026-07-30 · **Kết quả:** làm xong toàn bộ phạm vi, chưa commit.

### File đã sửa/tạo (đúng 4 file, khớp `git status`)

| File | Thay đổi |
|---|---|
| `server/Shopee.Hub.Web/Components/Shared/DispatchOrdersTab.razor` | **TẠO** — 381 dòng. Toàn bộ markup + state + phương thức của tab Đơn hàng. |
| `server/Shopee.Hub.Web/Components/Pages/Dispatch.razor` | **1601 → 1270 dòng**. Bỏ phần đã chuyển; nhánh `else` giờ chỉ gọi `<DispatchOrdersTab …/>`. |
| `server/Shopee.Hub.Web/Components/DispatchViewLogic.cs` | +17 dòng: nhận `OpBtn` (public record, top-level cùng file — call site giữ nguyên `OpBtn`); kèm khối doc của `DispatchWorkItem` vốn bị mồ côi (nó đang bám nhờ vào `OpBtn`, chuyển `OpBtn` đi thì phải đưa doc về đúng chỗ). |
| `server/Shopee.Hub.Web/Components/FleetViewProjection.cs` | +10 dòng: nhận `HostName(FleetSnapshot, string)`. Cha gọi lại qua hàm mới (`private string HostName(id) => FleetViewProjection.HostName(Snap, id);`), KHÔNG giữ 2 bản. |

`Fleet.razor` **không đụng** — `HostName` bên đó khác luật (fallback `ShortId`, không phải id đầy đủ).

### Ranh giới tách — đã làm đúng danh sách

- Chuyển đi: `_shops`, `_orderSums`, `_shopIdByLogin`, `_ordersLoadedAt`, `_oHost`, `_oMachineMsg`, `_oResult`,
  `_oMachines`, `_oAccounts`, `_oCmds`, `_oCounts`, `_oHolders`; record `OrdersMachineCard`; 18 phương thức theo
  đúng danh sách plan; markup dòng 428–539. Kèm 2 thứ plan không liệt kê nhưng chỉ tab này dùng:
  `const WaitingStatus` và `const OrdersLeasePrefix`.
- Ở lại cha: `_ordersRunning`, `ReloadOrdersRunning()`, `OrdersItem(...)`, `OnOrdersStop(...)` (**bẫy số 1** —
  đã đối chiếu từng tên, `ReloadOrdersLive` mới là hàm của tab); `_oMach`, `_oOpen`; toàn bộ tab BigSeller + KPI.

### Build & test

```text
dotnet build server/ShopeeHub.sln -c Debug --no-incremental
  → Build succeeded. 0 Warning(s), 0 Error(s)
dotnet test server/Shopee.Hub.Web.Tests
  → Passed! Failed: 0, Passed: 30, Skipped: 0
```

Không phải sửa test nào (`OpBtn`/`HostName` không có test cũ chạm tới).

### Kiểm bằng mắt — CÓ chạy thật

Hub chạy **cục bộ** `127.0.0.1:8199`, `HUB_DATA_DIR` trỏ thư mục tạm trong scratchpad (DB rỗng, tự seed
4 máy / 2 acc BigSeller / 4 tk Đơn hàng / 9 đơn / 1 lease / 1 assignment qua chính API client). **Không đụng VM,
không đụng DB thật.** Chụp bằng Chromium (Playwright) 1440×1000.

Ảnh (thư mục scratchpad phiên `68d1c245-…/scratchpad/shots/`):

| Trước | Sau | Nội dung |
|---|---|---|
| `truoc-bs.png` | `cuoi-bs.png` | tab BigSeller |
| `truoc-orders.png` | `cuoi-orders.png` | tab Đơn hàng (mở từ URL có sẵn `omach`+`oacc`) |
| `truoc-tuongtac-sau-bam.png` | `cuoi-tuongtac-sau-bam.png` | sau khi bấm chọn máy + bung shop |
| `truoc-tuongtac-sau-f5.png` | `cuoi-tuongtac-sau-f5.png` | sau F5 |
| — | `sau-kpi-dangchay.png` | panel KPI "Việc đang chạy" mở từ tab BigSeller |

Ngoài ảnh còn **diff DOM thật** (`.dispatch` outerHTML lưu lại cả hai lượt, cùng bộ dữ liệu):

- tab BigSeller: **giống hệt từng byte**;
- tab Đơn hàng: chỉ khác thụt đầu dòng (markup ra khỏi khối `else` nên bớt 4 space — text node, không đổi hiển
  thị) và chuỗi "N phút trước" (thời gian thực trôi giữa 2 lượt chạy). Chuẩn hoá 2 thứ đó → **giống hệt**.

**Bất biến 1 — URL.** Trình tự bấm giống hệt nhau trước/sau:

```text
[1] vào /dispatch trần        -> (không có query)
[2] bấm tab Đơn hàng          -> ?tab=orders
[3] chọn máy PC-ALPHA         -> ?tab=orders&omach=pc-alpha%3Aorders
[4] bung shop của 1 tài khoản -> ?tab=orders&omach=pc-alpha%3Aorders&oacc=seller.one%40example.com
[5] F5                        -> URL giữ nguyên; máy đang chọn = PC-ALPHA; chip shop đang bung = Shop One / Shop Two
[6] bấm lại thẻ máy = bỏ chọn -> ?tab=orders
```

**Bất biến 3 — chỉ chạy khi tab đang mở.** Thêm `Console.WriteLine` TẠM vào `OnParametersSet` của con, đứng ở
tab BigSeller 30 giây rồi sang tab Đơn hàng 30 giây, đếm dòng log theo mốc thời gian:

```text
17:24:05 → 17:24:35 (đứng ở tab BigSeller):  0 lần
17:24:35 → 17:25:05 (đứng ở tab Đơn hàng): 16 lần   (≈ nhịp 2s + 1 lần lúc vừa mở tab)
```

Dòng log tạm **đã gỡ**, build lại sạch (`grep` xác nhận không còn `TAM-KIEM-NHIP`/`Console.WriteLine`).

**Bất biến 5 — KPI.** Đứng ở tab BigSeller: thẻ đọc `4 Máy online | 2 Việc đang chạy | 2 Việc chờ | 0 Việc gián
đoạn` — **giống hệt ảnh trước**. Mở thẻ "Việc đang chạy" (vẫn ở tab BigSeller), bảng chi tiết liệt kê đúng 2
phiên Đơn hàng kèm nút ✖ Dừng:

```text
📦 Đơn hàng — seller.one@example.com  PC-ALPHA  cập nhật 3 phút trước — ✖ Dừng
📦 Đơn hàng — seller.four@example.com PC-GAMMA  cập nhật 3 phút trước — ✖ Dừng
```

Dữ liệu seed chỉ có 1 việc BigSeller (đang *chờ*, 0 đang chạy) → con số "2 đang chạy" **chỉ có thể** đến từ phiên
Đơn hàng ⇒ `ReloadOrdersRunning`/`OrdersItem` đúng là còn ở cha và còn chạy.

### Lỗi đã vấp trong lúc làm (ghi lại cho lần sau)

Lượt chụp "sau" đầu tiên ra bảng RỖNG. Nguyên nhân: `<DispatchOrdersTab SelectedMachine="_oMach" …>` — tham số
**kiểu string mà thiếu `@`** thì Razor coi là chuỗi HẰNG, con nhận đúng chuỗi `"_oMach"`. Build xanh, test xanh,
chỉ lộ khi chạy thật. Đã sửa thành `SelectedMachine="@_oMach"` / `OpenAccount="@_oOpen"` và ghi chú ngay tại chỗ.

### Điểm lệch so với plan / cần phiên chính soi

1. **Con inject thêm `FleetStateService`** (plan chỉ nói `HubDatabase`). Bắt buộc: `OnOrdersRunClick` gọi
   `FleetState.Refresh()`. Không phải state, chỉ là service.
2. **`OpBtn` để `public`** đúng như plan ghi, nhưng nó nằm cạnh `DispatchKpiCard`/`DispatchWorkItem` đang là
   `internal`, và không có nhu cầu public nào (test đã có `InternalsVisibleTo`). Đề nghị cân nhắc hạ về
   `internal` cho đồng bộ — 1 chữ.
3. **Đường TỰ bỏ chọn cố ý KHÔNG báo lên cha** (`DropOrdersMachine` khi máy offline, và thu gọn tài khoản trong
   `ReloadOrdersLive`). Lý do: hai đường này chạy cả trong lượt render đầu (còn prerender), mà cha ghi URL bằng
   `NavigateTo` — navigate lúc prerender là ném redirect (chính cái bẫy `RestoreFromUrl` đã ghi chú). Bản gộp cũ
   cũng không ghi URL ở hai đường này. **Hệ quả:** sau một lượt tự-bỏ-chọn, `_oMach` bên cha còn giá trị cũ nên
   một thao tác *khác* sau đó có thể ghi `omach=<máy đã rụng>` vào URL. Vô hại về hiển thị (F5 bỏ qua máy offline,
   `RestoreFromUrl` đã lọc sẵn) nhưng là điểm duy nhất lệch bản cũ — nói ra để soi.
4. **Con giữ bản chiếu `_oMach`/`_oOpen`, chỉ nhận lại khi cha đổi THẬT** (`_lastSelectedMachine`/`_lastOpenAccount`).
   Đây là hệ quả bắt buộc của mục 3: nhận vô điều kiện mỗi nhịp thì cha sẽ đưa lại đúng cái máy con vừa bỏ.
5. **Một thao tác của người dùng nay ghi URL 2 lượt** (`SelectedMachineChanged` rồi `OpenAccountChanged`, mỗi cái
   một `UpdateUrl`), thay vì 1 lượt như bản gộp. Cả hai đều `replace:true` nên không đẻ history, URL cuối đúng.
6. **Bảng tab Đơn hàng nạp thêm ở vài nhịp cha re-render vì lý do khác** (vd bấm nút trong panel KPI trong lúc
   đang mở tab Đơn hàng): bản cũ chỉ nạp theo nhịp fleet. Chỉ là vài query nhỏ, không đổi hiển thị.
7. `git status` lúc nhận việc (ảnh chụp đầu phiên) có 4 file `orders/…` đang sửa dở; lúc làm xong chúng **không
   còn** trong `git status`. Tôi không đụng thư mục đó — nhiều khả năng phiên chính đã commit chúng, nhưng nêu ra
   để đối chiếu.
