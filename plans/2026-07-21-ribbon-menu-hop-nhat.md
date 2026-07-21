# Plan: Đại tu menu Shopee Suite thành Ribbon kiểu Word/Excel mới + gộp 2 màn Cài đặt

- **Ngày:** 2026-07-21
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nơi làm:** worktree `D:\Projects\shopee-suite-wt-fresh-profile`, nhánh `task/ribbon-menu`
  (nối tiếp nhánh checkbox — đã có ProfileJanitor/checkbox trong cây). TUYỆT ĐỐI không đọc/ghi
  cây làm việc chính `D:\Projects\shopee-suite` (agent khác đang làm phần hub ở đó).

## 1. Bối cảnh & mục tiêu (đã chốt với người dùng 2026-07-21)

Cấu trúc menu mới — 4 tab kiểu Ribbon Office hiện đại:

| Tab | Nội dung |
|---|---|
| **Workspace** | Gom TOÀN BỘ phần suite cũ: BigSeller Workspace · Dữ liệu sản phẩm · Shopee Search · Tài khoản & Proxy · Trạng thái & Giao việc |
| **Cấu hình BigSeller** | Màn Cấu hình BigSeller như cũ |
| **Đơn hàng** | 4 màn con của module đơn hàng LÊN DẢI RIBBON: Tài khoản · Đơn hàng · Chạy tự động · Proxy (BỎ nav nội bộ cũ; màn Cài đặt của đơn hàng RỜI sang tab Cài đặt chung) |
| **Cài đặt** | GỘP 2 màn cài đặt (suite + đơn hàng) vào MỘT màn, chia section |

Ribbon **kèm nút hành động chính** (không chỉ điều hướng): hoist các COMMAND CÓ SẴN của
ViewModel lên ribbon — TUYỆT ĐỐI không viết logic nghiệp vụ mới, chỉ bind lại.

Layout đích (Office-modern):
```
┌──────────────────────────────────────────────────────────────┐
│ [logo]  Workspace │ Cấu hình BigSeller │ Đơn hàng │ Cài đặt   │  ← tab strip
├──────────────────────────────────────────────────────────────┤
│ ┌ Màn hình ─────────────┐ ┌ Hành động ─────────┐             │  ← dải ribbon
│ │ [🗂][📦][🔍][👥][🖥] │ │ [⟳ Sync] [■ Dừng]  │             │    (nhóm + nhãn đáy)
│ └───────────────────────┘ └────────────────────┘             │
├──────────────────────────────────────────────────────────────┤
│                  (nội dung màn đang chọn)                     │
└──────────────────────────────────────────────────────────────┘
```

Hiện trạng liên quan (khảo sát sẵn):
- Shell: `suite/Shopee.Suite/MainWindow.axaml` (topbar 58px + ListBox `topnav` + ContentControl
  `Current`), `ViewModels/ShellViewModel.cs` (Modules tạo 1 lần dòng 34-96, PrepareShutdownAsync
  57-63, RequestNavigate 91-92, Welcome mặc định `_selected=null`), `ViewModels/ModuleItem.cs`,
  `Infrastructure/AppIcons.cs`, map VM→View bằng DataTemplate ở `App.axaml` (+ ViewLocator orders).
- Module đơn hàng: `orders/.../Views/MainView.axaml` (DockPanel: topbar nav + statusBar +
  ContentControl `CurrentViewModel`), `ViewModels/MainViewModel.cs` (NavItems 5 mục,
  SelectedNavIndex switch 5 nhánh có gọi `Reload()` từng màn — dòng ~79-106).
- Cài đặt suite: `Modules/Settings/SettingsView(.axaml)/SettingsViewModel` (hiệu năng, đồng bộ
  Hub, nút "Cập nhật & khởi động lại"). Cài đặt đơn hàng: `orders/.../Views/SettingsView.axaml`
  + `SettingsViewModel` (trình duyệt, thư mục phiếu, chu kỳ, GSheet, webhook) — view TỰ CHỨA
  resource (ModuleResources) nên nhúng ở đâu cũng an toàn.

## 2. Phạm vi

- **Làm (suite):**
  - `ViewModels/RibbonModels.cs` (MỚI): `RibbonTab { Title, Groups }`,
    `RibbonGroup { Title, Items }`, item 2 loại: `RibbonScreenItem { Title, Icon, object ScreenVm,
    bool IsActive }` (điều hướng) và `RibbonActionItem { Title, Icon, ICommand }` (+ dạng toggle
    checkbox khi cần bind bool).
  - `ShellViewModel`: GIỮ NGUYÊN toàn bộ khởi tạo VM/worker/dispatcher/PrepareShutdown/
    RemoteUpdate hiện có; thay `Modules`/`Selected` bằng `Tabs` (4 RibbonTab) + `SelectedTab` +
    `CurrentScreen` (nhớ màn đang chọn RIÊNG cho từng tab — quay lại tab thấy đúng màn cũ).
    `RequestNavigate` của Workspace map sang chọn tab "Cấu hình BigSeller". Mặc định mở tab
    Workspace (màn BigSeller Workspace) — bỏ Welcome khỏi luồng (GIỮ file WelcomeView/VM dormant,
    không xóa); GoHome (bấm logo) → tab Workspace.
  - `MainWindow.axaml`: dựng tab strip + dải ribbon (nhóm có nhãn đáy + divider dọc, nút to
    icon 24 trên nhãn dưới, nút nav đang active tô accent như Office) + ContentControl. Style
    ribbon viết trong `Themes/Theme.axaml` hoặc file style riêng include vào App.axaml — dùng
    token màu sẵn có của suite, KHÔNG hardcode màu mới lung tung.
  - Tab **Đơn hàng**: nhóm "Màn hình" = 4 RibbonScreenItem trỏ thẳng 4 VM con của orders
    (`MainViewModel` cần expose 4 VM con + `SettingsVm` qua property public read-only — thêm bên
    orders, xem dưới). LƯU Ý: chuyển màn phải gọi đúng `Reload()` như switch trong
    `OnSelectedNavIndexChanged` cũ (giữ hành vi nạp lại) — cách gọn: ribbon vẫn set
    `MainViewModel.SelectedNavIndex` (0-3) thay vì tự quản, tab content vẫn là `MainView`
    (đã bỏ topbar). Nhóm "Hành động": hoist command CÓ SẴN của `AccountsViewModel` (khảo sát tên
    thật: Sync trọn gói / Kiểm tra / Xử lý đơn cho các dòng tick, Dừng...) — chọn 3-4 nút chính;
    nhóm "Tùy chọn": CHUYỂN checkbox "Xóa profile và tạo lại" từ toolbar AccountsView lên ribbon
    (bind `AccountsViewModel.XoaProfileTaoLai` sẵn có, gỡ checkbox khỏi AccountsView.axaml).
  - Tab **Workspace**: nhóm "Màn hình" = 5 nút (Workspace/Dữ liệu/Search/Tài khoản & Proxy/
    Trạng thái); nhóm "Hành động": nút "Dừng jobs" (gọi đúng các lệnh dừng đã dùng trong
    PrepareShutdownAsync: update.StopAllSingle + scrape.StopCommand + search.StopCommand khi
    CanExecute — bind qua một RelayCommand mới trong ShellViewModel CHỈ gọi lệnh sẵn có).
  - Tab **Cấu hình BigSeller**: nhóm "Màn hình" = 1 nút; hành động: khảo sát command public an
    toàn của BigSellerViewModel, hoist 0-2 nút (không có thì thôi — ghi báo cáo).
  - Tab **Cài đặt**: content = view gộp MỚI `Modules/Settings/UnifiedSettingsView.axaml` (+VM
    mỏng giữ 2 VM con): ScrollViewer 2 section — "Shopee Suite" nhúng `SettingsView` suite,
    "Đơn hàng" nhúng `orders SettingsView` (view orders tự chứa resource nên nhúng thẳng, truyền
    DataContext = orders SettingsViewModel); header section rõ ràng. Hành động ribbon: nút
    "Cập nhật & khởi động lại" (command sẵn có của suite SettingsViewModel).
  - Nếu module đơn hàng KHÔNG khởi tạo được (OrdersModuleHost.TryCreate null): ẩn tab Đơn hàng
    + section Đơn hàng trong Cài đặt (suite vẫn chạy như trước).
- **Làm (orders):**
  - `MainViewModel.cs`: expose 4+1 VM con read-only (`AccountsVm/OrdersVm/AutoRunVm/ProxiesVm/
    SettingsVm`); NavItems co lại 4 mục (bỏ Cài đặt khỏi nav — SelectedNavIndex 0-3, cập nhật
    switch; SettingsVm vẫn sống để tab Cài đặt chung dùng).
  - `Views/MainView.axaml`: BỎ topbar (Border Classes="topbar" chứa title + ListBox navTop);
    GIỮ statusBar đáy + ContentControl (tab content của Đơn hàng).
  - `Views/AccountsView.axaml`: gỡ checkbox "Xóa profile và tạo lại" (đã lên ribbon).
- **Không làm:**
  - KHÔNG đụng `server/`, `shared/`, `suite/Shopee.Core/` (trừ khi buộc phải thêm icon —
    AppIcons nằm ở Shopee.Suite, OK), KHÔNG đụng `Infrastructure/OrdersModuleHost.cs`
    (một việc khác sắp sửa file này — tránh conflict; mọi truy cập VM con đi qua MainViewModel).
  - KHÔNG viết logic nghiệp vụ mới cho nút hành động — chỉ bind command sẵn có.
  - KHÔNG xóa WelcomeView/ModuleItem cũ (dormant, dọn ở phase 5).
  - KHÔNG commit.

## 3. Kiểm chứng

- `dotnet build ShopeeSuite.sln -c Release` → 0 lỗi 0 warning mới.
- `dotnet test orders/.../XuLyDonShopee.Tests.csproj -c Release` → 754 baseline pass (sửa test
  nào gãy vì NavItems đổi 5→4 thì cập nhật test tương ứng — khai báo rõ trong báo cáo).
- Chạy app trong worktree (`dotnet run --project suite/Shopee.Suite/...`): chụp/mô tả từng tab —
  4 tab hiện đủ, chuyển màn được, nút hành động Enable/Disable theo CanExecute, tab Đơn hàng
  đủ 4 màn + status bar, tab Cài đặt hiện đủ 2 section, đổi giá trị lưu được (thử 1 setting mỗi
  bên). WDAC chặn thì ghi nhận để người dùng kiểm tay.

## 4. Tiêu chí nghiệm thu

- [ ] Build sạch; test ≥754 pass (test sửa phải khai báo lý do).
- [ ] 4 tab đúng cấu trúc chốt; không còn sidebar/topnav cũ; không còn nav nội bộ trong tab Đơn hàng.
- [ ] Mỗi tab nhớ màn đang chọn; chuyển màn Đơn hàng vẫn Reload() đúng như cũ.
- [ ] Cài đặt gộp 1 màn 2 section, lưu được cả 2 bên; màn Cài đặt cũ không còn lối vào trùng.
- [ ] Checkbox "Xóa profile và tạo lại" nằm trên ribbon Đơn hàng, vẫn persist.
- [ ] Style ribbon dùng token suite; 2 theme (suite xanh / orders cam trong content) không lẫn.
- [ ] Module đơn hàng hỏng init → tab Đơn hàng ẩn, app vẫn chạy.

## 5. Rủi ro & lưu ý

- `OnSelectedNavIndexChanged` của orders gọi `Reload()` từng màn — nếu ribbon by-pass nó, màn
  sẽ hiện dữ liệu cũ. Giải pháp trong plan (ribbon set SelectedNavIndex) tránh bẫy này.
- Command hoist lên ribbon phải là command CÓ SẴN + CanExecute đúng; nếu một hành động cần
  context màn (vd "Xử lý đơn" cần dòng tick ở màn Tài khoản) thì nút chỉ Enable khi hợp lệ —
  KHÔNG chế fallback.
- StaticResource trong view orders: đã tự chứa (ModuleResources) — nhúng SettingsView orders
  vào UnifiedSettingsView không cần thêm resource ở Application.
- Style Button global của suite (Theme.axaml ControlTheme) áp cho nút ribbon — viết style ribbon
  theo class riêng (vd `Button.ribbon`) trên nền đó.
- Đây là nhánh nối tiếp checkbox (`8792f31`) — diff sẽ gồm cả commit đó khi merge; không sao,
  Fable merge cả chuỗi.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
