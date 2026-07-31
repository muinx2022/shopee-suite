# Plan: Màn Cài đặt chia TAB (mỗi phần 1 tab) + gỡ sạch chữ webhook/Slack

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Màn Cài đặt hiện tại (`suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.xaml` — WPF, bản v1.7.0)
là MỘT cột dọc cuộn qua 5 section. Người dùng xem xong nói "không thấy gì thay đổi" so với bản cũ (phần đầu
màn vốn giống) và chốt yêu cầu mới:

1. **Mỗi phần đặt vào 1 tab** — TabControl thay cho cột dọc. User chốt thêm (tin nhắn bổ sung): KHÔNG tách
   riêng 2 tab kiểu cũ "Hiệu năng" / "Đồng bộ nhiều máy" — GỘP chung thành MỘT tab. Kết quả **4 tab**:
   *Chế độ ứng dụng · Phiên bản & cập nhật · Hiệu năng & Đồng bộ · Đơn hàng*.
2. **Loại bỏ phần webhook tới Slack** — card webhook đã bỏ từ trước, nhưng CHỮ nhắc webhook vẫn còn (ít
   nhất: dòng caption dưới tiêu đề "…Cấu hình AI, prompt và webhook thông báo đặt trên Hub"). Gỡ sạch mọi
   chữ "webhook"/"Slack" khỏi màn Cài đặt.

Máy đang chạy bản production v1.7.0 (WPF) — app này build từ `main` hiện tại, làm trực tiếp trên cây chính.

## 2. Phạm vi

- **Làm:** viết lại bố cục `UnifiedSettingsView.xaml` (+ `.xaml.cs` nếu cần) thành TabControl; gỡ chữ
  webhook/Slack trong màn Cài đặt; CHANGELOG mục mới (không bump version — điều phối bump khi phát hành).
- **Không làm:** không đổi `UnifiedSettingsViewModel`/`SettingsViewModel` (2 bên) — mọi binding giữ nguyên;
  không đụng Theme.xaml trừ khi style TabItem hiện có thiếu gì đó cho màn này; không đụng nhánh `avalonia`.

## 3. Các bước thực hiện

1. Đọc `UnifiedSettingsView.xaml` hiện tại + style TabControl/TabItem trong `Themes/Theme.xaml` (đã có từ
   đợt port, active = gạch chân cam — LƯU Ý 2 bẫy đã ghi trong báo cáo plan đợt 4: TabItem giữ
   ContentAlignment=Stretch, KHÔNG đặt Foreground/FontWeight trên TabItem).
2. Bố cục mới:
   - Hàng đầu: TextBlock "Cài đặt" `h1` + chip trạng thái `Suite.Status` (giữ như cũ); caption dưới tiêu đề
     VIẾT LẠI ngắn gọn, KHÔNG nhắc webhook (vd: "Chế độ ứng dụng · cập nhật · hiệu năng · đồng bộ nhiều máy
     · Đơn hàng").
   - Dưới: `TabControl` chiếm phần còn lại, 4 TabItem theo thứ tự: **Chế độ ứng dụng** / **Phiên bản & cập
     nhật** / **Hiệu năng & Đồng bộ** / **Đơn hàng**. Nội dung mỗi tab = đúng các card của section tương
     ứng hiện tại, chuyển NGUYÊN KHỐI (binding giữ nguyên từng chữ). Tab "Hiệu năng & Đồng bộ" chứa đủ 4
     card (Tài nguyên → trần Brave · Máy của bạn · Máy này · Kết nối Hub) — xếp lưới 2 cột cho cân, bọc
     ScrollViewer dọc; tab "Đơn hàng" cũng bọc ScrollViewer.
   - Ẩn tab theo chế độ: "Hiệu năng & Đồng bộ" `Visibility` theo `ShowsWorkspaceSettings`; "Đơn hàng" theo
     `HasOrders` (converter BoolToVis có sẵn). Tab mặc định chọn = "Chế độ ứng dụng" (luôn hiện ở mọi chế
     độ — an toàn khi các tab khác Collapsed).
3. Gỡ chữ webhook/Slack: grep `webhook`/`slack` (không phân biệt hoa thường) trong
   `suite/Shopee.Suite/Modules/Settings/` + phần chữ tĩnh các card Đơn hàng trong màn này → xoá/viết lại
   câu cho tự nhiên. KHÔNG đụng code backend (OrderNotifyService…) — chỉ chữ trên màn Cài đặt.
4. Build `dotnet build ShopeeSuite.sln` 0 error 0 warning; `dotnet test` 2 project xanh.
5. Chạy thử CÁCH LY (bản production v1.7.0 ĐANG CHẠY — tuyệt đối không đụng):
   - `data-dir.txt` cạnh exe build ra + redirect USERPROFILE/APPDATA/LOCALAPPDATA/TEMP sang hồ sơ giả có
     thật (quy trình chuẩn trong báo cáo plan đợt 5 — `plans/2026-07-31-port-wpf-dot5-orders-caidat.md`);
     KHÔNG `--mode full`; không bấm nút chạy job; chỉ đóng đúng PID mình mở; `SHOPEESUITE_SOFTWARE_RENDER=1`
     khi chụp.
   - Chạy `--mode shopee`: thấy 3 tab (Chế độ / Phiên bản / Đơn hàng); `--mode workspace`: thấy 3 tab
     (Chế độ / Phiên bản / Hiệu năng & Đồng bộ). Chụp TỪNG tab ở cả 2 chế độ; `SHOPEESUITE_BINDING_LOG`
     = 0 dòng.
   - Grep ảnh/cây UIA: không còn chữ "webhook" trong màn.
6. Điền "Báo cáo thực thi" + cập nhật CHANGELOG (mục "Chưa phát hành": màn Cài đặt chia tab, mỗi phần một
   tab; gỡ chữ webhook còn sót).

## 4. Tiêu chí nghiệm thu

- [ ] Build 0/0; test 1459 + 61 xanh.
- [ ] Màn Cài đặt là TabControl 4 tab (Hiệu năng + Đồng bộ nhiều máy GỘP 1 tab), ẩn/hiện đúng theo chế độ;
      mọi nút/ô nhập cũ đủ nguyên vẹn trong tab tương ứng (đối chiếu binding từng section với bản trước).
- [ ] Grep `webhook|slack` trong `Modules/Settings/*.xaml` = 0 kết quả.
- [ ] Binding log 0 dòng ở cả 2 chế độ chạy thử; không sót file tạm/process; production không bị đụng.

## 5. Rủi ro & lưu ý

- Tab "Đơn hàng" chứa binding `Orders.*` với `Orders` có thể null (workspace mode) — tab Collapsed nhưng
  binding classic vẫn phải im lặng (0 lỗi log) như bản hiện tại.
- TabControl là control ĐÃ có style chuẩn trong theme — đừng chế style mới; nếu tab quá dài do 5 nhãn thì
  giữ nhãn ngắn gọn như liệt kê ở bước 2.
- KHÔNG commit — điều phối commit sau nghiệm thu.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Ngày:** 2026-07-31 · **Người thực thi:** Opus (`opus-executor`) · **Nơi làm:** cây chính
`d:\Projects\shopee-suite` (nhánh `main`) · **CHƯA commit** (theo plan).

### 1. Kết quả kiểm chứng (số liệu thật)

| Hạng mục | Lệnh | Kết quả |
|---|---|---|
| Build | `dotnet build ShopeeSuite.sln --no-incremental` | **Build succeeded — 0 Warning(s), 0 Error(s)** (19,01 s) |
| Test core | `dotnet test suite\Shopee.Core.Tests --no-build` | **Passed 61 / Failed 0 / Skipped 0** |
| Test orders | `dotnet test orders\XuLyDonShopee.Tests --no-build` | **Passed 1459 / Failed 0 / Skipped 0** |
| Grep `webhook\|slack` trong `Modules/Settings/` (cả .xaml LẪN .cs) | `grep -rin` | **0 kết quả** (exit 1) |
| Grep `webhook\|slack` trong cây UIA lúc chạy | 9 file dump `uia-*.txt` (mọi tab × 2 chế độ) | **0/9 file có kết quả** |
| Lỗi binding runtime | `SHOPEESUITE_BINDING_LOG`, 4 lượt chạy (`t2` shopee · `t3`/`w1` workspace · `w2` workspace-không-`--mode`) | **0 dòng** sau TỪNG bước, ở CẢ 4 lượt |
| Đóng app | WM_CLOSE cho cửa sổ chính | **ExitCode 0** ở cả 4 lượt |

### 2. File đã sửa

| File | Việc |
|---|---|
| `suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.xaml` | **VIẾT LẠI BỐ CỤC** — ScrollViewer + cột dọc 5 section → `TabControl` 4 `TabItem`; nội dung từng card chuyển nguyên khối |
| `suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.xaml.cs` | **SỬA 2 dòng doc-comment** (mô tả 4 tab). Không thêm code-behind — vẫn thuần binding |
| `suite/Shopee.Suite/Modules/Settings/UnifiedSettingsViewModel.cs` | **SỬA 1 doc-comment**: bỏ chữ "webhook" trong `<summary>` của property `Orders` (bước 3 của plan yêu cầu grep cả thư mục; chữ này còn sót và đã SAI — `SettingsViewModel` của orders không còn cấu hình webhook). **KHÔNG đụng property/command/binding nào.** |
| `CHANGELOG.md` | **THÊM mục "Chưa phát hành"** (chia tab + gỡ chữ webhook). KHÔNG bump `version.txt` |

### 3. Bố cục mới (đúng bước 2 của plan)

- Header giữ nguyên: `TextBlock "Cài đặt"` (h1) + chip `Suite.Status`; caption viết lại thành
  *"Chế độ ứng dụng · cập nhật · hiệu năng · đồng bộ nhiều máy · Đơn hàng. Cấu hình AI và prompt đặt trên Hub."*
- `TabControl Background=Transparent BorderThickness=0` (đúng quy ước FleetView/SearchView của đợt 4), mỗi tab
  bọc `ScrollViewer` dọc, nội dung `Margin="0,12,0,0"`:
  1. **Chế độ ứng dụng** — 1 card (combo chế độ + nút "Lưu & khởi động lại" + nút tạo shortcut). Luôn hiện.
  2. **Phiên bản & cập nhật** — 1 card `MaxWidth=560` (y hệt bản cũ). Luôn hiện.
  3. **Hiệu năng & Đồng bộ** — `Visibility` theo `ShowsWorkspaceSettings`; lưới 2 cột `1.15* / 14 / 1*`, mỗi cột
     có nhãn nhỏ (`sectionLabel`) để phân biệt 2 phần cũ: **trái = HIỆU NĂNG** (card tài nguyên→trần Brave +
     card "Máy của bạn"), **phải = ĐỒNG BỘ NHIỀU MÁY** (card "Máy này" + card "Kết nối tới Hub"). Đủ 4 card.
  4. **Đơn hàng** — `Visibility` theo `HasOrders`; giữ nguyên lưới `* / 14 / *` (trái: Tự động hoá + Trình duyệt;
     phải: Đồng bộ Google Sheet).
- Tab mặc định (`SelectedIndex=0`) là "Chế độ ứng dụng" — luôn hiện ở mọi chế độ nên không bao giờ rơi vào tab
  Collapsed.

### 4. Bằng chứng "không sót nút/ô nhập nào" (đối chiếu máy móc, không phải nhìn bằng mắt)

So bản `HEAD` với bản mới bằng cách rút danh sách `{Binding …}` (đã bỏ phần `Converter=`) và sắp xếp:

```
cu: 61 binding | moi: 57 binding
DIFF (chỉ có 4 dòng, đều là gate Visibility bị TRÙNG ở bản cũ):
< {Binding HasOrders}              (cũ 2 chỗ: nhãn section + Grid → nay 1 chỗ trên TabItem)
< {Binding ShowsWorkspaceSettings} ×3  (cũ 4 chỗ: 2 nhãn + 2 Grid → nay 1 chỗ trên TabItem)
```

Tức **57/57 binding còn lại giống hệt từng chữ** (mọi `Suite.*` và `Orders.*`, mọi `Command`, mọi `StringFormat`,
`Mode=OneWay` của 2 `<Run>`). Đối chiếu tiếp chuỗi tĩnh:

- `Content=` / `ToolTip=` / `b:WatermarkAssist.Watermark=`: **diff RỖNG** (không mất tooltip/gợi ý mờ nào).
- `Text="…"` tĩnh: chỉ khác 4 dòng — bỏ 3 nhãn section giờ trùng với nhãn tab (`CHẾ ĐỘ ỨNG DỤNG`,
  `PHIÊN BẢN & CẬP NHẬT`, `ĐƠN HÀNG`) và viết lại caption đầu màn. Hai nhãn `HIỆU NĂNG` / `ĐỒNG BỘ NHIỀU MÁY`
  **được giữ** làm nhãn cột trong tab gộp.

### 5. Nghiệm thu bằng mắt (rig UIAutomation, chạy CÁCH LY)

Script `…\86f7fb17-…\scratchpad\verify-caidat-tab.ps1` (viết mới, kế thừa `verify-dot5.ps1`). Cách ly KÉP đúng
bước 5: `data-dir.txt` cạnh exe dev + `USERPROFILE`/`APPDATA`/`LOCALAPPDATA`/`TEMP`/`TMP` của TIẾN TRÌNH CON trỏ
hồ sơ giả CÓ THẬT trong scratchpad; `SHOPEESUITE_SOFTWARE_RENDER=1`; **không** `--mode full`; **không** bấm nút
chạy job nào (chỉ đổi tab + chụp); đóng đúng PID mình mở.

| Ảnh (trong scratchpad) | Nội dung đã soi |
|---|---|
| `tab-shopee-1-Chế-độ-ứng-dụng-t2.png` | Dải 3 tab **Chế độ ứng dụng \| Phiên bản & cập nhật \| Đơn hàng** (tab 1 chữ cam + gạch chân); card combo "Chỉ Shopee — đơn hàng", dòng "khoá bởi shortcut", nút "Tạo shortcut cho chế độ này" |
| `tab-shopee-2-Phiên-bản---cập-nhật-t2.png` | Card `Phiên bản: v1.7.0` (mono) + dòng ℹ "chỉ tự cập nhật khi cài qua Velopack" (đúng vì chạy từ thư mục build) |
| `tab-shopee-3-Đơn-hàng-t2.png` | 2 cột: trái *Tự động hóa* (thư mục hoá đơn + nút Chọn…, Chu kỳ = 30, nút Lưu) và *Trình duyệt* (combo + "Đang dùng: Chrome (…)" + nút Lưu); phải *Đồng bộ Google Sheet* (3 ô có gợi ý mờ + nút Lưu). Có thanh cuộn dọc |
| `tab-workspace-1-Chế-độ-ứng-dụng-w1.png` | Dải 3 tab **Chế độ ứng dụng \| Phiên bản & cập nhật \| Hiệu năng & Đồng bộ** — tab *Đơn hàng* **ẩn đúng** (Orders null) |
| `tab-workspace-2-Phiên-bản---cập-nhật-w1/w2.png` | Y hệt tab 2 của chế độ Shopee (không bị gate theo chế độ) |
| `tab-workspace-3-Hiệu-năng---Đồng-bộ-w1.png` | **Tab GỘP**: cột trái nhãn `HIỆU NĂNG` + card "Tài nguyên… → tính số cửa sổ Brave tối đa" (CPU 10/20, RAM 32/32, dải "→ Tối đa 10 cửa sổ Brave", nút Lưu) + card "Máy của bạn"; cột phải nhãn `ĐỒNG BỘ NHIỀU MÁY` + card "Máy này" (tên máy mono + ô tên hiển thị + "Lưu tên") + card "Kết nối tới Hub" (checkbox, URL, token, 3 nút + "Đẩy cấu hình lên Hub"). **Đủ 4 card, ẩn tab Đơn hàng** |
| `tab-workspace-1-Chế-độ-ứng-dụng-w2.png` | Lượt chạy KHÔNG kèm `--mode` (đặt chế độ qua `app-mode.json` trong KHO TẠM) ⇒ `ModeLockedByArg=false`: **nút "Lưu & khởi động lại" hiện đủ** và dòng "khoá bởi shortcut" ẩn — chứng minh cả 2 nhánh Visibility của tab 1 |

**An toàn (rig tự đo trước/sau MỖI lượt):**

| Kiểm chứng | Trước | Sau (cả 4 lượt) |
|---|---|---|
| `%APPDATA%\XuLyDonShopee\app.db` production | 1.339.392 bytes · ghi 2026-07-31 01:23:12 · sha256 `4BDF62F41740E903` | **Y HỆT** |
| Số tiến trình Brave | 8 | **8** |
| Cổng cầu nối 47821 do PID dev mở | — | **0 dòng netstat** |
| Tiến trình ShopeeSuite còn lại | 33568 (production, `…\AppData\Local\ShopeeSuite\current\`) | **33568** — production không bị đụng |
| `data-dir.txt` cạnh exe dev | — | **đã xoá** (rig xoá trong `finally`) |
| Thư mục tạm | 5 × `tab-data-*`, 5 × `tab-home-*` | **đã xoá hết** |

Ghi chú: file `shopeesuite-crash.log` trong hồ sơ giả KHÔNG phải crash — nội dung là
`StartupJanitor … Bỏ qua dọn dẹp khởi động: có ShopeeSuite khác đang chạy`, tức chính chốt an toàn của app đã
chặn dọn dẹp khi thấy bản production đang chạy (app vẫn thoát ExitCode 0).

### 6. Điểm trệch plan / ghi chú

1. **Bỏ 3 nhãn section trùng nhãn tab** (`CHẾ ĐỘ ỨNG DỤNG`, `PHIÊN BẢN & CẬP NHẬT`, `ĐƠN HÀNG`): tab đã là nhãn
   của phần đó, để lại thì trên màn có 2 lần cùng một chữ chồng nhau. Giữ 2 nhãn `HIỆU NĂNG` /
   `ĐỒNG BỘ NHIỀU MÁY` vì tab gộp cần phân biệt 2 nhóm. Style `sectionLabel` giữ nguyên, chỉ đổi `Margin`
   `0,22,0,10` → `0,0,0,10` (khoảng cách trên đã do `Margin="0,12,0,0"` của nội dung tab lo).
2. **Sửa 1 doc-comment trong `UnifiedSettingsViewModel.cs`** dù plan ghi "không đổi VM": bước 3 yêu cầu grep cả
   thư mục `Modules/Settings/`, và chữ "webhook" ở đó vừa sót vừa sai. Chỉ là comment — 0 thay đổi hành vi,
   0 thay đổi binding. Nếu Fable muốn giữ nguyên VM tuyệt đối thì hoàn nguyên 1 dòng này là xong.
3. **Chữ "webhook" còn ở NƠI KHÁC (cố ý không đụng, đúng phạm vi plan):**
   `suite/Shopee.Core/Coordination/{HubClient,HubRoutes,OrderDtos}.cs`,
   `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.HubPush.cs`,
   `orders/XuLyDonShopee.App/ViewModels/SettingsViewModel.cs` (doc-comment lớp). Đều là backend/ghi chú kỹ
   thuật, KHÔNG hiện trên màn Cài đặt.
4. **Bẫy TabItem của đợt 4 KHÔNG phải sửa lại:** style `TabItem` trong `Themes/Theme.xaml` đã có sẵn
   `Horizontal/VerticalContentAlignment=Stretch` và đặt `TextElement.*` trên Border trong template. Ảnh chụp
   xác nhận: chữ trong thân tab **không** bị cam/in đậm, nội dung **không** co về góc trên-trái. Không đụng
   `Theme.xaml`.
5. **Lợi thêm (không phải yêu cầu):** WPF chỉ dựng nội dung của tab ĐANG CHỌN, nên ở chế độ Workspace cây bind
   `Orders.*` thậm chí không được tạo (trước đây vẫn tạo rồi ẩn bằng `Visibility`).
6. **Nit CÓ TỪ TRƯỚC, không phải do đợt này:** nút "Kết nối ngay" hiện 2 glyph (icon Sync + một vệt nhỏ) — đối
   chiếu ảnh cũ `d5-7b-caidat-workspace-cuoi-d6r5w.png` thấy y hệt. Nếu muốn sửa thì làm việc riêng.
7. **Rig `verify-caidat-tab.ps1` phải bỏ cách chuyển tab bằng `Ctrl+N`** (SendKeys không tới được cửa sổ trong
   rig này — lượt `t1`/`t3` đứng nguyên ở màn cũ); đổi sang tìm phần tử tên "Cài đặt" rồi
   Invoke/Select, kèm chốt chặn: nếu tab đầu tiên đọc được không phải "Chế độ ứng dụng" thì `throw` (tránh
   chụp nhầm màn khác rồi báo đạt như lượt `t3`).

### 7. Cách chạy lại rig

```powershell
& '<scratchpad>\verify-caidat-tab.ps1' -Tag <hậu-tố> -Mode shopee
& '<scratchpad>\verify-caidat-tab.ps1' -Tag <hậu-tố> -Mode workspace
& '<scratchpad>\verify-caidat-tab.ps1' -Tag <hậu-tố> -Mode workspace -ViaFile   # bỏ --mode để hiện nút "Lưu & khởi động lại"
```
Script tự: chụp vân tay `app.db` production trước/sau, tạo/xoá `data-dir.txt`, dựng hồ sơ người dùng giả, duyệt
LẦN LƯỢT mọi tab của màn Cài đặt (chụp + dump cây UIA + in số dòng binding log sau từng tab), kiểm netstat 47821
+ số tiến trình Brave, và đóng đúng PID nó mở.
