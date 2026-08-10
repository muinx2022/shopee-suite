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

---

## HẬU KIỂM 09/08 (sau khi phát hành 1.8.6): bản này làm CHẾT cầu nối extension

Người dùng cập nhật lên 1.8.6 thì **toàn bộ flow check đơn đứng**, nhật ký lặp đúng một câu cho mọi tài khoản,
cả lượt bản sạch lẫn lượt sau khi đăng nhập Playwright:

```
Chờ extension nối cầu (ready) — tối đa 45s...
Vòng cầu nối chưa trọn: Extension không nối cầu trong 45s (bản chép extension hỏng? trình duyệt chặn?).
```

**Nguyên nhân: lỗi CÚ PHÁP JavaScript** trong `extensions/shopee-orders/flow-returns.js` (đúng file của đợt này).
Khi đổi `traVe` sang `Object.assign(...)` để nhét thêm `soTrangDaDoc`/`coTrangSau`, object truyền cho `send(...)`
mất dấu `}` đóng: `}, them || {})));` — thừa một `)`, thiếu một `}`.

Đường lây: `background.js` `import { ... } from "./flow-returns.js"` (tĩnh) ⇒ service worker MV3 **chết ngay lúc
nạp** ⇒ `bridge.connect()` ở cuối `background.js` KHÔNG BAO GIỜ chạy ⇒ không có `onOpen` ⇒ không có `ready` ⇒
C# chờ hết 45s. Không liên quan tới việc đảo thứ tự bản-sạch-trước (plan `2026-08-07-...`): thứ tự nào cũng chết,
chỉ là bản 1.8.6 đảo thứ tự nên câu lỗi hiện ra ở đầu vòng thay vì giữa vòng.

**Vì sao 1688 test không đỏ:** không có một test nào chạm tới JavaScript. Toàn bộ `extensions/` nằm ngoài mọi
lưới — build C# không đọc nó, `dotnet test` không đọc nó, và nó chỉ được nạp lúc chạy thật trên máy người dùng.

### Đã sửa (09/08, sau phát hành)

1. `extensions/shopee-orders/flow-returns.js`: đóng lại object của `send(...)` (`}, them || {})),` + `});`).
2. Lưới mới `suite/Shopee.Core.Tests/ExtensionJsCuPhapTests.cs` — chạy trong `dotnet test`, canh **cả ba**
   extension (`shopee-orders`, `shopee-scrape`, `shopee-search`, đều được `Shopee.Suite.csproj` chép vào bản
   phát hành):
   - `MoiFileJs_PhaiParseDuocLaEsModule` — parse từng file bằng Acornima (parser JS thuần .NET, không cần Node).
   - `MoiImportTuongDoi_PhaiTroToiFileCoThat` — chặn lại lỗi kiểu "quên chép `shared/`" ngay từ trong repo.
   - `MoiImportTenGoi_PhaiCoExportTuongUng` — import một tên không được export cũng giết SW y hệt (lỗi *link*
     module, không phải lỗi cú pháp), nên phải có lưới riêng.
   - Cả ba đều có trần `SoFileJsToiThieu = 20`: quét ra ít hơn 20 file thì FAIL, để đường dẫn repo hỏng không
     biến test thành xanh giả.
   - **Thử phá đủ ba:** trả lại dấu ngoặc thừa ⇒ test 1 đỏ; đổi `"./shared/util.js"` thành tên không có ⇒ test 2
     đỏ; đổi tên hàm `pageFindNextPage` khi export ⇒ test 3 đỏ. Khôi phục ⇒ xanh lại.

Kiểm chứng sau sửa: `dotnet build ShopeeSuite.sln` **0 warning / 0 error**; orders **1688/1688**; hub **120/120**;
core **114/114** (trước: 111 — cộng đúng 3 test mới).

### Bài học ghi lại

Extension là **mã chạy thật trên máy người dùng nhưng nằm ngoài toàn bộ lưới của repo**. Sửa `extensions/**` thì
số test xanh KHÔNG nói lên điều gì cả — trước đợt này, cách duy nhất biết extension còn sống là chạy tay một
tài khoản. Từ nay `ExtensionJsCuPhapTests` gánh phần "còn nạp được"; phần *logic* của extension thì vẫn chỉ có
chạy thật mới biết, nên **đổi extension là bắt buộc chạy thử một tài khoản trước khi phát hành**.

---

## Đợt 10/08 (sáng): ba lỗi nữa lộ ra khi chạy thật

Sau khi vá lỗi cú pháp, chạy thật lộ tiếp ba thứ — cả ba đều KHÔNG phải do đợt 09/08 gây ra, chỉ là trước đó
cầu nối chết nên không ai đi tới được các bước này.

### 1. Chrome ≥ 151 KHÔNG còn nạp extension qua `--load-extension`

Đo bằng cách mở đúng bộ tham số của `BraveLaunchArgs.BuildCleanPocArgs` rồi ngồi nghe cổng:

| Trình duyệt | Bản | Kết quả |
|---|---|---|
| Brave | 151.1.93.134 | nạp được, `{"action":"ready"}` sau vài giây |
| Edge | 151.0.4129.72 | nạp được, `ready` |
| **Chrome** | **151.0.7922.109** | **KHÔNG nạp** — 40s im lặng, hồ sơ không ghi nhận extension nào |

Cờ `--disable-features=DisableLoadExtensionCommandLineSwitch` (thêm hồi Chrome 137) nay VÔ TÁC DỤNG trên Chrome:
Google đã bỏ hẳn lối thoát đó. Brave/Edge còn giữ. Hồ sơ tài khoản là `profiles/1-chrome` ⇒ phải đổi Cài đặt →
Trình duyệt sang Brave/Edge. **Chưa có chốt chặn trong code** — app vẫn để chọn Chrome rồi chết câm 45s với câu
lỗi sai thủ phạm ("bản chép extension hỏng?"). Việc còn treo.

### 2. Modal "Điều khoản - Điều kiện" chắn trang, bộ dọn modal KHÔNG thấy nó

`pageLocateBlockingModalButton` bỏ qua nút `disabled` (dòng `if (el.disabled) continue`), mà modal TosModal khoá
nút "Đồng ý" cho tới khi tick ô "Tôi xác nhận đã đọc..." và KHÔNG có nút ✕ ⇒ hàm trả `null` ⇒ `dongModalChan`
tưởng trang sạch ⇒ modal nằm lì nuốt mọi trusted click. Đây là gốc của cả "Lỗi địa chỉ" LẪN "trang trả hàng
chưa render ô tổng sau 20s" (cú bấm tab trả hàng bị nuốt, trang đứng nguyên ở `/portal/sale/order`).

Đã sửa:

- `pageLocateBlockingModalCheckbox` — tìm ô tick bắt buộc, **chỉ** trong modal đang có nút xác nhận BỊ KHOÁ
  (modal nào nút đã bấm được thì không tự tick hộ ô nào của nó).
- Tách `dongModalChan` sang `flow-modal.js` (dùng chung) + gọi thêm ở đầu `doReadReturnRequests`.
- Bước mở trang trả hàng có ĐƯỜNG LUI: bấm tab xong mà URL không sang `returnrefundcancel` trong 6s → điều
  hướng thẳng (trước đây bấm xong là tin luôn, rồi ngồi hết 20s trên đúng trang cũ).
- `pageConLopPhuChan` + `choHetLopPhu`: sau khi đóng modal, CHỜ lớp mask tan thật sự (≤3s) thay vì ngủ 900ms.
  EDS gỡ hộp modal trước, mask mờ dần sau — bấm trong khoảng đó là trượt im lặng.

Kiểm chứng: dựng lại ĐÚNG khối HTML người dùng gửi rồi chạy hai hàm trong trình duyệt thật — trước khi tick,
`pageLocateBlockingModalButton` trả `null` (đúng lỗ hổng); `pageLocateBlockingModalCheckbox` trả toạ độ ô tick;
sau khi tick, hàm ô tick trả `null` và hàm nút trả `dong y`. Bốn ca ngược cũng đúng: modal có nút mở sẵn →
KHÔNG tick hộ; modal "Sửa Địa chỉ" của flow → loại trừ; modal khoá mà không có ô tick → null; modal ẩn → bỏ qua.
**Lượt chạy thật 06:24:56 đã xác nhận**: "đã tick 1 ô xác nhận để mở khoá nút" → "đã đóng modal chắn bằng nút dong y".

### 3. Service worker chết trong lúc nghỉ giữa hai shop

`06:08:41 [Shop 2/12] Cầu nối lỗi: Cầu nối extension chưa kết nối (WebSocket chưa mở)` — shop 1 xong, nghỉ ~3
phút, shop 2 chết ngay lệnh đầu. Service worker MV3 bị trình duyệt giết sau ~30s không hoạt động; chết rồi thì
`scheduleReconnect` của ws-bridge chết theo, không ai đánh thức lại được (content.js chỉ gửi `wake` lúc trang load).

Đã sửa: `OrdersBridgeChannel` bắn gói `{action:"ping"}` mỗi `NhipGiuSong` = **20s** (Chromium tính hoạt động
WebSocket là hoạt động của service worker). Extension bỏ qua action lạ nên gói này không sinh phản hồi, không
đụng chặng nào đang chờ. Timer tắt trong `Dispose()` TRƯỚC khi bỏ server.

Test `CauNoiGiuSongTests` (2 test, dùng `BridgeTestRig` với nhịp rút gọn 200ms): ping bắn đều khi không ai gửi
lệnh; ping KHÔNG hoàn tất/không fault chặng đang chờ. **Thử phá:** tắt timer ⇒ 2 test đỏ; khôi phục ⇒ xanh.
Đã BỎ test thứ ba ("Dispose tắt timer") vì không quan sát được tất định — nó đỏ ngẫu nhiên khi chạy song song.

### Kiểm chứng sau cả ba

`dotnet build ShopeeSuite.sln` 0 warning / 0 error · orders **1690/1690** · core **114/114** · hub **120/120**.

### 4. Khối chắn KHÔNG chỉ là modal: tour hướng dẫn `.on-boarding`

Người dùng gửi HTML lúc 10/08: sau khi đóng modal điều khoản, trang trả hàng bật **tour hướng dẫn** — `.on-boarding`
+ `.on-boarding-highlight` (lớp phủ có ô khoét) + `.eds-popover` với nút "Đã hiểu". Nó KHÔNG có `.eds-modal__box`
nào nên `pageLocateBlockingModalButton` (chỉ quét `.eds-modal__box`) mù hoàn toàn với nó. Đây mới là thứ nuốt hai
cú bấm lúc 06:24:58 và 06:25:04, không phải mask còn sót của modal.

Đã sửa:

- Quét thêm `.on-boarding` trong `pageLocateBlockingModalButton`. Khung tour thường có rect 0×0 (mọi con định vị
  absolute) nên BỎ điều kiện "khung phải có kích thước" cho riêng loại này — chốt chặn hiển thị vẫn còn ở rect
  của từng NÚT.
- `pageConLopPhuChan` nhận thêm `on-boarding|onboarding` (trước chỉ bắt `eds-modal|mask|overlay|backdrop`, tức là
  vẫn sẽ báo "trang thông thoáng" trước một cái tour đang phủ kín).
- `TRAN_MODAL_CHAN` 3 → 6: tour đi NHIỀU BƯỚC, mỗi bước một nút "Đã hiểu".

Kiểm chứng trên đúng khối HTML người dùng gửi (trình duyệt thật): tìm được nút `da hieu` của tour; tại điểm sắp
bấm báo đúng `div.on-boarding-highlight`; KHÔNG tự tick hộ ô nào (tour không có ô tick); đóng xong thì sạch.

### Việc CÒN TREO sau đợt này

1. **Chưa chặn Chrome trong code** — chọn Chrome cho cầu nối vẫn chết câm 45s với câu lỗi sai thủ phạm.
2. **Sync mật khẩu client → Hub** (user yêu cầu 10/08, đã chốt: *vá ô trống, không đè* + *lưu dạng thường*) —
   chưa bắt đầu. Chạm `OrdersAccountItem`/`OrdersDirectoryAccount`, bảng `orders_accounts` (3 cột + migration),
   `BuildOrdersMirror`, `HubDirectoryPuller`; kèm redeploy Hub.
3. Chưa commit, chưa bump version — máy khác vẫn ôm 1.8.6 hỏng.

### 5. Locator tab "Đơn Trả hàng Hoàn tiền" trượt vì Shopee đổi giao diện

Vòng 06:37 (đã có bản vá modal + tour): KHÔNG còn dòng modal/tour nào, nhưng vẫn "KHÔNG xác nhận được tab 'Đơn
Trả hàng Hoàn tiền' đang chọn" — và in ra **cùng giây** với dòng đọc đơn trước đó. Cùng giây = KHÔNG có cú bấm
nào: nhánh có bấm phải tốn ≥8s chờ. Tức `pageLocateReturnCaseTab` trả `null` ngay.

Hàm đó đòi ĐỦ CẢ HAI: khung `.return-case-tab-wrapper` **và** text khớp đúng `"don tra hang hoan tien"`. Chính
cái tour vừa bật là thông báo Shopee **đổi giao diện đúng khu này**, và nhãn trong tour viết
"Đơn Trả hàng/**Hoàn tiền**" — có dấu `/` ⇒ `_na` cho `don tra hang/hoan tien` ⇒ không khớp chuỗi cũ.

Đã sửa:

- `RETURN_TAB_RE` nới thành `don tra hang\s*[\/|.,-]?\s*hoan tien` — chịu được dấu ngăn, VẪN khớp nhãn cũ.
- `pageLocateReturnCaseTab` chạy HAI VÒNG: vòng 1 khung `.return-case-tab-wrapper` (giữ hành vi đã kiểm chứng),
  vòng 2 quét toàn trang khi khung đó không còn. Nhận "đang chọn" qua CẢ `class active` LẪN `aria-selected`.
  Quét toàn trang KHÔNG lỏng tay vì regex bắt buộc bắt đầu bằng "don tra hang…" — tab điều hướng trái
  "Trả hàng/Hoàn tiền/Hủy" (`tra hang/hoan tien/huy`) không thể lọt.
- `pageChanDoanTabTraHang`: khi vẫn trả null thì LIỆT KÊ mọi phần tử trông như tab (text + class) kèm
  `wrapper=co/KHONG` vào câu báo. Không có nó thì mỗi lần Shopee đổi giao diện là một vòng đoán mò.

Kiểm chứng (trình duyệt thật, 8 ca): giao diện MỚI (không wrapper, nhãn có `/`) → tìm đúng tab và **toạ độ rơi
đúng vào "Đơn Trả hàng/Hoàn tiền"**, không dính tab điều hướng trái; `aria-selected=true` → `daDung`; giao diện
CŨ (wrapper + `class active`) vẫn `daDung`, chưa chọn thì trả toạ độ; không tab nào khớp → `null` + chẩn đoán
vẫn liệt kê được. Regex kiểm riêng 9 ca nhãn (5 khớp / 4 không) — nạp thẳng hằng từ `constants.js`.

⚠ CHƯA chứng minh được là đủ: chưa ai nhìn thấy markup THẬT của giao diện mới. Nếu Shopee bỏ luôn
`.eds-tabs__nav-tab`/`[role=tab]` thì vòng 2 vẫn trượt — nhưng lúc đó câu chẩn đoán sẽ nói thẳng ra.

### Kết quả chạy thật sau bản vá locator (vòng 06:45–06:46)

```
06:46:17 Check đơn trả hàng [alina99.store]: 0 yêu cầu — LẦN ĐẦU của shop này, quét sâu rồi ghi mốc.
```

**Bước check trả hàng chạy TRỌN lần đầu tiên trong cả đợt.** Không còn "KHÔNG xác nhận được tab", không còn
"KHÔNG đổi được sắp xếp", và C# ghi mốc thay vì bỏ lượt. Con số cũng đổi đúng bản chất: 33 trước đây là của tab
"Tất cả" (gộp Đơn Hủy / Giao không thành công), nay **0** là số THẬT của tab "Đơn Trả hàng/Hoàn tiền".

Người dùng gửi kèm markup dải tab L1 (`Tất cả / Chờ xác nhận / Chờ lấy hàng / Đang giao / Đã giao /
Trả hàng/Hoàn tiền/Hủy`) — xác nhận `[data-testid='l1-tab-return_refund_cancel']` mà `pageLocateReturnTab` dùng
vẫn còn nguyên, tức bước vào trang trả hàng chưa bao giờ là thủ phạm. Cũng xác nhận vòng-2 quét toàn trang KHÔNG
bấm nhầm: `_na("Trả hàng/Hoàn tiền/Hủy")` = `tra hang/hoan tien/huy`, không khớp regex bắt buộc có "don".

Chuỗi bốn lỗi chồng nhau của đợt này, theo đúng thứ tự phải gỡ: **cú pháp JS** (SW chết, không có cầu nối) →
**Chrome bỏ `--load-extension`** (đổi sang Brave) → **modal điều khoản + tour onboarding** (nuốt trusted click) →
**locator tab L2 lỗi thời** (Shopee đổi nhãn/khung). Mỗi lỗi che lỗi sau nó, nên chỉ lộ ra từng cái một qua
từng vòng chạy thật.

### 6. Khung tab L2 còn, nhưng markup bên trong đã đổi (vòng 06:50)

Chẩn đoán thêm ở mục 5 trả về đúng thứ cần:

```
Tab thấy trên trang: wrapper=co · KHONG co phan tu nao trong nhu tab
```

Khung `.return-case-tab-wrapper` **CÒN**, nhưng trong đó không có phần tử nào là `.eds-tabs__nav-tab` hay
`[role=tab]` ⇒ vòng 1 (khung + class tab) và vòng 2 (toàn trang + class tab) đều trượt. Kèm mốc giờ: modal đóng
lúc 06:50:42, kết luận lúc 06:50:43 — **1 giây**, tức cũng có thể là nhìn quá sớm khi Vue chưa vẽ lại dải tab.

Xử CẢ HAI khả năng:

- **VÒNG 3** trong `pageLocateReturnCaseTab`: dò theo TEXT bên trong `.return-case-tab-wrapper`, bỏ hẳn ràng
  buộc class. Khung là ranh giới an toàn (chỉ chứa mấy tab loại đơn) nên không có nguy cơ bấm lạc như dò toàn
  trang. Lấy phần tử KHỚP SÂU NHẤT (không con nào cũng khớp) để không bấm vào thẻ bọc; "đang chọn" nhận ở chính
  nó HOẶC leo tối đa 4 cấp cha (dải EDS gắn `active` lên thẻ bọc).
- **CHỜ** `CHO_TAB_LOAI_DON_MS = 6000`: dò lại mỗi 500ms thay vì dò đúng một lần.
- Chẩn đoán nay đổ luôn **HTML rút gọn của chính khung** (bỏ img/svg, trần `MAX_RETURN_HEAD_HTML`) — lượt sau
  nếu vẫn trượt thì biết ngay markup thật, khỏi đoán vòng nữa.

Kiểm chứng (trình duyệt thật): markup MỚI kiểu `div.case-tab-item > span.lbl` → tìm được, **toạ độ rơi đúng vào
"Đơn Trả hàng/Hoàn tiền"**; `active` ở thẻ CHA → `daDung`; `aria-selected` ở chính phần tử → `daDung`; khung có
mà không mục nào khớp → `null` + chẩn đoán đổ HTML khung. Chạy lại toàn bộ ca CŨ sau khi refactor: wrapper +
`.eds-tabs__nav-tab` + `active` → `daDung`; chưa chọn → toạ độ đúng; giao diện mới không wrapper → vẫn không
bấm nhầm tab L1 "Trả hàng/Hoàn tiền/Hủy"; trang trống → `null`.

### 7. Nhịp ping 20s KHÔNG cứu được service worker — phải HỒI SINH, không phải GIỮ SỐNG

Vòng 06:50 chạy với bản đã có nhịp ping 20s, vậy mà shop 3 (sau 4 phút nghỉ) vẫn:

```
06:55:30 [Shop 3/12] vtinho.store — mở Chi tiết...
06:55:30 Cầu nối lỗi: Cầu nối extension chưa kết nối (WebSocket chưa mở) — không gửi được lệnh.
```

Tức giả định "Chromium tính hoạt động WebSocket vào hạn nhàn rỗi của service worker" KHÔNG đúng ở đây (hoặc
không đủ). Ghi nhận là **đã bác bỏ bằng lượt chạy thật**, đừng dựng lại giả định đó.

Đổi hướng: thôi cố GIỮ service worker sống, chuyển sang **DỰNG NÓ DẬY** rồi để nó tự nối lại.

- `manifest.json` thêm quyền `alarms`; `background.js` tạo alarm `hoi-sinh-cau-noi` nhịp 0.5 phút, handler gọi
  lại đúng khối `chrome.storage.session.get → bridge.connect` vốn đã chạy sẵn ở top-level. Mấu chốt: **alarm
  sống độc lập với service worker** — tới hẹn trình duyệt tự dựng service worker dậy, và chỉ riêng việc dựng
  dậy đã đủ nối lại cầu. `bridge.connect` tự bỏ qua khi socket còn sống nên gọi thừa vô hại.
  (Extension nạp bằng `--load-extension` nên được phép nhịp dưới 1 phút; bị kẹp lên 1 phút cũng vẫn ổn.)
- `OrdersBridgeChannel.SendAsync` thêm `ChoNoiLai = 90s`: socket đang đứt thì **CHỜ** extension nối lại rồi mới
  gửi, thay vì ném ngay. 90s ôm trọn một nhịp alarm kể cả khi bị kẹp lên 1 phút. Hết hạn vẫn chưa có thì ném
  đúng lỗi cũ (KHÔNG nuốt im lặng). Có log lúc bắt đầu chờ và lúc nối lại được — trước đây đứt là im re.
- `GuiGiuSong` nay gửi THẲNG qua `_ws` và bỏ qua khi chưa nối: nếu đi qua `SendAsync` thì mỗi nhịp ping sẽ ngồi
  chờ 90s và xếp hàng chồng nhau. Giữ ping lại vì rẻ, nhưng xmldoc ghi rõ nó KHÔNG phải lưới an toàn.

Test `CauNoiGiuSongTests` +2 (rig có `NgatKetNoiAsync`/`NoiLaiAsync` mô phỏng service worker chết rồi sống lại):
`SendAsync_ChoExtensionNoiLai_ThayViNemNgay` (đang đứt → lệnh KHÔNG hoàn tất ngay; nối lại → lệnh đi bình thường)
và `SendAsync_HetHanChoNoiLai_ThiVanNem`. **Thử phá:** vô hiệu nhánh chờ ⇒ test thứ nhất đỏ; khôi phục ⇒ xanh.
Test thứ hai KHÔNG đỏ khi phá (bỏ chờ thì vẫn ném) — nó canh chuyện khác: hết hạn không được nuốt lỗi.

Kiểm chứng: build `ShopeeSuite.sln` 0 warning / 0 error · orders **1692/1692** · core **114/114**.

### 8. DƯƠNG TÍNH GIẢ do chính vòng 3 — hại hơn trượt hẳn (vòng 07:08)

```
07:08:32 Check đơn trả hàng [alina99.store]: 33 yêu cầu — TĂNG 33 so với mốc 0.
07:08:32 đọc 33 dòng → 0 cặp đủ hai mã, 0 dòng THIẾU mã yêu cầu, bỏ 33 dòng vì href là ĐƠN HỦY.
```

KHÔNG có cảnh báo nào — tức `tabTraHang == true`, code tưởng đã đứng đúng tab. Nhưng 33 là số của tab "Tất cả"
(vòng 06:46 chọn đúng tab thì ra **0**), và 33/33 dòng đều là ĐƠN HỦY. Dương tính giả.

Gốc: vòng 3 nhận "đang chọn" bằng cách **leo mù 4 cấp cha** tìm class `active`. Dải bọc chứa CẢ 4 tab cũng mang
class `active` ⇒ leo tới đó là tab NÀO cũng được coi là đang chọn ⇒ không bấm, không cảnh báo, đọc số tab "Tất
cả" rồi **ghi thẳng vào mốc**. Đây là kiểu hỏng tệ nhất trong cả đợt: trượt hẳn thì còn bỏ lượt và GIỮ mốc, còn
cái này ghi số sai vào mốc — nuốt vĩnh viễn mọi yêu cầu trả hàng mới của shop đó.

Sửa: chỉ leo khi cha **vẫn chỉ chứa đúng tab này** (`_na(cha.textContent) === _na(ô tab)`); text đổi = đã trèo ra
khỏi ô tab, dừng ngay. Kèm `dangChon` trong kết quả và MỘT dòng nhật ký đối chứng mỗi shop
(`tab loại đơn đang chọn: "..."`) — dương tính giả không đi lọt im lặng được nữa.

Kiểm chứng (trình duyệt thật, dựng đúng bẫy: dải bọc mang `active`, tab đang chọn thật là "Tất cả"):
`daDung:false` + toạ độ trỏ đúng "Đơn Trả hàng/Hoàn tiền" (tức sẽ BẤM chứ không tưởng bở); chọn đúng tab trả hàng
⇒ `daDung:true, dangChon:"don tra hang/hoan tien"`; đang ở "Đơn Hủy" ⇒ vẫn `daDung:false` + toạ độ. Ca cũ chạy
lại đủ: wrapper + `.eds-tabs__nav-tab` + `active` ⇒ `daDung` kèm `dangChon`; chưa chọn ⇒ toạ độ; giao diện mới
không wrapper ⇒ không bấm nhầm tab L1; không khớp ⇒ `null` + chẩn đoán đổ HTML khung.

### 9. Tab bị DISCARD trong lúc nghỉ — và bằng chứng `chrome.alarms` ĐÃ chạy đúng (vòng 08:43)

Chẩn đoán ở mục 8 trả lời dứt điểm:

```
tab=662819876 url=https://banhang.shopee.vn/portal/shop · func=pageScrollDetailIntoView
```

URL **NẰM TRONG** `host_permissions` ⇒ câu báo của Chrome ("must request permission to access the respective
host") **SAI THỦ PHẠM**. Thật ra tab đã bị trình duyệt **DISCARD** trong 4 phút nghỉ: tab discarded giữ nguyên
URL (nên `chrome.tabs.get` vẫn đọc ra) nhưng mất renderer, `chrome.scripting.executeScript` bơm vào là ném đúng
câu đó. Đi tìm theo câu báo là đi sai hướng — mất đúng một vòng vì thế.

**Đồng thời vòng này chứng minh mục 7 ĐÚNG:** không còn "WebSocket chưa mở". Sau 4 phút nghỉ, lệnh mở Chi tiết
shop 2 GỬI ĐI ĐƯỢC ⇒ `chrome.alarms` dựng service worker dậy và cầu nối sống qua lúc nghỉ. Ta không chết ở đó
nữa mà đi tiếp tới lỗi kế — đúng kiểu bóc từng lớp của cả đợt này.

Cũng vòng này, mục 8 được xác nhận: `extension: tab loại đơn đang chọn: "don tra hang hoan tien"` — chốt text
đã chặn dương tính giả, và dòng đối chứng làm đúng việc của nó.

Sửa hai tầng:

1. **Chặn từ gốc** — `BraveLaunchArgs.ChanVutTabKhoiBoNho` thêm vào `--disable-features` của đường mở sạch:
   `HighEfficiencyModeAvailable, BatterySaverModeAvailable, PerformanceControlsPerformanceInterventions,
   FreezingOnEnergySaver, ModernDiscardStrategy`. Test `ChanVutTabKhoiBoNho_CoTrongDisableFeatures`; **thử phá**
   (bỏ nhóm cờ khỏi call-site) ⇒ đỏ ⇒ khôi phục ⇒ xanh.
2. **Lưới hai bên extension** — `execInTab` bắt lỗi, nếu `tab.discarded === true` thì `chrome.tabs.reload` +
   chờ `status === "complete"` (trần 20s) rồi **thử lại đúng một lần**. Hỏng tiếp mới ném, và câu ném nay kèm
   `url= status= discarded=` để lần sau khỏi đoán.

Kiểm chứng: build `ShopeeSuite.sln` 0 warning / 0 error · orders **1693/1693** · core **114/114**.

## 10. Cầu nối chết trong kỳ nghỉ giữa hai shop — `chrome.alarms` KHÔNG cứu được (10/08, vòng 09:44)

Hai vòng liên tiếp chết cùng một kiểu, chưa vòng nào đi hết 12 shop:

| Vòng | Chết ở | Dòng |
|---|---|---|
| 09:11 | shop 5/12 | `Cầu nối: hết thời gian chờ phản hồi từ extension` |
| 09:44 | shop 6/12 | `Cầu nối rớt — chờ extension nối lại tối đa 90s...` → `Extension KHÔNG nối lại trong hạn chờ.` |

⇒ **`chrome.alarms` 30s thêm sáng nay là đường CHẾT.** Socket rớt trong kỳ nghỉ ~10:08, C# gọi lệnh lúc 10:11:34
rồi chờ thêm 90s: hơn 3 phút mà service worker không hề dậy nối lại. Đừng tin vào nó nữa (đúng như đã từng
phải kết luận với gói ping 20s ở vòng 06:50).

**Vá:** nhịp đánh thức 20s từ **content script** (`content.js`), giữ `chrome.alarms` làm lưới thứ hai. Message
từ content script khác hẳn hai đường kia ở chỗ nó vừa GIA HẠN đồng hồ idle của SW đang sống, vừa DỰNG DẬY SW đã
chết; tab picker `/portal/shop` mở suốt kỳ nghỉ nên luôn có người bắn nhịp. Kèm theo: `background.js` chỉ nhận
vai `listTabId` từ gói `dauTien` (lượt đầu mỗi lần load trang) — nhịp lặp là keepalive thuần, không được để tab
shop cướp vai tab picker giữa chừng.

## 11. Sắp xếp "Ngày yêu cầu (Mới - Cũ)" — không phải sai selector, mà bấm trượt

Chẩn đoán bắt được 2 lần ở vòng 09:44, giống hệt nhau:

```
Chẩn đoán: sort-button=co · nut hien · menu tong=2 hien=0 · KHONG co muc nao trong menu dang hien
```

Nút CÓ và ĐANG HIỆN, menu có trong DOM nhưng không cái nào mở ⇒ loại hẳn giả thuyết "sai selector"
(`SORT_NEWEST_RE` cũng đã kiểm khớp đúng nhãn thật). Manh mối quyết định: lỗi **không cố định** — vòng 09:11
alina99 + shop9x đều lỗi, vòng 09:44 hai shop đó lại sắp xếp được. Đua thời gian.

Thủ phạm nhiều khả năng nhất: `pageLocateSortButton` gọi `scrollIntoView({block:"center"})` rồi đo rect NGAY —
mà `behavior` mặc định theo CSS `scroll-behavior`, trang cuộn mượt thì toạ độ đo được là toạ độ CŨ, cú bấm rơi
xuống chỗ trống. Khớp với việc nút TAB (sát đỉnh trang, không phải cuộn) thì bấm được còn nút sắp xếp (phải
cuộn xuống) thì không.

**Vá:** `behavior: "instant"` + gọi hai lượt (lượt đầu chỉ để cuộn, nghỉ 400ms cho layout lắng, lượt hai mới đo
toạ độ thật) + thử bấm tối đa 2 lượt (`SO_LAN_MO_SAP_XEP`), mỗi lượt chờ menu 4s. Chẩn đoán được nới: mỗi menu
ẨN nay khai luôn `display/visibility/opacity`, lớp của nó và của cha, và có chứa mục "ngay yeu cau" hay không —
đủ để chốt ngay ở lượt sau nếu vẫn hỏng.

## 12. THỦ PHẠM THẬT: nhóm cờ chặn vứt tab làm TRÌNH DUYỆT TỰ CHẾT — đã gỡ (10/08, vòng 11:31)

Mục 10 chẩn đoán sai. "Cầu nối chết trong kỳ nghỉ" chỉ là **hệ quả**: chính trình duyệt sạch tự kết liễu.

Hai dòng mốc mới (`Cầu nối: extension ĐỨT/NỐI`) + dòng theo dõi tiến trình mới cho bức tranh đầy đủ:

| Vòng | Giây phase-lock | Chu kỳ đứt | Cú chết | Trình duyệt sống được |
|---|---|---|---|---|
| 11:00 | `:46` | đúng 240s | cú thứ **6** | 22m47s |
| 11:31 | `:38` | đúng 240s | cú thứ **6** | 23m29s |

Giây phase-lock ĐỔI theo vòng ⇒ nhịp neo vào lúc khởi động vòng, không phải đồng hồ hệ thống. Năm cú đầu là
service worker bị giết rồi tự dựng lại (1–13s, cổng bền `runtime.connect` lo) — **chấp nhận được**. Cú thứ sáu
thì khác hẳn:

```
11:54:38 ⚠ Trình duyệt sạch (PID 22024) đã THOÁT lúc 11:54:38 — mã thoát -2147483645 (0x80000003).
```

`0x80000003` = **STATUS_BREAKPOINT** = Chromium tự kết liễu vì một `CHECK` thất bại. KHÔNG phải hết RAM (máy còn
15,7 GB), KHÔNG phải bị kill từ ngoài (`0x40010004`), KHÔNG phải thoát êm (`0`). Kiểm tay lúc 11:27 vòng trước:
`tasklist` không còn tiến trình `brave.exe` nào, hồ sơ `1-brave` ghi lần cuối đúng giây cầu nối đứt.

**Thủ phạm là nhóm cờ ở mục 9 do chính đợt này thêm vào sáng 10/08** — `HighEfficiencyModeAvailable,
BatterySaverModeAvailable, PerformanceControlsPerformanceInterventions, FreezingOnEnergySaver,
ModernDiscardStrategy`. Bằng chứng gián tiếp nhưng chặt: trình duyệt luôn chết TRONG kỳ nghỉ — đúng lúc bộ máy
đóng băng/vứt tab chạy — và triệu chứng xuất hiện đúng từ vòng đầu tiên sau khi thêm cờ; trước đó chưa vòng nào
chết kiểu này. Tắt nửa vời bộ máy tiết kiệm tài nguyên để lại đúng những đường mã Chromium không lường tới.

**Đã gỡ hẳn nhóm cờ.** Test cũ `ChanVutTabKhoiBoNho_CoTrongDisableFeatures` được **đảo ngược** thành
`KhongChanVutTabKhoiBoNho_VonLamTrinhDuyetTuChet` (giữ lại ở dạng đảo để lần sau gặp lại lỗi vứt tab thì không
đi lại vết xe cũ); **thử phá** bằng cách thêm lại `ModernDiscardStrategy` ⇒ đỏ ⇒ khôi phục ⇒ xanh. Lỗi vứt tab
nguyên bản vẫn còn lưới bên extension (nạp lại tab discarded/unloaded rồi thử lại một lượt), và ba vòng gần nhất
không tái phát lần nào (`discarded=` 0 lần).

**Bài học ghi lại:** cả buổi sáng đổ lỗi cho service worker MV3 vì câu log tự viết
`"(service worker ngủ trong lúc nghỉ?)"` — một lời ĐOÁN nằm trong nhật ký, đọc mãi thành ra sự thật. Đã bỏ câu
đoán đó; nhật ký nay chỉ ghi SỰ KIỆN (đứt/nối lúc mấy giờ, tiến trình thoát mã gì) và để người đọc tự kết luận.

### Đường mất dữ liệu còn treo: lệnh đang bay bị nuốt khi cầu nối chớp tắt

Vòng 11:00 và 11:31 đều dính, cùng ở `deilca.store`: cầu nối đứt rồi nối lại chỉ sau **1 giây**, nhưng lệnh đang
chờ phản hồi thì mất luôn — 53s sau mới ném `TimeoutException` và shop mất trắng bước "Lấy Số tiền cuối cùng"
(log chỉ ghi "vẫn lưu phần đã có"). Nối lại nhanh KHÔNG cứu được lệnh dở dang: cần gửi lại lệnh sau khi socket
sống lại, hoặc ít nhất rút ngắn hạn chờ khi phát hiện socket vừa thay. CHƯA LÀM.

## 13. NGUYÊN NHÂN THẬT của "chết giữa vòng": tiến trình GPU — không liên quan gì service worker

Mục 10 và mục 12 đều đoán sai. Người dùng đặt đúng câu hỏi mở nút: *"sao bản 1.7.x chạy rất tốt, không bao giờ
chết giữa chừng, mà 1.8.x thì chết lên chết xuống"* — tức là phải tìm HỒI QUY, đừng vá triệu chứng nữa.

Bốn vòng liên tiếp trình duyệt tự chết sau ~23,5 phút, **sai số 2 giây**:

| Vòng | Sống được |
|---|---|
| 10:24 → 10:48:15 | ~23m |
| 11:00 → 11:23:46 | 22m47s |
| 11:31 → 11:54:38 | 23m29s |
| 12:04 → 12:27:30 | 23m28s |

Độ chính xác đó loại hẳn "sập ngẫu nhiên". Bằng chứng cuối cùng nằm ngay trong hồ sơ, ở chỗ chưa ai mở:
`<hồ sơ>\Crashpad\reports\*.dmp` — **5 dump, mốc giờ trùng khít 5 lần chết**. Moi chuỗi ASCII ra:

```
[24388:13324:0810/122730.160:FATAL:content\browser\gpu\gpu_data_manager_impl_private.cc:417]
GPU process isn't usable. Goodbye.
```

Bật `--enable-logging` xác nhận cơ chế: tiến trình GPU chết rồi dựng lại **đều đặn ~2 phút/lần**
(`gpu-process` mới lúc 12:31:43 rồi 12:33:43). Chromium đếm số lần chết, tụt dần qua các chế độ dự phòng, hết
đường thì `FATAL` — giết CẢ trình duyệt. ~12 lần × 2 phút ≈ đúng 23,5 phút.

**Vá:** `--disable-gpu-process-crash-limit` — tắt bộ đếm đó. GPU chết rồi dựng lại thì kệ, trình duyệt sống tiếp.
Guard: `BraveCleanPocArgsTests.CoCoChanGietTrinhDuyetKhiGpuChet`.

**CỐ Ý KHÔNG dùng `--disable-gpu`:** tắt GPU đẩy WebGL sang SwiftShader, mà chuỗi renderer "Google SwiftShader"
là dấu hiệu bot kinh điển — cả kiến trúc này sinh ra để né anti-bot.

**Chưa chứng minh được** thay đổi nào của 1.8.x làm tiến trình GPU bắt đầu chết (có thể là driver / bản Brave,
không phải code repo). Cờ trên chặn hậu quả chứ không chữa gốc; nếu muốn truy tiếp thì đọc `chrome_debug.log`
quanh lúc gpu-process dựng lại.

### Hai lỗi do chính đợt này đẻ ra, đã sửa

1. **`_nutSapXep is not defined`** — tách phần gom ứng viên nút sắp xếp ra hàm cấp module cho gọn. Hàm bơm vào
   trang qua `chrome.scripting.executeScript` chỉ mang theo THÂN của chính nó, phạm vi module ở lại. Mỗi lượt
   gọi ném ReferenceError ⇒ bước sắp xếp chết câm ⇒ vòng 12:04 hỏng **5/5 shop** (trước đó 2/5). Lỗi này
   **không lộ ra nhật ký app**, chỉ nằm trong console của TRANG — tìm thấy nhờ `--enable-logging`. Đã nội tuyến
   lại và ghi cảnh báo ngay trên hàm.
2. **Nhóm cờ chặn vứt tab** (mục 12) — gỡ rồi vẫn chết y hệt ⇒ vô can với cái chết, nhưng vẫn để gỡ: nó chưa
   bao giờ chứng minh được lợi ích, mà `discarded=` đã 0 lần suốt bốn vòng.

### CÒN TREO (cập nhật cuối 10/08)

1. **Chưa chặn Chrome trong code** — chọn Chrome cho cầu nối vẫn chết câm 45s với câu lỗi sai thủ phạm.
2. **Số tiền cuối cùng thử lại vô hạn** — đơn `260805H7XY9YWB` hụt thẻ ở MỌI vòng, tốn thời gian + nhiễu log.
   Cần trần thử lại theo `orderSn` (khuôn giống trần tuổi 14 ngày của mã trả hàng). Hạ ưu tiên: vòng 09:12 lấy
   được 1/1 nên nhiều khả năng là thẻ tải chậm chứ không phải luật sai.
3. **Sắp xếp** — đã vá (mục 11), CHỜ vòng 10:24 nghiệm thu.
4. **Cầu nối chết lúc nghỉ** — đã vá (mục 10), CHỜ vòng 10:24 nghiệm thu. Đây là rào cản khiến chưa vòng nào
   đi hết 12 shop.
5. **Sync mật khẩu client → Hub** — đã chốt (vá ô trống, không đè; lưu dạng thường), chưa bắt đầu.
6. **Chưa commit, chưa bump version** — máy khác vẫn ôm 1.8.6 hỏng.
