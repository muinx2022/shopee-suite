# Plan: Đợt E — Tách `extensions/shopee-orders/background.js` (1.909 dòng) theo khuôn shopee-search

- **Ngày:** 2026-08-06
- **Trạng thái:** hoàn thành
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

### Kết quả: XONG. 10 file, 58/58 hàm chuyển đúng 1 lần, thân hàm khớp NGUYÊN VĂN từng ký tự.

Cách làm: KHÔNG gõ tay lại code. Một script ở scratchpad cắt đúng dải dòng của bản gốc rồi ghép vào file
đích; chỉ hai phép biến đổi được phép (`export ` và `listTabId/shopTabId/lastTabUrls` → `ctx.*`). Một script
thứ hai đảo ngược phép biến đổi rồi so với bản gốc — nên "chỉ move, không viết lại" là chứng minh được chứ
không phải lời hứa.

### Bố cục sau tách (dòng)

| File | Dòng | Nội dung |
|---|---:|---|
| `background.js` | **83** | header protocol (giữ nguyên) + import + `handleCommand` + 2 listener top-level |
| `core.js` | 62 | `DEFAULT_PORT`, `ctx` (state), `bridge`/`send`, `setCommandHandler`, `ensureListTab`, `orderTabId` |
| `constants.js` | 22 | 13 hằng URL/trần/regex (dòng 935–953 bản gốc) |
| `exec.js` | 45 | `pageInstallHelpers`, `execInTab` |
| `page-funcs.js` | 709 | `_na`, `_provCore` + 27 hàm page* (picker shop, đơn, modal, phiếu, địa chỉ) |
| `page-funcs-returns.js` | 140 | 8 hàm page* của trang Trả hàng/Hoàn tiền/Hủy |
| `flow-shop.js` | 229 | `gotoSellerCentre`, `ensureShopPicker`, `doReadShopList`, `doReadToShip`, `openShopDetail`, `doCloseShopTab` |
| `flow-orders.js` | 397 | `waitOrdersStable`, `waitOrdersChanged`, `doSyncOrders`, `doSyncOrderFinals`, `doPrepareNextOrder`, `doRedownloadSlip` |
| `flow-address.js` | 156 | `doSetPickupAddress`, `doSetPickupAddressToOther` |
| `flow-returns.js` | 156 | `doReadReturnRequests` |
| **tổng** | **1.999** | gốc 1.909 → +90 dòng header/import (đúng phần boilerplate) |

Đồ thị import MỘT CHIỀU, không vòng:
`background → {core, flow-*}` · `flow-* → {core, exec, constants, page-funcs*}` · `page-funcs-returns → page-funcs` ·
`core → shared/ws-bridge` · `constants`, `exec`, `page-funcs` không import module nội bộ nào.

### Bảng phân loại 58 hàm (hàm → module đích)

`send`→core · `ensureListTab`→core · `orderTabId`→core · `pageInstallHelpers`→exec · `execInTab`→exec ·
`handleCommand`→background ·
**page-funcs.js (29)**: `_na`, `_provCore`, `pageScanShopList`, `pageScrollDetailIntoView`, `pageLocateDetailRect`,
`pageReadToShip`, `pageScanOrders`, `pageOrderCount`, `pageReadFinalAmount`, `pageChanDoanUocTinh`,
`pageReadOrderProducts`, `pageListSignature`, `pageFindNextPage`, `pageLocateByText`, `pageDumpClickables`,
`pageFindPrepareOrder`, `pageFindPrintInCardBySn`, `pageModalHasTitle`, `pageAnyModalVisible`,
`pageReadModalTracking`, `pageLocateInModal`, `pagePrintButton`, `pageFetchSlipBase64`, `pageFindAddressEdit`,
`pageFindOtherAddressEdit`, `pageFirstUncheckedBox`, `pageCheckboxCount`, `pageShopRowCount`, `pageIsLoginForm` ·
**page-funcs-returns.js (8)**: `pageLocateReturnTab`, `pageLocateReturnCaseTab`, `pageReturnSummaryText`,
`pageLocateSortButton`, `pageLocateSortOption`, `pageReturnRowCount`, `pageScanReturnRows`, `pageChanDoanTraHang` ·
**flow-shop.js (6)** · **flow-orders.js (6)** · **flow-address.js (2)** · **flow-returns.js (1)** — xem bảng bố cục.

### Kiểm chứng (kết quả THẬT)

1. **`node --check`** — 10 file mới + `content.js`: **PASS hết**.
2. **Script đối chiếu** (`verify-split.js`, scratchpad) — **ĐẠT**, gồm:
   - 58 hàm gốc → 58 hàm sau tách, mỗi hàm **đúng 1 lần** (0 mất, 0 trùng); 1 hàm MỚI duy nhất là `setCommandHandler`.
   - So **thân hàm từng ký tự** sau khi gỡ `export`/`ctx.`: **lệch 0/58**.
   - So **từng dòng chép** (1.866 dòng): đảo ngược về gốc **khớp nguyên văn, lệch 0**. 43 dòng gốc không chép
     nguyên văn đều liệt kê được: 4 dòng import phân phối lại, 3 dòng `let` state → `ctx`, 8 dòng khối `bridge`
     (đổi `handleCommand` → `commandHandler`), 3 dòng `orderTabId` (→ `ctx.*`), còn lại là dòng trống.
   - 15 hằng gốc: không mất, không nhân bản.
   - Mọi call-site của 58 hàm + 15 hằng + `ctx/bridge/sleep/waitForTabComplete/ensureDbg/trustedClick` đều
     resolve trong chính file đó (khai báo hoặc import) — 10/10 file ok. Không có import thừa.
   - 35 cặp `import {…}` đều trỏ file có thật và tên có `export` tương ứng; **không có import vòng**.
3. **Nạp thật như service worker** (`linkcheck.mjs`): `import()` background.js với `chrome` giả → **NẠP OK**,
   side-effect top-level đúng 2 cái (`runtime.onMessage.addListener`, `storage.session.get(["wsPort","listTabId"])`),
   `DEFAULT_PORT = 47821`, `ctx = {listTabId:null, shopTabId:null, lastTabUrls:[]}`.
4. **Ràng buộc tự chứa (mục 4 plan)** (`check-selfcontained.js`): soi 36 hàm `page*`, đối chiếu với 87 tên cấp
   module của cả extension → **0 vi phạm**. 13 hàm dùng `_na`/`_provCore` — đúng ngoại lệ cũ (pageInstallHelpers
   cài lên `window` của TRANG), không hàm nào chạm import/hằng/hàm module khác.
5. **`extensions\sync-shared.cmd --check`** → `[sync-shared] OK - cac ban copy khop extensions\shared\.` (exit 0).
6. **Hợp đồng WS với C#**:
   - `DEFAULT_PORT = 47821` giữ nguyên (`core.js` + `content.js`), khớp `OrdersBridgeChannel.BridgePort`.
   - 12 lệnh `handleCommand` **khớp 1:1** (diff rỗng) với 12 lệnh C# gửi: `closeShopTab, gotoSellerCentre,
     openShopDetail, prepareNextOrder, readReturnRequests, readShopList, readToShip, redownloadSlip,
     setPickupAddress, setPickupAddressToOther, syncOrderFinals, syncOrders`.
   - Chiều ext→C#: 13 `action` gửi đi + 5 `kind` của `pageData` **giống hệt bản gốc** (diff rỗng) → mọi `case`
     trong `OrdersBridgeChannel.OnMessage` vẫn có nguồn.
7. **Đối chứng ÂM (thử phá để chắc script không xanh vì lý do khác)** — sửa 1 selector trong thân page-func,
   đổi 1 tên import thành tên không tồn tại, xoá 1 hàm: `node --check` **VẪN PASS cả ba** (đúng như plan cảnh
   báo: syntax check không bắt được import sai → SW chết câm), còn script đối chiếu bắt đủ **3/3**. Sau đó
   dựng lại từ script và verify lại: ĐẠT.
8. **`PrepareFreshExtensionCopy`** (`OrdersBridgeLauncher.cs:47`, chỉ ĐỌC): gọi `CopyDirectory` chép mọi file
   top-level + đệ quy thư mục con ⇒ 9 file .js mới ở gốc extension được chép đủ. Không cần sửa gì.
9. **Phạm vi**: `git status` chỉ có `M extensions/shopee-orders/background.js` + 9 file `??` cùng thư mục.
   Các thay đổi khác trong cây (C#, `extensions/shopee-search`) là của agent chạy song song — không đụng tới.

### Một lỗi ĐÃ tự bắt được trong lúc làm (ghi lại vì nó đúng loại lỗi plan sợ)

Lần dựng đầu tiên, phép đổi `listTabId → ctx.listTabId` ăn cả vào **chuỗi ký tự**:
`chrome.storage.session.get(["wsPort", "listTabId"], …)` thành `["wsPort", "ctx.listTabId"]`. Đây là KHOÁ lưu
trữ — sai là service worker ngủ dậy không khôi phục được `listTabId` (ghi khoá `listTabId`, đọc khoá
`ctx.listTabId` → luôn undefined), hỏng ÂM THẦM, `node --check` xanh, và phép so-thân-hàm cũng KHÔNG bắt được
vì dòng đó nằm ở cấp module chứ không trong hàm nào. Chỉ lượt nạp-thật (`linkcheck.mjs`) lộ ra. Đã sửa: phép
biến đổi nay bỏ qua nội dung chuỗi/comment, và verify thêm hẳn mục "so TỪNG DÒNG chép" phủ cả code cấp module.

### Sai khác so với plan (khai báo rõ, không sửa plan cho khớp kết quả)

1. **Thêm `flow-shop.js`** (plan chỉ liệt kê flow-orders/flow-address/flow-returns). Lý do: 6 lệnh cấp shop
   (SSO → picker → mở Chi tiết → đọc Chờ Lấy Hàng → đóng tab) là một giai đoạn đời sống riêng, chạy TRƯỚC mọi
   việc cấp đơn; gộp vào `flow-orders.js` thì file đó ~600 dòng và trộn hai tầng. Bước 1 của plan bảo lấy
   shopee-search làm khuôn — bên đó cũng có `flow-shop.js`.
2. **Tách `page-funcs` làm hai** — plan cho phép ("nếu quá dài… ranh giới tự nhiên, ghi lại"). Ranh giới dùng
   đúng dấu mốc CÓ SẴN trong bản gốc (dòng 761: `===== Bước CUỐI flow shop — trang "Trả hàng/Hoàn tiền/Hủy"`).
   Tên file đầu để là `page-funcs.js` (không phải `page-funcs-orders.js`) vì nó chứa cả picker shop, đơn, modal
   và địa chỉ chứ không riêng đơn — và khớp tên bên shopee-search.
3. **`listTabId`/`shopTabId`/`lastTabUrls` gom vào `ctx`** (core.js). BẮT BUỘC: binding import không gán lại
   được từ module khác. Đây là phép biến đổi duy nhất chạm vào thân hàm, và là phép được kiểm chứng đảo ngược
   ở mục 2. Giống hệt `ctx` của `shopee-search/core.js`.
4. **Thêm 1 hàm mới `setCommandHandler`** (core.js) để `core` không phải import ngược module flow
   (`bridge.onMessage` gọi handler do background đăng ký) — nếu không thì có vòng `core → flow → core`.
   Cùng mẫu `setAppMessageHandler` của shopee-search.
5. **`orderTabId` đặt ở `core.js`** chứ không ở flow-orders: cả flow-orders, flow-address, flow-returns đều dùng.

### Chưa làm được / rủi ro còn lại

- **Không chạy được vòng đơn thật** (cần Brave + tài khoản Shopee + Seller Centre) — đúng như plan đã lường:
  extension không có test tự động. Mọi bằng chứng ở trên là TĨNH + một lượt nạp module thật, không phải chạy
  nghiệp vụ. Lần chạy đầu sau khi phát hành vẫn cần soi log "chờ extension" (triệu chứng SW chết câm).
- Không chạy `dotnet build`/`dotnet test` (theo yêu cầu giao việc — có agent khác đang làm phần C#). Không sửa
  file C# nào; chỉ ĐỌC `OrdersBridgeChannel.cs`, `OrdersBridgeSession.cs`, `ShopFlowRunner.cs`,
  `OrdersBridgeLauncher.cs` để đối chiếu hợp đồng.
- Chưa commit (đúng yêu cầu).

### Đề xuất

- Khi phát hành bản kèm đợt này, nên chạy MỘT vòng shop thật trước khi đẩy rộng: rủi ro duy nhất còn lại của
  một đợt tách cơ học là link ES module lúc Chrome nạp — mà thứ đó `linkcheck.mjs` đã mô phỏng được, nhưng
  Chrome MV3 vẫn có thể khác ở chi tiết (vd đường dẫn phân biệt hoa/thường trên bản chép tạm).
- Cân nhắc đưa `extensions/*/…` vào một lượt kiểm tra tĩnh của `release-suite.cmd` (kiểu `node --check` +
  đối chiếu import↔export như script ở đợt này): `node --check` một mình KHÔNG bắt được import sai tên —
  đã chứng minh ở mục đối chứng âm 7.

---

## Nghiệm thu (Fable tổng hợp sau phản biện, 2026-08-06)

`nghiem-thu` chấm **CHƯA ĐẠT** với đúng **1 lỗi chặn**, sau đó phiên chính đã vá và tái kiểm — giờ ĐẠT:

- **Lỗi chặn (P0), đã vá**: `flow-shop.js:163` trong `doReadToShip` — dòng gốc có 3 chỗ phải đổi `ctx.*`
  nhưng chỉ đổi 2, còn `shopTabId` trần ở nhánh giữa → ReferenceError ngay nhánh THƯỜNG (sau openShopDetail),
  lan sang C# thành hủy nguyên vòng shop (0/N shop). Nghiệm thu tái lập thật bằng cách gọi hàm với chrome giả.
  Vá: `ctx.shopTabId != null ? ctx.shopTabId : ctx.listTabId`. Phiên chính chạy lại 2 phép kiểm quyết định
  (freevars.mjs = 0 vi phạm; swload.mjs CA1+CA2 đều OK).
- **Bài học phương pháp (đáng nhớ)**: cả hai script kiểm của người thực thi đều "so với bản gốc SAU KHI đảo
  ngược phép biến đổi" — cách đó về nguyên tắc KHÔNG THỂ bắt lỗi áp-thiếu phép biến đổi (phép đảo triệt tiêu
  chính lỗi). Phải quét BIẾN TỰ DO (free variable) mới lộ. Script để lại ở scratchpad phiên 66c0a9c6.
- Ghi nhận mức thấp đã xử lý: thêm comment bất biến "connect sau setCommandHandler" ở core.js.
- Còn nguyên các kết luận ĐẠT khác: 58/58 thân hàm khớp từng byte, import graph một chiều, page* tự chứa,
  12 lệnh khớp 1:1 với C#, PrepareFreshExtensionCopy chép đệ quy đủ.
- **Việc cho lần chạy thật đầu tiên sau phát hành**: chạy MỘT vòng shop thật trước khi đẩy rộng; triệu chứng
  nếu hỏng là treo "chờ extension" 45s (SW chết câm).
