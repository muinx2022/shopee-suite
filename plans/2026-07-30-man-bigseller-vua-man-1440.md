# Plan: Màn Cấu hình BigSeller gọn lại trên màn 1440×900

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành (Bước 5 dùng phương án dự phòng — hạn chế Avalonia)
- **Người lập:** Fable · **Người thực thi:** opus-dev

## 1. Bối cảnh & mục tiêu

Sau khi v1.6.15 sửa xong lỗi cửa sổ tràn màn, user chạy trên máy 1440×900 (maximized) và báo màn
**Cấu hình BigSeller** nhìn xấu. User chốt 3 loại vấn đề: **(a)** bị cắt / chữ cụt / dính viền,
**(b)** thừa chỗ trống, phân bổ lệch, **(c)** phải cuộn mới thấy hết.

### Số đo thật (quy từ ảnh chụp màn 1440×900 maximized, taskbar 40px)

Chiều dọc, từ trên xuống trong vùng nội dung:

| Vùng | Cao (px thật) | Ghi chú |
|---|---|---|
| Tab + ribbon | ~173 | không đụng |
| Header "Cấu hình BigSeller" | ~45 | không đụng |
| **Khung cấu hình (card)** | **~351** | trừ `Padding="18"` hai đầu → form chỉ còn **~315** |
| **Ô nhật ký** | **~192** | `Height="180"` cứng + `Padding="6"` hai đầu |
| Thanh trạng thái | ~28 | không đụng |

Form cột trái (hub-mode) cao khoảng **450px** nhưng chỉ được cấp **315px** → `ScrollViewer` ở
`BigSellerView.axaml:110` bật thanh cuộn, nút **"Xóa Medias"** và chú thích của nó nằm dưới tầm nhìn.
Thanh cuộn còn ăn thêm ~12px bề ngang làm ô "File cookie BigSeller" bị cắt chữ.

Chiều ngang: 1440 − margin 56 = 1384; cột trái danh sách 300 + gap 14 → phần chi tiết 1070; trừ
`Padding="18"` hai đầu → 1034; chia `1*,18,1.1*` → **form ~484**, **shops ~532**.

### Mục tiêu

1. Ở 1440×900 maximized, **hub-mode không phải cuộn** — thấy trọn form kể cả nút "Xóa Medias".
2. Hết chữ cụt: cột **"Batch"** trong bảng Shops đang hiện thành **"Ba"**.
3. Hết cột trống vô nghĩa: cột **"Sheet"** luôn rỗng khi tài khoản dùng kho Hub.
4. Không phá excel-mode và không đổi hành vi/logic — **thuần layout**.

## 2. Phạm vi

### Làm

Chỉ 2 file:

- `suite/Shopee.Suite/Modules/BigSeller/BigSellerView.axaml`
- `suite/Shopee.Suite/Modules/BigSeller/BigSellerView.axaml.cs` (chỉ cho Bước 5)

### Không làm

- Không đụng màn khác (Workspace / Shopee / Cài đặt) — user chỉ nêu màn này.
- Không đổi ViewModel, không đổi binding dữ liệu, không thêm/bớt chức năng.
- Không bỏ nút nào, không đổi Command/Classes của nút.
- Không thêm GridSplitter / không cho người dùng kéo chỉnh tỉ lệ (quá tầm lần này).
- Không bump version trong lượt này — phiên chính lo phát hành sau khi nghiệm thu.

## 3. Các bước thực hiện

Ngân sách cần thu hồi: **~135px** chiều dọc (450 cần − 315 có). Bốn bước dưới đây gộp lại thu hồi
~164px, dư an toàn.

### Bước 1 — Ô nhật ký: 180 → 110 (thu hồi ~70px)

`BigSellerView.axaml:344` — `Height="180"` → `Height="110"`.

Ô này hiện trống rỗng phần lớn thời gian mà chiếm 192px. 110px vẫn đủ đọc ~7 dòng log
(`FontSize="11.5"`), và `TextBox` bên trong vốn đã tự cuộn.

### Bước 2 — Bỏ 2 chú thích trùng lặp với ToolTip (thu hồi ~60px)

Hai `TextBlock Classes="caption"` này lặp lại đúng nội dung đã có trong `ToolTip.Tip` của chính nút
ngay phía trên, mỗi cái chiếm ~34px vì `TextWrapping="Wrap"` ở cột hẹp 484px:

- `BigSellerView.axaml:201-202` — "Có proxy → đăng nhập qua proxy đó; không → IP máy. Cookie tự lưu."
- `BigSellerView.axaml:222-223` — "Xóa Medias: dọn sạch thư viện ảnh khi kho đầy…"

**Xóa 2 `TextBlock` đó.** Đổi lại, **chuyển nguyên văn nội dung vào `ToolTip.Tip`** của nút tương ứng
để không mất thông tin:

- Nút "Mở Profile Bigseller" (`:186`) hiện **chưa có** `ToolTip.Tip` → thêm mới, dùng đúng câu vừa xóa.
- Nút "Xóa Medias" (`:205`) đã có `ToolTip.Tip` → **nối thêm** câu vừa xóa vào cuối, cách bằng dấu chấm
  và khoảng trắng (đừng ghi đè mất câu cũ).

### Bước 3 — Gộp 4 nút hành động về 1 hàng (thu hồi ~34px)

Hiện có 2 `StackPanel Orientation="Horizontal"` liên tiếp (`:185-200` và `:204-221`):
hàng 1 = "Mở Profile Bigseller" + "Đóng", hàng 2 = "Xóa Medias" + "Dừng".

Gộp thành **một** `WrapPanel Orientation="Horizontal"` chứa cả 4 nút theo thứ tự cũ. Dùng `WrapPanel`
(không phải `StackPanel`) để khi cột hẹp hơn dự tính thì nút tự xuống dòng thay vì bị cắt.

- Giữ nguyên `Classes`, `Command`, `MinWidth`, `IsVisible` của từng nút.
- Bỏ `Margin="10,0,0,0"` rời rạc trên từng nút, thay bằng khoảng cách đều: đặt
  `ItemSpacing`/`Margin` sao cho các nút cách nhau 8–10px và cách nhau 6px khi xuống dòng
  (`WrapPanel` không có `Spacing` — dùng `Margin="0,0,8,6"` trên từng nút con là đủ).
- Bốn nút cộng lại ~150+80+150+80 + khoảng cách ≈ 490px > 484px của cột → **sẽ xuống 2 dòng ở 1440**.
  Chấp nhận được (vẫn đúng 2 dòng như hiện tại) NHƯNG để tiết kiệm thật sự, hạ `MinWidth` của 2 nút
  chính từ **150 → 132** và 2 nút phụ từ **80 → 74**; tổng còn ~436px → **lọt 1 dòng**. Kiểm lại chữ
  trong nút không bị cắt sau khi hạ (chữ dài nhất: "Mở Profile Bigseller").

### Bước 4 — Cột "Batch" trong bảng Shops hết cụt (lỗi cắt chữ)

`BigSellerView.axaml:263` — `Width="60"` không đủ cho tiêu đề "Batch" + mũi tên sắp xếp nên hiện ra
**"Ba"**. Đổi thành `Width="78"`.

### Bước 5 — Ẩn cột "Sheet" khi tài khoản dùng kho Hub (bỏ cột trống)

`BigSellerView.axaml:262` — cột `Sheet` bind `SheetDisplay`, mà hub-mode để trống có chủ ý
(comment dòng 261: "Hub-mode: cột để TRỐNG… không phô GUID ngăn"). Kết quả: một cột chiếm `1.4*`
(~1/3 bảng) luôn rỗng — đúng cái "thừa chỗ trống" user than.

`DataGridColumn` **không nằm trong visual tree** nên `{Binding}` trên `IsVisible` của nó không ăn
DataContext. Vì vậy làm ở code-behind:

1. Đặt `x:Name="ShopSheetColumn"` cho cột Sheet (`:262`).
2. Trong `BigSellerView.axaml.cs`: theo dõi tài khoản đang chọn và gán
   `ShopSheetColumn.IsVisible = <tài khoản KHÔNG dùng kho Hub>`.
   - Thuộc tính có sẵn ở VM tài khoản: `IsHubData` (dùng ở `:151`) và `CanPickWorkbook` (dùng ở `:130`)
     — **đọc code VM trước** rồi chọn đúng cái, đừng đoán. Excel-mode phải thấy lại cột Sheet.
   - Bắt cả 2 thời điểm: (a) đổi tài khoản đang chọn, (b) chính tài khoản đó đổi chế độ dữ liệu
     (`PropertyChanged`). Nhớ **gỡ handler** khỏi tài khoản cũ khi đổi chọn, kẻo rò bộ nhớ.
   - Chưa chọn tài khoản nào → ẩn hay hiện đều được (bảng rỗng), miễn không ném exception.
3. Khi ẩn cột Sheet, 3 cột còn lại (`1.4*`, `78`, `1.4*`) tự giãn — không cần chỉnh thêm.

**Nếu bước này vướng** (API `IsVisible` của `DataGridColumn` không có trong bản Avalonia đang dùng,
hoặc không tìm được thuộc tính VM tương ứng): **dừng lại, đừng chế biến thêm**, mà hạ `Width` cột Sheet
từ `1.4*` xuống `0.8*` rồi ghi rõ trong báo cáo là đã dùng phương án dự phòng.

### Bước 6 — Ô đường dẫn dài hết dính viền

`BigSellerView.axaml:157` (ô "File cookie BigSeller") và `:132` (ô Workbook) là `TextBox` chỉ-đọc chứa
đường dẫn dài, luôn bị cắt ở cột 484px. Thêm `ToolTip.Tip="{Binding CookieFile}"` (và
`ToolTip.Tip="{Binding WorkbookPath}"`) để rê chuột đọc được đường dẫn đầy đủ. Không đổi kiểu control
(vẫn cần bôi đen copy được).

## 4. Kiểm chứng

### Build

```text
dotnet build suite/Shopee.Suite/Shopee.Suite.csproj -c Debug
```

### Kiểm bằng mắt — BẮT BUỘC, không được bỏ qua

Máy dev màn to nhưng **vẫn giả lập được**: chạy app rồi **chỉnh cửa sổ về đúng 1424×844** (kích thước
mà máy 1440×900 nhận được sau khi clamp). Cách làm: chạy app, rồi từ PowerShell dùng
`MoveWindow`/`SetWindowPos` qua P/Invoke, hoặc thêm tạm 2 dòng gán `Width/Height` rồi bỏ đi sau khi
chụp. Chụp màn hình lại để đối chiếu.

Ở kích thước đó, tab **Cấu hình BigSeller** phải đạt:

1. Cột form bên trái **không có thanh cuộn dọc**; nhìn thấy nút "Xóa Medias" mà không phải cuộn.
2. Cột bảng Shops: tiêu đề đọc đủ chữ **"Batch"**, không còn "Ba".
3. Tài khoản dùng kho Hub: **không còn cột "Sheet"** rỗng.
4. Hàng 4 nút hành động nằm gọn, chữ trong nút không bị cắt.
5. Ô nhật ký thấp lại nhưng vẫn đọc được vài dòng và cuộn được.
6. Không ô nhập nào bị đè lên viền card.

### Không được làm hỏng

- Đổi sang một tài khoản **excel-mode** (có Workbook): cột "Sheet" phải **hiện lại**, cụm Workbook +
  nút "Sheet" vẫn đủ chỗ. Nếu excel-mode vẫn phải cuộn thì **chấp nhận** (form excel-mode dài hơn hẳn)
  — ghi vào báo cáo, đừng cắt bớt field để ép vừa.
- Bấm thử "Mở Profile Bigseller" / "Xóa Medias" xem lệnh vẫn gắn đúng (không cần chạy tới cùng, chỉ cần
  thấy app phản ứng / log nhả dòng).

## 5. Tiêu chí nghiệm thu

- [ ] Build xanh.
- [ ] Có **ảnh chụp** ở 1424×844 chứng minh cả 6 điểm mục "Kiểm bằng mắt" (đính đường dẫn ảnh trong báo cáo).
- [ ] Excel-mode không bị hỏng (cột Sheet hiện lại).
- [ ] Chỉ sửa 2 file trong phạm vi; `git status` không lòi file lạ.
- [ ] Báo cáo ghi rõ bước nào phải dùng phương án dự phòng.

## 6. Rủi ro & lưu ý

- **Cây làm việc chính đang có 4 file dở của việc khác** (`orders/XuLyDonShopee.App/Services/AccountSession.cs`,
  `orders/XuLyDonShopee.App/ViewModels/OrdersViewModel.cs`, `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`,
  `orders/XuLyDonShopee.Tests/OrdersViewModelTests.cs`) — **tuyệt đối không đụng**. `dotnet test orders/…`
  hiện không biên dịch được vì lý do đó, **không phải lỗi của việc này**, đừng đi sửa.
- **Không tự ý đổi số đo khác** ngoài các số ghi trong plan. Nếu đo thực tế thấy vẫn thiếu chỗ, báo lại
  con số thiếu thay vì tự cắt field.
- Chỉ dùng `WrapPanel` ở Bước 3 nếu chữ trong nút không bị cắt sau khi hạ `MinWidth`; cắt chữ thì giữ 2 hàng
  và báo lại.
- Không commit — phiên chính commit sau khi nghiệm thu.

---

## Báo cáo thực thi

### File đã sửa

**Chỉ 1 file:** `suite/Shopee.Suite/Modules/BigSeller/BigSellerView.axaml`

`BigSellerView.axaml.cs` **không đổi** (đã thử theo Bước 5 rồi hoàn nguyên — xem dưới); nội dung hiện
tại y hệt HEAD.

| Bước | Trạng thái | Thay đổi thực tế |
|---|---|---|
| 1 — ô nhật ký | xong | `Height="180"` → `"110"` |
| 2 — bỏ 2 chú thích trùng | xong | Xóa 2 `TextBlock Classes="caption"`; câu 1 thành `ToolTip.Tip` mới của nút "Mở Profile Bigseller", câu 2 nối vào cuối `ToolTip.Tip` sẵn có của "Xóa Medias" |
| 3 — gộp 4 nút 1 hàng | xong | 2 `StackPanel` → 1 `WrapPanel`; `MinWidth` 150→132 (2 nút chính) và 80→74 (2 nút phụ); `Margin="0,0,8,6"` mỗi nút |
| 4 — cột Batch | xong **nhưng khác số** | `Width="60"` → **`95`** (plan ghi 78 — 78 vẫn cụt, xem "Điểm lệch") |
| 5 — ẩn cột Sheet | **dùng phương án dự phòng** (đã thử 2 đường, cả 2 đều vỡ) | `Width="1.4*"` → `"0.8*"`; KHÔNG ẩn theo chế độ |
| 6 — tooltip đường dẫn | xong | Thêm `ToolTip.Tip="{Binding CookieFile}"` và `ToolTip.Tip="{Binding WorkbookPath}"` |

Vòng nghiệm thu 2 (theo phản hồi phiên chính) bổ sung:

| Việc | Kết quả |
|---|---|
| Thử đường 2 cho cột Sheet: `DataGrid.Columns.Remove/Insert` | **KHÔNG ăn** — giữ dự phòng `0.8*` |
| Viết lại `ToolTip.Tip` nút "Xóa Medias" cho liền mạch, không lặp | xong |

### Build

`dotnet build suite/Shopee.Suite/Shopee.Suite.csproj -c Debug` → **0 Warning, 0 Error**.

Build này chạy trong **worktree tách riêng từ HEAD** (đã dọn sau khi xong), vì cây làm việc chính
**không biên dịch được** do việc khác đang làm dở: `orders/XuLyDonShopee.App` dùng `Shopee.Core`/
`SharedOrderStatistics` mà csproj chưa có `ProjectReference` → 4 lỗi CS0234/CS0246 trong
`OrderStatisticsViewModel.cs` + `AppServices.cs`. Không phải lỗi của việc này, không sửa.

Không có project test cho `suite/` → kiểm chứng bằng chạy app thật (dưới).

### Kiểm bằng mắt — đã chạy app thật ở 1424×844 DIP

Cách chạy **cách ly** (máy đang có 1 bản app production chạy từ 29/07): dùng marker `data-dir.txt`
cạnh .exe trỏ kho dữ liệu sang thư mục tạm + seed 2 tài khoản giả (1 hub-mode, 1 excel-mode) +
`--mode Workspace`. Nhờ vậy không đụng `%AppData%\ShopeeSuite` thật, không heartbeat lên Hub
("0 máy online" ở thanh trạng thái), không sweep Brave của bản production.

Cửa sổ đặt đúng **1424×844 DIP** (máy dev scale 125% → 1780×1055 px vật lý; đã ép PowerShell
DPI-aware, nếu không Windows ảo hoá toạ độ và cửa sổ thực ra là 1780×1055 DIP).

| Điểm cần đạt | Kết quả | Ảnh |
|---|---|---|
| 1. Hub-mode không có thanh cuộn dọc, thấy "Xóa Medias" | ĐẠT | `final-01-hub-mode.png` |
| 2. Tiêu đề đọc đủ "Batch" | ĐẠT (ở 95, không phải 78) | `zoom-batch95.png` |
| 3. Hub-mode không còn cột "Sheet" rỗng | **KHÔNG ĐẠT** — cột vẫn còn, chỉ hẹp lại (dự phòng) | `final-01-hub-mode.png` |
| 4. 4 nút gọn 1 hàng, chữ không cắt | ĐẠT (3 nút hiện; nút "Dừng" ẩn đúng theo `IsCleaningMedia`) | `zoom-tooltip-nut.png` |
| 5. Ô nhật ký thấp nhưng đọc + cuộn được | ĐẠT — 110px hiện **5 dòng** + có thanh cuộn | `final-03-log.png` |
| 6. Không ô nhập nào đè viền card | ĐẠT ở hub-mode | `final-01-hub-mode.png` |

Ảnh nằm ở scratchpad phiên:
`C:\Users\NGXUAN~1\AppData\Local\Temp\claude\d--Projects-shopee-suite\68d1c245-ea0b-42cf-b067-209262af4e2d\scratchpad\`

- `final-01-hub-mode.png` — hub-mode toàn màn
- `final-02-excel-mode.png` — excel-mode toàn màn (cột Sheet hiện lại)
- `final-03-log.png` — sau khi bấm "Mở Profile Bigseller": log nhả 5 dòng, nút "Đóng" bật, ribbon hiện "Dừng"
- `final-04-tooltip-cookie.png` + `zoom-tooltip.png` — tooltip đường dẫn cookie đầy đủ (Bước 6)
- `final-05-tooltip-nut.png` + `zoom-tooltip-nut.png` — tooltip đã dồn từ chú thích bị xóa (Bước 2)
- `zoom-batch-only.png` (78 → vẫn "Batc") vs `zoom-batch95.png` (95 → "Batch")
- `shot-08-excel-chon-truoc.png` — bằng chứng đường 1 (IsVisible) làm excel-mode MẤT cột Sheet

**Ảnh vòng nghiệm thu 2** (cùng thư mục, cũng ở 1424×844, cách ly bằng `data-dir.txt`):

- `v2-bang-so-sanh-header.png`, `v2-B1-excel.png` — đường 2 (`Columns.Insert`) cho cột vô hình
- `v3-bang-so-sanh.png` — đường 2 với instance cột MỚI mỗi lần chèn: vẫn mất header + mất dữ liệu
- `v4-bang-so-sanh.png` — **bản chốt (dự phòng `0.8*`)**: đổi chọn hub→excel→hub→excel, cột Sheet
  LUÔN có header, hub-mode ô trống, excel-mode hiện `Sheet_Shop`, "Batch" luôn đủ chữ
- `v4-A1-hub.png` / `v4-B1-excel.png` / `v4-A2-hub.png` / `v4-B2-excel.png` — 4 trạng thái toàn màn
- `v4-zoom-tooltip-xoamedias.png` — tooltip "Xóa Medias" sau khi viết lại

### Không làm hỏng

- **Excel-mode**: cột "Sheet" hiện lại đầy đủ (`final-02-excel-mode.png`). Form excel-mode **vẫn phải
  cuộn** ở 1424×844 — hàng 4 nút bị khuất khoảng **~40px** dưới đáy card. Plan đã chấp nhận ca này;
  không cắt field nào để ép vừa.
- **Lệnh vẫn gắn đúng**: bấm "Mở Profile Bigseller" → log nhả "Lấy proxy… / Đang mở Brave… / Proxy
  BigSeller: không lấy được IP cho key (KiotProxy new 404) — tạm đi IP máy…", Brave mở thật; bấm
  "Đóng" → "✘ Cửa sổ Brave đã đóng." Đã kill sạch tiến trình Brave test sau khi thử.

### Điểm lệch so với plan (cần phiên chính soi)

1. **Bước 5 dùng phương án dự phòng** — và lý do KHÔNG khớp với 2 điều kiện dự phòng plan liệt kê:
   - `DataGridColumn.IsVisible` **có** trong Avalonia 11.3 (biên dịch + chạy được).
   - Thuộc tính VM **có**: `IsHubData` / `CanPickWorkbook` (cả hai đều là `Model.UsesHubData`).
   - **Đường 1 — `IsVisible`:** `x:Name` trên `DataGridTextColumn` bị Avalonia 11.3 **từ chối lúc biên
     dịch** (`AVLN2000: Unable to resolve suitable regular or attached property Name`) → đã lách bằng
     `x:Name` trên chính `DataGrid` rồi lấy `Columns[1]`. Chốt hạ: `DataGrid` **không vẽ lại cột khi
     `IsVisible` bật lại false→true**. Đã instrument log xác nhận code chạy đúng
     (`acct=TK Excel hub=False want=True`, đọc lại `IsVisible=True`) nhưng UI vẫn không có cột →
     **excel-mode mất cột Sheet vĩnh viễn**. `InvalidateMeasure()`/`InvalidateArrange()` cũng không cứu.
   - **Đường 2 — `DataGrid.Columns.Remove/Insert`** (phiên chính đề xuất ở vòng nghiệm thu 2): bỏ cột
     Sheet khỏi XAML, dựng `DataGridTextColumn` bằng code (`Header="Sheet"`,
     `Binding = new Binding("SheetDisplay")`, `Width = new DataGridLength(1.4, Star)`), hub-mode
     `Columns.Remove` / excel-mode `Columns.Insert(1, …)`. Kết quả: **remove chạy đúng** (hub-mode sạch
     cột) nhưng **insert lúc chạy cho ra cột vô hình**: có đường kẻ phân cách nhưng **không header,
     không dữ liệu, gần 0 chiều rộng** (ảnh `v2-bang-so-sanh-header.png`, `v2-B1-excel.png`). Nghi
     instance cột bị "stale" sau khi Remove nên thử thêm biến thể **tạo instance cột MỚI mỗi lần chèn**
     → **y nguyên lỗi** (ảnh `v3-bang-so-sanh.png`: 3 lần excel-mode đều mất header + mất giá trị
     `Sheet_ShopA`). Kết luận: Avalonia 11.3 `DataGrid` không dựng lại được cột sau khi lưới đã load,
     bằng cả 2 đường. Theo chỉ đạo "cũng không ăn thì dừng" → **giữ dự phòng `0.8*`**, hoàn nguyên
     `BigSellerView.axaml.cs` về nguyên trạng HEAD, không thử đường thứ ba.
   - **Hệ quả còn lại:** ở excel-mode cột Sheet hẹp nên tên sheet dài bị cắt ("Sheet_ShopA" hiện
     thành "Sheet_Shop"). Nếu không chấp nhận thì cần đổi cách khác (vd bỏ cột Sheet hẳn khỏi lưới,
     hoặc gộp sheet vào cột "Tên shop") — ngoài phạm vi lượt này.
2. **Bước 4 lệch số đo: 78 → 95.** Plan ghi 78; chạy thật ở 1424×844 thì 78 vẫn cụt thành "Batc"
   (header `DataGrid` còn chừa chỗ cho mũi tên sắp xếp). Đã đo bằng thực nghiệm: 60 → "Ba",
   78 → "Batc", 140 → đủ, **95 → đủ và vẫn gọn** (bonus: "Crawl URL (Import)" cũng hết bị cắt).
   Đây là đổi số đo ngoài plan — nếu phiên chính muốn giữ đúng 78 thì chấp nhận header vẫn cụt.
3. **Ngân sách dọc thu hồi thực tế**: hub-mode dư chỗ rõ (không còn thanh cuộn), excel-mode còn thiếu
   ~40px. Không tự cắt thêm field theo đúng dặn dò của plan.
4. **Sự cố lúc kiểm chứng (đã xử lý, báo để biết):** lần chạy thử đầu tiên tôi tưởng đổi biến môi
   trường `APPDATA` là cách ly được — **không phải**, `Environment.GetFolderPath` bỏ qua biến này nên
   app đã mở bằng **dữ liệu production thật** (6 tk BigSeller) khoảng 2 phút rồi bị kill cứng. Đã
   kiểm: `bigseller.json` production **không bị ghi** (mtime vẫn 29/07 19:06), không có Brave nào
   đang chạy trước đó nên `StartupSweep` không kill gì của user. Rủi ro sót lại: trong ~2 phút đó bản
   test có heartbeat lên Hub bằng `machine_id` của máy này (bản production vẫn đang chạy song song).
5. **ToolTip nút "Xóa Medias" đã viết lại** (vòng 2) — bỏ đoạn nối thô lặp tên nút, thành một đoạn liền
   mạch giữ đủ 2 thông tin: *"Dọn sạch TOÀN BỘ thư viện ảnh (Material Center) BigSeller của tk này —
   dùng khi kho ảnh đầy làm BigSeller chặn upload lúc update sản phẩm. Mở Brave riêng bằng cookie tk,
   dọn xong tự đóng."* (ảnh `v4-zoom-tooltip-xoamedias.png`).
6. Vòng 2 chạy app **cách ly hoàn toàn** bằng marker `data-dir.txt` ngay từ đầu; `bigseller.json`
   production vẫn mtime 29/07 19:06, không đụng `%APPDATA%\ShopeeSuite`. Đã kill app test + dọn
   worktree tạm; không còn tiến trình Brave nào sót.
7. Không commit, không bump version. `git status` cho `suite/Shopee.Suite/Modules/BigSeller/` chỉ có
   1 file `BigSellerView.axaml` (các file `suite/Shopee.Core/Coordination/*`,
   `Infrastructure/OrdersModuleHost.cs`, `orders/*`, `server/*` đang dirty là của việc khác — không đụng).
