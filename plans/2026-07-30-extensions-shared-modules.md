# Plan: `extensions/shared/` — khử trùng lặp 3 extension (3G)

- **Ngày:** 2026-07-30
- **Trạng thái:** chờ (chạy SAU khi plan B1 merge — chung background.js orders)
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

3 extension (`shopee-search` 2471 dòng, `shopee-scrape` 984, `shopee-orders` ~2000 sau B1) chép tay lẫn nhau: ws-bridge (reconnect), tab-wait, verify/network-error detect (2 danh sách marker bổ khuyết nhau), dbg-input (trustedClick/dbgClick), sleep/rand. Hiện trạng manifest (30/07): search đã `"type":"module"`; scrape + orders còn classic service worker; orders có content_scripts (content.js 15 dòng gửi 'wake').

Ràng buộc vận hành: extension nạp THẲNG từ thư mục repo qua `--load-extension` (cả dev lẫn máy client sau release) → import tương đối ra ngoài thư mục extension KHÔNG hoạt động ⇒ mô hình: **nguồn chuẩn ở `extensions/shared/`, mỗi extension giữ bản copy checked-in trong thư mục nó, script sync tự chép + kiểm drift**.

## 2. Phạm vi

- **Làm:** như dưới. Khu: `extensions/**` + `release-suite.cmd`/`release-suite.sh` (thêm bước sync-check) + (nếu cần) script `tools/`.
- **Không làm:** KHÔNG hợp nhất 3 extension làm một (khác footprint quyền — chủ đích); KHÔNG đổi hành vi/delay các thao tác (anti-bot); KHÔNG đụng C# ngoài việc không có gì phải đổi (đường load giữ nguyên thư mục từng extension).

## 3. Các bước thực hiện

1. Chuyển `shopee-scrape` + `shopee-orders` manifest sang `"type":"module"` (MV3 service worker module; content_scripts của orders không ảnh hưởng). Kiểm tra không dùng `importScripts` sẵn có.
2. Tạo `extensions/shared/` với các module ES, nguồn lấy từ bản TỐT NHẤT hiện có (đối chiếu cả 3 trước khi viết):
   - `ws-bridge.js` — mẫu reconnect có guard socket-sống + huỷ timer của orders (`shopee-orders/background.js` sau fix 1C.1/1C.2 các bản đã fix nằm ở search — lấy bản ĐÃ FIX làm chuẩn), tham số hoá port + handler.
   - `tab-wait.js` — bản scrape (chuẩn nhất theo review 25/07).
   - `net-detect.js` — verify/network-error markers: tập HỢP của 2 danh sách hiện có (ghi bảng nguồn từng marker).
   - `dbg-input.js` — trustedClick/dbgClick/… (bản orders đang sống; search/scrape dùng phần nào lấy phần đó).
   - `util.js` — sleep/rand.
3. Mỗi extension: import từ bản copy nội bộ (vd `shopee-search/shared/ws-bridge.js`); XOÁ code trùng tại chỗ. Hành vi từng extension phải giữ nguyên — chỗ nào 2 bản lệch tham số (delay, số retry) thì tham số hoá theo bản của extension đó.
4. Script sync: `extensions/sync-shared.cmd` + `.sh` (copy `extensions/shared/*.js` → `shopee-*/shared/`); gắn vào `release-suite.cmd`/`.sh` TRƯỚC bước đóng gói; thêm mode `--check` (so hash, exit≠0 nếu drift) và gọi mode check trong release script.
5. `node --check` cả 3 background.js + các module.

## 4. Tiêu chí nghiệm thu

- [ ] `node --check` sạch toàn bộ file JS đổi.
- [ ] 3 manifest đều `"type":"module"`; extension load được (không kiểm tra tay được thì tối thiểu: cấu trúc import đúng, không tham chiếu importScripts).
- [ ] Grep các hàm trùng cũ (reconnect chép tay, sleep/rand bản cục bộ) = 0 ngoài shared/ + bản copy.
- [ ] `sync-shared --check` pass; release-suite.cmd/.sh có bước sync + check.
- [ ] Báo cáo: bảng "module nào lấy từ bản nào, extension nào lệch gì đã tham số hoá".

## 5. Rủi ro & lưu ý

- Chạy SAU B1 (B1 đang sửa `shopee-orders/background.js`).
- Đổi manifest sang module là thay đổi runtime thật — soi kỹ scrape/orders không dùng API chỉ có ở classic worker; nếu extension nào chuyển module gặp rào thật sự → giữ classic + dùng `importScripts` bản copy, ghi rõ.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
