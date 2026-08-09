using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Cấp một file SQLite tạm cho test, tự dọn khi Dispose.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    public string Path { get; }

    public TempDatabase()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"xlds_test_{Guid.NewGuid():N}.db");
    }

    /// <summary>Tạo một instance Database mới trỏ vào cùng file (mô phỏng đóng/mở lại).</summary>
    public Database Open() => new(Path);

    /// <summary>
    /// Tạo một tài khoản mang <paramref name="id"/> <b>CHỈ ĐỊNH</b> và trả về chính id đó.
    /// <para>
    /// <b>Vì sao không <c>Accounts.Insert</c> thẳng:</b> DB tạm nào cũng rỗng nên lần Insert đầu LUÔN cấp
    /// <c>Id = 1</c>. Mà <c>PushGate</c> là chốt TOÀN TIẾN TRÌNH khoá theo <c>(accountId, kind)</c>, còn xUnit
    /// chạy các LỚP test SONG SONG ⇒ hai lớp cùng chạy lượt đẩy cho "tài khoản 1" sẽ giành gate của nhau: lớp
    /// thua nhận <c>ChayQuaGateAsync → null</c> rồi đổ ở chỗ không ai ngờ. Đã dính thật:
    /// <c>NotifyDonTraKhoMaTests.BadgeChoDay_DemCaMaTraHangConTon</c> đỏ lác đác (~1 lần / 10–14 lượt
    /// full-suite) với <c>Assert.False() Failure — Actual: null</c>; xem
    /// <c>plans/2026-08-07-sua-test-flaky-badge-cho-day.md</c>.
    /// </para>
    /// <para>
    /// <b>LUẬT:</b> lớp test nào chạy qua <c>PushGate</c> (<c>HubOutboxWorker.MotLuotAsync</c>,
    /// <c>OrderPersistPipeline.PersistSyncedOrdersAsync</c>) phải dùng một id RIÊNG. Các id đang dùng:
    /// <c>4001</c> <c>OrderPersistPipelineTests</c> · <c>4101</c> <c>NotifyDonTraKhoMaTests</c> ·
    /// <c>4201</c> <c>HubOutboxWorkerRoundTests</c> · <c>900_10x</c> <c>PushGateTests</c>.
    /// </para>
    /// Gọi NGAY sau khi dựng bộ dịch vụ, TRƯỚC khi ghi đơn / mã trả hàng: các bảng khác giữ <c>account_id</c>
    /// dạng số RỜI (không khoá ngoại) nên đổi id sau sẽ bỏ mồ côi đúng phần dữ liệu test vừa dựng.
    /// </summary>
    public static long ThemTaiKhoanIdRieng<TLopTest>(Database db, long id, string email = "shop-test@example.com")
    {
        // CHỐT chống chép-dán: lớp test mới chép `Dung()` của lớp khác sẽ mang theo luôn hằng id của lớp đó và
        // dựng lại ĐÚNG cuộc đua vừa vá — mà biểu hiện lại là "đỏ 1 lần / 10 lượt", tốn thêm một đợt điều tra.
        // Ở đây id bị hai LỚP khác nhau đòi là đổ NGAY, tất định, kèm tên cả hai bên.
        // Khoá sổ theo TYPE chứ không theo file gọi ([CallerFilePath]): đơn vị chạy song song của xUnit là LỚP,
        // và bản khoá-theo-file sẽ MÙ ngay khi ai đó gom các hàm `Dung()` vào một file helper dùng chung —
        // đúng kiểu dọn dẹp mà người sau thấy là cải thiện, trong khi nó tắt chốt này không một tiếng động.
        var chuMoi = typeof(TLopTest).FullName ?? typeof(TLopTest).Name;
        var chuCu = ChuIdTaiKhoan.GetOrAdd(id, chuMoi);
        if (!string.Equals(chuCu, chuMoi, StringComparison.Ordinal))
        {
            // Bên bị NÊU TÊN là bên đăng ký sau — đọc là "hai lớp này trùng id", đừng đọc là "lớp này sai".
            throw new InvalidOperationException(
                $"Id tài khoản {id} đã thuộc về {chuCu}; {chuMoi} phải chọn id KHÁC "
                + "(xem xmldoc TempDatabase.ThemTaiKhoanIdRieng).");
        }

        var cu = new AccountRepository(db).Insert(new Account { Email = email });
        if (cu == id)
        {
            return id;
        }

        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET Id = $moi WHERE Id = $cu;";
        cmd.Parameters.AddWithValue("$moi", id);
        cmd.Parameters.AddWithValue("$cu", cu);
        if (cmd.ExecuteNonQuery() != 1)
        {
            // Im lặng ở đây = test chạy trên id KHÁC id nó tưởng ⇒ đua lại như cũ. Thà đổ ngay.
            // (Ca "id đích đã có" thì SQLite ném PRIMARY KEY ngay ở ExecuteNonQuery, không tới được đây.)
            throw new InvalidOperationException($"Không đổi được Id tài khoản {cu} → {id}: UPDATE khớp 0 dòng.");
        }
        return id;
    }

    /// <summary>
    /// LỚP test nào đang giữ id nào (khoá = id). Chỉ để chốt chống chép-dán ở
    /// <see cref="ThemTaiKhoanIdRieng{TLopTest}"/> — hai LỚP khác nhau cùng đòi một id là lỗi cấu hình test,
    /// không phải đua.
    /// <para>
    /// Gieo sẵn <c>4001</c>: <c>OrderPersistPipelineTests</c> KHÔNG đi qua helper này (nó chỉ truyền số vào
    /// constructor <c>OrderPersistPipeline</c>, không cần bản ghi tài khoản) nhưng vẫn chiếm
    /// <c>PushGate(4001, …)</c> thật qua <c>PersistSyncedOrdersAsync</c> — không gieo thì một lớp mới xin
    /// <c>4001</c> sẽ được cấp bình thường rồi đua thật. (<c>PushGateTests</c> dùng dải <c>900_10x</c> và cũng
    /// không qua helper, nhưng dải đó đặt xa hẳn nên để nguyên.)
    /// </para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> ChuIdTaiKhoan = new(
        new[] { KeyValuePair.Create(4001L, "XuLyDonShopee.Tests.OrderPersistPipelineTests (không qua helper)") });

    public void Dispose()
    {
        // Nhả pool để giải phóng file handle trước khi xóa file — CHỈ pool của ĐÚNG file này.
        // KHÔNG dùng ClearAllPools(): nó chốt TOÀN TIẾN TRÌNH, mà xUnit chạy các lớp test SONG SONG ⇒ lớp này
        // Dispose là đóng luôn connection đang mở của lớp khác → ObjectDisposedException lác đác, không tái hiện
        // ổn định. Pool của Microsoft.Data.Sqlite khóa theo CHUỖI KẾT NỐI nên dựng lại đúng chuỗi mà Database
        // dùng (SqliteConnectionStringBuilder{DataSource=Path}) là trúng pool cần nhả; connection này không cần Open.
        using (var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path }.ToString()))
        {
            SqliteConnection.ClearPool(conn);
        }

        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch
        {
            // Bỏ qua lỗi dọn file tạm.
        }
    }
}
