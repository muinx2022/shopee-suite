# Plan: Cửa sổ vừa màn hình HD+ (1440×900)

- **Ngày:** 2026-07-29
- **Trạng thái:** hoàn thành (v1.6.15)
- **Người lập:** Fable · **Người thực thi:** Auto (Cursor)

## 1. Bối cảnh & mục tiêu

### Hiện trạng (đã xác nhận bằng ảnh máy thật)

- Máy lỗi: **1440 × 900 @ scale 100%** (Windows Display Settings — Recommended).
- Vùng làm việc sau taskbar ≈ **1440 × ~860** (pixel = DIP vì scale 100%).
- `MainWindow.axaml` mở cứng: `Width="1500" Height="940" MinWidth="1080" MinHeight="680"`.
- **1500 > 1440** và **940 > 860** → cửa sổ lớn hơn vùng làm việc → tràn, không nhìn đủ (title/status/góc bị cắt hoặc không kéo được).
- Khi maximize trên 1440×900, màn **Shopee → Tài khoản** (3 cột: list 340 | form 3* | log 2*) vẫn vừa vừa nhưng rất sát mép; hàng nút trái "Thêm tài khoản" + "Kéo TK từ Hub" + xóa dễ chật trong cột 340.

### Mục tiêu

1. Lần mở app trên màn nhỏ hơn kích thước mặc định: cửa sổ **không bao giờ lớn hơn WorkingArea**, canh giữa, vẫn nhìn đủ chrome (tab + ribbon + status).
2. Full HD (1920×1080) và lớn hơn: vẫn mở gần như hiện tại (~1500×940), không maximize ép.
3. MinWidth/MinHeight hạ vừa đủ để người dùng còn thu nhỏ/kéo trên 1440×900.
4. Màn Tài khoản Shopee: hàng nút đáy cột trái không tràn ngang khi cột 340.

### Quyết định đã chốt (mặc định khuyến nghị — user gửi ảnh 1440×900, chưa trả lời AskQuestion)

- **Startup:** clamp kích thước theo `Screens` WorkingArea (trừ taskbar), canh giữa — **không** luôn maximize.
- **Phạm vi layout:** cửa sổ chính + chỉnh hẹp màn Tài khoản Shopee (hàng nút cột trái). Các màn khác (Search/BigSeller/…) chỉ sửa nếu còn chỗ cứng rõ ràng gây cắt trên content ~1440×(860−chrome); không redesign toàn bộ.

## 2. Phạm vi

### Làm

- `suite/Shopee.Suite/MainWindow.axaml` — điều chỉnh Width/Height/MinWidth/MinHeight mặc định.
- `suite/Shopee.Suite/MainWindow.axaml.cs` — logic clamp theo WorkingArea lúc `Opened` (hoặc `Loaded` lần đầu).
- `orders/XuLyDonShopee.App/Views/AccountsView.axaml` — hàng nút đáy cột trái (khoảng dòng 289–312) không tràn khi hẹp.
- Cửa sổ phụ nếu Width/Height cố định > WorkingArea tối thiểu mục tiêu: `CheckAccountWindow` (940×760) vẫn OK trên 1440×900; chỉ clamp nếu mở trên màn còn nhỏ hơn (phụ, có thể dùng helper chung).

### Không làm

- Không nhớ kích thước/vị trí cửa sổ lần trước (persist) — lần sau có thể làm riêng.
- Không redesign ribbon / không thu ribbon khi thấp.
- Không refactor toàn bộ SearchView / BigSellerView / DataView trừ khi build/review phát hiện cắt cứng trên 1440×900 sau khi clamp cửa sổ.
- Không đổi Hub web, không bump version/release.

## 3. Các bước thực hiện

### Bước 1 — Điều chỉnh kích thước khai báo trong XAML

File: `suite/Shopee.Suite/MainWindow.axaml` (dòng 5)

Đề xuất:

| Thuộc tính | Hiện tại | Mới | Lý do |
|---|---|---|---|
| `Width` | 1500 | **1280** | vừa WorkingArea ngang 1440, còn lề; Full HD vẫn rộng đủ |
| `Height` | 940 | **780** | vừa WorkingArea dọc ~860; Full HD vẫn cao đủ |
| `MinWidth` | 1080 | **1024** | cho phép thu nhỏ hơn một chút trên HD+ |
| `MinHeight` | 680 | **640** | content sau chrome (~tab+ribbon 112+status 32 ≈ 185) còn ~455 — chấp nhận được |

Giữ `WindowStartupLocation="CenterScreen"`. Logic clamp ở code-behind sẽ ghi đè Width/Height nếu màn còn nhỏ hơn (hoặc scale > 100%).

### Bước 2 — Helper clamp cửa sổ theo WorkingArea

File: `suite/Shopee.Suite/MainWindow.axaml.cs` (hiện chỉ `InitializeComponent()`).

Hành vi:

1. Hook sự kiện `Opened` (một lần): gọi `FitToWorkingArea()`.
2. `FitToWorkingArea()`:
   - Lấy `var screen = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;` — nếu null thì return (không crash).
   - `working = screen.WorkingArea` (pixel).
   - `scale = screen.Scaling` (DIP = pixel / scale).
   - `maxW = working.Width / scale`, `maxH = working.Height / scale`.
   - Margin an toàn ~8 DIP mỗi phía (tránh dính sát taskbar/mép): `maxW -= 16`, `maxH -= 16` (floor tối thiểu = MinWidth/MinHeight).
   - `Width = Math.Min(Width, maxW)`, `Height = Math.Min(Height, maxH)` — **không phóng to** trên màn lớn.
   - Đặt lại vị trí canh giữa trong WorkingArea (vì sau khi đổi size, `CenterScreen` có thể đã tính theo size cũ):
     - `WindowStartupLocation = Manual` tạm thời khi reposition, hoặc set `Position` pixel:
       `x = working.X + (working.Width - widthPx) / 2`, tương tự Y.
   - Không đổi `WindowState` (giữ Normal trừ khi user maximize).
3. Chỉ chạy **một lần** lúc mở (flag `_fitted`), không chạy lại mỗi lần resize tay.

Ghi chú DPI: mọi so sánh Width/Height (DIP) với WorkingArea (pixel) **phải** chia `Scaling`. Máy 1440×900 @100% → scale=1. Máy 1600×900 @125% → WorkingArea logical ≈ 1280×720 — clamp vẫn đúng.

Có thể tách static helper nhỏ trong cùng file hoặc `suite/Shopee.Suite/Infrastructure/WindowFit.cs` nếu muốn tái dùng cho dialog phụ — không bắt buộc nếu chỉ MainWindow.

### Bước 3 — Màn Tài khoản Shopee: hàng nút cột trái

File: `orders/XuLyDonShopee.App/Views/AccountsView.axaml` (~dòng 289–312)

Hiện: `Grid ColumnDefinitions="*,Auto,Auto"` với 3 nút cạnh nhau trong cột rộng cố định **340** (trừ margin) → trên 1440×900 khi maximize vẫn chật; chữ "Kéo TK từ Hub" dễ bị cắt.

Cách sửa (chọn A, đơn giản khớp pattern sẵn có):

- Đổi hàng nút thành `WrapPanel` Orientation=Horizontal (Spacing/Margin giữ khoảng 8), hoặc `StackPanel` Orientation=Vertical nếu WrapPanel làm nút "Thêm" quá hẹp.
- Khuyến nghị: `WrapPanel` — nút "Thêm tài khoản" stretch hết hàng nếu chỉ một mình; khi hẹp, "Kéo TK từ Hub" + xóa xuống dòng dưới.
- Giữ nguyên Command/ToolTip/Classes (`success`, `destructive iconOnly`).
- Không đổi `ColumnDefinitions="340,*"` của layout chính lần này (340 vẫn ổn khi maximize 1440).

### Bước 4 — (Tùy chọn nhẹ) Cửa sổ phụ

`CheckAccountWindow` 940×760: trên 1440×900 vẫn vừa. Không bắt buộc clamp. Nếu làm helper chung thì gọi luôn trong constructor/Opened của các `*Window.axaml.cs` chính (CheckAccount, ImportAccounts, ScrapeStats) — **không** áp cho MessageDialog SizeToContent.

### Bước 5 — Kiểm chứng

```text
dotnet build suite/Shopee.Suite/Shopee.Suite.csproj -c Debug
```

Kiểm thủ công (máy dev hoặc máy 1440×900):

1. Mở app trên 1440×900 @100%: cửa sổ Normal nằm gọn trong màn (không cắt status bar / không vượt taskbar), canh giữa.
2. Mở trên Full HD (hoặc thu cửa sổ giả lập WorkingArea lớn): kích thước ≈ Width/Height XAML mới (1280×780), không bị maximize ép.
3. Thu nhỏ cửa sổ tới MinWidth/MinHeight: không crash; status bar vẫn hiện.
4. Tab Shopee → Tài khoản: hàng nút "Thêm / Kéo TK từ Hub / Xóa" không bị cắt chữ; bấm được cả 3.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build suite/Shopee.Suite/Shopee.Suite.csproj` thành công.
- [ ] Trên màn có WorkingArea < 1500×940 (điển hình 1440×900), app mở lần đầu **không tràn** khỏi WorkingArea.
- [ ] Trên màn lớn hơn mặc định mới, app không bị thu nhỏ vô cớ dưới Width/Height XAML.
- [ ] Scale ≠ 100% (nếu test được): clamp dùng đúng `WorkingArea/Scaling`, không nhân nhầm pixel/DIP.
- [ ] AccountsView: 3 nút đáy cột trái không tràn ngang cột 340.
- [ ] Cập nhật `Trạng thái` plan → `hoàn thành` khi xong.

## 5. Rủi ro & lưu ý

- **Screens null lúc constructor:** chỉ clamp trong `Opened`/`Loaded`, không trong ctor trước khi gắn platform.
- **UltraViewer / remote desktop:** WorkingArea có thể khác màn local; clamp theo screen của cửa sổ (`ScreenFromWindow`) đúng hơn `Primary`.
- **Hai màn hình:** dùng screen chứa cửa sổ, không luôn Primary.
- **Fire-and-forget / lỗi phụ:** nếu Screens API lỗi → nuốt/log, giữ size XAML (không chặn mở app).
- **MinHeight 640:** một số màn (SearchView cố định 190+150) vẫn chật khi thu tối thiểu — ngoài phạm vi lần này; ghi vào báo cáo nếu thấy.
- Không commit code trừ khi user yêu cầu; chỉ commit file plan theo quy trình.

---

## Báo cáo thực thi

**Ngày:** 2026-07-30 · **Người thực thi:** subagent Opus (opus-dev)

### ⚠ User ĐỔI QUYẾT ĐỊNH so với plan (chốt sau khi xem lại ảnh máy thật)

| Mục plan | Plan viết | User chốt lại — bản đang có trong code |
|---|---|---|
| Bước 1 | `Width 1500→1280`, `Height 940→780` | **GIỮ 1500×940**. Cơ chế kẹp đã lo cho máy màn nhỏ rồi, không việc gì bắt máy Full HD mở nhỏ hơn hiện tại. Chỉ giữ phần hạ `MinWidth 1080→1024`, `MinHeight 680→640`. |
| Bước 2 | chỉ kẹp, "không đổi WindowState" | **Có thêm maximize** cho riêng cửa sổ chính khi màn nhỏ: kẹp + canh giữa TRƯỚC rồi mới `WindowState = Maximized`. |
| Bước 3 | sửa hàng nút AccountsView (WrapPanel) | **BỎ HẲN** — user xem ảnh máy thật, 3 nút vẫn đủ chỗ trong cột 340. File đã `git checkout --` về đúng bản gốc. |

Ngoài ra: **không thêm `ScrollViewer`** ở bất kỳ đâu (đã khảo sát — mọi màn đều có vùng `*` chứa DataGrid/ScrollViewer tự cuộn; bọc ScrollViewer ngoài DataGrid làm mất ảo hóa gây đơ).

### File đã sửa/tạo

| File | Thay đổi |
|---|---|
| `suite/Shopee.Suite/MainWindow.axaml` | Dòng 5: chỉ hạ `MinWidth 1080→1024`, `MinHeight 680→640`. `Width="1500" Height="940"` GIỮ NGUYÊN. Giữ `WindowStartupLocation="CenterScreen"`. |
| `suite/Shopee.Suite/Infrastructure/WindowFit.cs` | **MỚI.** Extension `Window.FitOnOpen(bool maximizeIfTooSmall = false)` (gắn handler `Opened`, tự gỡ sau lần đầu) + `Window.FitToWorkingArea(bool maximizeIfTooSmall = false)`. Kẹp: `ScreenFromWindow(this) ?? Primary`; `WorkingArea` (pixel) chia `Scaling` ra DIP; lề an toàn 8 DIP mỗi phía; sàn = `MinWidth`/`MinHeight`; **chỉ thu nhỏ** (`Math.Min`), không phóng to. Kích thước XAML đã lọt màn → `return` sớm, không kẹp **và không maximize**. Khi kẹp thật: gán `Width`/`Height` mới → `WindowStartupLocation = Manual` + canh giữa bằng `Position` (pixel) → **cuối cùng** mới `WindowState = Maximized` nếu `maximizeIfTooSmall`. Thứ tự này bắt buộc: kích thước "khôi phục" chính là số vừa gán, nên bấm nút khôi phục ra cửa sổ vừa màn chứ không bật lại 1500×940. Bỏ qua khi: `WindowState != Normal`, `Width`/`Height` là NaN (cửa sổ SizeToContent), `Screens`/`Screen` null; toàn bộ bọc `try/catch` nuốt lỗi → không chặn mở app. Điều kiện maximize hoàn toàn theo phép so kích thước, **không ngưỡng số cứng**. |
| `suite/Shopee.Suite/MainWindow.axaml.cs` | Ctor gọi `this.FitOnOpen(maximizeIfTooSmall: true)` sau `InitializeComponent()` — chỉ cửa sổ chính maximize. |
| `suite/Shopee.Suite/Modules/CheckAccount/CheckAccountWindow.axaml.cs` | (Bước 4) ctor gọi `this.FitOnOpen()` — mặc định false → chỉ kẹp, không maximize. |
| `suite/Shopee.Suite/Modules/Accounts/ImportAccountsWindow.axaml.cs` | (Bước 4) ctor gọi `this.FitOnOpen()` — chỉ kẹp. |
| `suite/Shopee.Suite/Modules/Scrape/ScrapeStatsWindow.axaml.cs` | (Bước 4) ctor rỗng gọi `this.FitOnOpen()` — chỉ kẹp; ctor `(ScrapeStatsViewModel)` chuyển sang chain `: this()` để dùng chung (trước đó tự gọi `InitializeComponent()` riêng nên sẽ lọt). |
| `orders/XuLyDonShopee.App/Views/AccountsView.axaml` | **Không đổi** — đã hoàn tác về bản gốc theo quyết định mới của user. |

**KHÔNG** áp kẹp cho `MessageDialog` (SizeToContent) và `RowEditWindow` (SizeToContent=Height, CanResize=False) — đúng ghi chú Bước 4.

### Build / test

- `dotnet build suite/Shopee.Suite/Shopee.Suite.csproj -c Debug` (chạy lại sau khi sửa theo quyết định mới) → **Build succeeded. 0 Warning(s) 0 Error(s)** (8,9s).
- `dotnet test orders/XuLyDonShopee.Tests` → **KHÔNG chạy được**, lỗi biên dịch nằm hoàn toàn trong `OrdersViewModelTests.cs` — phần việc dở đang có sẵn trên cây làm việc, KHÔNG thuộc plan này:
  - `OrdersViewModelTests.cs(133,9): error CS0200: Property or indexer 'AppServices.Sessions' cannot be assigned to`
  - `OrdersViewModelTests.cs(136,12): error CS1061: 'OrdersViewModel' does not contain a definition for 'RedownloadSlipCommand'`
  - Cả 2 dòng đều nằm trong 47 dòng mới của diff chưa commit (`git diff` xác nhận). Không có test nào phủ layout/kích thước cửa sổ.

### Điểm còn tồn / chưa kiểm chứng

1. **Chưa test trên máy 1440×900 thật** — máy dev không có màn đó, cũng chưa chạy app. Toàn bộ nhánh "có kẹp" (gán `Width/Height`, `Position`, `WindowStartupLocation`, rồi `Maximized`) chưa chạy thật lần nào; máy dev màn lớn đi nhánh `return` sớm (mở 1500×940 như cũ). Cần user mở thử trên ADMIN-PC, kiểm 2 điểm: (a) mở ra là maximize, thấy đủ tab+ribbon+status; (b) bấm nút **khôi phục** thì cửa sổ vừa màn (~1424×844) chứ không phải 1500×940 tràn.
2. **Chưa test scale ≠ 100%** — công thức chia `Scaling` mới chỉ đúng trên giấy.
3. Dialog phụ khi bị kẹp sẽ canh giữa theo **màn hình**, không còn theo owner (`CenterOwner`) — chỉ xảy ra trên màn quá nhỏ, chấp nhận được.
4. `MinHeight 640` như plan đã lường: vài màn (SearchView có khối cố định 190+150) sẽ chật khi thu tối thiểu — ngoài phạm vi lần này (và đã chốt KHÔNG thêm ScrollViewer).
5. `dotnet test orders/XuLyDonShopee.Tests` không chạy được vì lỗi biên dịch trong `OrdersViewModelTests.cs` — việc dở của phiên chính, không liên quan hạng mục này (chi tiết ở mục Build/test).
6. Phiên chính đã nghiệm thu (đọc lại code + tự chạy build) và phát hành **v1.6.15** để user kiểm trên máy ADMIN-PC 1440×900. Trạng thái plan chuyển `hoàn thành`; nếu bản thật còn sai thì mở plan mới.
