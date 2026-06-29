namespace Shopee.Core.Coordination;

/// <summary>Hub TẮT: mọi acquire được cấp (Off), không ghi gì — app chạy single-machine như cũ.</summary>
public sealed class NoOpCoordinationHub : ICoordinationHub
{
    public static readonly NoOpCoordinationHub Instance = new();
    private NoOpCoordinationHub() { }

    public bool Enabled => false;
    public event Action? Changed { add { } remove { } }

    public Task<LeaseAttempt> AcquireAsync(CoordKey key, bool force, CancellationToken ct)
        => Task.FromResult(new LeaseAttempt(AcquireResult.Off(), null));

    public void PublishProgress(CoordKey key, int from, int to) { }
    public void PublishCompletion(CoordKey key, string status, int lastRow) { }
    public IReadOnlyList<LeaseRecord> ActiveLeases() => [];
}

/// <summary>Đồng bộ TẮT: không làm gì.</summary>
public sealed class NoOpHubSync : IHubSync
{
    public static readonly NoOpHubSync Instance = new();
    private NoOpHubSync() { }

    public bool Enabled => false;
    public void Start() { }
    public void Stop() { }
    public Task PullNowAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Bộ định vị dịch vụ điều phối toàn app. Mặc định NoOp (hub tắt → không đổi hành vi). Khi người
/// dùng bật Hub, App khởi tạo và gán <see cref="Hub"/>/<see cref="Sync"/> bằng impl HTTP.
/// </summary>
public static class Coordination
{
    public static ICoordinationHub Hub { get; set; } = NoOpCoordinationHub.Instance;
    public static IHubSync Sync { get; set; } = NoOpHubSync.Instance;
}
