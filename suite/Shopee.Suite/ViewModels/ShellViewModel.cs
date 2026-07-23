using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Infrastructure;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Modules.Accounts;
using Shopee.Suite.Modules.BigSeller;
using Shopee.Suite.Modules.Data;
using Shopee.Suite.Modules.Fleet;
using Shopee.Suite.Modules.Scrape;
using Shopee.Suite.Modules.Search;
using Shopee.Suite.Modules.Settings;
using Shopee.Suite.Modules.UpdateProduct;
using Shopee.Suite.Modules.Workspace;
using Shopee.Suite.Services;
using OrdersMainViewModel = XuLyDonShopee.App.ViewModels.MainViewModel;

namespace Shopee.Suite.ViewModels;

/// <summary>
/// ViewModel gốc của shell: dải RIBBON kiểu Word/Excel gồm tối đa 4 tab (Workspace · Cấu hình BigSeller ·
/// Shopee · Cài đặt). Tập tab hiển thị tuỳ "Chế độ ứng dụng" (<see cref="AppMode"/>): Full = tất cả;
/// Workspace = Workspace + Cấu hình BigSeller; Shopee = chỉ đơn hàng; tab Cài đặt LUÔN có. Mọi module
/// ViewModel được khởi tạo MỘT LẦN và giữ sống suốt vòng đời app; mỗi tab NHỚ màn đang chọn riêng nên quay
/// lại tab thấy đúng màn cũ. Toàn bộ nút hành động trên ribbon chỉ bind command CÓ SẴN của các ViewModel —
/// không thêm logic nghiệp vụ mới ở đây.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    // Giữ tham chiếu các VM có lệnh dừng job của Workspace để nút "Dừng jobs" trên ribbon gọi lại lệnh SẴN CÓ.
    // Nullable: chế độ KHÔNG có Workspace (vd Shopee) không dựng khối workspace → 3 field này null.
    private readonly ScrapeViewModel? _scrape;
    private readonly UpdateProductViewModel? _update;
    private readonly SearchViewModel? _search;

    /// <summary>Các tab trên dải ribbon (tập phụ thuộc chế độ ứng dụng).</summary>
    public ObservableCollection<RibbonTab> Tabs { get; } = new();

    [ObservableProperty] private RibbonTab? _selectedTab;

    /// <summary>Màn đang chọn RIÊNG cho từng tab (quay lại tab thấy đúng màn cũ).</summary>
    private readonly Dictionary<RibbonTab, object> _screenByTab = new();

    private RibbonTab? _workspaceTab;
    private RibbonTab? _bigSellerTab;
    private RibbonScreenItem? _workspaceHomeScreen;

    /// <summary>ViewModel màn đang hiển thị của tab đang chọn (nội dung ContentControl chính).</summary>
    public object? CurrentScreen =>
        SelectedTab is not null && _screenByTab.TryGetValue(SelectedTab, out var vm) ? vm : null;

    partial void OnSelectedTabChanged(RibbonTab? value) => OnPropertyChanged(nameof(CurrentScreen));

    public ShellViewModel()
    {
        // ══════════ Chế độ ứng dụng: chỉ dựng NHÓM module cần thiết cho gọn ══════════
        // Đọc 1 lần (đổi chế độ = restart nên không đổi giữa vòng đời). ws = có nhóm Workspace (gồm Cấu hình
        // BigSeller); sp = có module Shopee (đơn hàng). Tab Cài đặt LUÔN có.
        var mode = AppModeStore.Shared.Current;
        bool ws = AppModeStore.ShowsWorkspace(mode);
        bool sp = AppModeStore.ShowsShopee(mode);

        // Khối VM Workspace — CHỈ dựng khi chế độ có Workspace (gọn RAM + không chạy nền thừa). Biến nào còn
        // được tham chiếu BÊN NGOÀI block (field dừng-êm / closure PrepareShutdown) mới cần để nullable.
        ScrapeViewModel? scrape = null;
        UpdateProductViewModel? update = null;
        SearchViewModel? search = null;
        AssignmentWorker? worker = null;
        RibbonTab? workspaceTab = null;
        RibbonTab? bigSellerTab = null;

        if (ws)
        {
            // Màn gộp Workspace DÙNG CHUNG đúng 3 VM BigSeller/Scrape/Update với 3 màn cũ → state luôn đồng bộ.
            var bigSeller = new BigSellerViewModel();
            scrape = new ScrapeViewModel();
            update = new UpdateProductViewModel();
            search = new SearchViewModel();
            var workspace = new WorkspaceViewModel(bigSeller, scrape, update);

            // Màn "Dữ liệu sản phẩm" (kho Hub) — ctor không I/O (nạp ở EnsureLoaded).
            var data = new DataViewModel();
            var accounts = new AccountsViewModel();

            // Giao việc đa máy: worker (client tự chạy việc Hub giao) + dispatcher (máy Hub tự đẩy việc).
            worker = new AssignmentWorker(scrape, update, search);
            var fleet = new FleetViewModel(worker);
            // Dispatcher (tự đẩy việc) CHỈ chạy timer trên máy Hub (và chỉ chế độ có Workspace).
            if (Shopee.Core.Coordination.HubServerConfigStore.Shared.Current.Enabled) HubDispatcher.Shared.Start();

            // ── Tab 1: Workspace (gom toàn bộ 5 màn suite cũ) ──
            var wsWorkspace = new RibbonScreenItem("Workspace", AppIcons.Dashboard, workspace,
                toolTip: "BigSeller Workspace — Scrape · Import · Update theo từng shop");
            var wsData = new RibbonScreenItem("Dữ liệu", AppIcons.Inventory, data,
                toolTip: "Kho sản phẩm trên Hub — lọc · thêm/sửa · đã bán · SKU · xoá");
            var wsSearch = new RibbonScreenItem("Search", AppIcons.Search, search,
                toolTip: "Thống kê tìm kiếm sản phẩm");
            var wsAccounts = new RibbonScreenItem("Tài khoản & Proxy", AppIcons.People, accounts,
                toolTip: "Kho tài khoản Shopee dùng chung · Check tài khoản");
            var wsFleet = new RibbonScreenItem("Trạng thái", AppIcons.Servers, fleet,
                toolTip: "Theo dõi máy + Hub giao việc cho từng máy (đa máy)");
            _workspaceHomeScreen = wsWorkspace;

            workspaceTab = new RibbonTab("Workspace", new List<RibbonGroup>
            {
                new RibbonGroup("Màn hình", new object[] { wsWorkspace, wsData, wsSearch, wsAccounts, wsFleet }),
                new RibbonGroup("Hành động", new object[]
                {
                    new RibbonActionItem("Dừng jobs", "■", StopWorkspaceJobsCommand,
                        "Dừng các việc đang chạy (Scrape · Update · Search)"),
                }),
            });
            _workspaceTab = workspaceTab;

            // ── Tab 2: Cấu hình BigSeller ──
            var bsScreen = new RibbonScreenItem("Cấu hình BigSeller", AppIcons.Database, bigSeller,
                toolTip: "Tài khoản · workbook · cookie · shop · proxy");
            bigSellerTab = new RibbonTab("Cấu hình BigSeller", new List<RibbonGroup>
            {
                new RibbonGroup("Màn hình", new object[] { bsScreen }),
                new RibbonGroup("Hành động", new object[]
                {
                    new RibbonActionItem("Đăng nhập tất cả", "▶", bigSeller.LoginAllCommand,
                        "Tự đăng nhập headless mọi tài khoản đủ Email + Mật khẩu rồi lưu cookie"),
                    new RibbonActionItem("Dừng", "■", bigSeller.StopLoginCommand,
                        "Dừng tiến trình đăng nhập tất cả"),
                }),
            });
            _bigSellerTab = bigSellerTab;

            // Workspace bấm "Đăng nhập / cấu hình" → nhảy sang tab "Cấu hình BigSeller" (target luôn là bigSeller VM).
            workspace.RequestNavigate = _ => { SelectedTab = _bigSellerTab; };
        }
        _scrape = scrape; _update = update; _search = search;

        var settings = new SettingsViewModel();

        // Module đơn hàng (phase 1b): CHỈ dựng khi chế độ có Shopee. Mở SQLite riêng qua OrdersModuleHost. Init
        // hỏng → TryCreate null → suite vẫn chạy, chỉ ẩn tab Đơn hàng + section Đơn hàng trong Cài đặt.
        var ordersVm = sp ? OrdersModuleHost.TryCreate() : null;

        // Màn Cài đặt GỘP: 1 màn (Chế độ ứng dụng + Shopee Suite + Đơn hàng). Section Đơn hàng ẩn khi ordersVm null.
        var unifiedSettings = new UnifiedSettingsViewModel(settings, ordersVm?.SettingsVm);

        // ══════════ DỪNG ÊM trước khi Velopack restart — guard null cho chế độ thu gọn ══════════
        UpdateService.PrepareShutdownAsync = async () =>
        {
            update?.StopAllSingle();   // chưa dựng / không có job → tự bỏ qua
            if (scrape is not null && scrape.StopCommand.CanExecute(null)) scrape.StopCommand.Execute(null);
            if (search is not null && search.StopCommand.CanExecute(null)) search.StopCommand.Execute(null);
            if (worker is not null) await worker.PrepareForShutdownAsync(TimeSpan.FromSeconds(40));
            // Module đơn hàng: dừng vòng "Chạy tự động" + kill hết phiên Brave trước khi restart.
            await OrdersModuleHost.StopAsync();
        };
        // Lệnh update từ Hub → cùng đường dừng-êm + restart như nút tay.
        Shopee.Core.Coordination.HttpCoordinationHub.UpdateRequested =
            (hub, requestedAt) => RemoteUpdateService.Shared.OnCommand(hub, requestedAt);

        // ── Tab 3: Shopee (đơn hàng — 4 màn con LÊN ribbon; chỉ dựng khi module khởi tạo được) ──
        RibbonTab? ordersTab = null;
        if (ordersVm is not null)
        {
            var acc = ordersVm.AccountsVm;
            var oAccounts = new RibbonScreenItem("Tài khoản", AppIcons.People, ordersVm, 0, "Tài khoản shop");
            var oOrders = new RibbonScreenItem("Đơn hàng", AppIcons.Receipt, ordersVm, 1, "Theo dõi & xử lý đơn · in phiếu");
            var oAuto = new RibbonScreenItem("Chạy tự động", AppIcons.PlayCircle, ordersVm, 2, "Vòng chạy tự động");
            var oProxy = new RibbonScreenItem("Proxy", AppIcons.SwapHoriz, ordersVm, 3, "Kho proxy KiotProxy");

            // Nhóm "Hành động" + "Tùy chọn" CHỈ có nghĩa ở màn "Tài khoản" (thao tác trên danh sách tài khoản).
            // Giữ tham chiếu để bật/tắt CẢ NHÓM theo màn đang chọn (làm mờ, không ẩn) — xem đoạn nối bên dưới.
            var oActionGroup = new RibbonGroup("Hành động", new object[]
            {
                new RibbonActionItem("Chọn tất cả", "✓", acc.SelectAllCommand,
                    "Chọn / bỏ chọn toàn bộ tài khoản đang hiển thị"),
                new RibbonActionItem("Sync đã chọn", "⇊", acc.SyncSelectedCommand,
                    "Chạy trọn gói cho các tài khoản đang tick: mở trang → kiểm tra → xử lý đơn nếu có → sync"),
                new RibbonActionItem("Dừng đã chọn", "■", acc.StopSelectedCommand,
                    "Dừng toàn bộ việc đang làm của các tài khoản đang tick"),
                new RibbonActionItem("Dừng tất cả", "✕", acc.StopAllCommand,
                    "Dừng mọi phiên đang chạy (đóng hết Brave)"),
            });
            var oOptionGroup = new RibbonGroup("Tùy chọn", new object[]
            {
                new RibbonToggleItem("Xóa profile và tạo lại", acc, nameof(acc.XoaProfileTaoLai),
                    () => acc.XoaProfileTaoLai, v => acc.XoaProfileTaoLai = v,
                    "Phiên mở mới sẽ xóa hồ sơ trình duyệt của tài khoản rồi tạo lại — phải đăng nhập lại. Áp cho mọi phiên mở mới."),
                new RibbonToggleItem("Tự động xác nhận", acc, nameof(acc.TuDongXacNhan),
                    () => acc.TuDongXacNhan, v => acc.TuDongXacNhan = v,
                    "BẬT: khi Shopee bắt xác minh qua email, app tự tìm mail + bấm link 'TẠI ĐÂY' + chờ đăng nhập. TẮT: chỉ đăng nhập hộp thư rồi dừng cho bạn tự bấm."),
            });

            ordersTab = new RibbonTab("Shopee", new List<RibbonGroup>
            {
                new RibbonGroup("Màn hình", new object[] { oAccounts, oOrders, oAuto, oProxy }),
                oActionGroup,
                oOptionGroup,
            });

            // Nối bật/tắt 2 nhóm với màn đang chọn: CHỈ màn "Tài khoản" (SelectedNavIndex==0) → bật; các màn
            // Đơn hàng/Chạy tự động/Proxy → làm mờ CẢ NHÓM (disable, KHÔNG ẩn). Gate ở mức NHÓM để ở màn Tài
            // khoản các nút vẫn theo CanExecute riêng của command, không bị đè.
            var ordersMain = ordersVm; // capture non-null cho closure
            void SyncOrdersActionGroups()
            {
                var onAccounts = ordersMain.SelectedNavIndex == 0;
                oActionGroup.IsEnabled = onAccounts;
                oOptionGroup.IsEnabled = onAccounts;
            }
            SyncOrdersActionGroups();
            ordersMain.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ordersMain.SelectedNavIndex)) SyncOrdersActionGroups();
            };
        }

        // ── Tab 4: Cài đặt (gộp 2 màn cài đặt) — LUÔN có ở mọi chế độ ──
        var setScreen = new RibbonScreenItem("Cài đặt", AppIcons.Settings, unifiedSettings,
            toolTip: "Chế độ ứng dụng · cấu hình Shopee Suite + Đơn hàng");
        var settingsTab = new RibbonTab("Cài đặt", new List<RibbonGroup>
        {
            new RibbonGroup("Màn hình", new object[] { setScreen }),
            new RibbonGroup("Hành động", new object[]
            {
                new RibbonActionItem("Cập nhật & khởi động lại", "⬆", settings.ApplyUpdateCommand,
                    "Áp dụng bản đã tải + mở lại app (chỉ khả dụng khi đã tải xong bản mới)"),
            }),
        });

        // Thứ tự tab: Workspace → Cấu hình BigSeller → Shopee → Cài đặt (chỉ thêm cái đã dựng theo chế độ).
        if (workspaceTab is not null) Tabs.Add(workspaceTab);
        if (bigSellerTab is not null) Tabs.Add(bigSellerTab);
        if (ordersTab is not null) Tabs.Add(ordersTab);
        Tabs.Add(settingsTab);

        // Ráp lệnh điều hướng + trạng thái active cho mọi nút màn (post-wire vì cần tham chiếu tab đã dựng xong).
        foreach (var tab in Tabs)
            foreach (var scr in tab.Groups.SelectMany(g => g.Items).OfType<RibbonScreenItem>())
            {
                scr.OwnerTab = tab;
                var s = scr;
                s.ActivateCommand = new RelayCommand(() => ActivateScreen(s));
            }

        // Mặc định mỗi tab mở màn ĐẦU TIÊN (được nhớ riêng cho từng tab).
        foreach (var tab in Tabs)
        {
            var first = tab.Groups.SelectMany(g => g.Items).OfType<RibbonScreenItem>().FirstOrDefault();
            if (first is not null) { _screenByTab[tab] = first.ScreenVm; first.IsActive = true; }
        }

        // Mặc định mở tab Shopee → Workspace → (Cài đặt nếu chỉ còn nó). Không để null ở bất kỳ chế độ nào.
        SelectedTab = ordersTab ?? workspaceTab ?? settingsTab;
    }

    /// <summary>Chuyển màn đang hiển thị cho tab chứa nút. Với module đơn hàng: set SelectedNavIndex để giữ
    /// hành vi Reload() từng màn con như switch cũ; các màn suite: đổi thẳng ScreenVm. Tô active đúng nút.</summary>
    private void ActivateScreen(RibbonScreenItem item)
    {
        var tab = item.OwnerTab;
        if (tab is null) return;

        if (item.NavIndex >= 0 && item.ScreenVm is OrdersMainViewModel orders)
            orders.SelectedNavIndex = item.NavIndex;   // Reload() + đổi CurrentViewModel bên trong MainView

        _screenByTab[tab] = item.ScreenVm;
        foreach (var s in tab.Groups.SelectMany(g => g.Items).OfType<RibbonScreenItem>())
            s.IsActive = ReferenceEquals(s, item);

        if (ReferenceEquals(tab, SelectedTab)) OnPropertyChanged(nameof(CurrentScreen));
    }

    /// <summary>Nút "Dừng jobs" (ribbon Workspace): CHỈ gọi các lệnh dừng SẴN CÓ (như trong PrepareShutdownAsync),
    /// không thêm logic mới. Guard null: chế độ không có Workspace thì nút này không tồn tại nhưng vẫn an toàn.</summary>
    [RelayCommand]
    private void StopWorkspaceJobs()
    {
        _update?.StopAllSingle();
        if (_scrape is not null && _scrape.StopCommand.CanExecute(null)) _scrape.StopCommand.Execute(null);
        if (_search is not null && _search.StopCommand.CanExecute(null)) _search.StopCommand.Execute(null);
    }

    /// <summary>Bấm logo/brand → về tab Workspace (màn BigSeller Workspace). Chế độ không có Workspace
    /// (vd Shopee) → không điều hướng để khỏi set SelectedTab về null.</summary>
    [RelayCommand]
    private void GoHome()
    {
        if (_workspaceTab is null) return;
        SelectedTab = _workspaceTab;
        if (_workspaceHomeScreen is not null) ActivateScreen(_workspaceHomeScreen);
    }
}
