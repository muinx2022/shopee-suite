# Plan: Check đơn trả hàng — thôi bỏ sót (4 việc)

- **Ngày:** 2026-08-09
- **Trạng thái:** ĐÃ LÀM XONG cả 4 việc — build 0 warning, 1682/1682 test xanh, 4 lượt THỬ PHÁ đều đỏ đúng chỗ.
  Chờ phát hành client + **nạp lại extension** (có lệnh cầu nối MỚI). Xem "Báo cáo thực thi" cuối file.
- **Người lập + thực thi:** phiên chính (Opus)

## 1. Bối cảnh

Người dùng báo: *"check còn thiếu rất nhiều"*. Rà lại toàn chuỗi (`flow-returns.js` → `TraHangParser` →
`ShopFlowRunner.CheckDonTraHangAsync` → `ReturnCodesRepository` → `HubOutbox` → `Code.gs`) tìm ra **4 đường mất
mã**, ba trong số đó mất **VĨNH VIỄN** (mốc vẫn nhảy nên không lượt nào quay lại).

### Lỗ 1 — luật "số không đổi/giảm ⇒ không đọc dòng nào"

Số ở `.return-list-summary-title` là **mức tồn tại thời điểm đọc**, không phải bộ đếm cộng dồn — yêu cầu xử xong
rớt khỏi danh sách. Nên giữa hai lượt:

| Thực tế | Số hiển thị | Nhánh | Kết quả |
|---|---|---|---|
| +3 mới, −3 xử xong | không đổi | `KhongDoi` | 0 dòng đọc, **3 mã mất** |
| +2 mới, −5 xử xong | giảm | `Giam` | 0 dòng đọc, **2 mã mất** |
| +5 mới, −3 xử xong | +2 | `Tang` | đọc 2 dòng đầu, **3 mã mất** |

Mốc vẫn bị ghi đè ở cuối `CheckDonTraHangAsync` ⇒ mất vĩnh viễn.

**Điểm chua nhất:** extension **đã gửi sẵn** tối đa 50 dòng trong CÙNG payload (`flow-returns.js` gọi
`pageScanReturnRows` vô điều kiện). `Take(SoDongCanCheck)` bên C# vứt phần còn lại — nhánh `KhongDoi`/`Giam` vứt
cả 50 dòng đã cào được. **Bỏ luật cắt này tốn thêm 0 mili-giây trình duyệt.**

### Lỗ 2 — chỉ trang đầu ⇒ tồn đọng không bao giờ đọc tới

Lần đầu mỗi shop: đọc ≤ số dòng TRANG 1 (≈20), rồi ghi mốc = tổng (141/340) ⇒ toàn bộ tồn đọng vĩnh viễn không
được đọc. Lý do cũ của "không phân trang" (*"đơn không còn trong DB thì lưu mã cũng vứt"*) đã HẾT hiệu lực từ khi
có bảng `return_codes` sống độc lập — chính doc `TranDongMoiLuot` cũng ghi vậy mà trần thì chưa sửa theo.

### Lỗ 3 — Sheet: mã bị script BỎ QUA vẫn bị đánh dấu "đã đẩy"

`Code.gs` trả `ok:true` (+ `boQua:true`) khi không tra thấy mã đơn ở bất kỳ tab nào; `DocKetQua` **không parse**
`boQua`, `HubOutbox` chỉ đọc `Ok` rồi `DanhDauDaDay` ⇒ không bao giờ thử lại. Cùng kiểu: thiếu tiêu đề cột
(`canhBao`/`thieuCot`) → giá trị bị bỏ, vẫn `ok:true` ⇒ mất im lặng. Đây là lỗ người dùng NHÌN THẤY trực tiếp
(ô "Mã đơn trả hàng" trống dù app đã quét ra mã).

### Lỗ 4 — cả bước bị bỏ khi shop hỏng sớm

`CheckDonTraHangAsync` là mắt xích cuối, mà `RunShopOrdersAsync` `return` trước đó ở 3 chỗ: captcha khi đọc đơn,
captcha khi đặt địa chỉ, **không đặt được địa chỉ lấy hàng**. Shop dính lỗi địa chỉ (lỗi thường trực, có hẳn
banner riêng) **không bao giờ được check trả hàng**.

## 2. Phạm vi

**Làm (4 việc người dùng đã duyệt):**
1. Parse **HẾT** dòng extension gửi về, mọi lượt — bỏ `Take(SoDongCanCheck)`.
2. **Phân trang có điều kiện**: sâu ở lần đầu / khi số tăng vọt; steady-state vẫn đúng 1 trang.
3. Sheet: **không đánh dấu đã-đẩy** khi script báo `boQua` / thiếu cột; có trần tuổi để không thử lại vô hạn.
4. Bước check chạy cả ở nhánh **bỏ shop vì địa chỉ** (captcha vẫn tự bỏ như cũ).

**Không làm:**
- KHÔNG đổi khoá `return_codes` (ca một đơn nhiều yêu cầu — để đợt sau).
- KHÔNG đụng luật nhận diện mã (`TachMa` 3 tầng class→nhãn→vị trí) và cửa sổ 20 ngày.
- KHÔNG đụng bước đọc đơn / chuẩn bị hàng / in phiếu / địa chỉ.
- KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. Các bước

### Việc 1 — parse hết dòng (C#)

- `TraHangParser`: đổi `QuyetDinhTraHang.SoDongCanCheck` → `SoDongMoiUocTinh` (nay CHỈ để log + tính độ sâu phân
  trang, KHÔNG còn cắt danh sách). 4 nhánh luật giữ nguyên.
- `ShopFlowRunner.CheckDonTraHangAsync`: `GhepCap(doc.Dong)` — toàn bộ. Chống trùng đã có sẵn ở `LuuMaTraHang`
  (`WHERE code <> $code` ⇒ mã cũ không đụng dòng, không đẩy trùng, không notify trùng).
- `sortApplied == false` không còn nguy: đọc hết thì thứ tự không quan trọng (chỉ còn cấm phân trang, xem việc 2).

### Việc 2 — phân trang có điều kiện

- C# tính **số trang** bằng hàm thuần `SoTrangCanDoc(mocCu, soMoi)`:
  ```
  mocCu null (lần đầu)  → TranTrangTraHang (10)          — quét sâu MỘT lần cho mỗi shop
  tăng k > DongMoiTrangUocTinh → min(ceil(k / 10) + 1, 10)
  còn lại (≤10 / không đổi / giảm) → 1                    — steady-state giữ nguyên chi phí hiện tại
  ```
  `DongMoiTrangUocTinh = 10` cố ý ƯỚC LƯỢNG THẤP (trang thật ~20 dòng) — thà lật thừa một trang.
- Gửi kèm `maxPages` trong lệnh `readReturnRequests`; extension lật trang bằng `pageFindNextPage` (đã có sẵn,
  dùng chung với danh sách đơn) + `pageReturnListSignature` để chờ danh sách ĐỔI.
- **Không tin được thứ tự (`sortApplied === false`) → CHỈ trang 1** (lật trang lúc đó là nhặt ngẫu nhiên).
- `MAX_RETURN_ROWS` 50 → **200**, khớp `TraHangParser.TranDongMoiLuot`.
- Không thấy nút lật trang → gửi kèm **chẩn đoán pager** (HTML rút gọn) để lượt chạy thật lộ markup thật.

### Việc 3 — Sheet không nuốt mã

- `GsheetOrderResult` thêm `BoQua`; `DocKetQua` parse `boQua`.
- `PushRowsAsync` nhận cờ `canhBaoLaBoQua` (chỉ đường mã-trả bật): phản hồi có `canhBao` (thiếu tiêu đề cột) ⇒
  coi CẢ lô là `BoQua` — payload mã-trả chỉ ghi đúng cột đó nên thiếu cột = chắc chắn chưa ghi được.
- `HubOutbox.PushReturnCodesToGsheetAsync`: chỉ `DanhDauDaDay` các mã `Ok && !BoQua`; log số bị bỏ.
- Chặn thử-lại-vô-hạn bằng TUỔI: `LayMaTraHangChuaDay`/`DemChuaDay` chỉ lấy bản ghi ≤ `SoNgayThuLaiSheet` (14)
  ngày. Quá hạn → nằm im chờ `DonDep` (90 ngày) dọn, có log.

### Việc 4 — bước check không bị nhánh dừng sớm nuốt

Tách thân `RunShopOrdersAsync` → `ThanShopAsync` (giữ nguyên 3 nhánh `return` sớm), rồi:

```csharp
kq = await ThanShopAsync(...);      // ném → KHÔNG chạy bước phụ (cầu nối đang hỏng)
await CheckTraHangBocKinAsync(...); // chạy cả khi thân trả sớm vì địa chỉ
return kq;
```

Guard `_ch.CaptchaSeen` sẵn có bên trong `CheckDonTraHangAsync` lo phần captcha (trang đang là `/verify`).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` 0 warning; `dotnet test orders/XuLyDonShopee.Tests` xanh.
- [ ] `node --check` sạch cho 3 file extension đụng tới.
- [ ] Test: `KhongDoi`/`Giam` mà payload có dòng ⇒ **VẪN lưu mã** (khoá lại lỗ 1).
- [ ] Test ma trận `SoTrangCanDoc`: null→10; +3→1; +25→4 (kẹp 10); không đổi/giảm→1.
- [ ] Test: `sortApplied=false` ⇒ KHÔNG gửi lượt đọc thêm (chỉ trang đầu).
- [ ] Test: script trả `boQua:true` ⇒ **không** `DanhDauDaDay`; `canhBao` ⇒ cả lô coi như chưa đẩy.
- [ ] Test: mã quá 14 ngày chưa đẩy được ⇒ rơi khỏi `LayMaTraHangChuaDay`.
- [ ] Test: nhánh bỏ shop vì địa chỉ ⇒ `readReturnRequests` VẪN được gửi.

## 5. Rủi ro

- **Selector nút lật trang trên trang trả hàng CHƯA xác nhận** (dùng chung `pageFindNextPage` của trang đơn — cùng
  bộ EDS pager, nhiều khả năng đúng). Không thấy nút → chỉ đọc trang 1 (đúng hành vi hiện tại) + gửi chẩn đoán;
  KHÔNG được ném.
- Lượt đầu sau khi cài bản này **nặng hơn**: mỗi shop lật tới 10 trang một lần. Từ lượt hai về 1 trang. Chủ ý.
- Payload lớn hơn (200 dòng × 4000 ký tự head). Loopback WS, chấp nhận được.

---

## Báo cáo thực thi

### Đã làm

**Việc 1 — parse hết dòng.** `QuyetDinhTraHang.SoDongCanCheck` → `SoDongMoiUocTinh` (đổi tên để lộ hết call-site:
nay CHỈ để log + tính độ sâu trang). `QuyetDinhCheck` bỏ tham số `tranDong`, nhánh `LanDau` không kẹp trần nữa.
`CheckDonTraHangAsync` bỏ `Take(...)` — `GhepCap(dong)` chạy trên TOÀN BỘ dòng nhận được, mọi nhánh luật.

**Việc 2 — phân trang HAI NHỊP.** Không đoán trước độ sâu được (độ sâu phụ thuộc số yêu cầu, mà số đó phải đọc
từ trang mới biết), nên tách:
- nhịp 1 `readReturnRequests` — như cũ (mở trang, chọn tab, đổi sắp xếp, quét TRANG ĐẦU) + trả thêm
  `soTrangDaDoc` và `coTrangSau`;
- nhịp 2 `readReturnRequestsMore {maxPages}` — **lệnh cầu nối MỚI**, lật trang trên chính trang đang mở, KHÔNG
  điều hướng / KHÔNG chọn lại tab / KHÔNG đổi lại sắp xếp. Dùng lại `pageFindNextPage` của trang đơn (cùng bộ
  EDS pager) + `pageReturnListSignature` mới để chờ danh sách ĐỔI.

C# chỉ gửi nhịp 2 khi `SoTrangCanDoc > 1` **và** `SortApplied` **và** `CoTrangSau`. Ba điều kiện, mỗi cái chặn
một cái bẫy: khỏi lật vô ích ở lượt thường; khỏi nhặt ngẫu nhiên khi sắp xếp không áp được; và **khỏi gửi lệnh
mà extension đời cũ không biết rồi ngồi chờ hết 90s** (bản cũ không gửi `coTrangSau` ⇒ false ⇒ không bao giờ gửi
nhịp 2). Nhịp 2 còn bọc try/catch riêng, hỏng thì vẫn giữ nguyên phần trang đầu. Dòng hai nhịp GỘP rồi lưu MỘT
lần (một lượt notify).

`MAX_RETURN_ROWS` 50 → 200 (khớp `TranDongMoiLuot`), thêm `MAX_RETURN_PAGES` = 10 ↔ `TranTrangTraHang`.
Không thấy nút lật trang → gửi kèm HTML khối phân trang (`pageChanDoanPagerTraHang`) để lượt chạy thật phân biệt
"shop chỉ có một trang" với "selector pager của trang này khác trang đơn".

**Việc 3 — Sheet không nuốt mã.** `GsheetOrderResult` thêm `BoQua`; `DocKetQua` đọc `boQua`; thêm `DocCanhBao`
(thiếu tiêu đề cột). `PushReturnCodesAsync` bật cờ `canhBaoLaBoQua` — lô mã-trả chỉ ghi đúng một cột nên thiếu
tiêu đề = chắc chắn chưa ghi được gì. `HubOutbox` chỉ `DanhDauDaDay` cho `Ok && !BoQua`, log số bị bỏ, và trả
`KhongCanDay` (không phải `ThatBai`) khi cả lô bị bỏ mà không có lỗi đích — đích đang khoẻ, không có gì để
backoff. Chặn thử-lại-vô-hạn: `ReturnCodesRepository.SoNgayThuLaiSheet` = 14 ngày, áp CHUNG cho
`LayMaTraHangChuaDay` + `DemChuaDay` (badge đếm đúng cái sẽ được đẩy).

**Việc 4 — bước phụ thoát khỏi 3 nhánh dừng sớm.** Tách thân thành `ThanShopAsync` (giữ nguyên 3 nhánh `return`),
`RunShopOrdersAsync` gọi thân rồi mới chạy bước check. Ngoại lệ NÉM ra từ thân vẫn không chạy bước phụ (cầu nối
đang hỏng thì gửi thêm lệnh chỉ là chờ hết hạn); captcha vẫn tự bỏ bằng guard `_ch.CaptchaSeen` sẵn có.

### Kiểm chứng

`dotnet build ShopeeSuite.sln` **0 warning / 0 error**; `dotnet test orders/XuLyDonShopee.Tests`
**1682/1682 xanh** (thêm 14 test mới trong `TraHangKhongBoSotTests.cs`, trong đó 3 test chạy ĐẦU-CUỐI qua Web App
Apps Script giả). `node --check` sạch cho 4 file extension.

**THỬ PHÁ (4 lượt, đều đỏ đúng chỗ rồi khôi phục):**

| Phá | Test đỏ |
|---|---|
| Khôi phục `Take(SoDongMoiUocTinh)` | `SoKhongDoi…`, `SoGiam…`, `SoTang1_NhungTrangCo3DongMoi…`, `KhongDatDuocDiaChi…` |
| Tắt điều kiện gửi nhịp 2 | `LanDau_ConTrangSau_GuiLuotDocThem_VaGopDong` |
| Đánh dấu đã-đẩy cả khi `BoQua` | `ScriptBoQua…`, `ThieuTieuDeCot…` |
| Trả sớm khi `PickupFailedShop != null` | `KhongDatDuocDiaChi_VanGuiLenhCheckTraHang` |

**Chạy trong git worktree tách riêng** (`--detach HEAD` + chép đúng file của đợt này): một phiên Claude KHÁC đang
sửa dở `Database.cs` / `OrdersRepository*.cs` trong cùng cây làm test project không compile được, kết quả trên là
của riêng đợt này, không lẫn phần dở dang đó. Worktree đã dọn.

### Cần làm khi phát hành

1. **Nạp lại extension** — có lệnh cầu nối MỚI `readReturnRequestsMore`. Chưa nạp thì việc 1/3/4 vẫn chạy, riêng
   phân trang tự tắt (an toàn, không treo).
2. Không phải dán lại Apps Script — `boQua` / `canhBao` là hợp đồng script ĐÃ có, chỉ phía client trước nay
   không đọc.
3. Lượt chạy đầu sau khi cập nhật sẽ **nặng hơn** (mỗi shop lật tới 10 trang một lần). Từ lượt hai về 1 trang.

### Vòng PHẢN BIỆN — bản vá đầu bị bác một phần, đã sửa lại (09/08, chiều)

`nghiem-thu` chấm **ĐẠT 8/8** tiêu chí (tự chạy lại, tự thử phá 3 lượt). `phan-bien` **vẫn tìm ra 3 lỗi NẶNG** —
đúng cảnh "build xanh + test xanh + đúng plan mà vẫn hỏng" mà quy trình sinh ra để bắt:

1. **[NẶNG] Quét sâu không bao giờ chạm đúng nhóm shop cần nó.** `SoTrangCanDoc` suy độ sâu từ MỐC: chỉ quét
   sâu khi `mocCu is null`. Mà mốc được ghi ở cuối MỌI lượt check từ 29/07 và không migration nào reset ⇒ **mọi
   shop đang chạy đều có mốc ≠ null** ⇒ shop tồn 141/340 yêu cầu — đúng nhóm người dùng đang báo thiếu — không
   bao giờ được lật quá trang 1. Việc 2 coi như chưa tới đích.
   → **Sửa: BỎ HẲN `SoTrangCanDoc`.** Độ sâu nay do DỮ LIỆU quyết định: lật tiếp chừng nào trang vừa đọc còn ra
   mã MỚI so với kho `return_codes` (`ReturnCodesRepository.DemMaChuaBiet`, đếm SAU khi đã lọc cửa sổ ngày).
   Lật MỖI LẦN MỘT TRANG rồi hỏi lại. Lượt thường: trang đầu hết mã mới ⇒ 0 vòng lật, đúng bằng chi phí cũ.
   Shop tồn đọng: lật tới khi cạn. Không phụ thuộc mốc, tự chạy lại được nếu lượt trước gãy.
2. **[NẶNG] Mốc vẫn nhảy khi nhịp 2 thất bại** (`_saveReturnCount` chạy vô điều kiện) — lặp lại đúng cái bẫy cả
   đợt này đi vá: lật trượt/captcha/chạm trần một lần là mất vĩnh viễn.
   → **Sửa:** cờ `docDuSau`; lượt BIẾT là còn sót (không đổi được sắp xếp · lật trượt · chạm trần trang · chạm
   trần dòng) thì **giữ nguyên mốc** + log rõ.
3. **[NẶNG] `created_at` không được làm mới khi mã ĐỔI** ⇒ hạn thử lại 14 ngày (vừa thêm ở việc 3) giết chính
   cơ chế "yêu cầu bị tạo lại thì đẩy lại": cờ được mở ra rồi bản ghi rơi khỏi hàng đợi, không log, không badge.
   → **Sửa:** `DO UPDATE` set thêm `created_at = $now`.
4. **[TRUNG BÌNH]** Bom hẹn giờ: 4 test trong `MaTraHangDocLapTests` sẽ đỏ từ **12/08** (fixture ngày cứng
   `Luc = 29/07` + bộ lọc 14 ngày theo giờ THẬT). → truyền `Luc` vào cả 6 chỗ gọi.
5. **[TRUNG BÌNH]** Chẩn đoán pager bắn ngược: ca "selector sai" thì không bao giờ bắn (vì `coTrangSau=false`
   chặn nhịp 2 từ đầu), còn đường THÀNH CÔNG (lật hết trang) lại bắn kèm 4000 ký tự HTML mỗi shop mỗi vòng.
   → chỉ chẩn đoán khi `soTrangLat === 0`.
6. **[NHẸ]** Trần dòng khai "gộp mọi trang" nhưng mỗi lệnh tự đếm lại từ 0. → C# kẹp `TranDongMoiLuot` cho cả lượt.

Kèm một phát hiện của `nghiem-thu`: test `HangTran_KhopBanExtension` chỉ chốt phía C#, không chặn được hằng bên
JS trôi. → nay test **đọc thẳng `constants.js`** và so.

**Kiểm chứng sau vòng sửa:** build `ShopeeSuite.sln` 0 warning; `dotnet test` **1681/1681 xanh**; `node --check`
sạch. Thử phá 2 lượt riêng biệt: khôi phục điều kiện `mocCu is null` ⇒ `CoMocRoi_TrangDauConMaMoi_VanLatTrang`
đỏ; bỏ cờ `docDuSau` ⇒ `LatTrangTruot_KhongChotMoc` đỏ. Lớp test nay **16 test**.

### Còn lại, KHÔNG làm đợt này

- Khoá `return_codes` vẫn là `(account_id, order_sn)` ⇒ một đơn có HAI yêu cầu trả hàng chỉ giữ được MỘT mã.
  **User chốt 09/08: giữ mã MỚI NHẤT + ghi nhật ký** — đã làm, xem dưới. Không đổi khoá, không đổi layout sheet.
- Selector nút lật trang trên trang trả hàng vẫn **chưa xác nhận trên trang thật** — đang dùng chung bộ EDS pager
  của trang đơn. Lượt chạy thật đầu tiên sẽ lộ: nhật ký có dòng "không thấy nút 'trang sau'" kèm HTML khối phân
  trang thì ghim lại selector thật.

**Hai việc phản biện nêu mà đợt này CỐ Ý chưa làm** (cần quyết định của user):

*(Việc `canhBao` cấp phản hồi đã LÀM XONG — xem dưới.)*

### Bổ sung: hợp đồng trạng thái ghi mã trả hàng theo TỪNG DÒNG (09/08, tối)

**[TRUNG BÌNH — ĐÃ SỬA] `canhBao` là cấp PHẢN HỒI, `thieuCot` gom chung cả FILE PHỤ.** Hệ quả bản trước: file
phụ thiếu tiêu đề "Mã đơn trả hàng" ⇒ cả lô mã ĐÃ ghi xong ở file chính vẫn bị hạ `BoQua` ⇒ đẩy lại mỗi chu kỳ
suốt 14 ngày (đốt quota Apps Script, badge "⏳ Chờ đẩy" báo sai). Không mất dữ liệu, nhưng sai.

- **`Code.gs`**: thêm hàm `ghiMaTraHang` trả 4 kết cục cho ĐÚNG dòng đang xử —
  `'ghi'` / `'trung'` (ô đã đúng mã ⇒ coi như XONG) / `'thieucot'` / `'congthuc'`. Hai kết cục sau đặt
  `r.chuaGhiMaTra = true` + `r.lyDoChuaGhi`. **FILE PHỤ cố ý KHÔNG bao giờ đặt cờ này** — nó là bản sao, thiếu
  cột ở đó không có nghĩa file chính hỏng. Tách khỏi `ghiTruong`/`ghiDeNeuKhac` vì hai hàm đó chỉ trả
  true/false, mà `'trung'` và `'thieucot'` cùng ra `false` — gộp lại thì client không tài nào biết nên đánh dấu
  đã-đẩy hay giữ lại, và đó đúng là chỗ mã trả hàng bị nuốt.
- **Client**: `DocKetQua` đọc `chuaGhiMaTra` → `BoQua` theo từng dòng; **BỎ** luật `canhBaoLaBoQua` hạ cả lô
  (`canhBao` nay chỉ còn LOG).
- Test: `ThieuTieuDeCot_CoiNhuChuaDay` (đổi sang cờ theo dòng) + **đối chứng mới**
  `CanhBaoCapPhanHoi_NhungDongBaoGhiDuoc_ThiVanDanhDauDaDay` — có `canhBao` mà dòng không mang cờ ⇒ PHẢI đánh
  dấu đã đẩy. Thử phá 2 lượt (bỏ đọc `chuaGhiMaTra`; khôi phục luật hạ-cả-lô) — mỗi lượt đỏ đúng 1 test.

**⚠ CẦN THAO TÁC TAY:** `orders/gsheet-apps-script/Code.gs` chỉ là BẢN SAO tham chiếu. Phải dán lên
script.google.com rồi **Triển khai → Phiên bản mới** thì cờ mới có tác dụng. Chưa dán thì client không thấy cờ
⇒ quay về hành vi "script bảo ok thì tin là ok" — an toàn, không mất mã, chỉ là mất khả năng thử lại khi thiếu cột.

Sau bổ sung: build 0 warning, **1688/1688 xanh** (chạy lại 2 lượt đều sạch).
*(Việc "mã quá hạn không có log" đã LÀM XONG — xem dưới.)*

### Bổ sung sau khi user hỏi lại (09/08, tối)

**[TRUNG BÌNH — ĐÃ SỬA] Mã quá hạn 14 ngày rơi khỏi hàng đợi không có log nào.** Thêm
`ReturnCodesRepository.DemQuaHanThuLai` (đếm mã CHƯA đẩy được mà đã quá hạn — mã đã đẩy xong không tính) + một
dòng cảnh báo ở đầu `PushReturnCodesToGsheetAsync`. **Đếm TRƯỚC nhánh "hàng đợi rỗng"**: ca đáng lo nhất chính
là hàng đợi rỗng *vì mọi mã đều quá hạn* — nếu đếm sau thì đúng ca đó lại im lặng. Test
`DemQuaHanThuLai_DemDungNhomDaThoiThu`, đã thử phá (bỏ điều kiện `gsheet_synced_at IS NULL` ⇒ đỏ) rồi khôi phục.
Sau bổ sung: build 0 warning, **1682/1682 xanh**.

**[ĐÃ LÀM — user chốt] Đơn có NHIỀU HƠN MỘT yêu cầu trả hàng: giữ mã mới nhất + ghi nhật ký.** `GhepCap` vốn
dedupe theo mã đơn và bỏ dòng sau IM LẶNG — nhìn log không biết đơn nào có hai mã. Nay `KetQuaGhepTraHang` có
thêm `TrungMaDon`, `ShopFlowRunner` log số đơn dính + tối đa 3 dòng chi tiết dạng
`260731AAAAAA: giữ <mã mới> (mới nhất), BỎ <mã cũ>`. Chỉ báo khi dòng bị bỏ THẬT SỰ đọc được mã yêu cầu (dòng
trùng mà không có mã thì không mất gì). Luật GIỮ nguyên như cũ — chỉ thôi im lặng.

*Vì sao không chứa cả hai mã:* ca hay gặp là yêu cầu bị TẠO LẠI sau khi bị hủy/từ chối, lúc đó mã cũ đã chết nên
giữ mã mới đúng là cái cần. Ca hai yêu cầu CÙNG SỐNG thì cột "Mã đơn trả hàng" trên Google Sheet chỉ có MỘT ô
mỗi dòng đơn — chứa mã thứ hai đòi đổi layout sheet, là quyết định của user chứ không phải của code. Số trong
nhật ký chính là dữ liệu để sau này quyết: nó lớn bất thường thì mới đáng bàn cách chứa.

Test `MotDonHaiYeuCau_GiuMaMoiNhat_VaGhiNhatKy` (assert cả mã được giữ LẪN nội dung dòng log), đã thử phá (vô
hiệu hoá `trung.Add` ⇒ đỏ) rồi khôi phục. Sau bổ sung: build 0 warning, **1683/1683 xanh**.
