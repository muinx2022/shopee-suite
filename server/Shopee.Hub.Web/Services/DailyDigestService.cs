using Shopee.Hub;
using XuLyDonShopee.Core.Services;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Tin TỔNG KẾT CUỐI NGÀY (H2.1): mỗi <see cref="QuetMoi"/> kiểm tra đã tới giờ hẹn (giờ VN) của ngày hôm nay
/// chưa, chưa gửi thì gom số liệu rồi bắn MỘT tin qua <see cref="WebhookQueueService"/>.
/// <para>Quyết định nằm hết ở lõi thuần <see cref="DailyDigest"/> (test được); service này chỉ lo nhịp, đọc cấu
/// hình, dựng tin, xếp hàng gửi và GHI MỐC "đã gửi ngày d" vào settings (bền qua restart — khác cảnh báo máy
/// offline giữ trạng thái trong bộ nhớ).</para>
/// <para>Mốc được ghi TRƯỚC khi xếp hàng gửi: webhook chết thì mất tin của hôm đó (log lại), còn ghi SAU sẽ
/// khiến mỗi nhịp 60s lại bắn thêm một tin nữa suốt buổi tối.</para>
/// </summary>
public sealed class DailyDigestService : BackgroundService
{
    /// <summary>Nhịp kiểm tra. Tin chỉ một lần/ngày nên không cần dày; trễ tối đa một nhịp so với giờ hẹn.</summary>
    public static readonly TimeSpan QuetMoi = TimeSpan.FromMinutes(1);

    /// <summary>Nhãn kênh dùng trong log kết quả gửi (khuôn của các kênh sẵn có).</summary>
    private const string NhanKenh = "tổng kết ngày";

    private readonly HubDatabase _db;
    private readonly FleetStateService _fleet;
    private readonly WebhookQueueService _queue;
    private readonly ILogger<DailyDigestService> _log;

    /// <summary>Đã ghi log "bật tổng kết nhưng chưa có webhook" chưa — chống rác log mỗi nhịp.</summary>
    private bool _daLogThieuUrl;

    public DailyDigestService(
        HubDatabase db, FleetStateService fleet, WebhookQueueService queue, ILogger<DailyDigestService> log)
    {
        _db = db;
        _fleet = fleet;
        _queue = queue;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(QuetMoi);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { MotLuot(DateTimeOffset.UtcNow); }
                catch (Exception ex) { _log.LogWarning(ex, "Tổng kết ngày: lượt kiểm tra lỗi, bỏ qua nhịp này."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Tắt host bình thường.
        }
    }

    /// <summary>Một lượt kiểm tra (tách ra để try/catch gọn + gọi được từ test).</summary>
    internal void MotLuot(DateTimeOffset now)
    {
        if ((_db.GetSetting(SettingKeys.NotifyTongKetBat) ?? "").Trim() != "1") return;

        var url = (_db.GetSetting(SettingKeys.NotifyWebhookTongKet) ?? "").Trim();
        if (url.Length == 0)
        {
            // Chưa có kênh → KHÔNG đụng mốc "đã gửi": điền webhook lúc 21:30 thì tin của hôm đó vẫn còn kịp bắn.
            if (!_daLogThieuUrl)
            {
                _daLogThieuUrl = true;
                TryLog("warn",
                    $"notify \"{NhanKenh}\": đang BẬT nhưng CHƯA cấu hình webhook ({SettingKeys.NotifyWebhookTongKet})"
                    + " — điền ô webhook tương ứng ở Hub → Cài đặt");
            }
            return;
        }
        _daLogThieuUrl = false;

        var gio = DailyDigest.KepGio(_db.GetSetting(SettingKeys.NotifyTongKetGio));
        if (!DailyDigest.DenLuotGui(now, gio, _db.GetSetting(SettingKeys.NotifyTongKetDaGuiNgay), out var ngay)) return;

        // Ghi mốc TRƯỚC khi gửi (xem xmldoc lớp): nhịp 60s + gửi thất bại = 1 tin/phút suốt buổi tối.
        _db.SetSetting(SettingKeys.NotifyTongKetDaGuiNgay, ngay);

        var nguong = TimeSpan.FromMinutes(
            MachineOfflineWatchService.KepNguong(_db.GetSetting(SettingKeys.NotifyMayOfflinePhut)));
        var so = DailyDigest.GomSoLieu(_db, _fleet.Snapshot, now, nguong);
        var text = OrderNotifyService.TaoTinNhanTongKetNgay(
            GioVietNam.Doi(now).DateTime, so.TongDonCho, so.TheoShop, so.MaTraMoi, so.ShopCanhBaoDiaChi, so.MayOffline);

        _queue.TryQueue(new WebhookNotification(
            "", new[] { url }, text, NhanKenh, $"tổng kết ngày {ngay}: {so.TongDonCho} đơn chuẩn bị hàng"));
    }

    private void TryLog(string level, string text)
    {
        try { _db.AppendLog(new Shopee.Core.Coordination.AppendLogRequest("", "", level, text)); }
        catch (Exception ex) { _log.LogWarning(ex, "Tổng kết ngày: lỗi ghi log."); }
    }
}
