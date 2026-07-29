using Microsoft.Extensions.Logging.Abstractions;
using Shopee.Hub;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Tests;

public sealed class FleetStateServiceTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-fleet-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Refresh_DoesNotOverlapSubscriberCallbacks()
    {
        using var db = new HubDatabase(_dataDir);
        using var service = new FleetStateService(db, NullLogger<FleetStateService>.Instance);
        var active = 0;
        var maxActive = 0;

        service.Changed += () =>
        {
            var current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maxActive, current);
            Thread.Sleep(20);
            Interlocked.Decrement(ref active);
        };

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(service.Refresh)));

        Assert.Equal(1, maxActive);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}

internal static class InterlockedExtensions
{
    public static void Max(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (current < value)
        {
            var original = Interlocked.CompareExchange(ref target, value, current);
            if (original == current) return;
            current = original;
        }
    }
}
