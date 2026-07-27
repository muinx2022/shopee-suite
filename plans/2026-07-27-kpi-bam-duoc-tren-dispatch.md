# Plan: KPI bấm được ở /dispatch — xổ ra danh sách "đang chạy cái gì, ở đâu"

- **Ngày:** 2026-07-27
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — **chạy trong WORKTREE**

## 1. Bối cảnh & mục tiêu

Trang `/dispatch` có 4 thẻ KPI ở đầu: **Máy online**, **Việc đang chạy**, **Việc chờ**, **Việc gián đoạn**. Chúng
chỉ là số chết. Người dùng đang nhìn `5 việc gián đoạn` mà không biết là những việc nào, và yêu cầu:

> "với những cái đang hiển thị > 0, ví dụ 5 việc gián đoạn, click vào đó thì hiển thị ra những việc đó; ví dụ 5
> việc đang chạy, bấm vào thì biết nó **đang chạy cái gì ở đâu**"

Mục tiêu: thẻ KPI có số > 0 thì **bấm được**, bấm ra bảng chi tiết ngay dưới hàng KPI, mỗi dòng nói rõ *việc gì ·
shop nào · máy nào · từ lúc nào*, kèm hành động phù hợp.

**Bối cảnh sự cố vừa xảy ra (lý do tính năng này đáng giá):** một job Scrape chạy 6 phút mà không mở nổi cửa sổ nào
(key proxy hết hạn), hub vẫn hiện `⏳ đang chạy` và người vận hành không có cách nào thấy nó đang kẹt ở đâu.

Trang hiện tại (sau commit `a179738`) đã theo trục **máy-first**: chọn máy → bấm action thẳng trên dòng. Bảng chi
tiết này là lối tra cứu ngang, **không thay** luồng đó.

## 2. Phạm vi

**Làm:** 4 thẻ KPI bấm được + bảng chi tiết tương ứng + hành động trên từng dòng + trạng thái vào URL.

**Không làm:**
- KHÔNG đụng `suite/`, `orders/` — có plan chạy **song song trên cây chính** sửa `ScrapeRunner`/`AssignmentWorker`.
  Việc này CHỈ sửa hub web.
- KHÔNG đổi luồng chọn-máy / nút action trên lưới shop.
- KHÔNG đụng `Fleet.razor`.
- KHÔNG commit, KHÔNG deploy.

## 3. Các bước thực hiện

### Bước 1 — Thẻ KPI thành nút

`server/Shopee.Hub.Web/Components/Pages/Dispatch.razor`, khối `.kpis` ở đầu trang:
- Thẻ có số **> 0** → `<button class="kpi kpi-btn">`, bấm để mở/đóng bảng chi tiết (bấm lại thẻ đang mở = đóng).
- Thẻ có số **= 0** → giữ nguyên `<div>` như hiện tại, KHÔNG bấm được (đừng render nút rồi disable — thẻ 0 không có
  gì để xem).
- Thẻ đang mở: viền/nền nhấn theo token sẵn có (`--primary`, `--primary-soft`), thêm mũi chỉ báo `▾`.

### Bước 2 — Bảng chi tiết cho từng loại

Panel xổ **ngay dưới hàng KPI**, trên hàng tab (không dùng modal: trang đã có `.tablewrap` cuộn ngang, và modal
trên mobile thì bí). Mỗi loại một bộ cột. Dùng lại `.tablewrap` + `table.grid.sm` + `.pill` sẵn có.

| KPI | Nguồn | Cột | Hành động/dòng |
|---|---|---|---|
| **Máy online** | `_budgets` lọc `Online` | Máy · Chế độ · Suất · Quỹ Brave (còn/tổng) · Số việc · Bản app · Nhịp cuối | `→ Chọn máy này` (chỉ với suất workspace: set `_selMachine` + đóng panel) |
| **Việc đang chạy** | `Snap.Assignments` `status == "running"` | Việc (op) · Shop · Tài khoản · Máy · Chạy từ lúc · Dải dòng | `✖ Huỷ` |
| **Việc chờ** | `Snap.Assignments` `status == "queued"` | Việc · Shop · Tài khoản · Máy đích (rỗng = chưa ghim) · Xếp lúc | `✖ Huỷ` |
| **Việc gián đoạn** | `Snap.Interrupted` | Việc · Shop · Tài khoản · Máy cuối · **Lý do** · Lúc | `▶ Tiếp tục` · `✕ Bỏ` |

Ghi chú bắt buộc:
- **Tên shop/tài khoản, không phải id.** `Assignment` chỉ có `BigsellerId`/`ShopId` → map sang tên qua `_rows`
  (đã dựng từ `Config.BigSellerAccounts()`); không tra được (shop đã xoá khỏi config) thì hiện id rút gọn + title
  đầy đủ, **đừng để trống**.
- **Máy:** hostname, không phải `machine_id`. Dùng `HostName(...)` sẵn có trong trang.
- **Thời gian:** dùng `FleetStateService.Ago(...)` cho khớp phần còn lại của hub.
- Hàng KPI "Việc đang chạy" hiện đếm theo **ô lưới** (`Cell(r,op).Kind == 1`) còn bảng chi tiết đọc
  `Snap.Assignments` — hai nguồn có thể lệch (việc chạy TAY không có assignment). **Phải xử lý cho khớp:** hoặc đổi
  KPI sang đếm đúng nguồn của bảng, hoặc bảng gộp thêm việc chạy tay từ `Snap.Leases` (cột Máy lấy `Hostname` của
  lease, hành động để trống + title "việc chạy tay — dừng ở app trên máy đó"). **Chọn cách 2** để không giấu việc
  chạy tay khỏi người vận hành; ghi rõ lựa chọn trong comment.
- Danh sách rỗng sau khi lọc (vd vừa huỷ hết) → hiện dòng "không còn việc nào" thay vì bảng trống trơn, và tự đóng
  panel ở nhịp fleet sau nếu KPI về 0.

### Bước 3 — Hành động

- `✖ Huỷ` → `Db.CancelAssignment(id)`; `▶ Tiếp tục` → `Db.ResumeAssignment(id)` (trả lỗi thì hiện inline);
  `✕ Bỏ` → dùng đúng API mà nút "Bỏ việc gián đoạn" của trang Fleet đang dùng (tìm trong `HubDatabase.Assignments`,
  đừng tự viết SQL mới).
- Sau mỗi hành động: `FleetState.Refresh()` **một lần**, cập nhật dòng kết quả ngắn ở panel.
- Huỷ/Bỏ là **thao tác một chiều** → **xác nhận 2 bước** theo pattern repo (bấm lần nữa để xác nhận), giống nút
  "cả acc". `▶ Tiếp tục` thì không cần.

### Bước 4 — URL-state + nhịp fleet

- Thêm `kpi` vào query (`machines` | `running` | `queued` | `interrupted`; rỗng = đóng). Giữ nguyên các key đang có
  (`tab/f/acct/q/mach`). F5 phải mở lại đúng panel.
- Trang bám nhịp fleet 2s: bảng tự cập nhật. Trạng thái chờ-xác-nhận (bấm-lần-nữa) phải **huỷ** khi dòng đó biến
  mất khỏi danh sách, kẻo bấm lần 2 trúng việc khác — đây đúng lỗi đã phải vá cho nút "cả acc".

### Bước 5 — CSS (`server/Shopee.Hub.Web/wwwroot/app.css`)

- Thêm `.kpi-btn` (con trỏ, hover, trạng thái mở), `.kpipanel`. Đặt trong khối
  `/* ===== Trang Giao việc (/dispatch) ===== */` đang có.
- Chỉ dùng token sẵn có; override nền tối viết `html.dark .xxx`.
- Bump `app.css?v=30` trong `Components/App.razor`.
- Mobile (≤920px): thẻ KPI vẫn bấm được, bảng cuộn ngang trong `.tablewrap`, body **không** cuộn ngang.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới.
- [ ] Thẻ có số 0 **không bấm được**; thẻ > 0 bấm ra bảng, bấm lại đóng.
- [ ] "Việc gián đoạn" (đang là 5 trên production) → bảng đúng **5 dòng**, mỗi dòng có **tên shop + tên máy + lý do**,
      không dòng nào để trống cột Máy hay hiện id trần khi tra được tên.
- [ ] "Việc đang chạy" → thấy đúng việc gì · shop nào · máy nào · chạy từ bao lâu; việc chạy TAY cũng hiện, kèm
      title giải thích không huỷ được từ hub.
- [ ] `✖ Huỷ` cần **2 lần bấm**; sau khi huỷ, dòng biến mất và KPI giảm ở nhịp sau.
- [ ] `▶ Tiếp tục` đưa việc gián đoạn về hàng chờ (kiểm bằng KPI "Việc chờ" tăng).
- [ ] F5 khi đang mở panel → mở lại đúng panel đó (URL có `kpi=`).
- [ ] Bấm `→ Chọn máy này` ở bảng Máy online → panel đóng, máy đó được chọn ở lưới bên dưới.
- [ ] 400px: không cuộn ngang toàn trang; nền tối đọc được.
- [ ] `git diff --stat` chỉ đụng `Dispatch.razor`, `app.css`, `App.razor` (+ file plan). KHÔNG đụng `suite/`,
      `orders/`, `Fleet.razor`.

## 5. Rủi ro & lưu ý

- **Việc này chạy trong worktree riêng**, song song với plan `2026-07-27-dung-job-khi-loi-ha-tang-toan-cuc.md` đang
  sửa `suite/`. Mọi đường dẫn quy về thư mục làm việc của agent; **tuyệt đối không đọc/ghi cây làm việc chính**.
- Đừng để lệch nguồn đếm giữa KPI và bảng (xem Bước 2) — số nói một đằng, bảng nói một nẻo là mất niềm tin vào
  cả trang.
- `Assignment.ShopId`/`BigsellerId` là GUID; hiện id trần lên UI là vô dụng với người vận hành — phải map ra tên.
- Không tự ý đổi ý nghĩa 4 KPI hiện có (người dùng đang quen số đó).

---

## Báo cáo thực thi (Opus điền sau khi xong)
