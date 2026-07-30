# Plan: Làm lại màn Cài đặt (một hệ style duy nhất) + bỏ card webhook phía client

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
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

<để trống>
