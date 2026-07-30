# Plan: Màn Cấu hình BigSeller gọn lại trên màn 1440×900

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
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

<Để trống — người thực thi điền.>
