namespace Shopee.Hub.Web.Services;

/// <summary>Một shop cần giao việc (đơn vị nhỏ nhất operator tick ở trang /dispatch). <see cref="AccountName"/>
/// CHỈ dùng để viết lý do bỏ qua cho người đọc — để trống thì lùi về <see cref="AccountId"/>.</summary>
public sealed record DispatchTarget(string AccountId, string ShopId, string Sheet, string ShopName, string AccountName = "");

/// <summary>Quỹ chạy của 1 máy lúc cân tải: <see cref="Free"/> = số khung Brave còn trống (trần máy tự báo trừ
/// việc đang chạy/chờ), <see cref="Running"/> = số việc đang chạy/chờ (chỉ để HIỂN THỊ, thuật toán không dùng).</summary>
public sealed record MachineBudget(string MachineId, string Hostname, bool Online, int Free, int Running);

/// <summary>Kết quả cân tải: <see cref="ByMachine"/> = máy → các shop được giao; <see cref="Skipped"/> = các dòng
/// lý do người-đọc-được (bỏ qua vì không xếp được, hoặc cảnh báo "xếp vào máy đã hết quỹ").</summary>
public sealed record BalancePlan(Dictionary<string, List<DispatchTarget>> ByMachine, List<string> Skipped);

/// <summary>
/// Chia một tập shop cho các máy client. Thuần BCL (không đụng Blazor/DB) để test được VÀ để trang /dispatch
/// dựng dòng XEM TRƯỚC bằng ĐÚNG hàm dùng lúc giao thật (khỏi 2 nhánh logic lệch nhau).
/// </summary>
public static class DispatchBalancer
{
    /// <summary>
    /// Xếp <paramref name="targets"/> vào <paramref name="machines"/> theo luật (đúng thứ tự này):
    /// <list type="number">
    /// <item>Gom target theo tài khoản — 1 acc BigSeller chỉ chạy trên MỘT máy tại một thời điểm nên cả nhóm
    ///       phải về cùng một máy.</item>
    /// <item>Máy đích của nhóm: (a) <paramref name="holds"/> = máy ĐANG giữ acc nếu còn online — máy DUY NHẤT
    ///       hợp lệ, hết quỹ vẫn xếp vào (client tự xếp hàng) kèm cảnh báo; (b) <paramref name="homes"/> =
    ///       affinity trusted-device nếu online và còn quỹ; (c) máy online còn quỹ nhiều nhất.</item>
    /// <item>Không máy nào thoả → thêm lý do vào <see cref="BalancePlan.Skipped"/>.</item>
    /// <item>Mỗi nhóm acc giao được thì trừ ĐÚNG 1 khỏi quỹ máy (một acc chiếm một khung, KHÔNG phải mỗi shop
    ///       một khung).</item>
    /// </list>
    /// Tất định: tie-break máy theo MachineId ordinal; nhóm acc xử lý theo thứ tự xuất hiện trong
    /// <paramref name="targets"/> (= đúng thứ tự bảng operator đang nhìn).
    /// </summary>
    public static BalancePlan Balance(
        IReadOnlyList<DispatchTarget> targets,
        IReadOnlyList<MachineBudget> machines,
        IReadOnlyDictionary<string, string> holds,
        IReadOnlyDictionary<string, string> homes)
    {
        var byMachine = new Dictionary<string, List<DispatchTarget>>(StringComparer.Ordinal);
        var skipped = new List<string>();

        // Quỹ còn lại — bản sao mutate được (KHÔNG đụng MachineBudget đầu vào). Trùng MachineId → giữ bản đầu.
        var byId = new Dictionary<string, MachineBudget>(StringComparer.Ordinal);
        var free = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in machines)
        {
            if (m is null || m.MachineId.Length == 0 || byId.ContainsKey(m.MachineId)) continue;
            byId[m.MachineId] = m;
            free[m.MachineId] = m.Free;
        }

        foreach (var g in targets.Where(t => t is not null).GroupBy(t => t.AccountId, StringComparer.Ordinal))
        {
            var group = g.ToList();
            var name = AcctName(group[0]);
            string? pick;

            if (holds.TryGetValue(g.Key, out var holder) && !string.IsNullOrEmpty(holder))
            {
                // (a) Acc đang bị giữ → máy giữ là máy DUY NHẤT hợp lệ. Máy đó tắt = nhóm này không giao được
                //     (giao sang máy khác thì việc nằm chờ mãi vì khoá 1-acc-1-máy).
                if (!byId.TryGetValue(holder, out var hm) || !hm.Online)
                {
                    skipped.Add($"{name} ({group.Count} shop): máy {HostOf(byId, holder)} đang giữ acc nhưng đang offline");
                    continue;
                }
                pick = holder;
                // Hết quỹ vẫn xếp vào (client tự xếp hàng) — nhưng phải nói trước để operator khỏi tưởng treo.
                if (free[holder] <= 0) skipped.Add($"{name}: {HostOf(byId, holder)} đã hết quỹ Brave, việc sẽ phải chờ");
            }
            else if (homes.TryGetValue(g.Key, out var home) && !string.IsNullOrEmpty(home)
                     && byId.TryGetValue(home, out var hm2) && hm2.Online && free[home] > 0)
            {
                // (b) Máy "nhà" (đã dựng profile trusted-device cho acc này) — ưu tiên khi còn quỹ.
                pick = home;
            }
            else
            {
                // (c) Máy online còn quỹ NHIỀU NHẤT; hoà thì MachineId nhỏ hơn (ordinal) → kết quả tất định.
                pick = byId.Values
                    .Where(m => m.Online && free[m.MachineId] > 0)
                    .OrderByDescending(m => free[m.MachineId])
                    .ThenBy(m => m.MachineId, StringComparer.Ordinal)
                    .Select(m => m.MachineId)
                    .FirstOrDefault();
                if (pick is null)
                {
                    skipped.Add($"{name} ({group.Count} shop): không máy nào online còn quỹ");
                    continue;
                }
            }

            free[pick]--;   // 1 acc = 1 khung, không phải mỗi shop một khung
            if (!byMachine.TryGetValue(pick, out var list)) byMachine[pick] = list = new List<DispatchTarget>();
            list.AddRange(group);
        }

        return new BalancePlan(byMachine, skipped);
    }

    private static string AcctName(DispatchTarget t) =>
        string.IsNullOrWhiteSpace(t.AccountName) ? t.AccountId : t.AccountName;

    private static string HostOf(Dictionary<string, MachineBudget> byId, string machineId) =>
        byId.TryGetValue(machineId, out var m) && !string.IsNullOrWhiteSpace(m.Hostname) ? m.Hostname : machineId;
}
