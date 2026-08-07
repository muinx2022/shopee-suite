# Plan: Tự đóng modal thông báo chắn trang Địa chỉ + tự gỡ banner khi shop hết lỗi

- **Ngày:** 2026-08-08
- **Trạng thái:** hoàn thành
- **Người lập:** Opus 5 (phiên chính) · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

### Triệu chứng người dùng gặp

Tab "Shops" liên tục hiện banner `Cảnh báo: Lỗi địa chỉ. Shop <ten>` (v1.8.2, ảnh chụp 08/08/2026 có 2 banner:
`minoa.store`, `hanily.store`). Nhật ký tương ứng:

```
⊖ Không đặt được địa chỉ lấy hàng (Thanh Hóa) ở shop hanily.store, minoa.store — đã bỏ qua shop đó,
  chưa in phiếu; các shop khác vẫn chạy.
```

### Nguyên nhân gốc (người dùng chỉ ra, 08/08/2026 — nguyên văn)

> "khi vào phần địa chỉ, nếu có thay đổi j thì shopee sẽ thông báo, và bật thông báo đó, ext ko bắt được phần
> địa chỉ -> lỗi. nếu chỉ thử địa chỉ, nhưng vẫn ko tắt đc modal đó thì lỗi vẫn còn nguyên, thế nên nếu vào địa
> chỉ mà lỗi, cần check xem có modal nào ở phía trên, click vào Đồng ý hoặc OK, khi đó sẽ đóng đc modal đó và
> fix được lỗi. Cái này làm ngay trong phần check đơn trong shop, nếu vẫn ko đóng được thì báo lỗi như hiện tại."

Tức: Shopee bật một **modal thông báo** (đổi chính sách/điều khoản/tính năng mới…) đè lên trang
`Cài đặt vận chuyển`. Extension đọc toạ độ rồi `trustedClick` bằng `chrome.debugger` — cú click rơi vào **mask
của modal** chứ không tới phần tử đích, hoặc phần tử đích không đọc được. Kết quả: `pickupDone ok:false`
→ C# coi là "không đặt được địa chỉ" → bỏ qua shop + ghi banner. **Lỗi này là giả**: chỉ cần bấm "Đồng ý"/"OK"
trong modal thông báo là mọi thứ chạy lại bình thường.

Repo ĐÃ nhận diện đúng lớp lỗi này ở chỗ khác: `page-funcs.js:466 pageAnyModalVisible()` có comment
*"Dùng làm chốt chặn TRƯỚC khi bấm lại một toạ độ đã đọc từ trước: modal đang mở thì cú bấm đó rơi vào mask/nút
trong modal."* — nhưng `flow-address.js` KHÔNG hề dùng nó.

### Hiện trạng mã nguồn (đã khảo sát, dùng làm căn cứ — không phải đoán)

**Extension — `extensions/shopee-orders/flow-address.js`**

`doSetPickupAddress(province)` (dòng 15–86) đi tuần tự và có **6 lối ra `pickupDone ok:false`**, mỗi lối đều là
nạn nhân tiềm tàng của modal chắn:

| Dòng | Bước | Lối ra khi hỏng |
|---|---|---|
| 19–23 | `tabs.update` sang `SHIPPING_SETTINGS_URL` + `waitForTabComplete` | `/verify` → `captcha` (giữ nguyên, KHÔNG đụng) |
| 28–34 | tìm + click tab "Địa Chỉ" (`pageLocateByText`) | không thấy → chạy tiếp (không fail) nhưng bước sau sẽ fail |
| 38–44 | `pageFindAddressEdit(province)` | `!info.found` → **`ok:false`** ← ca nghi ngờ chính |
| 46–50 | `!info.hasEdit` | `ok: info.hasTag` |
| 52–58 | click "Sửa" → chờ modal `^sua dia chi$` | `!hasModal` → **`ok:false`** ← ca nghi ngờ chính (click rơi vào mask) |
| 62–71 | tick checkbox | `cnt.total === 0` → **`ok:false`** |
| 75–76 | tìm nút `^luu$` trong modal | `!save` → **`ok:false`** |
| 81–82 | hộp xác nhận `^dong y$` sau khi Lưu (best-effort, đã có sẵn) | — |

`doSetPickupAddressToOther()` (dòng 90–156) có cấu trúc y hệt, dùng cho bước revert cuối flow shop.

**Extension — `extensions/shopee-orders/page-funcs.js`**

- `pageModalHasTitle(reSrc)` (452): có modal hiển thị mang title khớp regex không.
- `pageAnyModalVisible()` (466): có BẤT KỲ `.eds-modal__box` nào đang hiển thị không.
- `pageLocateInModal(titleReSrc, selectors, textReSrc)` (496): tìm phần tử trong modal có title khớp → `{x,y}`.
- `pageLocateByText(selectors, reSrc)` (366): tìm theo text trên toàn trang → `{x,y}`.
- Title modal chuẩn: `.eds-modal__title`, fallback `.title`. Hộp modal: `.eds-modal__box`.
- ⚠ **Mọi hàm `page*` chạy world MAIN và được serialize ĐỘC LẬP → PHẢI TỰ CHỨA**, chỉ được gọi bare
  `_na(...)` / `_provCore(...)` (do `exec.js/pageInstallHelpers` cài vào `window` trước mỗi lượt). Xem
  `page-funcs.js:1–7`.
- `_na` = chuẩn hoá text (bỏ dấu, lowercase, gộp khoảng trắng) — mọi regex so khớp đều viết KHÔNG DẤU,
  lowercase (`"^dong y$"`, `"^sua dia chi$"`).

**C# — `orders/XuLyDonShopee.Core/Services/ShopFlowRunner.cs`**

- `QuyetDinhSauDatDiaChi(pickupOk, captchaSeen)` (140–143) — hàm THUẦN, 3 nhánh: `DungViCaptcha` / `XuDon` /
  `DungViDiaChi`.
- `RunShopOrdersAsync` (146…): bước đặt địa chỉ ở dòng ~213–240, **chỉ chạy khi `toShip > 0` và có `_invoiceDir`**.
  Nhánh `DungViDiaChi` đặt `PickupFailedShop = shopLogin` (property, dòng 124) rồi return.
- **KHÔNG có tín hiệu ngược lại** — không chỗ nào báo "shop này ĐẶT ĐƯỢC địa chỉ".

**C# — `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`**

- `record OrdersBridgeRunResult(int ShopCount, int ShopsDone, int TotalOrders, int TotalSlips, bool Captcha,
  string? Error, bool PickupAddressFailed = false, string? PickupFailedShop = null)` — dòng 45–47.
- `RunAllShopsAsync` (466…): vòng qua từng shop; sau `RunShopOrdersAsync` đọc `_flow.PickupFailedShop`, gom vào
  `pickupFailedShops`, **reset `_flow.PickupFailedShop = null`** để shop sau không dính cờ cũ (dòng ~540).
  Cuối vòng nối `string.Join(", ", pickupFailedShops)` vào `PickupFailedShop`.

**C# — `orders/XuLyDonShopee.App/Services/AccountSession.cs`**

- `RunBridgeContinuousAsync` (390…): sau mỗi vòng, nhánh `result.PickupAddressFailed` (dòng 507–516) gọi
  `_persist.StartCanhBaoDiaChiInBackground(...)` + `_persist.GhiBannerLoiDiaChi(result.PickupFailedShop, province, log, ct)`.

**C# — `orders/XuLyDonShopee.App/Services/OrderPersistPipeline.cs`**

- `TachTenShopLoiDiaChi(string?)` (455) — hàm THUẦN tách chuỗi nối `", "` → danh sách shop, distinct
  OrdinalIgnoreCase, rỗng → `["(không rõ shop)"]`.
- `GhiBannerLoiDiaChi(pickupFailedShop, tinh, log, ct)` (473): với mỗi shop → `PickupAlerts.GhiPhatHienTaiCho`
  (đặt cờ `cho_day=1`) → `RaiseAddressAlertsChanged` → `Task.Run` đẩy Hub qua
  `PickupAlertHubGate.RunAsync(accountLogin, shop, () => upsertHub(...))` → được rev thì `DanhDauDaDay(..., daDayDismiss: false)`.

**C# — `orders/XuLyDonShopee.Core/Data/PickupAddressAlertsRepository.cs`** — đúng 3 cửa ghi, KHÔNG được gộp:

| Hàm | Ý nghĩa | `cho_day` |
|---|---|---|
| `GhiPhatHienTaiCho(accountId, shopLogin, province)` | phát hiện lỗi TẠI CHỖ → mở/giữ banner | `= 1` |
| `DismissTaiCho(accountId, shopLogin)` | đóng banner TẠI CHỖ | `= 1` |
| `ApDungTuHub(accountId, shopLogin, province, dismissed, hubRev)` | NHẬN trạng thái từ Hub | `= 0` |
| `DanhDauDaDay(accountId, shopLogin, hubRev, daDayDismiss)` | ack sau khi Hub nhận | hạ cờ nếu khớp trạng thái |

Đường "người dùng bấm Đóng" — `AccountsViewModel.KetQua.cs:820 DismissAddressAlert(row)`:
`DismissTaiCho` → xoá dòng khỏi `AddressAlertRows` → `ApplyAddressErrorFlags()` → `DayLenHub(...)` (dòng 769)
đẩy `dismissHub` qua `PickupAlertHubGate`, được rev thì `DanhDauDaDay(..., daDayDismiss: true)`.

Hub chỉ có **hai động từ**: upsert (mở banner) và dismiss (tombstone), mỗi lần `rev = rev + 1`. **KHÔNG có
khái niệm "lý do đóng"** ở cả client lẫn Hub → "gỡ vì đã hết lỗi" phải **dùng LẠI y nguyên đường dismiss**,
không đẻ động từ mới, không thêm cột.

### Quyết định đã chốt với người dùng (08/08/2026)

1. Sửa gốc trước, tách riêng khỏi việc "nút Kiểm tra" (việc đó là plan sau).
2. Khi vào địa chỉ mà lỗi → dò modal chắn phía trên → click "Đồng ý"/"OK" → thử lại.
   **Vẫn không đóng được thì báo lỗi y như hiện tại** (không đổi hành vi thất bại).
3. Kiểm tra xong mà shop HẾT lỗi địa chỉ → **tự gỡ banner + đồng bộ Hub**.

## 2. Phạm vi

**Làm:**

- **A.** Extension: thêm hàm page thuần dò nút đóng của modal CHẮN (modal KHÔNG phải "Sửa Địa chỉ").
- **B.** Extension: `doSetPickupAddress` — dọn modal chắn TRƯỚC khi chạy, và khi lượt đầu thất bại thì dọn
  modal rồi **thử lại ĐÚNG MỘT lượt**.
- **C.** Extension: `doSetPickupAddressToOther` — chỉ dọn modal chắn TRƯỚC khi chạy (không thử lại).
- **D.** C#: có tín hiệu "shop này ĐẶT ĐƯỢC địa chỉ" chạy từ `ShopFlowRunner` → `OrdersBridgeRunResult`.
- **E.** C#: sau mỗi vòng, shop nào đặt được địa chỉ mà đang có banner active → **tự gỡ banner + đẩy dismiss lên Hub**.
- **F.** Test cho mọi hàm thuần mới + test cho đường gỡ banner.
- **G.** Bump `version.txt` + ghi `CHANGELOG.md`.

**Không làm:**

- KHÔNG làm nút "Kiểm tra" trên banner (plan riêng — xem mục 6).
- KHÔNG đổi luật `PickupAlertMerge.QuyetDinh`, KHÔNG thêm cột DB, KHÔNG thêm động từ API Hub.
- KHÔNG đổi hành vi nhánh captcha (`/verify` → `send captcha`) ở bất kỳ đâu.
- KHÔNG đổi hành vi khi vẫn không đặt được địa chỉ: vẫn bỏ qua shop, vẫn KHÔNG in phiếu, vẫn ghi banner,
  vẫn gửi cảnh báo Slack/Hub như cũ.
- KHÔNG đụng `flow-orders.js`, `flow-returns.js`, `flow-shop.js`.
- KHÔNG sửa `server/` (Hub).

## 3. Các bước thực hiện

### Bước A — `extensions/shopee-orders/page-funcs.js`: thêm `pageLocateBlockingModalButton`

Thêm hàm export mới (đặt ngay sau `pageAnyModalVisible`, dòng ~472) — **TỰ CHỨA**, chỉ được gọi bare `_na`:

```js
// Tìm nút ĐÓNG của modal CHẮN — modal đang hiển thị mà KHÔNG phải modal ta đang chờ (exceptTitleReSrc).
// Shopee hay bật thông báo (đổi chính sách/tính năng) đè lên trang Cài đặt vận chuyển; mask của nó nuốt mọi
// trusted click nên bước đặt địa chỉ fail OAN. Trả {x,y,title,label} của nút bấm được, hoặc null.
// Ưu tiên nút CHỮ ở footer (Đồng ý/OK/Xác nhận/Đã hiểu/Tôi đã hiểu/Bỏ qua/Để sau/Đóng), sau đó mới tới
// nút ✕ (.eds-modal__close) — bấm nút chữ là ý người dùng thật, ✕ chỉ là đường lui.
export function pageLocateBlockingModalButton(exceptTitleReSrc) { ... }
```

Yêu cầu hành vi:

1. Duyệt `document.querySelectorAll(".eds-modal__box")`, bỏ box có `getBoundingClientRect()` width/height = 0.
2. Đọc title: `box.querySelector(".eds-modal__title") || box.querySelector(".title")`; `t = _na(title?.textContent)`.
3. **BỎ QUA** box có title khớp `new RegExp(exceptTitleReSrc)` (khi `exceptTitleReSrc` rỗng/null thì không bỏ box nào).
4. Với box còn lại, tìm nút chữ: duyệt `[".eds-modal__footer button", ".eds-modal__footer [role='button']",
   "button", "[role='button']", "a"]`, khớp `_na(el.textContent)` với
   `/^(dong y|ok|xac nhan|da hieu|toi da hieu|toi biet roi|da biet|bo qua|de sau|dong|tiep tuc|hoan tat)$/`.
   Bỏ phần tử `disabled` hoặc rect 0.
5. Không có nút chữ → thử nút đóng: `box.querySelector(".eds-modal__close, .eds-icon-close, [aria-label='Close'], [class*='close']")`.
6. Tìm được → `scrollIntoView({block:"center"})` rồi đọc LẠI rect, trả
   `{ x: round(left+w/2), y: round(top+h/2), title: t || "", label: _na(el.textContent) || "x" }`.
7. Không thấy gì → `return null`.

> ⚠ Tên hằng regex phải nằm TRONG hàm (world MAIN serialize độc lập, không tham chiếu được biến ngoài).

### Bước B — `extensions/shopee-orders/flow-address.js`: helper `dongModalChan`

Thêm helper (không export, dùng nội bộ file):

```js
// Dọn các modal thông báo đang CHẮN trang (không tính modal `giuTitle` mà flow đang chờ). Bấm tối đa
// TRAN_MODAL_CHAN lần để không quay vô tận khi Shopee bật modal mới liên tục. Trả về SỐ modal đã đóng được.
const TRAN_MODAL_CHAN = 3;
async function dongModalChan(tabId, giuTitle) { ... }
```

Hành vi:

1. Vòng `for (let i = 0; i < TRAN_MODAL_CHAN; i++)`:
   - `const m = await execInTab(tabId, pageLocateBlockingModalButton, [giuTitle || ""]);`
   - `if (!m) break;`
   - `await trustedClick(tabId, m.x, m.y); await sleep(900);`
   - `daDong++;`
   - `send({ action: "progress", message: 'đã đóng modal chắn "' + (m.title || "(không tiêu đề)") + '" bằng nút "' + m.label + '".' });`
2. Chạm trần mà vẫn còn modal → `send progress` báo rõ *"còn modal chắn sau N lượt đóng — bỏ cuộc"* (**cấm im lặng**).
3. `return daDong;`

Nhớ `import { pageLocateBlockingModalButton }` thêm vào khối import từ `./page-funcs.js` (dòng 6–9).

### Bước C — `flow-address.js`: tách thân `doSetPickupAddress` + thử lại một lượt

Tách phần thân hiện tại (dòng 25–85, tính TỪ SAU khối điều hướng + kiểm `/verify`) thành hàm nội bộ:

```js
// Một LƯỢT thử đặt địa chỉ trên trang ĐANG mở. KHÔNG gửi pickupDone (hàm gọi quyết định) — chỉ gửi progress.
// Trả { ok: bool, lyDo: string } — lyDo để hàm gọi ghi nhật ký khi bỏ cuộc.
async function thuDatDiaChi(tabId, province) { ... }
```

- Mỗi chỗ hiện đang `send({action:"pickupDone", ok:false}); return;` → đổi thành `return { ok: false, lyDo: "<nguyên văn message cũ>" };`
- Chỗ `send({action:"pickupDone", ok: info.hasTag}); return;` → `return { ok: info.hasTag, lyDo: "<message cũ>" };`
- Chỗ cuối cùng `send({action:"pickupDone", ok:true})` → `return { ok: true, lyDo: "" };`
- **GIỮ NGUYÊN mọi `send({action:"progress", ...})`** đang có — nhật ký production đang dựa vào chúng.
- Bước điều hướng + kiểm `/verify` (dòng 19–23) **ở lại hàm ngoài**, chỉ chạy MỘT lần.
  ⚠ Nhưng lượt thử LẠI phải quay về đúng trang: nếu modal chắn đã làm trang đổi (hiếm), gọi lại
  `chrome.tabs.update(tabId, { url: SHIPPING_SETTINGS_URL })` + `waitForTabComplete` + kiểm `/verify` ở ĐẦU lượt 2.
  Để đơn giản và chắc chắn: đưa cả khối điều hướng vào `thuDatDiaChi` (nó rẻ — trang đã ở đó thì reload nhanh),
  và hàm ngoài chỉ còn điều phối. Nếu `thuDatDiaChi` gặp `/verify` thì trả `{ ok:false, lyDo:"captcha", captcha:true }`
  để hàm ngoài `send({action:"captcha", ...})` rồi return — **KHÔNG gửi `pickupDone` ở nhánh captcha**
  (hành vi hiện tại: gặp `/verify` chỉ gửi `captcha`, không gửi `pickupDone`; C# xử qua `_ch.CaptchaSeen`). Giữ y hệt.

Thân `doSetPickupAddress` mới:

```js
export async function doSetPickupAddress(province) {
  const tabId = orderTabId();
  if (tabId == null) { send({ action: "error", message: "chưa có tab shop để đặt địa chỉ" }); return; }

  // Dọn TRƯỚC: modal thông báo của Shopee hiện ngay khi vào trang, nuốt mọi trusted click phía sau.
  // (Chạy trước lượt 1 nên ca phổ biến nhất không tốn thêm lượt thử nào.)
  let kq = await thuDatDiaChi(tabId, province, /*donModalTruoc*/ true);
  if (kq.captcha) { return; }            // thuDatDiaChi đã send captcha
  if (!kq.ok) {
    // Lượt 1 hỏng: có thể modal bật SAU khi trang load xong (Shopee bật trễ). Dọn rồi thử LẠI đúng 1 lượt,
    // và CHỈ khi thực sự đóng được modal — không đóng được gì mà vẫn thử lại là tốn 1 lượt vô ích mỗi shop.
    const daDong = await dongModalChan(tabId, "^sua dia chi$");
    if (daDong > 0) {
      send({ action: "progress", message: "đã đóng " + daDong + " modal chắn — thử đặt địa chỉ lại." });
      kq = await thuDatDiaChi(tabId, province, /*donModalTruoc*/ false);
      if (kq.captcha) { return; }
    }
  }
  if (!kq.ok) { send({ action: "progress", message: "đặt địa chỉ vẫn hỏng: " + kq.lyDo }); }
  send({ action: "pickupDone", ok: kq.ok });
}
```

Trong `thuDatDiaChi`, khi `donModalTruoc === true` thì gọi `await dongModalChan(tabId, "^sua dia chi$")` **ngay
sau `await sleep(1000)`** (sau khi trang load xong, trước khi tìm tab "Địa Chỉ").

⚠ **Bất biến phải giữ:** mỗi lần gọi `doSetPickupAddress`, phía C# chờ ĐÚNG MỘT `pickupDone`
(`ArmPickup()` + `AwaitAsync`, hạn `ChoChang.Pickup` = 90s). Gửi hai lần `pickupDone` là **thừa một message
làm hỏng chặng sau**; gửi 0 lần (ngoài nhánh captcha) là **treo tới hết 90s**. Rà kỹ mọi nhánh.

⚠ **Ngân sách thời gian:** `ChoChang.Pickup` = 90s. Lượt thử tốn tối đa ~10s (tab) + ~15s (tìm địa chỉ) +
~10s (chờ modal) + vài `sleep` ≈ 40s. Hai lượt + 3 lần đóng modal (~2.7s) có thể **vượt 90s**.
→ **Phải nới `ChoChang.Pickup`** trong `orders/XuLyDonShopee.Core/Services/OrdersBridgeChannel.cs:81` từ
`90` lên `180` giây, kèm cập nhật xmldoc nói rõ vì sao (có thêm một lượt thử lại sau khi dọn modal).

### Bước D — `flow-address.js`: `doSetPickupAddressToOther` dọn modal trước

Chỉ thêm **một dòng** sau `await sleep(1000);` (dòng 100):

```js
await dongModalChan(tabId, "^sua dia chi$");
```

Không tách hàm, không thử lại — bước revert hỏng chỉ ghi `pickupOtherDone ok:false` và flow bỏ qua
(`ShopFlowRunner` đã `catch (TimeoutException)`), không đẻ banner.

### Bước E — C#: tín hiệu "shop ĐẶT ĐƯỢC địa chỉ"

**E1. `orders/XuLyDonShopee.Core/Services/ShopFlowRunner.cs`**

Thêm property đối xứng với `PickupFailedShop` (dòng 124), cùng phong cách xmldoc:

```csharp
/// <summary>Nhãn shop ĐẶT ĐƯỢC địa chỉ lấy hàng trong lượt này (null = chưa/không chạy bước đặt địa chỉ).
/// Đối xứng <see cref="PickupFailedShop"/> — vòng ngoài (phiên) đọc để TỰ GỠ banner lỗi địa chỉ cũ của
/// shop đó. CHỈ đặt khi bước đặt địa chỉ THỰC SỰ chạy và trả ok: shop 0 đơn chờ lấy hàng không chạy bước
/// này nên KHÔNG được coi là "đã hết lỗi" (chưa chứng minh được gì).</summary>
public string? PickupOkShop { get; set; }
```

Trong `RunShopOrdersAsync`, ngay sau khi `quyetDinh == SauDatDiaChi.XuDon` (tức lọt qua cả hai nhánh
`DungViCaptcha` / `DungViDiaChi`), đặt:

```csharp
PickupOkShop = string.IsNullOrWhiteSpace(shopLogin) ? "(không rõ shop)" : shopLogin;
```

**E2. `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`**

- Thêm field vào record (dòng 45–47), **để CUỐI + có giá trị mặc định** cho khỏi vỡ chỗ gọi khác:
  ```csharp
  public sealed record OrdersBridgeRunResult(
      int ShopCount, int ShopsDone, int TotalOrders, int TotalSlips, bool Captcha, string? Error,
      bool PickupAddressFailed = false, string? PickupFailedShop = null, string? PickupOkShops = null);
  ```
- Trong `RunAllShopsAsync`: khai báo `var pickupOkShops = new List<string>();` cạnh `pickupFailedShops` (dòng 469).
- Trong vòng shop, ở nhánh `else` (không lỗi địa chỉ), thêm:
  ```csharp
  if (_flow.PickupOkShop is not null) { pickupOkShops.Add(_flow.PickupOkShop); }
  ```
- **RESET `_flow.PickupOkShop = null;` ở đầu MỖI shop** (hoặc ngay sau khi đọc) — y như `PickupFailedShop`
  đang làm. Quên là shop sau dính cờ của shop trước → **gỡ nhầm banner của shop chưa hề chạy bước địa chỉ**.
  An toàn nhất: đặt `_flow.PickupOkShop = null;` NGAY TRƯỚC lời gọi `_flow.RunShopOrdersAsync(...)`.
- Điền `PickupOkShops: pickupOkShops.Count > 0 ? string.Join(", ", pickupOkShops) : null` vào **TẤT CẢ**
  các `return new OrdersBridgeRunResult(...)` nằm SAU vòng shop hoặc thoát giữa vòng shop:
  nhánh captcha khi mở Chi tiết, nhánh captcha khi đọc/xử đơn, nhánh `closeErr`, nhánh
  `pickupFailedShops.Count > 0` cuối vòng, và nhánh thành công cuối vòng.
  (Các `return` TRƯỚC vòng shop — login lỗi, 0 shop — giữ nguyên, chưa shop nào chạy.)

**E3. `orders/XuLyDonShopee.App/Services/OrderPersistPipeline.cs`**

Thêm hàm mới ngay SAU `GhiBannerLoiDiaChi`:

```csharp
/// <summary>
/// TỰ GỠ banner lỗi địa chỉ của những shop vòng này ĐÃ ĐẶT ĐƯỢC địa chỉ lấy hàng — dùng LẠI y nguyên đường
/// "người dùng bấm Đóng" (<c>DismissTaiCho</c> + đẩy dismiss lên Hub) chứ KHÔNG đẻ động từ mới: Hub chỉ có
/// upsert/dismiss, thêm khái niệm "đóng vì hết lỗi" là phải sửa cả hợp đồng API lẫn luật merge.
/// <para>Chỉ đụng shop ĐANG có banner active — shop chưa từng lỗi thì không ghi gì (khỏi đẻ tombstone rác
/// và khỏi bơm rev vô ích lên Hub mỗi vòng).</para>
/// Nuốt lỗi Hub — local vẫn đúng khi offline, cờ <c>cho_day</c> ở lại để nhịp sync tab Shops đẩy lại.
/// </summary>
public void GoBannerLoiDiaChi(string? pickupOkShops, Action<string> log, CancellationToken ct)
```

Hành vi:

1. `if (string.IsNullOrWhiteSpace(pickupOkShops)) return;`
2. Tách danh sách shop. **KHÔNG dùng `TachTenShopLoiDiaChi`** — hàm đó trả `["(không rõ shop)"]` khi rỗng,
   ở đây rỗng phải là "không làm gì". Viết hàm THUẦN riêng, đặt `internal static` để test được:
   ```csharp
   /// <summary>HÀM THUẦN: tách danh sách shop từ chuỗi nối ", " — rỗng → danh sách RỖNG (khác
   /// <see cref="TachTenShopLoiDiaChi"/>: ở đó rỗng nghĩa là "lỗi không rõ shop", ở đây rỗng nghĩa là
   /// "không có shop nào để gỡ").</summary>
   internal static IReadOnlyList<string> TachTenShopDatDuocDiaChi(string? s)
   ```
3. Đọc `var active = _services.PickupAlerts.ListActive(_accountId);` → `HashSet<string>` OrdinalIgnoreCase.
4. Với mỗi shop trong danh sách MÀ có trong `active`:
   - Lấy chuỗi shop **của dòng local** (`ListAll` → khớp OrdinalIgnoreCase) chứ KHÔNG dùng chuỗi từ vòng chạy:
     khoá SQL so BINARY, lấy nhầm chuỗi là ghi trượt sang không dòng nào (cùng cạm bẫy đã ghi ở
     `MergeVaDayOutbox`, `AccountsViewModel.KetQua.cs:740–742`).
   - `_services.PickupAlerts.DismissTaiCho(_accountId, shopLocal);`
   - `log($"Banner địa chỉ: shop {shopLocal} đã đặt được địa chỉ ({...}) — tự gỡ banner.");`
5. Có gỡ ít nhất 1 shop → `_services.RaiseAddressAlertsChanged(_accountId);`
6. Đẩy Hub: `Task.Run` giống hệt `GhiBannerLoiDiaChi` nhưng gọi `_services.DismissPickupAlertToHub` và
   `DanhDauDaDay(..., daDayDismiss: true)`. Bọc `PickupAlertHubGate.RunAsync(accountLogin, shop, ...)`.
   `dismissHub is null` hoặc `accountLogin` rỗng → return (local vẫn đúng, nhịp sync sau lo).

**E4. `orders/XuLyDonShopee.App/Services/AccountSession.cs`**

Trong `RunBridgeContinuousAsync`, sau khối `if/else if/else` xử lý `result` (sau dòng ~525), thêm **một lời gọi
duy nhất, chạy ở MỌI nhánh** (kể cả nhánh có shop khác lỗi địa chỉ — shop A hết lỗi thì gỡ banner A, không
liên quan shop B đang lỗi):

```csharp
// Shop nào vòng này ĐẶT ĐƯỢC địa chỉ mà còn banner cũ → tự gỡ (người dùng chốt 08/08/2026). Đặt SAU nhánh
// GhiBannerLoiDiaChi để hai đường không giẫm nhau khi một vòng vừa có shop hỏng vừa có shop lành.
_persist.GoBannerLoiDiaChi(result.PickupOkShops, log, ct);
```

⚠ Đặt **sau** `GhiBannerLoiDiaChi` trong cùng vòng. Hai hàm đụng các shop KHÁC NHAU nên không chồng chéo,
nhưng thứ tự này giữ đúng ngữ nghĩa "ghi cái mới rồi mới gỡ cái cũ".

### Bước F — Test

`orders/XuLyDonShopee.Tests/` (xUnit, `Using Include="Xunit"` sẵn — file test KHÔNG cần `using Xunit;`):

1. **`OrderPersistPipelineTests.cs`** (hoặc file test sẵn có nếu đã có) — cho `TachTenShopDatDuocDiaChi`:
   - `null` → rỗng; `""` / `"   "` → rỗng (**khác** `TachTenShopLoiDiaChi` trả `["(không rõ shop)"]`).
   - `"a.store, b.store"` → `["a.store","b.store"]`.
   - `"a.store, A.STORE"` → 1 phần tử (distinct OrdinalIgnoreCase).
   - `"a.store,, b.store"` → 2 phần tử (bỏ mục rỗng).
2. **`PickupAddressAlertsTests.cs`** (đã có 25 test) — thêm:
   - Shop đang có banner active → `DismissTaiCho` → `ListActive` không còn shop đó, `ListChoDay` CÓ (cờ `cho_day=1`).
   - Shop CHƯA từng có banner → đường gỡ **không tạo dòng mới** (`ListAll` vẫn rỗng).
3. **`ShopFlowRunner` / `OrdersBridgeSession`** — kiểm tín hiệu:
   - Xem `orders/XuLyDonShopee.Tests/PickupAddressStopTests.cs` đang dựng ca "không đặt được địa chỉ" thế nào
     (client WebSocket giả) rồi thêm ca **đối xứng**: extension trả `pickupDone ok:true` → `PickupOkShop` =
     nhãn shop; extension trả `ok:false` → `PickupOkShop` vẫn `null`.
   - Ca shop `toShip = 0` (bước địa chỉ KHÔNG chạy) → `PickupOkShop` phải `null`.

⚠ **Test mới viết xong phải thử PHÁ đúng cái luật nó canh rồi chạy lại** (bỏ dòng gán `PickupOkShop`, bỏ dòng
reset `= null`, đổi rỗng→`["(không rõ shop)"]`…). Test không đổ = nó đang xanh vì lý do khác, chưa canh gì cả.
Đã dính đúng lỗi này ngày 04/08/2026.

### Bước G — Phát hành

- `version.txt`: bump từ `1.8.3` lên `1.8.4`.
- `CHANGELOG.md`: thêm mục cho 1.8.4, tiếng Việt, theo đúng khuôn các mục đã có.
- **KHÔNG chạy `release-suite.cmd`**, KHÔNG upload, KHÔNG deploy Hub — người dùng tự quyết khi nào phát hành.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` — **0 warning, 0 error**. (Lệnh "tổng" có thể KHÔNG phủ hết project —
      kiểm bằng cách đọc output xem có project `XuLyDonShopee.*` không; thiếu thì build riêng từng project.)
- [ ] `dotnet build orders/XuLyDonShopee.App/XuLyDonShopee.App.csproj` — 0 warning.
- [ ] `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` — **toàn bộ xanh**, số test TĂNG so
      với trước (ghi rõ số cũ → số mới trong báo cáo).
- [ ] Rà TỪNG nhánh của `doSetPickupAddress` mới và chứng minh trong báo cáo: **mọi đường đi đều gửi ĐÚNG một
      `pickupDone`**, trừ nhánh captcha (gửi `captcha`, KHÔNG gửi `pickupDone`) — liệt kê thành bảng nhánh → message gửi.
- [ ] `pageLocateBlockingModalButton` **KHÔNG bao giờ** trả về nút nằm trong modal `Sửa Địa chỉ` khi gọi với
      `exceptTitleReSrc = "^sua dia chi$"` — chứng minh bằng đọc code (điều kiện bỏ box) và ghi vào báo cáo.
- [ ] `pageLocateBlockingModalButton` TỰ CHỨA: không tham chiếu biến/hàm nào ngoài `_na` và tham số.
- [ ] `ChoChang.Pickup` đã nới lên 180s và xmldoc giải thích lý do.
- [ ] `_flow.PickupOkShop` được reset về `null` TRƯỚC mỗi shop — chỉ ra đúng dòng trong báo cáo.
- [ ] `GoBannerLoiDiaChi` **không ghi gì** khi shop chưa có banner active — chứng minh bằng test.
- [ ] `git diff` không đụng file nào ngoài danh sách ở mục 2.

## 5. Rủi ro & lưu ý

1. **Gửi hai lần `pickupDone` = hỏng chặng sau.** `OrdersBridgeChannel._pickupTcs` là `TaskCompletionSource`
   dùng `TrySetResult` nên message thứ hai bị nuốt im lặng — nhưng nếu nó tới lúc C# đã sang chặng khác thì
   không sao, còn nếu logic đổi thì rất khó lần. Rà bảng nhánh cho kỹ (tiêu chí nghiệm thu có mục này).
2. **Modal "Sửa Địa chỉ" chính là `.eds-modal__box`.** Dò nhầm nó rồi bấm "Đóng" là **tự phá chính flow mình
   đang chạy**. Đây là rủi ro số một của plan này — `exceptTitleReSrc` phải luôn được truyền
   `"^sua dia chi$"` ở MỌI chỗ gọi trong `flow-address.js`.
3. **Hộp xác nhận `^dong y$` sau khi bấm Lưu (dòng 81–82) là modal HỢP LỆ**, không phải modal chắn.
   `dongModalChan` chỉ chạy ở ĐẦU lượt và SAU khi lượt thất bại — không được chèn vào giữa bước Lưu → xác nhận.
4. **Nới `ChoChang.Pickup` lên 180s làm shop hỏng thật lâu hơn ~90s.** Chấp nhận được: một shop hỏng địa chỉ
   trong một vòng, đổi lại cứu được cả vòng khi nguyên nhân chỉ là modal. Ghi rõ trong CHANGELOG.
5. **`PickupOkShop` không reset = gỡ nhầm banner.** Shop A đặt được địa chỉ, shop B (0 đơn) không chạy bước
   địa chỉ — quên reset thì B thừa hưởng cờ của A và banner của B bị gỡ oan. Đặt reset ngay trước lời gọi.
6. **`LoadAddressAlertsFromLocal` xoá sạch rồi dựng lại `AddressAlertRows`** mỗi lần merge / nhịp 60s /
   `AddressAlertsChanged`. Nên KHÔNG được gắn trạng thái tạm nào lên `PickupAlertRow` ở plan này (plan sau
   làm nút "Kiểm tra" sẽ phải xử lý chuyện đó).
7. **Chuỗi shop trong SQL so BINARY** còn so trong C# là OrdinalIgnoreCase — luôn ghi bằng chuỗi lấy TỪ DÒNG
   LOCAL, không phải chuỗi từ vòng chạy. Cạm bẫy đã ghi ở `AccountsViewModel.KetQua.cs:740–742`.
8. **`ex.ToString()` cho catch-all chỉ-ghi-log** (đủ stack để lần ra); `ex.Message` cho nhánh đã phân loại và
   cho chuỗi hiện ra UI. Xem `orders/CLAUDE.md`.
9. **Không có cách test tự động cho phần extension JS** (repo không có test runner JS). Phần A–D nghiệm thu
   bằng đọc code + bảng nhánh; kiểm thật phải chạy app trên Seller Centre — người dùng làm sau khi build.

## 6. Việc TIẾP THEO (plan riêng, KHÔNG làm trong plan này)

Nút **"Kiểm tra"** trên banner lỗi địa chỉ — người dùng đã chốt các điểm sau (ghi lại để plan sau không hỏi lại):

- Nút nằm cạnh nút "Đóng" trên mỗi dòng banner (ô xanh trong ảnh chụp 08/08/2026).
- Bấm → mở shop đó ra, **vào thẳng trang Địa chỉ của shop**, rồi buông tay cho người dùng tự xử.
- **Vòng lặp kiểm tra chỉ vào phần địa chỉ, KHÔNG check đơn** (người dùng chốt 08/08/2026).
- Đang có phiên chạy → mở **tab thứ 2** trong chính trình duyệt đang chạy.
- Không có phiên chạy → **lấy profile đã đăng nhập, tìm đúng shop đó và chạy vòng lặp chỉ 1 shop**.
- Người dùng tự sửa xong thì bấm "Đóng" trên banner = xác nhận đã fix (hành vi hiện có).

Ràng buộc kỹ thuật đã khảo sát cho plan đó (đừng khảo sát lại):

- `core.js` giữ MỘT `ctx` toàn cục (`listTabId` / `shopTabId`) — mở shop thứ hai là GHI ĐÈ shop thứ nhất.
- Giao thức WS **không có request-id**; `background.js` gọi `commandHandler(cmd)` fire-and-forget, không hàng đợi.
- `WebSocketServer` phía C# chỉ giữ MỘT socket — kết nối mới đóng kết nối cũ ("replaced").
- `shared/dbg-input.js` giữ `_dbgTab` đơn nhất, `ensureDbg` tự detach khỏi tab kia mỗi lần đổi tab.
- ⚠ `OrdersBridgeLauncher.Launch` gọi `FreeProfile(..., alsoMatchBridgeExtension: true)` → **mở trình duyệt cho
  luồng thứ hai sẽ GIẾT trình duyệt của luồng đang chạy**.
- Tiền lệ dùng được: `doSyncOrderFinals` trong `flow-orders.js` đã tự mở tab riêng rồi đóng.
- Cổng cầu nối truyền động được qua hash `#_od_ws=<port>` (`content.js:10`), nhưng hash rụng → fallback
  `DEFAULT_PORT = 47821` (`content.js:7`) — đó là bẫy phải xử nếu dùng cổng thứ hai.

---

## Báo cáo thực thi

**Xong 08/08/2026.** `opus-executor` triển khai → `nghiem-thu` phản biện → phiên chính đối chiếu diff, sửa
các điểm phản biện nêu đúng, tự chạy lại kiểm chứng.

### Kiểm chứng thật (phiên chính tự chạy, không lấy báo cáo của subagent)

| Lệnh | Kết quả |
|---|---|
| `dotnet build ShopeeSuite.sln -t:Rebuild` | 0 Warning, 0 Error (rebuild thật, phủ cả 3 project `XuLyDonShopee.*`) |
| `dotnet test orders/XuLyDonShopee.Tests` | **1658 passed / 0 failed** (trước bản vá 1647 → +11) |
| `node --check` hai file JS | OK |
| Bàn thử DOM giả cho `pageLocateBlockingModalButton` | **8/8 đạt** |

Phần JS không có test runner trong repo, nên `pageLocateBlockingModalButton` được kiểm bằng một bàn thử DOM
giả viết riêng (ngoài repo, ở scratchpad). Hai ca sống còn đều đạt: (1) chỉ có modal "Sửa Địa chỉ" trên màn →
trả `null`, tuyệt đối không đụng modal của chính flow; (4) modal chắn chỉ có `<div class="closed-order-badge">`
trang trí → trả `null`, không bấm bừa.

### Đổi hướng so với plan (ghi lại, không sửa plan cho khớp kết quả)

1. **Bước C — lượt 2 dùng `donModalTruoc: true`** (plan ghi `false`). Lý do: plan cũng chốt đưa cả khối điều
   hướng vào `thuDatDiaChi`, nên lượt 2 **tải lại trang** — modal nào Shopee bật ở MỖI lần load sẽ hiện lại
   ngay sau lượt dọn vừa xong, và lượt 2 chết đúng cái lỗi lượt 1 vừa chết. Giá phải trả: một `execInTab` trả
   `null` khi trang sạch.
2. **`ChoChang.Pickup` = 240s** (plan ghi 180s). Ngân sách trong plan tính thiếu vòng tick checkbox
   (8 × ~1,1s ≈ 9s) và thiếu một lượt `dongModalChan`: một lượt xấu nhất ~72s chứ không phải ~40s, hai lượt +
   dọn ≈ 150s ⇒ biên tới 180s chỉ còn 30s. Quá hạn ở chặng này làm **dừng cả vòng, không ghi banner, không gửi
   cảnh báo** — tệ hơn hành vi cũ — nên nới rộng tay.
3. **`ListActive` thay `ListAll`** để lấy chuỗi shop của dòng local: dòng active chính là dòng local, tiết kiệm
   một truy vấn. Vẫn có test `GoBanner_NhanLechHoaThuong_VanGoTrungDongLocal` canh.
4. **Thêm `PickupOkShops` vào cả 2 khối `catch` cuối `RunAllShopsAsync`** (plan liệt kê 5 lối ra). Hai catch đó
   cũng là "thoát giữa vòng shop"; không thêm thì shop đã chạy xong đầu vòng mất tín hiệu gỡ banner khi vòng gãy.

### Lỗi do `nghiem-thu` phát hiện và ĐÃ sửa

| | Lỗi | Sửa |
|---|---|---|
| V1 | `dongModalChan` gọi `execInTab` (`exec.js:34` KHÔNG bọc `executeScript`) và `trustedClick` (`dbgSend` **reject** khi `chrome.runtime.lastError`, vd "Detached while handling command"). Exception thoát ra → `core.js:33` gửi `{action:"error"}` → C# fault chặng pickup → ném khỏi vòng shop → **cả vòng dừng, KHÔNG ghi banner, KHÔNG cảnh báo**. Tức một lượt dọn hỏng làm hỏng luôn các shop còn lại — tệ hơn hành vi cũ (bỏ đúng 1 shop). | Bọc kín thân `dongModalChan` bằng `try/catch`, trả số đã đóng được. Dọn modal là việc best-effort, không được nằm trên đường quyết định của vòng. |
| V2 | `daDong++` ngay sau `trustedClick`, tức **đếm số cú BẤM chứ không phải số modal ĐÃ ĐÓNG**. Cú bấm trượt vẫn tính ⇒ chốt `if (daDong > 0)` luôn đúng ⇒ **mọi shop, mọi vòng tốn thêm nguyên một lượt thử (~72s) + 9 cú bấm vô nghĩa**, đúng cái mà comment tuyên bố muốn tránh. Nhật ký còn nói sai sự thật. | Sau mỗi cú bấm dò lại: cùng `title` còn đó → **không đếm, dừng luôn** (bấm thêm cũng vậy) + ghi nhật ký "KHÔNG đóng được"; biến mất mới `daDong++`. |
| V4 | xmldoc của `ChoChang.Pickup` tính sai ngân sách (bỏ sót vòng tick checkbox, đếm thiếu một lượt dọn). | Tính lại đầy đủ trong xmldoc + nới 180s → 240s (mục "Đổi hướng" #2). |
| G1 | `SEL_X` có `[class*='close']` quá rộng; `querySelector` trả phần tử ĐẦU theo **thứ tự DOM**, không theo thứ tự selector ⇒ `<div class="closed-…">` trang trí cũng trúng, và ta bấm mù vào tâm nó. Flow chạy tiếp trên chính trang đó nên một cú click lạc gây điều hướng sẽ đẻ ra đúng cái "lỗi địa chỉ oan" đang đi sửa. | Thu về `.eds-modal__close, .eds-icon-close, [aria-label='Close'], [class*='eds-modal__close']`. Ca 4 của bàn thử DOM canh việc này. |
| G2 | `label: _na(el.textContent)` — nếu dò trúng phần tử bọc thì nhãn là text CẢ modal, đẩy nguyên khối qua WebSocket vào ô nhật ký. | `.slice(0, 40)`. Ca 8 của bàn thử DOM canh. |

### Điểm `nghiem-thu` nêu mà phiên chính KHÔNG nhận (đã tự đối chiếu code)

- **V3 — "xoá `_flow.PickupOkShop = null;` không làm đổ test nào ⇒ luật nguy hiểm nhất đang không có gì canh".**
  Đúng là không test nào đổ, nhưng **đột biến đó trung tính về ngữ nghĩa**, không phải lỗ hổng test: giá trị rò
  rỉ LUÔN là nhãn của shop TRƯỚC, mà nhãn đó đã nằm sẵn trong `pickupOkShops`; `TachTenShopDatDuocDiaChi` lại
  `Distinct(OrdinalIgnoreCase)` ⇒ không có hành vi quan sát được nào đổi. Không có đường nào để một shop nhận
  nhãn của CHÍNH NÓ mà không chạy bước địa chỉ. Dòng reset vẫn giữ (phòng thủ theo chiều sâu, đúng plan §5.5),
  nhưng không dựng thêm harness cho `RunAllShopsAsync` chỉ để cố định một đột biến trung tính.
- **G5 — hai dòng chỉ khác hoa/thường cùng tồn tại thì `TryAdd` bỏ dòng sau.** Đúng, nhưng đó là trạng thái
  bệnh lý mà repo đã dung nạp ở chỗ khác (`MergeVaDayOutbox` cũng `TryAdd`). Giữ nhất quán, không thêm phức tạp.
- **Đường đẩy Hub của `GoBannerLoiDiaChi` chưa có test.** Đúng — nhưng nó là `Task.Run` fire-and-forget, test sẽ
  phải chờ nền → dễ chập chờn; và `GhiBannerLoiDiaChi` (đường đối xứng, có sẵn từ trước) cũng không có test.
  Ghi nhận là nợ, không vá bằng một test chập chờn.

### Test CHẬP CHỜN bắt được trong lúc nghiệm thu (đã vá) — ngoài phạm vi plan

Một lượt `dotnet test` đổ **1/1658** rồi các lượt sau xanh lại. Không bỏ qua: truy ra
`BridgeTestRig.CongTrong()` (`orders/XuLyDonShopee.Tests/BridgeTestRig.cs:118`) có khe hở kinh điển — mở
`TcpListener` cổng 0 → **`Stop()` nhả cổng** → mới `channel.Start(port)` bind lại. xUnit chạy các collection
SONG SONG nên giữa hai bước đó một rig khác chiếm đúng cổng ấy và giữ suốt bài test của nó.
`OrdersBridgeChannel.Start` có retry 5 lượt nhưng retry **ĐÚNG CỔNG CŨ** → vô ích khi cổng bị chiếm thật → ném
→ bài test đổ oan. Việc này thêm 3 test dùng rig nên xác suất đụng tăng lên.

Đã vá ở tầng test: `StartAsync` nay xin **cổng KHÁC** tối đa 5 lượt khi `Start` ném. Không đụng mã sản xuất.

Trung thực về mức bằng chứng: **không chụp được tên bài test đã đổ** (lượt sau xanh nên không còn thông báo),
và chạy hai lượt `dotnet test` song song cố ép đụng cổng cũng không tái hiện được — cơ chế race thì chắc chắn
có thật và đọc ra được từ mã. Sau khi vá đã chạy **14 lượt liên tiếp, xanh cả 14**.

### Còn lại / nợ kỹ thuật

- Phần extension JS chỉ được kiểm bằng đọc code + bàn thử DOM giả. **Kiểm thật phải chạy app trên Seller Centre**
  — người dùng làm sau khi phát hành 1.8.4.
- Chưa phát hành: `release-suite.cmd` CHƯA chạy, chưa upload, chưa deploy Hub (đúng phạm vi plan).
