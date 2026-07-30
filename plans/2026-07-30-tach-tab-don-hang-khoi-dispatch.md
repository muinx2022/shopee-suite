# Plan: Tách tab "Đơn hàng" ra khỏi Dispatch.razor

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
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

<Để trống — người thực thi điền.>
