# Plan: Đợt E — Tách `extensions/shopee-orders/background.js` (1.909 dòng) theo khuôn shopee-search

- **Ngày:** 2026-08-06
- **Trạng thái:** chờ làm (chạy song song với đợt B được — không đụng file .NET, không chạy dotnet)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

`extensions/shopee-orders/background.js` là file JS dài nhất repo (1.909 dòng, 58 hàm top-level), trộn 4 tầng: ~50 hàm `page*` (DOM Seller Centre, ~77–916), hằng cấu hình (~935–957), hạ tầng `execInTab`/`pageInstallHelpers` (~959–999), toàn bộ flow điều khiển (`handleCommand` ~1000+: login, quét đơn, in phiếu, sửa địa chỉ, trả hàng). Extension shopee-search cùng cảnh (2.455 dòng) đã tách 9 module ES đợt 30/07 — orders bị loại khỏi phạm vi đợt đó (plans/2026-07-25-don1-2-extensions.md:16 ghi rõ). `manifest.json:12` đã `"type": "module"` nên import ES dùng được ngay.

Đây là cầu nối duy nhất của app Đơn hàng với Seller Centre — **không có test tự động**; an toàn dựa hoàn toàn vào tính CƠ HỌC của việc tách + phản biện đối chiếu. Shopee đổi selector thường xuyên nên giá trị của việc tách là diff các lần vá sau sẽ gọn.

## 2. Phạm vi

- **Làm:** tách file theo phần 3, THUẦN CƠ HỌC — mỗi hàm/hằng chuyển nguyên văn sang đúng một module, thêm import/export. KHÔNG đổi logic, selector, tên hàm, thứ tự await.
- **Không làm:** không sửa DOM logic, không đổi hợp đồng WS (`DEFAULT_PORT` 47821, tên/shape command — `OrdersBridgeChannel` phía C# phải khớp), không đụng file C#, không đụng extension khác, không đụng `shared/`.

## 3. Các bước thực hiện

1. Đọc cấu trúc shopee-search đã tách (core / page-funcs / flows / tabs / shared) làm khuôn.
2. Lập bảng phân loại 58 hàm + hằng của background.js (ghi vào báo cáo): mỗi hàm → module đích.
3. Tách thành các module trong `extensions/shopee-orders/`:
   - `core.js` — ws-bridge (kết nối, reconnect, gửi/nhận), state chung, `DEFAULT_PORT`.
   - `page-funcs.js` — toàn bộ hàm `page*` thuần DOM (chạy trong tab qua exec). Nếu quá dài thì tách đôi theo mảng (`page-funcs-orders.js` / `page-funcs-returns.js`) — quyết theo ranh giới tự nhiên, ghi lại.
   - `exec.js` — `execInTab`, `pageInstallHelpers`, helpers bơm hàm vào tab.
   - `flow-orders.js` — quét đơn, chuẩn bị hàng, in phiếu.
   - `flow-returns.js` — trả hàng.
   - `flow-address.js` — đặt/sửa địa chỉ lấy hàng.
   - `constants.js` — hằng cấu hình (~935–957).
   - `background.js` còn lại: import + đăng ký listener + dispatch `handleCommand`.
4. **Ràng buộc kỹ thuật quan trọng**: các hàm `page*` được serialize bơm vào tab (`func.toString()` / exec) — hàm bơm KHÔNG được tham chiếu biến ngoài closure của module (import ở module scope không tồn tại trong tab). Đọc kỹ cách shopee-search xử lý (page-funcs là hàm tự chứa) và giữ đúng luật đó; hàm nào đang tự chứa thì sau tách PHẢI vẫn tự chứa.
5. Kiểm chứng tĩnh:
   - `node --check` từng file mới (syntax).
   - Script đối chiếu: tổng số hàm trước = sau, mỗi hàm xuất hiện đúng 1 lần, mọi call-site resolve (grep tên hàm ↔ export/import khớp).
   - `sync-shared.cmd --check` vẫn pass (không đụng shared/ nhưng chạy cho chắc).
6. Kiểm tra `PrepareFreshExtensionCopy` phía C# (orders Core) — đã chép ĐỆ QUY từ v1.7.5 (memory orders-bridge-extension-copy-recursive) nên file mới ở gốc extension được chép đủ; xác nhận lại bằng cách đọc hàm đó (KHÔNG sửa).

## 4. Tiêu chí nghiệm thu

- [ ] `node --check` pass mọi file .js mới + background.js.
- [ ] Bảng phân loại 58 hàm trong báo cáo; script đối chiếu chứng minh không hàm nào mất/trùng.
- [ ] `background.js` mới ≤ ~200 dòng (wiring thuần).
- [ ] Tổng dòng các file mới ≈ 1.909 (± import/export boilerplate) — không viết lại, chỉ move.
- [ ] Grep `DEFAULT_PORT` = 47821 không đổi; tên command trong `handleCommand` khớp nguyên bộ với `OrdersBridgeChannel` phía C# (liệt kê đối chiếu trong báo cáo).
- [ ] KHÔNG file nào ngoài `extensions/shopee-orders/` bị sửa.

## 5. Rủi ro & lưu ý

- **Không có test tự động cho extension** — lỗi chỉ lộ khi user chạy vòng đơn thật. Vì vậy: (a) thuần cơ học tuyệt đối, (b) phản biện sẽ đối chiếu từng hàm, (c) sau khi phát hành, lần chạy vòng đơn đầu tiên user cần để ý log "chờ extension" (triệu chứng SW chết câm — xem memory v1.7.5).
- Service worker MV3: import tĩnh ES module fail (syntax/path) là SW chết KHÔNG log — node --check bắt syntax nhưng không bắt path sai; tự rà lại từng dòng import bằng mắt + đường dẫn tương đối `./`.
- KHÔNG commit.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
