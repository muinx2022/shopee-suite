using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>Phần HubDatabase: giao việc Hub→client (assignments) — tạo, claim theo pipeline + độc quyền acc (1 acc = 1 máy), sweep hết-nhịp, huỷ.</summary>
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
                    u.CommandText = "UPDATE assignments SET target_machine_id=$t, pinned=$p, start_row=$sr, end_row=$er, payload=$pl, processes=$pr, frame_size=$fs, reload_seconds=$rl, updated_at=$ua WHERE id=$id AND status='queued'";
                    u.Parameters.AddWithValue("$t", (object?)r.TargetMachineId ?? DBNull.Value);
                    u.Parameters.AddWithValue("$p", r.Pinned ? 1 : 0);
                    u.Parameters.AddWithValue("$sr", r.StartRow);
                    u.Parameters.AddWithValue("$er", r.EndRow);
                    u.Parameters.AddWithValue("$pl", r.Payload ?? "");
                    u.Parameters.AddWithValue("$pr", r.Processes);
                    u.Parameters.AddWithValue("$fs", r.FrameSize);
                    u.Parameters.AddWithValue("$rl", r.ReloadSeconds);
                    u.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
                    u.Parameters.AddWithValue("$id", dup.Id);
                    u.ExecuteNonQuery();
                    dup.TargetMachineId = r.TargetMachineId; dup.Pinned = r.Pinned;
                    dup.StartRow = r.StartRow; dup.EndRow = r.EndRow; dup.Payload = r.Payload ?? "";
                    dup.Processes = r.Processes; dup.FrameSize = r.FrameSize; dup.ReloadSeconds = r.ReloadSeconds;
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
                Processes = r.Processes, FrameSize = r.FrameSize, ReloadSeconds = r.ReloadSeconds,
                Status = "queued", CreatedAt = now, UpdatedAt = now,
            };
            using var c = _conn.CreateCommand();
            c.CommandText = @"
INSERT INTO assignments(id,bigseller_id,shop_id,sheet,op,target_machine_id,pinned,status,claimed_by,claimed_host,last_error,created_at,updated_at,start_row,end_row,payload,processes,frame_size,reload_seconds)
VALUES($id,$b,$s,$sh,$o,$t,$p,'queued','','','',$ca,$ua,$sr,$er,$pl,$pr,$fs,$rl);";
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
            c.Parameters.AddWithValue("$pr", a.Processes);
            c.Parameters.AddWithValue("$fs", a.FrameSize);
            c.Parameters.AddWithValue("$rl", a.ReloadSeconds);
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

    /// <summary>Việc GIÁN ĐOẠN để operator bấm ▶ Tiếp tục (resume): bản MỚI NHẤT của mỗi nhóm (acc,shop,op)
    /// đang 'failed'/'canceled' trong 7 ngày, MÀ nhóm đó không còn bản queued|running đang mở VÀ ledger CHƯA
    /// 'completed'. Sweep trước như <see cref="ListAssignments"/> để 'running' chết được quy về 'failed' rồi mới lọc.</summary>
    public List<Assignment> ListInterrupted()
    {
        lock (_gate)
        {
            SweepStaleLocked(DateTimeOffset.UtcNow);
            return ListInterruptedLocked();
        }
    }

    /// <summary>Lõi ListInterrupted (gọi TRONG lock). Quét mọi bản 'failed'/'canceled' trong 7 ngày, sắp mới→cũ,
    /// giữ bản MỚI NHẤT mỗi nhóm (acc,shop,op); loại nhóm còn việc đang mở (đang chạy tiếp) hoặc ledger đã
    /// 'completed' (đã xong, khỏi mời tiếp tục). op='search' KHÔNG ghi ledger → bỏ điều kiện ledger.</summary>
    private List<Assignment> ListInterruptedLocked()
    {
        var cut = Iso(DateTimeOffset.UtcNow - TimeSpan.FromDays(7));
        var rows = new List<Assignment>();
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "SELECT * FROM assignments WHERE status IN ('failed','canceled') AND updated_at >= $cut ORDER BY updated_at DESC";
            c.Parameters.AddWithValue("$cut", cut);
            using var rd = c.ExecuteReader();
            while (rd.Read()) rows.Add(ReadAssignmentRow(rd));
        }
        var result = new List<Assignment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);   // nhóm đã xét (chỉ giữ bản mới nhất — đã ORDER BY desc)
        foreach (var a in rows)
        {
            var grp = $"{a.BigsellerId}__{a.ShopId}__{a.Op}";
            if (!seen.Add(grp)) continue;                          // bản cũ hơn của nhóm đã lấy → bỏ
            // Nhóm còn việc queued|running → KHÔNG "gián đoạn" (đang chờ/chạy tiếp) → bỏ.
            if (FindOpenAssignmentLocked(a.BigsellerId, a.ShopId, a.Op) is not null) continue;
            // Đã xong theo ledger → khỏi mời tiếp tục (search không ghi ledger nên bỏ qua điều kiện này).
            if (a.Op != "search" && ReadLedgerLocked(grp)?.Status == "completed") continue;
            result.Add(a);
        }
        return result;
    }

    /// <summary>
    /// Máy <paramref name="machineId"/> (vai trò <paramref name="role"/>) lấy tối đa <paramref name="max"/> việc
    /// đủ điều kiện: đúng vai trò / được ghim, đúng thứ tự pipeline (import sau khi scrape xong, update sau khi
    /// import xong). Luật độc quyền acc: 1 acc chỉ do 1 MÁY chạy tại 1 thời điểm (owner, xuyên máy) — NHƯNG 1 máy
    /// được chạy NHIỀU acc song song; trong 1 acc chỉ 1 việc (scrape/import/update/rewrite) tại 1 thời điểm
    /// (mọi op dùng CHUNG cookie BigSeller của acc → không cho chồng). Atomic dưới _gate.
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

            // Tài khoản đang BẬN (lease tươi BẤT KỲ máy nào, HOẶC assignment running) — mỗi acc chỉ 1 việc
            // scrape/import/update/rewrite tại 1 thời điểm (chung cookie BigSeller).
            var busy = BusyOpsLocked(now);
            // Chủ sở hữu acc hiện tại: máy nào đang chạy BẤT KỲ op (scrape/import/update/rewrite) của acc đó. GHIM
            // acc về 1 máy tại 1 thời điểm — máy KHÁC không được đụng op của acc đang do máy khác giữ (chống 1 cookie
            // BigSeller bị dùng từ nhiều IP cùng lúc → BigSeller đá phiên → login lại mãi khi chạy nhiều client).
            // 1 máy VẪN được chạy NHIỀU acc song song (không còn luật "1 client = 1 acc").
            var owner = AccountOwnersLocked(now);

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
                // Mỗi acc chỉ 1 việc tại 1 thời điểm (scrape/import/update/rewrite đều đụng CHUNG cookie acc —
                // scrape ghi ngược muc_token sau mỗi link, update/import ghi định kỳ → 2 việc song song cùng acc
                // = 2 phiên cùng ghi 1 file cookie → nguy cơ đè token mới bằng token cũ → "log in first"). Cũng
                // khớp sổ job client (_wsJobs: 1 workflow/acc) — việc thứ 2 nằm 'queued' chạy nối tiếp, không failed oan.
                var fam = OpFamily(a.Op);
                if (fam is not null && a.BigsellerId.Length > 0 && busy.Contains($"{a.BigsellerId}__{fam}")) continue;
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
                    if (fam is not null && a.BigsellerId.Length > 0) busy.Add($"{a.BigsellerId}__{fam}");   // không cấp thêm việc cho acc này trong cùng lượt
                }
            }
            return claimed;
        }
    }

    /// <summary>Nhóm op để tính "bận theo acc": scrape/import/update/rewrite gộp CHUNG nhóm "bs" — mọi op đều
    /// dùng cookie BigSeller của acc (scrape ghi ngược muc_token sau mỗi link, update/import ghi định kỳ) nên
    /// 2 việc bất kỳ của cùng acc chạy song song = 2 phiên cùng ghi 1 file cookie → nguy cơ rotation-war token
    /// ("log in first"). null = op KHÔNG tính bận (vd "search" — không đụng cookie BigSeller).</summary>
    private static string? OpFamily(string op) => op switch
    {
        "scrape" or "import" or "update" or "rewrite" => "bs",
        _ => null,
    };

    /// <summary>Tập tài khoản đang BẬN (key = "{bigsellerId}__{family}"): mỗi acc chỉ 1 việc scrape/import/
    /// update/rewrite tại 1 thời điểm (chung cookie BigSeller — xem <see cref="OpFamily"/>); việc kế cùng acc
    /// nằm 'queued' chạy nối tiếp. Nguồn bận: lease tươi hoặc assignment đang running.</summary>
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
                var fam = OpFamily(S(rd, 1));
                if ((now - hb) < StaleLease && S(rd, 3) is "running" or "finishing" && fam is not null)
                    set.Add($"{S(rd, 0)}__{fam}");
            }
        }
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "SELECT bigseller_id,op FROM assignments WHERE status='running' AND op IN ('scrape','import','update','rewrite')";
            using var rd = c.ExecuteReader();
            while (rd.Read())
            {
                var fam = OpFamily(S(rd, 1));
                if (fam is not null) set.Add($"{S(rd, 0)}__{fam}");
            }
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

    /// <summary>Operator bấm ▶ Tiếp tục 1 việc đã dừng/huỷ: đưa về 'queued' để claim lại, GIỮ NGUYÊN mọi cột
    /// tham số (khoảng dòng/payload/processes/frame/reload/đích/ghim) → chạy tiếp đúng lượt cũ. Trả null = OK;
    /// chuỗi tiếng Việt = lý do từ chối. Chỉ tiếp tục được việc 'failed'/'canceled'; nhóm (acc,shop,op) đang có
    /// việc mở thì từ chối (kẻo 2 bản chạy song song → 2 phiên cùng cookie BigSeller).</summary>
    public string? ResumeAssignment(string id)
    {
        lock (_gate)
        {
            Assignment? a;
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "SELECT * FROM assignments WHERE id=$id";
                c.Parameters.AddWithValue("$id", id);
                using var rd = c.ExecuteReader();
                a = rd.Read() ? ReadAssignmentRow(rd) : null;
            }
            if (a is null) return "không tìm thấy việc để tiếp tục";
            if (a.Status is not ("failed" or "canceled")) return "chỉ tiếp tục được việc đã dừng/hủy";
            if (FindOpenAssignmentLocked(a.BigsellerId, a.ShopId, a.Op) is not null)
                return "đã có việc khác đang mở cho op này";

            // Chỉ đụng status + bỏ claim + xoá cờ lỗi; KHÔNG chạm các cột tham số → lượt chạy lại y lệnh cũ.
            using var u = _conn.CreateCommand();
            u.CommandText = "UPDATE assignments SET status='queued', claimed_by='', claimed_host='', last_error='', updated_at=$ua WHERE id=$id";
            u.Parameters.AddWithValue("$ua", Iso(DateTimeOffset.UtcNow));
            u.Parameters.AddWithValue("$id", id);
            u.ExecuteNonQuery();
            return null;
        }
    }

    /// <summary>Client gọi lúc KHỞI ĐỘNG LẠI (re-attach) để nhận lại việc đang dở của CHÍNH máy mình. Khác
    /// <see cref="ResetMachineWork"/> (operator CHỦ ĐỘNG xoá máy → HUỶ việc): đây là máy vừa bật lại nên process
    /// cũ CHẮC CHẮN đã chết → nhả khoá của nó rồi ĐƯA VIỆC VỀ HÀNG CHỜ, không huỷ. Trả tổng số việc về 'queued'.</summary>
    public int ResumeMachineWork(string machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return 0;
        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            // (1) Nhả lease + account-lease của process ĐÃ CHẾT (máy vừa khởi động lại) → khoá single-session nhả
            //     NGAY, khỏi chờ 5' stale. KHÔNG cancel assignment (khác ResetMachineWork).
            foreach (var tbl in new[] { "leases", "account_leases" })
                using (var c = _conn.CreateCommand())
                {
                    c.CommandText = $"DELETE FROM {tbl} WHERE machine_id=$m";
                    c.Parameters.AddWithValue("$m", machineId);
                    c.ExecuteNonQuery();
                }

            // (2) Việc 'running' CHÍNH máy này đang giữ (mồ côi vì process cũ chết) → về 'queued' + bỏ claim để
            //     claim lại vòng sau.
            int n;
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "UPDATE assignments SET status='queued', claimed_by='', claimed_host='', last_error='', updated_at=$ua WHERE claimed_by=$m AND status='running'";
                c.Parameters.AddWithValue("$ua", now);
                c.Parameters.AddWithValue("$m", machineId);
                n = c.ExecuteNonQuery();
            }

            // (3) Hồi việc bị SweepStaleLocked đánh 'failed' OAN khi máy chết giữa chừng (đúng cờ 'hết nhịp'). Làm
            //     TỪNG bản: chỉ hồi khi nhóm (acc,shop,op) KHÔNG còn bản mở khác (kể cả bản vừa queued ở bước 2) VÀ
            //     ledger ≠ 'completed' → chặn chạy lại việc đã xong ở máy khác / tránh 2 bản mở cùng nhóm.
            var revive = new List<Assignment>();
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "SELECT * FROM assignments WHERE claimed_by=$m AND status='failed' AND last_error='hết nhịp (máy nhận có thể đã thoát)'";
                c.Parameters.AddWithValue("$m", machineId);
                using var rd = c.ExecuteReader();
                while (rd.Read()) revive.Add(ReadAssignmentRow(rd));
            }
            foreach (var a in revive)
            {
                if (FindOpenAssignmentLocked(a.BigsellerId, a.ShopId, a.Op) is not null) continue;   // nhóm còn bản mở → bỏ
                if (ReadLedgerLocked($"{a.BigsellerId}__{a.ShopId}__{a.Op}")?.Status == "completed") continue;   // đã xong ở máy khác
                using var u = _conn.CreateCommand();
                u.CommandText = "UPDATE assignments SET status='queued', claimed_by='', claimed_host='', last_error='', updated_at=$ua WHERE id=$id AND status='failed'";
                u.Parameters.AddWithValue("$ua", now);
                u.Parameters.AddWithValue("$id", a.Id);
                if (u.ExecuteNonQuery() == 1) n++;
            }
            return n;
        }
    }

    /// <summary>Trạng thái của bản assignment MỚI NHẤT của nhóm (acc,shop,op); null nếu chưa từng có việc.
    /// DispatcherService dùng cho "sticky-cancel": nhóm mà bản mới nhất bị operator HUỶ ('canceled') thì auto
    /// KHÔNG tạo lại. Query nhẹ (LIMIT 1) — đừng nạp cả bảng.</summary>
    public string? LatestAssignmentStatus(string bs, string shop, string op)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT status FROM assignments WHERE bigseller_id=$b AND shop_id=$s AND op=$o ORDER BY updated_at DESC LIMIT 1";
            c.Parameters.AddWithValue("$b", bs);
            c.Parameters.AddWithValue("$s", shop);
            c.Parameters.AddWithValue("$o", op);
            return c.ExecuteScalar() as string;
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
            Processes = rd.IsDBNull(i("processes")) ? 0 : rd.GetInt32(i("processes")),
            FrameSize = rd.IsDBNull(i("frame_size")) ? 0 : rd.GetInt32(i("frame_size")),
            ReloadSeconds = rd.IsDBNull(i("reload_seconds")) ? 0 : rd.GetInt32(i("reload_seconds")),
            CreatedAt = D(rd, i("created_at")), UpdatedAt = D(rd, i("updated_at")),
        };
    }
}
