# Plan: Đợt 1 — Sửa bug hành vi app Đơn hàng (orders/)

- **Ngày:** 2026-07-25
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Plan cha:** `plans/2026-07-25-ke-hoach-refactor-toan-app.md` (mục 1B)

## 1. Bối cảnh & mục tiêu

App Đơn hàng (`orders/XuLyDonShopee.*`) đã chuyển từ Playwright sang **extension bridge**: C# (`OrdersBridgeSession`) nói chuyện với extension `extensions/shopee-orders/` qua WebSocket cổng cố định 47821 (`OrdersWebSocketServer`). Đợt review 2026-07-25 phát hiện 4 bug hành vi + 1 bug extension phía orders. User đã chốt: nút "Tải phiếu" sửa bằng cách **làm action qua bridge** (không gỡ nút).

Lưu ý mô hình: `AccountSession.RunAsync` (Playwright cũ) là code chết — `StartAsync` chỉ gọi `RunBridgeContinuousAsync` (`orders/XuLyDonShopee.App/Services/AccountSession.cs:183`). KHÔNG dọn code chết trong plan này (sẽ có plan riêng ngay sau) — chỉ sửa bug, đụng ít file nhất có thể.

## 2. Phạm vi

- **Làm:** 5 hạng mục dưới; file thuộc `orders/` và `extensions/shopee-orders/`.
- **Không làm:** dọn code chết (plan sau); KHÔNG đụng `extensions/shopee-search/`, `extensions/shopee-scrape/`, `extensions/shopee-orders-test/`, `suite/`, `server/`.

## 3. Các bước thực hiện

### Bước 1 — Nút "Tải phiếu" hoạt động lại qua bridge

Hiện trạng hỏng: `AccountSession.RedownloadSlipAsync` (`orders/XuLyDonShopee.App/Services/AccountSession.cs:869-891`) kiểm tra `_session` — biến này CHỈ được gán trong `RunAsync` chết (dòng 2018) → luôn null → luôn trả false với thông báo sai "tài khoản đang bận thao tác khác". UI vẫn gọi từ `OrdersViewModel.cs:423` và `OrderRowViewModel.cs:157`.

Sửa:
1. **Extension** `extensions/shopee-orders/background.js`: thêm action `redownloadSlip` (payload gồm mã đơn `orderSn` + thông tin shop nếu cần). Nghiên cứu flow tải phiếu hiện có trong `doPrepareNextOrder` (phiếu trả về base64 qua message hiện hành) và TÁI DÙNG đúng đường đó: điều hướng tới đơn tương ứng → tải phiếu → trả `{ slipBase64, orderSn }`. Nếu flow hiện tại chỉ tải được phiếu của đơn đang mở, chấp nhận ràng buộc "chỉ tải lại được khi phiên đang ở đúng shop" — báo lỗi rõ ràng khi không đúng shop.
2. **C#** `OrdersBridgeSession`: thêm `RedownloadSlipAsync(orderSn, ct)` — gửi action + TCS chờ kết quả (timeout ~120s, dùng cùng khuôn TCS hiện có), nhận base64, lưu PDF qua đúng đường lưu phiếu hiện hành (cùng thư mục + cùng cách đặt tên file mà `ThieuPhieu`/`TryReadSlipBase64` đang đọc, để cột "thiếu phiếu" tự hết đỏ).
3. `AccountSession.RedownloadSlipAsync`: route sang bridge session đang chạy (field giữ tham chiếu `OrdersBridgeSession` hiện hành của `RunBridgeContinuousAsync`); nếu phiên không chạy → trả false + thông báo đúng: "Hãy bấm Chạy tài khoản rồi mới tải lại phiếu".
4. Chữ ký `IAccountSession`/UI giữ nguyên.

### Bước 2 — Guard 1-bridge-một-lúc (chống kill chéo + tranh cổng 47821)

Hiện trạng: cổng bridge cố định `BridgePort = 47821` (`OrdersBridgeSession.cs:143`); `KillBrowsersOnProfile` (`OrdersBridgeSession.cs:226-227`) giết MỌI trình duyệt có `'shopee-orders'` trong command line — không giới hạn profile account hiện tại. UI cho "Chạy đã chọn" nhiều tài khoản (`AccountsViewModel.cs:1005-1015`) → phiên 2 giết trình duyệt phiên 1 + bind cổng fail → Error.

Sửa tại `AccountSessionManager` (`orders/XuLyDonShopee.App/Services/AccountSessionManager.cs`): chỉ cho phép 1 session bridge chạy đồng thời; account được Start khi đang có phiên chạy thì vào hàng đợi FIFO, state hiển thị dạng "Chờ đến lượt"; khi phiên trước Stopped THẬT SỰ (xem bước 5) thì tự start phiên kế. Stop một account đang xếp hàng = rút khỏi hàng. Không đổi hành vi khi chỉ chạy 1 account.

### Bước 3 — `OrdersWebSocketServer.SendAsync` fail-fast

Hiện trạng: `orders/XuLyDonShopee.Core/Services/OrdersWebSocketServer.cs:112-115` — socket chưa/mất kết nối thì `return` im lặng → caller ngồi chờ TCS 30–300s rồi nhận `TimeoutException` với thông điệp sai hướng.

Sửa: `SendAsync` trả `bool` (false khi `ws?.State != Open`) hoặc ném `InvalidOperationException` — chọn phương án ít lan toả nhất với các call-site trong `OrdersBridgeSession`; caller phân biệt được "extension mất kết nối" (fail ngay, thông báo đúng) với "extension kẹt" (timeout). Tiện thể: cache `JsonSerializerOptions` static (hiện tạo mới mỗi lần gọi, dòng 117-121).

### Bước 4 — Chỉ fault TCS đang chờ khi extension báo lỗi

Hiện trạng: `OrdersBridgeSession.cs:871-888` — khi extension báo `error`, fault cả 11 TCS trong khi chỉ 1 cái đang được await → 10 exception không ai observe → `UnobservedTaskException`.

Sửa: chỉ fault TCS của chặng đang chờ; các TCS còn lại dùng `TrySetCanceled` (hoặc giữ nguyên + field `_lastError` để chặng kế đọc). Bảo đảm không còn UnobservedTaskException trong kịch bản extension báo lỗi.

### Bước 5 — `StopAsync` không khai tử sớm

Hiện trạng: `AccountSession.cs:213,240` — chờ vòng nền tối đa 8s rồi đặt thẳng `State = Stopped` → `AccountSessionManager.OnSessionChanged` (`AccountSessionManager.cs:146-150`) gỡ phiên khỏi dict → user bấm Chạy lại tạo phiên MỚI cùng profile + cùng cổng trong khi phiên cũ còn tháo dỡ (bước login Playwright có thể sống quá 8s).

Sửa: chỉ đặt `Stopped` khi `_runTask` thật sự hoàn tất; quá 8s thì giữ trạng thái "Đang dừng…" (thêm state nếu cần) và khoá nút Chạy của account đó; kết hợp với hàng đợi bước 2 (phiên kế chỉ start khi phiên trước Stopped thật).

### Bước 6 — Test + build

- Thêm test cho bước 3 + 4 (fake WS server bắn message — `OrdersBridgeSession` nhận `_ws` qua abstraction sẵn có; nếu phải thêm seam nhỏ thì được phép, giữ tối thiểu). Test hàng đợi bước 2 ở mức `AccountSessionManager` nếu tách được khỏi UI.
- `dotnet build ShopeeSuite.sln` + `dotnet test orders/XuLyDonShopee.Tests` — toàn bộ xanh.

## 4. Tiêu chí nghiệm thu

- [ ] Build sạch, toàn bộ test cũ + mới xanh.
- [ ] `RedownloadSlipAsync` khi phiên đang chạy → gửi action `redownloadSlip` và lưu PDF đúng thư mục phiếu; khi phiên không chạy → thông báo "Hãy bấm Chạy tài khoản…" (không còn thông báo "đang bận" sai).
- [ ] Start account thứ 2 khi account 1 đang chạy → account 2 ở trạng thái chờ, KHÔNG kill trình duyệt account 1, KHÔNG bind cổng 47821 lần 2; account 1 dừng xong thì account 2 tự chạy.
- [ ] SendAsync khi chưa có extension nối → caller fail ngay với thông điệp "extension chưa kết nối", không chờ 30-300s.
- [ ] Kịch bản extension báo `error`: không phát sinh UnobservedTaskException (kiểm bằng test).
- [ ] Grep không còn đường nào đặt `State = Stopped` khi `_runTask` chưa hoàn tất.

## 5. Rủi ro & lưu ý

- Action `redownloadSlip` phía extension phải theo ĐÚNG phong cách message hiện có của `shopee-orders/background.js` (envelope `action`/`kind`, cách trả pageData) — không phát minh format mới.
- Không đổi hành vi flow đơn hiện hành (prepareNextOrder…) — chỉ THÊM action.
- `shopee-orders/background.js` cũng đang được plan extensions khác sửa các file **shopee-search/shopee-scrape** — không đụng 2 extension đó để tránh conflict.
- Hàng đợi bước 2: cẩn thận đường auto-start (nếu có) và "Chạy đã chọn" — mọi đường vào Start đều phải qua hàng đợi.

---

## Báo cáo thực thi (Opus điền sau khi xong)
