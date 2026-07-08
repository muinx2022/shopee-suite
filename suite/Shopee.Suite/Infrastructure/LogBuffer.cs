using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Shopee.Core.Infrastructure;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Bộ đệm log cho UI, thay <see cref="ObservableCollection{T}"/> trần ở các ViewModel:
///  (1) GIỚI HẠN số dòng giữ trong bộ nhớ (mặc định 500) → ô log (TextBox) KHÔNG phình vô hạn. Trước đây
///      log nối liên tục khi chạy lâu → TextBox dựng lại TOÀN BỘ text mỗi dòng (O(n²)) → ĐƠ MÁY. Cắt đầu
///      giữ trần → chi phí vẽ không đổi dù chạy bao lâu.
///  (2) GHI TOÀN BỘ log ra file <c>%AppData%\ShopeeSuite\logs\{fileName}</c> ở LUỒNG NỀN (không chặn UI) →
///      "muốn xem toàn bộ log thì mở file". Tự xoay vòng khi file quá lớn (giữ 1 bản .1 rồi ghi mới).
///
/// Kế thừa <see cref="ObservableCollection{T}"/> nên mọi chỗ gọi <c>.Add</c> / <c>.Clear</c> cũ CHẠY NGUYÊN;
/// chỉ khác: tự cắt dòng cũ khi vượt trần + tự đẩy mọi dòng ra file. Add được gọi trên UI thread (như trước);
/// việc ghi file làm ở timer nền qua hàng đợi để không đụng UI thread.
/// </summary>
public sealed class LogBuffer : ObservableCollection<string>
{
    private readonly int _cap;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly Timer _flush;
    // Mọi buffer ghi file dưới CÙNG một khoá (mỗi buffer 1 file khác nhau; ghi 1s/lần nên không phải nút cổ chai).
    private static readonly object FileLock = new();
    private const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>Đường dẫn file log đầy đủ (để nút "Mở log" mở đúng file).</summary>
    public string FilePath { get; }

    public LogBuffer(string fileName, int cap = 500)
    {
        _cap = Math.Max(50, cap);
        FilePath = Path.Combine(SuitePaths.ModuleDir("logs"), fileName);
        // Đẩy hàng đợi ra file mỗi 1s (gộp nhiều dòng 1 lần ghi) — nền, best-effort.
        _flush = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    protected override void InsertItem(int index, string item)
    {
        base.InsertItem(index, item);
        _pending.Enqueue($"{DateTime.Now:HH:mm:ss} {item}");
        // Vượt trần → cắt dòng CŨ NHẤT ở đầu. TextBox (qua LogText) tự dựng lại từ tập đã cắt → chi phí có trần.
        while (Count > _cap) base.RemoveItem(0);
    }

    protected override void ClearItems()
    {
        base.ClearItems();
        // Clear = bắt đầu lượt hiển thị mới → chỉ xoá phần XEM, GIỮ file (đánh dấu mốc để đọc file dễ phân đoạn).
        _pending.Enqueue($"──────── {DateTime.Now:yyyy-MM-dd HH:mm:ss} ────────");
    }

    private void Flush()
    {
        if (_pending.IsEmpty) return;
        var sb = new StringBuilder();
        while (_pending.TryDequeue(out var line)) sb.Append(line).Append(Environment.NewLine);
        if (sb.Length == 0) return;
        try
        {
            lock (FileLock)
            {
                RollIfNeeded();
                File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { /* ghi file là phụ, không được làm gãy UI */ }
    }

    private void RollIfNeeded()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists || fi.Length <= MaxBytes) return;
            var bak = FilePath + ".1";
            try { if (File.Exists(bak)) File.Delete(bak); } catch { }
            try { File.Move(FilePath, bak); } catch { try { File.WriteAllText(FilePath, ""); } catch { } }
        }
        catch { }
    }
}
