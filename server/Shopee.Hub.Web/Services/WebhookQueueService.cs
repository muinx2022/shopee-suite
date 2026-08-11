using System.Threading.Channels;
using Shopee.Core.Coordination;
using XuLyDonShopee.Core.Services;

namespace Shopee.Hub.Web.Services;

public sealed record WebhookNotification(
    string MachineId,
    IReadOnlyList<string> Urls,
    string Text,
    string Label,
    string Description)
{
    /// <summary>Gọi SAU khi worker đã XỬ XONG tin này (hết mọi lần thử; tham số = gửi OK đủ mọi URL không).
    /// Null = không ai cần biết — mọi caller cũ giữ nguyên. Sinh ra cho mốc "đã gửi tổng kết ngày" (T8, review
    /// 11/08): mốc phải ghi SAU khi tin được xử, kẻo restart đúng lúc (tin còn nằm trong hàng đợi bị vứt) là
    /// mất hẳn tin của ngày mà không có đường gửi bù.</summary>
    public Action<bool>? OnDone { get; init; }
}

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

    /// <summary>Ngân sách XẢ NỐT hàng đợi lúc tắt host — nhỏ hơn hẳn ShutdownTimeout mặc định 30s của host để
    /// không kéo dài lượt restart; đủ cho vài tin webhook (mỗi tin tối đa 2 lần thử).</summary>
    private static readonly TimeSpan NganSachXaKhiTat = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var notification in _queue.Reader.ReadAllAsync(stoppingToken))
                await SendAsync(notification, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host đang tắt — nhưng KHÔNG được vứt im lặng phần đã xếp hàng (T8, review 11/08): restart đúng
            // lúc là mất tin (kể cả tổng kết ngày) không dấu vết. Xả nốt bên dưới với ngân sách ngắn.
        }
        await XaNotKhiTatAsync();
    }

    /// <summary>Xả phần còn trong hàng đợi khi host tắt: gửi được thì gửi (token ngân sách riêng), hết ngân sách
    /// thì ĐẾM số tin đành bỏ và log — mất thì vẫn mất (không persist), nhưng mất CÓ TIẾNG. Tin nào chưa được
    /// xử thì OnDone không được gọi ⇒ mốc "đã gửi" của nó chưa ghi ⇒ bên đặt lịch tự gửi lại sau restart.</summary>
    private async Task XaNotKhiTatAsync()
    {
        var guiKip = 0;
        var danhBo = 0;
        using var cts = new CancellationTokenSource(NganSachXaKhiTat);
        while (_queue.Reader.TryRead(out var notification))
        {
            if (cts.IsCancellationRequested)
            {
                danhBo++;
                continue;
            }
            try
            {
                await SendAsync(notification, cts.Token);
                guiKip++;
            }
            catch (OperationCanceledException)
            {
                danhBo++;
            }
            catch (Exception ex)
            {
                danhBo++;
                _log.LogWarning(ex, "Webhook queue: lỗi khi xả tin {Label} lúc tắt host.", notification.Label);
            }
        }
        if (guiKip + danhBo > 0)
        {
            _log.LogInformation("Webhook queue: tắt host — xả kịp {GuiKip} tin, đành bỏ {DanhBo} tin.", guiKip, danhBo);
        }
    }

    /// <summary>Gửi một tin (mọi URL, mỗi URL tối đa <see cref="MaxAttempts"/> lần) rồi báo <c>OnDone</c> —
    /// callback đặt trong <c>finally</c>: tin đã được XỬ (kể cả gửi fail toàn phần) thì bên đặt lịch phải biết,
    /// nếu không mốc "đã gửi" không bao giờ ghi và nhịp quét sẽ xếp lại tin y hệt mỗi phút.</summary>
    private async Task SendAsync(WebhookNotification notification, CancellationToken ct)
    {
        var ok = 0;
        var daXuXong = false;
        try
        {
            await GuiMoiUrlAsync(notification, n => ok = n, ct);
            daXuXong = true;
        }
        finally
        {
            // Báo OnDone theo cờ "ĐÃ XỬ XONG" chứ KHÔNG theo trạng thái token (phản biện 11/08): token có thể
            // vừa hủy NGAY SAU khi cú POST cuối đã thành công — lúc đó tin ĐÃ đi, không báo là mốc "đã gửi"
            // không bao giờ ghi và sau restart tin y hệt bị bắn lại. Ngược lại, hủy GIỮA CHỪNG thì
            // GuiMoiUrlAsync ném ⇒ cờ còn false ⇒ không báo ⇒ tin được coi là "chưa xử" để gửi lại sau restart.
            if (daXuXong)
            {
                try { notification.OnDone?.Invoke(ok == notification.Urls.Count); }
                catch (Exception ex) { _log.LogWarning(ex, "Notify: OnDone của {Label} ném lỗi.", notification.Label); }
            }
        }
    }

    private async Task GuiMoiUrlAsync(WebhookNotification notification, Action<int> baoSoOk, CancellationToken ct)
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
        baoSoOk(ok);

        TryAppendLog(notification.MachineId, ok == notification.Urls.Count ? "info" : "warn",
            $"notify \"{notification.Label}\": gửi {ok}/{notification.Urls.Count} webhook OK — {notification.Description}");
    }

    private void TryAppendLog(string machineId, string level, string text)
    {
        try { _db.AppendLog(new AppendLogRequest(machineId, "", level, text)); }
        catch (Exception ex) { _log.LogWarning(ex, "Notify: lỗi ghi log kết quả."); }
    }
}
