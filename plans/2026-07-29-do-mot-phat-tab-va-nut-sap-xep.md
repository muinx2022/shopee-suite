# Plan: Dò tab + nút sắp xếp CHỈ MỘT PHÁT — mong manh với trang render từng mảnh

- **Ngày:** 2026-07-29
- **Trạng thái:** **CHỜ DỮ LIỆU** — người dùng đang cho chạy hết một vòng 12 shop để lấy bằng chứng.
  Người dùng chốt: *"nếu log không v.đ gì thì sẽ sửa lại phần dò đấy"* ⇒ **sửa dù log có sạch**, chỉ là cách sửa
  phụ thuộc kết quả (xem bảng ở mục 3).
- **Người lập:** Fable · **Người thực thi:** chưa giao

## 1. Vấn đề

Trong `doReadReturnRequests` (`extensions/shopee-orders/background.js`), ô tổng được **chờ tới 20 giây**, nhưng
hai thứ ngay sau đó chỉ được dò **đúng một lần**:

```javascript
// 2) Poll ≤20s chờ .return-list-summary-title
const ct  = await execInTab(tabId, pageLocateReturnCaseTab, [RETURN_TAB_RE]);  // ← MỘT PHÁT
const btn = await execInTab(tabId, pageLocateSortButton, []);                  // ← MỘT PHÁT
```

Nếu tab-strip / nút sắp xếp render chậm hơn ô tổng dù chỉ một nhịp, **cả hai cùng trượt**.

Đây đúng lớp lỗi đã cắn ở thẻ "Số tiền cuối cùng" (`2026-07-28-doc-uoc-tinh-theo-nhan-doanh-thu.md`): Shopee
render từng mảnh, dò một phát rồi bỏ là mong manh. Hôm đó mất 1/3 số đơn vì đúng kiểu này.

## 2. Bằng chứng đang có (chưa đủ kết luận)

Lượt 15:04 ngày 29/07, bản mới, shop `alina99.store` (**0 yêu cầu**):

```
15:04:59  extension: KHÔNG chọn được tab 'Đơn Trả hàng Hoàn tiền' — đọc theo tab đang mở.
15:05:05  extension: KHÔNG đổi được sắp xếp 'Ngày yêu cầu (Mới - Cũ)' — đọc theo thứ tự đang có.
15:05:07  Check đơn trả hàng [alina99.store]: 0 yêu cầu — không đổi so với mốc 0, bỏ qua.
```

**Cả hai cùng hỏng một lúc** — dấu hiệu nghiêng về đua render hơn là sai selector. Nhưng shop này có 0 yêu cầu,
mà trang rỗng cũng có thể không render tab-strip lẫn nút sắp xếp ⇒ **chưa loại trừ được**.

Hai cảnh báo này **hoạt động đúng thiết kế** (báo ra thay vì im lặng) — đó là phần đã làm được.

## 3. Bảng quyết định — đọc log cả vòng rồi chọn nhánh

| Log cho thấy | Kết luận | Cách sửa |
|---|---|---|
| **Mọi** shop đều trượt tab | Sai text/selector, hoặc dò sớm một cách hệ thống | Poll chờ tab **+ soi lại luật khớp text** trên HTML thật |
| Chỉ shop **0 yêu cầu** trượt; shop có yêu cầu thì được | Trang rỗng không render tab-strip/nút sắp xếp | Lành tính — **thôi cảnh báo khi số = 0**, vẫn thêm poll cho chắc |
| Trượt **lúc được lúc không** | Đua render, đúng như nghi | Poll chờ tab (giống cách đang chờ ô tổng) |

Shop đáng nhìn nhất: `deilca` (141 yêu cầu), `cicily` (340), `minoa` (122) — nhiều dữ liệu, trang nặng, nếu tab
bấm được ở đó thì nhánh 2 đúng.

## 4. Cách sửa (chung cho mọi nhánh)

- Poll `pageLocateReturnCaseTab` theo nhịp ~500ms, **trần ngắn (≈5s)** — không cần 20s vì trang đã render tới
  mức có ô tổng rồi; chờ lâu là nhân với 12 shop mỗi vòng.
- Tương tự cho `pageLocateSortButton`.
- **Không nới trần chờ ô tổng** trong việc này (20s) — chuyện khác, chưa có dữ liệu.
- Nếu vào nhánh 2: khi `soYeuCau == 0` thì **không** bắn hai cảnh báo (không có gì để quét, cảnh báo chỉ gây nhiễu
  log và làm người đọc tưởng hỏng).

## 5. ⚠ Bẫy

1. **Đừng poll dài.** 12 shop × mỗi vòng — thêm 20s/shop là +4 phút mỗi vòng cho thứ thường có sẵn ngay.
2. **Giữ nguyên nhánh `daDung`** (tab đã đúng thì KHÔNG bấm). Poll không được biến thành "bấm lại mỗi lần".
3. **Không chặn cả bước.** Hết trần vẫn phải đi tiếp với tab hiện tại + cảnh báo, y như đang làm.
4. Chẩn đoán hiện chỉ chạy khi **ô tổng** không đọc được. Nên gọi cả khi **tab** không tìm thấy — `pageChanDoanTraHang`
   đã trả sẵn `coTabWrapper`, chỉ là chưa gọi ở nhánh đó. Rẻ và dứt điểm.

## 6. Nghiệm thu

- [ ] `node --check` OK; jsdom rig `kiem-tab-tra-hang.js` vẫn 4/4 (hồi quy).
- [ ] Ca mới: tab-strip xuất hiện MUỘN (sau 1–2 nhịp) → vẫn tìm thấy và bấm được.
- [ ] Ca mới: không bao giờ có tab-strip → hết trần, đi tiếp, có cảnh báo (không treo, không ném).
- [ ] Đo và nêu rõ: thời gian XẤU NHẤT thêm vào mỗi shop.

---

## Báo cáo thực thi (điền sau khi xong)
