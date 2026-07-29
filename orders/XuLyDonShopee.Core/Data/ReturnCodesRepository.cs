using Microsoft.Data.Sqlite;

namespace XuLyDonShopee.Core.Data;

/// <summary>
/// Kho MÃ YÊU CẦU TRẢ HÀNG (<c>return_codes</c>) — <b>sống ĐỘC LẬP với vòng đời đơn</b>.
/// <para>
/// Vì sao cần bảng riêng: Shopee cho trả hàng trong 15 ngày, mà app DỌN đơn kết thúc ngay khi đã ghi sheet + đếm
/// + đẩy hub (<c>AccountSession.NenXoaDonKetThuc</c>) — thường trong một hai vòng. Khi yêu cầu trả hàng xuất hiện
/// (nhiều ngày sau) thì <c>OrdersRepository.SetReturnRequestCodes</c> không tìm thấy đơn nữa và VỨT mã đi. Dòng
/// trên Google Sheet thì vẫn còn, và Apps Script tra theo MÃ ĐƠN rồi điền ô trống — nó không cần máy còn giữ đơn.
/// </para>
/// <para>
/// <b>⚠ BẤT BIẾN:</b> KHÔNG khoá ngoại tới <c>orders</c>, KHÔNG xoá theo đơn. Chống phình bằng <see cref="DonDep"/>
/// theo TUỔI bản ghi (<see cref="SoNgayGiuMac"/> ngày) — 90 ngày là hơn 4 lần cửa sổ 20 ngày của bước quét nên
/// không bao giờ dọn nhầm mã còn đang chờ đẩy.
/// </para>
/// <para>
/// <c>OrdersRepository.SetReturnRequestCodes</c> GIỮ NGUYÊN (vẫn ghi vào <c>orders</c> khi đơn còn, cho lưới app
/// + hub) — nay chỉ KHÔNG còn là đường duy nhất; caller ghi vào CẢ hai.
/// </para>
/// </summary>
public class ReturnCodesRepository
{
    /// <summary>Số ngày GIỮ một bản ghi mã trả hàng trước khi <see cref="DonDep"/> xoá (tính từ <c>created_at</c>).
    /// Rộng rãi so với cửa sổ quét 20 ngày: bảng này rất nhẹ (2 chuỗi ngắn/dòng), thà giữ thừa.</summary>
    public const int SoNgayGiuMac = 90;

    private readonly Database _db;

    public ReturnCodesRepository(Database db) => _db = db;

    /// <summary>
    /// UPSERT các cặp <c>(mã đơn, mã yêu cầu)</c> vừa quét được. Cặp thiếu mã đơn / mã yêu cầu → bỏ qua (không ghi
    /// đè bằng rỗng). Ghi trong MỘT transaction; trả về số bản ghi THỰC SỰ thêm mới hoặc đổi mã.
    /// <list type="bullet">
    /// <item>Chưa có → thêm mới, <c>gsheet_synced_at</c> NULL (chờ đẩy).</item>
    /// <item>Đã có, mã KHÔNG đổi → KHÔNG chạm dòng ⇒ giữ nguyên cờ đã đẩy (đừng đẩy trùng).</item>
    /// <item>Đã có, mã ĐỔI (yêu cầu được tạo lại) → ghi mã mới + <b>RESET <c>gsheet_synced_at</c> về NULL</b> để
    /// lượt kế đẩy lại — cùng mẫu <c>SetReturnRequestCodes</c> đang dùng cho <c>hub_synced_at</c>.</item>
    /// </list>
    /// </summary>
    public int LuuMaTraHang(
        long accountId, IEnumerable<(string OrderSn, string Code)> pairs, string? shopLogin, DateTime nowUtc)
    {
        var nowStr = DbSerialization.FormatDate(nowUtc);
        var shop = string.IsNullOrWhiteSpace(shopLogin) ? null : shopLogin.Trim();
        var ghi = 0;

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var (sn, code) in pairs ?? Array.Empty<(string, string)>())
        {
            if (string.IsNullOrWhiteSpace(sn) || string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // Mệnh đề WHERE của DO UPDATE lo luôn phần "chỉ ghi khi KHÁC": mã trùng → 0 dòng đổi, cờ đã-đẩy còn
            // nguyên. shop_login dùng COALESCE($shop, cũ) — lượt sau không biết shop thì đừng xoá cái đã biết.
            cmd.CommandText = @"INSERT INTO return_codes
        (account_id, order_sn, code, shop_login, created_at, gsheet_synced_at)
    VALUES ($a, $sn, $code, $shop, $now, NULL)
    ON CONFLICT(account_id, order_sn) DO UPDATE SET
        code = $code,
        shop_login = COALESCE($shop, return_codes.shop_login),
        gsheet_synced_at = NULL
    WHERE return_codes.code <> $code;";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$sn", sn.Trim());
            cmd.Parameters.AddWithValue("$code", code.Trim());
            cmd.Parameters.AddWithValue("$shop", (object?)shop ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", nowStr);
            ghi += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return ghi;
    }

    /// <summary>
    /// Các mã CHƯA đẩy lên Google Sheet của một tài khoản (<c>gsheet_synced_at IS NULL</c>), cũ trước.
    /// <b>KHÔNG</b> join sang <c>orders</c>: đơn còn hay đã bị dọn đều phải đẩy — đó là toàn bộ mục đích của bảng.
    /// </summary>
    public IReadOnlyList<(string OrderSn, string Code)> LayMaTraHangChuaDay(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT order_sn, code FROM return_codes
    WHERE account_id = $a AND gsheet_synced_at IS NULL
    ORDER BY created_at, order_sn;";
        cmd.Parameters.AddWithValue("$a", accountId);

        var list = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }
            list.Add((reader.GetString(0), reader.GetString(1)));
        }
        return list;
    }

    /// <summary>Số mã CHƯA đẩy (dùng cho badge/log — khỏi nạp cả danh sách).</summary>
    public int DemChuaDay(long accountId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM return_codes WHERE account_id = $a AND gsheet_synced_at IS NULL;";
        cmd.Parameters.AddWithValue("$a", accountId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Đánh dấu các mã ĐÃ đẩy lên Google Sheet lúc <paramref name="luc"/> (một transaction). Danh sách rỗng → 0,
    /// KHÔNG mở kết nối. Mã đơn rỗng bị bỏ qua. Trả về số dòng thực đổi.
    /// </summary>
    public int DanhDauDaDay(long accountId, IReadOnlyCollection<string> orderSns, DateTime luc)
    {
        if (orderSns is null || orderSns.Count == 0)
        {
            return 0;
        }

        var atStr = DbSerialization.FormatDate(luc);
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        var n = 0;
        foreach (var sn in orderSns)
        {
            if (string.IsNullOrWhiteSpace(sn))
            {
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE return_codes SET gsheet_synced_at = $at WHERE account_id = $a AND order_sn = $sn;";
            cmd.Parameters.AddWithValue("$at", atStr);
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$sn", sn.Trim());
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    /// <summary>
    /// DỌN các bản ghi tạo TRƯỚC <paramref name="truocNgayUtc"/> — chống phình vô hạn mà KHÔNG dính vào vòng đời
    /// đơn (xem bất biến ở doc lớp). Trả về số dòng đã xoá.
    /// <para>Xoá theo <c>created_at</c> chứ không theo <c>gsheet_synced_at</c>: mã quá cũ mà vẫn chưa đẩy được thì
    /// dòng trên sheet cũng không còn để điền — giữ lại chỉ tổ thử lại mãi.</para>
    /// </summary>
    public int DonDep(DateTime truocNgayUtc)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM return_codes WHERE created_at < $moc;";
        cmd.Parameters.AddWithValue("$moc", DbSerialization.FormatDate(truocNgayUtc));
        return cmd.ExecuteNonQuery();
    }
}
