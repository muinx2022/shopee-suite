# Plan: Sửa 6 lỗ NẶNG của phần check đơn hàng (review 11/08)

- **Ngày:** 2026-08-11
- **Trạng thái:** HOÀN THÀNH — đủ 5 chặng + **đã nghiệm thu CHẠY THẬT trọn 1 vòng 12/12 shop** trên bản build
  mới (18:01 ngày 11/08, xem cuối file). Chưa commit (chờ user).
- **Người lập:** Fable (phiên chính) · **Người thực thi:** `opus-dev`

## 1. Bối cảnh & mục tiêu

Đợt review 11/08 (4 lượt phản biện độc lập + phiên chính kiểm chứng từng phát hiện trên code) tìm ra 6 lỗ
NẶNG trong phần check đơn hàng — 3 ở tầng lưu/đẩy client, 3 ở extension — đều thuộc lớp **mất dữ liệu âm
thầm** hoặc **thao tác thật trên mục tiêu sai**. Đợt này sửa 6 lỗ đó (V1–V7) + 1 việc 1-dòng cùng file
(V8). Nhóm phát hiện TRUNG BÌNH/NHẸ còn lại của review **không thuộc đợt này**.

Bối cảnh kỹ thuật cần biết (đủ để không phải đọc lịch sử chat):

- Mã trả hàng sống ở bảng `return_codes` (khóa `(account_id, order_sn)`, cột `code`), độc lập vòng đời đơn.
  Cờ `gsheet_synced_at NULL` = còn chờ đẩy. `LuuMaTraHang` (DO UPDATE khi mã ĐỔI) reset cờ + làm mới
  `created_at`.
- Bảng `orders` có cặp chốt thế hệ `hub_push_gen`/`gsheet_push_gen` (+cột `*_sent`): mọi đường GHI đơn +1
  gen để lượt Mark* đang bay không đóng cờ oan. `GetForGsheetPush` chụp `gsheet_push_gen_sent`;
  `MarkGsheetSynced` chỉ đóng khi gen khớp.
- Mốc "số yêu cầu trả hàng" theo shop: `account_shops.return_count_last_tra_hang`
  (`ResultsRepository.GetReturnCount`/`SetReturnCount`, UPSERT không đụng cột khác). Từ đợt 09/08 mốc KHÔNG
  còn quyết định độ sâu đọc — độ sâu do `MaMoiTrong` (đếm mã trang vừa đọc chưa có trong kho, sau lọc cửa
  sổ 20 ngày) quyết.
- Extension `extensions/shopee-orders/` là mã chạy thật ngoài lưới test; lưới duy nhất là
  `suite/Shopee.Core.Tests/ExtensionJsCuPhapTests.cs` (parse Acornima + check import/export). File
  `flow-returns.js` từng làm chết cả service worker vì 1 ký tự thừa — sửa JS phải chạy lại lưới này.
- Service worker MV3 chết/dựng lại thường xuyên (plan 09/08 đo chu kỳ đứt 240s). `ctx` trong `core.js` là
  bộ nhớ SW; hiện chỉ `listTabId` + `wsPort` được persist vào `chrome.storage.session`.

## 2. Phạm vi

- **Làm:** V1–V8 dưới đây. Chỉ đụng `orders/` + `extensions/shopee-orders/` (+ test tương ứng).
- **Không làm:**
  - KHÔNG đụng hub/server (`server/`) — các phát hiện T4–T9 của review để đợt sau.
  - KHÔNG đổi khóa `return_codes`, KHÔNG đổi layout sheet, KHÔNG đổi hợp đồng gói tin cầu nối (tên
    action/field hiện có — chỉ được THÊM field tùy chọn).
  - KHÔNG commit / push / release. KHÔNG bump version.
  - KHÔNG sửa các nhóm phát hiện TRUNG BÌNH khác (T1–T12 trừ T13), kể cả khi "tiện tay".

## 3. Các bước thực hiện

### V1 — `DanhDauDaDay` đóng cờ theo CẶP (mã đơn, mã), không theo mã đơn trần

**Lỗ:** [ReturnCodesRepository.cs:243](../orders/XuLyDonShopee.Core/Data/ReturnCodesRepository.cs) UPDATE
`WHERE account_id AND order_sn` — không so `code`. Lô sheet bay lâu (nhiều lô × ≤120s); bước check shop chạy
song song ghi mã mới R2 (cờ về NULL) → lô về đóng cờ đè lên R2 → R2 không bao giờ lên sheet, không log.

**Sửa:**
- `DanhDauDaDay` nhận `IReadOnlyList<(string OrderSn, string Code)>`; SQL thêm `AND code = $code`.
- Caller `HubOutbox.PushReturnCodesToGsheetAsync` (`orders/XuLyDonShopee.App/Services/HubOutbox.cs`, quanh
  dòng 660–690): `xong` giữ cặp (sn, code) — code lấy từ chính `GsheetReturnCodeRow` đã gửi trong nhóm
  (map `MaDon → DonTraHang`), KHÔNG đọc lại DB (đọc lại là lấy nhầm R2).

**Test (kèm THỬ PHÁ):** lưu R1 → đổi thành R2 (`LuuMaTraHang` lần 2) → `DanhDauDaDay` với cặp (sn, R1) ⇒
`gsheet_synced_at` của dòng (đang mang R2) **vẫn NULL**; với cặp (sn, R2) ⇒ đóng. Phá: bỏ `AND code=$code`
⇒ test đỏ.

### V2 — `SetReturnRequestCodes` phải +1 `gsheet_push_gen`

**Lỗ:** [OrdersRepository.cs:274-279](../orders/XuLyDonShopee.Core/Data/OrdersRepository.cs) mở cờ
`gsheet_da_co_don_tra_hang = NULL` + `hub_push_gen + 1` nhưng KHÔNG `gsheet_push_gen + 1` → lô sheet đang
bay `MarkGsheetSynced` khớp gen cũ, đóng lại cờ vừa mở ⇒ `donTraHangMoi` false vĩnh viễn, mã đổi không bao
giờ đi đường đơn thường nữa.

**Sửa:** thêm `gsheet_push_gen = gsheet_push_gen + 1` vào chính câu UPDATE đó. Sửa luôn comment sai ở
`Database.cs` quanh dòng 306–310 (câu "Chỗ duy nhất reset … là nút Đẩy lại" — sai phạm vi: chốt thế hệ bảo
vệ CẢ NHÓM cờ gsheet, mọi đường mở bất kỳ cờ nào trong nhóm đều phải +1 gen).

**Test (kèm THỬ PHÁ):** `GetForGsheetPush` chụp gen → `SetReturnRequestCodes` đổi mã → `MarkGsheetSynced`
với gen đã chụp + `coDonTraHang:true` ⇒ **không** đóng (đơn còn trong hàng đợi gsheet / cờ
`gsheet_da_co_don_tra_hang` vẫn NULL). Phá: bỏ dòng +1 ⇒ đỏ.

### V3 — Bước DỌN không được tin ảnh chụp: `DeleteOrders` có mệnh đề thế hệ

**Lỗ:** `HubOutbox.PushOrdersToGsheetAsync` đọc `pending` MỘT lần (dòng ~288) rồi sau nhiều phút (đọc PDF +
POST Apps Script) mới dọn (dòng ~542–573) bằng chính ảnh chụp đó; `DeleteOrders` xóa vô điều kiện. Cờ mở
lại giữa chừng (nút "Đẩy lại" `DatLaiCoDayLai`, `SetReturnRequestCodes`, `UpsertMany` đổi status) bị xóa
theo — cú bấm của user bốc hơi, hub vĩnh viễn thiếu mã. Chốt thế hệ hiện chỉ canh nhánh `MarkGsheetSynced`,
không canh nhánh settled-by-design và các cờ `DaDayHub`/`DaDayPhieuHub` chụp từ đầu.

**Sửa:**
- `GsheetPendingOrder` mang thêm `HubPushGen` (đọc trong cùng transaction của `GetForGsheetPush`).
- Đường DỌN gọi `DeleteOrders` bản mới nhận cặp `(OrderSn, GenChup)`:
  `DELETE … WHERE account_id=$a AND order_sn=$sn AND hub_push_gen=$gen`.
- Mọi đường ghi mở-lại-nghĩa-vụ hiện có đều +1 `hub_push_gen` (Đẩy lại, mã trả, UpsertMany reset) nên mệnh
  đề này chặn đủ các ca đã dựng. Grep mọi call-site `DeleteOrders`: đường xóa TAY của user (nếu có) giữ
  hành vi cũ (ý chí trực tiếp), CHỈ đường dọn tự động đi bản mới.

**Test (kèm THỬ PHÁ):** đọc `GetForGsheetPush` (chụp gen) → `DatLaiCoDayLai` (hoặc `SetReturnRequestCodes`)
→ `DeleteOrders` với gen đã chụp ⇒ đơn **CÒN** trong DB; không ai đụng gì ⇒ xóa bình thường. Phá: bỏ mệnh
đề `AND hub_push_gen=$gen` ⇒ đỏ.

### V4 — Ô tổng N>0 mà 0 dòng: cảnh báo bắt buộc + KHÔNG chốt mốc

**Lỗ:** [ShopFlowRunner.cs:850](../orders/XuLyDonShopee.Core/Services/ShopFlowRunner.cs) `dong.Count > 0`
bỏ nguyên khối lưu không log; `docDuSau` vẫn true → chốt mốc. Selector dòng đổi (`.return-row-item` /
`headHtml` — [TraHangParser.cs:271](../orders/XuLyDonShopee.Core/Services/TraHangParser.cs) bỏ im dòng
thiếu headHtml) thì mọi shop "trông khỏe" trong khi 0 mã nào được đọc, lặp mọi vòng.

**Sửa:**
- C# `CheckDonTraHangAsync`: sau vòng lật, nếu `soMoi > 0 && dong.Count == 0` ⇒ `docDuSau = false` (giữ
  mốc), log `⚠ … {soMoi} yêu cầu mà KHÔNG đọc được dòng nào — selector dòng/khối đầu dòng có thể đã đổi`,
  in `doc.ChanDoan` nếu có. Ca `soMoi == 0` giữ NGUYÊN hành vi (shop sạch, mốc 0 ghi bình thường).
- Extension `flow-returns.js` bước 5: khi quét xong mà `list.length === 0` **và** ô tổng parse ra > 0 ⇒ lấy
  `pageChanDoanTraHang` nhét vào field `chanDoan` của payload (field đã có trong hợp đồng, `ParseKetQua`
  đã đọc). Chỉ làm khi rơi vào ca này để không tốn mỗi lượt.

**Test (kèm THỬ PHÁ):** dựng `KetQuaDocTraHang` SoYeuCau=33, Dong rỗng qua đường test hiện có
(`TraHangKhongBoSotTests` đã có hạ tầng fake) ⇒ mốc KHÔNG được ghi + nhật ký có dòng cảnh báo. Phá: bỏ điều
kiện ⇒ đỏ. JS: `node --check` + lưới Acornima.

### V5 — Cờ "còn sót" bền theo shop: rút cạn tồn đọng qua nhiều lượt

**Lỗ:** cửa vào vòng lật ([ShopFlowRunner.cs:805](../orders/XuLyDonShopee.Core/Services/ShopFlowRunner.cs))
đòi *trang 1 còn mã mới*; vòng lật `break` khi gặp trang toàn-mã-cũ (dòng ~830). Lượt chạm trần 200
dòng/10 trang bỏ lại phần đuôi nằm SAU dải-đã-biết ⇒ không lượt nào với tới nữa; khi đuôi trôi lên thì đã
quá cửa sổ 20 ngày → `LocTheoCuaSo` bỏ ⇒ mất vĩnh viễn. Câu log "để lượt sau đọc tiếp" đang hứa cơ chế
không tồn tại.

**Sửa:**
- Migration `Database.cs`: cột mới `account_shops.tra_hang_con_sot INTEGER NOT NULL DEFAULT 0` (theo mẫu
  migration sẵn có; default 0 = shop cũ giữ nguyên hành vi).
- `ResultsRepository`: đọc/ghi cờ cùng chỗ mốc (mở rộng `GetReturnCount`/`SetReturnCount` hoặc thêm cặp hàm
  riêng — chọn lấy một, giữ UPSERT không đụng cột khác).
- `ShopFlowRunner.CheckDonTraHangAsync` (chuyển cờ qua callback như mốc hiện tại):
  - Cửa vào vòng lật: `coTrangSau && _demMaTraChuaBiet != null && (MaMoiTrong(doc.Dong) > 0 || conSot)`.
  - Trong vòng lật, khi `conSot`: **KHÔNG** `break` ở `maMoi == 0` (vẫn break ở trần dòng / trần trang /
    hết trang / lật trượt). Log rõ đang ở "chế độ rút tồn đọng".
  - Cuối lượt: `docDuSau == false` ⇒ ghi cờ 1 (mốc giữ nguyên như hiện tại); `docDuSau == true` ⇒ ghi cờ 0
    + chốt mốc như hiện tại. (V4 đặt `docDuSau=false` nên ca 0-dòng cũng tự bật cờ — đúng ý.)
  - Sửa các câu log cho khớp cơ chế mới (giờ "lượt sau đọc tiếp" là THẬT).
- Lưu ý ràng buộc có sẵn: `sortApplied == false` vẫn cấm lật trang (giữ nguyên); trần 200/10 vẫn giữ (chi
  phí mỗi lượt có trần, tồn đọng rút dần qua nhiều lượt).

**Test ma trận (kèm THỬ PHÁ):** (a) lượt chạm trần dòng ⇒ cờ bật + mốc giữ; (b) lượt sau trang 1 KHÔNG có
mã mới nhưng cờ bật ⇒ VẪN lật trang; (c) lượt đọc tới đáy (hết trang) ⇒ cờ tắt + mốc chốt; (d) lật trượt ⇒
cờ vẫn bật. Phá: bỏ `|| conSot` ở cửa vào ⇒ test (b) đỏ.

### V6 — Pager: cuộn `instant` + đo hai nhịp + không trượt im lặng

**Lỗ:** [page-funcs.js:357-359](../extensions/shopee-orders/page-funcs.js) `scrollIntoView({block:"center"})`
rồi đo rect NGAY — đúng thủ phạm đã kết luận cho nút sắp xếp (plan 09/08 mục 11) nhưng pager chưa được vá,
mà pager luôn nằm đáy trang (luôn phải cuộn). Caller thì `break` trần khi `!changed`
([flow-orders.js:78-83](../extensions/shopee-orders/flow-orders.js)) ⇒ **mất đơn trang 2+ không một dòng
log** (nghiệm thu 12 shop/1128 đơn ⇒ ~94 đơn/shop, lật trang là đường đi THƯỜNG). Cùng lỗ ở
`doRedownloadSlip` (báo sai "không thấy đơn") và `latTrang` trả hàng.

**Sửa (áp đúng khuôn đã kiểm chứng của nút sắp xếp):**
- `pageFindNextPage`: `scrollIntoView({ block: "center", behavior: "instant" })`.
- Caller (`doSyncOrders`, `doRedownloadSlip` trong `flow-orders.js`; `latTrang` trong `flow-returns.js`):
  gọi tìm-nút hai nhịp (nhịp 1 để cuộn, nghỉ ~400ms, nhịp 2 lấy tọa độ thật); khi `!changed` thì thử bấm
  lại đúng MỘT lượt; vẫn trượt ⇒ `send progress` `"lật sang trang N trượt — dừng ở N−1 trang, đọc THIẾU"`
  rồi mới break. KHÔNG đổi hợp đồng gói tin.

**Kiểm:** `node --check` từng file sửa + `ExtensionJsCuPhapTests` xanh. (Logic chỉ nghiệm thu được khi chạy
thật — chặng chốt của phiên chính lo, ghi rõ trong báo cáo.)

### V7 — `shopTabId` bền qua cú chết service worker; lệnh cấp đơn không lùi im lặng

**Lỗ:** `ctx.shopTabId` chỉ nằm trong bộ nhớ SW ([core.js:12-16](../extensions/shopee-orders/core.js));
`background.js` chỉ persist `listTabId`+`wsPort` (dòng 80, 100–101); `orderTabId()` lùi về `listTabId` IM
LẶNG (core.js:136-138). SW chết giữa shop (thường xuyên) ⇒ SW mới có `shopTabId=null` ⇒
`prepareNextOrder`/`setPickupAddress`/`readReturnRequests` chạy trên TAB PICKER: kéo picker khỏi
`/portal/shop`, tab shop mồ côi, nguy cơ thao tác THẬT trên shop sticky sai.

**Sửa:**
- Persist: gán `ctx.shopTabId` ([flow-shop.js:217](../extensions/shopee-orders/flow-shop.js)) thì
  `chrome.storage.session.set({shopTabId})`; `doCloseShopTab` xóa (set null). `noiLaiTuStorage`
  (background.js) khôi phục kèm VALIDATE: `chrome.tabs.get` sống + URL thuộc `banhang.shopee.vn` — không
  hợp lệ ⇒ để null.
- Hết lùi im lặng: các flow CẤP ĐƠN (`flow-orders`, `flow-address`, `flow-returns`) dùng biến thể nghiêm
  (`orderTabIdStrict()` hoặc tương đương): `shopTabId == null` ⇒ `send({action:"error", message:"mất ngữ
  cảnh tab shop (service worker vừa khởi động lại) — bỏ lượt"})`, KHÔNG chạy trên picker. (C# sẵn cơ chế:
  `error` fault chặng đang chờ → shop ghi lỗi, vòng sau chạy lại.) `gotoSellerCentre`/`readShopList`/
  `openShopDetail`/`doCloseShopTab` giữ nguyên đường `listTabId`.
- Lưu ý: sau `openShopDetail` nhánh "shop mở cùng tab picker" thì `shopTabId == listTabId` — hợp lệ, biến
  thể nghiêm chỉ đòi `shopTabId != null`, không đòi khác `listTabId`.

**Kiểm:** `node --check` + `ExtensionJsCuPhapTests`. (Ca SW chết thật: nghiệm thu chạy thật ở chặng chốt.)

### V8 — (T13, 1 dòng) dòng đối chứng tab hết mù

Nhánh `if (ct && ct.daDung)` ([flow-returns.js:209-210](../extensions/shopee-orders/flow-returns.js)) gán
thêm `dangChon = ct.dangChon || ""` — để dòng `tab loại đơn đang chọn: "…"` in được nhãn thật ở đúng nhánh
dương-tính-giả từng xảy ra (07:08 ngày 10/08).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` — 0 warning, 0 error.
- [ ] `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` — xanh 100%, có test MỚI cho
      V1/V2/V3/V4/V5 đúng các ca ghi trong từng mục.
- [ ] `dotnet test suite/Shopee.Core.Tests/Shopee.Core.Tests.csproj` — xanh (lưới Acornima cho JS).
- [ ] `node --check` sạch cho TỪNG file JS đã sửa.
- [ ] **Mỗi test mới có lượt THỬ PHÁ** ghi trong báo cáo: phá → đỏ ĐÚNG test đó → khôi phục → xanh (luật 4).
- [ ] V3: grep xác nhận mọi call-site `DeleteOrders` — đường dọn tự động đi bản có gen, đường xóa tay (nếu
      có) ghi rõ vì sao giữ.
- [ ] V5: migration chạy được trên DB cũ (test migration theo mẫu `DatabaseMigrationTests`).
- [ ] Không đụng file ngoài `orders/`, `extensions/shopee-orders/`, `suite/Shopee.Core.Tests/` (nếu cần
      chỉnh lưới JS), `plans/`.
- [ ] KHÔNG commit.

## 5. Rủi ro & lưu ý

- **Máy này hay có nhiều phiên Claude song song trên cùng repo.** `git status` trước khi bắt đầu (lúc lập
  plan: cây SẠCH tại `c865b38`). Thấy file `M` lạ ⇒ DỪNG, báo lại, không "tiện tay" sửa.
- `flow-returns.js` từng giết service worker vì 1 ký tự — mọi sửa JS phải qua `node --check` +
  `ExtensionJsCuPhapTests` trước khi báo xong.
- V1: lấy `code` cho cặp từ payload ĐÃ GỬI, tuyệt đối không đọc lại DB lúc mark (đọc lại là lấy nhầm mã mới).
- V3: nếu `GsheetPendingOrder` được test khác dựng bằng positional args thì thêm field mới ở CUỐI record để
  đỡ vỡ test cũ; sửa test vỡ theo đúng nghĩa, không nới lỏng assert.
- V5: cờ `conSot` KHÔNG được vượt `sortApplied == false` (cấm lật khi không tin thứ tự — giữ nguyên).
- V6/V7: logic extension không kiểm chứng được bằng test đơn vị — phần "chạy thật một tài khoản" là việc
  của phiên chính ở chặng chốt, KHÔNG phải của lượt thực thi này; báo cáo phải ghi rõ giới hạn đó.
- Số trần mới (400ms nghỉ hai nhịp, 1 lượt bấm lại) đặt thành hằng có tên trong JS (cùng nếp
  `TRAN_THOI_GIAN_SAP_XEP_MS`).

---

## Báo cáo thực thi (opus-dev, 2026-08-11)

Cây SẠCH tại `c865b38` lúc bắt đầu (chỉ có file plan này là untracked) — không có phiên khác đang sửa dở.

### Đã làm từng việc

**V1 — `DanhDauDaDay` theo CẶP.** `ReturnCodesRepository.DanhDauDaDay` đổi chữ ký sang
`IReadOnlyCollection<(string OrderSn, string Code)>`, SQL thêm `AND code = $code`. Caller
`HubOutbox.PushReturnCodesToGsheetAsync` dựng `maDaGui` (map `MaDon → DonTraHang`) **từ chính
`nhom.Value` đã gửi**, không đọc lại DB. Mã đơn lạ trong phản hồi (không có trong lô) → đếm vào `boQua`,
KHÔNG đánh dấu (thà đẩy lại thừa).

**V2 — `SetReturnRequestCodes` +1 `gsheet_push_gen`.** Thêm vào chính câu UPDATE + xmldoc. Sửa comment sai
phạm vi ở `Database.cs` (chốt thế hệ bảo vệ CẢ NHÓM cờ gsheet, nên `SetReturnRequestCodes` cũng phải +1).

**V3 — `DeleteOrders` có mệnh đề thế hệ.** `GsheetPendingOrder` thêm `long HubPushGen = 0` **ở CUỐI record**;
`GetForGsheetPush` đọc thêm cột `hub_push_gen` (chỉ số 22, thêm cuối danh sách cột nên không lệch reader cũ).
`DeleteOrders` nhận `(OrderSn, GenChup)` + `AND hub_push_gen = $gen`. Grep toàn repo: **chỉ MỘT call-site
thật** là `HubOutbox.cs:570` (đường dọn tự động) — KHÔNG có đường xoá tay của user, nên không giữ nạp chồng
bản cũ (một hàm, một hành vi). Thêm dòng log khi `n < deletable.Count` (giữ lại bao nhiêu đơn vì thế hệ lệch).

**V4 — ô tổng N>0 mà 0 dòng.** C# `CheckDonTraHangAsync`: sau vòng lật, `soMoi > 0 && dong.Count == 0` ⇒
`docDuSau = false` + log `⚠ … KHÔNG đọc được dòng nào …` + in `doc.ChanDoan`. Ca `soMoi == 0` giữ NGUYÊN
(có test đối chứng). JS `flow-returns.js` bước 5: quét ra 0 dòng mà ô tổng parse ra > 0 ⇒ nhét
`pageChanDoanTraHang` vào field `chanDoan` sẵn có (chỉ ở đúng ca này).

**V5 — cờ "còn sót" bền theo shop.** Cột mới `account_shops.tra_hang_con_sot INTEGER NOT NULL DEFAULT 0`
(CREATE TABLE + `EnsureColumn`). `ResultsRepository.GetTraHangConSot` / `SetTraHangConSot` (cặp hàm RIÊNG, UPSERT
không đụng cột khác — chọn phương án này thay vì mở rộng `Get/SetReturnCount` để chỗ gọi đọc rõ nghĩa).
`ShopFlowRunner`: hai callback tuỳ chọn mới `conSotTraHang` / `luuConSotTraHang` (**quyết định tự chốt**: shape
`Func<string,bool>` + `Action<string,bool>`, cùng khuôn `returnCountLast`/`saveReturnCount`, null ⇒ hành vi y
như trước — đường "Chạy thử" không rót). Cửa vào vòng lật thêm `|| conSot`; trong vòng, `maMoi == 0` chỉ break
khi `!conSot`; cuối lượt `docDuSau` quyết cả mốc lẫn cờ. `sortApplied == false` vẫn cấm lật (có test).
Wiring: `OrdersBridgeSession` (thêm 2 param + xmldoc) → `AccountSession`.

**V6 — pager.** `pageFindNextPage` cuộn `behavior:"instant"` (kèm fallback try/catch như
`pageLocateSortButton`). Module MỚI `extensions/shopee-orders/pager.js`: `timNutTrangSau(tabId)` hai nhịp +
hằng có tên `NGHI_DO_LAI_PAGER_MS = 400`, `SO_LAN_BAM_LAI_PAGER = 1`. Ba caller (`doSyncOrders`,
`doRedownloadSlip`, `latTrang`) dùng hàm này, `!changed` ⇒ bấm lại đúng 1 lượt ⇒ vẫn trượt thì `send progress`
nói rõ "đọc THIẾU" rồi mới break. Hợp đồng gói tin KHÔNG đổi (chỉ thêm dòng `progress`).

**V7 — `shopTabId` bền + hết lùi im lặng.** `core.js`: bỏ `orderTabId()` (đường lùi im lặng), thay bằng
`orderTabIdStrict()` + hằng `LOI_MAT_TAB_SHOP`. 7 chỗ gọi ở `flow-orders` / `flow-address` / `flow-returns`
chuyển sang biến thể nghiêm, `tabId == null` ⇒ `send({action:"error", …})`. Hai chỗ trước đây trả "kết quả
rỗng" (`doRedownloadSlip`, `doReadReturnRequestsMore`) nay cũng báo `error` — rỗng nhìn y hệt "không tìm thấy
đơn" / "lật trượt", tức đổ oan cho dữ liệu. `flow-shop.js`: mọi lối gán đi qua `nhoTabShop()` (ghi `ctx` + lưu
`chrome.storage.session`). `background.js`: `noiLaiTuStorage` đọc thêm `shopTabId` và VALIDATE qua
`khoiPhucTabShop` (tab còn sống + URL thuộc `banhang.shopee.vn`, không hợp lệ ⇒ null + xoá khỏi storage).
`flow-shop.js:166` (`doReadToShip`) vẫn giữ lối lùi cũ — lệnh cấp SHOP, ngoài phạm vi plan (ghi ở mục dưới).

**V8.** Nhánh `if (ct && ct.daDung)` gán `dangChon = ct.dangChon || ""`.

### Kiểm chứng THẬT (cây làm việc `D:\Projects\shopee-suite`, không dùng worktree)

| Lệnh | Kết quả |
|---|---|
| `dotnet build ShopeeSuite.sln` | **0 warning, 0 error** |
| `dotnet test orders/XuLyDonShopee.Tests` | **1749 passed / 0 failed** (trước khi sửa: 1737 — **+12 test mới**) |
| `dotnet test suite/Shopee.Core.Tests` | **139 passed / 0 failed** (lưới Acornima, đã quét cả `pager.js`) |
| `node --check` | sạch cho **toàn bộ 14 file** `.js` của `extensions/shopee-orders` (chạy lại sau mỗi lượt sửa JS) |

⚠ Lệnh build solution phải chạy với `-p:OutDir=<scratch>` vì app **ShopeeSuite (PID 27444) đang chạy** khoá
các DLL trong `suite/Shopee.Suite/bin` — lỗi/warning MSB302x của lượt build thẳng là **file lock, KHÔNG phải
warning trình biên dịch** (không có lấy một `warning CS`). Không tự tắt app của user.

### Thử phá (mỗi test mới đều có ít nhất một lượt phá làm nó ĐỎ)

| # | Phá gì | Test ĐỎ |
|---|---|---|
| 1 | Bỏ `AND code = $code` trong `DanhDauDaDay` | `MaTraHangDocLapTests.DanhDauDaDay_MaDaDoiGiuaLucLoDangBay_KhongDongCoOan` |
| 2 | Bỏ `gsheet_push_gen + 1` ở `SetReturnRequestCodes` | `DayLaiDonTests.MaTraHangDoi_XenGiuaLoSheetDangBay_KhongDongCoOan` |
| 3 | Bỏ `AND hub_push_gen = $gen` ở `DeleteOrders` | `DayLaiDonTests.Don_DonDaXongNhungCoMoLaiGiuaLuot_KhongBiXoa` + `Don_MaTraHangVuaGhiGiuaLuot_KhongBiXoa` |
| 4 | Tắt hẳn khối V4 (`if (false && …)`) | `TraHangKhongBoSotTests.OTongCoSo_MaKhongDocDuocDongNao_GiuMoc_CanhBao_BatCoConSot` |
| 5 | Nới V4 thành `soMoi >= 0` (phá ca shop sạch) | `TraHangKhongBoSotTests.OTongBang0_KhongCoDong_VanChotMoc0_KhongCanhBao` |
| 6 | Bỏ `|| conSot` ở cửa vào vòng lật | `TraHangKhongBoSotTests.ConSot_TrangDauKhongCoMaMoi_VanLatTrang_DocToiDay_TatCo` |
| 7 | Trả `break` khi `maMoi == 0` (bỏ `&& !conSot`) | `ConSot_TrangDauKhongCoMaMoi_VanLatTrang_DocToiDay_TatCo` |
| 8 | Ghi cờ LUÔN `false` ở nhánh chưa-đủ-sâu | `OTongCoSo_…_BatCoConSot`, `ChamTranDong_GiuMoc_VaBatCoConSot`, `LatTrangTruot_BatCoConSot` |
| 9 | Ghi cờ LUÔN `true` ở nhánh đủ-sâu | `KhongConSot_TrangDauKhongCoMaMoi_VanKhongLatTrang`, `ConSot_…_TatCo`, `OTongBang0_…` |
| 10 | Bỏ chốt "không đổi được sắp xếp thì cấm lật" | `ConSot_NhungKhongDoiDuocSapXep_VanKhongLatTrang` + `KhongDoiDuocSapXep_KhongLatTrang` (test cũ) |
| 11 | Bỏ `EnsureColumn(tra_hang_con_sot)` | `TraHangLuuTests.Migration_DbCu_ThemCotTraHangConSot_ShopCuMacDinhKhongConSot` |

Sau mỗi lượt phá đều KHÔI PHỤC và chạy lại → xanh. Lượt full cuối cùng (1749/1749) chạy trên cây đã khôi phục.

### Test cũ phải sửa (sửa theo đúng nghĩa, KHÔNG nới assert)

- `OrdersRepositoryTests` (3 chỗ): `DeleteOrders` nay nhận cặp — đơn mới upsert có `hub_push_gen = 0`.
- `MaTraHangDocLapTests` (2 chỗ), `TraHangKhongBoSotTests` (1 chỗ): `DanhDauDaDay` nay nhận cặp.
- `TraHangLuuTests.SetReturnRequestCodes_MaDoi_GhiDe_VaResetCoDeDayLai`: trước đây `MarkGsheetSynced(pushGen: 0)`
  cứng số 0; nay lượt ghi mã ĐÃ +1 thế hệ nên test đọc lại gen hiện tại (đúng như lượt đẩy thật chụp gen).
  **Đây chính là bằng chứng V2 có hiệu lực** — test cũ đỏ đúng chỗ nó phải đỏ.

### GIỚI HẠN đã biết / cần phiên chính soi

1. **V6 + V7 chưa nghiệm thu được bằng test** — logic extension không có lưới đơn vị (chỉ có parse Acornima +
   `node --check`). **Chưa chạy thật lượt nào** ở chặng này. Phải chạy thật một tài khoản để xác nhận: lật trang
   đơn/trả hàng còn ăn, và ca SW chết giữa shop ra `error` chứ không thao tác trên tab picker.
2. **V1 mới khoá ở tầng repo.** Phần "lấy cặp từ payload đã gửi" trong `HubOutbox` chưa có test đầu-cuối (khó
   chèn một lượt ghi mã vào GIỮA lượt POST của Web App giả). Đọc mắt: `maDaGui` dựng từ `nhom.Value` trước khi
   gọi `PushReturnCodesAsync`.
3. **Đổi hành vi đáng soi:** `doRedownloadSlip` và `doReadReturnRequestsMore` nay gửi `error` (fault chặng đang
   chờ) thay vì trả kết quả rỗng khi mất tab shop. Đã kiểm `TaiLaiPhieuThieuAsync` bắt `InvalidOperationException`/
   `TimeoutException` per-đơn nên không vỡ vòng, nhưng đây là đường mới chưa chạy thật.
4. **`DeleteOrders` giờ chỉ còn bản có gen.** Nếu sau này thêm nút "xoá đơn" theo ý chí user thì phải thêm bản
   riêng, đừng dùng lại bản này với gen đọc-lại-DB (làm thế là vô hiệu hoá chốt).
5. **Cờ `tra_hang_con_sot` có thể kẹt 1 vĩnh viễn** ở shop không bao giờ đổi được sắp xếp (`sortApplied=false`
   ⇒ `docDuSau=false` mỗi lượt). Vô hại (cờ chỉ mở thêm đường lật, mà nhánh sort chặn trước), nhưng nếu muốn
   phân biệt "còn sót vì chạm trần" với "còn sót vì hỏng sắp xếp" thì cần cột thứ hai — không làm ở đợt này.

### Phát hiện NGOÀI phạm vi (không tự sửa)

- `flow-shop.js:166` (`doReadToShip`) vẫn tự lùi `ctx.shopTabId ?? ctx.listTabId` — cùng lớp lỗi V7 nhưng ở
  lệnh cấp SHOP; đọc "Chờ Lấy Hàng" trên tab picker sau khi SW chết sẽ ra số của shop SAI. Plan không nêu.
- Trình soạn thảo ghi một số file bằng LF (repo dùng CRLF, `core.autocrlf` tự chuẩn hoá nên `git diff` vẫn gọn);
  chỉ là ghi chú lúc stage.

---

## Chặng 3 — NGHIỆM THU (11/08, agent `nghiem-thu` chạy lại độc lập)

**ĐẠT — 8/9 tiêu chí ĐẠT, 1 ĐẠT MỘT PHẦN.** Tự chạy lại: build sln (OutDir scratch) **0 warning / 0 error**;
orders **1749/1749**; core **139/139**; `node --check` sạch 13 file `extensions/shopee-orders/*.js` (con số
"14" trong báo cáo thực thi là đếm nhầm, vô hại); grep `DeleteOrders` đúng 1 call-site sản xuất; HEAD vẫn
`c865b38`, không file ngoài phạm vi. Tiêu chí thử-phá chấm "một phần" vì nghiệm thu không sửa code nên chỉ đối
chiếu được bảng phá phủ đủ 12/12 test mới, không tự phá lại. Không việc nào của plan bị bỏ sót.

## Chặng 4 — PHẢN BIỆN (11/08): CHƯA ĐẠT, 3 việc phải sửa + 3 việc nhẹ

1. **[NẶNG] Lượt bấm-lại của pager có thể lật HAI trang** — `!doi` không phân biệt "bấm trượt" với "trang đổi
   SAU hạn chờ 10s" (fetch chậm; `waitXxxChanged` bỏ mẫu `0|…` đang tải). Bấm lại lúc đó là nhảy N→N+2, trang
   N+1 không ai quét; đường trả hàng còn tự chốt mốc + tắt cờ còn-sót ⇒ mất vĩnh viễn, im lặng. Cả 3 caller.
2. **[TRUNG BÌNH] `TraDiaChiVeKhacAsync` chỉ catch `TimeoutException`** — đường `error` mới của V7
   (`setPickupAddressToOther` mất tab) ném `InvalidOperationException` xuyên ra: shop chạy TRỌN bị đếm "hỏng
   giữa chừng", mất tổng kết + mất lượt check trả hàng; 3 shop liên tiếp là dừng cả vòng.
3. **[TRUNG BÌNH] V5 không hội tụ với shop tồn đọng sâu hơn trần** (200 dòng/10 trang): chế độ rút tồn đọng
   quét LẠI cửa sổ đầu mỗi vòng, phần đuôi chỉ tới lượt khi danh sách vơi; câu log "để lượt sau đọc tiếp" hứa
   quá lời. Chấp nhận cơ chế (chi phí mỗi vòng có trần), nhưng log phải nói thật.
4. [NHẸ] Phản hồi mang mã đơn ngoài lô bị đếm chung `boQua` → câu tóm tắt đổ oan "thiếu tiêu đề cột".
5. [NHẸ] Log "thế hệ lệch" kết luận oan khi lượt song song đã dọn trước. 6. [NHẸ] `khoiPhucTabShop`
   fire-and-forget có cửa sổ đua vài ms với `doCloseShopTab` (tự lành ở `openShopDetail` kế) — ghi nợ.

Phản biện cũng bác 4 giả thuyết đáng ngờ khác (V3 không rò rỉ đơn — không đường nào bump gen đều đặn trên đơn
terminal; định tuyến `error` fault đúng chặng; V4×V5 chỉ tốn 1 lượt lật/vòng khi selector hỏng; cờ kẹt-1 ở shop
hỏng sắp xếp vô hại thật).

## Chặng 5 — CHỐT VIỆC (phiên chính tự sửa 4 điểm, 11/08)

1. **Sửa NẶNG-1:** `pager.js` thêm `trangThaiTruocBamLai(tabId, hamChuKy, sigTruoc)` → `"doi"` (chữ ký đã khác
   ⇒ cú bấm đầu ĐÃ ăn, coi như thành công, KHÔNG bấm nữa) / `"dangTai"` (`0|…` hoặc đọc hụt ⇒ CHỜ thêm một
   lượt, không bấm) / `"chuaDoi"` (trượt thật ⇒ mới được bấm lại). Cả 3 caller (`doSyncOrders`,
   `doRedownloadSlip`, `latTrang`) hỏi chốt này TRƯỚC cú bấm lại. `node --check` sạch cả 3 file.
2. **Sửa TB-2:** `TraDiaChiVeKhacAsync` thêm `catch (InvalidOperationException)` (best-effort y như quá hạn).
   Test MỚI `TraHangKhongBoSotTests.TraDiaChiVeKhac_ExtensionBaoLoi_VanChayCheckTraHang_KhongNemRaNgoai` —
   **đã thử phá**: gỡ nhánh catch ⇒ test ĐỎ ⇒ khôi phục ⇒ xanh.
3. **Sửa TB-3 (mức log):** hai câu "chạm trần … để lượt sau đọc tiếp" đổi thành nói thật — lượt sau quét LẠI
   cửa sổ đầu, phần sâu hơn chỉ đọc được khi danh sách vơi bớt (cần xử lý bớt yêu cầu đang mở). Cơ chế giữ
   nguyên có chủ đích: con trỏ "đọc tới trang k" đòi extension nhảy trang tùy ý — để đợt sau nếu thực tế cần.
4. **Sửa NHẸ-4 + NHẸ-5:** tách biến đếm `lechHopDong` + câu tóm tắt riêng; câu log "thế hệ lệch" chừa đường
   cho ca lượt song song đã dọn trước.

**Ghi nợ (không làm đợt này):** NHẸ-6 (`khoiPhucTabShop` đua vài ms); `doReadToShip` còn lối lùi tab picker
(lệnh cấp SHOP — cùng lớp V7); phân biệt "còn sót vì trần" với "còn sót vì hỏng sắp xếp" (cột thứ hai);
con trỏ trang cho shop tồn đọng sâu hơn trần.

**Kiểm chứng chốt (sau 4 điểm sửa của chặng 5, phiên chính tự chạy):**

| Lệnh | Kết quả |
|---|---|
| `dotnet build ShopeeSuite.sln -p:OutDir=<scratch>` | **0 warning / 0 error** (14,7s) |
| `dotnet test orders/XuLyDonShopee.Tests` | **1750/1750** (1749 + 1 test mới của chặng 5) |
| `dotnet test suite/Shopee.Core.Tests` | **139/139** (lưới Acornima quét lại cả 3 file JS vừa sửa) |
| `node --check` pager.js / flow-orders.js / flow-returns.js | sạch |
| Thử phá test mới (gỡ `catch (InvalidOperationException)`) | ĐỎ đúng `TraDiaChiVeKhac_ExtensionBaoLoi_…` ⇒ khôi phục ⇒ xanh |

**NGHIỆM THU CHẠY THẬT (11/08, 17:08–18:01 — phiên chính tự dừng app cũ lúc nghỉ giữa vòng, build vào bin,
mở lại, tự bấm Chạy qua UI Automation, tự đọc nhật ký):**

- **Trọn vòng 12/12 shop, 1128 đơn, 0 phiếu — TRÙNG TỪNG CON SỐ với hai vòng cuối của bản cũ** (12:24 và
  14:47 cùng ngày): 34/3/0/54/202/71/240/59/32/400/17/16 đơn theo shop. Không hồi quy.
- **V6:** pager mới nhai hết các shop nặng (deilca 202, minoa 240, cicily **400 đơn** ≈ 20 trang) — **0** dòng
  "trượt"/"đọc THIẾU" toàn vòng.
- **V8:** cả 12 shop in nhãn tab thật (`"don tra hang hoan tien"`, minoa còn ra biến thể `"… (1)"` — dòng đối
  chứng làm đúng việc); **0** lần "(không đọc được nhãn)".
- **Check trả hàng:** chạy đủ 12/12 — deilca 33 dòng/3 đơn đa-yêu-cầu, cicily 64 yêu cầu/40 dòng, minoa lưu
  10 mã vào kho (đơn đã dọn — kho mã độc lập nhận đúng). **0** cảnh báo V4 (không có ca ô-tổng-có-số-mà-0-dòng
  — đúng, trang đọc bình thường).
- **V7:** cầu nối sống suốt 53′ (0 cú SW chết) ⇒ đường strict-tab chưa bị kích hoạt trong vòng này — nghiệm
  thu được mặt KHÔNG-hồi-quy; mặt "SW chết thì ra error thay vì thao tác nhầm tab" sẽ tự lộ ở lần SW chết
  tự nhiên kế tiếp (persist + validate đã có test lưới cú pháp, code soi tay 2 lượt).
- App để lại ĐANG CHẠY phiên bình thường (vòng kế 19:31) — trả lại đúng trạng thái vận hành của user.

**Việc còn lại:** commit + bump version + **nạp lại extension trên các MÁY KHÁC** khi phát hành (máy này bin
đã có bản mới; có file JS mới `pager.js` + đổi `core.js`/`background.js`) — chờ lệnh user.
