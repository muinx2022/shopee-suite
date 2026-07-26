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

    partial void OnSelectedTabChanged(RibbonTab? value)
    {
        OnPropertyChanged(nameof(CurrentScreen));
        // Catch-all làm mới footer Workspace khi quay lại tab (rẻ) — bù cho counter không có event Changed (vd máy online).
        WorkspaceStatus?.Refresh();
    }

    // ══════════ Footer trạng thái (cấp shell): 2 đoạn Shopee + Workspace theo chế độ ══════════
    /// <summary>Hiện đoạn Shopee của footer (chế độ có module đơn hàng).</summary>
    public bool ShowShopeeStatus { get; }
    /// <summary>Hiện đoạn Workspace của footer (chế độ có nhóm Workspace).</summary>
    public bool ShowWorkspaceStatus { get; }
    /// <summary>Vạch ngăn giữa 2 đoạn — chỉ khi cả 2 cùng hiện (chế độ Full).</summary>
    public bool ShowStatusSeparator => ShowShopeeStatus && ShowWorkspaceStatus;
    /// <summary>Đoạn "Trình duyệt" của thanh trạng thái lấy từ module đơn hàng — CHỈ khi không có Workspace
    /// (Workspace có counter trình duyệt riêng); tránh hiện 2 đoạn trình duyệt trùng nhau ở chế độ Full.</summary>
    public bool ShowOrdersBrowserStatus => ShowShopeeStatus && !ShowWorkspaceStatus;
    /// <summary>Counter đoạn Shopee = MainViewModel đơn hàng (Status*Text). null nếu không có Shopee.</summary>
    public OrdersMainViewModel? ShopeeStatusVm { get; }
    /// <summary>Counter đoạn Workspace (6 số từ các kho dùng chung). null nếu không có Workspace.</summary>
    public WorkspaceStatusViewModel? WorkspaceStatus { get; }

    /// <summary>Phiên bản app cho đoạn cuối thanh trạng thái ("v1.2.3") — đọc từ assembly (nướng lúc build
    /// từ <c>version.txt</c>), KHÔNG ghi cứng.</summary>
    public string AppVersionText => $"v{AppInfo.Version}";

    /// <summary>Đoạn đầu thanh trạng thái: "Đang chạy · N job" / "Rảnh · không có job".</summary>
    [ObservableProperty] private string _jobStatusText = "Rảnh · không có job";

    public ShellViewModel()
    {
        // ══════════ Chế độ ứng dụng: chỉ dựng NHÓM module cần thiết cho gọn ══════════
        // Đọc 1 lần (đổi chế độ = restart nên không đổi giữa vòng đời). ws = có nhóm Workspace (gồm Cấu hình
        // BigSeller); sp = có module Shopee (đơn hàng). Tab Cài đặt LUÔN có.
        var mode = AppModeStore.Shared.Current;
        bool ws = AppModeStore.ShowsWorkspace(mode);
        bool sp = AppModeStore.ShowsShopee(mode);
        ShowWorkspaceStatus = ws;
        // ShowShopeeStatus set SAU khi biết ordersVm (gate theo module dựng ĐƯỢC, không chỉ theo chế độ) — tránh
        // đoạn Shopee RỖNG + vạch ngăn thừa nếu OrdersModuleHost.TryCreate() fail ở chế độ Shopee/Full.

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

            // Footer trạng thái đoạn Workspace (6 counter đọc từ các kho dùng chung). Dựng cùng nhóm Workspace.
            WorkspaceStatus = new WorkspaceStatusViewModel();

            // Giao việc đa máy: worker (client tự chạy việc Hub giao).
            worker = new AssignmentWorker(scrape, update, search);
            var fleet = new FleetViewModel(worker);

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
                    new RibbonActionItem("Dừng jobs", "IconStop", StopWorkspaceJobsCommand,
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
                    new RibbonActionItem("Đăng nhập tất cả", "IconLogin", bigSeller.LoginAllCommand,
                        "Tự đăng nhập headless mọi tài khoản đủ Email + Mật khẩu rồi lưu cookie"),
                    new RibbonActionItem("Dừng", "IconStop", bigSeller.StopLoginCommand,
                        "Dừng tiến trình đăng nhập tất cả"),
                }),
            });
            _bigSellerTab = bigSellerTab;

            // Workspace bấm "Đăng nhập / cấu hình" → nhảy sang tab "Cấu hình BigSeller" (target luôn là bigSeller VM).
            workspace.RequestNavigate = _ => { SelectedTab = _bigSellerTab; };
        }
        _scrape = scrape; _update = update; _search = search;

        // ══════════ Đoạn "trạng thái job" của thanh trạng thái ══════════
        // Đếm việc đang chạy từ các VM ĐÃ CÓ (không thêm nghiệp vụ): scrape · update batch · update inline
        // theo shop · search. Các VM này sống suốt vòng đời app như shell nên KHÔNG cần gỡ handler (giống
        // cách WorkspaceViewModel theo dõi AnyRunning).
        if (scrape is not null) scrape.PropertyChanged += OnJobStateChanged;
        if (update is not null)
        {
            update.PropertyChanged += OnJobStateChanged;
            update.JobsChanged += () => UiThread.Post(RefreshJobStatus);
        }
        if (search is not null) search.PropertyChanged += OnJobStateChanged;
        RefreshJobStatus();

        var settings = new SettingsViewModel();

        // Module đơn hàng (phase 1b): CHỈ dựng khi chế độ có Shopee. Mở SQLite riêng qua OrdersModuleHost. Init
        // hỏng → TryCreate null → suite vẫn chạy, chỉ ẩn tab Đơn hàng + section Đơn hàng trong Cài đặt.
        var ordersVm = sp ? OrdersModuleHost.TryCreate() : null;
        ShopeeStatusVm = ordersVm;   // footer đoạn Shopee bind qua VM này (Status*Text đã tính ở ctor MainViewModel)
        ShowShopeeStatus = ordersVm is not null;   // module dựng được mới hiện đoạn Shopee (tránh đoạn rỗng + vạch ngăn thừa)

        // Màn Cài đặt GỘP: 1 màn (Chế độ ứng dụng + Shopee Suite + Đơn hàng). Section Đơn hàng ẩn khi ordersVm null.
        var unifiedSettings = new UnifiedSettingsViewModel(settings, ordersVm?.SettingsVm);

        // ══════════ DỪNG ÊM trước khi Velopack restart — guard null cho chế độ thu gọn ══════════
        UpdateService.PrepareShutdownAsync = async () =>
        {
            update?.StopAllSingle();   // chưa dựng / không có job → tự bỏ qua
            if (scrape is not null && scrape.StopCommand.CanExecute(null)) scrape.StopCommand.Execute(null);
            if (search is not null && search.StopCommand.CanExecute(null)) search.StopCommand.Execute(null);
            if (worker is not null) await worker.PrepareForShutdownAsync(TimeSpan.FromSeconds(40));
            // Module đơn hàng: kill hết phiên Brave trước khi restart.
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
            // Màn CHỈ ĐỌC đơn của MỌI máy (đọc thẳng Hub). Icon "dàn máy chủ" = ý ĐA MÁY/toàn hệ thống —
            // KHÔNG dùng lại Receipt (đã là "Đơn hàng" của máy này, đứng cạnh nhau sẽ không phân biệt được).
            var oHubOrders = new RibbonScreenItem("Đơn toàn hệ thống", AppIcons.Servers, ordersVm, 2,
                "Xem đơn của MỌI máy trên Hub (chỉ đọc — không xử lý ở đây)");
            // (Bỏ nút "Proxy" khỏi ribbon Shopee theo yêu cầu — màn proxy index 2 không còn điều hướng tới từ ribbon.)

            // Nhóm "Hành động" + "Tùy chọn" CHỈ có nghĩa ở màn "Tài khoản" (thao tác trên danh sách tài khoản).
            // Giữ tham chiếu để bật/tắt CẢ NHÓM theo màn đang chọn (làm mờ, không ẩn) — xem đoạn nối bên dưới.
            var oActionGroup = new RibbonGroup("Hành động", new object[]
            {
                new RibbonActionItem("Chọn tất cả", "IconCheckAll", acc.SelectAllCommand,
                    "Chọn / bỏ chọn toàn bộ tài khoản đang hiển thị"),
                new RibbonActionItem("Chạy đã chọn", "IconPlay", acc.RunSelectedCommand,
                    "Mở + đăng nhập + tự lặp shop cho các tài khoản đang tick"),
                new RibbonActionItem("Dừng đã chọn", "IconStop", acc.StopSelectedCommand,
                    "Dừng toàn bộ việc đang làm của các tài khoản đang tick"),
                new RibbonActionItem("Dừng tất cả", "IconStop", acc.StopAllCommand,
                    "Dừng mọi phiên đang chạy (đóng hết Brave)"),
            });
            var oOptionGroup = new RibbonGroup("Tùy chọn", new object[]
            {
                new RibbonToggleItem("Xóa profile và tạo lại", acc, nameof(acc.XoaProfileTaoLai),
                    () => acc.XoaProfileTaoLai, v => acc.XoaProfileTaoLai = v,
                    "Phiên mở mới sẽ xóa hồ sơ trình duyệt của tài khoản rồi tạo lại — phải đăng nhập lại. Áp cho mọi phiên mở mới."),
            });

            ordersTab = new RibbonTab("Shopee", new List<RibbonGroup>
            {
                new RibbonGroup("Màn hình", new object[] { oAccounts, oOrders, oHubOrders }),
                oActionGroup,
                oOptionGroup,
            });

            // Nối bật/tắt 2 nhóm với màn đang chọn: CHỈ màn "Tài khoản" (SelectedNavIndex==0) → bật; các màn
            // Đơn hàng/Proxy → làm mờ CẢ NHÓM (disable, KHÔNG ẩn). Gate ở mức NHÓM để ở màn Tài
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
                new RibbonActionItem("Cập nhật & khởi động lại", "IconUpgrade", settings.ApplyUpdateCommand,
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

    /// <summary>Cờ chạy của 3 VM đổi → tính lại chuỗi trạng thái job. (SearchViewModel cũng dùng đúng tên
    /// property "IsRunning" như UpdateProductViewModel nên 2 tên dưới đây phủ cả 3 VM.)</summary>
    private void OnJobStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScrapeViewModel.IsBusy) or nameof(UpdateProductViewModel.IsRunning))
            UiThread.Post(RefreshJobStatus);
    }

    /// <summary>Đếm việc ĐANG CHẠY từ dữ liệu sẵn có: scrape (1 phiên) · update batch (1 phiên) · update
    /// inline theo shop (đếm theo tk BigSeller) · search (1 phiên). Chế độ không có Workspace → luôn 0.</summary>
    private void RefreshJobStatus()
    {
        var n = 0;
        if (_scrape is { IsBusy: true }) n++;
        if (_update is not null)
        {
            if (_update.IsRunning) n++;
            foreach (var acct in Shopee.Core.BigSeller.BigSellerStore.Shared.Accounts)
                if (_update.IsUpdateRunning(acct.Id)) n++;
        }
        if (_search is { IsRunning: true }) n++;

        JobStatusText = n > 0 ? $"Đang chạy · {n} job" : "Rảnh · không có job";
    }

    /// <summary>Ctrl+1…4: chọn tab theo chỉ số (0-based). Chỉ số ngoài tập tab của chế độ hiện tại → bỏ qua.</summary>
    [RelayCommand]
    private void SelectTabAt(string? index)
    {
        if (!int.TryParse(index, out var i) || i < 0 || i >= Tabs.Count) return;
        SelectedTab = Tabs[i];
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
