using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>Trạng thái 1 ô op (scrape/import/update) của 1 shop. Port của FleetViewModel.OpCell — thay Brush
/// WPF bằng lớp CSS. kind: 0 nghỉ/xong · 1 đang chạy · 2 đã xếp · 3 dừng/lỗi.</summary>
public sealed record OpCellState(string Text, string Css, int Kind)
{
    /// <summary>Ô đang chạy (kind==1) → khoá selectbox đặt-tay (khỏi đè ledger giữa chừng).</summary>
    public bool Locked => Kind == 1;
}

/// <summary>Chấm hiện diện của 1 máy (🟢/🟡/⚪) + nhãn "bao lâu trước".</summary>
public sealed record PresenceState(string Dot, string Label, bool Online);

/// <summary>
/// Ảnh chụp fleet dùng chung cho các trang Blazor: 1 luồng nền gọi <see cref="HubDatabase.Fleet"/> mỗi 2s và
/// bắn sự kiện <see cref="Changed"/> để trang tự vẽ lại (giống nhịp poll 2s của tab Fleet WPF). Kèm các hàm
/// tính ô trạng thái / hiện diện port nguyên từ FleetViewModel (OpCell, Presence, MachineOffline).
/// </summary>
public sealed class FleetStateService : IHostedService, IDisposable
{
    private readonly HubDatabase _db;
    private Timer? _timer;

    public FleetSnapshot Snapshot { get; private set; } = new();
    public event Action? Changed;

    public FleetStateService(HubDatabase db) => _db = db;

    public Task StartAsync(CancellationToken ct)
    {
        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { _timer?.Dispose(); return Task.CompletedTask; }
    public void Dispose() => _timer?.Dispose();

    /// <summary>Đọc lại snapshot ngay (dùng sau khi thao tác để UI phản hồi tức thì, khỏi chờ nhịp 2s).</summary>
    public void Refresh()
    {
        try { Snapshot = _db.Fleet(); Changed?.Invoke(); }
        catch { /* offline/khóa DB nhất thời → giữ snapshot cũ */ }
    }

    /// <summary>Ngưỡng "CÒN CHẠY" để HIỂN THỊ ⏳: nhịp lease/máy trong 120s (= 4 nhịp heartbeat 30s bị lỡ; &lt; 180s
    /// offline). Client đóng/crash → nhịp đóng băng → sau ~2' ô việc THÔI báo running (khớp dòng máy đã ⚪), thay vì
    /// kẹt ⏳ tới 5' như trước. KHÁC StaleLease(5') — cái đó dùng KHOÁ acc chống 2 máy + sweep huỷ; ở đây CHỈ sửa vị
    /// ngữ HIỂN THỊ, KHÔNG đụng khoá/sweep (việc hub-giao vẫn 'running' nội bộ, vẫn huỷ + hồi sinh được).</summary>
    private static readonly TimeSpan LeaseFresh = TimeSpan.FromSeconds(120);

    // ── Hàm tính trạng thái (port FleetViewModel) ────────────────────────────────
    public static OpCellState OpCell(FleetSnapshot f, string bsId, string shopId, string op)
    {
        var key = $"{bsId}__{shopId}__{op}";
        var lease = f.Leases.FirstOrDefault(l => l.Key == key);
        // Chỉ ⏳ khi lease CÒN TƯƠI (client còn nhịp). Client đã đóng/crash → nhịp đóng băng → rơi xuống ledger/idle.
        if (lease is not null && (DateTimeOffset.Now - lease.HeartbeatAt) < LeaseFresh)
            return new($"⏳ {Host(lease.Hostname)}", "run", 1);

        var asn = f.Assignments.FirstOrDefault(a => a.BigsellerId == bsId && a.ShopId == shopId && a.Op == op && a.Status is "queued" or "running");
        // Assignment 'running' nhưng máy đã OFFLINE (≥180s) → thôi báo ⏳ (nội bộ vẫn 'running' để huỷ/hồi sinh).
        if (asn is { Status: "running" } && !MachineOffline(f, asn.ClaimedByMachineId))
            return new($"⏳ {Host(asn.ClaimedByHostname)}", "run", 1);

        var led = f.Ledger.FirstOrDefault(l => l.Key == key);
        if (led?.Status == "completed") return new("✓ xong", "done", 0);
        if (led?.Status == "stopped") return new("■ dừng dở", "warn", 3);
        if (asn is { Status: "queued" })
        {
            var retry = string.IsNullOrWhiteSpace(asn.LastError) ? "" : $" · thử lại: {asn.LastError}";
            if (!string.IsNullOrEmpty(asn.TargetMachineId))
            {
                var tm = f.Machines.FirstOrDefault(m => m.MachineId == asn.TargetMachineId);
                if (MachineOffline(f, asn.TargetMachineId))
                    return new($"⚠ chờ {Host(tm?.Hostname ?? "")} (máy tắt){retry}", "warn", 2);
                return new($"• đã xếp → {Host(tm?.Hostname ?? "")}{retry}", "queued", 2);
            }
            return new($"• đã xếp{retry}", "queued", 2);
        }

        // Ledger 'running' mà TỚI ĐÂY nghĩa là: KHÔNG còn lease tươi (đã qua nhánh đầu), KHÔNG có assignment
        // running đại diện (nhánh assignment-running ở trên đã return) và KHÔNG có bản queued (vừa xét ở trên).
        // → tiến trình chạy TAY (không tạo assignment) đã CHẾT giữa chừng (crash/tắt máy). Trước đây rơi thẳng
        // xuống "· chờ" nên việc chết dở thành VÔ HÌNH; giờ báo "■ dừng dở (mất kết nối)" cùng kind/màu nhánh
        // 'stopped' để operator thấy & ▶ tiếp tục lại trên máy cũ. (asn đang 'running'-nhưng-offline vẫn thuộc
        // cơ chế assignment nội bộ → loại khỏi đây, giữ nguyên hành vi cũ.)
        if (led?.Status == "running" && asn is not { Status: "running" })
            return new("■ dừng dở (mất kết nối)", "warn", 3);

        var failed = f.Assignments
            .Where(a => a.BigsellerId == bsId && a.ShopId == shopId && a.Op == op && a.Status == "failed")
            .OrderByDescending(a => a.UpdatedAt).FirstOrDefault();
        if (failed is not null && (DateTimeOffset.Now - failed.UpdatedAt) < TimeSpan.FromMinutes(3))
            return new($"✘ lỗi ({Host(failed.ClaimedByHostname)})", "fail", 3);

        return new("· chờ", "idle", 0);
    }

    public static PresenceState Presence(DateTimeOffset at)
    {
        var s = (DateTimeOffset.Now - at).TotalSeconds;
        if (s < 45) return new("🟢", "online · " + Ago(at), true);
        if (s < 180) return new("🟡", Ago(at), true);
        return new("⚪", "offline · " + Ago(at), false);
    }

    /// <summary>Trạng thái máy cho bảng fleet — 3 mức có nghĩa (thay hiển thị version):
    /// ⚪ offline (mất nhịp ≥180s) · 🟢 online (đang chạy việc: có lease/assignment running) · 🟡 idle (nối nhưng rảnh).</summary>
    public static PresenceState Status(FleetSnapshot f, MachinePresence m)
    {
        if ((DateTimeOffset.Now - m.LastSeen).TotalSeconds >= 180)
            return new("⚪", "offline · " + Ago(m.LastSeen), false);
        var working = f.Leases.Any(l => l.MachineId == m.MachineId && (DateTimeOffset.Now - l.HeartbeatAt) < LeaseFresh)
            || f.Assignments.Any(a => a.Status == "running" && a.ClaimedByMachineId == m.MachineId);
        return working
            ? new("🟢", "online · đang chạy", true)
            : new("🟡", "idle · " + Ago(m.LastSeen), true);
    }

    /// <summary>Máy im nhịp ≥3' hoặc đã biến mất khỏi bảng — chắc chắn KHÔNG claim việc được.</summary>
    public static bool MachineOffline(FleetSnapshot f, string? machineId)
    {
        if (string.IsNullOrEmpty(machineId)) return false;
        var m = f.Machines.FirstOrDefault(x => x.MachineId == machineId);
        return m is null || (DateTimeOffset.Now - m.LastSeen).TotalSeconds >= 180;
    }

    public static string Host(string h) => string.IsNullOrWhiteSpace(h) ? "?" : h;

    public static string Ago(DateTimeOffset at)
    {
        if (at == default || at == DateTimeOffset.MinValue) return "";
        var s = (DateTimeOffset.Now - at).TotalSeconds;
        if (s < 0) s = 0;
        return s < 60 ? $"{(int)s}s trước" : s < 3600 ? $"{(int)(s / 60)} phút trước" : $"{(int)(s / 3600)} giờ trước";
    }

    public static readonly (string key, string display)[] RoleChoices =
    [
        (MachineRoles.Off, "Tắt"), (MachineRoles.Scrape, "Scrape"), (MachineRoles.Import, "Import"),
        (MachineRoles.Update, "Update"), (MachineRoles.All, "Mọi việc"),
    ];
}
