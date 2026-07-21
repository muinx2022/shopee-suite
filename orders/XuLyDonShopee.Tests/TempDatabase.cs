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
        // Xóa pool để giải phóng file handle trước khi xóa file.
        SqliteConnection.ClearAllPools();
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
