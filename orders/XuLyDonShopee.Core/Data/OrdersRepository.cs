using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Core.Data;

/// <summary>
/// DTO một đơn ỨNG VIÊN đẩy lên Google Sheet (đọc từ bảng <c>orders</c> qua
/// <see cref="OrdersRepository.GetForGsheetPush"/> — superset MỌI đơn của tài khoản; việc CHỌN đơn nào gửi
/// do <c>AccountSession</c> quyết bằng C#). <see cref="DaGhiSheet"/> = đã có <c>gsheet_synced_at</c> (đơn
/// từng được ghi dòng); <see cref="FileUrl"/> = <c>gsheet_file_url</c> đã lưu (null nếu chưa upload phiếu);
/// <see cref="GsheetDaHuy"/> = trạng thái hủy ĐÃ ĐẨY lần trước (0/1; null nếu chưa đẩy) — để phát hiện
/// trạng thái hủy thay đổi; <see cref="GsheetDaCoVanDon"/> = lần đẩy gần nhất có gửi mã vận đơn chưa (0/1;
/// null nếu chưa đẩy) — để tự điền cột B khi vận đơn xuất hiện sau; <see cref="FinalAmount"/> =
/// "Số tiền cuối cùng" (<c>final_amount</c>, cột "Ước tính") — SỐ TIỀN đẩy lên sheet (null = chưa mở trang chi
/// tiết) và <see cref="GsheetDaCoUocTinh"/> = lần đẩy gần nhất có gửi kèm số ước tính chưa (0/1; null nếu chưa
/// đẩy) — để tự đẩy lại ghi đè đúng số khi ước tính xuất hiện sau. <see cref="Status"/>/
/// <see cref="StatusDescription"/>/<see cref="CancelReason"/> dùng phân loại hủy (<c>ShopeeShippingNav.LaDonHuy</c>).
/// <see cref="DaDemDaBan"/> = đã đếm "Đã bán" (<c>sold_counted_at IS NOT NULL</c>) và
/// <see cref="DaDayHub"/> = đã đẩy lên hub đơn hàng (<c>hub_synced_at IS NOT NULL</c>) — dùng để QUYẾT ĐỊNH có
/// được DỌN đơn kết thúc khỏi DB chưa (giữ lại đến khi mọi nghĩa vụ hoàn tất — xem <c>OrderPersistPipeline.NenXoaDonKetThuc</c>).
/// <see cref="DaDayPhieuHub"/> = đã đẩy FILE PHIẾU lên hub (<c>hub_slip_synced_at IS NOT NULL</c>) — dùng để GIỮ
/// đơn kết thúc khi còn phiếu local hợp lệ CHƯA đẩy hub (hub đang bật).
/// <see cref="GsheetTab"/> = tab (sheet) đã ghi LẦN ĐẦU của đơn (<c>gsheet_tab</c>; null = chưa ghi/chưa nhớ) —
/// đơn đẩy LẠI phải về đúng tab này (không nhân đôi dòng khi tab đổi theo tháng).
/// <see cref="ItemsJson"/> = mảng sản phẩm đã quét (<c>items_json</c>) — nguồn suy cột "Phân loại" gửi lên sheet
/// (<c>XuLyDonShopee.Core.Services.PhanLoaiExtractor</c>); KHÔNG có cột "phân loại" riêng trong DB.
/// <see cref="ReturnRequestCode"/> = mã yêu cầu trả hàng khớp đơn (<c>return_request_code</c>; null = đơn chưa có
/// yêu cầu trả hàng) và <see cref="GsheetDaCoDonTraHang"/> = lần đẩy gần nhất có gửi kèm mã đó chưa (0/1; null =
/// chưa đẩy) — mẫu y hệt cặp vận đơn, để mã xuất hiện SAU vẫn được đẩy lại điền vào ô.
/// <see cref="ShopLogin"/> = TÊN ĐĂNG NHẬP SHOP của ĐƠN (<c>shop_login</c>; null = đơn cũ chưa gắn shop) — nguồn
/// cột "Shop" (F) trên sheet. Bắt buộc phải per-đơn: đường đẩy BÙ của worker gom MỌI shop của tài khoản trong
/// một lượt nên không có "tên shop của cả lượt" để dùng chung.
/// </summary>
public sealed record GsheetPendingOrder(
    string OrderSn,
    string? TrackingNumber,
    string? Sku,
    string? ItemsJson,
    long? TotalPrice,
    long? FinalAmount,
    string? Status,
    string? StatusDescription,
    string? CancelReason,
    bool DaGhiSheet,
    string? FileUrl,
    long? GsheetDaHuy,
    long? GsheetDaCoVanDon,
    long? GsheetDaCoUocTinh,
    bool DaDemDaBan,
    bool DaDayHub,
    bool DaDayPhieuHub,
    string? GsheetTab,
    string? ReturnRequestCode,
    long? GsheetDaCoDonTraHang,
    string? ShopLogin = null);

/// <summary>
/// Kết quả phát hiện đơn CHUYỂN sang "đã giao" giữa 2 lần sync (<see cref="OrdersRepository.DetectNewlyDelivered"/>),
/// dùng để +1 "Đã bán" theo SKU trên kho hub. Tách 3 nhóm để caller xử đúng thứ tự idempotent:
/// <list type="bullet">
/// <item><see cref="SkusToIncrement"/>: các SKU cần +1 lên hub (mỗi đơn chuyển-sang-đã-giao CÓ SKU đóng góp 1 phần
/// tử; đơn trùng SKU → SKU lặp → +N). Đơn không SKU KHÔNG nằm đây.</item>
/// <item><see cref="PendingMarkOrderSns"/>: các <c>order_sn</c> ứng với <see cref="SkusToIncrement"/> — chỉ đánh cờ
/// <c>sold_counted_at</c> SAU KHI hub +1 OK (kẻo hub lỗi thì mất đếm).</item>
/// <item><see cref="ImmediateMarkOrderSns"/>: các <c>order_sn</c> đánh cờ NGAY (KHÔNG +1) — gồm đơn grandfather
/// (đã-giao-sẵn: mới toanh đã delivered / đơn cũ status đã delivered) VÀ đơn chuyển-sang-đã-giao nhưng KHÔNG có SKU.</item>
/// </list>
/// </summary>
public sealed record SoldTransitionResult(
    IReadOnlyList<string> SkusToIncrement,
    IReadOnlyList<string> PendingMarkOrderSns,
    IReadOnlyList<string> ImmediateMarkOrderSns);

/// <summary>
/// Lưu/đọc đơn hàng đã sync trong bảng <c>orders</c>. Khóa nghiệp vụ là cặp
/// <c>(account_id, order_sn)</c> (UNIQUE) → mỗi đơn của một tài khoản chỉ một dòng; sync lại thì
/// CẬP NHẬT chứ không thêm trùng.
/// </summary>
public partial class OrdersRepository
{
    private readonly Database _db;

    public OrdersRepository(Database db) => _db = db;

    /// <summary>
    /// XÓA các đơn (theo <c>(account_id, order_sn)</c>) khỏi bảng <c>orders</c> trong MỘT transaction. Dùng để
    /// DỌN đơn KẾT THÚC (Đã giao / Đã hủy) khỏi app SAU khi mọi nghĩa vụ hoàn tất (GSheet đã ghi + "Đã bán" đã
    /// đếm + hub đã nhận). Trả về SỐ dòng thực xóa. Danh sách rỗng/null → trả 0 và KHÔNG mở connection. Đơn không
    /// có mã (rỗng) bị bỏ qua.
    /// </summary>
    public int DeleteOrders(long accountId, IReadOnlyCollection<string> orderSns)
    {
        if (orderSns is null || orderSns.Count == 0)
        {
            return 0;
        }

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        var deleted = 0;
        foreach (var sn in orderSns)
        {
            if (string.IsNullOrWhiteSpace(sn))
            {
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM orders WHERE account_id = $a AND order_sn = $sn;";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$sn", sn);
            deleted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return deleted;
    }

    /// <summary>
    /// Đánh dấu một đơn ĐÃ "chuẩn bị hàng" xong (arrange) lúc <paramref name="atUtc"/> — phiên cầu nối gọi ngay
    /// sau mỗi đơn. <c>prepared_at</c> dùng <c>COALESCE(prepared_at, $at)</c>: chỉ ghi LẦN ĐẦU, arrange lại /
    /// chạy lại KHÔNG dời thời điểm sang hôm khác (hub nhóm đếm theo NGÀY). <c>hub_synced_at</c> RESET về NULL để
    /// lượt đẩy hub kế mang <c>prepared_at</c> lên (hub chỉ lấy đơn <c>hub_synced_at IS NULL</c>). Khóa theo
    /// <c>(account_id, order_sn)</c>; mã đơn rỗng → bỏ qua, mã đơn không có trong DB → 0 dòng đổi (KHÔNG ném).
    /// </summary>
    public void MarkPrepared(long accountId, string orderSn, DateTime atUtc)
    {
        if (string.IsNullOrWhiteSpace(orderSn))
        {
            return;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE orders SET
    prepared_at = COALESCE(prepared_at, $at),
    hub_synced_at = NULL,
    hub_push_gen = hub_push_gen + 1
    WHERE account_id = $a AND order_sn = $sn;";
        cmd.Parameters.AddWithValue("$at", DbSerialization.FormatDate(atUtc));
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$sn", orderSn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// <b>ĐẨY LẠI MỘT ĐƠN</b> theo lệnh người dùng (nút "Đẩy lại" trên màn chẩn đoán đơn kẹt): đưa đơn về hàng
    /// chờ của CẢ hai đích để lượt outbox sau gửi lại. Khóa <c>(account_id, order_sn)</c>; trả số dòng đổi
    /// (0 = không có đơn đó). Mã đơn rỗng → 0, KHÔNG chạm DB.
    /// <para><b>Cờ được đặt lại (bộ TỐI THIỂU để hai đích thật sự đẩy lại):</b></para>
    /// <list type="bullet">
    /// <item><c>hub_synced_at = NULL</c> — <c>GetForHubPush</c> chỉ lấy đơn NULL.</item>
    /// <item><c>hub_push_gen + 1</c> — BẮT BUỘC đi kèm: nếu đúng lúc này có một lô đang bay lên hub,
    /// <c>MarkHubSynced</c> sẽ thấy thế hệ đã lệch và KHÔNG đóng cờ oan ngay sau khi ta vừa mở (xem
    /// <see cref="MarkHubSynced"/>).</item>
    /// <item><c>gsheet_synced_at = NULL</c> — <c>CountForGsheetPush</c> đếm theo cột này, mà
    /// <c>HubOutboxWorker</c> CHỈ chạy lượt đẩy sheet khi số đếm &gt; 0; không đặt lại thì lượt sheet không bao
    /// giờ được kích hoạt và mấy cờ dưới đây vô nghĩa.</item>
    /// <item>4 cờ "trạng thái đã đẩy lần trước" <c>gsheet_da_huy</c> / <c>gsheet_da_co_van_don</c> /
    /// <c>gsheet_da_co_uoc_tinh</c> / <c>gsheet_da_co_don_tra_hang</c> = NULL — để nhánh quyết định gửi của
    /// <c>HubOutbox.ConNghiaVuGhiSheet</c> bật lên (mẫu sẵn có của "vận đơn vừa xuất hiện").</item>
    /// </list>
    /// <para><b>TUYỆT ĐỐI KHÔNG đụng</b> (mỗi cột là một lỗi đã từng trả giá):</para>
    /// <list type="bullet">
    /// <item><c>gsheet_tab</c> — đơn đẩy lại phải về ĐÚNG tab cũ; xoá đi là dòng bị ghi lần hai ở tab tháng mới.</item>
    /// <item><c>sold_counted_at</c> — mở lại là +1 "Đã bán" LẦN HAI trên kho hub (sai số liệu, không sửa ngược được).</item>
    /// <item><c>gsheet_file_url</c> — mở lại là upload lại file phiếu đã có link.</item>
    /// <item><c>hub_slip_synced_at</c> — đơn còn nợ phiếu thì cột này VỐN đã NULL; mở lại cho đơn đã đẩy phiếu chỉ
    /// tạo hàng tồn không bao giờ vơi khi file phiếu local đã bị xoá.</item>
    /// <item><c>created_at</c> / <c>prepared_at</c> / <c>return_request_code</c> — dữ liệu, không phải cờ đẩy.</item>
    /// </list>
    /// </summary>
    public int DatLaiCoDayLai(long accountId, string orderSn)
    {
        if (string.IsNullOrWhiteSpace(orderSn))
        {
            return 0;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE orders SET
    hub_synced_at = NULL,
    hub_push_gen = hub_push_gen + 1,
    gsheet_synced_at = NULL,
    gsheet_da_huy = NULL,
    gsheet_da_co_van_don = NULL,
    gsheet_da_co_uoc_tinh = NULL,
    gsheet_da_co_don_tra_hang = NULL
    WHERE account_id = $a AND order_sn = $sn;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$sn", orderSn.Trim());
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Kết quả một lượt <see cref="SetReturnRequestCodes"/> — để log / notify đúng cặp vừa ghi.</summary>
    /// <param name="DaGhi">Số đơn VỪA ghi mã mới (khác mã cũ).</param>
    /// <param name="KhongDoi">Số đơn đã mang đúng mã đó rồi (không ghi lại, không đẩy lại).</param>
    /// <param name="KhongCoDon">Số mã yêu cầu KHÔNG khớp đơn nào trong DB (đơn cũ hơn thời gian giữ / shop khác).</param>
    /// <param name="CapDaGhi">Các cặp (order_sn, mã yêu cầu) vừa ghi thành công — dùng notify, không gửi cả list check.</param>
    public sealed record ReturnCodeSaveResult(
        int DaGhi,
        int KhongDoi,
        int KhongCoDon,
        IReadOnlyList<(string OrderSn, string Code)> CapDaGhi);

    /// <summary>
    /// Lưu MÃ YÊU CẦU TRẢ HÀNG vào đơn theo <c>order_sn</c> (bước check đơn trả hàng, cuối flow shop).
    /// <list type="bullet">
    /// <item>GHI ĐÈ khi mã khác mã đang có (yêu cầu trả hàng có thể được tạo lại), nhưng KHÔNG bao giờ ghi đè bằng
    /// RỖNG — cặp có mã rỗng bị bỏ qua.</item>
    /// <item>Mã KHÔNG đổi → không chạm dòng (khỏi đẩy lại GSheet/hub vô ích).</item>
    /// <item>Mã đơn không có trong DB → đếm vào <see cref="ReturnCodeSaveResult.KhongCoDon"/> để caller LOG; TUYỆT
    /// ĐỐI không tạo đơn mới (đơn đã bị dọn theo vòng đời, insert lại sẽ lặp ghi-xóa).</item>
    /// </list>
    /// Khi mã ĐỔI: <c>hub_synced_at</c> RESET về NULL và <c>gsheet_da_co_don_tra_hang</c> RESET về NULL để lượt đẩy
    /// KẾ mang mã mới lên hub + Google Sheet (đúng cơ chế cờ sẵn có của vận đơn/ước tính, không đẻ cơ chế mới).
    /// Cập nhật nhiều đơn trong một transaction (mẫu <see cref="MarkHubSynced"/>).
    /// </summary>
    public ReturnCodeSaveResult SetReturnRequestCodes(long accountId, IEnumerable<(string OrderSn, string Code)> pairs)
    {
        int daGhi = 0, khongDoi = 0, khongCoDon = 0;
        var capDaGhi = new List<(string OrderSn, string Code)>();
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        foreach (var (sn, code) in pairs ?? Array.Empty<(string, string)>())
        {
            if (string.IsNullOrWhiteSpace(sn) || string.IsNullOrWhiteSpace(code))
            {
                continue; // không có khóa / mã rỗng → bỏ (không ghi đè bằng rỗng)
            }

            var snTrim = sn.Trim();
            var codeTrim = code.Trim();

            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT 1 FROM orders WHERE account_id = $a AND order_sn = $sn;";
                sel.Parameters.AddWithValue("$a", accountId);
                sel.Parameters.AddWithValue("$sn", snTrim);
                if (sel.ExecuteScalar() is null)
                {
                    khongCoDon++;
                    continue;
                }
            }

            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            // WHERE ... <> $code lo luôn phần "chỉ ghi khi KHÁC": 0 dòng đổi = mã cũ đã đúng.
            upd.CommandText = @"UPDATE orders SET
    return_request_code = $code,
    gsheet_da_co_don_tra_hang = NULL,
    hub_synced_at = NULL,
    hub_push_gen = hub_push_gen + 1
    WHERE account_id = $a AND order_sn = $sn AND COALESCE(return_request_code, '') <> $code;";
            upd.Parameters.AddWithValue("$code", codeTrim);
            upd.Parameters.AddWithValue("$a", accountId);
            upd.Parameters.AddWithValue("$sn", snTrim);
            if (upd.ExecuteNonQuery() > 0)
            {
                daGhi++;
                capDaGhi.Add((snTrim, codeTrim));
            }
            else
            {
                khongDoi++;
            }
        }

        tx.Commit();
        return new ReturnCodeSaveResult(daGhi, khongDoi, khongCoDon, capDaGhi);
    }

    /// <summary>
    /// Thời điểm sync gần nhất (<c>MAX(synced_at)</c>, giờ UTC) của TỪNG tài khoản — MỘT query cho cả bảng,
    /// dùng cho gương danh bạ đẩy lên Hub (chạy định kỳ trên MỌI tài khoản nên không hỏi từng tài khoản một).
    /// Tài khoản chưa có đơn nào → KHÔNG có khóa trong map (bên gọi hiểu là "chưa sync lần nào").
    /// </summary>
    public IReadOnlyDictionary<long, DateTime> MaxSyncedAtByAccount()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, MAX(synced_at) FROM orders GROUP BY account_id;";

        var map = new Dictionary<long, DateTime>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }
            // Dòng cũ có thể mang chuỗi ngày lạ (DB chép tay) → bỏ qua dòng đó thay vì ném cả lượt đẩy gương.
            if (DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at))
            {
                map[reader.GetInt64(0)] = at;
            }
        }
        return map;
    }
}
