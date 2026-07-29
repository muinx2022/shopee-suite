using Microsoft.Extensions.Logging.Abstractions;
using Shopee.Hub;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Tests;

public sealed class WebhookQueueServiceTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-webhook-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryQueue_RejectsItemWhenBoundedQueueIsFull()
    {
        using var db = new HubDatabase(_dataDir);
        using var service = new WebhookQueueService(db, NullLogger<WebhookQueueService>.Instance);
        var notification = new WebhookNotification("machine", ["https://example.invalid"], "text", "test", "capacity");

        for (var i = 0; i < 256; i++) Assert.True(service.TryQueue(notification));

        Assert.False(service.TryQueue(notification));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
