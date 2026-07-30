# Plan: Tách `shopee-search/background.js` thành module ES (đợt 4 — extension search)

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

`extensions/shopee-search/background.js` ~2.400 dòng — service worker MV3 (đã `"type":"module"` + đã import `shared/` sau 3G: ws-bridge, util, tab-wait, net-detect). Phần còn lại vẫn một file: flow keyword-search, flow shop, flow category, page-functions synthetic (bơm vào trang), extract kết quả, quản lý tab.

Mục tiêu (refactor thuần): tách thành ~6-7 module ES trong `shopee-search/` (KHÔNG phải shared/ — đây là code riêng của search): đề xuất `sw-main.js` (entry: đăng ký listener top-level + điều phối), `tabs.js`, `detect.js` (những gì chưa nằm ở shared/net-detect), `flow-keyword.js`, `flow-shop.js`, `flow-category.js`, `page-funcs.js` (hàm bơm vào trang — LƯU Ý các hàm này bị serialize độc lập, `const sleep`/helper bên trong thân hàm PHẢI GIỮ), `extract.js`. Manifest trỏ service worker sang entry mới (hoặc background.js giữ làm entry mỏng chỉ import — chọn cách ít đổi manifest nhất, ghi rõ). Khử 4 bản helper chuột trong page-funcs bằng pattern `pageInstallHelpers` (mẫu orders) NẾU các bản thật sự trùng — lệch thì giữ, ghi rõ.

## 2. Phạm vi & ràng buộc

- Chỉ đụng `extensions/shopee-search/**` (+ `extensions/sync-shared` nếu cần thêm đường check — thường không).
- MV3: mọi `chrome.*.addListener` phải ở top-level của module được import ngay từ entry (không đăng ký trong async callback).
- KHÔNG đổi hành vi/delay/selector; KHÔNG đụng shared/, extension khác, C#.
- KHÔNG commit.

## 3. Nghiệm thu

- [ ] `node --check` (qua bản .mjs) sạch mọi file; rig nạp module với `chrome.*` giả lập (mẫu 3G — dựng lại được từ mô tả trong plan 3G) nạp entry OK.
- [ ] Không file nào > ~600 dòng.
- [ ] `git diff --stat`: tổng số dòng không tăng quá +50 (chỉ tách + import, không viết mới).
- [ ] Bảng "hàm nào → module nào" + xác nhận listener top-level trong báo cáo.

## 5. Rủi ro & lưu ý

- Page-functions serialize: hàm truyền vào `chrome.scripting.executeScript({func})` không nhìn thấy import của module — mọi helper nó dùng phải nằm TRONG thân hàm hoặc install qua pattern pageInstallHelpers. Đây là chỗ dễ gãy nhất — soi từng page-func sau khi tách.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
