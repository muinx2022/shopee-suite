using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Shopee.Core.Coordination;
using Shopee.Suite.Modules.Scrape;
using Shopee.Suite.Modules.UpdateProduct;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Worker phía CLIENT: định kỳ XIN việc Hub giao cho máy này (đúng vai trò) rồi TỰ chạy bằng đúng các
/// entry-point fire-and-forget của Workspace (<see cref="ScrapeViewModel.RunSingleAsync"/> /
/// <see cref="UpdateProductViewModel.RunImportSingleAsync"/> …, gọi với silent:true để KHÔNG bao giờ mở
/// modal) — KHÔNG viết lại engine. Khoá lease/account-lease bên trong runner vẫn là lưới an toàn cuối.
/// Chạy trên MỌI máy có vai trò ≠ Tắt (kể cả máy Hub vì Hub cũng có thể là worker).
/// </summary>
public sealed class AssignmentWorker : IDisposable
{
    private readonly ScrapeViewModel _scrape;
    private readonly UpdateProductViewModel _update;
    private readonly DispatcherTimer _timer;             // claim/launch/reconcile (UI thread)
    private readonly System.Threading.Timer _heartbeat;  // nhịp 'running' (luồng NỀN, không bị UI làm nghẽn)
    private readonly Dictionary<string, InFlight> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _liveIds = new(StringComparer.Ordinal);
    private bool _ticking;

    private const int MaxConcurrent = 4;
    private const int GraceTicks = 6;   // ~60s: chưa thấy chạy trong ngần này coi như không khởi động được

    /// <summary>Người ngồi máy bấm "Tạm dừng nhận việc" → ngừng xin việc mới (việc đang chạy vẫn xong).</summary>
    public bool Paused { get; set; }

    /// <summary>Tự kéo workbook/cookie trước import/update và đẩy sau scrape (bàn giao xuyên máy). Mặc định
    /// TẮT để không đụng cấu hình máy đang chạy 1 máy; bật từ bảng điều phối khi tách vai trò qua nhiều máy.</summary>
    public bool AutoSyncHandoff { get; set; }

    private sealed class InFlight
    {
        public required Assignment A;
        public bool SeenRunning;
        public int IdleTicks;
    }

    public AssignmentWorker(ScrapeViewModel scrape, UpdateProductViewModel update)
    {
        _scrape = scrape;
        _update = update;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        // Nhịp giữ-sống chạy trên luồng nền → KHÔNG bị log-flood của scrape (10-20 cửa sổ) làm nghẽn, nên
        // SweepStaleLocked (5') không đánh nhầm 'failed' việc đang chạy thật.
        _heartbeat = new System.Threading.Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Dispose() { _timer.Stop(); _heartbeat.Dispose(); }

    private void Heartbeat()
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null) return;
        foreach (var id in _liveIds.Keys) _ = hub.ReportAssignmentAsync(id, "running");
    }

    private async void Tick()
    {
        if (_ticking) return;
        _ticking = true;
        try { await TickAsync(); } catch { } finally { _ticking = false; }
    }

    private async Task TickAsync()
    {
        var hub = CoordinationRuntime.Hub;
        if (hub is null) return;

        await ReconcileInflightAsync(hub);

        if (Paused) return;

        var myId = hub.MachineId;
        var role = hub.CurrentFleet.Roles.FirstOrDefault(r => r.MachineId == myId)?.Role ?? MachineRoles.Off;
        // KHÔNG return sớm khi role==Off: việc GHIM TAY (Giao tay) định tuyến theo MÁY, không cần vai trò.
        // Server chỉ trả việc ghim-cho-máy-này (+ việc đúng vai trò nếu có) → role=Off vẫn nhận được việc ghim.

        var free = MaxConcurrent - _inflight.Count;
        if (free <= 0) return;

        foreach (var a in await hub.ClaimAssignmentsAsync(role, free))
        {
            if (_inflight.ContainsKey(a.Id)) continue;
            _inflight[a.Id] = new InFlight { A = a };
            await LaunchAsync(hub, a);
        }
    }

    private async Task LaunchAsync(HttpCoordinationHub hub, Assignment a)
    {
        // Tiền-kiểm KHÔNG mở dialog: thiếu điều kiện (kho tk/Brave/workbook/cookie/sheet) → báo failed NGAY
        // (khỏi kẹt việc 'running' chặn single-session tài khoản, và KHÔNG chạm tới đường Warn/modal).
        if (!CanLaunch(a, out var problem))
        {
            _inflight.Remove(a.Id);
            await hub.ReportAssignmentAsync(a.Id, "failed", problem);
            return;
        }

        // Bàn giao xuyên máy: kéo workbook/cookie mới nhất trước khi import/update.
        if (AutoSyncHandoff && a.Op is "import" or "update" or "rewrite")
        {
            try { if (CoordinationRuntime.ConfigSync is { } sync) await sync.PullAccountsAsync(); } catch { }
        }

        // BeginInvoke (không Invoke) → LaunchCore không chạy trong nested modal pump nếu lỡ có dialog đang mở.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (LaunchCore(a)) _liveIds[a.Id] = 1;
            else { _inflight.Remove(a.Id); _ = hub.ReportAssignmentAsync(a.Id, "failed", "không khởi động được trên máy này"); }
        });
    }

    /// <summary>Tiền-kiểm điều kiện chạy (KHÔNG side-effect mở dialog). false → báo failed ngay.</summary>
    private bool CanLaunch(Assignment a, out string problem)
    {
        problem = "";
        if (a.Op == "scrape")
        {
            var t = _scrape.ScrapeTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
            var shop = t?.Account.Shops.FirstOrDefault(s => s.Id == a.ShopId);
            if (t is null || shop is null) { problem = "không thấy tài khoản/shop trên máy này"; return false; }
            t.SelectedShop = shop;
            return _scrape.CanDispatchScrape(t, out problem);
        }
        var ut = _update.RunTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
        var ushop = ut?.Account.Shops.FirstOrDefault(s => s.Id == a.ShopId);
        if (ut is null || ushop is null) { problem = "không thấy tài khoản/shop trên máy này"; return false; }
        ut.SelectedShop = ushop;
        return _update.CanDispatchUpdate(ut, a.Op, out problem);
    }

    private bool LaunchCore(Assignment a)
    {
        switch (a.Op)
        {
            case "scrape":
            {
                var t = _scrape.ScrapeTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
                var shop = t?.Account.Shops.FirstOrDefault(s => s.Id == a.ShopId);
                if (t is null || shop is null) return false;
                t.SelectedShop = shop;
                _ = _scrape.RunSingleAsync(t, resume: true, silent: true, a.StartRow, a.EndRow);   // Hub đặt khoảng dòng (0 = dùng cấu hình client)
                return true;
            }
            case "import":
            case "update":
            case "rewrite":
            {
                var t = _update.RunTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
                var shop = t?.Account.Shops.FirstOrDefault(s => s.Id == a.ShopId);
                if (t is null || shop is null) return false;
                t.SelectedShop = shop;
                if (a.Op == "import") _ = _update.RunImportSingleAsync(t, silent: true, a.StartRow, a.EndRow);
                else if (a.Op == "update") _ = _update.RunUpdateSingleAsync(t, silent: true, a.StartRow, a.EndRow);
                else _ = _update.RunNameRewriteSingleAsync(t, silent: true, a.StartRow, a.EndRow);
                return true;
            }
            default: return false;
        }
    }

    private async Task ReconcileInflightAsync(HttpCoordinationHub hub)
    {
        if (_inflight.Count == 0) return;

        // 1) Việc bị HUỶ ở Hub → dừng job local + bỏ khỏi sổ (làm Cancel thực sự có hiệu lực).
        foreach (var f in _inflight.Values.ToList())
        {
            var st = hub.CurrentFleet.Assignments.FirstOrDefault(x => x.Id == f.A.Id)?.Status;
            if (st == "canceled")
            {
                StopLocal(f.A);
                _liveIds.TryRemove(f.A.Id, out _);
                _inflight.Remove(f.A.Id);
            }
        }

        // 2) Phân loại còn chạy / cần kết luận; cập nhật _liveIds cho nhịp giữ-sống nền.
        var toConclude = new List<InFlight>();
        foreach (var f in _inflight.Values)
        {
            if (IsRunningLocally(f.A)) { f.SeenRunning = true; f.IdleTicks = 0; _liveIds[f.A.Id] = 1; continue; }
            _liveIds.TryRemove(f.A.Id, out _);
            f.IdleTicks++;
            // SeenRunning: chờ ~2 nhịp để chắc đã dừng hẳn; chưa từng thấy chạy: chờ hết grace.
            if (f.IdleTicks >= (f.SeenRunning ? 2 : GraceTicks)) toConclude.Add(f);
        }

        // 3) Kết luận dựa trên ledger TƯƠI (round-trip thật) — KHÔNG dùng snapshot poll 12s (tránh báo nhầm).
        foreach (var f in toConclude)
        {
            var status = await hub.FetchLedgerStatusAsync(f.A.CoordId);
            var ok = status == "completed";
            await hub.ReportAssignmentAsync(f.A.Id, ok ? "done" : "failed",
                ok ? null : (f.SeenRunning ? (status ?? "dừng dở") : "không khởi động được (có thể bị chặn khoá)"));

            if (AutoSyncHandoff && f.A.Op == "scrape" && ok)
            { try { _ = CoordinationRuntime.ConfigSync?.PushAsync(); } catch { } }

            _liveIds.TryRemove(f.A.Id, out _);
            _inflight.Remove(f.A.Id);
        }
    }

    private void StopLocal(Assignment a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        d.BeginInvoke(() =>
        {
            if (a.Op == "scrape")
            {
                var t = _scrape.ScrapeTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
                if (t is not null) _ = _scrape.StopSingleAsync(t);
            }
            else _update.StopSingle(a.BigsellerId);
        });
    }

    private bool IsRunningLocally(Assignment a)
    {
        if (a.Op == "scrape")
        {
            var t = _scrape.ScrapeTargets.FirstOrDefault(x => x.Account.Id == a.BigsellerId);
            var shop = t?.Account.Shops.FirstOrDefault(s => s.Id == a.ShopId);
            return t is not null && shop is not null && (t.IsShopRunning?.Invoke(shop) ?? false);
        }
        return _update.IsUpdateRunning(a.BigsellerId);
    }
}
