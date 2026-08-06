using Microsoft.AspNetCore.Components;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Components.Pages;

/// <summary>Code-behind trang Giao việc ("/dispatch"). Markup ở Dispatch.razor; dựng dòng/quỹ máy/nhãn nút ở
/// DispatchRowsBuilder.cs; hai vùng lớn nằm trong component riêng (DispatchKpiPanel, DispatchShopsGrid).
/// Ở đây còn STATE của trang (bộ lọc + máy đang chọn + tham số lượt giao — đều nằm trong URL) và mọi đường
/// GHI xuống hub (tạo/huỷ assignment, lệnh Đơn hàng).</summary>
public partial class Dispatch
{
    private static readonly (string Key, string Label)[] StateChips =
    [
        ("todo", "Chưa xong"), ("run", "Đang chạy"), ("int", "Gián đoạn"), ("all", "Tất cả"),
    ];

    // ── Nguồn (đọc lại chậm hơn nhịp fleet) ──
    private List<BigSellerAccount> _accounts = new();
    private DateTimeOffset _staticLoadedAt;

    private List<DispatchShopRow> _rows = new();
    private List<DispatchShopRow> _visible = new();
    private int _hiddenNoSheet;
    private int _kMachines, _kRunning, _kQueued, _kInterrupted;
    // Tên tài khoản / shop theo GUID — dựng từ TOÀN BỘ config (kể cả shop chưa gán ngăn dữ liệu, tức không có
    // trong _rows) vì Snap.Interrupted có thể trỏ tới shop như vậy. Id không tra được → Short() + title đầy đủ.
    private Dictionary<string, string> _acctNames = new(StringComparer.Ordinal);
    private Dictionary<string, string> _shopNames = new(StringComparer.Ordinal);

    // ── Bảng chi tiết của thẻ KPI đang mở ──
    // Dựng CÙNG một lượt quét với 3 số KPI (xem RecomputeKpis) → số trên thẻ LUÔN bằng số dòng bảng.
    private List<DispatchWorkItem> _running = new(), _queued = new();
    private List<MachinePresence> _onlineMachines = new();
    /// <summary>Phiên module Đơn hàng của MỌI máy (gương client tự đẩy) — nguồn phần KPI đến từ Đơn hàng. Tách hẳn
    /// khỏi danh bạ của <c>DispatchOrdersTab</c>: bảng kia chỉ nạp MÁY ĐANG CHỌN, mà KPI là số toàn hệ thống.</summary>
    private List<OrdersRunningAccount> _ordersRunning = new();
    private string _kpi = "";           // "" = đóng; còn lại là key KPI hợp lệ (cũng nằm trong URL ?kpi=)
    private string _kpiMsg = "";        // kết quả ngắn của hành động vừa bấm trong panel
    /// <summary>Key của DÒNG bảng chi tiết đang chờ bấm-lần-nữa (rỗng = không có). Khoá theo <c>Key</c> chứ KHÔNG
    /// theo <c>AsnId</c>: dòng Đơn hàng không có assignment nên mọi dòng Đơn hàng sẽ cùng khớp <c>AsnId</c> rỗng.</summary>
    private string _confirmWork = "";
    private bool _confirmDismiss;       // arm 2-bước nút "Bỏ cả N việc gián đoạn"
    private int _kpiEmptyTicks;         // số nhịp fleet liên tiếp panel đang mở mà KPI = 0 (2 nhịp → tự đóng)

    // ── Bộ lọc (đều nằm trong URL) ──
    private string _tab = "bs";
    private string _fState = "todo";
    private string _fAcct = "", _fQuery = "";

    // ── Máy đang chọn (trục của cả trang) + tham số của lượt giao ──
    private string _selMachine = "", _selHost = "";
    private string _machineMsg = "";      // "máy vừa offline — đã bỏ chọn" (nhịp fleet 2s tự phát hiện)
    private int _optStart, _optEnd, _optProcs, _optFrame, _optReload;   // 0 = dùng cấu hình client
    // Khoảng nghỉ giữa 2 link của Scrape (giây) — 0 = dùng cấu hình client (mặc định 120–240s).
    private int _optRestMin, _optRestMax;
    private bool _optFromClaimed;
    private string _result = "";
    private string _confirmAcct = "";     // "accountId|op" đang chờ bấm-lần-nữa (rỗng = không có)
    /// <summary>Nút HUỶ hàng loạt đang chờ bấm-lần-nữa: "mach" (theo máy) hoặc "acct|&lt;id&gt;" (theo tài khoản).
    /// Rỗng = không có. Tách khỏi <see cref="_confirmAcct"/> để hai cụm không tranh nhau một biến.</summary>
    private string _confirmCancel = "";

    private List<MachineBudget> _budgets = new();
    private Dictionary<string, string> _holds = new(StringComparer.Ordinal);
    /// <summary>Số việc hub-giao đang chạy/chờ gắn từng máy (khoá = machineId) — dựng 1 lần mỗi nhịp, cả
    /// hàng thẻ chọn máy lẫn bảng "Máy online" đọc lại.</summary>
    private Dictionary<string, int> _used = new(StringComparer.Ordinal);

    // ── View-state của tab Đơn hàng — bảng của tab đó nằm trong <DispatchOrdersTab>, HAI giá trị này ở lại đây
    //    vì chúng nằm trong URL (?omach=&oacc=) mà UpdateUrl/RestoreFromUrl là việc của trang. ──
    /// <summary>Máy đang chọn ở tab Đơn hàng (nằm trong URL <c>?omach=</c>). Tách hẳn khỏi
    /// <see cref="_selMachine"/> của tab BigSeller: hai tab chọn hai TẬP máy khác nhau (suất workspace vs suất đơn hàng).</summary>
    private string _oMach = "";
    private string _oOpen = "";           // login tài khoản đang bung danh sách shop ("" = thu gọn hết)

    protected override void OnInitialized()
    {
        base.OnInitialized();
        ReloadStatic();
        Rebuild();
        // F5/deep-link: khôi phục bộ lọc SAU KHI _rows dựng xong. KHÔNG navigate ở đây (còn prerender →
        // NavigateTo ném redirect); URL chỉ được ghi lại khi user thao tác sau đó.
        RestoreFromUrl();
    }

    protected override void OnFleetTick()
    {
        if ((DateTimeOffset.UtcNow - _staticLoadedAt).TotalSeconds > 10) ReloadStatic();
        Rebuild();
        SyncKpiPanel();   // CHỈ ở đây, KHÔNG trong Rebuild(): hàm này navigate được, mà Rebuild còn chạy lúc prerender
    }

    /// <summary>Đọc nguồn CHẬM (file config) — tối đa mỗi 10s, khỏi đọc mỗi nhịp 2s.</summary>
    private void ReloadStatic()
    {
        _accounts = Config.BigSellerAccounts();
        _staticLoadedAt = DateTimeOffset.UtcNow;
    }

    // Thứ tự BẮT BUỘC: quỹ/hold dựng TRƯỚC bộ lọc (mọi luật khoá nút đọc _holds).
    private void Rebuild()
    {
        BuildRows();
        BuildBudgets();
        ApplyFilter();
        // Phiên Đơn hàng nạp KHÔNG phụ thuộc _tab và TRƯỚC RecomputeKpis (thẻ KPI đếm cả chúng): KPI là số toàn
        // hệ thống, đứng ở tab BigSeller vẫn phải đúng. Bảng của tab Đơn hàng thì <DispatchOrdersTab> tự lo —
        // component đó chỉ tồn tại khi tab đang mở nên đứng ở tab kia không có truy vấn nào của nó chạy.
        ReloadOrdersRunning();
        RecomputeKpis();
    }

    private void BuildRows()
    {
        var set = DispatchRowsBuilder.BuildRows(_accounts);
        _rows = set.Rows;
        _hiddenNoSheet = set.HiddenNoSheet;
        _acctNames = set.AcctNames;
        _shopNames = set.ShopNames;
    }

    private void BuildBudgets()
    {
        _holds = DispatchRowsBuilder.Holds(Snap);
        _used = DispatchRowsBuilder.UsedByMachine(Snap);
        _budgets = DispatchRowsBuilder.BuildBudgets(Snap, _used);

        // Máy đang chọn TẮT/biến mất giữa chừng (nhịp fleet 2s) → tự bỏ chọn + nói rõ, khỏi giao vào một máy
        // không claim được việc (việc sẽ nằm chờ mãi).
        if (_selMachine.Length == 0) return;
        var sel = _budgets.FirstOrDefault(m => m.MachineId == _selMachine);
        if (sel is null) DropMachine($"Máy {_selHost} vừa rời fleet — đã bỏ chọn.");
        else if (!sel.Online) DropMachine($"Máy {sel.Hostname} vừa offline — đã bỏ chọn.");
        else _selHost = sel.Hostname;
    }

    private void ApplyFilter()
    {
        _visible = DispatchRowsBuilder.Filter(Snap, _rows, _fAcct, _fQuery, _fState);

        // Cụm nút cả-acc đang chờ bấm-lần-nữa mà acc rơi khỏi bộ lọc → huỷ chờ (kẻo bấm lần 2 trúng acc khác).
        if (_confirmAcct.Length > 0)
        {
            var bar = _confirmAcct.LastIndexOf('|');
            if (bar <= 0 || !_visible.Any(r => r.AccountId == _confirmAcct[..bar])) _confirmAcct = "";
        }
        // Cụm HUỶ-theo-acc đang chờ mà acc rơi khỏi bộ lọc → huỷ chờ (cú bấm thứ 2 sẽ trúng acc khác).
        if (_confirmCancel.StartsWith("acct|", StringComparison.Ordinal)
            && !_visible.Any(r => r.AccountId == _confirmCancel[5..])) _confirmCancel = "";
    }

    private void DropMachine(string why)
    {
        _selMachine = "";
        _confirmCancel = "";
        _confirmAcct = "";
        _machineMsg = why;
    }

    /// <summary>Gương phiên Đơn hàng toàn hệ thống — đọc mỗi nhịp (bảng nhỏ, một query) để thẻ KPI và bảng chi
    /// tiết luôn dựng từ CÙNG một lượt quét. Khoá DB nhất thời → giữ bản cũ, y các đường đọc DB khác của trang.</summary>
    private void ReloadOrdersRunning()
    {
        try { _ordersRunning = Db.OrdersRunningAccounts(); }
        catch { /* khoá DB nhất thời → giữ bản cũ */ }
    }

    /// <summary>Số 4 thẻ KPI + DÒNG của 4 bảng chi tiết, dựng trong CÙNG một lượt: mỗi số là <c>Count</c> của
    /// đúng danh sách mà bảng đó vẽ ra (xem <c>DispatchRowsBuilder.BuildWorkLists</c>), nên thẻ không bao giờ nói
    /// một đằng bảng một nẻo. Thẻ "gián đoạn" giữ nguyên: đó là vòng đời của assignment BigSeller, phiên Đơn hàng
    /// không có. Phần còn lại của hàm là DỌN các trạng thái chờ-bấm-lần-nữa đã hết nghĩa.</summary>
    private void RecomputeKpis()
    {
        _onlineMachines = DispatchRowsBuilder.OnlineMachines(Snap);
        _kMachines = _onlineMachines.Count;

        var (running, queued) = DispatchRowsBuilder.BuildWorkLists(Snap, _rows, _ordersRunning);
        _running = running;
        _queued = queued;
        _kRunning = running.Count;
        _kQueued = queued.Count;
        _kInterrupted = Snap.Interrupted.Count;

        // Dòng đang chờ bấm-lần-nữa mà đã rơi khỏi bảng (nhịp fleet 2s) → huỷ chờ, kẻo cú bấm thứ 2 trúng việc
        // khác — đúng lỗi đã phải vá cho cụm nút "cả acc". So theo Key, xem chú thích của _confirmWork.
        if (_confirmWork.Length > 0
            && !running.Any(w => w.Key == _confirmWork) && !queued.Any(w => w.Key == _confirmWork))
            _confirmWork = "";
        if (_confirmDismiss && _kInterrupted == 0) _confirmDismiss = false;
        // Nút "Huỷ MỌI việc" đang chờ bấm-lần-nữa mà hệ thống vừa hết việc → huỷ chờ (nút cũng thành disabled).
        if (_confirmCancel == "all" && AllOpenCount == 0) _confirmCancel = "";
    }

    // ── Bảng chi tiết KPI: nhãn + hành động ───────────────────────────────────────
    private int KpiCount(string key) => DispatchViewLogic.KpiCount(key, _kMachines, _kRunning, _kQueued, _kInterrupted);

    private string KpiPanelTitle() => DispatchViewLogic.KpiPanelTitle(_kpi, _kMachines, _kRunning, _kQueued, _kInterrupted);

    // Bấm lại đúng thẻ đang mở = đóng.
    private void ToggleKpi(string key)
    {
        _kpi = _kpi == key ? "" : key;
        _kpiMsg = "";
        _confirmWork = "";
        _confirmDismiss = false;
        _kpiEmptyTicks = 0;
        UpdateUrl();
    }

    /// <summary>Panel đang mở mà KPI về 0 (vừa huỷ hết): nhịp này còn hiện dòng "không còn việc nào" cho người
    /// dùng biết chuyện gì vừa xảy ra, nhịp sau tự đóng — thẻ 0 không bấm được nên không tự đóng thì panel kẹt.</summary>
    private void SyncKpiPanel()
    {
        var advance = DispatchViewLogic.AdvanceEmptyKpiPanel(_kpi, KpiCount(_kpi), _kpiEmptyTicks);
        if (!advance.Close) { _kpiEmptyTicks = advance.EmptyTicks; return; }
        _kpi = "";
        _kpiMsg = "";
        _kpiEmptyTicks = advance.EmptyTicks;
        UpdateUrl();
    }

    /// <summary>"→ Chọn máy này" ở bảng Máy online: ĐẶT máy đang chọn (khác <see cref="SelectMachine"/> — hàm đó
    /// bấm-lại-là-bỏ-chọn) rồi đóng panel để mắt về thẳng lưới shop bên dưới.</summary>
    private void PickMachine(string machineId)
    {
        _selMachine = machineId;
        _selHost = HostName(machineId);
        _confirmAcct = ""; _confirmCancel = "";
        _result = "";
        _machineMsg = "";
        _kpi = "";
        _kpiMsg = "";
        _kpiEmptyTicks = 0;
        UpdateUrl();
    }

    // Huỷ là thao tác một chiều → xác nhận 2 bước (bấm lần nữa), giống cụm nút "cả acc".
    private void OnWorkCancel(DispatchWorkItem w)
    {
        if (w.Manual || w.AsnId.Length == 0) return;   // việc chạy tay: hub không có gì để huỷ
        if (_confirmWork != w.Key) { _confirmWork = w.Key; _kpiMsg = ""; return; }
        _confirmWork = "";
        Db.CancelAssignment(w.AsnId);
        _kpiMsg = $"✖ Đã huỷ {OpLabel(w.Op)} · {w.ShopName} (tham số giữ nguyên — ▶ Tiếp tục để chạy lại).";
        FleetState.Refresh();
    }

    /// <summary>✖ Dừng một phiên Đơn hàng ngay từ bảng chi tiết KPI. Đi ĐÚNG đường lệnh của tab Đơn hàng
    /// (<see cref="HubDatabase.CreateOrdersCommand"/> — idempotent), không đẻ đường tạo lệnh thứ hai. Xác nhận 2
    /// bước như nút Huỷ vì hub không dừng nửa chừng rồi hoàn lại được: client đóng trình duyệt là mất phiên.</summary>
    private void OnOrdersStop(DispatchWorkItem w)
    {
        if (w.MachineId.Length == 0 || w.Login.Length == 0) return;   // phòng thân: dòng không phải phiên Đơn hàng
        if (_confirmWork != w.Key) { _confirmWork = w.Key; _kpiMsg = ""; return; }
        _confirmWork = "";
        Db.CreateOrdersCommand(w.MachineId, w.Login, OrdersCommandActions.Stop);
        _kpiMsg = $"⏳ Đã gửi lệnh ✖ Dừng · {w.Login} → {w.Host} (chờ máy nhận ở nhịp 12s rồi xác nhận).";
        FleetState.Refresh();
    }

    // Tiếp tục KHÔNG cần xác nhận: chỉ đưa việc về hàng chờ, không mất gì.
    private void OnResume(Assignment a)
    {
        var shop = DispatchRowsBuilder.NameLabel(_shopNames, a.ShopId).Text;
        _kpiMsg = Db.ResumeAssignment(a.Id) is { } err
            ? $"✘ {OpLabel(a.Op)} · {shop}: {err}"
            : $"▶ Đã cho {OpLabel(a.Op)} · {shop} chạy lại (về hàng chờ).";
        FleetState.Refresh();
    }

    // Hub chỉ có DismissAllInterrupted() (không có API bỏ TỪNG việc) → nút này bỏ HẾT, y nút cùng tên trên Fleet.
    private void OnDismissAll()
    {
        if (!_confirmDismiss) { _confirmDismiss = true; _kpiMsg = ""; return; }
        _confirmDismiss = false;
        _kpiMsg = $"✕ Đã bỏ {Db.DismissAllInterrupted()} việc gián đoạn khỏi danh sách.";
        FleetState.Refresh();
    }

    // ── Nhóm hiển thị + nhãn ──────────────────────────────────────────────────────
    private List<DispatchRowGroup> VisibleGroups() => _visible
        .GroupBy(r => r.AccountId, StringComparer.Ordinal)
        .Select(g => new DispatchRowGroup(g.Key, g.First().AccountName, g.ToList()))
        .ToList();

    /// <summary>Ngữ cảnh nút của lưới — dựng lúc render (và lúc xử lý cú bấm) để trang với component lưới tính
    /// nút bằng ĐÚNG cùng dữ liệu.</summary>
    private DispatchGridContext GridContext() => new(Snap, _holds, _selMachine, _confirmAcct, _confirmCancel);

    private string HostName(string machineId) => FleetViewProjection.HostName(Snap, machineId);

    private string EmptyText() => _rows.Count == 0
        ? (_hiddenNoSheet > 0
            ? $"Chưa shop nào gán ngăn dữ liệu ({_hiddenNoSheet} shop bị ẩn) — vào BigSeller › ⚙ Cấu hình để gán."
            : "Chưa có tài khoản BigSeller nào có shop.")
        : "Không dòng nào khớp bộ lọc.";

    private static string OpLabel(string op) => FleetViewProjection.OperationLabel(op);

    // ── Chọn máy ──────────────────────────────────────────────────────────────────
    private string MachineTitle(MachineBudget m)
    {
        if (!m.Online) return $"Máy {m.Hostname} đang offline ({FleetStateService.Ago(m.LastSeen)}) — không giao việc được.";
        if (m.MachineId == _selMachine) return $"Đang giao việc cho {m.Hostname} — bấm lần nữa để bỏ chọn.";
        return m.Free <= 0
            ? $"Chọn {m.Hostname} (đã hết quỹ Brave — việc giao thêm sẽ phải xếp hàng)."
            : $"Chọn {m.Hostname} để giao việc.";
    }

    private void SelectMachine(string machineId)
    {
        // Bấm lại đúng thẻ đang chọn = bỏ chọn (cách duy nhất để về trạng thái "chưa chọn máy").
        _selMachine = _selMachine == machineId ? "" : machineId;
        _selHost = HostName(_selMachine);
        _confirmAcct = "";
        _result = "";
        _machineMsg = "";
        UpdateUrl();
    }

    // ── Điều phối tự động (công tắc toàn hệ thống, lưu trong bảng settings) ──────
    private void ToggleDispatch(ChangeEventArgs e)
    {
        Dispatcher.Enabled = e.Value is true;
        FleetState.Refresh();
    }

    // ── Huỷ HÀNG LOẠT: toàn hệ thống / theo máy / theo tài khoản ─────────────────
    /// <summary>Việc hub-giao đang mở (queued/running) của TOÀN HỆ THỐNG — số trên nút "✖ Huỷ MỌI việc". Khác
    /// <see cref="MachineOpenCount"/> (chỉ máy đang chọn).</summary>
    private int AllOpenCount => Snap.Assignments.Count(a => a.Status is AssignmentStatus.Queued or AssignmentStatus.Running);

    /// <summary>Việc hub-giao đang mở (queued/running) của máy đang chọn — đếm cả việc máy đã claim lẫn việc mới
    /// ghim chưa claim. Việc chạy TAY không có assignment nên KHÔNG nằm ở đây (hub không huỷ được).</summary>
    private int MachineOpenCount => _selMachine.Length == 0 ? 0 : OpenAssignmentsOfMachine().Count;

    private List<Assignment> OpenAssignmentsOfMachine() => Snap.Assignments
        .Where(a => a.Status is AssignmentStatus.Queued or AssignmentStatus.Running
                    && (a.ClaimedByMachineId == _selMachine || a.TargetMachineId == _selMachine))
        .ToList();

    /// <summary>Huỷ 2 bước: bấm lần đầu chỉ ARM (đổi nhãn), lần hai mới huỷ thật. Chỉ MỘT nút ở trạng thái chờ
    /// tại một thời điểm; đổi máy/lọc/tab đều huỷ trạng thái chờ (xem nơi gán <c>_confirmCancel = ""</c>).</summary>
    private bool Arm(string key)
    {
        if (_confirmCancel == key) { _confirmCancel = ""; return true; }
        _confirmCancel = key;
        _confirmAcct = "";   // không để hai cụm cùng ở trạng thái chờ
        _result = "";
        return false;
    }

    /// <summary>Huỷ MỌI việc hub-giao đang chạy/chờ trên MỌI máy, MỌI tài khoản (chuyển từ trang Fleet sang).
    /// Bản 'canceled' GIỮ NGUYÊN tham số → điều phối tự động không tạo lại (sticky-cancel); bấm ▶ Tiếp tục ở thẻ
    /// KPI "Việc gián đoạn" để chạy lại đúng lượt cũ.</summary>
    private void CancelAllWork()
    {
        var open = Snap.Assignments.Where(a => a.Status is AssignmentStatus.Queued or AssignmentStatus.Running).ToList();
        if (open.Count == 0) { _confirmCancel = ""; return; }
        if (!Arm("all")) return;

        foreach (var a in open) Db.CancelAssignment(a.Id);
        _result = $"✖ Đã huỷ {open.Count} việc (toàn hệ thống) — tham số giữ nguyên, ▶ Tiếp tục để chạy lại.";
        FleetState.Refresh();   // 1 lần sau vòng lặp
    }

    private void CancelByMachine()
    {
        if (_selMachine.Length == 0) return;
        var open = OpenAssignmentsOfMachine();
        if (open.Count == 0) { _confirmCancel = ""; return; }
        if (!Arm("mach")) return;

        foreach (var a in open) Db.CancelAssignment(a.Id);
        _result = $"✖ Đã huỷ {open.Count} việc trên máy {HostName(_selMachine)}";
        FleetState.Refresh();   // 1 lần sau vòng lặp
    }

    private void CancelByAcct(DispatchRowGroup g)
    {
        var open = Snap.Assignments
            .Where(a => a.BigsellerId == g.AccountId && a.Status is AssignmentStatus.Queued or AssignmentStatus.Running).ToList();
        if (open.Count == 0) { _confirmCancel = ""; return; }
        if (!Arm("acct|" + g.AccountId)) return;

        foreach (var a in open) Db.CancelAssignment(a.Id);
        _result = $"✖ Đã huỷ {open.Count} việc của tài khoản {g.AccountName}";
        FleetState.Refresh();
    }

    // Một click = MỘT lệnh, KHÔNG xác nhận 2 bước (nhẹ tay, và huỷ lại được ngay bằng chính nút đó).
    private void OnOpClick((DispatchShopRow Row, string Op) e)
    {
        var (r, op) = e;
        var b = DispatchButtons.Btn(GridContext(), r, op);
        if (b.Act == "cancel")
        {
            if (DispatchRowsBuilder.OpenAsn(Snap, r, op) is not { } asn) return;
            Db.CancelAssignment(asn.Id);
            _result = $"✖ Đã huỷ {OpLabel(op)} · {r.ShopName}";
        }
        else if (b.Act == "run")
        {
            CreateJob(r, op);
            _result = $"✔ Đã giao {OpLabel(op)} · {r.ShopName} → {HostName(_selMachine)}";
        }
        else return;   // nút disable (phòng thân: browser đã chặn bằng thuộc tính disabled)

        _confirmAcct = ""; _confirmCancel = "";
        FleetState.Refresh();
    }

    /// <summary>Tạo 1 assignment ghim vào máy đang chọn — khuôn giống <c>Fleet.Pin()</c>. FrameSize + khoảng nghỉ
    /// chỉ scrape đọc, Reload chỉ import/update đọc → op khác truyền 0 để sạch dữ liệu. Processes áp mọi op.</summary>
    private void CreateJob(DispatchShopRow r, string op)
    {
        var payload = op == AssignmentOps.Import
            ? JsonSerializer.Serialize(new ImportJobPayload { FromClaimedTab = _optFromClaimed })
            : "";
        var scrape = op == AssignmentOps.Scrape;
        Db.CreateAssignment(new CreateAssignmentRequest(
            r.AccountId, r.ShopId, r.Sheet, op, _selMachine, Pinned: true,
            Math.Max(0, _optStart), Math.Max(0, _optEnd), payload,
            Processes: Math.Max(0, _optProcs),
            FrameSize: scrape ? Math.Max(0, _optFrame) : 0,
            ReloadSeconds: op is AssignmentOps.Import or AssignmentOps.Update ? Math.Max(0, _optReload) : 0,
            RestMinSec: scrape ? Math.Max(0, _optRestMin) : 0,
            RestMaxSec: scrape ? Math.Max(0, _optRestMax) : 0));
    }

    // ── Nút mức tài khoản (chạy cả acc) ───────────────────────────────────────────
    private void OnAcctClick((DispatchRowGroup Group, string Op) e)
    {
        var (g, op) = e;
        var ctx = GridContext();
        if (DispatchButtons.AcctBtn(ctx, g, op).Disabled) return;
        var key = g.AccountId + "|" + op;
        if (_confirmAcct != key) { _confirmAcct = key; _result = ""; return; }   // bấm lần 1 = arm

        _confirmAcct = "";
        var runnable = g.Rows.Where(r => DispatchButtons.Btn(ctx, r, op).Act == "run").ToList();
        var skipped = g.Rows.Count - runnable.Count;
        foreach (var r in runnable) CreateJob(r, op);
        _result = $"✔ Đã giao {runnable.Count} việc {OpLabel(op)} của {g.AccountName} → {HostName(_selMachine)}"
                  + (skipped > 0 ? $" (bỏ qua {skipped} shop đang chạy / bị khoá)" : "");
        FleetState.Refresh();   // 1 lần sau vòng lặp (KHÔNG gọi trong vòng lặp)
    }

    // ── Bộ lọc / tab ──────────────────────────────────────────────────────────────
    private void OnFilterChanged() { _confirmAcct = ""; _confirmCancel = ""; Rebuild(); UpdateUrl(); }
    private void SetState(string key) { _fState = key; _confirmAcct = ""; _confirmCancel = ""; Rebuild(); UpdateUrl(); }

    private void SetTab(string tab)
    {
        _tab = tab;
        _confirmAcct = ""; _confirmCancel = "";
        // Sang tab Đơn hàng KHÔNG cần nạp gì ở đây: <DispatchOrdersTab> vừa hiện ra là OnParametersSet của nó
        // chạy ngay trong chính lượt render này → có dữ liệu ở khung hình đầu, không chớp một nhịp rỗng.
        UpdateUrl();
    }

    /// <summary>Con báo máy / tài khoản đang bung vừa đổi (người dùng bấm). Trang giữ hai giá trị này vì chúng
    /// nằm trong URL — ghi lại rồi đưa vào query, y hệt các nút khác của trang.</summary>
    private void OnOrdersMachineChanged(string machineId) { _oMach = machineId; UpdateUrl(); }

    private void OnOrdersAccountChanged(string login) { _oOpen = login; UpdateUrl(); }

    // ── View-state ↔ URL query (?tab=&f=&acct=&mach=&omach=&oacc=&q=&kpi=) — F5 / chia sẻ link giữ nguyên ──
    private bool _restored;      // cờ once: chỉ khôi phục lúc init, KHÔNG re-restore mỗi tick
    private bool _suppressUrl;   // chặn ghi URL trong lúc restore

    private void UpdateUrl()
    {
        if (_suppressUrl) return;
        UrlState.Update(Nav, replace: true,
            ("tab", _tab == "bs" ? "" : _tab),
            ("f", _fState == "todo" ? "" : _fState),
            ("acct", _fAcct),
            ("mach", _selMachine),
            ("omach", _oMach),
            ("oacc", _oOpen),
            ("q", _fQuery.Trim()),
            ("kpi", _kpi));
    }

    private void RestoreFromUrl()
    {
        if (_restored) return;
        _restored = true;
        var q = UrlState.Read(Nav);
        _suppressUrl = true;
        try
        {
            _tab = q["tab"] == "orders" ? "orders" : "bs";
            var f = q["f"];
            if (StateChips.Any(c => c.Key == f)) _fState = f;
            var acct = q["acct"];
            if (acct.Length > 0 && _accounts.Any(a => a.Id == acct)) _fAcct = acct;
            // Máy trong URL đã biến mất / đang offline → BỎ QUA (không chọn, không báo "vừa offline").
            var mach = q["mach"];
            if (mach.Length > 0 && Snap.Machines.Any(m => m.MachineId == mach)
                && !FleetStateService.MachineOffline(Snap, mach))
            {
                _selMachine = mach;
                _selHost = HostName(mach);
            }
            // Máy của tab Đơn hàng: cùng luật với "mach" — biến mất / offline thì BỎ QUA (không chọn, không báo).
            var omach = q["omach"];
            if (omach.Length > 0 && Snap.Machines.Any(m => m.MachineId == omach && m.Kind == MachineSlots.Orders)
                && !FleetStateService.MachineOffline(Snap, omach))
            {
                _oMach = omach;
                // Tài khoản đang bung shop chỉ có nghĩa khi đã chọn được máy; <DispatchOrdersTab> tự thu gọn
                // nếu login trong URL không còn trong danh bạ của máy đó.
                _oOpen = q["oacc"];
            }
            _fQuery = q["q"];
            var kpi = q["kpi"];
            if (DispatchViewLogic.IsKpiKey(kpi)) _kpi = kpi;
            Rebuild();
        }
        finally { _suppressUrl = false; }
    }
}
