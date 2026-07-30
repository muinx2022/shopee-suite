using Microsoft.Extensions.DependencyInjection;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Components.Pages;

/// <summary>Code-behind trang BigSeller ("/"). Markup ở Fleet.razor; dựng dòng/nhóm/tóm tắt ở FleetRowsBuilder.
/// Ở đây chỉ còn STATE của trang: lựa chọn acc/shop/tab (nằm trong URL), rewrite-trên-hub, modal bản đồ dòng.</summary>
public partial class Fleet
{
    private List<BigSellerAccount> _accounts = new();
    private List<FleetShopRow> _rows = new();
    private string _summary = "";
    private int _kMachines, _kRunning, _kQueued, _kCookies;
    private DateTimeOffset _accountsLoadedAt;

    private string _search = "";
    // Selection master-detail: chọn shop set cả 2 (acct = của shop); chọn acc thì _selShopKey = null.
    private string? _selAcctId;
    private (string AccountId, string ShopId)? _selShopKey;
    private FleetShopRow? _selRow;
    private string _tab = "action";
    // Trạng thái XEM của lưới per-shop (tab data) để URL-hoá: _gridInitial áp XUỐNG panel đúng 1 lần (deep-link/F5);
    // _gridView nhận LÊN từ panel (OnViewChanged) để ghi vào URL. Cả hai clear khi đổi chọn (Select*/SetTab).
    private (string Q, int P, int Ps)? _gridInitial;
    private (string Q, int P, int Ps)? _gridView;

    // Rewrite-tên-trên-hub (tab Hành động của shop hub-mode): số dòng chờ (cache theo shop), thông báo Start, và
    // theo dõi chuyển trạng thái để tự làm-mới số dòng chờ khi 1 lượt chạy xong.
    private int? _rwPending;
    private string? _rwPendingKey;
    private string _rwActionMsg = "";
    private (string Key, string Status)? _rwWatch;

    private bool PgReady => Sp.GetService<ProductDb>() is { IsReady: true };
    private static string RwKey(FleetShopRow r) => r.AccountId + " " + r.Sheet;

    // Ngữ cảnh modal bản đồ dòng (heatmap) — mở modal = dựng ctx, đóng = null.
    private RowMapCtx? _map;
    private sealed record RowMapCtx(string Title, string Op, string OpLabel, string AccountId, string Sheet, WorkLedgerRecord? Ledger);

    protected override void OnInitialized()
    {
        base.OnInitialized();
        ReloadAccounts();
        Rebuild();
        // F5/deep-link: khôi phục acc/shop/tab từ query SAU KHI _rows dựng xong. Không navigate ở đây (còn ở
        // giai đoạn prerender → NavigateTo sẽ ném redirect); URL chỉ được ghi lại khi user thao tác sau đó.
        RestoreSelectionFromUrl();
        RecomputeSummary();
    }

    // Modal heatmap dùng dữ liệu chụp lúc mở → dừng hẳn nhịp vẽ (cả StateHasChanged) khỏi diff 20k ô mỗi 2s.
    protected override bool ShouldTickRender() => _map is null;

    protected override void OnFleetTick()
    {
        // Config đọc lại tối đa mỗi 10s (khỏi đọc file mỗi 2s).
        if ((DateTimeOffset.UtcNow - _accountsLoadedAt).TotalSeconds > 10) ReloadAccounts();
        Rebuild();
        RecomputeSummary();
        MaybeAutoRefreshRewrite();
    }

    private void ReloadAccounts()
    {
        _accounts = Config.BigSellerAccounts();
        _accountsLoadedAt = DateTimeOffset.UtcNow;
        try { _kCookies = Db.ListFiles().Count(f => f.Name.StartsWith("cookies/", StringComparison.OrdinalIgnoreCase)); } catch { }
    }

    private void Rebuild()
    {
        _rows = FleetRowsBuilder.BuildRows(_accounts, Snap);

        // Giữ shop đang chọn qua các lần refresh (đọc lại tham chiếu mới theo key).
        _selRow = _selShopKey is { } k
            ? _rows.FirstOrDefault(r => r.AccountId == k.AccountId && r.ShopId == k.ShopId)
            : null;

        // Mặc định: chưa chọn gì → chọn acc đầu tiên theo thứ tự hiển thị (luôn có 1 mục được chọn).
        if (_selAcctId is null && _selShopKey is null)
            _selAcctId = FleetRowsBuilder.OrderedGroups(_rows, _accounts).FirstOrDefault()?.AccountId;
    }

    private void RecomputeSummary()
    {
        var s = FleetRowsBuilder.Summarize(_rows, Snap);
        _kMachines = s.Machines;
        _kRunning = s.Running;
        _kQueued = s.Queued;
        _summary = s.Text;
    }

    private List<FleetAcctGroup> Filtered() =>
        FleetRowsBuilder.Filter(FleetRowsBuilder.OrderedGroups(_rows, _accounts), _search).ToList();

    private string ListEmptyText =>
        _rows.Count == 0 && _accounts.Count == 0 ? "Chưa có tài khoản." : "Không khớp tìm kiếm.";

    private void OnSearchChanged(string search) => _search = search;

    private List<FleetShopRow> AcctShops() =>
        _selAcctId is null ? new() : _rows.Where(r => r.AccountId == _selAcctId).ToList();

    // Tên acc hiển thị ở header (chọn acc-không-shop cũng lấy được tên từ config).
    private string AcctName =>
        _selRow?.AccountName
        ?? (_selAcctId is null ? ""
            : _rows.FirstOrDefault(r => r.AccountId == _selAcctId)?.AccountName
              ?? _accounts.FirstOrDefault(a => a.Id == _selAcctId)?.DisplayName
              ?? "");

    // Acc này đang ở chế độ kho Hub (Postgres) — dùng để chọn chữ hiển thị (né "sheet/workbook" của excel-mode).
    private bool IsHubAcct(string accountId) =>
        _accounts.FirstOrDefault(a => a.Id == accountId)?.UsesHubData == true;

    private void SelectShop(FleetShopRow r)
    {
        _selShopKey = (r.AccountId, r.ShopId);
        _selAcctId = r.AccountId;
        _selRow = r;
        _rwActionMsg = "";
        // Shop kho-Hub → nạp trước số dòng chờ rewrite (không chặn render); ngược lại xoá cache.
        if (IsHubAcct(r.AccountId) && !string.IsNullOrEmpty(r.Sheet)) RefreshRewritePending(r.AccountId, r.Sheet);
        else { _rwPending = null; _rwPendingKey = null; }
        ClearGridView();
        UpdateUrl();
    }

    // ── Rewrite tên trên hub ─────────────────────────────────────────────────────────
    private void StartRewrite(FleetShopRow r)
    {
        _rwActionMsg = Rewrite.Start(r.AccountId, r.Sheet, r.ShopName) ?? "";   // null = xếp hàng OK
    }

    // Nạp lại số dòng chờ (fire-and-forget; key-guard chống ghi đè khi đổi chọn nhanh).
    private void RefreshRewritePending(string acct, string sheet)
    {
        var key = acct + " " + sheet;
        _rwPendingKey = key;
        _rwPending = null;
        _ = LoadRewritePendingAsync(acct, sheet, key);
    }

    private async Task LoadRewritePendingAsync(string acct, string sheet, string key)
    {
        try
        {
            var n = await Rewrite.CountPendingAsync(acct, sheet, CancellationToken.None);
            if (_rwPendingKey == key) { _rwPending = n; await InvokeAsync(StateHasChanged); }
        }
        catch { /* pg lỗi/đổi chọn → giữ "đang đếm" */ }
    }

    // Chuyển running → done ở shop đang xem → tự làm mới số dòng chờ (đã rewrite bớt).
    private void MaybeAutoRefreshRewrite()
    {
        if (_selRow is not { } r || !IsHubAcct(r.AccountId) || string.IsNullOrEmpty(r.Sheet)) { _rwWatch = null; return; }
        var key = RwKey(r);
        var st = Rewrite.GetState(r.AccountId, r.Sheet)?.Status ?? "idle";
        var prev = _rwWatch;
        _rwWatch = (key, st);
        if (prev is { } p && p.Key == key && p.Status == "running" && st == "done")
            RefreshRewritePending(r.AccountId, r.Sheet);
    }

    private static string RewriteStatusLine(RewriteState rw) => rw.Status switch
    {
        "queued" => "⏳ Đã xếp hàng — chờ tới lượt…",
        "running" => $"⏳ Đang chạy: {rw.Done}/{rw.Total} tên · {rw.RowsWritten:N0} dòng — {rw.Message}",
        "done" => rw.Message,
        "failed" => rw.Message,
        _ => rw.Message,
    };

    private void SelectAcct(string accountId)
    {
        _selAcctId = accountId;
        _selShopKey = null;
        _selRow = null;
        if (_tab is "stats" or "data") _tab = "action";   // mức acc không có tab Thống kê/Dữ liệu (chỉ ở mức shop)
        ClearGridView();
        UpdateUrl();
    }

    private void SetTab(string tab)
    {
        _tab = tab;
        ClearGridView();   // đổi tab → bỏ view lưới per-shop khỏi URL (tab data mới sẽ nạp view mặc định)
        UpdateUrl();
    }

    // Bỏ trạng thái xem lưới per-shop (deep-link + view hiện tại) — mọi lần đổi chọn/tab để URL không dính view cũ.
    private void ClearGridView() { _gridInitial = null; _gridView = null; }

    // Panel per-shop báo view đổi (tìm/trang/cỡ) → lưu + ghi vào URL. KHÔNG set ngược xuống panel (tránh loop).
    private void OnGridViewChanged((string Q, int P, int Ps) v)
    {
        _gridView = v;
        UpdateUrl();
    }

    // ── Selection ↔ URL query (?acc=…&shop=…&tab=…) — F5/deep-link giữ nguyên focus ──────────────────────
    private bool _selectionRestored;   // cờ once: chỉ khôi phục từ URL 1 lần lúc init, KHÔNG re-restore mỗi tick
    private bool _suppressUrl;          // chặn ghi URL trong lúc restore (SelectShop/SelectAcct gọi UpdateUrl bên trong)

    // Ghi selection hiện tại vào query (replace → khỏi phình history). Bỏ chọn → param rỗng bị loại.
    // Không chạy trong lúc restore/prerender.
    private void UpdateUrl()
    {
        if (_suppressUrl) return;
        var acc = _selAcctId ?? "";
        var shop = _selShopKey?.ShopId ?? "";
        var tab = _selAcctId is null ? "" : _tab;   // tab vô nghĩa khi chưa chọn gì
        // Trạng thái xem lưới per-shop CHỈ khi đang ở tab data của 1 shop (q=search đã áp, p>1, ps≠100).
        var gv = (tab == "data" && _selRow is not null) ? _gridView : null;

        UrlState.Update(Nav, replace: true,
            ("acc", acc),
            ("shop", shop),
            ("tab", tab),
            ("q", gv?.Q ?? ""),
            ("p", gv is { } g1 && g1.P > 1 ? g1.P.ToString() : ""),
            ("ps", gv is { } g2 && g2.Ps != 100 ? g2.Ps.ToString() : ""));
    }

    // Đọc query lúc vào trang: shop khớp (acc+shop) → chọn shop; chỉ acc khớp → chọn acc; không khớp gì (đã xoá) →
    // im lặng bỏ qua, giữ mặc định (Rebuild đã tự chọn acc đầu). _tab kẹp về giá trị hợp lệ theo cấp đang chọn.
    private void RestoreSelectionFromUrl()
    {
        if (_selectionRestored) return;
        _selectionRestored = true;
        var q = UrlState.Read(Nav);
        var qAcc = q["acc"];
        var qShop = q["shop"];
        var qTab = q["tab"];
        _suppressUrl = true;
        try
        {
            if (qAcc.Length > 0 && qShop.Length > 0
                && _rows.FirstOrDefault(r => r.AccountId == qAcc && r.ShopId == qShop) is { } row)
            {
                SelectShop(row);
                _tab = NormalizeTab(qTab, shopLevel: true);
                // Tab data → nạp view lưới per-shop từ query (áp xuống panel lần đầu + giữ trong URL).
                if (_tab == "data")
                {
                    _gridInitial = (q["q"], q.Int("p", 1, min: 2), q.Int("ps", 100));
                    _gridView = _gridInitial;
                }
            }
            else if (qAcc.Length > 0 && (_rows.Any(r => r.AccountId == qAcc) || _accounts.Any(a => a.Id == qAcc)))
            {
                SelectAcct(qAcc);
                _tab = NormalizeTab(qTab, shopLevel: false);
            }
        }
        finally { _suppressUrl = false; }
    }

    // Cấp shop có 4 tab (action/stats/data/config); cấp acc chỉ action/config. Giá trị lạ → "action".
    private static string NormalizeTab(string? tab, bool shopLevel) => shopLevel
        ? (tab is "action" or "stats" or "data" or "config" ? tab! : "action")
        : (tab is "action" or "config" ? tab! : "action");

    // Thêm 1 acc BigSeller rỗng, lưu ngay (đọc list+version tươi), rồi chọn nó ở tab Cấu hình để điền tiếp.
    private void AddAccount()
    {
        var all = Config.BigSellerAccounts();
        var version = Config.VersionOf(FileStoreConfigService.BigSellerFile);
        // Acc tạo trên web hub mặc định dùng KHO HUB (Postgres) — bản mới không còn workbook Excel; excel-mode
        // chỉ còn là đường chuyển tiếp cho acc cũ (tạo từ client hoặc migrate dở).
        var acc = new BigSellerAccount { Label = "Tài khoản mới", DataSource = "hub" };
        all.Add(acc);
        ConfigSave.Apply(Config, FileStoreConfigService.BigSellerFile, all, ref version, () => { });
        ReloadAccounts();
        Rebuild();
        RecomputeSummary();
        SelectAcct(acc.Id);
        SetTab("config");
    }

    // Panel con báo đã lưu/đổi cấu hình → parent nạp lại + vẽ lại ngay (khỏi chờ nhịp 2s).
    private void OnConfigChanged()
    {
        ReloadAccounts();
        Rebuild();
        RecomputeSummary();
    }

    // "➕ Thêm"/🗑 xoá shop ở lưới AccountConfigPanel đã LƯU → chỉ rebuild list trái, KHÔNG nhảy panel (shop mới
    // dùng được ngay, khỏi ép user vào màn cấu hình).
    private void OnShopAdded()
    {
        ReloadAccounts();
        Rebuild();
        RecomputeSummary();
    }

    // Nút ⚙ trong lưới shop → chọn shop đó + mở tab Cấu hình nâng cao (ShopConfigPanel).
    private void OnShopOpen(string shopId)
    {
        ReloadAccounts();
        Rebuild();
        RecomputeSummary();
        var row = _rows.FirstOrDefault(r => r.AccountId == _selAcctId && r.ShopId == shopId);
        if (row is not null) { SelectShop(row); SetTab("config"); }
    }

    // Xoá shop → bỏ chọn shop (vẫn đứng ở acc); xoá acc → bỏ chọn hết (Rebuild tự chọn acc đầu). UpdateUrl SAU
    // OnConfigChanged để bắt cả acc mới được Rebuild tự chọn.
    private void OnShopDeleted() { _selShopKey = null; _selRow = null; OnConfigChanged(); UpdateUrl(); }
    private void OnAcctDeleted() { _selAcctId = null; _selShopKey = null; _selRow = null; OnConfigChanged(); UpdateUrl(); }

    // Thế hệ của các combo đặt-tay ledger — nhảy sau mỗi lần đặt để @key đổi → element dựng lại về ⋯.
    private int _cellsetGen;

    private void SetLedger(string bs, string shop, string sheet, string op, string? status)
    {
        if (string.IsNullOrEmpty(status)) return;
        Db.SetLedgerStatus($"{bs}__{shop}__{op}", bs, shop, sheet, op, status);
        _cellsetGen++;
        FleetState.Refresh();
    }

    /// <summary>Mở bản đồ dòng của 1 việc trên shop đang chọn: chỉ dựng ctx → RowMapModal tự đọc sheet + tô dòng.</summary>
    private void OpenMap(string op)
    {
        if (_selRow is null) return;
        var shop = _selRow;
        // Acc hub-mode: "sheet" là ngăn nội bộ (= GUID) → KHÔNG phô ra tiêu đề; chỉ excel-mode mới hiện tên sheet.
        var hub = _accounts.FirstOrDefault(a => a.Id == shop.AccountId)?.UsesHubData == true;
        var title = hub
            ? $"{OpLabel(op)} · {shop.ShopName}"
            : $"{OpLabel(op)} · {shop.ShopName} · sheet '{shop.Sheet}'";
        _map = new RowMapCtx(
            title, op, OpLabel(op), shop.AccountId, shop.Sheet,
            Snap.Ledger.FirstOrDefault(l => l.Key == $"{shop.AccountId}__{shop.ShopId}__{op}"));
    }

    // ── Lối sang trang 🎯 Giao việc (/dispatch) ───────────────────────────────────
    /// <summary>Link mở trang Giao việc đã lọc sẵn shop này. Tên tham số ĐÚNG như <c>Dispatch.RestoreFromUrl</c>
    /// đọc: <c>acct</c> = id tài khoản, <c>q</c> = ô tìm (lọc ở mức tài khoản), <c>f</c> = chip trạng thái. Ép
    /// <c>f=all</c> vì trang đó mặc định lọc "Chưa xong" — shop đã ✓ hết sẽ không hiện dòng nào.</summary>
    private static string DispatchLink(FleetShopRow r) =>
        $"/dispatch?acct={Uri.EscapeDataString(r.AccountId)}&q={Uri.EscapeDataString(r.ShopName)}&f=all";

    /// <summary>Link mở trang Giao việc đã lọc sẵn 1 tài khoản (giữ chip mặc định "Chưa xong").</summary>
    private static string DispatchLink(string accountId) =>
        "/dispatch?acct=" + Uri.EscapeDataString(accountId);

    private static string OpLabel(string op) => FleetViewProjection.OperationLabel(op);
}
