using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Hub;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Components;

/// <summary>Một dòng shop của trang Giao việc: shop trong config ĐÃ gán ngăn dữ liệu (shop chưa gán không giao
/// việc được nên không thành dòng — xem <c>DispatchRowSet.HiddenNoSheet</c>).</summary>
public sealed class DispatchShopRow
{
    public string AccountId = "", AccountName = "", ShopId = "", ShopName = "", Sheet = "", Key = "";
}

public sealed record DispatchRowGroup(string AccountId, string AccountName, List<DispatchShopRow> Rows);

/// <summary>Kết quả một lượt dựng dòng: dòng đã sắp + số shop bị ẩn vì chưa gán ngăn dữ liệu + tên tài khoản/shop
/// theo GUID (dựng từ TOÀN BỘ config, kể cả shop bị ẩn, vì <c>Snap.Interrupted</c> có thể trỏ tới shop như vậy).</summary>
public sealed record DispatchRowSet(
    List<DispatchShopRow> Rows,
    int HiddenNoSheet,
    Dictionary<string, string> AcctNames,
    Dictionary<string, string> ShopNames);

/// <summary>Quỹ chạy + hiện diện của 1 máy, dựng cho hàng thẻ chọn máy: <c>Free</c> = số khung Brave còn
/// trống (trần máy tự báo trừ việc đang chạy/chờ), <c>Running</c> = số việc đang chạy/chờ.</summary>
public sealed record MachineBudget(
    string MachineId, string Hostname, bool Online, int Free, int Running, string Version, DateTimeOffset LastSeen,
    string Mode);

/// <summary>Mọi thứ luật khoá/nhãn nút của lưới Giao việc cần — trang dựng 1 lần mỗi nhịp rồi truyền xuống lưới,
/// để trang và component con tính nút bằng ĐÚNG một hàm (<see cref="DispatchButtons"/>).</summary>
public sealed record DispatchGridContext(
    FleetSnapshot Snap,
    Dictionary<string, string> Holds,
    string SelMachine,
    string ConfirmAcct,
    string ConfirmCancel);

/// <summary>Dựng dòng/quỹ máy/bộ lọc cho trang Giao việc ("/dispatch") — thuần dữ liệu: không đọc DB, không giữ
/// state UI. KHÔNG overlay ledger như trang BigSeller: shop mồ côi không giao việc được nữa.</summary>
public static class DispatchRowsBuilder
{
    // 3 op hub giao được cho client BigSeller (rewrite chạy trên hub → không có ở đây).
    public static readonly string[] DispatchOps = [AssignmentOps.Scrape, AssignmentOps.Import, AssignmentOps.Update];

    /// <summary>Trần Brave mặc định khi máy chưa báo <c>MaxBrave</c> (0 = CHƯA BÁO, không phải "không có quỹ").</summary>
    public const int DefaultBrave = 2;

    // Dòng bảng = MỌI shop trong config ĐÃ gán ngăn dữ liệu (shop chưa gán thì không giao việc được → chỉ đếm
    // để nói ở dòng hint).
    public static DispatchRowSet BuildRows(List<BigSellerAccount> accounts)
    {
        var rows = new List<DispatchShopRow>();
        var hidden = 0;
        var configured = FleetViewProjection.ConfiguredShops(accounts);
        var acctNames = accounts.GroupBy(a => a.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.Ordinal);
        var shopNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var shop in configured)
        {
            shopNames[shop.ShopId] = shop.ShopName;
            if (!shop.HasSheet) { hidden++; continue; }
            rows.Add(new DispatchShopRow
            {
                AccountId = shop.AccountId,
                AccountName = shop.AccountName,
                ShopId = shop.ShopId,
                ShopName = shop.ShopName,
                Sheet = shop.Sheet,
                Key = shop.Key,
            });
        }
        return new DispatchRowSet(
            rows.OrderBy(r => r.AccountName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(r => r.ShopName, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            hidden, acctNames, shopNames);
    }

    /// <summary>Số việc hub-giao đang chạy/chờ gắn từng máy (khoá = machineId) — cả hàng thẻ chọn máy lẫn bảng
    /// "Máy online" đọc lại kết quả này.</summary>
    public static Dictionary<string, int> UsedByMachine(FleetSnapshot snap)
    {
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in snap.Assignments.Where(a => a.Status is AssignmentStatus.Queued or AssignmentStatus.Running))
        {
            var mid = !string.IsNullOrEmpty(a.ClaimedByMachineId) ? a.ClaimedByMachineId : a.TargetMachineId ?? "";
            if (mid.Length == 0) continue;
            used[mid] = used.GetValueOrDefault(mid) + 1;
        }
        return used;
    }

    // Quỹ máy: trần Brave máy tự báo (0 = chưa báo → coi như DefaultBrave) trừ số việc đang chạy/chờ gắn máy đó.
    public static List<MachineBudget> BuildBudgets(FleetSnapshot snap, Dictionary<string, int> used) => snap.Machines
        // CHỈ suất Workspace: suất đơn hàng KHÔNG claim việc BigSeller (hub cũng chặn cứng ở ClaimNext/
        // CreateAssignment) → hiện ra chỉ tổ giao nhầm rồi việc nằm chờ mãi.
        .Where(m => m.Kind != MachineSlots.Orders)
        .Select(m =>
        {
            var cap = m.MaxBrave > 0 ? m.MaxBrave : DefaultBrave;
            var run = used.GetValueOrDefault(m.MachineId);
            return new MachineBudget(
                m.MachineId,
                string.IsNullOrWhiteSpace(m.Hostname) ? m.MachineId : m.Hostname,
                !FleetStateService.MachineOffline(snap, m.MachineId),
                Math.Max(0, cap - run), run,
                m.AppVersion ?? "", m.LastSeen, m.Mode);
        })
        .OrderByDescending(m => m.Online)
        .ThenBy(m => m.Hostname, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>Máy đang GIỮ từng acc BigSeller (khoá 1-acc-1-máy) — CÙNG luật hub dùng lúc client claim việc
    /// (HubDatabase.AccountOwnersLocked): lease việc còn tươi, rồi tới assignment đang 'running'.
    /// CHÚ Ý: KHÔNG dùng <c>Snap.AccountLeases</c> — bảng đó khoá acc SHOPEE (và khoá "orders:&lt;login&gt;" của
    /// module Đơn hàng), khác hẳn không gian Id của acc BigSeller.</summary>
    public static Dictionary<string, string> Holds(FleetSnapshot snap)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in snap.Leases)
            if (l.BigsellerId.Length > 0 && l.MachineId.Length > 0 && !map.ContainsKey(l.BigsellerId))
                map[l.BigsellerId] = l.MachineId;
        foreach (var a in snap.Assignments.Where(a => a.Status == AssignmentStatus.Running))
            if (a.BigsellerId.Length > 0 && a.ClaimedByMachineId.Length > 0 && !map.ContainsKey(a.BigsellerId))
                map[a.BigsellerId] = a.ClaimedByMachineId;
        return map;
    }

    // Ô TÌM lọc ở mức TÀI KHOẢN, không phải mức shop: gõ tên một shop (vd "delica") → giữ nguyên CẢ tài khoản
    // chứa shop đó cùng MỌI shop của nó. Vì 1 acc chỉ chạy 1 máy tại 1 thời điểm — thấy thiếu shop cùng acc là
    // dễ thao tác sai (nút "cả acc" cũng chỉ đúng nghĩa khi nhìn đủ shop của acc).
    public static List<DispatchShopRow> Filter(
        FleetSnapshot snap, List<DispatchShopRow> rows, string acct, string query, string state)
    {
        var q = query.Trim();
        var hit = q.Length == 0 ? null : rows
            .Where(r => r.ShopName.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || r.AccountName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.AccountId)
            .ToHashSet(StringComparer.Ordinal);

        return rows.Where(r =>
            (acct.Length == 0 || r.AccountId == acct)
            && MatchState(snap, r, state)
            && (hit is null || hit.Contains(r.AccountId)))
            .ToList();
    }

    public static bool MatchState(FleetSnapshot snap, DispatchShopRow r, string state) => state switch
    {
        "run" => DispatchOps.Any(op => Cell(snap, r, op).Kind == 1),
        "int" => DispatchOps.Any(op => Cell(snap, r, op).Kind == 3)
                 || snap.Interrupted.Any(a => a.BigsellerId == r.AccountId && a.ShopId == r.ShopId),
        "all" => true,
        _ => DispatchOps.Any(op => !Cell(snap, r, op).Text.StartsWith('✓')),   // "todo": còn op chưa xong
    };

    public static OpCellState Cell(FleetSnapshot snap, DispatchShopRow r, string op) =>
        FleetStateService.OpCell(snap, r.AccountId, r.ShopId, op);

    public static string LedgerKey(DispatchShopRow r, string op) => $"{r.AccountId}__{r.ShopId}__{op}";

    /// <summary>Assignment hub-giao ĐANG MỞ của đúng ô (acc, shop, op) — <c>Snap.Assignments</c> chứa cả việc op
    /// 'rewrite' lẫn của shop khác nên phải lọc đủ 3 khoá.</summary>
    public static Assignment? OpenAsn(FleetSnapshot snap, DispatchShopRow r, string op) =>
        snap.Assignments.FirstOrDefault(a =>
            a.BigsellerId == r.AccountId && a.ShopId == r.ShopId && a.Op == op
            && a.Status is AssignmentStatus.Queued or AssignmentStatus.Running);

    /// <summary>Lý do lượt chạy TRƯỚC của (shop, op) hỏng — rỗng khi lượt trước không hỏng, hoặc hỏng vì bị HUỶ
    /// TAY (không phải sự cố, nói ra chỉ gây nhiễu). Nguồn Snap.Interrupted: đã lọc sẵn "bản mới nhất mỗi nhóm,
    /// chưa bị Bỏ, không còn việc đang mở, ledger chưa completed" — đúng tập việc còn đáng nói lý do.</summary>
    public static string LastFailReason(FleetSnapshot snap, DispatchShopRow r, string op)
    {
        var a = snap.Interrupted.FirstOrDefault(x =>
            x.BigsellerId == r.AccountId && x.ShopId == r.ShopId && x.Op == op);
        return a is null || string.IsNullOrWhiteSpace(a.LastError) ? "" : a.LastError;
    }

    public static List<MachinePresence> OnlineMachines(FleetSnapshot snap) => snap.Machines
        .Where(FleetStateService.IsOnline)
        .OrderBy(m => m.Kind == MachineSlots.Orders)   // suất workspace (giao việc được) lên trước
        .ThenBy(m => m.Hostname, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>DÒNG của hai bảng chi tiết KPI "đang chạy" / "đang chờ" — trang lấy luôn <c>Count</c> của chúng
    /// làm số trên thẻ, nên thẻ không bao giờ nói một đằng bảng một nẻo. Cụ thể "đang chạy" quét theo Ô LƯỚI —
    /// mà ô lưới ⏳ có thể do LEASE (việc chạy TAY, không có assignment) → những việc đó vẫn hiện trong bảng,
    /// đánh dấu <c>Manual</c> thay vì bị giấu đi.
    /// <para>Cả hai list đếm CẢ phiên module Đơn hàng: phiên đó không nằm trong <paramref name="rows"/> (đơn vị là
    /// TÀI KHOẢN, không phải shop) nên chỉ quét lưới thì thẻ báo 0 trong khi tab Đơn hàng hiện rõ máy đang chạy.
    /// Chúng cộng THẲNG vào hai list (KHÔNG đếm riêng bằng một query COUNT) đúng để giữ bất biến trên.</para></summary>
    public static (List<DispatchWorkItem> Running, List<DispatchWorkItem> Queued) BuildWorkLists(
        FleetSnapshot snap, List<DispatchShopRow> rows, List<OrdersRunningAccount> ordersRunning)
    {
        var running = new List<DispatchWorkItem>();
        var queued = new List<DispatchWorkItem>();
        foreach (var r in rows)
        foreach (var op in DispatchOps)
        {
            var k = Cell(snap, r, op).Kind;
            if (k == 1) running.Add(RunningItem(snap, r, op));
            else if (k == 2 && OpenAsn(snap, r, op) is { Status: AssignmentStatus.Queued } asn) queued.Add(QueuedItem(snap, r, op, asn));
        }

        foreach (var s in ordersRunning)
        {
            // Máy im nhịp ≥3' → bỏ: gương đứng nguyên ở giá trị lần đẩy cuối, nên máy tắt giữa phiên sẽ để lại một
            // dòng "đang chạy" vĩnh viễn. Cùng luật ô lưới BigSeller dùng cho assignment 'running' của máy offline.
            if (FleetStateService.MachineOffline(snap, s.MachineId)) continue;
            switch (s.SessionState)
            {
                // opening/running/stopping đều CHIẾM chỗ thật: 'stopping' còn giữ slot cầu nối, chưa nhả máy.
                case OrdersSessionStates.Opening:
                case OrdersSessionStates.Running:
                case OrdersSessionStates.Stopping:
                    running.Add(OrdersItem(snap, s));
                    break;
                case OrdersSessionStates.Queued:
                    queued.Add(OrdersItem(snap, s));
                    break;
                // Trạng thái lạ (client bản mới hơn hub) → không đếm: thà thiếu còn hơn xếp nhầm thẻ.
            }
        }

        return (running, queued);
    }

    /// <summary>Dòng "đang chạy" của ô (shop, op). Nguồn thật của ô ⏳ theo đúng thứ tự OpCell xét: lease tươi
    /// (chạy tay HOẶC hub-giao) trước, rồi assignment 'running'. Không có assignment ⇒ việc chạy TAY.</summary>
    private static DispatchWorkItem RunningItem(FleetSnapshot snap, DispatchShopRow r, string op)
    {
        // CHỈ nhận lease còn nhịp: bảng leases không xoá khi client chết, nên bản CHẾT của lượt chạy hôm qua
        // vẫn nằm đó — lấy AcquiredAt của nó thì "Chạy từ" báo "1 ngày trước" cho việc vừa khởi động.
        // 120s = đúng ngưỡng FleetStateService dùng để quyết ô có còn ⏳ hay không.
        var lease = snap.Leases.FirstOrDefault(l => l.Key == LedgerKey(r, op)
            && (DateTimeOffset.Now - l.HeartbeatAt).TotalSeconds < 120);
        var asn = snap.Assignments.FirstOrDefault(a =>
            a.BigsellerId == r.AccountId && a.ShopId == r.ShopId && a.Op == op && a.Status == AssignmentStatus.Running);
        // "Chạy từ": lease.AcquiredAt là mốc bắt đầu THẬT; assignment.UpdatedAt bị nhịp 'running' đẩy liên tục
        // nên vô dụng ở đây → cạn lease thì lấy CreatedAt (lúc xếp việc).
        var since = lease is not null ? lease.AcquiredAt : asn?.CreatedAt ?? default;
        var host = asn is not null
            ? HostLabel(snap, asn.ClaimedByMachineId, asn.ClaimedByHostname)
            : HostLabel(snap, lease?.MachineId ?? "", lease?.Hostname ?? "");
        return new DispatchWorkItem(
            LedgerKey(r, op), asn?.Id ?? "", op, r.ShopName, r.AccountName, host, since,
            RowRange(asn?.StartRow ?? 0, asn?.EndRow ?? 0), Manual: asn is null);
    }

    private static DispatchWorkItem QueuedItem(FleetSnapshot snap, DispatchShopRow r, string op, Assignment asn) => new(
        LedgerKey(r, op), asn.Id, op, r.ShopName, r.AccountName,
        // Máy đích rỗng = việc chờ KHÔNG ghim máy nào (bản cũ / điều phối tự động theo vai trò).
        string.IsNullOrEmpty(asn.TargetMachineId) ? "—" : HostLabel(snap, asn.TargetMachineId, ""),
        asn.CreatedAt, RowRange(asn.StartRow, asn.EndRow), Manual: false);

    /// <summary>Dòng bảng chi tiết cho MỘT phiên Đơn hàng. Shop + dải dòng để "—": một phiên chạy CẢ tài khoản
    /// (đăng nhập subaccount rồi lặp lần lượt mọi shop của nó) nên không thuộc shop nào và không có dải dòng.
    /// <c>Since</c> = nhịp gương gần nhất, KHÔNG phải mốc bắt đầu phiên — gương không lưu mốc đó, nên cột thời
    /// gian phải nói "cập nhật" (xem <c>DispatchViewLogic.SinceText</c>).</summary>
    private static DispatchWorkItem OrdersItem(FleetSnapshot snap, OrdersRunningAccount s) => new(
        $"orders|{s.MachineId}|{s.Login}", "", DispatchViewLogic.OrdersOperation, "—", s.Login,
        HostLabel(snap, s.MachineId, s.Hostname), s.UpdatedAt, "—", Manual: false,
        MachineId: s.MachineId, Login: s.Login);

    /// <summary>Tên máy đọc được: ưu tiên hostname máy đang báo nhịp, rồi hostname đóng dấu lúc claim; cạn cả hai
    /// thì id rút gọn. KHÔNG bao giờ trả rỗng — cột Máy trống là dòng vô dụng với người vận hành.</summary>
    public static string HostLabel(FleetSnapshot snap, string machineId, string storedHost)
    {
        if (machineId.Length > 0
            && snap.Machines.FirstOrDefault(x => x.MachineId == machineId) is { } m
            && !string.IsNullOrWhiteSpace(m.Hostname)) return m.Hostname;
        if (!string.IsNullOrWhiteSpace(storedHost)) return storedHost;
        return machineId.Length > 0 ? Short(machineId) : "—";
    }

    /// <summary>Tên tài khoản / shop từ GUID cho bảng gián đoạn (nguồn <c>Snap.Interrupted</c> có thể trỏ tới
    /// shop đã xoá khỏi cấu hình). Tra được → tên, Title rỗng; không tra được → id rút gọn + Title là id đầy đủ.</summary>
    public static (string Text, string Title) NameLabel(Dictionary<string, string> map, string id)
    {
        if (id.Length == 0) return ("—", "");
        return map.TryGetValue(id, out var name) ? (name, "") : (Short(id), "Không còn trong cấu hình · id " + id);
    }

    public static string Short(string id) => id.Length <= 12 ? id : id[..8] + "…";

    /// <summary>Khoảng dòng hub đặt cho lượt chạy; 0 = dùng cấu hình của client (hiện "—", không phải "0–0").</summary>
    public static string RowRange(int start, int end) => start <= 0 && end <= 0
        ? "—"
        : $"{(start > 0 ? start.ToString() : "đầu")}–{(end > 0 ? end.ToString() : "cuối")}";
}

/// <summary>Nhãn + luật khoá của các nút trên lưới Giao việc. Tách khỏi trang vì CẢ trang (lúc xử lý cú bấm) lẫn
/// component lưới (lúc vẽ) đều phải hỏi CÙNG một hàm — hai bản chép tay là chỗ đẻ ra nút bấm được mà không chạy.</summary>
internal static class DispatchButtons
{
    /// <summary>Trạng thái nút của 1 ô, xét theo THỨ TỰ: chưa chọn máy → assignment đang mở → ô đang chạy TAY
    /// (lease) → acc đang bị máy khác giữ → đã xong (chạy lại) → còn lại (giao mới).</summary>
    public static OpBtn Btn(DispatchGridContext ctx, DispatchShopRow r, string op)
    {
        var cell = DispatchRowsBuilder.Cell(ctx.Snap, r, op);
        if (ctx.SelMachine.Length == 0)
            return new(cell.Text, "", "blocked", "Chọn máy client ở trên trước.");

        if (DispatchRowsBuilder.OpenAsn(ctx.Snap, r, op) is { } asn)
        {
            var owner = asn.ClaimedByMachineId.Length > 0 ? asn.ClaimedByMachineId : asn.TargetMachineId ?? "";
            // owner rỗng = việc chờ KHÔNG ghim máy nào (bản cũ) → vẫn cho huỷ, kẻo ô kẹt disable vĩnh viễn.
            if (owner.Length == 0 || string.Equals(owner, ctx.SelMachine, StringComparison.Ordinal))
                return new($"✖ Huỷ · {cell.Text}", "cancel", "busy", $"Huỷ việc {OpLabel(op)} của {r.ShopName}.");
            return new($"⏳ {HostName(ctx.Snap, owner)}", "", "blocked",
                $"{OpLabel(op)} đã xếp/đang chạy ở máy {HostName(ctx.Snap, owner)} — chọn máy đó nếu muốn huỷ.");
        }

        if (cell.Locked)
        {
            var host = ctx.Snap.Leases.FirstOrDefault(l => l.Key == DispatchRowsBuilder.LedgerKey(r, op))?.Hostname ?? "";
            return new(cell.Text, "", "blocked",
                $"Đang chạy TAY trên máy {FleetStateService.Host(host)} — dừng ở app máy đó.");
        }

        if (ctx.Holds.TryGetValue(r.AccountId, out var holder) && !string.Equals(holder, ctx.SelMachine, StringComparison.Ordinal))
            return new(cell.Text, "", "blocked",
                $"Acc {r.AccountName} đang do máy {HostName(ctx.Snap, holder)} giữ — 1 acc chỉ chạy 1 máy.",
                DispatchRowsBuilder.LastFailReason(ctx.Snap, r, op));

        var note = DispatchRowsBuilder.LastFailReason(ctx.Snap, r, op);
        return cell.Text.StartsWith('✓')
            ? new("↻ Chạy lại", "run", "done", $"Chạy lại {OpLabel(op)} · {r.ShopName} trên {HostName(ctx.Snap, ctx.SelMachine)}.", note)
            : new($"▶ {OpLabel(op)}", "run", "run", $"Giao {OpLabel(op)} · {r.ShopName} cho {HostName(ctx.Snap, ctx.SelMachine)}.", note);
    }

    public static OpBtn AcctBtn(DispatchGridContext ctx, DispatchRowGroup g, string op)
    {
        var label = $"▶ {OpLabel(op)} cả acc";
        if (ctx.SelMachine.Length == 0)
            return new(label, "", "blocked", "Chọn máy client ở trên trước.");
        if (ctx.Holds.TryGetValue(g.AccountId, out var holder) && !string.Equals(holder, ctx.SelMachine, StringComparison.Ordinal))
            return new(label, "", "blocked", $"Acc {g.AccountName} đang do máy {HostName(ctx.Snap, holder)} giữ — 1 acc chỉ chạy 1 máy.");

        var n = RunnableCount(ctx, g, op);
        if (n == 0) return new(label, "", "blocked", $"Không dòng nào của {g.AccountName} giao {OpLabel(op)} được lúc này.");
        // Xác nhận 2 bước: một cú bấm tạo NHIỀU việc thật xuống fleet đang chạy.
        return ctx.ConfirmAcct == g.AccountId + "|" + op
            ? new($"Bấm lần nữa để giao {n} việc", "run", "run", $"Giao {OpLabel(op)} cho {n} shop của {g.AccountName}.")
            : new(label, "run", "run", $"Giao {OpLabel(op)} cho {n} shop đang hiện của {g.AccountName} → {HostName(ctx.Snap, ctx.SelMachine)}.");
    }

    /// <summary>Số dòng ĐANG HIỆN của acc mà nút op đó bấm-giao được (dòng disable/đang chạy bị bỏ qua).</summary>
    public static int RunnableCount(DispatchGridContext ctx, DispatchRowGroup g, string op) =>
        g.Rows.Count(r => Btn(ctx, r, op).Act == "run");

    private static string HostName(FleetSnapshot snap, string machineId) => FleetViewProjection.HostName(snap, machineId);

    private static string OpLabel(string op) => FleetViewProjection.OperationLabel(op);
}
