using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>Một shop con trong gương danh bạ (chỉ để XEM trên trang điều phối). <see cref="Login"/> =
/// <c>shop_login</c> phía client = <c>shops.username</c> trên hub → tra được số đơn của shop.</summary>
public sealed record OrdersMirrorShop(string Login, string Name);

/// <summary>Một tài khoản Đơn hàng trong gương của MỘT máy. Khoá là <see cref="Login"/> (email đăng nhập).
/// <see cref="UpdatedAt"/> = lúc máy đó đẩy gương gần nhất (biết dữ liệu còn tươi không).</summary>
public sealed record OrdersMirrorAccount(
    string Login, string SessionState, bool VerifyFailed,
    DateTimeOffset? LastSyncAt, DateTimeOffset UpdatedAt, List<OrdersMirrorShop> Shops)
{
    /// <summary>Mật khẩu đăng nhập tài khoản phụ, dạng thường (rỗng = chưa máy nào góp). Trong THÂN record để
    /// mọi chỗ đang dựng bằng 6 tham số vị trí không phải sửa.</summary>
    public string Password { get; init; } = "";

    /// <summary>Hòm thư nhận mail xác minh (rỗng = chưa máy nào góp).</summary>
    public string VerifyEmail { get; init; } = "";

    /// <summary>Mật khẩu hòm thư xác minh, dạng thường (rỗng = chưa máy nào góp).</summary>
    public string VerifyEmailPassword { get; init; } = "";
}

/// <summary>Số tài khoản / số tài khoản ĐANG CHẠY trong gương của một máy — cho thẻ chọn máy.</summary>
public sealed record OrdersMirrorCount(int Accounts, int Running);

/// <summary>Một tài khoản Đơn hàng ĐANG CHIẾM PHIÊN, trên MÁY NÀO — dòng nguồn cho KPI toàn hệ thống của trang
/// Giao việc. <see cref="Hostname"/> lấy từ bảng <c>machines</c>, rỗng khi máy chưa từng báo nhịp (bên gọi tự lùi
/// về id rút gọn). <see cref="UpdatedAt"/> = lúc máy đẩy gương gần nhất — KHÔNG phải mốc bắt đầu phiên (gương
/// không lưu mốc đó), chỗ nào hiện ra phải nói đúng là "cập nhật lúc".</summary>
public sealed record OrdersRunningAccount(
    string MachineId, string Hostname, string Login, string SessionState, DateTimeOffset UpdatedAt);

/// <summary>Một dòng lệnh hub giao cho suất đơn hàng (đọc ra để trang điều phối bám trạng thái ⏳/✓/✘).</summary>
public sealed record OrdersCommandRow(
    string Id, string MachineId, string Login, string Action, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? AckAt, string Error);

/// <summary>
/// Phần HubDatabase: GƯƠNG danh bạ tài khoản Đơn hàng (client đẩy lên — hub KHÔNG sở hữu) + kênh LỆNH hub →
/// suất đơn hàng (đi trong phản hồi heartbeat, client ack lại).
/// <para><b>Hợp đồng gương:</b> mỗi máy là nguồn sự thật cho danh sách của CHÍNH NÓ → <see cref="UpsertOrdersAccounts"/>
/// THAY TOÀN BỘ danh bạ của đúng <c>machine_id</c> đó trong một transaction. Đây KHÔNG phải "hub tự dọn dữ liệu":
/// dòng của máy khác TUYỆT ĐỐI không đụng tới.</para>
/// <para><b>Từ 11/08/2026 bảng CÓ giữ 3 ô đăng nhập</b> (mật khẩu tài khoản phụ, hòm thư xác minh, mật khẩu hòm
/// thư) dạng thường — xem lý do ở khối chú thích trên <see cref="OrdersAccountsPushRequest"/>. Cookie vẫn KHÔNG
/// bao giờ có mặt. Ba ô này là NGOẠI LỆ của luật "thay toàn bộ": lượt đẩy mang ô RỖNG không được phép xoá giá
/// trị đang giữ (xem <see cref="GiuLaiNeuRong"/>), vì rỗng chỉ có nghĩa "máy này chưa nhập".</para>
/// </summary>
public sealed partial class HubDatabase
{
    /// <summary>Lệnh ở trạng thái 'sent' quá lâu mà client không ack ⇒ coi như client không nhận được/đã thoát →
    /// quy về 'failed' để trang điều phối thôi hiện ⏳ vĩnh viễn. Mốc đo từ <c>created_at</c> (lệnh được lấy đi ở
    /// nhịp heartbeat kế, tức ≤12s sau khi tạo — chênh lệch không đáng kể so với ngưỡng 5').</summary>
    public TimeSpan StaleOrdersCommand { get; init; } = TimeSpan.FromMinutes(5);

    private void EnsureOrdersAccountsSchema() => ExecRaw(@"
CREATE TABLE IF NOT EXISTS orders_accounts(
  machine_id TEXT NOT NULL, login TEXT NOT NULL, session_state TEXT DEFAULT '',
  verify_failed INTEGER DEFAULT 0, last_sync_at TEXT DEFAULT '', updated_at TEXT,
  password TEXT DEFAULT '', verify_email TEXT DEFAULT '', verify_email_password TEXT DEFAULT '',
  PRIMARY KEY(machine_id, login));
CREATE TABLE IF NOT EXISTS orders_account_shops(
  machine_id TEXT NOT NULL, login TEXT NOT NULL, shop_login TEXT NOT NULL, shop_name TEXT DEFAULT '',
  sort_order INTEGER DEFAULT 0,
  PRIMARY KEY(machine_id, login, shop_login));
CREATE TABLE IF NOT EXISTS orders_commands(
  id TEXT PRIMARY KEY, machine_id TEXT NOT NULL, login TEXT NOT NULL, action TEXT NOT NULL,
  status TEXT NOT NULL, created_at TEXT, ack_at TEXT, error TEXT DEFAULT '');
CREATE INDEX IF NOT EXISTS ix_orders_commands_machine ON orders_commands(machine_id, status);");

    // ── Gương danh bạ ───────────────────────────────────────────────────────────

    /// <summary>
    /// (THUẦN) Luật gộp cho 3 ô đăng nhập ở chiều <b>client → Hub</b>: ô mới KHÁC RỖNG thì ghi đè, ô mới RỖNG
    /// thì GIỮ giá trị cũ.
    /// <para>Vì sao không cho rỗng ghi đè: <see cref="UpsertOrdersAccounts"/> xoá-rồi-ghi-lại toàn bộ danh bạ của
    /// máy đó mỗi lượt đẩy (3s/lần khi có thay đổi), mà một máy chưa nhập mật khẩu vẫn đẩy gương bình thường —
    /// để rỗng thắng thì mật khẩu bay ngay nhịp sau.</para>
    /// <para>Vì sao vẫn cho giá trị mới khác rỗng ghi đè (thay vì đóng băng giá trị đầu tiên): user đổi mật khẩu
    /// Shopee thì Hub phải theo kịp, không thì Hub phát tán mật khẩu cũ sang mọi máy mới.</para>
    /// </summary>
    internal static string GiuLaiNeuRong(string? moi, string? cu)
        => string.IsNullOrWhiteSpace(moi) ? (cu ?? "") : moi;

    /// <summary>
    /// Nhận một lượt đẩy gương: THAY TOÀN BỘ danh bạ của <c>r.MachineId</c> (xoá dòng cũ của MÁY ĐÓ rồi ghi lại)
    /// trong MỘT transaction — crash giữa chừng không để lại danh bạ nửa vời. Tài khoản thiếu login bị bỏ
    /// (không có khoá). NGOẠI LỆ: 3 ô đăng nhập được đọc ra TRƯỚC khi xoá rồi gộp theo
    /// <see cref="GiuLaiNeuRong"/>. Trả số tài khoản đã ghi.
    /// </summary>
    public int UpsertOrdersAccounts(OrdersAccountsPushRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.MachineId)) return 0;
        lock (_gate)
        {
            var now = Iso(DateTimeOffset.UtcNow);
            var written = 0;
            using var tx = _conn.BeginTransaction();

            // Ba ô đăng nhập đang giữ của máy này — phải đọc TRƯỚC khối DELETE bên dưới, không thì lượt đẩy nào
            // mang ô rỗng cũng xoá mất dữ liệu tốt.
            var oCu = DocODangNhapLocked(tx, r.MachineId);

            // Xoá danh bạ CŨ của ĐÚNG máy này (không đụng máy khác — xem hợp đồng gương ở chú thích lớp).
            foreach (var tbl in new[] { "orders_accounts", "orders_account_shops" })
            {
                using var d = _conn.CreateCommand();
                d.Transaction = tx;
                d.CommandText = $"DELETE FROM {tbl} WHERE machine_id=$m";
                d.Parameters.AddWithValue("$m", r.MachineId);
                d.ExecuteNonQuery();
            }

            foreach (var a in r.Accounts ?? [])
            {
                var login = a?.Login?.Trim() ?? "";
                if (a is null || login.Length == 0) continue;

                var cu = oCu.TryGetValue(login, out var x) ? x : ("", "", "");
                using (var c = _conn.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = @"
INSERT INTO orders_accounts(machine_id,login,session_state,verify_failed,last_sync_at,updated_at,password,verify_email,verify_email_password)
VALUES($m,$l,$s,$vf,$ls,$ua,$pw,$ve,$vp)
ON CONFLICT(machine_id,login) DO UPDATE SET session_state=$s, verify_failed=$vf, last_sync_at=$ls, updated_at=$ua,
  password=$pw, verify_email=$ve, verify_email_password=$vp;";
                    c.Parameters.AddWithValue("$m", r.MachineId);
                    c.Parameters.AddWithValue("$l", login);
                    c.Parameters.AddWithValue("$s", a.SessionState ?? "");
                    c.Parameters.AddWithValue("$vf", a.VerifyFailed ? 1 : 0);
                    c.Parameters.AddWithValue("$ls", a.LastSyncAt is { } ls ? Iso(ls) : "");
                    c.Parameters.AddWithValue("$ua", now);
                    c.Parameters.AddWithValue("$pw", GiuLaiNeuRong(a.Password, cu.Item1));
                    c.Parameters.AddWithValue("$ve", GiuLaiNeuRong(a.VerifyEmail, cu.Item2));
                    c.Parameters.AddWithValue("$vp", GiuLaiNeuRong(a.VerifyEmailPassword, cu.Item3));
                    c.ExecuteNonQuery();
                }
                written++;

                var order = 0;
                foreach (var s in a.Shops ?? [])
                {
                    var shopLogin = s?.Login?.Trim() ?? "";
                    if (shopLogin.Length == 0) continue;   // không định danh được → bỏ (khoá là shop_login)
                    using var c = _conn.CreateCommand();
                    c.Transaction = tx;
                    c.CommandText = @"
INSERT INTO orders_account_shops(machine_id,login,shop_login,shop_name,sort_order)
VALUES($m,$l,$sl,$sn,$so)
ON CONFLICT(machine_id,login,shop_login) DO UPDATE SET shop_name=$sn, sort_order=$so;";
                    c.Parameters.AddWithValue("$m", r.MachineId);
                    c.Parameters.AddWithValue("$l", login);
                    c.Parameters.AddWithValue("$sl", shopLogin);
                    c.Parameters.AddWithValue("$sn", string.IsNullOrWhiteSpace(s!.Name) ? shopLogin : s.Name.Trim());
                    c.Parameters.AddWithValue("$so", order++);
                    c.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return written;
        }
    }

    /// <summary>Ba ô đăng nhập ĐANG GIỮ của một máy, khoá theo login (không phân biệt hoa/thường — client có thể
    /// đổi cách viết hoa email giữa hai lượt đẩy). Gọi TRONG lock + TRONG transaction của
    /// <see cref="UpsertOrdersAccounts"/>, ngay TRƯỚC khối DELETE.</summary>
    private Dictionary<string, (string, string, string)> DocODangNhapLocked(SqliteTransaction tx, string machineId)
    {
        var map = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        using var c = _conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "SELECT login,password,verify_email,verify_email_password FROM orders_accounts WHERE machine_id=$m";
        c.Parameters.AddWithValue("$m", machineId);
        using var rd = c.ExecuteReader();
        while (rd.Read())
        {
            var login = S(rd, 0).Trim();
            if (login.Length == 0) continue;
            map[login] = (S(rd, 1), S(rd, 2), S(rd, 3));
        }
        return map;
    }

    /// <summary>Danh bạ tài khoản Đơn hàng của MỘT máy (kèm shop con, đúng thứ tự máy đó đẩy lên). Máy chưa đẩy
    /// gương (client bản cũ) → list RỖNG, KHÔNG lỗi.</summary>
    public List<OrdersMirrorAccount> OrdersAccountsOf(string machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return [];
        lock (_gate)
        {
            // Shop gom trước theo login để khỏi query lồng trong vòng lặp (1 acc có thể có hàng chục shop).
            var shops = new Dictionary<string, List<OrdersMirrorShop>>(StringComparer.Ordinal);
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "SELECT login,shop_login,shop_name FROM orders_account_shops WHERE machine_id=$m ORDER BY sort_order, shop_login";
                c.Parameters.AddWithValue("$m", machineId);
                using var rd = c.ExecuteReader();
                while (rd.Read())
                {
                    var login = S(rd, 0);
                    var shopLogin = S(rd, 1);
                    var name = S(rd, 2);
                    if (!shops.TryGetValue(login, out var list)) shops[login] = list = [];
                    list.Add(new OrdersMirrorShop(shopLogin, name.Length > 0 ? name : shopLogin));
                }
            }

            var accounts = new List<OrdersMirrorAccount>();
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "SELECT login,session_state,verify_failed,last_sync_at,updated_at,password,verify_email,verify_email_password FROM orders_accounts WHERE machine_id=$m ORDER BY login COLLATE NOCASE";
                c.Parameters.AddWithValue("$m", machineId);
                using var rd = c.ExecuteReader();
                while (rd.Read())
                {
                    var login = S(rd, 0);
                    var ls = D(rd, 3);
                    accounts.Add(new OrdersMirrorAccount(
                        login, S(rd, 1), !rd.IsDBNull(2) && rd.GetInt32(2) != 0,
                        ls == DateTimeOffset.MinValue ? null : ls, D(rd, 4),
                        shops.TryGetValue(login, out var list) ? list : [])
                    {
                        Password = S(rd, 5),
                        VerifyEmail = S(rd, 6),
                        VerifyEmailPassword = S(rd, 7),
                    });
                }
            }
            return accounts;
        }
    }

    /// <summary>
    /// DANH BẠ sub-acc Đơn hàng GỘP TỪ MỌI MÁY, distinct theo <c>login</c> (ignore-case), shop UNION theo
    /// <c>shop_login</c> (ignore-case, tên hiển thị lấy bản KHÔNG rỗng đầu tiên). Dùng cho máy MỚI kéo về để tạo
    /// sẵn bản ghi tài khoản rỗng-mật-khẩu — vì thế <see cref="OrdersMirrorAccount.SessionState"/>/
    /// <see cref="OrdersMirrorAccount.VerifyFailed"/>/<see cref="OrdersMirrorAccount.LastSyncAt"/>/
    /// <see cref="OrdersMirrorAccount.UpdatedAt"/> KHÔNG mang ý nghĩa (để mặc định) — chỉ <c>login</c>, <c>shops</c>
    /// và 3 ô đăng nhập có giá trị. Ba ô gộp theo luật <b>ô nào CÓ CHỮ ĐẦU TIÊN thì thắng</b> (quét theo
    /// <c>machine_id</c> để kết quả ổn định): máy nào đã nhập thì máy khác dùng được, máy chưa nhập không làm
    /// hỏng gì. KHÔNG BAO GIỜ có cookie. Bảng rỗng → list rỗng.
    /// </summary>
    public List<OrdersMirrorAccount> AllOrdersAccountsDistinct()
    {
        lock (_gate)
        {
            // login (thường hóa) → (login hiển thị, shop_login thường hóa → shop). Giữ thứ tự gặp đầu tiên.
            var accs = new Dictionary<string, (string Login, Dictionary<string, OrdersMirrorShop> Shops)>(StringComparer.OrdinalIgnoreCase);
            // login (thường hóa) → 3 ô đăng nhập đã gộp từ MỌI máy.
            var oDangNhap = new Dictionary<string, (string P, string E, string Ep)>(StringComparer.OrdinalIgnoreCase);

            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "SELECT login,password,verify_email,verify_email_password FROM orders_accounts ORDER BY machine_id, login";
                using var rd = c.ExecuteReader();
                while (rd.Read())
                {
                    var login = S(rd, 0).Trim();
                    if (login.Length == 0) continue;
                    if (!accs.ContainsKey(login))
                        accs[login] = (login, new Dictionary<string, OrdersMirrorShop>(StringComparer.OrdinalIgnoreCase));

                    oDangNhap.TryGetValue(login, out var cu);
                    oDangNhap[login] = (
                        OCoChuDauTien(cu.P, S(rd, 1)),
                        OCoChuDauTien(cu.E, S(rd, 2)),
                        OCoChuDauTien(cu.Ep, S(rd, 3)));
                }
            }

            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "SELECT login, shop_login, shop_name FROM orders_account_shops ORDER BY sort_order, shop_login";
                using var rd = c.ExecuteReader();
                while (rd.Read())
                {
                    var login = S(rd, 0).Trim();
                    var shopLogin = S(rd, 1).Trim();
                    var shopName = S(rd, 2).Trim();
                    if (login.Length == 0 || shopLogin.Length == 0) continue;

                    if (!accs.TryGetValue(login, out var entry))
                    {
                        // Shop trỏ tới login chưa có dòng trong orders_accounts (dữ liệu lệch) → vẫn nhận login đó.
                        entry = (login, new Dictionary<string, OrdersMirrorShop>(StringComparer.OrdinalIgnoreCase));
                        accs[login] = entry;
                    }

                    if (!entry.Shops.ContainsKey(shopLogin))
                        entry.Shops[shopLogin] = new OrdersMirrorShop(shopLogin, shopName.Length > 0 ? shopName : shopLogin);
                    else if (shopName.Length > 0 && entry.Shops[shopLogin].Name == entry.Shops[shopLogin].Login)
                        entry.Shops[shopLogin] = new OrdersMirrorShop(shopLogin, shopName); // vá tên khi bản trước chỉ có login
                }
            }

            return accs.Values
                .Select(e =>
                {
                    var o = oDangNhap.TryGetValue(e.Login, out var v) ? v : ("", "", "");
                    return new OrdersMirrorAccount(
                        e.Login, "", false, null, DateTimeOffset.MinValue, e.Shops.Values.ToList())
                    {
                        Password = o.Item1,
                        VerifyEmail = o.Item2,
                        VerifyEmailPassword = o.Item3,
                    };
                })
                .OrderBy(a => a.Login, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>(THUẦN) Luật gộp 3 ô đăng nhập ở chiều <b>gộp nhiều máy</b>: ô đã CÓ CHỮ thì giữ, chưa có thì lấy
    /// của máy kế tiếp. Khác <see cref="GiuLaiNeuRong"/> ở chỗ đây là gộp NGANG giữa các máy (không máy nào ưu
    /// tiên hơn), còn kia là gộp DỌC theo thời gian của cùng một máy (lượt mới thắng lượt cũ).</summary>
    internal static string OCoChuDauTien(string? dangCo, string? themVao)
        => !string.IsNullOrWhiteSpace(dangCo) ? dangCo : (themVao ?? "");

    /// <summary>Số tài khoản / số tài khoản đang chiếm phiên theo TỪNG máy (một lượt quét) — cho thẻ chọn máy ở
    /// tab Đơn hàng, khỏi gọi <see cref="OrdersAccountsOf"/> cho từng máy mỗi nhịp.</summary>
    public Dictionary<string, OrdersMirrorCount> OrdersMirrorCounts()
    {
        lock (_gate)
        {
            var map = new Dictionary<string, OrdersMirrorCount>(StringComparer.Ordinal);
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT machine_id, COUNT(*), SUM(CASE WHEN session_state<>'' THEN 1 ELSE 0 END) FROM orders_accounts GROUP BY machine_id";
            using var rd = c.ExecuteReader();
            while (rd.Read())
                map[S(rd, 0)] = new OrdersMirrorCount(
                    rd.IsDBNull(1) ? 0 : rd.GetInt32(1), rd.IsDBNull(2) ? 0 : rd.GetInt32(2));
            return map;
        }
    }

    /// <summary>Mọi tài khoản Đơn hàng ĐANG CÓ PHIÊN (<c>session_state</c> khác rỗng) của MỌI máy, trong MỘT lượt
    /// quét — cho KPI toàn hệ thống của trang Giao việc (<see cref="OrdersAccountsOf"/> chỉ đọc được một máy).
    /// LEFT JOIN <c>machines</c> để bảng chi tiết nói được "ở máy nào"; máy chưa đẩy gương (client bản cũ) đơn
    /// giản là không có dòng nào ở đây.</summary>
    public List<OrdersRunningAccount> OrdersRunningAccounts()
    {
        lock (_gate)
        {
            var list = new List<OrdersRunningAccount>();
            using var c = _conn.CreateCommand();
            c.CommandText = @"
SELECT a.machine_id, COALESCE(m.hostname,''), a.login, a.session_state, a.updated_at
FROM orders_accounts a LEFT JOIN machines m ON m.machine_id=a.machine_id
WHERE a.session_state<>''
ORDER BY m.hostname COLLATE NOCASE, a.login COLLATE NOCASE";
            using var rd = c.ExecuteReader();
            while (rd.Read())
                list.Add(new OrdersRunningAccount(S(rd, 0), S(rd, 1), S(rd, 2), S(rd, 3), D(rd, 4)));
            return list;
        }
    }

    // ── Lệnh hub → suất đơn hàng ────────────────────────────────────────────────

    /// <summary>
    /// Tạo lệnh cho (máy, tài khoản, hành động). IDEMPOTENT: đã có lệnh CÙNG bộ ba đang 'pending'/'sent' thì
    /// TRẢ LẠI lệnh cũ, không đẻ lệnh thứ hai — operator bấm hai lần (hoặc hai tab cùng bấm) không được biến
    /// thành hai lượt mở trình duyệt. Trả id lệnh; tham số rỗng → chuỗi rỗng.
    /// </summary>
    public string CreateOrdersCommand(string machineId, string login, string action)
    {
        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(action))
            return "";
        lock (_gate)
        {
            SweepOrdersCommandsLocked(DateTimeOffset.UtcNow);
            using (var q = _conn.CreateCommand())
            {
                q.CommandText = "SELECT id FROM orders_commands WHERE machine_id=$m AND login=$l AND action=$a AND status IN ('pending','sent') LIMIT 1";
                q.Parameters.AddWithValue("$m", machineId);
                q.Parameters.AddWithValue("$l", login);
                q.Parameters.AddWithValue("$a", action);
                if (q.ExecuteScalar() is string dup && dup.Length > 0) return dup;
            }

            var id = Guid.NewGuid().ToString("N");
            using var c = _conn.CreateCommand();
            c.CommandText = "INSERT INTO orders_commands(id,machine_id,login,action,status,created_at,ack_at,error) VALUES($id,$m,$l,$a,'pending',$ca,NULL,'');";
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$m", machineId);
            c.Parameters.AddWithValue("$l", login);
            c.Parameters.AddWithValue("$a", action);
            c.Parameters.AddWithValue("$ca", Iso(DateTimeOffset.UtcNow));
            c.ExecuteNonQuery();
            return id;
        }
    }

    /// <summary>Lấy các lệnh 'pending' của một máy để nhét vào phản hồi heartbeat, đồng thời đánh 'sent' (client
    /// tự dedup theo id nên gửi trùng cũng không chạy hai lần). Gọi TRONG lock (<see cref="MachineHeartbeat"/>).</summary>
    private List<OrdersCommandDto> TakeOrdersCommandsLocked(string machineId, DateTimeOffset now)
    {
        SweepOrdersCommandsLocked(now);
        var list = new List<OrdersCommandDto>();
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "SELECT id,login,action FROM orders_commands WHERE machine_id=$m AND status='pending' ORDER BY created_at";
            c.Parameters.AddWithValue("$m", machineId);
            using var rd = c.ExecuteReader();
            while (rd.Read()) list.Add(new OrdersCommandDto(S(rd, 0), S(rd, 1), S(rd, 2)));
        }
        if (list.Count == 0) return list;

        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "UPDATE orders_commands SET status='sent' WHERE machine_id=$m AND status='pending'";
            c.Parameters.AddWithValue("$m", machineId);
            c.ExecuteNonQuery();
        }
        return list;
    }

    /// <summary>Client báo kết quả một lệnh → ghi 'done'/'failed' + lý do. Lệnh đã kết thúc (ack trùng do mạng
    /// lặp) KHÔNG bị ghi đè: chỉ cập nhật dòng còn 'pending'/'sent'.</summary>
    public void AckOrdersCommand(string id, string status, string? error)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var st = status == OrdersCommandStatuses.Done ? OrdersCommandStatuses.Done : OrdersCommandStatuses.Failed;
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "UPDATE orders_commands SET status=$s, ack_at=$at, error=$e WHERE id=$id AND status IN ('pending','sent')";
            c.Parameters.AddWithValue("$s", st);
            c.Parameters.AddWithValue("$at", Iso(DateTimeOffset.UtcNow));
            c.Parameters.AddWithValue("$e", error ?? "");
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>Lệnh gần đây (2 giờ) của một máy — trang điều phối bám trạng thái theo ACK, không lạc quan theo
    /// cú bấm. Quét lệnh mồ côi TRƯỚC khi đọc (máy tắt thì không còn nhịp heartbeat nào để quét).</summary>
    public List<OrdersCommandRow> OrdersCommandsOf(string machineId)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return [];
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            SweepOrdersCommandsLocked(now);
            var list = new List<OrdersCommandRow>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT id,machine_id,login,action,status,created_at,ack_at,error FROM orders_commands WHERE machine_id=$m AND created_at > $cut ORDER BY created_at DESC";
            c.Parameters.AddWithValue("$m", machineId);
            c.Parameters.AddWithValue("$cut", Iso(now - TimeSpan.FromHours(2)));
            using var rd = c.ExecuteReader();
            while (rd.Read())
            {
                var ack = D(rd, 6);
                list.Add(new OrdersCommandRow(
                    S(rd, 0), S(rd, 1), S(rd, 2), S(rd, 3), S(rd, 4), D(rd, 5),
                    ack == DateTimeOffset.MinValue ? null : ack, S(rd, 7)));
            }
            return list;
        }
    }

    /// <summary>Lệnh 'sent'/'pending' quá <see cref="StaleOrdersCommand"/> mà chưa ack → 'failed'. Gọi TRONG lock
    /// từ mọi lối vào bảng lệnh (tạo / lấy ở heartbeat / đọc cho UI) — KHÔNG dựng timer riêng, đúng khuôn
    /// <c>SweepStaleLocked</c> của assignments.</summary>
    private void SweepOrdersCommandsLocked(DateTimeOffset now)
    {
        var cut = Iso(now - StaleOrdersCommand);
        // Kiểm bằng SELECT trước: trang điều phối gọi hàm này mỗi nhịp 2s, chạy thẳng UPDATE là mở một
        // transaction GHI mỗi 2s cho một mệnh đề gần như luôn khớp 0 dòng.
        using (var q = _conn.CreateCommand())
        {
            q.CommandText = "SELECT 1 FROM orders_commands WHERE status IN ('pending','sent') AND created_at < $cut LIMIT 1";
            q.Parameters.AddWithValue("$cut", cut);
            if (q.ExecuteScalar() is null) return;
        }

        using var c = _conn.CreateCommand();
        c.CommandText = "UPDATE orders_commands SET status='failed', ack_at=$at, error=$e WHERE status IN ('pending','sent') AND created_at < $cut";
        c.Parameters.AddWithValue("$at", Iso(now));
        c.Parameters.AddWithValue("$e", "client không phản hồi (máy tắt / bản client cũ chưa nhận lệnh)");
        c.Parameters.AddWithValue("$cut", cut);
        c.ExecuteNonQuery();
    }
}
