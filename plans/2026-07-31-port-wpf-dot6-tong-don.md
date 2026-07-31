# Plan: Port WPF — Đợt 6: tổng dọn + chuẩn bị phát hành (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

> **ĐỌC TRƯỚC:** `plans/2026-07-31-port-wpf-ke-hoach-tong.md` + "Báo cáo thực thi" plan đợt 1–5 (mục "Đề nghị
> đợt 6" của đợt 3/4/5 chính là đầu vào của plan này). WORKTREE `d:\Projects\shopee-suite-onlywin` (nhánh
> `only-windows`); TUYỆT ĐỐI không đọc/ghi `d:\Projects\shopee-suite`. Phần nhánh/merge về main do người điều
> phối làm SAU — KHÔNG thuộc plan này.

## 1. Bối cảnh & mục tiêu

Đợt 1–5 xong: toàn bộ UI hai module đã là WPF, build 0 warning, binding log 0. Đợt 6 dọn các món đã ghi nợ
qua 5 đợt + làm nhánh sẵn sàng merge về main.

## 2. Phạm vi

- **Làm:** các mục ở bước 3 dưới. **Không làm:** không đổi nghiệp vụ/VM; không bump `version.txt` (điều phối
  bump khi phát hành); không tách nhánh/merge; không commit.

## 3. Các bước thực hiện

1. **Quét `<Run Text="{Binding…}">` toàn repo** (suite + orders): mọi chỗ bind thiếu `Mode=OneWay` → thêm
   (bẫy đợt 3: mặc định TwoWay, gặp property chỉ-đọc là chết app lúc dựng cây). Grep cả `Run Text=` trong
   *.xaml, liệt kê từng chỗ trong báo cáo (đợt 3 ghi nhận DataView còn ~5 chỗ).
2. **Sửa các lỗi hiển thị đã ghi nhận:**
   - AccountsView orders: nhãn nút "Thêm tài khoản" bị cắt ("Thêm tài khoả…") — nới bố cục cột trái/чиều
     rộng nút cho đủ chữ (giữ tinh thần layout cũ, không thiết kế lại).
   - OrdersView: pill "Trả hàng/Hoàn tiền" cắt ở cột 140px — nới cột hoặc cho pill wrap/thu chữ, chọn phương
     án nhìn ổn nhất, chụp ảnh trước/sau.
   - SearchView tab "Tìm kiếm": hàng cao cố định `190` ép hàng `*` về 0 khi cửa sổ thấp → đổi sang
     `Auto`/`MaxHeight` như đề nghị đợt 4, kiểm ở cửa sổ 1000×700.
   - Template ComboBox phẳng (đợt 4 hoãn): đưa ComboBox về đúng hướng phẳng Win11 của theme (bo 4–6, viền ấm,
     focus cam) — một style chung trong `Theme.xaml`, orders dùng bản module nếu token khác.
3. **Dọn mã:** xoá `suite/Shopee.Suite/Views/PortingWindow.cs` (0 lớp con còn lại — xác minh bằng grep trước
   khi xoá); rà `grep -i avalonia` trong comment/xmldoc của 3 project UI → sửa các câu đã lỗi thời (vd ghi chú
   R2R/WDAC trong `orders/XuLyDonShopee.App.csproj` nói về DLL Avalonia — cập nhật thành ghi chú WPF: ràng
   buộc cũ nhiều khả năng hết áp dụng, cần kiểm lại trên máy WDAC khi phát hành thật).
4. **Xoá đường build Linux khỏi nhánh này:** `release-suite.sh`, `publish-suite.sh`, `install-linux.sh`
   (bản Linux sống ở nhánh `avalonia` sẽ tách sau). `release-suite.cmd` GIỮ NGUYÊN channel `win`. Grep các
   tham chiếu tới 3 file vừa xoá (docs/cmd khác) để khỏi gãy.
5. **CHANGELOG.md:** trong mục `## Chưa phát hành` (đã có entry làm lại Cài đặt), thêm phần port WPF: UI
   chuyển toàn bộ Avalonia → WPF (bản Windows-only), điểm người dùng nhận thấy (chữ ClearType sắc hơn, giao
   diện giữ nguyên bố cục), ghi rõ bản Linux/Ubuntu từ nay phát hành từ nhánh `avalonia`.
6. **Regression toàn cục:** build `--no-incremental` 0 error 0 warning; test 1459 + 61 xanh; chạy lại rig
   nghiệm thu các đợt (2/3/4/5 — cách ly như cũ, đặc biệt đợt 5: redirect USERPROFILE, cấm --mode full, không
   phóng trình duyệt) → binding log 0 dòng ở mọi lượt; chụp ảnh các chỗ sửa ở bước 2.
7. Điền "Báo cáo thực thi" plan này trong worktree (kèm bảng: từng món nợ đợt 3/4/5 → đã xử/why not).

## 4. Tiêu chí nghiệm thu

- [ ] Grep `<Run Text="{Binding` không còn chỗ nào thiếu `Mode=OneWay` (trừ chỗ property có setter VÀ cố ý
      two-way — nếu có, liệt kê + lý do).
- [ ] 4 lỗi hiển thị bước 2 có ảnh trước/sau.
- [ ] Không còn `PortingWindow.cs`; không còn comment Avalonia lỗi thời trong 3 project UI; không còn 3 script
      Linux trên nhánh.
- [ ] Build 0/0; test xanh; mọi rig binding log 0; không sót file tạm/process; production không bị đụng.

## 5. Rủi ро & lưu ý

- Đây là đợt "đánh bóng" — KHÔNG mở rộng: thấy món gì ngoài danh sách thì ghi vào báo cáo, đừng tự xử.
- Mọi lượt chạy thử tuân thủ nguyên xi quy trình cách ly đợt 5 (USERPROFILE giả, không --mode full).
- Smoke-test chế độ Full với máy thật (tắt bản production) KHÔNG thuộc đợt này — điều phối sẽ thu xếp với
  người dùng trước khi phát hành 1.7.0.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Ngày:** 2026-07-31 · **Người thực thi:** Opus (`opus-executor`) · **Nơi làm:** worktree
`d:\Projects\shopee-suite-onlywin` (nhánh `only-windows`) · **CHƯA commit** (theo plan).

### 1. Kết quả kiểm chứng (số liệu thật)

| Hạng mục | Lệnh | Kết quả |
|---|---|---|
| Build sạch | `dotnet build ShopeeSuite.sln --no-incremental` | **Build succeeded — 0 Warning(s), 0 Error(s)** |
| Test core | `dotnet test suite/Shopee.Core.Tests --no-build` | **Passed 61 / Failed 0 / Skipped 0** |
| Test orders | `dotnet test orders/XuLyDonShopee.Tests --no-build` | **Passed 1459 / Failed 0 / Skipped 0** |
| Rig đợt 2 (2 modal) | `verify-modals2.ps1 -Tag d6r2` | **0 dòng** binding log sau TỪNG bước · ExitCode 0 · còn đúng 1 cửa sổ |
| Rig đợt 3 (Workspace + ScrapeStats) | `verify-dot3.ps1 -Tag d6r3` | **0 dòng** · 9/9 bước · ExitCode 0 |
| Rig đợt 4 (Search/Fleet/CheckAcc) | `verify-dot4.ps1 -Tag d6r4` | **0 dòng** · 11/11 bước · ExitCode 0 |
| Rig đợt 4 ở cửa sổ 1000×700 | `verify-dot4.ps1 -Tag d6after-low -Width 1000 -Height 700` | **0 dòng** · ExitCode 0 |
| Rig đợt 5 chế độ Shopee | `verify-dot5.ps1 -Tag d6after -Mode shopee` | **0 dòng** · 12/12 bước · ExitCode 0 |
| Rig đợt 5 chế độ Workspace | `verify-dot5.ps1 -Tag d6r5w -Mode workspace -NoSeed` | **0 dòng** · ExitCode 0 |
| Rig MỚI: template ComboBox | `verify-combo.ps1 -Tag c1` | **0 dòng** · mở dropdown OK (8 mục) · chọn mục OK · gõ chữ vào ô IsEditable OK |
| Rig MỚI: đo chiều cao hàng Search | `measure-search-rows.ps1` | bảng số ở mục 4.3 |

**An toàn dữ liệu** (mọi lượt, rig tự in): `%APPDATA%\XuLyDonShopee\app.db` production **1.339.392 bytes ·
ghi lúc 2026-07-31 01:23:12 · sha256 `4BDF62F4…`** — Y HỆT trước/sau **cả 10 lượt chạy**; Brave **8 → 8**;
cổng 47821 do PID dev mở: **0 dòng netstat**; sau cùng chỉ còn ShopeeSuite **production** (PID 33732, khởi
động 01:23 — trước phiên này). `data-dir.txt` cạnh exe đã xoá; 22 thư mục tạm trong scratchpad đã dọn sạch.
"Crash log" duy nhất trong hồ sơ giả là dòng lành: *"Bỏ qua dọn dẹp khởi động: có ShopeeSuite khác đang chạy."*

### 2. Bảng MÓN NỢ đợt 3/4/5 → đã xử thế nào

| # | Nguồn | Món nợ | Đã xử |
|---|---|---|---|
| 1 | đợt 3, mục 6.1 | `DataView.xaml` còn 5 `<Run Text="{Binding SelectedCount}"/>` thiếu `Mode=OneWay` (bẫy chờ nổ) | **XONG** — thêm `Mode=OneWay` cả 5 + comment cảnh báo đầu file. Quét lại toàn repo: 26 `<Run Text="{Binding` **đều có** `Mode=OneWay`, **0 chỗ sót** |
| 2 | đợt 3, mục 6.4 | Chưa soi được badge ✓ "đã xong" + nút "Dừng việc shop này" (cần Hub sống) | **KHÔNG xử** — vẫn cần Hub + job thật, không dựng được trong rig cách ly. Vẫn nợ, ghi ở mục 6 |
| 3 | đợt 4, mục 6.1 | Công tắc `SHOPEESUITE_SOFTWARE_RENDER` (7 dòng ở `App.xaml.cs`) — Fable quyết giữ/bỏ | **GIỮ NGUYÊN, không đụng** — đợt 6 phải dùng lại nó ở **mọi** lượt chụp ảnh, không có nó là ảnh trắng trơn. Quyền quyết vẫn ở Fable |
| 4 | đợt 4, mục 6.6 | Tab "Tìm kiếm" hàng cố định `190` ép hàng `*` về 0 khi cửa sổ thấp | **XONG** — đổi `Height="190"` → `Height="*" MinHeight="150" MaxHeight="190"`; đo trước/sau ở mục 4.3 |
| 5 | đợt 4, mục 6.8 | Nit UIAutomation: TabItem dải tab link có `Name` = tên kiểu (thiếu `AutomationProperties.Name`) | **KHÔNG xử** — plan đợt 6 không nêu, và đây là chuyện trợ năng chứ không phải lỗi hiển thị; xem đề xuất mục 7 |
| 6 | đợt 4 mục 6.9 + đợt 5 mục 7.3 | ComboBox còn chrome mặc định WPF (nền gradient xám) | **XONG** — dựng template phẳng trong `Theme.xaml`; orders ăn theo qua implicit style, chỉ đè token màu |
| 7 | đợt 4, mục 6.10 + đợt 5, mục 7.6 | `Views/PortingWindow.cs` 0 lớp con | **XONG** — grep xác nhận 0 tham chiếu ngoài chính nó → đã xoá |
| 8 | đợt 5, mục 7.1 | Nhãn nút "Thêm tài khoản" bị cắt thành "Thêm tài khoả" | **XONG** — nới cột trái AccountsView 340 → 360 |
| 9 | đợt 5, mục 7.2 | Pill "Trả hàng/Hoàn tiền" bị cắt ở cột 140 | **XONG** — cột 140 → 160 + `TextTrimming` + `ToolTip` |
| 10 | đợt 5, mục 7.4 | `LetterSpacing` bỏ (WPF không có) | **KHÔNG xử được** — WPF thật sự không có thuộc tính này; đây là ghi nhận, không phải việc |
| 11 | đợt 5, mục 7.5 | Chưa soi được vòng quay cam / chấm xanh "Chờ lấy: N" / nút "Tải phiếu" thiếu file | **KHÔNG xử** — cần phiên trình duyệt THẬT, cố ý không thử (quy tắc an toàn). Vẫn nợ |
| 12 | plan tổng, QĐ 20 | Kiểm lại `PublishReadyToRun` + WDAC | **Xử một nửa** — đã viết lại ghi chú csproj cho đúng bối cảnh WPF, **giữ nguyên quyết định cũ** vì không có máy WDAC UMCI Enforced để đo. Chi tiết mục 5.4 |

### 3. Danh sách chỗ sửa `Mode=OneWay` (bước 1)

Quét `<Run Text=` toàn repo (suite + orders, `*.xaml`): **26** thẻ `<Run>` có bind. **Chỉ 5 chỗ thiếu**, tất cả
trong một file — đúng như đợt 3 dự báo:

| File | Dòng | Nhãn nút | Property bind |
|---|---|---|---|
| `suite/Shopee.Suite/Modules/Data/DataView.xaml` | 115 | "Bỏ chọn (N)" | `SelectedCount` |
| `suite/Shopee.Suite/Modules/Data/DataView.xaml` | 121 | "Đã bán (N)" | `SelectedCount` |
| `suite/Shopee.Suite/Modules/Data/DataView.xaml` | 128 | "Đã bán = 0 (N)" | `SelectedCount` |
| `suite/Shopee.Suite/Modules/Data/DataView.xaml` | 135 | "Sinh SKU mới (N)" | `SelectedCount` |
| `suite/Shopee.Suite/Modules/Data/DataView.xaml` | 141 | "Xóa nhiều (N)" | `SelectedCount` |

Không có chỗ nào two-way **cố ý** (`<Run>` chỉ để hiển thị). `orders/**` không có thẻ `<Run>` nào.
21 chỗ còn lại (WorkspaceView 7 · SearchView 6 · FleetView 1 · UnifiedSettingsView 2 · và các `<Run>` chữ
tĩnh) đã có `Mode=OneWay` từ đợt 3–5.

### 4. Bốn lỗi hiển thị — ảnh TRƯỚC/SAU

Ảnh nằm ở scratchpad `…\86f7fb17-b280-49ad-87e5-94d7a1e7b273\scratchpad\`. Ảnh `*-d6before*` chụp trên bản
build **trước** khi sửa, `*-d6after*` **sau** khi sửa, cùng rig + cùng dữ liệu seed.

#### 4.1 Nút "Thêm tài khoản" (AccountsView của orders)

| | Ảnh |
|---|---|
| Toàn màn | `d5-1-orders-tai-khoan-d6before.png` → `d5-1-orders-tai-khoan-d6after.png` |
| Cắt cận cảnh (phóng 2×) | **`d6-fix1-btn-TRUOC.png`** → **`d6-fix1-btn-SAU.png`** |

TRƯỚC: `+ Thêm tài khoả|` (chữ "n" bị cắt cụt sát viền). SAU: `+ Thêm tài khoản` đủ chữ, còn dư lề.
**Đo trước khi sửa** (`FormattedText`, Segoe UI Variable 12px): hàng nút cần 131 (Thêm tài khoản) + 8 +
126 (Kéo TK từ Hub) + 8 + 30 (nút xoá) = **303px**, mà cột 340 chỉ chừa 340−1(viền)−24(lề trái)−20(lề phải)
= **295px** ⇒ hụt 8px, hàng `*` nuốt trọn phần hụt. Sửa: cột trái **340 → 360** (chừa 315, dư ~12). Không đổi
cấu trúc `*,Auto,Auto` của bản Avalonia, không thu gọn nút nào.

#### 4.2 Pill "Trả hàng/Hoàn tiền" (OrdersView)

| | Ảnh |
|---|---|
| Toàn màn | `d5-5-orders-don-hang-d6before.png` → `d5-5-orders-don-hang-d6after.png` |
| Cắt cận cảnh cột Trạng thái (2×) | **`d6-fix2-pill-TRUOC.png`** → **`d6-fix2-pill-SAU.png`** |

TRƯỚC: `Trả hàng/Hoàn t` — cắt cụt GIỮA CHỮ, không có dấu "…". SAU: `Trả hàng/Hoàn tiền` đủ chữ.
**Đo:** pill cần 105,4 (chữ SemiBold 12px) + 20 (padding) + 2 (viền) = 127,4; cộng padding ô lưới 10+10 ⇒ cột
phải ≥ **147,4**, cột cũ 140 ⇒ hụt ~7px. Sửa: cột **140 → 160**. Vì trạng thái là **chuỗi tự do cào từ Shopee**
(vd "Giao hàng không thành công" cần ~201px — không thể nới cột tới đó trong lưới 16 cột), tôi thêm
`TextTrimming="CharacterEllipsis"` + `ToolTip="{Binding Status}"`: chuỗi dài bất thường cắt có "…" và rê chuột
đọc đủ, thay vì cụt giữa chữ như hiện nay.

#### 4.3 SearchView tab "Tìm kiếm" — hàng cố định 190

| | Ảnh |
|---|---|
| Cửa sổ 1000×700 | `d4-1-search-tim-kiem-d6before-low.png` → `d4-1-search-tim-kiem-d6after-low.png` |
| Cửa sổ 1900×1175 | `d4-1-search-tim-kiem-d6r4.png` (sau khi sửa) |

Sửa: `<RowDefinition Height="190"/>` → `<RowDefinition Height="*" MinHeight="150" MaxHeight="190"/>`.

**Đo bằng UIAutomation** (`measure-search-rows.ps1`, cửa sổ rộng 1500, số px VẬT LÝ ở DPI 125%, seed 4 link):

| Cửa sổ cao | TRƯỚC — lưới link | TRƯỚC — ô log | SAU — lưới link | SAU — ô log |
|---|---|---|---|---|
| 1400 | cao 178 | 1156..1329 · **trong cửa sổ** | cao 178 (y hệt) | 1156..1329 · **trong cửa sổ** |
| 1175 | cao 178 | 931..1104 · trong cửa sổ | cao **128** | 931..1104 · trong cửa sổ |
| 1000 | cao 178 | 852..1025 · **TRÀN** (đáy cửa sổ 1010) | cao **128** | **802..975 · TRONG cửa sổ** |
| 860 | cao 178 | 852..1025 · TRÀN | cao 128 | 802..975 · **vẫn TRÀN** |

Nghĩa là: cửa sổ cao thì **không đổi gì** so với bản cũ; xuống 1000px thì ô log từ **nằm ngoài** đáy cửa sổ
thành **nằm trong**, và hàng `*` (dải tab kết quả theo link) có chỗ thật thay vì 0.

**Nói thẳng về mốc 1000×700 mà plan yêu cầu kiểm:** ở chiều cao ~860px trở xuống nội dung **vẫn tràn** — thẻ
nhập ở trên chiếm ~155px và ô log 150px là hai khối không co được, tổng phần bắt buộc đã vượt vùng nhìn.
Không có cách xếp lưới nào (Auto hay sao, kẹp Min/Max kiểu gì) cứu được; muốn đỡ thì phải cho **cả tab cuộn
dọc** (ScrollViewer + MinHeight cho lưới trong) — đó là đổi kết cấu màn, KHÔNG thuộc phạm vi "đánh bóng" của
đợt 6 nên tôi dừng lại và ghi vào đề xuất mục 7.

*Vì sao `MinHeight="150"` chứ không phải 120:* tôi đã thử 120 và ĐO — ở cửa sổ 1175 lưới link tụt 178 → **98**
(chỉ còn ~1,3 dòng) trong khi hàng kết quả lúc chưa chạy search vốn rỗng, tức mất chỗ mà không được gì.
150 giữ lưới ở 128 (≈2,3 dòng, vẫn cuộn được) mà vẫn đủ nhường chỗ để ô log lọt vào cửa sổ ở mốc 1000.

#### 4.4 Template ComboBox phẳng

| | Ảnh |
|---|---|
| Cắt cận cảnh 2 ô lọc màn Đơn hàng (2×) | **`d6-fix4-combo-TRUOC.png`** → **`d6-fix4-combo-SAU.png`** |
| Dropdown đang mở (chụp cả màn hình vì popup nằm ngoài cửa sổ) | **`d6-fix4-dropdown-SAU.png`** (gốc `d6-combo-dropdown-c1.png`) |
| Gõ chữ vào ô IsEditable | **`d6-fix4-gochu-SAU.png`** (gốc `d6-combo-goChu-c1.png`) |
| Đối chiếu các màn khác | `d6-chk-dataview-combos.png` (2 combo lọc DataView nằm cạnh 3 TextBox), `d5-7-caidat-workspace-d6r5w.png` (combo "Chế độ"), `d5-9-dialog-chi-tiet-don-d6after.png` (combo "Đổi trạng thái" trong hộp thoại) |

TRƯỚC: chrome Aero2 mặc định — nền gradient xám, viền 2 lớp, nút thả hình hộp vuông; đứng cạnh TextBox của
theme là **lệch hẳn**. SAU: nền trắng, viền 1px `BorderBrush`, bo 5, chevron vector mảnh — **giống hệt ô nhập**
bên cạnh (thấy rõ ở `d6-chk-dataview-combos.png`: 2 combo và 3 TextBox giờ cùng một dáng).

Đã kiểm hành vi, không chỉ nhìn ảnh (`verify-combo.ps1`): mở dropdown → `ExpandCollapseState = Expanded`,
liệt kê đủ **8 mục**; chọn mục thứ 3 → ô hiển thị đổi thành "Chờ xác nhận"; gõ "Saigon" vào ô `IsEditable` →
`ValuePattern.Value = 'Saigon'`, viền chuyển CAM khi focus, nút ✕ xoá lọc hiện đúng chỗ, không đè chevron.

Chi tiết kỹ thuật đáng lưu:
- Style `comboItem` (ItemContainerStyle mặc định): hover nền xám nhẹ, **đang chọn = nền cam nhạt + chữ cam**
  (đúng ngữ pháp "active" của theme).
- Khung vẽ bằng `Border` RIÊNG (`PART_BorderElement`) chứ không phải bằng `ToggleButton`: ở chế độ
  `IsEditable`, vùng bấm co lại còn mỗi khoang chevron (để ô nhập nhận được chuột), nếu khung dính vào
  ToggleButton thì viền sẽ mất một đoạn.
- Trigger hover/focus/mở/disabled đặt **theo TargetName** (`PART_BorderElement`) chứ không đặt lại
  `BorderBrush` của chính ComboBox — chỗ dùng nào lỡ khai `BorderBrush` tại chỗ (local value) thì trigger đặt
  lên templated parent sẽ thua.
- Khoang chevron cố định **22px**: ô "gõ tên shop để lọc" ở OrdersView phủ watermark/nút xoá theo lề 13/34 —
  đã ghi cảnh báo ở cả `Theme.xaml`, `Controls.xaml` (orders) và chỗ dùng.
- Module Đơn hàng **không phải sửa gì**: `fieldCombo` vốn `BasedOn="{StaticResource {x:Type ComboBox}}"`,
  tra qua dự phòng `Application.Resources` (cùng đường với `{x:Type Button}`) nên tự ăn template mới; chỉ sửa
  comment cho khỏi nói sai.

### 5. File đã tạo / sửa / xoá

| File | Việc |
|---|---|
| `suite/Shopee.Suite/Themes/Theme.xaml` | **SỬA (+167 dòng)** — style `comboItem` + template ComboBox phẳng thay khối "giữ template mặc định"; sửa 1 tiêu đề mục DataGrid nói "port ở đợt sau" |
| `suite/Shopee.Suite/Modules/Data/DataView.xaml` | **SỬA** — 5 × `Mode=OneWay` + comment bẫy |
| `suite/Shopee.Suite/Modules/Search/SearchView.xaml` | **SỬA** — hàng 190 → `* MinHeight=150 MaxHeight=190` + ghi số đo vào comment |
| `suite/Shopee.Suite/Views/PortingWindow.cs` | **XOÁ** (62 dòng, 0 lớp con) |
| `orders/XuLyDonShopee.App/Views/AccountsView.xaml` | **SỬA** — cột trái 340 → 360 + comment giải thích số đo |
| `orders/XuLyDonShopee.App/Views/OrdersView.xaml` | **SỬA** — cột Trạng thái 140 → 160 + `TextTrimming` + `ToolTip`; sửa comment watermark của combo |
| `orders/XuLyDonShopee.App/Styles/Controls.xaml` | **SỬA (comment)** — `fieldCombo` nay ăn template phẳng của suite |
| `orders/XuLyDonShopee.App/Behaviors/WatermarkAssist.cs` | **SỬA (xmldoc)** — câu "ComboBox dùng template mặc định của WPF" đã lỗi thời |
| `orders/XuLyDonShopee.App/XuLyDonShopee.App.csproj` | **SỬA (comment)** — bỏ câu "view port ở ĐỢT 5"; viết lại ghi chú R2R/WDAC theo bối cảnh WPF (mục 5.4) |
| `release-suite.sh`, `publish-suite.sh`, `install-linux.sh` | **XOÁ** (99 dòng) |
| `extensions/sync-shared.sh` | **SỬA (comment)** — bỏ câu "release-suite.sh gọi" (script đó không còn trên nhánh này) |
| `CHANGELOG.md` | **SỬA** — mục `## Chưa phát hành` thêm đoạn port WPF |

Không đụng dòng logic/ViewModel nào. `App.xaml.cs`, `Themes/Icons.xaml`, `release-suite.cmd`, `version.txt`:
**không sửa**.

**5.1 Rà `grep -i avalonia` trong 3 project UI:** 77 chỗ, **hầu hết là ghi chú port hợp lệ** dạng "Avalonia làm
X → WPF làm Y" (giữ nguyên, chúng là lời giải thích vì-sao-code-viết-thế). Chỉ 4 câu đã **sai sự thật**, đã sửa:
csproj orders ("view port ở đợt 5" + ghi chú R2R), `WatermarkAssist.cs` (ComboBox dùng template mặc định),
`OrdersView.xaml` (như trên), `Theme.xaml` (tiêu đề mục DataGrid "port ở đợt sau"). Không còn chỗ nào trong 3
project UI nói về việc port **chưa xong**.

**5.2 Grep tham chiếu 3 script Linux đã xoá:** chỉ 2 chỗ — `extensions/sync-shared.sh` (comment, đã sửa) và
`plans/*.md` (chính plan này + plan tổng — là lịch sử, giữ nguyên). `README.md` chỉ nhắc `publish-suite.cmd`;
`CLAUDE.md` chỉ nhắc `release-suite.cmd`. **Không có chỗ nào gãy.**

**5.3 `release-suite.cmd`:** giữ nguyên 100% (channel `win`, gọi `extensions\sync-shared.cmd --check`, publish
`-p:PublishReadyToRun=true`). Không đụng.

**5.4 R2R / WDAC:** ghi chú cũ (2026-07-14) dựa trên việc UI chạy bằng **bộ DLL Avalonia tải từ NuGet** — R2R
crossgen đổi hash các DLL bên thứ ba → mất uy tín ISG cloud → WDAC chặn `0x800711C7`. Nay UI là WPF (runtime
nằm trong .NET desktop chính chủ Microsoft, đã ký), NuGet bên thứ ba chỉ còn CommunityToolkit.Mvvm + Velopack
⇒ ràng buộc **nhiều khả năng hết áp dụng**. Nhưng tôi **KHÔNG đo lại được** (cần đúng máy bật WDAC UMCI
Enforced) nên **giữ nguyên quyết định cũ** và chỉ viết lại ghi chú cho đúng bối cảnh + nêu rõ phải kiểm ở lần
phát hành thật. Đây là chỗ plan tổng (QĐ 20) hẹn "kiểm lại ở đợt cuối" — **chưa kiểm được, vẫn nợ**.

### 6. Điểm trệch plan / còn nợ

1. **Mốc 1000×700 của bước 2 KHÔNG đạt trọn.** Sửa đúng như plan yêu cầu (190 → sao có kẹp) và có cải thiện
   đo được ở mốc 1000 (ô log từ ngoài cửa sổ vào trong), nhưng ở ~860px trở xuống nội dung vẫn tràn vì thẻ
   nhập + ô log là hai khối không co được. Cần ScrollViewer cho cả tab — **cố ý không tự làm** (plan cấm mở
   rộng). Xem đề xuất 7.1.
2. **Sửa thêm 2 dòng comment ngoài danh sách bước 3** (`Theme.xaml` tiêu đề mục DataGrid, `extensions/sync-shared.sh`)
   — đều là hệ quả trực tiếp của bước 3/bước 4 (câu nói sai sau khi xoá/port xong), không đụng hành vi.
3. **Nâng cách ly cho rig đợt 2/3/4** (file trong scratchpad, KHÔNG thuộc repo): thêm hồ sơ người dùng giả
   (USERPROFILE/APPDATA/LOCALAPPDATA/TEMP) + `SHOPEESUITE_SOFTWARE_RENDER=1` + in vân tay `app.db` production
   và số Brave trước/sau, đúng chuẩn đợt 5 như prompt yêu cầu. `verify-dot4.ps1` thêm tham số `-Width/-Height`
   để kiểm bố cục ở cửa sổ thấp. Trước đó 3 rig này chạy bằng `Start-Process` kế thừa môi trường thật —
   `--mode workspace` **vẫn gọi `BraveFleet.StartupSweep()`** (`App.xaml.cs` dòng 59) nên đáng phải cách ly.
4. **`ToolTip` mới trên pill trạng thái** (mục 4.2) là bổ sung nhỏ so với nguồn — cần thiết vì trạng thái là
   chuỗi tự do, cắt "…" mà không có cách đọc đủ thì tệ hơn.
5. **Vẫn chưa quan sát được bằng mắt** (nợ từ đợt 3/5, cần Hub sống hoặc phiên trình duyệt thật): badge ✓ "đã
   xong" + nút "Dừng việc shop này" (Workspace); vòng quay cam cột tiến độ, chấm xanh "Chờ lấy: N", nút "Tải
   phiếu" thiếu file (Đơn hàng); trạng thái "đang chạy thật" của Search + dữ liệu Fleet từ Hub thật. Binding
   của chúng đều chạy (0 lỗi log) nhưng chỉ smoke-test chế độ Full với máy thật mới soi được.
6. **Không bump `version.txt`** (đúng plan) và **không commit** (đúng plan). 3 file `.sh` đã xoá đang ở trạng
   thái **staged** (do dùng `git rm`), các file còn lại unstaged — Fable stage nốt khi commit.

### 7. Đề xuất (Fable quyết)

1. **Màn Search ở cửa sổ nhỏ:** nếu người dùng thật sự chạy app ở cửa sổ < 900px thì nên bọc nội dung tab
   "Tìm kiếm" trong `ScrollViewer` (đặt `MinHeight` cho lưới trong để hàng sao vẫn hoạt động khi cửa sổ cao).
   Là đổi kết cấu → nên là plan riêng, không nhét vào đợt đánh bóng.
2. **Trợ năng dải tab theo link** (nợ đợt 4 mục 6.8): thêm `AutomationProperties.Name="{Binding Header}"` cho
   TabItem có header DataTemplate (Search + Workspace). 2 dòng, không rủi ro, nhưng ngoài danh sách bước của
   plan nên tôi không tự làm.
3. **Cột "ĐVVC" của lưới Đơn hàng cũng bị cắt** ("Giao Hàng Nh" ở cột 110px) — cùng họ với lỗi pill nhưng
   **không nằm trong danh sách 4 lỗi** của plan nên tôi không tự xử (đúng luật "thấy gì ngoài danh sách thì
   ghi vào báo cáo"). Sửa được bằng `ToolTip` + nới cột hoặc `TextTrimming` nếu Fable muốn.
4. **`SHOPEESUITE_SOFTWARE_RENDER` nên GIỮ** — máy dev này không "present" được cửa sổ WPF mới nếu thiếu nó
   (đợt 4 đã loại trừ nguyên nhân do code), và mọi rig nghiệm thu về sau đều cần. Mặc định tắt nên bản phát
   hành không đổi hành vi.
5. **Trước khi phát hành 1.7.0:** (a) smoke-test chế độ Full trên máy thật để đóng 5 món nợ "chưa soi được"
   ở mục 6.5; (b) đo lại R2R trên máy WDAC rồi cập nhật ghi chú csproj (mục 5.4); (c) chạy `vpk pack` thử để
   chắc client cũ channel `win` update lên được (tiêu chí nghiệm thu toàn cục của plan tổng, chưa ai kiểm).

### 8. Cách chạy lại các rig (cho lần nghiệm thu sau)

```powershell
$s = '<scratchpad>\86f7fb17-…\scratchpad'
& "$s\verify-modals2.ps1"  -Tag <hậu-tố>                       # đợt 2: 2 hộp thoại
& "$s\verify-dot3.ps1"     -Tag <hậu-tố>                       # đợt 3: Workspace + ScrapeStatsWindow
& "$s\verify-dot4.ps1"     -Tag <hậu-tố> [-Tall] [-Width W -Height H]   # đợt 4: Search/Fleet/CheckAcc
& "$s\verify-dot5.ps1"     -Tag <hậu-tố> -Mode shopee|workspace [-NoSeed]  # đợt 5: orders + Cài đặt
& "$s\verify-combo.ps1"    -Tag <hậu-tố>                       # MỚI: template ComboBox (mở/chọn/gõ)
& "$s\measure-search-rows.ps1" -Tag <hậu-tố>                   # MỚI: đo chiều cao hàng tab "Tìm kiếm"
& "$s\crop.ps1" -In <ảnh> -Out <ảnh> -X .. -Y .. -W .. -H .. -Zoom 2   # MỚI: cắt vùng làm ảnh trước/sau
```
Cả 6 rig đều tự tạo/xoá `data-dir.txt`, dựng hồ sơ người dùng giả, đặt `SHOPEESUITE_SOFTWARE_RENDER=1`
(thiếu là ảnh **trắng trơn** trên máy này), in số dòng binding log sau TỪNG bước, in vân tay `app.db`
production + số Brave trước/sau, và chỉ đóng đúng PID nó mở. **Cấm `--mode full`** — `StartupSweep` chạy cả ở
`--mode shopee` LẪN `--mode workspace`.
