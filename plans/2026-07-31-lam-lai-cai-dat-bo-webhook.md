# Plan: Làm lại màn Cài đặt (một hệ style duy nhất) + bỏ card webhook phía client

- **Ngày:** 2026-07-31
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Màn "Cài đặt" hiện tại (`suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.axaml`) là màn GỘP: nó nhúng
2 view con qua ContentControl + ViewLocator:

- `suite/Shopee.Suite/Modules/Settings/SettingsView.axaml` (bind `SettingsViewModel` của suite) — có header
  "Cài đặt" RIÊNG + TabControl 2 tab (Hiệu năng / Đồng bộ nhiều máy), style theo Theme của suite.
- `orders/XuLyDonShopee.App/Views/SettingsView.axaml` (bind `XuLyDonShopee.App.ViewModels.SettingsViewModel`)
  — có header "Cài đặt" RIÊNG NỮA, tự chứa resource `ModuleResources.axaml` với bộ token màu/chữ KHÁC suite.

Kết quả: một màn có 2 tiêu đề "Cài đặt", 3 hệ style trộn lẫn (section bar xám của UnifiedSettingsView + card
suite + card orders với font/màu/bo góc khác), thụt lề mỗi đoạn một kiểu → người dùng chê "lom nhom".

Ngoài ra card "THÔNG BÁO WEBHOOK (CHỈ KHI CHƯA NỐI HUB)" (3 ô webhook fallback) không còn cần thiết ở client:
webhook giờ là cấu hình HUB-OWNED, đặt trên trang Cài đặt của Hub web (Hub gửi tin). Người dùng yêu cầu **bỏ
hẳn phần webhook khỏi màn cài đặt client**.

Mục tiêu:
1. Một màn Cài đặt DUY NHẤT, một tiêu đề, một hệ style (Theme của suite), bố cục section rõ ràng, gọn gàng.
2. Không còn UI cấu hình webhook ở client (VM cũng bỏ các property/command webhook).
3. KHÔNG đổi bất kỳ hành vi command/lưu trữ nào khác — thuần bố cục lại + xoá webhook UI.

Quyết định đã chốt:
- Gộp toàn bộ nội dung vào **một file view duy nhất** `UnifiedSettingsView.axaml` (suite), bind trực tiếp
  `Suite.*` và `Orders.*` (2 VM con đã có sẵn trên `UnifiedSettingsViewModel`). Xoá 2 view con cũ.
- Backend webhook GIỮ NGUYÊN: `SettingsRepository` (các getter/setter NotifyWebhookUrl*) và
  `OrderNotifyService` không đụng — máy nào đã lưu giá trị từ trước vẫn chạy như cũ, chỉ là không còn UI sửa.

## 2. Phạm vi

- **Làm:**
  - Viết lại `suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.axaml` thành màn Cài đặt hoàn chỉnh.
  - Xoá `suite/Shopee.Suite/Modules/Settings/SettingsView.axaml` + `.axaml.cs` (nội dung đã inline vào view gộp).
  - Xoá `orders/XuLyDonShopee.App/Views/SettingsView.axaml` + `.axaml.cs`.
  - Gỡ mapping 2 view vừa xoá khỏi ViewLocator/DataTemplates (suite lẫn orders, chỗ nào có).
  - `orders/XuLyDonShopee.App/ViewModels/SettingsViewModel.cs`: bỏ 3 property `NotifyWebhookUrl*`,
    `NotifySavedMessage`, command `SaveNotifyUrl`, và mọi chỗ clear/tham chiếu chúng trong các command khác +
    trong `Reload()`; cập nhật xmldoc đầu class.
  - Cập nhật `CHANGELOG.md` (mục mới trên cùng, theo phong cách hiện có; KHÔNG bump `version.txt`).
- **Không làm:**
  - Không đụng `SettingsRepository`, `OrderNotifyService`, `GsheetConfigSync`, `OrdersModuleHost`, Hub.
  - Không đổi logic/command nào của 2 VM ngoài phần webhook nêu trên.
  - Không đổi style toàn cục (`Theme.axaml`) trừ khi thiếu icon (xem Rủi ro).
  - Không đụng các màn khác của orders (`MainView`, `OrdersView`, …).

## 3. Các bước thực hiện

1. **Khảo sát nhanh trước khi xoá** (bắt buộc):
   - Grep `SettingsView` trong toàn `suite/` + `orders/` (loại bin/obj) để chắc 2 view sắp xoá chỉ được dùng
     qua ViewLocator/DataTemplate từ màn gộp. Nếu phát hiện chỗ khác đang dựng chúng trực tiếp → DỪNG, ghi
     nhận vào báo cáo, không tự chế phương án.
   - Xem `suite/Shopee.Suite/Themes/Theme.axaml` (hoặc file icon tương ứng) có `IconFolder` chưa; thiếu thì
     chép path data từ `orders/XuLyDonShopee.App/Styles/Icons.axaml` vào bộ icon suite.

2. **Viết lại `UnifiedSettingsView.axaml`** — bố cục mới, TOÀN BỘ dùng token/class của Theme suite
   (`card`, `h1`, `h2`, `caption`, `primary`, `success`, PathIcon `IconSave/IconAdd/IconSync/IconExport/IconUpgrade/IconFolder`):

   - Root: `Grid RowDefinitions="Auto,*"` với `Margin="28,22,28,24"`.
     - Hàng 0 (header, duy nhất một lần): trái = TextBlock "Cài đặt" `Classes="h1"` + caption ngắn mô tả;
       phải = chip trạng thái bind `{Binding Suite.Status}` (Border SubtleBrush, CornerRadius 4, Padding 14,6)
       — giữ nguyên như header SettingsView cũ.
     - Hàng 1: ScrollViewer dọc → StackPanel các section, `MaxWidth="1180"` `HorizontalAlignment="Left"`.
   - Mỗi section = nhãn section (TextBlock chữ HOA, FontSize 12, Bold, `TextSecondaryBrush`,
     Margin trên 22 dưới 10 — thống nhất MỘT kiểu nhãn, bỏ hẳn kiểu Border bar xám cũ) + nội dung card.
     Card thống nhất: `Classes="card"`, Padding 18, khoảng cách giữa card trong cùng section 14.
   - Thứ tự section + điều kiện hiện:
     1. `CHẾ ĐỘ ỨNG DỤNG` (luôn hiện): 1 card — ComboBox Modes (MinWidth 380) + nút "Lưu & khởi động lại"
        (`Classes="primary"`, ẩn khi `ModeLockedByArg`) trên một hàng; dòng chú thích khoá-bởi-shortcut
        (hiện khi `ModeLockedByArg`); nút "Tạo shortcut cho chế độ này" + caption của nó. Bind y nguyên các
        binding hiện có ở UnifiedSettingsView cũ (dòng 23–58).
     2. `PHIÊN BẢN & CẬP NHẬT` (luôn hiện): 1 card, MaxWidth 560 — giữ nguyên nội dung card hiện tại
        (dòng 75–108 cũ): `Suite.AppVersionText`, `Suite.UpdateStatus`, nút Kiểm tra bản mới /
        Cập nhật & khởi động lại, ghi chú Velopack.
     3. `HIỆU NĂNG` (`IsVisible="{Binding ShowsWorkspaceSettings}"`): chuyển từ tab "Hiệu năng" của
        SettingsView suite cũ — Grid 2 cột (`1.25*,14,1*`): card "Tài nguyên cho phép…" (UsableCpu,
        UsableRamGb, CpuCoresMax, RamGbMax, ComputedMaxInfo, caption, nút Lưu `SavePerformanceCommand`)
        + card "Máy của bạn" (`MachineInfo`). Mọi binding prefix `Suite.`.
     4. `ĐỒNG BỘ NHIỀU MÁY` (`ShowsWorkspaceSettings`): chuyển từ tab "Đồng bộ nhiều máy" cũ — Grid 2 cột
        (`1*,14,1.35*`): card "Máy này" (MachineLabel, MachineDisplayName, SaveMachineNameCommand) + card
        "Kết nối tới Hub" (HubEnabled, HubBaseUrl, HubApiToken, ConnectToggle/Test/SaveHubClient,
        HubClientStatus, PushConfigToHub). Mọi binding prefix `Suite.`.
     5. `ĐƠN HÀNG` (`IsVisible="{Binding HasOrders}"`): chuyển nội dung từ orders SettingsView cũ nhưng
        style suite, mọi binding prefix `Orders.` và dùng binding CLASSIC (KHÔNG đặt `x:DataType` cho phần
        này — `Orders` có thể null khi module không chạy). Grid 2 cột (`*,14,*`):
        - Cột trái: card "Tự động hóa" — Thư mục lưu hóa đơn (TextBlock path mono + nút "Chọn…"
          `Orders.ChooseInvoiceFolderCommand`), Chu kỳ theo dõi đơn (NumericUpDown 1–1440), nút Lưu
          `Orders.SaveIntervalCommand` + `Orders.SavedMessage`; card "Trình duyệt" — ComboBox
          `Orders.BrowserOptions`/`Orders.SelectedBrowser` (ItemTemplate hiển thị `Label`), dòng
          `Orders.DetectedBrowserText`, ghi chú Chrome/Brave, nút Lưu `Orders.SaveBrowserCommand` +
          `Orders.BrowserSavedMessage`.
        - Cột phải: card "Đồng bộ Google Sheet" — 3 ô `Orders.GsheetWebAppUrl` / `Orders.GsheetTabName`
          (watermark "để trống = tự động Tháng MM-yyyy") / `Orders.GsheetSheet2`, các dòng ghi chú hiện có
          (kể cả ghi chú "DÙNG CHUNG toàn hệ thống qua Hub"), nút Lưu `Orders.SaveGsheetUrlCommand` +
          `Orders.GsheetSavedMessage`.
        - **KHÔNG có card webhook.**
   - TextBox trong section Đơn hàng dùng TextBox thường của theme suite (bỏ cấu trúc Border field/underline
     riêng của orders); watermark dùng thuộc tính `Watermark` của Avalonia TextBox.
   - Các dòng thông báo saved-message: TextBlock màu `SuccessBrush` (token suite; nếu suite đặt tên khác thì
     dùng token thành công của Theme suite), `IsVisible` theo StringConverters.IsNotNullOrEmpty như cũ.

3. **Xoá 2 view con + gỡ mapping**: xoá 4 file (`SettingsView.axaml`/`.axaml.cs` ở cả suite lẫn orders); grep
   `ViewLocator`/DataTemplate hai project gỡ entry tương ứng. `UnifiedSettingsView.axaml.cs` giữ nguyên.

4. **Sửa `orders/.../SettingsViewModel.cs`**: bỏ `_notifyWebhookUrlDonMoi/LoiApp/DonTra`,
   `_notifySavedMessage`, `SaveNotifyUrl()`; bỏ 3 dòng nạp Notify* trong `Reload()` và mọi dòng
   `NotifySavedMessage = null;` trong các command khác; cập nhật xmldoc class (bỏ đoạn nói về card webhook).
   KHÔNG đụng `using` nào còn cần.

5. **Build + test + rà cảnh báo**:
   - `dotnet build ShopeeSuite.sln` — 0 warning 0 error (mốc nghiệm thu của repo).
   - `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` — nếu có test đụng các member VM vừa
     xoá thì sửa test theo (chỉ phần webhook-VM; test của `SettingsRepository`/`OrderNotifyService` phải giữ
     nguyên và vẫn xanh).
   - Chạy thử app (`dotnet run --project suite/Shopee.Suite` hoặc chạy exe build ra) mở màn Cài đặt ở chế độ
     Full để mắt thường xác nhận không vỡ layout; nếu môi trường không mở được UI thì ghi rõ trong báo cáo.

6. **CHANGELOG.md**: thêm mục mới trên cùng (chưa gắn version — người điều phối sẽ đánh version khi phát
   hành): làm lại màn Cài đặt một hệ style + bỏ cấu hình webhook phía client (webhook giờ đặt trên Hub).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln`: 0 error, 0 warning.
- [ ] `dotnet test orders/XuLyDonShopee.Tests`: xanh toàn bộ; test SettingsRepository + OrderNotifyService
      KHÔNG bị sửa.
- [ ] Grep `NotifyWebhookUrl` trong `orders/XuLyDonShopee.App/` (ViewModels + Views) = 0 kết quả; trong
      `XuLyDonShopee.Core/` vẫn còn (repository + service giữ nguyên).
- [ ] Không còn file `SettingsView.axaml` nào trong repo ngoài lịch sử git; grep `SettingsView` không còn
      tham chiếu sống (ngoài comment/plan).
- [ ] Màn Cài đặt mới: đúng MỘT tiêu đề "Cài đặt"; không còn TabControl; không còn card webhook; section
      Hiệu năng + Đồng bộ nhiều máy ẩn ở chế độ Shopee (`ShowsWorkspaceSettings`), section Đơn hàng ẩn khi
      thiếu module (`HasOrders`); mọi nút/command cũ vẫn bind đúng (đối chiếu danh sách binding ở bước 2).

## 5. Rủi ro & lưu ý

- `Orders` có thể **null** (chế độ Workspace không có module đơn hàng): toàn bộ binding `Orders.*` phải nằm
  trong cây có `IsVisible="{Binding HasOrders}"` và dùng binding classic — TUYỆT ĐỐI không để crash/binding
  error ồ ạt khi null.
- Suite tham chiếu sẵn project `XuLyDonShopee.App` (alias `OrdersSettingsViewModel` trong
  `UnifiedSettingsViewModel.cs`) nên bind `Orders.*` từ view suite là hợp lệ, không cần thêm reference.
- `BrowserChoiceOption.Label` bind trong ItemTemplate: nếu không dùng `x:DataType` thì bind theo tên property
  reflection vẫn chạy; giữ nguyên cách hiển thị nhãn tiếng Việt.
- Token style: dùng đúng class/token ĐANG CÓ trong `Theme.axaml` của suite (đọc file này trước khi viết
  axaml, đừng bịa tên brush). Định hướng chung: phẳng kiểu Windows 11, bo 4–6, không gradient/bóng.
- Sau khi xoá orders `SettingsView`, class `ViewModelBase`/`DialogService` của orders vẫn dùng ở chỗ khác —
  không xoá gì thêm ngoài phạm vi.
- KHÔNG commit — người điều phối commit sau nghiệm thu.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Ngày thực thi:** 2026-07-31 · **Người thực thi:** Opus (`opus-executor`) · **Chưa commit** (theo yêu cầu).

### Khảo sát trước khi xoá (bước 1)

- Grep `SettingsView` toàn `suite/` + `orders/` (trừ bin/obj): **2 view sắp xoá chỉ được dựng qua
  DataTemplate/ViewLocator từ màn gộp** — không có chỗ nào `new SettingsView()` trực tiếp.
  - Suite: `App.axaml:53` `<DataTemplate DataType="settings:SettingsViewModel">` là mối duy nhất → đã gỡ.
  - Orders: `ViewLocator.cs` là locator **phản chiếu** (`ViewModel`→`View` theo tên), KHÔNG có entry tường minh
    nào để gỡ. `MainView.axaml` không tham chiếu màn Cài đặt; `MainViewModel.SettingsVm` chỉ để shell suite lấy
    VM. ⇒ Không còn đường nào resolve tới `XuLyDonShopee.App.Views.SettingsView` sau khi xoá.
- Icon: **không phải chép gì**. `Icons.axaml` của orders (nguồn DUY NHẤT) đã được merge ở cấp Application trong
  `App.axaml` của suite, đủ cả `IconSave/IconAdd/IconSync/IconExport/IconUpgrade/IconFolder`.

### File đã tạo/sửa/xoá

| File | Việc |
|---|---|
| `suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.axaml` | **Viết lại toàn bộ** — màn Cài đặt hoàn chỉnh, 1 tiêu đề, 5 section |
| `suite/Shopee.Suite/Modules/Settings/SettingsView.axaml` (+`.axaml.cs`) | **XOÁ** |
| `orders/XuLyDonShopee.App/Views/SettingsView.axaml` (+`.axaml.cs`) | **XOÁ** |
| `suite/Shopee.Suite/App.axaml` | Gỡ DataTemplate `SettingsViewModel → SettingsView`, sửa comment |
| `orders/XuLyDonShopee.App/ViewModels/SettingsViewModel.cs` | Bỏ 3 property `NotifyWebhookUrl*`, `NotifySavedMessage`, command `SaveNotifyUrl`, 3 dòng nạp trong `Reload()`, 5 dòng `NotifySavedMessage = null;` ở các command khác, `using System.Collections.Generic` (hết dùng); cập nhật xmldoc class |
| `CHANGELOG.md` | Thêm mục `## Chưa phát hành` trên cùng (KHÔNG bump `version.txt`) |

`UnifiedSettingsView.axaml.cs` giữ nguyên. KHÔNG đụng `SettingsRepository`, `OrderNotifyService`,
`GsheetConfigSync`, `OrdersModuleHost`, `Theme.axaml`, các màn khác của orders.

### Kết quả kiểm chứng (số liệu thật)

- `dotnet build ShopeeSuite.sln` → **Build succeeded. 0 Warning(s), 0 Error(s)**.
- `dotnet test orders/XuLyDonShopee.Tests` → **Passed! Failed: 0, Passed: 1459, Skipped: 0, Total: 1459** —
  **không phải sửa test nào**; `SettingsRepositoryTests` + `OrderNotifyServiceTests` giữ nguyên byte.
- Grep `NotifyWebhookUrl|NotifySavedMessage|SaveNotifyUrl` trong `orders/XuLyDonShopee.App/`: chỉ còn **3 lời gọi
  getter repository** trong `Services/OrderPersistPipeline.cs` (315/421/554 — đường gửi tin, đúng như plan giữ);
  ViewModels + Views = **0**. Trong `XuLyDonShopee.Core/`: **8** kết quả (repository + service còn nguyên).
- `find -name SettingsView.axaml*` (trừ bin/obj) = **0 file**. Grep `SettingsView` trong source chỉ còn khớp
  `SettingsViewModel` (VM vẫn sống) + 1 comment ở `App.axaml`.
- **Ảnh chụp màn hình thật (render offscreen, KHÔNG đụng dữ liệu máy):** đã dựng harness Avalonia headless +
  Skia nạp đúng `Themes/Theme.axaml` + `Icons.axaml` + converter của suite, `new UnifiedSettingsView()` với
  DataContext GIẢ, chụp 2 chế độ:
  - `settings-full.png` (Full/Workspace): đủ 5 section theo thứ tự, 1 tiêu đề, không TabControl.
  - `settings-shopee.png` (`ShowsWorkspaceSettings=false`): section **Hiệu năng + Đồng bộ nhiều máy ẩn đúng**,
    section Đơn hàng hiện đủ 3 card (Tự động hóa · Trình duyệt · Đồng bộ Google Sheet), **không có card webhook**.
  - Ảnh nằm ở scratchpad phiên: `…\scratchpad\settings-full.png` / `…\scratchpad\settings-shopee.png`.
  - Nhờ ảnh này bắt được **2 lỗi bố cục đã sửa ngay**: (a) `MaxWidth` + HorizontalAlignment mặc định (Stretch)
    làm 3 dòng chú thích ở section "Chế độ ứng dụng" bị **canh giữa** → thêm `HorizontalAlignment="Left"`;
    (b) header để tiêu đề và chip trạng thái **chồng lớp** trong cùng ô Grid → đổi thành Grid 2 cột `*,Auto`.
- **KHÔNG chạy app thật** (`dotnet run --project suite/Shopee.Suite`): lúc thực thi máy **đang chạy bản cài
  production** `C:\Users\…\AppData\Local\ShopeeSuite\current\ShopeeSuite.exe` (PID 33732, mở từ 01:23). Mở thêm
  instance thứ hai sẽ dùng chung `app.db`, chung `machine_id` khi heartbeat lên Hub và tranh cổng cầu nối 47821
  → rủi ro làm hỏng phiên đang chạy của người dùng. Render offscreen ở trên là bản thay thế (đã thấy layout thật).

### Chênh so với plan (đều là bố cục, đã cân nhắc)

1. Chip trạng thái header thêm `IsVisible` theo `Suite.Status` (plan ghi "giữ nguyên như header cũ"): tránh một
   ô xám rỗng nằm chình ình khi chưa có thông báo. Thuần hiển thị, không đụng VM.
2. Header đổi từ Grid 1 ô (chồng lớp, y như bản cũ) sang Grid 2 cột `*,Auto` — chống đè chữ ở cửa sổ hẹp.
3. Nhãn section khai bằng `UserControl.Styles` cục bộ (`TextBlock.sectionLabel`) thay vì lặp 5 lần thuộc tính.
   **Không** đụng `Theme.axaml` toàn cục.
4. Section "Đơn hàng": tiêu đề card ("Tự động hóa" / "Trình duyệt" / "Đồng bộ Google Sheet") dùng `Classes="h2"`
   bên TRONG card cho khớp các card của suite, thay vì nhãn `Classes="section"` riêng của orders.

### Việc cần người điều phối quyết (ngoài phạm vi plan)

- `OrderNotifyService.KiemTraUrl` (Core) nay **0 caller production** — chỉ còn test gọi (nó vốn chỉ phục vụ
  `SaveNotifyUrl` của VM). Plan cấm đụng `OrderNotifyService` nên đã **giữ nguyên**; nếu muốn dọn 0-caller thì
  mở việc riêng.
- Cùng lý do, xmldoc `OrderNotifyService.cs:76` ("`public` vì màn Cài đặt (SettingsViewModel) dùng để validate
  URL") nay **lỗi thời** — chưa sửa vì nằm trong file plan cấm đụng.
- Chưa bump `version.txt` và mục CHANGELOG để tiêu đề `## Chưa phát hành` — chờ người điều phối đánh version.
