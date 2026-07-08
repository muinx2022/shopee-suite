using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Shopee.Suite.Services;

/// <summary>
/// Tự cập nhật app qua Velopack + GitHub Releases (repo PUBLIC → không cần token).
///
/// Triết lý cho máy worker: kiểm tra + TẢI SẴN bản mới ở nền lúc khởi động, nhưng KHÔNG tự khởi động lại
/// (để không cắt ngang job scrape/update đang chạy). Người dùng bấm "Cập nhật &amp; khởi động lại" khi rảnh.
///
/// Chạy từ dev/bin (chưa cài qua Velopack) → <see cref="IsSupported"/> = false, mọi thứ no-op êm.
/// </summary>
public sealed class UpdateService
{
    public static UpdateService Shared { get; } = new();

    // Repo public — GithubSource đọc release không cần token. Đổi tại đây nếu chuyển repo phát hành.
    private const string RepoUrl = "https://github.com/muinx2022/shopee-suite";

    private readonly UpdateManager _mgr;
    private UpdateInfo? _pending;

    /// <summary>Bắn khi trạng thái đổi (đang kiểm tra / có bản mới / đã tải…) — UI subscribe để vẽ lại.</summary>
    public event Action? Changed;

    /// <summary>false khi app KHÔNG được cài qua Velopack (chạy dev/bin) → ẩn nút cập nhật.</summary>
    public bool IsSupported => _mgr.IsInstalled;
    public bool UpdateReady { get; private set; }
    public bool Busy { get; private set; }
    public string? AvailableVersion { get; private set; }
    public string Status { get; private set; } = "";

    private UpdateService()
    {
        // Cửa hậu STAGING/TEST: đặt env SHOPEE_UPDATE_FEED = thư mục hoặc URL chứa bản phát hành →
        // lấy bản mới từ đó thay vì GitHub. Để thử trọn vòng cập nhật trên máy dev mà KHÔNG phát hành công khai.
        // Không đặt (prod) → mặc định GitHub Releases (repo public, khỏi token).
        var feed = Environment.GetEnvironmentVariable("SHOPEE_UPDATE_FEED");
        _mgr = string.IsNullOrWhiteSpace(feed)
            ? new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false))
            : new UpdateManager(feed);
    }

    private void Set(string status) { Status = status; Changed?.Invoke(); }

    /// <summary>
    /// Kiểm tra bản mới rồi TẢI nền nếu có. An toàn gọi lúc khởi động — nuốt lỗi mạng, không ném.
    /// </summary>
    public async Task CheckAsync()
    {
        if (!IsSupported) { Set(""); return; }
        if (Busy) return;
        Busy = true;
        try
        {
            Set("⏳ Đang kiểm tra bản mới…");
            var info = await _mgr.CheckForUpdatesAsync();
            if (info is null)
            {
                _pending = null; UpdateReady = false; AvailableVersion = null;
                Set("🟢 Đang dùng bản mới nhất.");
                return;
            }

            AvailableVersion = info.TargetFullRelease?.Version?.ToString() ?? "?";
            Set($"⏳ Có bản mới v{AvailableVersion} — đang tải nền…");
            await _mgr.DownloadUpdatesAsync(info);

            _pending = info;
            UpdateReady = true;
            Set($"⬆ Đã tải xong bản mới v{AvailableVersion}. Bấm \"Cập nhật & khởi động lại\" khi rảnh.");
        }
        catch (Exception ex)
        {
            Set("Kiểm tra cập nhật lỗi: " + ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Áp dụng bản đã tải + khởi động lại app NGAY. CHỈ gọi khi người dùng chủ động bấm (sẽ đóng app).
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_pending is null || !IsSupported) return;
        _mgr.ApplyUpdatesAndRestart(_pending);
    }
}
