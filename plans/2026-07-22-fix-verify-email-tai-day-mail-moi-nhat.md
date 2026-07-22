# Plan: Fix verify-email — lọc mail "Cảnh báo bảo mật", chỉ click "TẠI ĐÂY", ưu tiên mail mới nhất, xử lý link hết hạn

- **Ngày:** 2026-07-22
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Luồng auto-login Shopee có bước xác nhận qua email (mở hộp thư Hotmail/Outlook → tìm mail Shopee → click link xác nhận). Khi test thực tế phát hiện 4 vấn đề (file: `orders/XuLyDonShopee.Core/Services/ShopeeLoginService.cs`):

1. **Không lọc theo tiêu đề:** `FindAllShopeeMailRowsAsync` (:1338-1383) chỉ lọc người gửi = regex "shopee" (`ShopeeSenderRegex` :788), KHÔNG lọc tiêu đề → mail **thông báo trả hàng** Shopee cũng lọt vào danh sách.
2. **Click nhầm "here":** `ConfirmLinkRegex` (:796-797) khớp cả `click here|\bhere\b` → link "here" trong **mail trả hàng** bị click nhầm.
3. **Click mail cũ, không ưu tiên mail mới nhất:** vòng lặp `for` mail (:1171-1184) mở từ trên xuống (DOM top = mới nhất) nhưng **`return true` ngay ở mail ĐẦU TIÊN có link khớp** — mà regex khớp cả "here" nên trúng mail cũ/mail trả hàng trước. Khi test nhiều lần Shopee gửi rất nhiều mail xác nhận → app click mail cũ.
4. **Không nhận diện trang link hết hạn:** `ClickConfirmLinkInMailAsync` (:1211) poll text thành công (`ConfirmSuccessRegex` :802-803) ở :1280-1294 nhưng **`return true` VÔ ĐIỀU KIỆN** ở :1307 — kể cả khi trang báo *"Liên kết xác thực đã hết hiệu lực"* / *"Liên kết xác thực gửi qua Email đã hết hạn"*. Không có regex nào cho "hết hiệu lực"/"hết hạn"/"expired".

**Mục tiêu (theo phản hồi user):**
- Chỉ xử lý mail tiêu đề **"Cảnh báo bảo mật Tài khoản Shopee"** (là mail chứa link "TẠI ĐÂY" cần click).
- **Chỉ click chữ "TẠI ĐÂY"** (tiếng Việt), BỎ "here"/"click here" (tránh dính mail trả hàng).
- **Ưu tiên mail MỚI NHẤT** (trên cùng danh sách).
- Nếu click ra trang **hết hiệu lực/hết hạn** → **đóng tab đó, KHÔNG coi là thành công**, quay lại tìm mail **mới hơn** (reload hộp thư + chờ) để click tiếp.

## 2. Phạm vi

- **Làm:** 4 điểm trên, chỉ trong `ShopeeLoginService.cs` (luồng verify-email).
- **Không làm:** không đụng luồng login Microsoft (`LoginHotmailAsync`), không đụng module Scrape/đơn/hub, không đổi cấu trúc timeout tổng (8') / deadline duyệt mail (6').

## 3. Các bước thực hiện

### Bước 1 — Lọc tiêu đề "Cảnh báo bảo mật" + ưu tiên mail mới nhất
- `FindAllShopeeMailRowsAsync` (:1338-1383): ngoài lọc người gửi "shopee", thêm lọc **tiêu đề/nội-dung-dòng CHỨA** (chuẩn hoá bỏ dấu/space, so `Contains`) cụm **"cảnh báo bảo mật"** (dạng đủ: "Cảnh báo bảo mật Tài khoản Shopee"). Bỏ các mail Shopee khác (trả hàng, khuyến mãi...).
  - Nếu dòng danh sách chỉ có preview ngắn không đủ tiêu đề, cân nhắc đọc thêm ô tiêu đề trong mỗi row (selector chủ đề của Outlook) — miễn khớp đúng "cảnh báo bảo mật".
- **Giữ thứ tự mới-nhất-trước:** danh sách Outlook mặc định trên-cùng = mới nhất; vòng `for` (:1171-1184) mở top-down nên đã ưu tiên mới nhất. Đảm bảo KHÔNG đảo thứ tự. (Nếu cần chắc chắn, đọc thời gian mail để sort giảm dần — tùy chọn.)
- **RÀ:** có logic "bỏ qua mail cảnh báo" cũ nào còn sót không (commit cũ ce5deb4 từng "bỏ qua mail cảnh báo"); nếu có và mâu thuẫn với việc GIỜ phải xử lý đúng mail "Cảnh báo bảo mật" thì sửa cho nhất quán.

### Bước 2 — Chỉ click "TẠI ĐÂY", bỏ "here"
- `ConfirmLinkRegex` (:796-797): **bỏ** nhánh `click here|\bhere\b`. Giữ `tại đây|tại đấy|tai day` (và các cụm xác nhận tiếng Việt an toàn: `xác nhận|xac nhan|nhấn vào đây|bấm vào đây|đúng là tôi`). Với mail đã lọc đúng "Cảnh báo bảo mật", link cần bấm là "TẠI ĐÂY" — ưu tiên khớp cụm này. Không còn "here" nên không dính mail trả hàng.

### Bước 3 — Nhận diện trang hết hạn
- Thêm regex mới (cạnh `ConfirmSuccessRegex` :802-803), vd `ConfirmExpiredRegex` khớp: `hết hiệu lực|het hieu luc|hết hạn|het han|đã hết|expired|no longer valid`. Chuẩn hoá lower + bỏ dấu khi so cho chắc.

### Bước 4 — Đổi kết quả click để retry mail mới hơn
- `ClickConfirmLinkInMailAsync` (:1211): đổi kiểu trả về từ `bool` sang **enum** (vd `ConfirmOutcome { NoLink, Confirmed, Expired }`):
  - Không tìm thấy link → `NoLink` (như `false` cũ).
  - Trong poll kết quả (:1280-1294): nếu body khớp **ConfirmExpiredRegex** → đóng tab (:1299) → trả **`Expired`**.
  - Nếu khớp `ConfirmSuccessRegex` → `Confirmed`. Hết poll mà không rõ (không success, không expired) → **`Confirmed`** (GIỮ hành vi lạc quan cũ ở :1307 để không hồi quy các ca xác nhận thật nhưng thiếu text thành công).
- Vòng `for` mail (:1171-1184) xử theo enum:
  - `Confirmed` → `return true` (như cũ).
  - `NoLink` → `continue` (thử mail kế, như cũ).
  - `Expired` → **KHÔNG return true**; đóng tab đã xong; **quay lại tìm mail mới hơn**: thoát vòng `for` hiện tại để về nhánh **reload hộp thư + chờ 10-15s** (:1191-1198) của vòng `while` ngoài (:1148), rồi liệt kê lại (mới-nhất-trước) và thử tiếp. (Không nên click các mail cũ hơn trong danh sách hiện tại vì chúng còn dễ hết hạn hơn.) Vẫn tôn trọng deadline 6' / timeout 8'.

## 4. Tiêu chí nghiệm thu

- [ ] Build `dotnet build ShopeeSuite.sln` (Debug) thành công; `dotnet test orders/XuLyDonShopee.Tests` xanh (nếu có test cho regex thì thêm).
- [ ] `ConfirmLinkRegex` KHÔNG còn khớp "here"/"click here" (đối chiếu regex + test nếu có); vẫn khớp "TẠI ĐÂY"/"tại đây".
- [ ] `FindAllShopeeMailRowsAsync` chỉ trả mail tiêu đề chứa "cảnh báo bảo mật" (đối chiếu code); mail trả hàng/khác bị loại.
- [ ] Có `ConfirmExpiredRegex`; `ClickConfirmLinkInMailAsync` trả `Expired` khi trang báo hết hạn, `Confirmed` khi thành công/không rõ, `NoLink` khi không có link.
- [ ] Vòng lặp: gặp `Expired` → đóng tab + reload + chờ mail mới hơn (KHÔNG return true); gặp `Confirmed` → xong. Đối chiếu control-flow.
- [ ] Không đụng `LoginHotmailAsync` / timeout 8' / deadline 6'.

## 5. Rủi ro & lưu ý

- **Đừng hồi quy ca thành công:** giữ nhánh "không rõ kết quả → coi như Confirmed" như hành vi cũ (:1307), CHỈ tách riêng ca Expired. Nếu đổi cả ca "không rõ" thành thất bại sẽ làm login đang chạy tốt bị lặp vô ích.
- **Lọc tiêu đề quá chặt có thể loại nhầm** nếu Shopee đổi tiêu đề (vd thêm tên shop). Dùng `Contains "cảnh báo bảo mật"` (chuẩn hoá) thay vì so bằng tuyệt đối cả câu.
- **Thứ tự mới-nhất:** dựa vào DOM top = mới nhất của Outlook; nếu Outlook đang sort khác (theo cuộc hội thoại...) có thể sai — kiểm khi test thật. Đọc timestamp để sort là phương án chắc hơn nếu cần.
- **Reload liên tục khi expired** có thể quay vòng tới hết deadline 6' nếu mail mới chưa về — chấp nhận (có timeout). Log rõ "link hết hạn → chờ mail mới hơn" để soi khi test.
- Build **Debug** (app đang chạy bản Release để user test — Debug ghi bin/Debug, không tranh khóa).

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chờ thực thi>
