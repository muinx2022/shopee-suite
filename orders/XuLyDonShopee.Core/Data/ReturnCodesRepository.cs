using Microsoft.Data.Sqlite;

namespace XuLyDonShopee.Core.Data;

/// <summary>
/// Kho MÃ YÊU CẦU TRẢ HÀNG (<c>return_codes</c>) — <b>sống ĐỘC LẬP với vòng đời đơn</b>.
/// <para>
/// Vì sao cần bảng riêng: Shopee cho trả hàng trong 15 ngày, mà app DỌN đơn kết thúc ngay khi đã ghi sheet + đếm
/// + đẩy hub (<c>OrderPersistPipeline.NenXoaDonKetThuc</c>) — thường trong một hai vòng. Khi yêu cầu trả hàng xuất hiện
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
/// <summary>Kết quả một lượt <see cref="ReturnCodesRepository.LuuMaTraHang"/>.</summary>
/// <param name="DaGhi">Số bản ghi THỰC SỰ thêm mới hoặc đổi mã (= <see cref="CapMoi"/>.Count).</param>
/// <param name="CapMoi">Đúng các cặp vừa thêm/đổi — nguồn DUY NHẤT đúng để notify "có đơn trả hàng": phần lớn mã
/// trả thuộc đơn ĐÃ bị dọn khỏi <c>orders</c> nên <c>OrdersRepository.ReturnCodeSaveResult.CapDaGhi</c> không có.</param>
public sealed record KetQuaLuuMaTraHang(int DaGhi, IReadOnlyList<(string OrderSn, string Code)> CapMoi);

public class ReturnCodesRepository
{
    /// <summary>Số ngày GIỮ một bản ghi mã trả hàng trước khi <see cref="DonDep"/> xoá (tính từ <c>created_at</c>).
    /// Rộng rãi so với cửa sổ quét 20 ngày: bảng này rất nhẹ (2 chuỗi ngắn/dòng), thà giữ thừa.</summary>
    public const int SoNgayGiuMac = 90;

    /// <summary>
    /// Số ngày còn THỬ LẠI đẩy một mã lên Google Sheet (tính từ <c>created_at</c>). Quá hạn thì mã nằm im chờ
    /// <see cref="DonDep"/> dọn — không đẩy nữa, không đếm vào badge "chờ đẩy".
    /// <para>
    /// Có hằng này vì từ 09/08/2026 mã bị Apps Script BỎ QUA (không tra thấy mã đơn ở tab nào, hoặc thiếu tiêu
    /// đề cột) KHÔNG còn bị đánh dấu "đã đẩy" — nếu không có trần thì mã của đơn chưa từng lên sheet sẽ được thử
    /// lại mỗi lượt cho tới khi <see cref="SoNgayGiuMac"/> dọn, tức 90 ngày gõ cửa Apps Script vô ích.
    /// </para>
    /// <para>14 ngày = quá đủ cho hai ca thật cần thử lại: dòng đơn lên sheet ở lượt sau, và người dùng sửa lại
    /// tiêu đề cột sau khi thấy cảnh báo trong nhật ký.</para>
    /// </summary>
    public const int SoNgayThuLaiSheet = 14;

    /// <summary>Điều kiện SQL "còn trong hạn thử lại" — dùng CHUNG cho <see cref="LayMaTraHangChuaDay"/> và
    /// <see cref="DemChuaDay"/> để danh sách đẩy và con số trên badge không bao giờ lệch nhau.</summary>
    private const string DieuKienChuaDay =
        "account_id = $a AND gsheet_synced_at IS NULL AND created_at >= $hanThuLai";

    private readonly Database _db;

    public ReturnCodesRepository(Database db) => _db = db;

    /// <summary>
    /// UPSERT các cặp <c>(mã đơn, mã yêu cầu)</c> vừa quét được. Cặp thiếu mã đơn / mã yêu cầu → bỏ qua (không ghi
    /// đè bằng rỗng). Ghi trong MỘT transaction; trả về số bản ghi THỰC SỰ thêm mới hoặc đổi mã KÈM chính các cặp
    /// đó (<see cref="KetQuaLuuMaTraHang.CapMoi"/> — caller notify đúng phần mới, không báo lại cả lô quét).
    /// <list type="bullet">
    /// <item>Chưa có → thêm mới, <c>gsheet_synced_at</c> NULL (chờ đẩy).</item>
    /// <item>Đã có, mã KHÔNG đổi → KHÔNG chạm dòng ⇒ giữ nguyên cờ đã đẩy (đừng đẩy trùng).</item>
    /// <item>Đã có, mã ĐỔI (yêu cầu được tạo lại) → ghi mã mới + <b>RESET <c>gsheet_synced_at</c> về NULL</b> để
    /// lượt kế đẩy lại — cùng mẫu <c>SetReturnRequestCodes</c> đang dùng cho <c>hub_synced_at</c> — <b>VÀ làm mới
    /// <c>created_at</c></b>: mã mới là một nghĩa vụ đẩy MỚI, phải được tính hạn thử lại
    /// (<see cref="SoNgayThuLaiSheet"/>) từ lúc này. Giữ <c>created_at</c> cũ thì bản ghi quá 14 ngày được mở cờ
    /// rồi rơi thẳng khỏi <see cref="LayMaTraHangChuaDay"/> — cờ mở ra mà không ai đẩy, không log, không badge.</item>
    /// </list>
    /// </summary>
    public KetQuaLuuMaTraHang LuuMaTraHang(
        long accountId, IEnumerable<(string OrderSn, string Code)> pairs, string? shopLogin, DateTime nowUtc)
    {
        var nowStr = DbSerialization.FormatDate(nowUtc);
        var shop = string.IsNullOrWhiteSpace(shopLogin) ? null : shopLogin.Trim();
        var capMoi = new List<(string OrderSn, string Code)>();

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
        gsheet_synced_at = NULL,
        created_at = $now
    WHERE return_codes.code <> $code;";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$sn", sn.Trim());
            cmd.Parameters.AddWithValue("$code", code.Trim());
            cmd.Parameters.AddWithValue("$shop", (object?)shop ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", nowStr);
            if (cmd.ExecuteNonQuery() > 0)
            {
                capMoi.Add((sn.Trim(), code.Trim()));
            }
        }
        tx.Commit();
        return new KetQuaLuuMaTraHang(capMoi.Count, capMoi);
    }

    /// <summary>
    /// Các mã CHƯA đẩy lên Google Sheet của một tài khoản (<c>gsheet_synced_at IS NULL</c>) và còn trong hạn
    /// thử lại <see cref="SoNgayThuLaiSheet"/> ngày, cũ trước.
    /// <b>KHÔNG</b> join sang <c>orders</c>: đơn còn hay đã bị dọn đều phải đẩy — đó là toàn bộ mục đích của bảng.
    /// </summary>
    /// <param name="nowUtc">Mốc "bây giờ" để tính hạn thử lại — null = <see cref="DateTime.UtcNow"/>. Tham số
    /// hoá chỉ để test được, caller thật KHÔNG truyền.</param>
    public IReadOnlyList<(string OrderSn, string Code)> LayMaTraHangChuaDay(long accountId, DateTime? nowUtc = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT order_sn, code FROM return_codes
    WHERE {DieuKienChuaDay}
    ORDER BY created_at, order_sn;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$hanThuLai", HanThuLai(nowUtc));

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

    /// <summary>
    /// Trong <paramref name="cap"/>, có bao nhiêu cặp là MỚI với kho — chưa có <c>order_sn</c> đó, hoặc có rồi
    /// nhưng mã KHÁC (yêu cầu bị tạo lại). Đây là tín hiệu duy nhất đáng tin để quyết định <b>còn lật trang trả
    /// hàng nữa hay không</b>: trang vừa đọc còn ra mã mới ⇒ nhiều khả năng trang sau cũng còn.
    /// <para>
    /// <b>Vì sao KHÔNG dùng mốc "số yêu cầu" để quyết định độ sâu</b> (bản 09/08 đầu tiên làm vậy và hỏng): mốc
    /// chỉ null ở lần check ĐẦU TIÊN của mỗi shop. Shop đã chạy từ trước thì mốc luôn khác null, nên luật
    /// "lần đầu thì quét sâu" không bao giờ chạm tới đúng nhóm shop đang tồn đọng — tức nhóm cần quét sâu nhất.
    /// Đếm mã mới thì không phụ thuộc mốc, tự dừng khi hết cái mới, và tự chạy lại được nếu lượt trước gãy giữa chừng.
    /// </para>
    /// <para>KHÔNG ghi gì — thuần đọc. Danh sách rỗng → 0, không mở kết nối.</para>
    /// </summary>
    public int DemMaChuaBiet(long accountId, IReadOnlyList<(string OrderSn, string Code)> cap)
    {
        if (cap is null || cap.Count == 0)
        {
            return 0;
        }

        using var conn = _db.OpenConnection();
        var moi = 0;
        foreach (var (sn, code) in cap)
        {
            if (string.IsNullOrWhiteSpace(sn) || string.IsNullOrWhiteSpace(code))
            {
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT code FROM return_codes WHERE account_id = $a AND order_sn = $sn;";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$sn", sn.Trim());
            var cu = cmd.ExecuteScalar() as string;
            if (cu is null || !string.Equals(cu, code.Trim(), StringComparison.Ordinal))
            {
                moi++;
            }
        }
        return moi;
    }

    /// <summary>Số mã CHƯA đẩy mà CÒN trong hạn thử lại (dùng cho badge/log — khỏi nạp cả danh sách). Cùng điều
    /// kiện với <see cref="LayMaTraHangChuaDay"/>: badge đếm đúng cái sẽ được đẩy, không đếm mã đã hết hạn.</summary>
    public int DemChuaDay(long accountId, DateTime? nowUtc = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM return_codes WHERE {DieuKienChuaDay};";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$hanThuLai", HanThuLai(nowUtc));
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Số mã CHƯA đẩy được mà đã QUÁ HẠN thử lại (<see cref="SoNgayThuLaiSheet"/> ngày) — app thôi không đẩy
    /// nữa, bản ghi nằm im chờ <see cref="DonDep"/> dọn ở <see cref="SoNgayGiuMac"/> ngày.
    /// <para>
    /// Có hàm này CHỈ để LOG. Không có nó thì nhóm mã đó biến khỏi badge lẫn khỏi mọi dòng nhật ký đúng vào lúc
    /// bị bỏ — người dùng nhìn ô "Mã đơn trả hàng" trống trên sheet mà không có cách nào biết app đã thôi thử.
    /// Đó là kiểu hỏng IM LẶNG mà cả đợt 09/08 đi vá; đừng để nó quay lại bằng chính bản vá.
    /// </para>
    /// </summary>
    public int DemQuaHanThuLai(long accountId, DateTime? nowUtc = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM return_codes "
            + "WHERE account_id = $a AND gsheet_synced_at IS NULL AND created_at < $hanThuLai;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$hanThuLai", HanThuLai(nowUtc));
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>Mốc <c>created_at</c> cũ nhất còn được thử đẩy lại, đã format đúng khuôn ngày trong DB.</summary>
    private static string HanThuLai(DateTime? nowUtc)
        => DbSerialization.FormatDate((nowUtc ?? DateTime.UtcNow).AddDays(-SoNgayThuLaiSheet));

    /// <summary>
    /// Đánh dấu các CẶP <c>(mã đơn, mã yêu cầu)</c> ĐÃ đẩy lên Google Sheet lúc <paramref name="luc"/> (một
    /// transaction). Danh sách rỗng → 0, KHÔNG mở kết nối. Cặp thiếu mã đơn / thiếu mã bị bỏ qua. Trả về số dòng
    /// thực đổi.
    /// <para>
    /// <b>⚠ BẤT BIẾN: so CẢ <c>code</c>, không chỉ mã đơn.</b> Một lô sheet bay khá lâu (nhiều nhóm tab, mỗi lượt
    /// POST tới 120s) mà bước check shop chạy SONG SONG có thể ghi mã MỚI cho đúng đơn đó —
    /// <see cref="LuuMaTraHang"/> khi mã ĐỔI sẽ reset <c>gsheet_synced_at</c> về NULL, tức mở một nghĩa vụ đẩy MỚI.
    /// Đóng cờ theo mã đơn TRẦN là đóng đè lên nghĩa vụ vừa mở ⇒ mã mới không bao giờ lên sheet, không log,
    /// không badge — đúng lớp lỗi "mất dữ liệu âm thầm" mà bảng này sinh ra để chặn.
    /// </para>
    /// <para>
    /// <b>Cặp phải lấy từ chính payload ĐÃ GỬI</b> (danh sách <c>GsheetReturnCodeRow</c> của lô), TUYỆT ĐỐI không
    /// đọc lại DB tại chỗ đánh dấu: đọc lại là lấy đúng mã MỚI rồi tự tay đóng cờ của nó.
    /// </para>
    /// </summary>
    public int DanhDauDaDay(long accountId, IReadOnlyCollection<(string OrderSn, string Code)> cap, DateTime luc)
    {
        if (cap is null || cap.Count == 0)
        {
            return 0;
        }

        var atStr = DbSerialization.FormatDate(luc);
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        var n = 0;
        foreach (var (sn, code) in cap)
        {
            if (string.IsNullOrWhiteSpace(sn) || string.IsNullOrWhiteSpace(code))
            {
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE return_codes SET gsheet_synced_at = $at "
                + "WHERE account_id = $a AND order_sn = $sn AND code = $code;";
            cmd.Parameters.AddWithValue("$at", atStr);
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$sn", sn.Trim());
            cmd.Parameters.AddWithValue("$code", code.Trim());
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
