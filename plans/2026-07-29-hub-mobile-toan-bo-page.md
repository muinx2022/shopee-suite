# Plan: Hub — rà soát & sửa mobile toàn bộ page

- **Ngày:** 2026-07-29
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Auto (Cursor)

## 1. Bối cảnh & mục tiêu

Sau đợt Giao việc + drawer ≤640px, còn nhiều trang Hub trên phone phải **cuộn ngang** hoặc lọc/input width cứng khó dùng. Mục tiêu: mọi trang điều hướng chính dùng được trên mobile (thẻ `stack-sm` hoặc lọc xếp dọc); desktop ≥921px không đổi ý nghĩa.

## 2. Phạm vi

### Đã ổn (không đụng / chỉ giữ)
- `/dispatch` (đã làm), `/machines` (stack-sm chính), `/config/errored`, `/config/ai`, `/config/orders`, `/logs-view`, layout drawer.

### Cần sửa
| Page | Vấn đề | Cách |
|------|--------|------|
| `/orders` | Bảng rộng, lọc `width:240px` | `stack-sm` + `data-label`; `bar-stack`; class search |
| `/shops` | Bảng không stack | `stack-sm` + `data-label` |
| `/files` | Bảng không stack | `stack-sm` + `data-label` |
| `/config/accounts` | 7 cột input width cứng | `stack-sm` + input full-width mobile; `bar-stack` |
| `/search` | Bảng máy + form row | `stack-sm` máy; `.editcard .row` xếp dọc |
| `/machines` (bảng chặn) | 2 cột plain | `stack-sm` |
| `/` Fleet | Bảng op shop nhỏ | `stack-sm` các bảng trong `wsmain` |
| `/data` + ProductGridPanel | Hàng lọc ngang | `bar-stack`; giữ cuộn ngang bảng SP (quá dày để stack) |
| Settings | Token 440px | Đã `max-width:100%` — thêm `bar-stack` nếu cần |

- **Không làm:** Đổi logic/API; redesign Fleet workspace master-detail; stack bảng sản phẩm AllData (giữ scroll + cap height).

## 3. Các bước thực hiện

1. **CSS chung** (`app.css`): `.bar-stack` (cột, input/select 100%); `.editcard .row` mobile cột; bump `app.css?v=36`.
2. **Orders / Shops / Files / Machines(revoked) / Search**: markup `stack-sm` + `data-label`.
3. **ConfigAccounts**: `stack-sm` + `data-label`; class `cfg-field` thay width inline (desktop giữ width hợp lý qua CSS).
4. **Fleet**: thêm `stack-sm` + label cho bảng trạng thái op (và bảng tương tự nếu có).
5. **AllData + ProductGridPanel**: class `bar-stack` trên hàng lọc.
6. Build Hub Web.

## 4. Tiêu chí nghiệm thu

- [ ] Orders/Shops/Files/ConfigAccounts/Search/Machines(revoked): trên ≤920px dạng thẻ, không bắt buộc cuộn ngang để thấy nút chính.
- [ ] Lọc Orders/Data xếp dọc, ô nhập dùng được.
- [ ] Desktop bảng ConfigAccounts vẫn nhiều cột (stack chỉ ≤920px).
- [ ] `dotnet build` Hub OK.

## 5. Rủi ro

- ConfigAccounts stack: form dài — chấp nhận để sửa được trên phone.
- Product grid không stack: vẫn cuộn ngang có chủ đích.

---

## Báo cáo thực thi

- Đã `stack-sm` + `data-label`: Orders, Shops, Files, ConfigAccounts, Search (máy + SP), Machines (chặn), Fleet (2 bảng).
- `bar-stack` / form xếp dọc: Orders, ConfigAccounts, AllData, ProductGridPanel; Settings/Search bỏ width cứng.
- CSS chung `.bar-stack` + `.editcard .row` trong `@media ≤920px`; `app.css?v=36`.
- Giữ cuộn ngang bảng SP AllData (cố ý). Build Hub Release OK. Chưa commit code / chưa deploy.
