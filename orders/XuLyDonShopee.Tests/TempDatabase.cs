using Microsoft.Data.Sqlite;
using XuLyDonShopee.Core.Data;

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
