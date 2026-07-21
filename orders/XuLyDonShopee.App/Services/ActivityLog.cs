using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Một dòng nhật ký hoạt động: thời điểm + nguồn (nhãn tài khoản/hệ thống) + nội dung.
/// </summary>
/// <param name="Time">Thời điểm ghi (giờ máy).</param>
/// <param name="Source">Nguồn phát log (thường là email/nhãn tài khoản của phiên).</param>
/// <param name="Message">Nội dung thông báo.</param>
public record LogEntry(DateTime Time, string Source, string Message)
{
    /// <summary>Một dòng hiển thị/ghi file: <c>HH:mm:ss [Source] Message</c>.</summary>
    public string Display => ActivityLog.FormatLine(this);
}

/// <summary>
/// Service thu thập nhật ký hoạt động của app: vừa đổ vào một <see cref="ObservableCollection{T}"/> để
/// panel UI hiển thị (ring-buffer cap tránh phình bộ nhớ khi chạy lâu), vừa ghi NGAY ra file trên đĩa
/// (xoay theo ngày) để xem lại sau khi đóng app.
/// <para>
/// <b>An toàn đa luồng:</b> <see cref="Append"/> có thể gọi từ nhiều phiên nền song song. Phần ghi file
/// nằm dưới <c>lock</c>; phần đụng <see cref="Entries"/> (ObservableCollection — chỉ an toàn trên UI thread)
/// được marshal qua <c>uiPost</c> nên luôn chạy trên UI thread. Lỗi ghi file bị NUỐT để không phá app.
/// </para>
/// </summary>
public sealed class ActivityLog
{
    private readonly string _logDir;
    private readonly Action<Action> _uiPost;
    private readonly int _maxEntries;
    private readonly object _fileLock = new();

    /// <summary>
    /// Khởi tạo service log.
    /// </summary>
    /// <param name="logDir">Thư mục chứa file log (đã được người gọi tạo sẵn).</param>
    /// <param name="uiPost">Cách marshal một hành động về UI thread. Null → dùng
    /// <c>Dispatcher.UIThread.Post</c>. Test truyền <c>a =&gt; a()</c> để chạy đồng bộ (không cần dispatcher).</param>
    /// <param name="maxEntries">Số dòng tối đa giữ trong <see cref="Entries"/> (ring-buffer).</param>
    public ActivityLog(string logDir, Action<Action>? uiPost = null, int maxEntries = 500)
    {
        _logDir = logDir;
        _uiPost = uiPost ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(() => a()));
        _maxEntries = maxEntries;
    }

    /// <summary>Các dòng log đang hiển thị. CHỈ được đọc/ghi trên UI thread (mutate qua <c>uiPost</c>).</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>Đường dẫn file log của HÔM NAY (để UI hiển thị "file log ở đâu").</summary>
    public string CurrentLogPath => Path.Combine(_logDir, $"hoatdong-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Định dạng một dòng log: <c>HH:mm:ss [Source] Message</c> (hàm thuần để test).</summary>
    public static string FormatLine(LogEntry e) => $"{e.Time:HH:mm:ss} [{e.Source}] {e.Message}";

    /// <summary>
    /// Thêm một dòng log: ghi NGAY ra file (đồng bộ, dưới lock, nuốt lỗi IO) rồi đổ vào
    /// <see cref="Entries"/> qua <c>uiPost</c> (thêm cuối, cắt bớt đầu nếu vượt cap).
    /// </summary>
    public void Append(string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, source, message);

        // 1) Ghi file NGAY (đồng bộ). Nuốt mọi lỗi IO để không phá app.
        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(CurrentLogPath, FormatLine(entry) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Lỗi ghi file (ổ đầy / quyền / file bị khóa...) → bỏ qua, không phá luồng.
        }

        // 2) Đổ vào Entries qua uiPost (ObservableCollection chỉ an toàn trên UI thread).
        _uiPost(() =>
        {
            Entries.Add(entry);
            while (Entries.Count > _maxEntries)
            {
                Entries.RemoveAt(0);
            }
        });
    }

    /// <summary>Xóa các dòng đang hiển thị (qua <c>uiPost</c>). KHÔNG xóa file log trên đĩa.</summary>
    public void Clear() => _uiPost(() => Entries.Clear());

    /// <summary>
    /// Xóa các dòng đang hiển thị của RIÊNG một <paramref name="source"/> (qua <c>uiPost</c>) — dùng khi người
    /// dùng chỉ muốn dọn log của tài khoản đang chọn. Duyệt NGƯỢC chỉ số để remove an toàn. KHÔNG xóa file log
    /// trên đĩa (giống <see cref="Clear()"/>).
    /// </summary>
    public void Clear(string source) => _uiPost(() =>
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            if (Entries[i].Source == source)
            {
                Entries.RemoveAt(i);
            }
        }
    });
}
