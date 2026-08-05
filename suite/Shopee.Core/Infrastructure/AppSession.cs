using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Shopee.Core.Infrastructure;

/// <summary>
/// Tham số của MỘT phiên runtime (<see cref="AppSession"/>). Mỗi module engine mở trình duyệt (Scrape,
/// Update Product) giữ phiên RIÊNG với dải cổng riêng — khác nhau đúng ở mấy tham số dưới đây.
/// </summary>
public sealed class AppSessionOptions
{
    /// <summary>Các cổng probe theo OFFSET block (<c>base + offset</c>): block chỉ được nhận khi TẤT CẢ còn trống.</summary>
    public int[] ProbePortsAtOffset { get; init; } = [];

    /// <summary>Các cổng probe theo CHỈ SỐ block (<c>base + offset / PortBlockSize</c>) — dải cấp 1 cổng/1 block.</summary>
    public int[] ProbePortsAtBlockIndex { get; init; } = [];

    /// <summary>Tạo sẵn thư mục persistent-data ngay trong <see cref="AppSession.Initialize"/>.
    /// (Thực tế dư: <see cref="SuitePaths.ModuleDir"/> đã <c>CreateDirectory</c> — giữ cờ để không đổi hành vi
    /// so với bản UpdateProduct cũ vốn có thêm dòng này.)</summary>
    public bool CreatePersistentDataDir { get; init; }

    /// <summary>Chuỗi lỗi khi quét hết 80 block mà không nhận được block nào.</summary>
    public string NoFreeBlockMessage { get; init; } = "Khong tim duoc block port trong.";
}

/// <summary>
/// PHIÊN RUNTIME của một module engine: cấp một BLOCK cổng riêng (giữ chỗ bằng file-lock độc quyền trong
/// <c>runtime-sessions/_port-locks</c>) + thư mục session tạm, và dọn session cũ. Trước đây là HAI bản chép tay
/// (MultiBrave · UpdateProduct) khác nhau đúng ở danh sách cổng probe, cờ tạo thư mục và chuỗi lỗi — nay gộp về
/// một lớp, khác biệt truyền qua <see cref="AppSessionOptions"/>.
/// <para>
/// <b>Mỗi module một THỂ HIỆN riêng</b> (không dùng chung static): file-lock mở với <see cref="FileShare.None"/>
/// nên phiên thứ hai trong CÙNG process bị <see cref="IOException"/> ở block đã bị phiên thứ nhất giữ → tự nhảy
/// sang block kế. Đó chính là cơ chế giữ cho Scrape và Update Product không giẫm dải cổng của nhau.
/// </para>
/// </summary>
public sealed class AppSession
{
    /// <summary>Số cổng mỗi block — offset của block thứ n là <c>n * PortBlockSize</c>.</summary>
    public const int PortBlockSize = 1000;

    private readonly AppSessionOptions _options;
    private FileStream? _portLock;

    public AppSession(AppSessionOptions options) => _options = options;

    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

    public string SessionId { get; private set; } = "";
    public string RootDirectory { get; private set; } = "";
    public int PortOffset { get; private set; }

    public void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(RootDirectory))
            return;

        SessionId = $"run-{Process.GetCurrentProcess().Id}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..34];
        RootDirectory = Path.Combine(BaseDirectory, "runtime-sessions", SessionId);
        Directory.CreateDirectory(RootDirectory);
        if (_options.CreatePersistentDataDir)
            Directory.CreateDirectory(ResolvePersistentDataPath());
        PortOffset = AllocatePortOffset();
        CleanupStaleSessions();
    }

    // Dữ liệu bền (profile/cookie login Shopee + BigSeller) lưu ở %AppData%\ShopeeSuite\persistent-data —
    // KHÔNG neo theo vị trí .exe nữa: trước đây tính BaseDirectory\..\..\.. nên bản publish (thêm cấp
    // win-x64\publish) trỏ vào trong bin/publish → bị xóa/ghi đè mỗi lần build publish → mất login.
    // (static: đường dẫn KHÔNG phụ thuộc phiên — mọi module dùng chung một kho persistent-data.)
    public static string ResolvePersistentDataPath(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = SuitePaths.ModuleDir("persistent-data");
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    public void Cleanup()
    {
        try { _portLock?.Dispose(); } catch { }
        _portLock = null;

        if (string.IsNullOrWhiteSpace(RootDirectory) || !Directory.Exists(RootDirectory))
            return;

        try
        {
            ClearReadOnlyAttributes(RootDirectory);
            Directory.Delete(RootDirectory, recursive: true);
        }
        catch
        {
            try
            {
                File.WriteAllText(Path.Combine(RootDirectory, ".delete-on-next-start"), DateTimeOffset.Now.ToString("O"));
            }
            catch { }
        }
    }

    private int AllocatePortOffset()
    {
        var lockRoot = Path.Combine(BaseDirectory, "runtime-sessions", "_port-locks");
        Directory.CreateDirectory(lockRoot);

        for (var block = 0; block < 80; block++)
        {
            var offset = block * PortBlockSize;
            if (!PortsLookFree(offset))
                continue;

            var lockPath = Path.Combine(lockRoot, $"block-{block}.lock");
            try
            {
                _portLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _portLock.SetLength(0);
                using var writer = new StreamWriter(_portLock, leaveOpen: true);
                writer.WriteLine(Process.GetCurrentProcess().Id);
                writer.WriteLine(DateTimeOffset.Now.ToString("O"));
                writer.Flush();
                _portLock.Position = 0;
                return offset;
            }
            catch (IOException)
            {
            }
        }

        throw new InvalidOperationException(_options.NoFreeBlockMessage);
    }

    private bool PortsLookFree(int offset)
    {
        foreach (var basePort in _options.ProbePortsAtOffset)
            if (!IsPortFree(basePort + offset))
                return false;
        foreach (var basePort in _options.ProbePortsAtBlockIndex)
            if (!IsPortFree(basePort + (offset / PortBlockSize)))
                return false;
        return true;
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CleanupStaleSessions()
    {
        var root = Path.Combine(BaseDirectory, "runtime-sessions");
        if (!Directory.Exists(root))
            return;

        foreach (var dir in Directory.EnumerateDirectories(root, "run-*"))
        {
            if (string.Equals(dir, RootDirectory, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var info = new DirectoryInfo(dir);
                if (DateTime.Now - info.LastWriteTime < TimeSpan.FromHours(12) &&
                    !File.Exists(Path.Combine(dir, ".delete-on-next-start")))
                    continue;

                ClearReadOnlyAttributes(dir);
                Directory.Delete(dir, recursive: true);
            }
            catch { }
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
    }
}
