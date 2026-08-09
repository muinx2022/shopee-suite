# Plan: Xử lý tồn đọng sau đợt rà soát toàn repo 2026-08-09

- **Ngày:** 2026-08-09
- **Trạng thái:** đang làm
- **Người lập:** Opus 5 (phiên chính) · **Người thực thi:** phiên chính (theo `CLAUDE.md` của repo)

## 1. Bối cảnh & mục tiêu

Đợt rà soát 09/08 (13 agent, 6 hướng độc lập, mọi phát hiện qua một lượt phản biện đối kháng) kết luận:
build 0 lỗi / 0 warning cả Debug lẫn Release, 1889/1889 test pass, 3 extension không lỗi cú pháp. **12 nghi vấn
bị phản biện bác bỏ** vì thực tế đã làm xong. Sau khử trùng lặp còn **46 việc thật**. Báo cáo đầy đủ: xem
mục "Phụ lục" cuối file.

Ba phát hiện nặng mà các đợt trước bỏ sót:

1. **Bản vá test flaky đã nghiệm thu ĐẠT nhưng kẹt ngoài `main`** — nhánh `claude/cranky-wright-3f4e9a`
   (`ea1a094` + `7a51206`), worktree chứa nó trỏ vào `C:/Projects/...` đã biến mất nên git đánh `prunable`;
   `git ls-remote` chỉ có `main`. Kèm **bom hẹn giờ**: mốc đóng cứng `2026-07-30` + `SoNgayGiuMac = 90` ⇒ từ
   **28/10/2026** hai test đỏ vĩnh viễn.
2. **4/5 tag `v1.8.x` trỏ sai commit** (`v1.8.2` trùng y hệt `v1.8.1`; `v1.8.4` trỏ commit "Bump v1.8.3") ⇒
   không checkout được mã nguồn thật của bản đã phát hành.
3. **Scrape mất dòng âm thầm** — dòng lỗi LẺ giữa chunk không bao giờ được vá lại.

**Mục tiêu plan này:** làm hết phần KHÔNG cần người dùng có mặt, ưu tiên các lỗ làm sai/mất dữ liệu; phần cần
người dùng bấm tay hoặc chốt chính sách thì gom thành checklist bàn giao, KHÔNG tự quyết.

**Ràng buộc user đã chốt 09/08:** *"khoan hẵng push bản mới"* ⇒ đợt này **KHÔNG** chạy `release-suite.cmd`,
**KHÔNG** upload, **KHÔNG** deploy hub, **KHÔNG** đụng tag trên remote.

## 2. Phạm vi

### Làm

| Đợt | Mục | Nội dung |
|---|---|---|
| 0 | A3 | Merge `claude/cranky-wright-3f4e9a` vào `main` (**xong**) |
| 1 | B2 | Scrape: dòng lỗi lẻ giữa chunk phải sinh việc vá; ô trạng thái hết báo "Xong dòng N" cho dòng vừa hỏng |
| 1 | B4 | "Đẩy lại" lật `DaGhiSheet` ⇒ đơn hủy ĐÃ CÓ DÒNG rơi vào lối tắt bỏ-qua rồi bị dọn ⇒ dòng cũ trắng vĩnh viễn |
| 1 | B3 | `MarkGsheetSynced` thiếu guard thế hệ ⇒ cú bấm "Đẩy lại" bị lượt đang bay nuốt im lặng |
| 1 | B5a | Thêm lối vào menu cho màn chẩn đoán đơn kẹt (badge ẩn khi `Tong == 0` nên đúng lớp đơn kẹt lại không mở được) |
| 2 | B6 | Extension: poll tab "Đơn Trả hàng Hoàn tiền" + nút sắp xếp thay vì dò một phát |
| 2 | B9 | Hồ sơ trình duyệt của `orders/` thiếu 3/4 cờ trần cache đĩa |
| 2 | B13 | Chuỗi "Chờ lấy hàng" còn 2 bản chép tay → trỏ về hằng chung |
| 2 | B12 | Sửa comment **sai** ở `HubDatabase.Orders.cs:160-161` + thống nhất `TRIM(x, ' \t\n\r')` |
| 3 | A5 | Bổ sung khối ⚠ "phải dán lại Apps Script" vào mục CHANGELOG v1.7.6 |
| 3 | D11 | Sửa dòng "Trạng thái" sai ở 3 plan đợt H |
| 3 | D7, D8 | Sửa comment nói app chạy Avalonia; comment trỏ sai tên lớp cổng cầu nối |
| 4 | D2–D6, D9, D10 | Dọn code chết + gộp bản trùng + bump `version` manifest 2 extension + xoá thư mục rỗng |
| 4 | D1 | `git worktree prune` (chỉ an toàn SAU khi đợt 0 xong) |

### Không làm (bàn giao cho người dùng)

- **A1** upload v1.8.5, **A2** bấm thử nút cập nhật ribbon — user đã chốt hoãn.
- **A4** sửa tag: đụng remote ⇒ chờ user đồng ý.
- **B1** lật `Hub:AllowClientConfigPush=false`: phải mở `/machines` xem còn máy `< v1.8.0` không — cần đăng nhập
  hub admin.
- **B7** ZIP có tiến trình/checksum, **B8** đa-lane cổng WS, **B10** lệnh sync-once, **B11** episode xuống DB,
  **B15** nút "Giao máy khác": việc lớn, cần user chốt có làm không.
- **B5b** tách bộ đếm badge khỏi cửa chạy worker: đổi hành vi worker, để đợt riêng.
- **Toàn bộ nhóm C** (10 việc bấm tay/chạy thật) và **nhóm E** (6 việc chờ chín / chờ chính sách).

## 3. Các bước thực hiện

1. **Đợt 0 (xong):** merge `--no-ff`, chạy lại bộ test orders nhiều lượt xác nhận hết chập chờn.
2. **Đợt 1** — mỗi mục: đọc mã + dựng test ĐỎ TRƯỚC (chứng minh lỗi có thật) → vá → test xanh.
   - B4 và B3 đụng cùng đường "Đẩy lại" ⇒ làm liền nhau, một lượt test chung.
   - B2 phải giữ nguyên ngữ nghĩa `AddPatch` theo dải, chỉ THÊM đường vá theo dòng rời rạc.
3. **Đợt 2** — B6 không có test tự động (extension) ⇒ bù bằng rig jsdom nếu dựng được, không thì ghi rõ mức
   bằng chứng chỉ là đọc mã.
4. **Đợt 3, 4** — thuần văn bản + xoá code 0 caller; sau mỗi đợt build lại.
5. Sau mỗi đợt: `dotnet build` cả solution + `dotnet test`, so với chuẩn 09/08 (**0 lỗi / 0 warning /
   1889 test pass**). Extension: `node --check` + kiểm link ES module.
6. Cuối cùng: một lượt **phản biện đối kháng** trên toàn bộ diff trước khi chốt.

## 4. Tiêu chí nghiệm thu

- [ ] Đợt 0: bộ test orders chạy ≥3 lượt liên tiếp xanh; `git log main` chứa `ea1a094`.
- [ ] B2: có test chứng minh dòng lỗi lẻ giữa chunk sinh ra việc vá đúng dòng đó; ô trạng thái không còn báo
      "Xong dòng N" cho dòng vừa log hỏng.
- [ ] B3 + B4: có test ĐỎ TRƯỚC KHI VÁ cho cả hai; sau khi vá, đơn hủy đã có dòng vẫn được gửi kèm `daHuy`
      sau khi bấm "Đẩy lại", và cú bấm không bị lượt đang bay nuốt.
- [ ] B5a: mở được màn chẩn đoán khi badge ẩn (`Tong == 0`).
- [ ] Build 2 solution: 0 lỗi, 0 warning. Test: **không tụt** dưới 1889 pass.
- [ ] Không mục nào trong "Không làm" bị đụng tới; không commit nào chạm `release-suite.cmd`/tag/remote.

## 5. Rủi ro & lưu ý

- **B3/B4 đụng đường ghi GSheet** — lớp bug "cờ đã đẩy bị reset đua nhau" đã cắn 2 lần (v1.6.3, v1.7.16). Mọi
  thay đổi cờ phải hỏng theo hướng AN TOÀN: thà đẩy thừa một lượt còn hơn nuốt mất một dòng.
- **B2 đụng vòng lặp scrape đang chạy production** — chỉ THÊM đường vá, không đổi ngữ nghĩa `LastCompletedRow`.
- **B6 đụng extension anti-bot** — chỉ thêm vòng poll, tuyệt đối không đổi thứ tự thao tác hay hằng delay.
- **D1 `git worktree prune`**: an toàn (không xoá nhánh), nhưng TUYỆT ĐỐI KHÔNG kèm `git branch -D` hay
  `worktree remove --force` — đó mới là thứ làm mất bản vá A3.
- Trường "Trạng thái" trong `plans/` **không đáng tin** (D11 chứng minh 3 plan ghi ngược thực tế) — mọi kết
  luận phải dựa vào `git log` + mã thật.

---

## Báo cáo thực thi

### Đợt 0 — A3 (ĐÃ COMMIT)

Merge `--no-ff` nhánh `claude/cranky-wright-3f4e9a` vào `main`, không xung đột. Bộ test orders chạy **3 lượt liên
tiếp xanh (1658/1658)**. Gỡ luôn bom hẹn giờ 28/10/2026.

### Đợt 1 — B3 + B4 (XONG, **CHƯA COMMIT** theo yêu cầu người dùng)

**B4 — "Đẩy lại" đẩy đơn hủy đã có dòng vào lối tắt bỏ-qua.** Tách hai cờ: `DaGhiSheet` (đang coi là đã ghi —
nút "Đẩy lại" xoá) và **`DaTungGhiSheet`** (đã TỪNG có dòng — bền qua nút đó, suy từ `gsheet_tab` là cột
`DatLaiCoDayLai` tuyệt đối không đụng, OR với `gsheet_synced_at` làm dây bảo hiểm cho đơn cũ hơn migration).
Lối tắt "đơn hủy chưa từng có vận đơn thì by design không ghi" nay hỏi `DaTungGhiSheet` ở CẢ HAI nơi
(`ConNghiaVuGhiSheet` + thân `PushOrdersToGsheetAsync`). Không thêm cột DB, không migration.

**B3 — `MarkGsheetSynced` thiếu guard thế hệ.** Thêm cột `gsheet_push_gen` (schema + `EnsureColumn`);
`DatLaiCoDayLai` +1; `GetForGsheetPush` đọc ra và mang theo trong `GsheetPendingOrder.GsheetPushGen` tới tận
`MarkGsheetSynced`, hàm này chỉ đóng cờ khi `gsheet_push_gen = $gen`. Khác phía hub một chi tiết có chủ đích:
hub cần CẶP cột vì `MarkHubSynced` chỉ nhận danh sách `order_sn`, còn ở đây thế hệ đi theo từng đơn ⇒ **một cột
là đủ, và không phải GHI trong lúc ĐỌC**. Tra không ra thế hệ → truyền `-1` (không khớp cột nào ⇒ không đóng cờ):
thà đẩy thừa một lượt còn hơn nuốt mất một dòng.

**Kiểm chứng:** `dotnet build ShopeeSuite.sln` **0 lỗi / 0 warning**; `dotnet test orders` **1685/1685 xanh**
(2 lượt); hub **120/120 xanh**. 3 test mới: `DayLai_XenGiuaLoDangBay_MarkGsheetSynced_KhongDongCoOan` (kèm đối
chứng lô mới vẫn đóng cờ được), `DayLai_KHONG_XoaBangChung_DaTungGhiSheet`,
`DonHuy_DaCoDong_SauKhiBamDayLai_VAN_Gui_KemDaHuy` (đầu-cuối qua Web App Apps Script giả).

**THỬ PHÁ (2 lượt, đều đỏ đúng chỗ rồi khôi phục):**

| Phá | Test đỏ |
|---|---|
| `!p.DaTungGhiSheet` → `!p.DaGhiSheet` (2 chỗ) | `DonHuy_DaCoDong_SauKhiBamDayLai_VAN_Gui_KemDaHuy` (2 ca cũ vẫn xanh) |
| Bỏ `AND gsheet_push_gen = $gen` | `DayLai_XenGiuaLoDangBay_MarkGsheetSynced_KhongDongCoOan` (6 ca cũ vẫn xanh) |

### Va chạm với một phiên Claude KHÁC (09/08, 17:46–18:09)

Giữa đợt 1, một phiên khác làm việc "check đơn trả hàng bỏ sót" (`plans/2026-08-09-check-tra-hang-khong-bo-sot.md`,
4 việc, đã xong) trong CÙNG cây làm việc. Ba file bị cả hai đợt sửa: `HubOutbox.cs` (hunk tách bạch — 5 của tôi,
4 của họ), `HubOutboxGsheetHuyTests.cs`, `MaTraHangDocLapTests.cs`. Không mất mã của bên nào. Người dùng chốt:
**chưa commit gì cả**, để nguyên trong cây.

### ĐÍNH CHÍNH — đợt 1 mới làm 2/4 mục

Bản báo cáo trước ghi "Đợt 2–4: CHƯA LÀM", đọc lên thành ra đợt 1 đã trọn. **Sai.** Đợt 1 có 4 mục, mới làm
**B3 + B4**. Còn nợ, không có một dòng mã nào:

- **B2** — scrape: dòng lỗi LẺ giữa chunk không sinh việc vá ⇒ mất dòng âm thầm; ô trạng thái còn báo "Xong dòng
  N" cho chính dòng vừa log hỏng. (`LauncherRunnerLoop.cs:271-274,396-405`)
- **B5a** — thêm lối vào menu cho màn chẩn đoán đơn kẹt (badge ẩn khi `Tong == 0` nên đúng lớp đơn kẹt lại không
  mở được màn). (`MainViewModel.cs:95,122`)

Lỗi khai thiếu này do `nghiem-thu` bắt được. Đợt 2–4 vẫn chưa làm.

### Vòng phản biện — 2 lỗi NẶNG do chính B3 gây ra, đã vá

`phan-bien` dựng rig thật ở mức `HubOutbox.PushOrdersToGsheetAsync` (Apps Script giả trên loopback, cú bấm
"Đẩy lại" bắn đúng lúc server nhận POST) và **phá được 2 chỗ**. Cả hai đều ở chỗ tiếp giáp giữa chốt thế hệ và
phần không sửa:

1. **Chốt thế hệ chặn luôn hai cột DỮ LIỆU.** `MarkGsheetSynced` vốn là MỘT câu UPDATE gộp cả `gsheet_tab` +
   `gsheet_file_url` với các cờ; thêm `AND gsheet_push_gen = $gen` vào là từ chối luôn hai cột đó ⇒ đơn quên mất
   tab đã ghi ⇒ lượt sau ghi **dòng THỨ HAI ở tab tháng mới** (doanh thu đếm đôi) + upload lại phiếu đã có link.
   Đúng hai lỗi mà `DatLaiCoDayLai` cố ý tránh — tức B3 tự dựng lại lỗi cũ.
   **Vá:** tách hai câu UPDATE trong một transaction — nhóm DỮ LIỆU (`gsheet_tab`, `gsheet_file_url`, đều
   COALESCE nên idempotent) LUÔN ghi; chỉ nhóm CỜ (`gsheet_synced_at` + 4 cờ `gsheet_da_*`) mới chốt thế hệ.
2. **`settled.Add` vẫn chạy khi đóng cờ BỊ TỪ CHỐI** ⇒ `settled` → `NenXoaDonKetThuc` → `DeleteOrders`: đơn bị
   xoá khỏi app với `gsheet_synced_at` còn NULL, cú bấm bốc hơi vĩnh viễn. Nghĩa là **B3 hụt đúng ca chính nó
   sinh ra để chữa** (đơn kẹt vì mỗi nghĩa vụ sheet — ca hay gặp nhất trên màn chẩn đoán).
   **Vá:** `MarkGsheetSynced` trả `int` số dòng đóng được cờ; `settled.Add` chỉ khi `> 0`; đếm `boChot` và ghi
   vào dòng log tổng kết để không im lặng. Chữa luôn ca `gen = -1` (đơn lạ script trả về) không còn bị dọn oan.
3. (nhẹ) Tham số `daTungGhiSheet` thêm vào factory test nhưng không ca nào truyền ⇒ chiều LỆCH của hai cờ không
   được phủ. **Vá:** thêm ca `DonHuy_ChuaVanDon_NhungDA_TUNG_GhiSheet_VAN_ConNghiaVuSheet` kèm đối chứng.

3 test mới cho vòng này: `ChotTheHe_ChanCoDaDay_NhungKHONG_Chan_TabVaLinkPhieu`,
`BamDayLai_XEN_GIUA_LuotDangBay_ThiGiuDon_KhongDon_KhongNuotCuBam` (đua TẤT ĐỊNH nhờ móc `KhiNhanBody` mới của
Apps Script giả — chạy giữa lúc nhận body và trả phản hồi), và ca lệch-hai-cờ ở trên.

**Ghi chú về một finding KHÔNG có thật:** `nghiem-thu` báo `HubOutbox.cs:642` là nhánh chết `if (r.Ok && false)`.
Kiểm lại: mã thật là `if (r.Ok && r.BoQua)`. Đó là ảnh chụp đúng lúc `phan-bien` đang phá-rồi-khôi-phục trên
cùng cây — hậu quả của việc chạy hai agent SONG SONG trên một cây làm việc. Lần sau chạy lần lượt.

### Đợt 2–4: CHƯA LÀM

Riêng **B6 phải xét lại** — xem mục dưới.

### Cập nhật phạm vi sau khi đọc plan của phiên kia

- **B6 KHÔNG được phiên kia vá** (đã kiểm mã hiện tại): `flow-returns.js:141` vẫn dò tab MỘT PHÁT, vòng poll ở
  `:153-162` và lượt xác nhận `:166` chỉ chạy SAU khi lần dò đầu đã thấy tab; `:179` dò nút sắp xếp cũng một phát
  (poll `:185-189` là cho MỤC sắp xếp, sau khi đã bấm được nút). **Nhưng hậu quả nhẹ hẳn**: bỏ lượt thì mốc giữ
  nguyên nên lượt sau vẫn là "lần đầu" ⇒ vẫn quét sâu, không mất mã vĩnh viễn. Hạ xuống mức *nhỏ*; làm SAU khi
  phần của họ đã commit (chung file).
- **Nợ mới nhận từ plan của họ:** (a) khoá `return_codes` là `(account_id, order_sn)` ⇒ một đơn có HAI yêu cầu
  trả hàng chỉ giữ mã sau cùng; (b) selector nút lật trang trang trả hàng CHƯA xác nhận trên trang thật.
- **Checklist phát hành có thêm một dòng quan trọng: phải NẠP LẠI EXTENSION** (đợt của họ thêm lệnh cầu nối mới
  `readReturnRequestsMore`) — trước nay phát hành chỉ cần cập nhật client.
