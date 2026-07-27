# Plan: Tách phần GIAO VIỆC khỏi trang Fleet — hai trang, hai vai rõ ràng

- **Ngày:** 2026-07-27
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Hub giờ có hai trang chồng vai nhau:
- `/dispatch` (**Giao việc**, mới) — trục MÁY: chọn máy → bấm action thẳng trên dòng shop. Đã có: giao/huỷ/chạy lại
  từng ô, nút cả-acc, huỷ theo máy, huỷ theo acc, KPI bấm được (đang chạy / chờ / gián đoạn + Tiếp tục + Bỏ cả N).
- `/` (**Fleet**, cũ, `Components/Pages/Fleet.razor`, 1241 dòng) — trục ACC→SHOP: vừa giao việc (panel "Ghim việc"),
  vừa xem số liệu, vừa đặt tay ledger, vừa rewrite, vừa cấu hình, vừa xem dữ liệu/thống kê.

**Người dùng chốt hướng:** KHÔNG dồn hết vào một trang, KHÔNG dựng lại thứ đã chạy tốt. Chỉ **bóc phần giao việc ra
khỏi Fleet**, giữ nguyên phần còn lại, để hai trang tách vai rõ ràng:

| Trang | Vai |
|---|---|
| `/dispatch` — **Giao việc** | Điều khiển fleet: giao / huỷ / tiếp tục việc. Mở cả ngày. |
| `/` — **Fleet** | Soi & sửa: số liệu theo acc, đặt tay ledger, rewrite, cấu hình, dữ liệu, thống kê. |

**RANH GIỚI (người dùng chốt, áp dụng cho mọi tranh cãi phạm vi):** *"Trang Giao việc CHỈ thực hiện đúng giao việc,
mọi thứ còn lại ở trong Fleet hết."*
- Thuộc **giao việc** ⇒ ở `/dispatch`: tạo việc, huỷ việc, tiếp tục việc, và **điều phối tự động** (đó là máy tạo
  việc tự động) + **huỷ MỌI việc** (huỷ hàng loạt). Hai cái này đang nằm ở Fleet nên PHẢI chuyển sang.
- KHÔNG thuộc giao việc ⇒ ở lại **Fleet**, và **KHÔNG được thêm vào `/dispatch`**: số liệu/thống kê, dữ liệu sản
  phẩm, cấu hình acc/shop, đặt tay ledger, rewrite tên, thêm tài khoản.
- Khi phân vân một mục đi đâu: hỏi "nó có TẠO/HUỶ/TIẾP TỤC việc cho client không?" — không thì để ở Fleet.

**BẪY phải xử lý trước khi bóc:** hai chức năng dưới đây CHỈ có ở Fleet, bóc đi mà không chuyển trước là **mất hẳn
khỏi hub**:
- Công tắc **Điều phối tự động** (`Dispatcher.Enabled` — dây chuyền scrape→import→update).
- Nút **✖ Huỷ MỌI việc** (huỷ toàn hệ thống, khác "huỷ theo máy/theo acc" mà `/dispatch` đã có).

## 2. Phạm vi

**Phần A — `/dispatch` nhận thêm (làm TRƯỚC):**
- Công tắc **Điều phối tự động**.
- Nút **✖ Huỷ MỌI việc** (xác nhận 2 bước).

**Phần B — Fleet bóc phần giao việc:**
- XOÁ panel **"Ghim việc"** (`.pinpanel` phần form) + mọi state/hàm chỉ phục vụ nó.
- XOÁ nút **▶ Tiếp tục trên máy X** (tiếp tục việc chạy-tay) trong cột cuối bảng op.
- XOÁ khối **⏯ Việc gián đoạn** của shop + nút ▶ Tiếp tục từng việc.
- XOÁ thanh hành động toàn cục: Điều phối tự động, Huỷ MỌI việc, Tiếp tục N, Bỏ N.
- THÊM liên kết chéo hai chiều với `/dispatch`.

**GIỮ NGUYÊN ở Fleet (đừng đụng):** danh sách acc→shop bên trái + ô tìm + `+ Thêm tài khoản`; dashboard acc (4 thẻ
op + ma trận shop + dòng Tổng); bảng 4 op ở mức shop **kèm combo đặt-tay ledger**; khối **Rewrite tên** (chạy trên
HUB, không phải giao việc cho client → ở lại); tab **Thống kê**, **Dữ liệu**, **Cấu hình**; hàng KPI đầu trang.

**Không làm:**
- KHÔNG tách trang chi tiết shop riêng (đã bàn, người dùng chọn hướng gọn hơn này).
- KHÔNG đổi hành vi đặt-tay ledger, rewrite, cấu hình, dữ liệu, thống kê.
- KHÔNG đụng `suite/`, `orders/`.
- KHÔNG commit, KHÔNG deploy.

## 3. Các bước thực hiện

### Bước 1 — `/dispatch`: công tắc Điều phối tự động

`Components/Pages/Dispatch.razor`. Inject `DispatcherService Dispatcher` (đã đăng ký DI, Fleet đang dùng).
Đặt ở hàng **ngay dưới hàng tab**, cùng hàng với dòng kết quả `_result` (đừng nhét vào panel tham số — nó là cấu
hình toàn hệ thống, không thuộc máy đang chọn):

```razor
<label class="toggle">
    <input type="checkbox" checked="@Dispatcher.Enabled" @onchange="ToggleDispatch" />
    Điều phối tự động (scrape→import→update)
</label>
```
`ToggleDispatch` copy đúng khuôn Fleet đang làm (tìm trong `Fleet.razor`), kèm `FleetState.Refresh()`.
Chỉ hiện ở tab BigSeller (tab Đơn hàng chưa có điều phối).

### Bước 2 — `/dispatch`: nút ✖ Huỷ MỌI việc

Cạnh công tắc trên. Xác nhận 2 bước, dùng lại biến `_confirmCancel` sẵn có với khoá `"all"`:
- Nhãn thường: `✖ Huỷ MỌI việc (N)` với N = số assignment `queued`/`running` toàn hệ thống.
- N = 0 → `disabled`.
- Bấm lần 2 → huỷ hết, `_result` = `✖ Đã huỷ N việc (toàn hệ thống)`, `FleetState.Refresh()` **một lần**.
- `title` phải nói rõ: huỷ MỌI máy, không chỉ máy đang chọn — kẻo nhầm với nút "Huỷ mọi việc trên máy này".

### Bước 3 — Fleet: bóc phần giao việc

Trong `Components/Pages/Fleet.razor`, xoá:

1. **Thanh hành động toàn cục** (khoảng dòng 36-80): công tắc `Dispatcher`, `✖ Huỷ MỌI việc`, `▶ Tiếp tục N việc
   gián đoạn`, `✕ Bỏ N việc gián đoạn` + state `_confirmCancelAll/_confirmResumeAll/_confirmDismissAll`,
   `_openWorkCount`, `_interruptedCount` và các hàm `ToggleDispatch/CancelAllWork/ResumeAllInterrupted/
   DismissAllInterrupted`. Giữ lại `<span class="stat">@_summary</span>` (dòng tóm tắt số liệu) và `_barMsg` nếu
   còn chỗ dùng; không còn ai dùng thì xoá luôn.
2. **Panel Ghim việc** (khối `.pinpanel` phần form, khoảng dòng 175-262): toàn bộ form + nút `📌 Ghim việc` /
   `✖ Huỷ việc (hub giao)` + `busyline` "Đang giao việc…/Đang hủy…" + 3 khối cảnh báo `_pinBlocked` /
   `_manualLease` / `_accountOwnerWarn`.
   State/hàm kèm theo: `_pinOp/_pinMachine/_pinStart/_pinEnd/_pinProcs/_pinFrame/_pinReload/_pinFromClaimed`,
   `_pinnableMachines`, `_pinBlocked`, `_accountOwnerMachine`, `_accountOwnerWarn`, `_assigning`, `_canceling`,
   `_activeAsn`, `_hasWork`, `_shopLease`, `_manualLease`, `Pin()`, `CancelSelected()`, `AsnMachineName()`.
3. **Nút ▶ Tiếp tục trên máy X** trong cột cuối bảng 4 op (khoảng dòng 160-167) + `ManualResumeLedger()` +
   `ResumeManual()`.
4. **Khối ⏯ Việc gián đoạn của shop** (khoảng dòng 263-282) + `_shopInterrupted`, `_resumeMsg`, `ResumeOne()`,
   `IntMachine()`, `IntReason()`.

**Cẩn thận:** `OwnerOf()` / `HostName()` / `Machine()` còn được dùng chỗ khác (dòng "🔒 Acc đang do máy X giữ" ở
dashboard acc, cột máy…) — **đừng xoá theo**. Sau khi xoá, build phải sạch 0 warning; nếu trình dịch báo "unused"
thì mới là xoá đủ.

### Bước 4 — Liên kết chéo hai trang

- **Fleet → Giao việc:** ở tab Hành động mức shop, ngay dưới bảng 4 op, thêm một dòng:
  `<a class="btn primary sm" href="/dispatch?acct={AccountId}&q={ShopName}">🎯 Giao việc cho shop này</a>`
  (dùng đúng tên tham số URL `/dispatch` đang nhận: `acct`, `q` — kiểm lại trong `Dispatch.razor.RestoreFromUrl`).
  Ở mức acc thì trỏ `/dispatch?acct={AccountId}`.
- **Giao việc → Fleet:** ở `/dispatch`, tên shop trong lưới thành liên kết mở Fleet đúng shop đó — kiểm tham số
  Fleet nhận trong `Fleet.razor.RestoreSelectionFromUrl` rồi dùng đúng tên, đừng đoán.
  Liên kết phải KHÔNG nuốt cú bấm vào nút hành động cùng dòng.

### Bước 5 — Nav + tiêu đề

`Components/Layout/MainLayout.razor`: đổi nhãn mục Fleet thành **"Số liệu & cấu hình"** (giữ nguyên route `/`) để
khỏi lẫn vai với "Giao việc"; sửa `UpdateTitle()` cho khớp. Thứ tự nav: **Giao việc** lên trên **Số liệu & cấu hình**.

### Bước 6 — Kiểm chứng

Chạy hub thật với dữ liệu seed và xác nhận: mọi thứ CÒN LẠI của Fleet vẫn chạy y như trước (đặt tay ledger,
rewrite, 3 tab, dashboard acc, thêm tài khoản), và không còn lối nào giao việc từ Fleet.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build server/Shopee.Hub.Web` sạch, **0 warning mới** (còn state/hàm mồ côi là chưa xoá hết).
- [ ] `/dispatch`: công tắc Điều phối tự động bật/tắt được, giá trị **giữ nguyên sau F5** (lưu trong bảng settings).
- [ ] `/dispatch`: `✖ Huỷ MỌI việc (N)` cần 2 lần bấm; sau khi huỷ, N về 0 và KPI "Việc đang chạy/chờ" cũng về 0.
- [ ] Nút "Huỷ MỌI việc" và "Huỷ mọi việc trên máy này" **phân biệt được** qua nhãn + title (đọc là hiểu ngay khác gì nhau).
- [ ] Fleet **không còn** bất kỳ lối giao việc nào: không panel Ghim việc, không ▶ Tiếp tục, không Huỷ MỌI việc,
      không công tắc điều phối.
- [ ] Fleet **vẫn còn và vẫn chạy**: combo đặt-tay ledger (✓ Xong / Chưa (reset) / ■ Dừng) đổi được trạng thái;
      khối Rewrite chạy được; tab Thống kê / Dữ liệu / Cấu hình mở bình thường; dashboard acc đủ 4 thẻ + dòng Tổng;
      `+ Thêm tài khoản` còn dùng được.
- [ ] Bấm `🎯 Giao việc cho shop này` ở Fleet → sang `/dispatch` **đã lọc sẵn đúng acc/shop đó**.
- [ ] Bấm tên shop ở `/dispatch` → mở Fleet **đúng shop đó**, và cú bấm KHÔNG kích hoạt nút hành động cùng dòng.
- [ ] Nav hiện "Giao việc" rồi tới "Số liệu & cấu hình"; tiêu đề topbar khớp.
- [ ] 400px không cuộn ngang; nền tối đọc được.

## 5. Rủi ro & lưu ý

- **Thứ tự bắt buộc: làm Phần A (Bước 1-2) TRƯỚC Phần B.** Bóc trước khi chuyển là hub mất hẳn 2 chức năng.
- Fleet 1241 dòng và đang chạy production — **chỉ xoá đúng phần giao việc**. Mọi thứ khác giữ nguyên từng dòng;
  đây là lý do người dùng chọn hướng này (không phải làm lại).
- `.pinpanel` là class CSS dùng chung: sau khi bóc form ghim việc, khối **Rewrite** vẫn dùng class đó → **đừng xoá
  CSS `.pinpanel`**.
- Đừng xoá nhầm hàm dùng chung (`OwnerOf`, `HostName`, `Machine`, `ShopNameOf`, `AcctLeases`) — chúng còn phục vụ
  phần số liệu.
- Rewrite chạy TRÊN HUB (không giao cho client) nên **ở lại Fleet**; đừng gom nhầm vào "phần giao việc".
- Tham số URL của cả hai trang phải **đọc từ code**, không đoán — link chéo sai tham số thì mở ra trang trống.

---

## Báo cáo thực thi (Opus điền sau khi xong)
