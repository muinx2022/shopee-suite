using System.IO;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Kho <see cref="LogBuffer"/> RIÊNG theo TỪNG tài khoản BigSeller (key = Account.Id), tạo-khi-cần, thread-safe.
/// Chạy nhiều tk song song thì log của mỗi tk đi vào buffer + file RIÊNG (tab log per-acc đợt sau bind vào),
/// thay vì trộn chung một chỗ như buffer gộp <see cref="LogBuffer"/> của module.
///
/// <para>Vì sao là REGISTRY (buffer sống ở đây, KHÔNG đặt trên VM per-acc): VM per-acc
/// (ScrapeTargetViewModel/UpdateRunTargetViewModel) bị <see cref="ObservableProjection{TSource,TItem}"/> DỰNG LẠI
/// mỗi khi kho BigSeller đổi cấu trúc → buffer đặt trên đó sẽ MẤT (log tươi biến mất giữa lượt chạy). Registry
/// sống theo module VM (<see cref="ModuleViewModelBase"/>) nên buffer BỀN qua rebuild.</para>
/// </summary>
public sealed class AccountLogRegistry
{
    private readonly string _filePrefix;
    private readonly object _lock = new();
    private readonly Dictionary<string, LogBuffer> _byAccount = new(StringComparer.Ordinal);
    // Tên file đã cấp — chống 2 accountId khác nhau (nhưng trùng displayName) ra CÙNG file → ghi đè log của nhau.
    private readonly HashSet<string> _usedFileNames = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="filePrefix">Tiền tố tên file, vd "workspace-update" → mỗi tk 1 file logs\workspace-update-{tên}.log.</param>
    public AccountLogRegistry(string filePrefix) => _filePrefix = filePrefix;

    /// <summary>Buffer log của 1 tk (tạo-khi-cần, cache theo accountId, thread-safe). Buffer SỐNG theo registry nên
    /// KHÔNG mất khi VM per-acc bị projection dựng lại. displayName ĐỔI sau khi buffer đã tạo → vẫn dùng file CŨ
    /// (chấp nhận: đổi tên hiển thị hiếm, không đáng xoay file đang ghi). TOTAL: không ném với input bất kỳ.</summary>
    public LogBuffer Get(string accountId, string displayName)
    {
        accountId ??= "";
        displayName ??= "";
        lock (_lock)
        {
            if (_byAccount.TryGetValue(accountId, out var buf)) return buf;

            // Tên file theo displayName (dễ đọc khi mở thư mục logs) — thay ký tự cấm tên file thành '_', trim.
            var safe = MakeSafe(displayName);
            if (safe.Length == 0) safe = MakeSafe(accountId);   // displayName rỗng → dùng accountId
            var fileName = $"{_filePrefix}-{safe}.log";
            // Đụng tên (accountId khác nhưng trùng displayName) → nối 6 ký tự đầu accountId cho khác nhau.
            if (_usedFileNames.Contains(fileName))
                fileName = $"{_filePrefix}-{safe}-{Head6(accountId)}.log";
            _usedFileNames.Add(fileName);

            buf = new LogBuffer(fileName);
            _byAccount[accountId] = buf;
            return buf;
        }
    }

    // Thay mọi ký tự cấm trong tên file thành '_' rồi trim (khoảng trắng/'.' ở 2 đầu gây tên file lạ trên Windows).
    private static string MakeSafe(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    private static string Head6(string accountId) => accountId.Length <= 6 ? accountId : accountId[..6];
}
