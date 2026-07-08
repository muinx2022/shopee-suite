using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Bảo trì nền: (1) mỗi 60s gọi ListAssignments() để KÍCH SweepStaleLocked chạy KỂ CẢ khi không có client
/// nào poll (trên máy Hub WPF cũ, sweep chỉ chạy khi có traffic /fleet hoặc /assignments) → việc 'running'
/// mồ côi vẫn được đánh 'failed' đúng hạn, nhả khoá tài khoản. (2) Mỗi ngày ~03:00 UTC: snapshot hub.db +
/// copy thư mục files\ vào backups\yyyyMMdd\ (giữ 7 bản) + wal_checkpoint(TRUNCATE) cho WAL khỏi phình.
/// </summary>
public sealed class MaintenanceService : BackgroundService
{
    private readonly HubDatabase _db;
    private readonly HubOptions _opts;
    private readonly ILogger<MaintenanceService> _log;
    private int _lastBackupDay = -1;

    public MaintenanceService(HubDatabase db, HubOptions opts, ILogger<MaintenanceService> log)
    {
        _db = db; _opts = opts; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try { _db.ListAssignments(); }             // kích sweep stale
            catch (Exception ex) { _log.LogWarning(ex, "sweep failed"); }

            var now = DateTimeOffset.UtcNow;
            if (now.Hour == 3 && now.DayOfYear != _lastBackupDay)
            {
                _lastBackupDay = now.DayOfYear;
                try { Backup(now); }
                catch (Exception ex) { _log.LogWarning(ex, "backup failed"); }
            }
        }
    }

    private void Backup(DateTimeOffset now)
    {
        var backupsRoot = Path.Combine(_opts.DataDir, "backups");
        var dir = Path.Combine(backupsRoot, now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);

        _db.VacuumInto(Path.Combine(dir, "hub.db"));    // snapshot nhất quán + checkpoint WAL
        CopyDir(_db.FilesDir, Path.Combine(dir, "files"));

        // Giữ 7 bản mới nhất.
        try
        {
            var old = Directory.GetDirectories(backupsRoot).OrderByDescending(Path.GetFileName).Skip(7);
            foreach (var d in old) Directory.Delete(d, recursive: true);
        }
        catch { }
        _log.LogInformation("backup done → {Dir}", dir);
    }

    private static void CopyDir(string src, string dst)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
