using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>Phần HubDatabase: giao việc Hub→client (assignments) — tạo, claim theo pipeline/single-session, sweep hết-nhịp, huỷ.</summary>
public sealed partial class HubDatabase
{
    // ── Vai trò máy + Giao việc (Hub đẩy việc cho client) ────────────────────────
    public TimeSpan StaleMachine { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>Việc 'running' quá lâu không có nhịp (worker báo "running" mỗi ~10s) ⇒ coi như máy nhận đã
    /// thoát → đánh 'failed' để nhả khoá single-session cho tài khoản đó. Khoá lease (5') là lưới cuối.</summary>
    public TimeSpan StaleRunning { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Đánh 'failed' cho việc 'running' đã hết nhịp (worker chết) → tránh kẹt tài khoản vĩnh viễn.
    /// QUAN TRỌNG: LEASE là tín hiệu "đang chạy" ĐÁNG TIN HƠN nhịp assignment. Nhịp assignment tắt khi client
    /// mất kết nối/hub restart/hub down &gt;5' (nhưng máy vẫn CHẠY THẬT); lease heartbeat 30s lại tự hồi khi
    /// kết nối trở lại. Sweep cũ chỉ nhìn updated_at của assignment → đánh 'failed' oan việc đang chạy → hub mất
    /// khả năng HUỶ việc hub-giao (việc kẹt vô hình). Fix: (1) KHÔNG fail nếu lease còn sống; (2) HỒI SINH việc bị
    /// sweep đánh oan khi lease sống lại (client re-sync) → assignment khớp thực tế, hub huỷ lại được.</summary>
    private void SweepStaleLocked(DateTimeOffset now)
    {
        var cut = Iso(now - StaleRunning);
        // Lease "còn sống" của CÙNG (acc,shop,op) do CHÍNH máy nhận giữ, heartbeat trong ngưỡng stale.
        const string leaseAlive =
            "EXISTS (SELECT 1 FROM leases l WHERE l.bigseller_id=assignments.bigseller_id " +
            "AND l.shop_id=assignments.shop_id AND l.op=assignments.op " +
            "AND l.machine_id=assignments.claimed_by AND l.heartbeat_at >= $cut)";

        // (1) 'running' hết nhịp → 'failed', TRỪ KHI lease còn sống (máy vẫn chạy thật).
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "UPDATE assignments SET status='failed', last_error='hết nhịp (máy nhận có thể đã thoát)', updated_at=$ua "
                + "WHERE status='running' AND updated_at < $cut AND NOT " + leaseAlive;
            c.Parameters.AddWithValue("$ua", Iso(now));
            c.Parameters.AddWithValue("$cut", cut);
            c.ExecuteNonQuery();
        }

        // (2) HỒI SINH: việc bị CHÍNH sweep này đánh 'failed' nhưng lease VẪN SỐNG (client đã re-sync) → về 'running'
        //     để hub huỷ được. Chỉ hồi đúng cờ 'hết nhịp' + lease sống của đúng máy đã claim (an toàn: job thật lỗi
        //     thì client nhả lease → lease chết → không hồi).
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "UPDATE assignments SET status='running', last_error='', updated_at=$ua "
                + "WHERE status='failed' AND last_error='hết nhịp (máy nhận có thể đã thoát)' AND " + leaseAlive;
            c.Parameters.AddWithValue("$ua", Iso(now));
            c.Parameters.AddWithValue("$cut", cut);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>Tạo việc; trùng (cùng bigseller+shop+op) đang chờ/chạy thì TRẢ LẠI việc cũ (idempotent).</summary>
    public Assignment CreateAssignment(CreateAssignmentRequest r)
    {
        lock (_gate)
        {
            // Op TỰ ĐỘNG (không ghim) đã 'completed' trong ledger → KHÔNG tạo lại (chống re-run do NextOp đọc
            // snapshot ledger trễ ở Hub). Ghim tay (Pinned) thì vẫn cho phép để operator chủ động chạy lại.
            if (!r.Pinned)
            {
                var selfLed = ReadLedgerLocked($"{r.BigsellerId}__{r.ShopId}__{r.Op}");
                if (selfLed?.Status == "completed")
                    return new Assignment { BigsellerId = r.BigsellerId, ShopId = r.ShopId, Sheet = r.Sheet, Op = r.Op, Status = "done" };
            }

            var dup = FindOpenAssignmentLocked(r.BigsellerId, r.ShopId, r.Op);
            if (dup is not null)
            {
                // Việc CÒN CHỜ (queued): người dùng giao lại có thể đổi đích/ghim/khoảng dòng/PAYLOAD (vd Search
                // đổi slice link, lane, khu vực) → cập nhật bản chờ để chạy đúng cái mới. RUNNING thì KHÔNG đụng.
                if (dup.Status == "queued")
                {
                    using var u = _conn.CreateCommand();
                    u.CommandText = "UPDATE assignments SET target_machine_id=$t, pinned=$p, start_row=$sr, end_row=$er, payload=$pl, updated_at=$ua WHERE id=$id AND status='queued'";
                    u.Parameters.AddWithValue("$t", (object?)r.TargetMachineId ?? DBNull.Value);
                    u.Parameters.AddWithValue("$p", r.Pinned ? 1 : 0);
                    u.Parameters.AddWithValue("$sr", r.StartRow);
                    u.Parameters.AddWithValue("$er", r.EndRow);
                    u.Parameters.AddWithValue("$pl", r.Payload ?? "");
                    u.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
                    u.Parameters.AddWithValue("$id", dup.Id);
                    u.ExecuteNonQuery();
                    dup.TargetMachineId = r.TargetMachineId; dup.Pinned = r.Pinned;
                    dup.StartRow = r.StartRow; dup.EndRow = r.EndRow; dup.Payload = r.Payload ?? "";
                }
                return dup;
            }

            var now = DateTimeOffset.UtcNow;
            var a = new Assignment
            {
                Id = Guid.NewGuid().ToString("N"),
                BigsellerId = r.BigsellerId, ShopId = r.ShopId, Sheet = r.Sheet, Op = r.Op,
                TargetMachineId = r.TargetMachineId, Pinned = r.Pinned,
                StartRow = r.StartRow, EndRow = r.EndRow, Payload = r.Payload ?? "",
                Status = "queued", CreatedAt = now, UpdatedAt = now,
            };
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO assignments(id,bigseller_id,shop_id,sheet,op,target_machine_id,pinned,status,claimed_by,claimed_host,last_error,created_at,updated_at,start_row,end_row,payload)
VALUES($id,$b,$s,$sh,$o,$t,$p,'queued','','','',$ca,$ua,$sr,$er,$pl);";
            c.Parameters.AddWithValue("$id", a.Id);
            c.Parameters.AddWithValue("$b", a.BigsellerId);
            c.Parameters.AddWithValue("$s", a.ShopId);
            c.Parameters.AddWithValue("$sh", a.Sheet);
            c.Parameters.AddWithValue("$o", a.Op);
            c.Parameters.AddWithValue("$t", (object?)a.TargetMachineId ?? DBNull.Value);
            c.Parameters.AddWithValue("$p", a.Pinned ? 1 : 0);
            c.Parameters.AddWithValue("$ca", Iso(now));
            c.Parameters.AddWithValue("$ua", Iso(now));
            c.Parameters.AddWithValue("$sr", a.StartRow);
            c.Parameters.AddWithValue("$er", a.EndRow);
            c.Parameters.AddWithValue("$pl", a.Payload ?? "");
            c.ExecuteNonQuery();
            return a;
        }
    }

    /// <summary>Việc đang mở (queued|running) cho 1 đơn vị (bigseller+shop+op). null nếu không có.</summary>
    private Assignment? FindOpenAssignmentLocked(string bsId, string shopId, string op)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT * FROM assignments WHERE bigseller_id=$b AND shop_id=$s AND op=$o AND status IN ('queued','running') LIMIT 1";
        c.Parameters.AddWithValue("$b", bsId);
        c.Parameters.AddWithValue("$s", shopId);
        c.Parameters.AddWithValue("$o", op);
        using var rd = c.ExecuteReader();
        return rd.Read() ? ReadAssignmentRow(rd) : null;
    }

    public List<Assignment> ListAssignments()
    {
        lock (_gate)
        {
            SweepStaleLocked(DateTimeOffset.UtcNow);
            var list = new List<Assignment>();
            using var c = _conn.CreateCommand();
            // Bỏ việc đã kết thúc lâu (>2h) để bảng gọn.
            c.CommandText = "SELECT * FROM assignments WHERE status IN ('queued','running') OR updated_at > $cut ORDER BY created_at";
            c.Parameters.AddWithValue("$cut", Iso(DateTimeOffset.UtcNow - TimeSpan.FromHours(2)));
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add(ReadAssignmentRow(rd));
            return list;
        }
    }

    /// <summary>
    /// Máy <paramref name="machineId"/> (vai trò <paramref name="role"/>) lấy tối đa <paramref name="max"/> việc
    /// đủ điều kiện: đúng vai trò / được ghim, GIỮ nguyên tắc single-session (1 op/1 tài khoản BigSeller),
    /// đúng thứ tự pipeline (import sau khi scrape xong, update sau khi import xong). Atomic dưới _gate.
    /// </summary>
    public List<Assignment> ClaimNext(string machineId, string role, int max)
    {
        if (string.IsNullOrWhiteSpace(machineId) || max <= 0) return [];
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            SweepStaleLocked(now);
            var host = HostnameOfLocked(machineId);
            var claimed = new List<Assignment>();

            // (Tài khoản, op) đang BẬN cho scrape/import (lease tươi BẤT KỲ máy nào, HOẶC assignment running).
            var busy = BusyOpsLocked(now);
            // Chủ sở hữu acc hiện tại: máy nào đang chạy BẤT KỲ op (scrape/import/update) của acc đó. GHIM acc
            // về 1 máy tại 1 thời điểm — máy KHÁC không được đụng op của acc đang do máy khác giữ (chống 1 cookie
            // BigSeller bị dùng từ nhiều IP cùng lúc → BigSeller đá phiên → login lại mãi khi chạy nhiều client).
            var owner = AccountOwnersLocked(now);

            // MỖI CLIENT CHỈ CHẠY 1 TÀI KHOẢN BigSeller TẠI 1 THỜI ĐIỂM: nếu máy này ĐANG giữ 1 acc (owner map),
            // chỉ cho nhận thêm việc của CHÍNH acc đó; chưa giữ acc nào thì acc đầu tiên nhận trong lượt này sẽ
            // khoá máy vào acc đó tới hết lượt. Search (bigseller_id rỗng) KHÔNG tính. Kết hợp với khoá acc-về-1-máy
            // ở trên → tại 1 thời điểm client↔acc là 1:1, tránh 1 máy mở nhiều phiên BigSeller song song.
            string? myAccount = null;
            foreach (var kv in owner)
                if (string.Equals(kv.Value, machineId, StringComparison.Ordinal) && kv.Key.Length > 0) { myAccount = kv.Key; break; }

            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT * FROM assignments WHERE status='queued' ORDER BY created_at";
            var candidates = new List<Assignment>();
            using (var rd = c.ExecuteReader())
                while (rd.Read()) candidates.Add(ReadAssignmentRow(rd));

            foreach (var a in candidates)
            {
                if (claimed.Count >= max) break;
                // Định tuyến: ghim đúng máy này, hoặc (không ghim) vai trò khớp op.
                var routed = a.Pinned || !string.IsNullOrEmpty(a.TargetMachineId)
                    ? string.Equals(a.TargetMachineId, machineId, StringComparison.Ordinal)
                    : MachineRoles.Handles(role, a.Op);
                if (!routed) continue;
                // GHIM acc về 1 máy (xuyên máy): acc đang do MÁY KHÁC giữ → bỏ qua. Máy ĐANG giữ acc vẫn lấy
                // thêm op/shop khác của chính acc đó (cùng 1 IP máy, BigSeller không coi là phiên lạ).
                if (owner.TryGetValue(a.BigsellerId, out var om) && !string.Equals(om, machineId, StringComparison.Ordinal)) continue;
                // 1 CLIENT = 1 ACC: máy đã bận acc khác → bỏ qua (acc khác rỗng nghĩa search thì không chặn).
                if (myAccount is not null && a.BigsellerId.Length > 0 && !string.Equals(a.BigsellerId, myAccount, StringComparison.Ordinal)) continue;
                // Trong CÙNG máy: giữ giới hạn cũ — mỗi acc chỉ 1 shop scrape + 1 shop import cùng lúc (scrape ↔
                // import vẫn song song, kể cả cùng shop). UPDATE không giới hạn (shop nào cũng chạy).
                if (a.Op is "scrape" or "import" && busy.Contains($"{a.BigsellerId}__{a.Op}")) continue;
                // Thứ tự pipeline.
                if (!PipelineReadyLocked(a)) continue;

                using var u = _conn.CreateCommand();
                u.CommandText = "UPDATE assignments SET status='running', claimed_by=$m, claimed_host=$h, updated_at=$ua WHERE id=$id AND status='queued'";
                u.Parameters.AddWithValue("$m", machineId);
                u.Parameters.AddWithValue("$h", host);
                u.Parameters.AddWithValue("$ua", Iso(now));
                u.Parameters.AddWithValue("$id", a.Id);
                if (u.ExecuteNonQuery() == 1)
                {
                    a.Status = "running"; a.ClaimedByMachineId = machineId; a.ClaimedByHostname = host; a.UpdatedAt = now;
                    claimed.Add(a);
                    if (a.Op is "scrape" or "import") busy.Add($"{a.BigsellerId}__{a.Op}");   // không cấp thêm CÙNG op cho acc này trong cùng lượt
                    if (myAccount is null && a.BigsellerId.Length > 0) myAccount = a.BigsellerId;   // khoá máy vào acc vừa nhận (1 client = 1 acc)
                }
            }
            return claimed;
        }
    }

    /// <summary>Tập (tài khoản, op) đang BẬN cho các op CẦN ĐỘC-QUYỀN-THEO-ACC = scrape, import: mỗi acc chỉ
    /// 1 shop scrape + 1 shop import cùng lúc. scrape ↔ import KHÔNG chặn nhau (cùng/khác shop chạy song song).
    /// UPDATE KHÔNG tính ở đây (shop nào cũng chạy được, vì đã import chọn shop). Key = "{bigsellerId}__{op}".
    /// Nguồn bận: lease tươi hoặc assignment đang running.</summary>
    private HashSet<string> BusyOpsLocked(DateTimeOffset now)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "SELECT bigseller_id,op,heartbeat_at,status FROM leases";
            using var rd = c.ExecuteReader();
            while (rd.Read())
            {
                var hb = DateTimeOffset.TryParse(S(rd, 2), out var d) ? d : DateTimeOffset.MinValue;
                if ((now - hb) < StaleLease && S(rd, 3) is "running" or "finishing" && S(rd, 1) is "scrape" or "import")
                    set.Add($"{S(rd, 0)}__{S(rd, 1)}");
            }
        }
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "SELECT bigseller_id,op FROM assignments WHERE status='running' AND op IN ('scrape','import')";
            using var rd = c.ExecuteReader();
            while (rd.Read()) set.Add($"{S(rd, 0)}__{S(rd, 1)}");
        }
        return set;
    }

    /// <summary>Chủ sở hữu hiện tại của mỗi tài khoản BigSeller = máy đang chạy BẤT KỲ op nào (scrape/import/
    /// update) của acc đó, lấy từ lease TƯƠI hoặc assignment đang 'running'. Dùng để GHIM acc về 1 máy tại 1
    /// thời điểm: máy khác không được claim op của acc đang do máy khác giữ → 1 cookie BigSeller không bị dùng từ
    /// nhiều IP cùng lúc (nguyên nhân BigSeller đá phiên, login lại mãi khi chạy nhiều client). Key = bigsellerId
    /// → machineId (giá trị đầu gặp; sau khi hội tụ chỉ 1 máy/acc — giai đoạn quá độ nếu 2 máy còn giữ thì 1 máy
    /// được ghi nhận, máy kia bị chặn, tự hội tụ). Máy chết KHÔNG khoá acc vĩnh viễn: SweepStaleLocked đánh 'failed'
    /// assignment quá hạn nhịp + lease có ngưỡng tươi (StaleLease) nên đều tự nhả.</summary>
    private Dictionary<string, string> AccountOwnersLocked(DateTimeOffset now)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string bs, string m) { if (bs.Length > 0 && m.Length > 0 && !map.ContainsKey(bs)) map[bs] = m; }
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "SELECT bigseller_id,machine_id,heartbeat_at,status FROM leases";
            using var rd = c.ExecuteReader();
            while (rd.Read())
            {
                var hb = DateTimeOffset.TryParse(S(rd, 2), out var d) ? d : DateTimeOffset.MinValue;
                if ((now - hb) < StaleLease && S(rd, 3) is "running" or "finishing") Put(S(rd, 0), S(rd, 1));
            }
        }
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "SELECT bigseller_id,claimed_by FROM assignments WHERE status='running'";
            using var rd = c.ExecuteReader();
            while (rd.Read()) Put(S(rd, 0), S(rd, 1));
        }
        return map;
    }

    /// <summary>Import chỉ chạy khi scrape của (bs+shop) đã 'completed'; update khi import đã 'completed'.
    /// Op tự động đã 'completed' thì không claim lại (chống re-run khi 1 assignment lỡ lọt vào 'queued').</summary>
    private bool PipelineReadyLocked(Assignment a)
    {
        // GHIM TAY = operator CHỦ ĐỘNG chọn chạy op này trên máy này → BỎ QUA ràng buộc thứ tự pipeline (đừng
        // đòi scrape/import phải 'completed' trong ledger). Trước đây pinned vẫn bị chặn nên import/update ghim
        // tay kẹt 'đã xếp' mãi khi scrape chưa 'completed' (scrape dừng dở / login-first / cào ở máy khác).
        // Lưới an toàn vẫn còn: tiền-kiểm workbook/sheet phía client (CanDispatchUpdate) + single-session (busy).
        if (a.Pinned) return true;

        var self = ReadLedgerLocked($"{a.BigsellerId}__{a.ShopId}__{a.Op}");
        if (self?.Status == "completed") return false;
        string? need = a.Op switch { "import" => "scrape", "update" => "import", _ => null };
        if (need is null) return true;
        var key = $"{a.BigsellerId}__{a.ShopId}__{need}";
        var led = ReadLedgerLocked(key);
        return led is not null && led.Status == "completed";
    }

    public void UpdateAssignmentStatus(string id, string machineId, string status, string? error)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            if (status == "requeue")
            {
                // Máy nhận đã claim (running) nhưng CHƯA chạy được vì lỗi TẠM THỜI (client mới chưa kịp đồng bộ
                // tài khoản/workbook…) → trả việc về 'queued' + bỏ claim để claim lại nhịp sau. Giữ '• đã xếp'
                // thay vì lặng lẽ 'failed' → về 'chờ'. Chỉ tác động khi CHÍNH máy đó còn giữ (running).
                c.CommandText = "UPDATE assignments SET status='queued', claimed_by='', claimed_host='', last_error=$e, updated_at=$ua WHERE id=$id AND claimed_by=$m AND status='running'";
                c.Parameters.AddWithValue("$e", error ?? "");
                c.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
                c.Parameters.AddWithValue("$id", id);
                c.Parameters.AddWithValue("$m", machineId);
                c.ExecuteNonQuery();
                return;
            }
            // Guard: chỉ đổi khi đang 'running' (hoặc set lại CHÍNH trạng thái đó) → KHÔNG cho nhịp 'running'
            // hồi sinh việc đã done/failed/canceled (giữ Cancel có hiệu lực + không hiện job ma).
            c.CommandText = "UPDATE assignments SET status=$st, last_error=$e, updated_at=$ua WHERE id=$id AND claimed_by=$m AND (status='running' OR status=$st)";
            c.Parameters.AddWithValue("$st", status);
            c.Parameters.AddWithValue("$e", error ?? "");
            c.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$m", machineId);
            c.ExecuteNonQuery();
        }
    }

    public void CancelAssignment(string id)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "UPDATE assignments SET status='canceled', updated_at=$ua WHERE id=$id AND status IN ('queued','running')";
            c.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    private static Assignment ReadAssignmentRow(SqliteDataReader rd)
    {
        // Cột theo thứ tự bảng: id,bigseller_id,shop_id,sheet,op,target_machine_id,pinned,status,claimed_by,claimed_host,last_error,created_at,updated_at
        int i(string n) => rd.GetOrdinal(n);
        return new Assignment
        {
            Id = S(rd, i("id")), BigsellerId = S(rd, i("bigseller_id")), ShopId = S(rd, i("shop_id")),
            Sheet = S(rd, i("sheet")), Op = S(rd, i("op")),
            TargetMachineId = rd.IsDBNull(i("target_machine_id")) ? null : rd.GetString(i("target_machine_id")),
            Pinned = !rd.IsDBNull(i("pinned")) && rd.GetInt32(i("pinned")) != 0,
            Status = S(rd, i("status")), ClaimedByMachineId = S(rd, i("claimed_by")),
            ClaimedByHostname = S(rd, i("claimed_host")), LastError = S(rd, i("last_error")),
            StartRow = rd.IsDBNull(i("start_row")) ? 0 : rd.GetInt32(i("start_row")),
            EndRow = rd.IsDBNull(i("end_row")) ? 0 : rd.GetInt32(i("end_row")),
            Payload = S(rd, i("payload")),
            CreatedAt = D(rd, i("created_at")), UpdatedAt = D(rd, i("updated_at")),
        };
    }
}
