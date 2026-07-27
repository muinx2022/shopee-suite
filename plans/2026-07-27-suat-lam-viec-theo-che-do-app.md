# Plan: Suất làm việc theo chế độ app (Workspace / Đơn hàng) — đợt 1: nền tảng

- **Ngày:** 2026-07-27
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

App có 3 chế độ (`Shopee.Core/Infrastructure/AppModeStore.cs`): **Full** (Workspace + Đơn hàng), **Workspace**
(chỉ BigSeller), **Shopee** (chỉ Đơn hàng). Chế độ đọc từ `%AppData%\ShopeeSuite\app-mode.json`, hoặc tham số
`--mode X` (shortcut) thắng file.

**Hiện trạng — hub KHÔNG thấy máy chạy chế độ Shopee.** Đây là cố ý, không phải bug:
`suite/Shopee.Suite/App.axaml.cs:49-68` rẽ nhánh — Full/Workspace gọi `CoordinationRuntime.InitFromConfig()`
(dựng `HttpCoordinationHub` → có heartbeat → đăng ký máy), còn Shopee gọi `InitClientOnlyFromConfig()` (chỉ
`HubClient` để đẩy đơn/phiếu, **không heartbeat, không đăng ký máy**). Lý do ghi ở
`CoordinationRuntime.cs:48-54`: `machine.json` nằm ở `%AppData%\ShopeeSuite\` nên **mọi bản chạy trên cùng một PC
dùng chung một `machine_id`** → bản Shopee sẽ tranh danh tính máy + lease với bản Workspace chạy song song.

Hệ quả kèm theo: máy chế độ Shopee **không nhận được lệnh update app từ hub** (lệnh đi trong phản hồi heartbeat,
`MachineHeartbeatResponse.UpdateRequestedAt`) và không hiện ở trang Máy client.

**Mô hình đã chốt với người dùng — "suất làm việc" (slot):** mỗi PC có **hai loại suất**, mỗi suất là một client
dưới mắt hub. Full chiếm **cả hai suất** → hub thấy 2 client.

| Suất | Ai chiếm | `machine_id` gửi lên hub |
|---|---|---|
| **Workspace** (việc BigSeller) | mode `Workspace`, mode `Full` | `<id-máy>` — **GIỮ NGUYÊN, không hậu tố** |
| **Đơn hàng** (việc Shopee) | mode `Shopee`, mode `Full` | `<id-máy>:orders` |

**BẤT BIẾN quan trọng:** suất Workspace phải giữ đúng `machine_id` cũ. Đổi nó là đứt mọi dữ liệu đang khoá theo
`machine_id`: `ledger.last_machine_id`, `account_home.machine_id`, `assignments.claimed_by`/`target_machine_id`,
`leases.machine_id`, `search_products.machine_id`, `machine_roles`. Chỉ suất MỚI (đơn hàng) mang hậu tố.

**Người dùng đã xác nhận:** có (hoặc sẽ có) PC chạy hai bản song song → bắt buộc tách danh tính như trên.

**Điểm phải giữ đúng:** Full = 2 client **về điều phối**, nhưng vẫn là **1 process / 1 CPU / 1 quỹ Brave**. Hub
KHÔNG được coi 2 suất là 2 máy độc lập rồi giao gấp đôi việc → heartbeat mang thêm `HostId` (id PC thật) để hub
gộp quỹ theo PC.

## 2. Phạm vi

**Làm (đợt 1 — nền tảng):**
- DTO heartbeat mang thêm `Mode`, `Kind` (loại suất), `HostId`.
- Hub: cột `machines.mode` / `machines.kind` / `machines.host_id` + `MachinePresence` tương ứng, tương thích ngược
  với client cũ.
- Chế độ **Shopee**: bắt đầu heartbeat bằng suất `:orders` (đăng ký máy + nhận lệnh update), **KHÔNG** claim
  assignment, **KHÔNG** giành lease BigSeller.
- Chế độ **Full**: heartbeat CẢ HAI suất từ cùng một process.
- Khoá acc đơn hàng (`orders:<login>`) đổi sang dùng id suất đơn hàng.
- UI hub: trang Máy client + thẻ máy ở /dispatch hiện mode/loại suất, gộp hai suất cùng `HostId` thành một cụm.
- Hub chặn giao việc BigSeller cho suất đơn hàng (việc sẽ nằm `queued` mãi vì suất đó không claim).

**Không làm (để đợt 2):**
- `BusyBrave` (báo quỹ Brave thật) và luật ưu tiên đơn hàng trên máy Full.
- Backend giao việc cho module Đơn hàng (assignment `op='orders'`).
- Phát hiện/xử lý xung đột khi hai bản cùng đòi một suất (chỉ ghi log ở đợt này).
- KHÔNG deploy, KHÔNG release client (Fable làm sau khi nghiệm thu).

## 3. Các bước thực hiện

### Bước 1 — Định danh suất (`suite/Shopee.Core`)

Thêm vào `Coordination/` một chỗ duy nhất sinh id suất (đừng rải chuỗi `":orders"` khắp nơi):

```csharp
public static class MachineSlots
{
    public const string Workspace = "workspace";
    public const string Orders    = "orders";
    /// <summary>Hậu tố suất đơn hàng. Suất Workspace GIỮ NGUYÊN id máy (không hậu tố) — đổi là đứt mọi dữ liệu
    /// đang khoá theo machine_id (ledger/account_home/assignments/leases/search).</summary>
    public const string OrdersSuffix = ":orders";
    public static string SlotId(string hostId, string kind) =>
        kind == Orders ? hostId + OrdersSuffix : hostId;
    public static string HostOf(string slotId) =>
        slotId.EndsWith(OrdersSuffix, StringComparison.Ordinal) ? slotId[..^OrdersSuffix.Length] : slotId;
}
```

`HostId` = `MachineIdentity.Shared.MachineId` (không đổi `machine.json`).

### Bước 2 — DTO heartbeat (`Coordination/HubDtos.cs`)

`MachineHeartbeatRequest` hiện là `(string MachineId, string Hostname, string? AppVersion, int MaxBrave = 0)`.
Thêm 3 field **có giá trị mặc định** để client cũ không gửi vẫn hợp lệ:

```csharp
public sealed record MachineHeartbeatRequest(
    string MachineId, string Hostname, string? AppVersion, int MaxBrave = 0,
    string Mode = "", string Kind = "", string HostId = "");
```

`MachinePresence` thêm `Mode`, `Kind`, `HostId` (string, mặc định rỗng).

**Tương thích ngược (bắt buộc):** client cũ gửi thiếu → hub tự suy: `Kind` rỗng → `"workspace"`;
`HostId` rỗng → `MachineSlots.HostOf(MachineId)`; `Mode` rỗng → `"Workspace"`. Ghi rõ quy ước này bằng comment
tại chỗ suy diễn.

### Bước 3 — Hub: schema + heartbeat (`server/Shopee.Hub.Web`)

- `Data/HubDatabase.cs`: bảng `machines` thêm `mode TEXT DEFAULT ''`, `kind TEXT DEFAULT ''`,
  `host_id TEXT DEFAULT ''`. **Phải thêm qua đường "cột mới cho DB ĐÃ TỒN TẠI"** — trong file này đã có sẵn cơ chế
  đó (xem comment quanh `HubDatabase.cs:54`), `CREATE TABLE IF NOT EXISTS` không thêm cột cho bảng cũ.
- `MachineHeartbeat(...)`: ghi 3 cột mới, áp quy ước suy diễn ở Bước 2. `AllMachines()` đọc ra `MachinePresence`.
- Không đổi khoá chính: `machine_id` vẫn là id suất.

### Bước 4 — Client: gate theo chế độ (`suite/Shopee.Suite/App.axaml.cs` + `CoordinationRuntime`)

Hành vi mong muốn theo chế độ:

| Mode | Suất Workspace | Suất Đơn hàng |
|---|---|---|
| `Workspace` | `InitFromConfig()` như hiện tại (heartbeat + poll + lease) | không |
| `Shopee` | không | heartbeat suất `:orders` (đăng ký máy + nhận lệnh update). **KHÔNG** poll assignment, **KHÔNG** lease BigSeller |
| `Full` | như `Workspace` | như `Shopee` — cùng một process |

Thêm vào `CoordinationRuntime` một đường khởi tạo suất đơn hàng, ví dụ `InitOrdersSlot()`: dựng `HubClient`
(đã có ở `InitClientOnlyFromConfig`) + **một timer heartbeat nhẹ** gửi `MachineHeartbeatRequest` với
`MachineId = SlotId(host, Orders)`, `Kind = "orders"`, `HostId = host`, `Mode = <mode hiện tại>`, và xử lý
`MachineHeartbeatResponse.UpdateRequestedAt` **dùng lại đúng đường auto-update sẵn có** (tìm chỗ Workspace xử lý
lệnh update trong `HttpCoordinationHub` và tái dùng, đừng chép logic).

Nhịp heartbeat: dùng cùng chu kỳ với đường Workspace hiện tại (soi `HttpCoordinationHub` để lấy đúng con số, đừng
tự đặt số mới).

Chú ý ở chế độ **Full**: `InitFromConfig()` và `InitOrdersSlot()` chạy song song trong một process — cả hai dùng
chung `HubClientConfig`. KHÔNG dựng 2 `HttpCoordinationHub`; suất đơn hàng chỉ cần `HubClient` + timer.

### Bước 5 — Khoá acc đơn hàng dùng id suất

`suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs` hiện có
`LeaseMachineId => MachineIdentity.Shared.MachineId`. Đổi sang id **suất đơn hàng**
(`MachineSlots.SlotId(host, Orders)`) để hub biết máy nào đang giữ tài khoản Shopee, và để trang /dispatch tra
đúng suất.

**Delta hành vi phải khai báo rõ:** comment hiện tại ở `OrdersModuleHost.cs:404-406` ghi rằng hai bản Full +
Shopee trên cùng PC dùng chung `MachineIdentity` nên hub cấp lại cùng `machine_id` → **không tự chặn nhau**. Sau
thay đổi này, cả hai vẫn dùng chung id suất `:orders` (vì cùng `HostId`) nên hành vi **giữ nguyên** — không phải
sửa gì thêm, nhưng phải **cập nhật lại comment đó cho khớp thực tế mới**.

### Bước 6 — Hub UI

1. `Components/Pages/Machines.razor`: thêm cột **Chế độ** (`Full` / `Workspace` / `Shopee`) và **Suất**
   (`Workspace` / `Đơn hàng`). Hai dòng cùng `HostId` xếp cạnh nhau (sort theo `host_id`, rồi `kind`), dòng suất
   đơn hàng thụt vào + nhãn phụ `↳ cùng máy với <hostname>`.
2. `Components/Pages/Dispatch.razor` — tab BigSeller: **chỉ hiện suất Workspace** trong hàng thẻ máy
   (`Kind != "orders"`). Suất đơn hàng không claim việc BigSeller nên hiện ra chỉ tổ giao nhầm.
3. Thẻ máy hiện thêm nhãn mode khi khác `Workspace` (vd `PC-01 · Full`).

### Bước 7 — Chặn cứng phía hub

`HubDatabase.ClaimNext` / `CreateAssignment`: từ chối tạo/giao assignment BigSeller có `target_machine_id` là suất
đơn hàng (id kết thúc bằng `:orders`, hoặc `kind = "orders"` trong bảng `machines`). Trả lỗi rõ ràng thay vì im
lặng, vì việc giao vào đó sẽ nằm `queued` vĩnh viễn.

### Bước 8 — Test

Thêm test cho phần thuần logic (đặt ở `orders/XuLyDonShopee.Tests`, LINK file nguồn theo khuôn đang dùng):
- `MachineSlots.SlotId` / `HostOf`: workspace giữ nguyên id; orders có hậu tố; `HostOf` đảo ngược đúng cả 2 chiều;
  id máy tình cờ chứa `":orders"` ở giữa không bị cắt nhầm.
- Quy ước suy diễn tương thích ngược: request thiếu `Kind`/`HostId`/`Mode` → ra `workspace` / host = chính id /
  `Workspace`.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` sạch, 0 warning mới; `dotnet test` xanh (kèm test mới).
- [ ] **Tương thích ngược:** chạy hub mới + giả lập heartbeat KHÔNG có 3 field mới (post JSON thiếu field) → máy
      vẫn đăng ký, `kind = workspace`, `host_id` = chính `machine_id`, không exception.
- [ ] DB cũ (đã có bảng `machines`) mở bằng hub mới → tự thêm 3 cột, không mất dữ liệu, không lỗi.
- [ ] Chạy client chế độ `Shopee` (`--mode Shopee`) trỏ vào hub local → **máy hiện ở trang Máy client** với suất
      "Đơn hàng"; hub ra lệnh update app tới máy đó thì client **nhận được** (kiểm bằng log/ack).
- [ ] Chạy client chế độ `Full` → hub hiện **2 dòng cùng một PC**, một suất Workspace (đúng `machine_id` cũ) +
      một suất Đơn hàng, cùng `host_id`.
- [ ] Chế độ `Workspace` giữ nguyên `machine_id` cũ — dữ liệu ledger/assignment/search cũ vẫn khớp máy (kiểm bằng
      cách chạy với `machine.json` sẵn có rồi soi trang Fleet vẫn thấy đúng máy cũ, không sinh máy lạ).
- [ ] Tab BigSeller ở /dispatch **không hiện** suất đơn hàng.
- [ ] Cố tạo assignment BigSeller ghim vào id suất `:orders` → hub từ chối kèm lý do (kiểm bằng gọi API trực tiếp).
- [ ] Suất đơn hàng **không** claim assignment BigSeller (soi log/DB sau vài phút chạy).

## 5. Rủi ro & lưu ý

- **Đừng đổi `machine_id` của suất Workspace.** Đây là rủi ro lớn nhất của plan này: đổi là mất liên kết lịch sử ở
  6 bảng. Nếu thấy chỗ nào bắt buộc phải đổi, DỪNG và báo lại thay vì tự quyết.
- **Tương thích ngược là bắt buộc**: hub deploy TRƯỚC, client release SAU. Trong khoảng giữa, mọi client đang chạy
  là bản cũ không gửi field mới — hub phải chạy đúng với chúng.
- Chế độ Full chạy 2 suất trong 1 process: cẩn thận rò timer (đã có tiền lệ `CoordinationRuntime.Reconnect()` phải
  dispose bản cũ, xem comment `CoordinationRuntime.cs:83-89`). Suất đơn hàng cũng phải được dọn khi Reconnect.
- `MachineIdentity` là singleton dùng chung; đừng sửa `machine.json` để chứa id suất — suất là thứ tính ra lúc
  chạy, không lưu.
- Không đụng `Fleet.razor` trừ khi bắt buộc; nếu phải đụng thì nêu rõ trong báo cáo.

---

## Báo cáo thực thi (Opus điền sau khi xong)
