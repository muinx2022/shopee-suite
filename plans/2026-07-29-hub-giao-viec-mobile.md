# Plan: Hub Giao việc thân thiện mobile

- **Ngày:** 2026-07-29
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Auto (Cursor)

## 1. Bối cảnh & mục tiêu

Trang **Giao việc** (`/dispatch`) đã có một phần responsive (icon-rail sidebar ≤920px, `.m-hide`, KPI cuộn ngang, `.opbtn` to hơn) nhưng vẫn khó dùng trên điện thoại:

- Lưới shop × 3 cột op + bảng KPI/Đơn hàng phải **cuộn ngang** — khó bấm nút Scrape/Import/Update.
- Hàng lọc + panel tham số (`optsrow`) xếp ngang, input `width` cứng.
- Sidebar 64px chỉ-icon **cướp bề ngang** trên phone hẹp; không có overlay/đóng được.
- Tab nhãn dài; thẻ máy (`mcards`) lưới nhiều cột nhỏ.

Mục tiêu: thao tác giao/huỷ việc trên phone **không cần cuộn ngang** cho các bảng chính; menu điều hướng mở/đóng được; giữ nguyên hành vi desktop ≥921px.

## 2. Phạm vi

- **Làm:**
  - `MainLayout.razor` + `app.css`: drawer sidebar khi `max-width: 640px` (nút ☰ trên topbar, backdrop đóng).
  - `Dispatch.razor`: thêm `stack-sm` + `data-label` cho bảng KPI, bảng tab Đơn hàng, và lưới shop BigSeller (class `dispatch-shops`); rút gọn nhãn tab trên mobile (span `m-hide` / `m-only` nếu cần).
  - `app.css` khối `@media (max-width: 920px)` riêng `.dispatch`: `mcards` 1 cột hoặc cuộn ngang snap; `.bar` / `.optspanel` xếp dọc full-width; `.opbtn` full-width trong thẻ shop; `.tabs` full-width; tăng touch target.
  - Bump `app.css?v=` trong `App.razor`.
- **Không làm:**
  - Không đổi logic giao việc / API / DispatcherService.
  - Không redesign trang Fleet BigSeller (`/`) trừ phần layout chung (drawer).
  - Không đụng Orders/Config/Settings ngoài CSS layout chung.
  - Không deploy VPS trong plan này (user yêu cầu riêng sau).

## 3. Các bước thực hiện

1. **Drawer navigation (≤640px)** — `Components/Layout/MainLayout.razor`
   - State `_navOpen`; nút `.nav-toggle` trong `.topbar` (chỉ hiện mobile qua CSS).
   - Class `nav-open` trên `.app`; backdrop `.nav-backdrop` `@onclick` đóng.
   - Đóng nav khi `LocationChanged`.
   - CSS: mặc định ẩn sidebar off-canvas; `nav-open` trượt vào full-height; `.main { margin-left: 0 }`; ≥641px giữ icon-rail/sidebar như hiện tại (không hiện toggle).

2. **Dispatch markup** — `Components/Pages/Dispatch.razor`
   - Bốn bảng trong `.kpipanel`: thêm class `stack-sm`, mỗi `<td>` có `data-label="…"`.
   - Bảng tab BigSeller: `class="grid sm dispatch-shops"`, shop row `data-label` cho Shop + từng op (`OpLabel`); dòng `acctrow` giữ colspan.
   - Bảng tab Đơn hàng: `stack-sm` + `data-label`.
   - Tab buttons: text ngắn trên mobile — vd thêm `<span class="m-hide">…</span>` phần phụ hoặc class `tab-full` / `tab-short`.

3. **CSS Dispatch mobile** — `wwwroot/app.css` (trong/sau khối `@media (max-width: 920px)` cuối trang)
   - `.dispatch .mcards`: `grid-template-columns: 1fr` (hoặc flex cuộn ngang + snap).
   - `.dispatch .bar`, `.optspanel .optsrow`: `flex-direction: column; align-items: stretch`; input/select `width: 100% !important` (ghi đè inline width).
   - `.dispatch .tabs button`: `flex: 1`.
   - `.dispatch-shops` + stack: op buttons trong card `width: 100%` hoặc grid 1 cột; `.acctacts` / `.oacts` cột full-width.
   - `.opnote`: cho `white-space: normal` trên mobile (đọc hết lý do).

4. **Cache CSS** — `App.razor`: `app.css?v=34` → `v=35`.

5. **Nghiệm thu:** `dotnet build server/Shopee.Hub.Web`; grep không phá desktop (rule drawer chỉ ≤640px).

## 4. Tiêu chí nghiệm thu

- [ ] Phone ≤640px: mở ☰ thấy đủ chữ menu; bấm link/backdrop đóng.
- [ ] `/dispatch`: bảng KPI / shop / đơn hàng dạng thẻ (không bắt buộc cuộn ngang để thấy nút op).
- [ ] Panel tham số + hàng lọc xếp dọc, ô nhập dùng được ngón tay.
- [ ] Desktop ≥921px: layout Giao việc không đổi ý nghĩa (sidebar 250px, bảng cột như cũ).
- [ ] Build Hub Web thành công.

## 5. Rủi ro & lưu ý

- `stack-sm` trên dòng `acctrow` colspan: đã có rule `td[colspan] { display: block }` — kiểm tra cụm `.acctacts` không vỡ.
- Inline `style="width:80px"` trên input opts: cần `!important` hoặc bỏ inline → class `.opt-num`.
- Prefer bỏ/giảm inline width bằng class thay vì chỉ `!important` nếu sửa markup nhanh.

---

## Báo cáo thực thi (điền sau khi xong)

_(chưa)_
