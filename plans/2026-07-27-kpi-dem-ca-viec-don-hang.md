# Plan: KPI trang Giao việc phải đếm CẢ việc Đơn hàng, không chỉ BigSeller

- **Ngày:** 2026-07-27
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh — lỗi người dùng báo (có ảnh)

> *"Trong phần giao việc, hình như chỉ mới tính cho workspace, còn shopee chưa tính thì phải — đang có 1 việc ở
> client chạy, nhưng stat card vẫn báo 0."*

Ảnh chụp production: thẻ **"Việc đang chạy" = 0**, trong khi ngay dưới đó tab Đơn hàng hiện máy `Hoàng DH - TH`
(*"1 tài khoản · 1 đang chạy"*) và dòng tài khoản `hoangdh200392:muinx` mang pill **`▶ Đang chạy`** (12 shop,
9 đơn chờ, sync 33s trước). Số nói một đằng, bảng nói một nẻo.

**Nguyên nhân (đã đọc code):** `Dispatch.razor.RecomputeKpis()` chỉ quét `_rows` (shop BigSeller) × `DispatchOps`
(scrape/import/update) qua `Cell(r, op).Kind`. Phiên **Đơn hàng** không nằm trong `_rows` nên không được đếm.

**Vướng thêm:** dữ liệu gương Đơn hàng hiện chỉ nạp cho **máy đang chọn** (`_oAccounts = Db.OrdersAccountsOf(_oMach)`),
mà KPI là số **toàn hệ thống** → phải có đường đọc trạng thái phiên Đơn hàng của **mọi máy**.

**BẤT BIẾN đã chốt từ đợt KPI trước (giữ nguyên):** số trên thẻ và số dòng trong bảng chi tiết phải đến từ **cùng
một lượt quét** — không được đếm một đường, dựng bảng một đường.

## 2. Phạm vi

**Làm:**
- Hub: đường đọc trạng thái phiên Đơn hàng của MỌI máy (không chỉ máy đang chọn).
- `RecomputeKpis`: cộng thêm phiên Đơn hàng vào "Việc đang chạy" / "Việc chờ".
- Bảng chi tiết của 2 thẻ đó: thêm dòng Đơn hàng, phân biệt rõ với việc BigSeller.
- Nút trên dòng Đơn hàng trong bảng chi tiết: `✖ Dừng` (gửi lệnh stop — đúng thứ hub được phép làm).

**Không làm:**
- KHÔNG đụng thẻ "Máy online" và "Việc gián đoạn" (gián đoạn là khái niệm của assignment BigSeller; phiên Đơn hàng
  không có vòng đời đó).
- KHÔNG đụng tab BigSeller, `Fleet.razor`, `suite/`, `orders/`.
- KHÔNG thêm nút "Đăng nhập lại" — ranh giới đã chốt: với Đơn hàng, hub CHỈ nhìn và chạy.
- KHÔNG commit, KHÔNG deploy.

## 3. Các bước thực hiện

### Bước 1 — Hub: đọc phiên Đơn hàng toàn hệ thống

`server/Shopee.Hub.Web/Data/HubDatabase.OrdersAccounts.cs` — thêm một hàm đọc thuần, ví dụ:

```csharp
/// <summary>Mọi tài khoản Đơn hàng ĐANG CÓ PHIÊN (session_state khác rỗng) của MỌI máy — cho KPI toàn hệ thống
/// của trang Giao việc. Kèm machine_id + hostname (JOIN machines) để bảng chi tiết nói được "ở máy nào".</summary>
public List<OrdersRunningAccount> OrdersRunningAccounts();

public sealed record OrdersRunningAccount(
    string MachineId, string Hostname, string Login, string SessionState, DateTimeOffset UpdatedAt);
```
- Một query duy nhất (LEFT JOIN `machines` lấy hostname; thiếu thì để rỗng, bên gọi tự lùi về id rút gọn).
- `hostname` của suất đơn hàng chính là hostname máy — KHÔNG cắt hậu tố `:orders` ở đây, việc hiển thị để UI lo.

### Bước 2 — Quy ước trạng thái → KPI

Trạng thái phiên client gửi lên nằm ở `OrdersSessionStates` (`suite/Shopee.Core/Coordination/HubDtos.cs`) —
**đọc hằng số đó, đừng chép chuỗi**. Ánh xạ:

| SessionState | Vào KPI nào |
|---|---|
| `opening`, `running`, `stopping` | **Việc đang chạy** |
| `queued` | **Việc chờ** |
| rỗng | không đếm |

Ghi comment giải thích `stopping` vẫn tính là "đang chạy": phiên còn chiếm slot cầu nối, chưa nhả máy.

### Bước 3 — `RecomputeKpis` cộng thêm

Trong `Dispatch.razor`:
- Nạp `_ordersRunning = Db.OrdersRunningAccounts()` trong cùng nhịp với dữ liệu khác (bọc try/catch giữ bản cũ như
  các đường đọc DB khác của trang).
- Duyệt danh sách đó, dựng `WorkItem` cho mỗi phiên rồi **cộng vào chính `running` / `queued`** đang có → `_kRunning`
  và `_kQueued` vẫn là `.Count` của list bảng ⇒ giữ nguyên bất biến "một lượt quét".
- `WorkItem` hiện là `(Key, AsnId, Op, ShopName, AcctName, Host, Since, Rows, Manual)`. Dòng Đơn hàng:
  - `Op` = `"orders"` (nhãn hiển thị `📦 Đơn hàng` — thêm nhánh vào `OpLabel` hoặc xử lý tại chỗ render).
  - `AcctName` = login tài khoản; `ShopName` = `"—"` (phiên chạy cả acc, không thuộc shop nào).
  - `Host` = hostname máy; `Since` = `UpdatedAt` của bản gương (nhịp cuối) — **ghi comment nói rõ đây là "cập nhật
    lúc" chứ không phải "chạy từ"**, vì gương không lưu mốc bắt đầu phiên. Cột trong bảng đặt tên cho đúng.
  - `AsnId` = rỗng (không phải assignment) → dùng `Key` để phân biệt dòng.
  - `Manual` = false.
- Trạng thái chờ-xác-nhận (`_confirmWork`) hiện so theo `AsnId`; dòng Đơn hàng không có `AsnId` → **phải so theo
  `Key`** để không nhầm hai dòng khác nhau. Sửa cả chỗ dọn `_confirmWork` trong `RecomputeKpis`.

### Bước 4 — Bảng chi tiết

Hai bảng "Việc đang chạy" / "Việc chờ":
- Thêm cột (hoặc pill ở cột Việc) phân biệt **BigSeller** vs **Đơn hàng** — nhìn là biết dòng nào loại nào.
- Dòng Đơn hàng: cột Shop để `—`, cột "Dải dòng" để `—`.
- Nút: dòng Đơn hàng đang chạy → `✖ Dừng` gửi lệnh `stop` qua `Db.CreateOrdersCommand(...)` **đúng khuôn nút Dừng ở
  tab Đơn hàng đang có** (tái dùng hàm sẵn có, đừng viết đường tạo lệnh thứ hai). Giữ xác nhận 2 bước như nút huỷ
  hiện tại.
- Dòng Đơn hàng ở trạng thái `queued` → không có nút (client tự xếp hàng, hub không chen ngang được).

### Bước 5 — Kiểm chứng

Chạy hub local, seed: 1 máy suất workspace có 1 assignment `running`, 1 máy suất đơn hàng có 1 acc `running` + 1 acc
`queued`. Xác nhận thẻ "Việc đang chạy" = 2, "Việc chờ" = 1, và bảng chi tiết đúng số dòng đó.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới.
- [ ] Thẻ **Việc đang chạy** đếm cả phiên Đơn hàng: seed 1 việc BigSeller running + 1 acc Đơn hàng running → **2**.
- [ ] Thẻ **Việc chờ** đếm acc Đơn hàng `queued`.
- [ ] Số trên thẻ == số dòng trong bảng chi tiết tương ứng (bất biến "một lượt quét").
- [ ] Bảng chi tiết phân biệt được dòng BigSeller và dòng Đơn hàng; dòng Đơn hàng nói đúng **tài khoản** + **máy**.
- [ ] `✖ Dừng` trên dòng Đơn hàng sinh đúng 1 lệnh `stop` trong `orders_commands` (2 bước xác nhận).
- [ ] Máy chưa đẩy gương (client cũ) → không có dòng Đơn hàng nào, KPI không đổi, không exception.
- [ ] Không đụng `suite/`, `orders/`, `Fleet.razor` (`git diff --stat` xác nhận).

## 5. Rủi ro & lưu ý

- **Giữ bất biến "một lượt quét"**: đừng đếm KPI bằng một query COUNT rồi dựng bảng bằng query khác — lệch nhau là
  mất niềm tin vào cả trang, đúng thứ người dùng vừa báo.
- `_confirmWork` đang khoá theo `AsnId`; dòng Đơn hàng không có id assignment → không sửa là hai dòng Đơn hàng khác
  nhau cùng "đang chờ xác nhận".
- `Since` của dòng Đơn hàng KHÔNG phải mốc bắt đầu phiên (gương không có) — đặt tên cột cho đúng sự thật, đừng ghi
  "Chạy từ" rồi hiện nhịp cập nhật.
- Đọc hằng trạng thái từ `OrdersSessionStates`, đừng chép chuỗi rời — client là nơi định nghĩa.

---

## Báo cáo thực thi (Opus điền sau khi xong)
