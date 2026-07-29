using System.Threading.Channels;
using Shopee.Core.Coordination;
using XuLyDonShopee.Core.Services;

namespace Shopee.Hub.Web.Services;

public sealed record WebhookNotification(
    string MachineId,
    IReadOnlyList<string> Urls,
    string Text,
    string Label,
    string Description);

/// <summary>Bounded queue gui webhook: endpoint chi enqueue, mot worker gui tuan tu va dung sach theo host.</summary>
public sealed class WebhookQueueService : BackgroundService
{
    private const int Capacity = 256;
    private const int MaxAttempts = 2;

    private readonly HubDatabase _db;
    private readonly ILogger<WebhookQueueService> _log;
    private readonly OrderNotifyService _sender = new();
    private readonly Channel<WebhookNotification> _queue = Channel.CreateBounded<WebhookNotification>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public WebhookQueueService(HubDatabase db, ILogger<WebhookQueueService> log)
    {
        _db = db;
        _log = log;
    }

    public bool TryQueue(WebhookNotification notification)
    {
        if (_queue.Writer.TryWrite(notification)) return true;

        _log.LogWarning("Webhook queue đầy; bỏ thông báo {Label}: {Description}",
            notification.Label, notification.Description);
        TryAppendLog(notification.MachineId, "warn",
            $"notify \"{notification.Label}\": queue đầy, đã bỏ — {notification.Description}");
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var notification in _queue.Reader.ReadAllAsync(stoppingToken))
                await SendAsync(notification, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown; no detached task survives the process.
        }
    }

    private async Task SendAsync(WebhookNotification notification, CancellationToken ct)
    {
        var ok = 0;
        foreach (var url in notification.Urls)
        {
            var sent = false;
            for (var attempt = 1; attempt <= MaxAttempts && !sent; attempt++)
            {
                try
                {
                    sent = await _sender.SendAsync(url, notification.Text,
                        message => _log.LogWarning("{Message}", message), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Notify: lỗi gửi webhook {Label}, lần {Attempt}.",
                        notification.Label, attempt);
                }

                if (!sent && attempt < MaxAttempts) await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            if (sent) ok++;
        }

        TryAppendLog(notification.MachineId, ok == notification.Urls.Count ? "info" : "warn",
            $"notify \"{notification.Label}\": gửi {ok}/{notification.Urls.Count} webhook OK — {notification.Description}");
    }

    private void TryAppendLog(string machineId, string level, string text)
    {
        try { _db.AppendLog(new AppendLogRequest(machineId, "", level, text)); }
        catch (Exception ex) { _log.LogWarning(ex, "Notify: lỗi ghi log kết quả."); }
    }
}
