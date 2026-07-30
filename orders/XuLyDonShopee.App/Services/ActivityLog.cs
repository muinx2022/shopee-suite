using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Service thu thập nhật ký hoạt động của app. Hai đường đi HOÀN TOÀN tách nhau:
/// <list type="number">
/// <item><b>Ra đĩa (đủ, không cắt):</b> <see cref="Append"/> chỉ ĐẨY dòng vào hàng đợi rồi trả về ngay —
/// KHÔNG chạm đĩa trên luồng gọi. Một <see cref="System.Threading.Timer"/> nền gom hàng đợi ghi MỘT lần
/// mỗi <see cref="FlushEvery"/>, tự xoay file khi vượt <see cref="MaxBytes"/>.</item>
/// <item><b>Lên UI (cắt, chỉ log mới):</b> mỗi nguồn giữ RIÊNG một ring-buffer
/// <see cref="MaxLinesPerSource"/> dòng gần nhất — tài khoản chạy ồn KHÔNG đẩy văng log của tài khoản
/// đang xem. Panel lấy log bằng <see cref="Snapshot"/> khi nghe <see cref="SourceUpdated"/>; sự kiện đó
/// gom nhóm, bắn tối đa 1 lần mỗi <see cref="NotifyEvery"/> nên log dồn dập cũng không làm UI dựng lại
/// chuỗi liên tục.</item>
/// </list>
/// <para>
/// <b>An toàn đa luồng:</b> <see cref="Append"/> gọi được từ mọi luồng (hàng đợi lock-free + buffer dưới
/// <c>lock</c> riêng, KHÔNG dùng chung khóa với khóa ghi file). <see cref="SourceUpdated"/> luôn nổ trên
/// UI thread (qua <c>uiPost</c>). Lỗi ghi file bị NUỐT để không phá app.
/// </para>
/// </summary>
public sealed class ActivityLog : IDisposable
{
    /// <summary>Số dòng tối đa giữ để HIỂN THỊ cho MỖI nguồn (file trên đĩa vẫn đủ dòng).</summary>
    public const int MaxLinesPerSource = 200;

    /// <summary>Nhịp đẩy hàng đợi ra file (gom nhiều dòng thành 1 lần ghi).</summary>
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(1);

    /// <summary>Nhịp gom báo UI: log dội cỡ nào cũng chỉ dựng lại panel ngần này một lần.</summary>
    private static readonly TimeSpan NotifyEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>Vượt cỡ này thì xoay file (giữ 1 bản <c>.1</c> rồi ghi tiếp vào file mới).</summary>
    private const long MaxBytes = 8 * 1024 * 1024;

    private readonly string _logDir;
    private readonly Action<Action> _uiPost;
    private readonly int _maxLinesPerSource;

    /// <summary>Buffer hiển thị RIÊNG từng nguồn (khóa = source, không phân biệt hoa/thường vì nguồn là email).</summary>
    private readonly Dictionary<string, Queue<LogEntry>> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _bufLock = new();

    /// <summary>Các dòng đang chờ ghi ra đĩa; timer nền rút hết mỗi nhịp.</summary>
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly object _fileLock = new();
    private readonly System.Threading.Timer _flushTimer;

    /// <summary>Các nguồn vừa có log mới, chờ báo UI một lượt gộp.</summary>
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _notifyLock = new();
    private bool _notifyScheduled;
    private readonly System.Threading.Timer _notifyTimer;

    /// <summary>
    /// Khởi tạo service log.
    /// </summary>
    /// <param name="logDir">Thư mục chứa file log (đã được người gọi tạo sẵn).</param>
    /// <param name="uiPost">Cách marshal một hành động về UI thread. Null → dùng
    /// <c>Dispatcher.UIThread.Post</c>. Test truyền <c>a =&gt; a()</c> để chạy đồng bộ (không cần dispatcher).</param>
    /// <param name="maxLinesPerSource">Số dòng tối đa giữ để hiển thị cho MỖI nguồn (ring-buffer).</param>
    public ActivityLog(string logDir, Action<Action>? uiPost = null, int maxLinesPerSource = MaxLinesPerSource)
    {
        _logDir = logDir;
        _uiPost = uiPost ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(() => a()));
        _maxLinesPerSource = Math.Max(1, maxLinesPerSource);

        // Ghi file ở luồng nền, gom theo nhịp — best-effort, không bao giờ được ném ra khỏi callback.
        _flushTimer = new System.Threading.Timer(_ => SafeFlush(), null, FlushEvery, FlushEvery);
        // Timer báo UI chạy MỘT NHỊP mỗi lần được hẹn (xem ScheduleNotify), không lặp.
        _notifyTimer = new System.Threading.Timer(
            _ => SafeFireNotify(), null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Một hoặc nhiều nguồn vừa có log mới (tham số là <c>source</c>). LUÔN nổ trên UI thread và ĐÃ GOM NHÓM
    /// (tối đa 1 lần mỗi <see cref="NotifyEvery"/> cho mỗi nguồn) — người nghe cứ dựng lại panel từ
    /// <see cref="Snapshot"/>, không cần tự chống dội.
    /// </summary>
    public event Action<string>? SourceUpdated;

    /// <summary>Đường dẫn file log của HÔM NAY (để UI hiển thị "file log ở đâu").</summary>
    public string CurrentLogPath => Path.Combine(_logDir, $"hoatdong-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Định dạng một dòng log: <c>HH:mm:ss [Source] Message</c> (hàm thuần để test).</summary>
    public static string FormatLine(LogEntry e) => $"{e.Time:HH:mm:ss} [{e.Source}] {e.Message}";

    /// <summary>
    /// Thêm một dòng log. CHỈ làm việc trong bộ nhớ: đẩy dòng vào hàng đợi ghi file + nhét vào ring-buffer của
    /// nguồn + hẹn một lượt báo UI. KHÔNG chạm đĩa, KHÔNG chờ UI thread → gọi được từ vòng lặp nóng của phiên.
    /// </summary>
    public void Append(string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, source ?? string.Empty, message);

        // 1) Ra đĩa: chỉ xếp hàng (lock-free). Timer nền lo phần I/O.
        _pending.Enqueue(FormatLine(entry));

        // 2) Lên UI: ring-buffer RIÊNG của nguồn này, cắt dòng cũ nhất khi vượt trần.
        lock (_bufLock)
        {
            if (!_buffers.TryGetValue(entry.Source, out var buf))
            {
                buf = new Queue<LogEntry>();
                _buffers[entry.Source] = buf;
            }

            buf.Enqueue(entry);
            while (buf.Count > _maxLinesPerSource)
            {
                buf.Dequeue();
            }
        }

        // 3) Hẹn báo UI (gộp — dòng tới trong lúc chờ không hẹn thêm lượt nào).
        ScheduleNotify(entry.Source);
    }

    /// <summary>
    /// Bản SAO các dòng đang giữ để hiển thị của <paramref name="source"/> (cũ → mới), tối đa
    /// <see cref="MaxLinesPerSource"/> dòng. Trả bản sao dưới lock nên người gọi đọc thoải mái trong lúc
    /// luồng nền vẫn ghi. Nguồn chưa có log → mảng rỗng.
    /// </summary>
    public IReadOnlyList<LogEntry> Snapshot(string source)
    {
        lock (_bufLock)
        {
            return _buffers.TryGetValue(source ?? string.Empty, out var buf)
                ? buf.ToArray()
                : Array.Empty<LogEntry>();
        }
    }

    /// <summary>Xóa các dòng đang hiển thị của MỌI nguồn. KHÔNG xóa file log trên đĩa.</summary>
    public void Clear()
    {
        string[] sources;
        lock (_bufLock)
        {
            sources = _buffers.Keys.ToArray();
            _buffers.Clear();
        }

        // Bấm nút là hành động của người dùng (không phải đường nóng) → báo NGAY, không đợi nhịp gom.
        RaiseSourceUpdated(sources);
    }

    /// <summary>
    /// Xóa các dòng đang hiển thị của RIÊNG một <paramref name="source"/> — dùng khi người dùng chỉ muốn dọn
    /// log của tài khoản đang chọn. KHÔNG xóa file log trên đĩa (giống <see cref="Clear()"/>).
    /// </summary>
    public void Clear(string source)
    {
        lock (_bufLock)
        {
            _buffers.Remove(source ?? string.Empty);
        }

        RaiseSourceUpdated(new[] { source ?? string.Empty });
    }

    /// <summary>
    /// Đẩy NGAY mọi dòng đang chờ ra file (đồng bộ, nuốt lỗi IO). Bình thường timer nền tự lo; hàm này để
    /// lúc thoát app và để test khỏi phải chờ nhịp.
    /// </summary>
    public void Flush()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var lines = new List<string>();
        while (_pending.TryDequeue(out var line))
        {
            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            return;
        }

        try
        {
            lock (_fileLock)
            {
                var path = CurrentLogPath;
                RollIfNeeded(path);
                File.AppendAllLines(path, lines, Encoding.UTF8);
            }
        }
        catch
        {
            // Lỗi ghi file (ổ đầy / quyền / file bị khóa...) → bỏ qua, không phá luồng.
        }
    }

    /// <summary>Dọn timer + đẩy nốt hàng đợi ra file.</summary>
    public void Dispose()
    {
        try { _flushTimer.Dispose(); } catch { /* bỏ qua khi thoát */ }
        try { _notifyTimer.Dispose(); } catch { /* bỏ qua khi thoát */ }
        Flush();
    }

    /// <summary>Một nhịp flush của timer nền. Ngoại lệ lọt ra khỏi callback Timer sẽ GIẾT tiến trình → nuốt hết.</summary>
    private void SafeFlush()
    {
        try { Flush(); } catch { /* bỏ nhịp này, nhịp sau ghi tiếp */ }
    }

    /// <summary>File log quá lớn → giữ 1 bản <c>.1</c> rồi ghi tiếp vào file mới (không để phình vô hạn).</summary>
    private static void RollIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= MaxBytes)
            {
                return;
            }

            var bak = path + ".1";
            try { if (File.Exists(bak)) File.Delete(bak); } catch { }
            try { File.Move(path, bak); } catch { try { File.WriteAllText(path, ""); } catch { } }
        }
        catch
        {
            // Không xoay được thì cứ ghi tiếp vào file cũ.
        }
    }

    /// <summary>
    /// Ghi nhận nguồn vừa có log mới rồi hẹn MỘT lượt báo UI sau <see cref="NotifyEvery"/>. Đã hẹn rồi thì
    /// các dòng tới sau chỉ gộp vào tập <see cref="_dirty"/>, KHÔNG hẹn thêm — đó chính là chỗ cắt hàng nghìn
    /// lượt dựng lại panel xuống còn vài lượt.
    /// </summary>
    private void ScheduleNotify(string source)
    {
        lock (_notifyLock)
        {
            _dirty.Add(source);
            if (_notifyScheduled)
            {
                return;
            }

            _notifyScheduled = true;
        }

        try { _notifyTimer.Change(NotifyEvery, System.Threading.Timeout.InfiniteTimeSpan); }
        catch { /* timer đã dispose (app đang thoát) */ }
    }

    /// <summary>Một nhịp báo UI của timer. Nuốt mọi lỗi (xem <see cref="SafeFlush"/>).</summary>
    private void SafeFireNotify()
    {
        string[] sources;
        try
        {
            lock (_notifyLock)
            {
                _notifyScheduled = false;
                if (_dirty.Count == 0)
                {
                    return;
                }

                sources = _dirty.ToArray();
                _dirty.Clear();
            }
        }
        catch
        {
            return;
        }

        RaiseSourceUpdated(sources);
    }

    /// <summary>Bắn <see cref="SourceUpdated"/> cho từng nguồn TRÊN UI THREAD (một lượt <c>uiPost</c> cho cả mẻ).</summary>
    private void RaiseSourceUpdated(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0)
        {
            return;
        }

        var handler = SourceUpdated;
        if (handler is null)
        {
            return;
        }

        try
        {
            _uiPost(() =>
            {
                foreach (var source in sources)
                {
                    try { handler(source); }
                    catch { /* một người nghe hỏng không được chặn những người còn lại */ }
                }
            });
        }
        catch
        {
            // Không có dispatcher (đang tắt app / môi trường test) → bỏ lượt báo, log trên đĩa vẫn đủ.
        }
    }
}
