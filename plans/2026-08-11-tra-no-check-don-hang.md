# Trả nợ đợt check đơn hàng + phát hành v1.9.1 (11/08/2026, tối)

**Bối cảnh:** đợt `2026-08-11-sua-6-lo-nang-check-don-hang.md` đã HOÀN THÀNH + nghiệm thu chạy thật, để lại
mục "Ghi nợ". User lệnh: "làm nốt đi rồi bump version". Phạm vi đợt này = 3 món nợ CLIENT + phát hành.

**KHÔNG làm đợt này (chốt có lý do):**
- Con trỏ "đọc tới trang k" cho shop tồn đọng sâu hơn trần 200 dòng/10 trang — plan trước đã chốt "để đợt sau
  nếu thực tế cần"; thực tế tồn đọng lớn nhất đang là 64 < trần 200, và nó đòi extension biết nhảy trang tùy ý
  (tính năng mới, rủi ro cao ngay trước phát hành).
- Nhóm phát hiện TRUNG BÌNH T1–T12 của đợt review (T4–T9 là phía Hub, cần đợt + deploy riêng).

## Nợ A — `doReadToShip` bỏ lối lùi tab picker

Hiện trạng: `flow-shop.js` dòng ~175 `ctx.shopTabId != null ? ctx.shopTabId : ctx.listTabId` — đúng lớp lỗi V7
nhưng ở lệnh cấp SHOP: SW chết + khôi phục hỏng ⇒ đọc "Chờ Lấy Hàng" trên TAB PICKER (số của shop sticky SAI,
hoặc treo 8s rồi trả null che mất chẩn đoán).

Sửa: bỏ lối lùi — `ctx.shopTabId == null` ⇒ `send({action:"error", message: LOI_MAT_TAB_SHOP})` (import từ
core.js, cùng chuỗi với các lệnh cấp đơn để nhật ký C# gom được). Phía C#: `error` → fault chặng ToShip →
catch từng-shop sẵn có ghi shop hỏng, chạy shop kế (xác nhận lại catch này khi làm).

**Tiêu chí:** (1) `flow-shop.js` không còn tham chiếu `listTabId` trong `doReadToShip`; (2) `node --check`
sạch; (3) test cú pháp extension xanh; (4) có test C# chứng minh `error` ở chặng ToShip không đánh đổ cả vòng
(nếu rig với tới vòng shop; không với tới thì ghi rõ lý do trong plan).

## Nợ B — bịt cửa sổ đua `khoiPhucTabShop`

Hiện trạng: `noiLaiTuStorage` chạy lúc SW dựng dậy **và mỗi nhịp alarm 30s**, gọi `khoiPhucTabShop`
fire-and-forget. Cửa sổ đua HAI CHIỀU quanh `await chrome.tabs.get`:
- `doCloseShopTab` vừa ghi null xong → khôi phục hoàn tất muộn HỒI SINH id đã đóng;
- `openShopDetail` vừa `nhoTabShop(idMới)` → khôi phục (validate id CŨ chết) hoàn tất muộn GHI ĐÈ null.

Sửa bằng **số thế hệ**: `ctx.theHeTabShop` (core.js), mọi ghi CHỦ ĐỘNG qua `nhoTabShop` (flow-shop.js) tăng
nó; `khoiPhucTabShop` (background.js) chụp thế hệ trước khi validate, sau `await` mà thế hệ đã đổi ⇒ bỏ kết
quả (ghi chủ động thắng). Thêm chốt đầu hàm: `ctx.shopTabId != null` ⇒ return (SW sống, ctx là nguồn chân lý —
nhịp alarm 30s không việc gì phải đè).

**Tiêu chí:** (1) `khoiPhucTabShop` không còn đường ghi `ctx.shopTabId` sau khi thế hệ đổi; (2) khôi phục
bình thường (SW mới, không ai ghi chen) vẫn chạy y cũ; (3) `node --check` + test cú pháp xanh.

## Nợ C — cột lý do còn-sót (`tra_hang_sot_ly_do`)

Hiện trạng: `tra_hang_con_sot` chỉ là 0/1 — không phân biệt được "sót vì chạm trần (danh sách cần vơi)" với
"sót vì hỏng sắp xếp / selector (cần người xem)". Shop hỏng sắp xếp giữ cờ 1 vĩnh viễn mà không ai biết vì sao.

Sửa:
- DB: cột `tra_hang_sot_ly_do TEXT NOT NULL DEFAULT ''` (CREATE TABLE + `EnsureColumn` cho DB cũ).
  Giá trị: `''` (không sót) | `tran` | `sap_xep` | `lat_truot` | `doc_hong`.
- `ResultsRepository.SetTraHangConSot(acc, shop, conSot, lyDo)` ghi cả hai cột; thêm
  `GetTraHangSotLyDo` để test/chẩn đoán. Đường ĐỌC cho quyết định chế độ giữ nguyên bool.
- Runner: biến `lyDoConSot` gán tại đúng 5 điểm đang set `docDuSau=false` (sắp xếp 819 → `sap_xep`; trần dòng
  837 + trần trang 873 → `tran`; lật trượt 849 → `lat_truot`; ô tổng >0 mà 0 dòng 886 → `doc_hong`, đè được
  `sap_xep` vì chẩn đoán sắc hơn). Câu log "GIỮ NGUYÊN mốc" nói kèm lý do bằng lời thật.
- Chữ ký callback `luuConSotTraHang` thêm tham số lyDo (OrdersBridgeSession, AccountSession, tests).

**Tiêu chí:** (1) migration DB cũ mọc cột, shop cũ mặc định `''`; (2) roundtrip Set/Get; (3) test rig: kịch
bản chạm trần ghi `tran`, hỏng sắp xếp ghi `sap_xep`, selector hỏng ghi `doc_hong`, đọc đủ sâu ghi lại `''`;
(4) test MỚI phải THỬ PHÁ (đỏ khi phá, xanh khi khôi phục).

## Kiểm chứng chung

Build `ShopeeSuite.sln` **0 warning/0 error**; `dotnet test` orders (đường mặc định, KHÔNG `-p:OutDir`) +
suite còn lại xanh đủ; `node --check` mọi file JS sửa; MỘT lượt phản biện subagent trên diff trả nợ trước khi
commit (bài học 04/08 + 09/08: test xanh không thay được phản biện).

## Phát hành

1. Commit đợt trả nợ (stage chọn lọc).
2. `version.txt` 1.9.0 → **1.9.1**; ghi CHANGELOG (lời cho người dùng); commit "Bump v1.9.1 + CHANGELOG (…)".
3. Chạy các bước `release-suite.cmd` (sync-shared --check → vpk download → publish → pack → upload github,
   token từ `gh auth token`). Push `main` (chuẩn cũ: origin đang khớp từng bump trước).
4. Máy này: đợi/kiểm cửa sổ NGHỈ giữa vòng → dừng app → build bin thật → mở lại + tự bấm Chạy → soi log vòng
   mới (đặc biệt: migration cột mới không lỗi, các mẫu `trượt|đọc THIẾU|mất ngữ cảnh` = 0) → ĐỂ APP CHẠY TIẾP.

## Kết quả phản biện (subagent, 11/08 tối) + sửa theo

Phản biện xác nhận không có đường mất dữ liệu trong 3 món nợ, kèm 2 TRUNG BÌNH + 3 NHẸ — **đã sửa cả**:

1. **[TB] Phân loại lỗi mất-ngữ-cảnh:** `error` "mất ngữ cảnh tab shop" ra `InvalidOperationException` ⇒
   `LaLoiCauNoi` cũ trả false ⇒ shop KHÔNG được thử lại cuối vòng (mất trọn vòng; 3 shop liên tiếp dừng cả
   vòng) — trong khi cùng cú SW chết biểu hiện bằng rớt socket thì được cứu. **Sửa:** hằng
   `OrdersBridgeChannel.MocLoiMatTabShop` ("mất ngữ cảnh tab shop", PHẢI khớp `LOI_MAT_TAB_SHOP` core.js) +
   `LaLoiCauNoi` nhận diện thêm nhánh này. An toàn vì `NenThuLaiShopRoiOan` vẫn xét `DaGuiChuanBiHang`
   TRƯỚC mọi điều kiện. Test mới: `MatNguCanhTabShop_CungLaLoiCauNoi` + test đồng bộ chuỗi C#↔JS
   `MocLoiMatTabShop_KhopBanExtension` (đọc thẳng core.js, khuôn `HangTran_KhopBanExtension`).
2. **[TB] Lỗ sinh đôi `listTabId`:** `noiLaiTuStorage` (chạy cả mỗi nhịp alarm 30s) đè `ctx.listTabId` từ
   storage vô điều kiện, mà `ensureListTab`/`gotoSellerCentre` đổi ctx KHÔNG ghi storage ⇒ alarm kéo lùi về
   tab chết → `doCloseShopTab` đốt 20s chờ tab ma → "picker không sẵn sàng" oan. **Sửa:** chốt
   `ctx.listTabId == null` (đúng kỷ luật gói wake `dauTien` sẵn có). Đối xứng đầy đủ (`nhoTabPicker` + thế hệ
   + đồng bộ storage chủ động) ghi nợ — xem dưới.
3. **[NHẸ] Lưới test hở:** nhánh trần TRANG chưa test nào đi qua (ghi sai hằng vẫn xanh cả suite) → test rig
   mới `ChamTranTrang_GiuMoc_BatCoConSot_LyDoTran` (lật đủ 10 trang); `MoTaLyDoSot` chưa ai gọi → test bảng
   mã→lời + nhánh mặc định đổi thành câu TỰ TỐ "không rõ lý do … (lỗi code, báo dev)" thay vì im lặng;
   `GetTraHangSotLyDo` shop chưa-có-dòng → thêm assert.
4. **[NHẸ] Tiêu chí A(4) — giải trình:** rig KHÔNG dựng được vòng shop `RunAllShopsAsync` (đòi SSO + picker
   thật) nên không có test end-to-end "error ở ToShip không giết vòng". Thay bằng: (i) cơ chế
   `error`→`FaultCurrent` là dùng chung mọi chặng và đã có test ở chặng địa chỉ; (ii) catch từng-shop +
   bảng `QuyetDinhSauShopHong`/`NenThuLaiShopRoiOan` là hàm thuần có test; (iii) phân loại thử-lại cho đúng
   lỗi này nay có test riêng (mục 1). Soi tay xác nhận đường chảy: OrdersBridgeSession.cs ~950.
5. **[NHẸ, ghi hồ sơ — không sửa đợt này]** Khe `FaultCurrent` giữa `SendAsync` và `AwaitAsync` đăng ký: `error`
   về sớm hơn đăng ký thì chỉ được log, chặng chờ hết hạn 30s rồi ra TimeoutException (vẫn được thử lại). Hại
   nhỏ, xác suất thấp.

**Ghi nợ mới của đợt này:** đối xứng hoá `listTabId` (`nhoTabPicker` ghi ctx+storage+thế hệ như `nhoTabShop`,
`doCloseShopTab` re-validate trước khi dùng); khe FaultCurrent ở trên.

## Kiểm chứng chốt

| Hạng mục | Kết quả |
|---|---|
| `node --check` cả 12 file JS extension | sạch |
| Build `ShopeeSuite.sln` (OutDir scratch — app đang chạy giữa vòng) | **0 warning / 0 error** |
| Test orders (đường mặc định) | **1759/1759** (1751 + 8 test mới đợt sửa-theo-phản-biện) |
| Test suite core | 139/139 |
| Thử phá | **8/8 lượt ĐỎ đúng test rồi khôi phục xanh**: (1) doc_hong đè sap_xep, (2) lý do trần DÒNG, (3) chuẩn-về-rỗng khi tắt cờ, (4) nhận diện mất-ngữ-cảnh trong LaLoiCauNoi, (5) đồng bộ mốc chuỗi C#↔JS, (6) bảng mã→lời, (7) lý do trần TRANG, (8) nhánh chưa-có-dòng GetTraHangSotLyDo |

(Số test cuối xác nhận lại ở lượt chạy chốt — xem báo cáo.)

## Tiến độ

- [x] Commit đợt 6 lỗ (`0a0f821`)
- [x] Nợ A, B (extension) + kiểm JS
- [x] Nợ C (DB + runner + plumbing) + tests + thử phá
- [x] Build 0W + full test xanh
- [x] Phản biện subagent + sửa theo (2 TB + 3 NHẸ — xem trên)
- [ ] Commit trả nợ
- [ ] Bump 1.9.1 + CHANGELOG + commit
- [ ] Phát hành GitHub + push
- [ ] Cập nhật bin máy này + chạy lại app + soi log
