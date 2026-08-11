# Plan: Làm nốt việc còn tồn sau v1.8.8

- **Ngày:** 2026-08-11
- **Trạng thái:** hoàn thành (phần client); còn 1 bước deploy Hub cần mật khẩu sudo của user
- **Người lập / thực thi:** phiên chính (tự làm, không giao subagent)

## 1. Bối cảnh & mục tiêu

Bản v1.8.8 đã phát hành (vòng chạy trọn 12/12 shop, 0 cú đứt cầu nối). User yêu cầu: *"còn những gì thì bạn
làm nốt đi, sau đó build release luôn"*. Đây là danh sách việc **còn tồn** đã ghi trong CHANGELOG v1.8.8 và
trong các lượt trao đổi trước.

Ràng buộc quan trọng: vòng chạy shop đang ỔN ĐỊNH (12/12, 0 lỗi). **Không được đụng vào đường chạy vòng** trừ
khi có lý do đo được. Mọi việc dưới đây đều nằm NGOÀI đường chạy vòng.

### Quyết định đã chốt từ trước (không mở lại)

- Đồng bộ mật khẩu: lưu **dạng thường** (plain text), giống hệt đường BigSeller đang làm
  (`FileStoreConfigService.UpdateSharedAccountFields` đã đồng bộ `Password` + `EmailPassword`).
- Quy tắc gộp: **"vá ô trống, không đè"**.

## 2. Phạm vi

**Làm:**

1. Sửa lỗi tách `--user-data-dir` khi đường dẫn có dấu cách (`BraveProcessReaper.ExtractUserDataDir`).
2. Đồng bộ 3 trường mật khẩu tài khoản Đơn hàng giữa client và Hub: `Password`, `VerifyEmail`,
   `VerifyEmailPassword`.
3. Xoá dead code `extensions/shopee-orders/chan-chat.js` (thí nghiệm chặn SDK chat, đã chứng minh vô can).
4. `.gitignore` cho `.claude/` (thư mục cấu hình local của Claude Code, đang untracked lửng lơ).

**Không làm (có lý do):**

- **Chặn Chrome cho cầu nối.** Ý định ban đầu: Chrome 137+ bỏ `DisableLoadExtensionCommandLineSwitch` nên
  `--load-extension` im lặng không nạp ⇒ cầu nối treo 45s. NHƯNG cách chữa duy nhất là ép `Auto` → Brave cho
  cầu nối, mà `BrowserLocator` cố ý ưu tiên Chrome/Edge vì **Brave bật sẵn chống-fingerprint nên ăn captcha
  nhiều hơn**. Đổi = đánh cược trên đúng luồng vừa chạy sạch 12/12. Máy user đang chạy Brave nên lỗi này không
  chạm tới họ. Để lại, ghi vào "Còn tồn".
- **Trần thử lại backfill "Số tiền cuối cùng".** Đã có trần rồi: `UocTinhDon.ChonDonLayUocTinh` giới hạn
  ≤7 ngày + **trần 5 đơn/lượt**. Đơn hỏng vĩnh viễn chỉ tốn tối đa 5 lượt mở chi tiết mỗi vòng — không đáng
  đánh đổi rủi ro sửa đường chạy vòng.

## 3. Các bước thực hiện

### Việc 1 — `ExtractUserDataDir` tách sai đường dẫn có dấu cách

**Bệnh (đã đo).** Phía orders giao args qua `ProcessStartInfo.ArgumentList` (`BraveArgs.CreateRaw()` — KHÔNG tự
bọc ngoặc). .NET bọc ngoặc **cả tham số** khi nó chứa dấu cách, nên dòng lệnh thật là:

```
"--user-data-dir=C:\Users\Ng Xuan Mui\AppData\...\acc_1" --profile-directory=Default ...
 ^ dấu " đứng TRƯỚC --user-data-dir, không phải sau dấu =
```

`ExtractUserDataDir` chỉ xét `rest[0] == '"'` (dạng `--user-data-dir="..."`), không xét dạng bọc-cả-tham-số →
rơi xuống nhánh cắt-tại-dấu-cách → trả `C:\Users\Ng`. Hệ quả: `BraveFleet.EnumerateOurBrave` không nhận ra
trình duyệt của app ⇒ **bước dọn hồ sơ mồ côi lúc khởi động không chạy** trên mọi máy có dấu cách trong đường
dẫn hồ sơ (tức là máy của user). `BraveWindowMinimizer` cũng dùng chung hàm này.

**Sửa.** Thay cách bóc chuỗi bằng **tách tham số đúng luật ngoặc** rồi tìm token bắt đầu bằng
`--user-data-dir=`. Xử đúng cả ba dạng: không ngoặc · ngoặc sau dấu `=` · ngoặc bọc cả tham số.

- File: `suite/Shopee.Core/Browser/BraveProcessReaper.cs` — viết lại `ExtractUserDataDir`, thêm hàm thuần
  `TachThamSo(string)`.
- Test mới: `suite/Shopee.Core.Tests/TachDuongDanHoSoTests.cs` — ma trận 3 dạng ngoặc × có/không dấu cách,
  cộng ca cờ đứng cuối dòng lệnh và ca không có cờ.

**Lưới an toàn đã có sẵn** (không cần thêm): `SweepOrphans` chỉ chạy khi `IsSoleAppInstance()`, và root hồ sơ
orders đăng ký ở chế độ `chiQuetLucKhoiDong: true` (`OrdersModuleHost.cs:91`) nên KHÔNG bị quét ở nhịp định kỳ.
Sửa parser chỉ làm bước dọn khởi động **bắt đầu hoạt động đúng như thiết kế**.

### Việc 2 — Đồng bộ 3 trường mật khẩu client ↔ Hub

**Hiện trạng.** Ba trường đã có đủ ở client (`Account.Password/VerifyEmail/VerifyEmailPassword`, cột SQLite,
form nhập) nhưng bị **cố ý chặn ở ranh giới DTO**: `OrdersAccountItem` không có, `orders_accounts` không có
cột, Hub không có UI. Quyết định "gương không giữ mật khẩu" được ghi ở 6 chỗ — lần này ĐẢO lại, phải sửa hết
các chú thích đó cho khớp, không để lại lời hứa sai trong code.

**Vì sao cần.** Máy MỚI kéo danh bạ từ Hub (`HubDirectoryPuller`) chỉ tạo được bản ghi **rỗng mật khẩu** →
user phải gõ tay 3 ô cho từng tài khoản trên từng máy.

**Luật gộp — hai đầu khác nhau, cố ý:**

| Chiều | Luật | Vì sao |
|---|---|---|
| client → Hub | ô Hub trống thì ghi; **incoming rỗng KHÔNG xoá** giá trị Hub đang có | Hiện `UpsertOrdersAccounts` xoá-rồi-ghi-lại cả danh bạ của máy đó mỗi lượt đẩy (3s/lần). Không giữ lại là mật khẩu bay ngay lượt sau. Máy chưa nhập mật khẩu không được phép xoá dữ liệu tốt của máy khác. |
| Hub → client | **vá ô trống, không đè** | Đúng chữ user chốt. "Ô" = ô nhập trên form client. Không bao giờ đè thứ user đã gõ. |

Chiều client→Hub cố ý cho **giá trị mới khác rỗng ghi đè giá trị cũ** — nếu đóng băng vĩnh viễn giá trị đầu
tiên thì user đổi mật khẩu Shopee xong Hub giữ mãi bản cũ rồi phát tán sang máy mới.

**Các file phải sửa:**

1. `suite/Shopee.Core/Coordination/HubDtos.cs`
   - `OrdersAccountItem` += 3 property `{ get; init; } = ""` (KHÔNG dùng tham số positional có giá trị mặc
     định — thêm vào thân record thì System.Text.Json bỏ qua field thiếu và giữ giá trị khởi tạo, client/hub
     bản cũ vẫn parse được).
   - `OrdersDirectoryAccount` += 3 property tương tự.
   - Sửa khối chú thích "TUYỆT ĐỐI không nhận mật khẩu" (dòng ~270–273) cho đúng hiện trạng mới.
2. `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.Mirror.cs` — `BuildOrdersMirror` nhét 3 trường vào
   payload; sửa chú thích dòng 19.
3. `server/Shopee.Hub.Web/Data/HubDatabase.cs` — 3 dòng `AddColumnIfMissing("orders_accounts", …)`
   (schema vá tay, không EF migration).
4. `server/Shopee.Hub.Web/Data/HubDatabase.OrdersAccounts.cs`
   - `UpsertOrdersAccounts`: đọc 3 trường CŨ của máy đó **trước** khối DELETE, gộp theo luật bảng trên rồi mới
     INSERT. Tách hàm thuần `GiuLaiNeuRong(string? moi, string? cu)` để test không cần DB.
   - `OrdersMirrorAccount` += 3 field; `OrdersAccountsOf` đọc thêm cột.
   - `AllOrdersAccountsDistinct`: gộp theo login lấy **giá trị khác rỗng đầu tiên** trên mọi máy.
5. `server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs` — endpoint directory trả kèm 3 trường; sửa chú thích
   dòng 208–219.
6. `orders/XuLyDonShopee.App/Services/HubDirectoryPuller.cs` — tài khoản TẠO MỚI nhận luôn 3 trường; tài khoản
   ĐÃ CÓ mà ô nào trống thì vá ô đó (hàm thuần `VaOTrong`), không đụng ô đã có chữ.
7. `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.HubRead.cs` — mang 3 trường từ DTO xuống puller.

**Test mới:** `server/Shopee.Hub.Web.Tests/DongBoMatKhauGuongTests.cs` (luật `GiuLaiNeuRong` + vòng
upsert→đọc lại trên DB tạm) và `orders/XuLyDonShopee.Tests/VaOTrongTuHubTests.cs` (luật `VaOTrong`).

### Việc 3 — Xoá `chan-chat.js`

`CHAN_SDK_CHAT = false` từ lúc thí nghiệm thất bại (bệnh gốc thật là vòng nhận WebSocket nằm trên thread pool,
đã sửa ở v1.8.8). File chỉ còn là bẫy đọc hiểu. Xoá file; 3 chỗ `import { ensureDbgChanChat }`
(`flow-shop.js`, `flow-returns.js`, `flow-orders.js`) trả về `ensureDbg` từ `./shared/dbg-input.js`.
Hành vi giữ nguyên tuyệt đối: với cờ `false`, `chanSdkChat` đã return ngay ở dòng đầu.

### Việc 4 — `.gitignore` cho `.claude/`

Thêm `.claude/` vào `.gitignore` (cấu hình local + worktree tạm của Claude Code, không thuộc sản phẩm).

## 4. Tiêu chí nghiệm thu

- [ ] `ExtractUserDataDir` trả ĐÚNG `C:\Users\Ng Xuan Mui\acc_1` cho cả 3 dạng ngoặc — test đỏ khi hoàn nguyên code.
- [ ] Đẩy gương 2 lượt (lượt 2 mật khẩu rỗng) → Hub vẫn giữ mật khẩu lượt 1.
- [ ] Đẩy gương với mật khẩu MỚI khác rỗng → Hub cập nhật.
- [ ] Kéo danh bạ về máy có tài khoản mật khẩu đã nhập → KHÔNG bị đè; ô trống thì được vá.
- [ ] `dotnet build ShopeeSuite.sln` — **0 warning, 0 error**.
- [ ] Toàn bộ test xanh: orders + suite core + hub.
- [ ] Mỗi test MỚI phải THỬ PHÁ được (sửa ngược code → test ĐỎ → khôi phục).
- [ ] `extensions/sync-shared.cmd --check` OK.
- [ ] App mở lại được, chạy được 1 vòng shop không lỗi mới.

## 5. Rủi ro & lưu ý

- **Hub phải deploy thì việc 2 mới có tác dụng đủ.** Client bản mới đẩy 3 field lên Hub bản cũ → Hub bỏ qua
  field lạ (System.Text.Json mặc định), vô hại nhưng vô ích. Bước deploy VM cần mật khẩu sudo ⇒ **phải hỏi
  user**, không tự làm.
- **Đây là đảo một quyết định bảo mật.** Sau thay đổi này, mật khẩu tài khoản phụ + mật khẩu hòm thư đi qua
  mạng (HTTPS, có `X-Api-Token`) và nằm **dạng thường** trong `hub.db` trên VM. Đường BigSeller vốn đã làm y
  hệt nên tư thế bảo mật của hệ thống không đổi về CHẤT — nhưng phải nói rõ trong CHANGELOG, không giấu.
- **Đừng đụng đường chạy vòng shop.** Không sửa `ShopFlowRunner`, `OrdersBridgeSession`, `OrdersBridgeChannel`,
  `WebSocketServer` trong đợt này.

---

## Báo cáo thực thi

### Đã làm

| Việc | File chính | Test |
|---|---|---|
| 1. Tách `--user-data-dir` đúng luật ngoặc | `suite/Shopee.Core/Browser/BraveProcessReaper.cs` (`ExtractUserDataDir` + `TachThamSo`) | `suite/Shopee.Core.Tests/TachDuongDanHoSoTests.cs` — 11 ca |
| 2. Đồng bộ 3 ô đăng nhập client ↔ Hub | 7 file (DTO · mirror · schema · merge · endpoint · puller · map DTO) | `server/Shopee.Hub.Web.Tests/DongBoMatKhauGuongTests.cs` — 17 ca · `orders/XuLyDonShopee.Tests/VaOTrongTuHubTests.cs` — 8 ca |
| 3. Xoá `chan-chat.js` | 3 file flow quay về `ensureDbg` | `ExtensionJsCuPhapTests` (sẵn có) + vòng chạy thật |
| 4. `.gitignore` cho `.claude/` | `.gitignore` | — |

Sửa kèm cho khỏi để lại lời hứa sai trong code (quyết định "gương không giữ mật khẩu" được ghi ở 6 chỗ):
`HubDtos.cs`, `HubRoutes.cs`, `HubDatabase.OrdersAccounts.cs`, `ClientApiEndpoints.cs`,
`OrdersModuleHost.Mirror.cs`, `DispatchOrdersTab.razor`.

### Kiểm chứng

- `dotnet build ShopeeSuite.sln` → **0 warning / 0 error**; `dotnet build server/Shopee.Hub.Web` → **0/0**
  (Hub KHÔNG nằm trong `ShopeeSuite.sln` — phải build riêng, suýt trượt).
- Test: orders **1776** (1768 → +8) · suite core **139** (128 → +11) · hub **137** (120 → +17). Tất cả xanh.
- `extensions/sync-shared.cmd --check` → OK.
- **THỬ PHÁ đủ 4 luật mới**, mỗi lần sửa ngược code rồi chạy lại:
  - hoàn nguyên `ExtractUserDataDir` về bóc chuỗi → **4/11 ĐỎ**;
  - `GiuLaiNeuRong` cho ô rỗng ghi đè + `OCoChuDauTien` lấy máy sau → **6/17 ĐỎ**;
  - `VaOTrong` bỏ điều kiện "ô đang trống" → **3/8 ĐỎ**.
  Khôi phục xong chạy lại: xanh hết.

### Quyết định giữ nguyên khi thực thi

- Luật gộp hai đầu KHÁC nhau (client→Hub: rỗng không xoá, khác rỗng ghi đè · Hub→client: vá ô trống, không đè).
  Đây là diễn giải chữ "vá ô trống, không đè" theo nghĩa **ô nhập trên form client**. Nếu ý ban đầu là đóng
  băng giá trị đầu tiên ở phía Hub thì sửa `GiuLaiNeuRong` một dòng là xong (test sẽ đỏ và nói rõ chỗ nào).
- Không đụng `ShopFlowRunner` / `OrdersBridgeSession` / `OrdersBridgeChannel` / `WebSocketServer` — đúng cam kết.

### Còn lại

- **Deploy Hub lên VM** (`dotnet publish` → scp → `install` + `systemctl restart shopee-hub`) — bước sudo cần
  mật khẩu của user. Chưa làm. Chưa deploy thì client mới đẩy 3 ô lên Hub cũ, Hub bỏ qua field lạ: vô hại
  nhưng cũng vô ích.
