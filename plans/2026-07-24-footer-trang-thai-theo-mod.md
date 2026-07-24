# Plan: Thanh trạng thái (footer) theo chế độ — Shopee + Workspace, Full hiện cả 2

- **Ngày:** 2026-07-24
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)
- **Nền:** Shell `MainWindow` có 3 row (tab / ribbon / nội dung), KHÔNG có footer chung. Thanh trạng thái hiện nằm TRONG orders `MainView` (đáy DockPanel) → chỉ hiện khi tab Shopee active. User muốn Workspace cũng có thanh, và **mod Full hiện CẢ 2 phần cùng lúc**.

## 1. Mục tiêu

Chuyển thanh trạng thái thành **footer cấp SHELL** (Row mới ở `MainWindow`) gồm 2 đoạn:
- **Đoạn Shopee** (hiện khi `AppModeStore.ShowsShopee`): `N tài khoản · N đơn hàng · N proxy · Trình duyệt: X` — tái dùng counter sẵn có của orders `MainViewModel`.
- **Đoạn Workspace** (hiện khi `AppModeStore.ShowsWorkspace`): `N tài khoản BigSeller · N shop · N acc Shopee · N proxy · N máy online · Trình duyệt: Brave` (chốt với user).
- **Full** = cả 2 đoạn + vạch ngăn ở giữa. **Shopee**/**Workspace** = chỉ 1 đoạn tương ứng.

Bỏ thanh trạng thái trong orders `MainView` (đã chuyển lên shell) — tránh 2 thanh chồng khi ở tab Shopee.

## 2. Nguồn dữ liệu Workspace (đã khảo sát)

| Counter | Nguồn |
|---|---|
| TK BigSeller | `Shopee.Core.BigSeller.BigSellerStore.Shared.Accounts.Count` |
| Shop | `BigSellerStore.Shared.Accounts.Sum(a => a.Shops.Count)` |
| Acc Shopee | `Shopee.Core.Accounts.AccountStore.Shared.Accounts.Count` |
| Proxy | `Shopee.Core.Proxy.KiotProxyPoolStore.Shared.Count` |
| Máy online | `Shopee.Core.Coordination.CoordinationRuntime.Hub?.CurrentFleet?.Machines.Count ?? 0` (chỉ đầy đủ trên máy Hub; máy thường có thể 0 — chấp nhận) |
| Trình duyệt | "Brave" (suite dùng Brave; không có browser-choice như orders) |

Sự kiện làm mới (đăng ký nếu có): `KiotProxyPoolStore.Shared.Changed`; `CoordinationRuntime.Hub?.Changed`; `AccountStore`/`BigSellerStore` Changed NẾU có (kiểm tra — nếu không có, làm mới khi `SelectedTab` đổi làm catch-all). Marshal về UI thread (`UiThread.Post`).

## 3. Các bước

### B1. VM mới `WorkspaceStatusViewModel`
`suite/Shopee.Suite/ViewModels/WorkspaceStatusViewModel.cs` (ObservableObject): 6 property string
`BigSellerText`/`ShopText`/`ShopeeAccountText`/`ProxyText`/`MachineText`/`BrowserText`. Method `Refresh()` tính lại từ các store trên (bọc try/catch — store lỗi → giữ giá trị cũ/rỗng, KHÔNG ném). Ctor: `Refresh()` + đăng ký các event Changed có sẵn (mỗi handler `UiThread.Post(Refresh)`). Best-effort: store nào không có event thì bỏ (đã có catch-all ở B2).

### B2. `ShellViewModel`
- Thêm property: `public bool ShowShopeeStatus`, `public bool ShowWorkspaceStatus`, `public bool ShowStatusSeparator => ShowShopeeStatus && ShowWorkspaceStatus;` (set từ `sp`/`ws` đã tính ở ctor).
- Thêm `public OrdersMainViewModel? ShopeeStatusVm` = `ordersVm` (đã có biến; gán khi dựng — null nếu không có Shopee). Footer đoạn Shopee bind qua nó.
- Thêm `public WorkspaceStatusViewModel? WorkspaceStatus` — dựng khi `ws` (trong khối `if (ws)`), gán field/property. null khi không có Workspace.
- Catch-all làm mới: trong `OnSelectedTabChanged` gọi `WorkspaceStatus?.Refresh()` (rẻ, đảm bảo số mới khi quay lại).
- Đảm bảo orders `MainViewModel` đã tính `StatusAccountsText/OrdersText/ProxiesText/BrowserText` NGAY khi dựng (nếu hiện chỉ tính lúc Reload màn → thêm 1 lần tính ở ctor MainViewModel HOẶC gọi refresh khi shell dựng — kiểm & xử để footer có số ngay cả khi chưa mở tab Shopee).

### B3. `MainWindow.axaml` — thêm footer (Row 3)
- Grid `RowDefinitions="58,Auto,*"` → `"58,Auto,*,Auto"`. `ContentControl` giữ `Grid.Row="2"`.
- Thêm `Border Grid.Row="3"` (nền `CardBackgroundBrush` + viền trên `BorderBrush`, cao ~30, padding 16,0) chứa `StackPanel` ngang:
  - **Đoạn Shopee** (`IsVisible="{Binding ShowShopeeStatus}"`): StackPanel ngang Spacing 14, các `TextBlock` bind
    `ShopeeStatusVm.StatusAccountsText` · `ShopeeStatusVm.StatusOrdersText` · `ShopeeStatusVm.StatusProxiesText` · `ShopeeStatusVm.StatusBrowserText` (xen `·`).
  - **Vạch ngăn** (`Rectangle` Width=1 hoặc TextBlock "·", `IsVisible="{Binding ShowStatusSeparator}"`).
  - **Đoạn Workspace** (`IsVisible="{Binding ShowWorkspaceStatus}"`): bind
    `WorkspaceStatus.BigSellerText` · `.ShopText` · `.ShopeeAccountText` · `.ProxyText` · `.MachineText` · `.BrowserText`.
- Style bằng tài nguyên SUITE (Theme.axaml: `TextSecondaryBrush`, `BorderBrush`, `UiFont`) — KHÔNG dùng class `statusBar/statusText` của orders (cô lập trong subtree orders). Font ~12, màu phụ.

### B4. Bỏ thanh trong orders `MainView.axaml`
- Xóa khối `<Border DockPanel.Dock="Bottom" Classes="statusBar">…</Border>` (dòng ~40–52). GIỮ nguyên `MainViewModel.Status*Text` (shell footer bind qua `ShopeeStatusVm`). Style `statusBar/statusText/statusSep` trong orders Controls.axaml để nguyên (vô hại nếu không dùng).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build suite/Shopee.Suite` XANH; `dotnet build orders/XuLyDonShopee.App` XANH; test hiện có giữ xanh.
- [ ] Diff đúng phạm vi: ShellViewModel + WorkspaceStatusViewModel(mới) + MainWindow.axaml + MainView.axaml (+ MainViewModel nếu cần tính status lúc ctor).
- [ ] (Verify thật) **Full**: footer hiện CẢ 2 đoạn (Shopee trái + Workspace phải, có vạch ngăn). **Shopee**: chỉ đoạn Shopee. **Workspace**: chỉ đoạn Workspace. Số liệu đúng (đổi acc/proxy/shop thấy cập nhật; máy online đúng trên máy Hub). Không còn thanh cũ trùng ở tab Shopee.

## 5. Rủi ro & lưu ý

- **Bind cross-namespace:** `MainWindow` KHÔNG đặt `x:DataType` ở Window → binding phản chiếu, `ShopeeStatusVm.StatusAccountsText` chạy được dù type ở namespace orders. Nếu opus thêm compiled-binding thì phải khai đúng type.
- **Máy online:** `CoordinationRuntime.Hub` null khi tắt đồng bộ → `?? 0`. Máy thường (không Hub) không thấy đủ danh sách máy → hiện số nhỏ/0; user đã chấp nhận.
- **Refresh timing:** counter đổi ít (import acc/shop). Đăng ký event có sẵn + refresh khi đổi tab là đủ; KHÔNG thêm timer nền.
- **Không đụng nghiệp vụ:** chỉ đọc-hiển thị; không thêm logic chạy/dừng. Không đổi `AppMode`/gate tab.
- **Orders standalone:** app orders nay luôn nhúng trong suite → bỏ thanh của nó an toàn.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
