using Shopee.Core.Coordination;
using Shopee.Hub;
using XuLyDonShopee.Core.Services;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Cảnh báo "máy client rơi offline khi đang giữ việc" (H1.3): mỗi <see cref="QuetMoi"/> giây đọc snapshot fleet,
/// so với ngưỡng mất nhịp trong /settings rồi bắn webhook qua <see cref="WebhookQueueService"/>.
/// <para>Quyết định nằm HẾT ở lõi thuần <see cref="MachineOfflineWatch"/> (test được); service này chỉ lo nhịp,
/// đọc cấu hình, dựng tin và xếp hàng gửi. CỐ Ý đọc <see cref="FleetStateService.Snapshot"/> (một tham chiếu đã
/// chụp) chứ KHÔNG gọi Refresh và KHÔNG gửi trong lock nào của service đó.</para>
/// <para>Trạng thái episode nằm trong bộ nhớ (xem <see cref="MachineOfflineWatch"/>) — mất khi restart hub.</para>
/// </summary>
public sealed class MachineOfflineWatchService : BackgroundService
{
    /// <summary>Nhịp quét. Nhỏ hơn ngưỡng phút rất nhiều nên tin trễ tối đa một nhịp.</summary>
    public static readonly TimeSpan QuetMoi = TimeSpan.FromSeconds(30);

    /// <summary>Ngưỡng mất nhịp mặc định (phút) khi admin chưa đặt ở /settings.</summary>
    public const int NguongMacDinhPhut = 10;

    /// <summary>Kẹp ngưỡng người dùng nhập: dưới 1' là báo loạn (heartbeat 12s + mạng chập chờn), trên 120' thì
    /// cảnh báo mất ý nghĩa.</summary>
    public const int NguongMinPhut = 1;
    public const int NguongMaxPhut = 120;

    /// <summary>Nhãn kênh dùng trong log kết quả gửi (khuôn của 3 kênh sẵn có: "đơn mới" / "lỗi app" / "đơn trả").</summary>
    private const string NhanKenh = "máy offline";

    private readonly HubDatabase _db;
    private readonly FleetStateService _fleet;
    private readonly WebhookQueueService _queue;
    private readonly ILogger<MachineOfflineWatchService> _log;

    /// <summary>machineId → số việc lúc bắn tin "mất nhịp". Chỉ luồng nền này đụng tới (không cần khoá).</summary>
    private readonly Dictionary<string, int> _daBao = new(StringComparer.Ordinal);

    /// <summary>Đã ghi log "bật cảnh báo nhưng chưa có webhook" chưa — chống rác log mỗi nhịp 30s. Reset ngay
    /// khi admin điền URL.</summary>
    private bool _daLogThieuUrl;

    public MachineOfflineWatchService(
        HubDatabase db, FleetStateService fleet, WebhookQueueService queue, ILogger<MachineOfflineWatchService> log)
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
                try { MotLuot(DateTimeOffset.Now); }
                catch (Exception ex) { _log.LogWarning(ex, "Cảnh báo máy offline: lượt quét lỗi, bỏ qua nhịp này."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Tắt host bình thường.
        }
    }

    /// <summary>Một lượt quét (tách ra để try/catch gọn + gọi được từ test thủ công).</summary>
    internal void MotLuot(DateTimeOffset now)
    {
        if (!DangBat())
        {
            // Tắt cảnh báo → quên mọi episode: bật lại sau này không được bắn "đã trở lại" cho chuyện đã cũ.
            _daBao.Clear();
            return;
        }

        var url = (_db.GetSetting(SettingKeys.NotifyWebhookMayOffline) ?? "").Trim();
        if (url.Length == 0)
        {
            // CHƯA có kênh → KHÔNG quét: quét sẽ đánh dấu episode vào _daBao, và episode "đã báo rồi" đó vĩnh
            // viễn im tiếng nếu admin điền webhook vào giữa chừng. Chưa quét = lượt sau (khi đã có URL) phát
            // hiện lại từ đầu. Log đúng MỘT lần cho tới khi có URL, khỏi rác /logs-view mỗi 30s.
            // (KHÔNG lùi về ô legacy — xem SettingKeys.NotifyWebhookMayOffline.)
            if (!_daLogThieuUrl)
            {
                _daLogThieuUrl = true;
                TryLog("", "warn",
                    $"notify \"{NhanKenh}\": đang BẬT nhưng CHƯA cấu hình webhook ({SettingKeys.NotifyWebhookMayOffline})"
                    + " — điền ô webhook tương ứng ở Hub → Cài đặt");
            }
            return;
        }
        _daLogThieuUrl = false;

        var tin = MachineOfflineWatch.Quet(_fleet.Snapshot, now, TimeSpan.FromMinutes(NguongPhut()), _daBao);
        if (tin.Count == 0) return;

        foreach (var t in tin)
        {
            var moTa = t.Loai == LoaiTinMay.MatNhip
                ? $"máy {t.Hostname} mất nhịp khi đang giữ {t.SoViec} việc"
                : $"máy {t.Hostname} đã nối lại";

            var text = t.Loai == LoaiTinMay.MatNhip
                ? OrderNotifyService.TaoTinNhanMayMatNhip(t.Hostname, t.SoViec, t.MatNhip, GioVietNam.BayGio())
                : OrderNotifyService.TaoTinNhanMayTroLai(t.Hostname, t.SoViec, GioVietNam.BayGio());
            _queue.TryQueue(new WebhookNotification(t.MachineId, new[] { url }, text, NhanKenh, moTa));
        }
    }

    private bool DangBat() => (_db.GetSetting(SettingKeys.NotifyMayOfflineBat) ?? "").Trim() == "1";

    /// <summary>Ngưỡng phút đã kẹp trong [<see cref="NguongMinPhut"/>, <see cref="NguongMaxPhut"/>]; thiếu/hỏng →
    /// <see cref="NguongMacDinhPhut"/>.</summary>
    private int NguongPhut() => KepNguong(_db.GetSetting(SettingKeys.NotifyMayOfflinePhut));

    /// <summary>HÀM THUẦN (test được) của <see cref="NguongPhut"/>.</summary>
    internal static int KepNguong(string? raw) =>
        int.TryParse((raw ?? "").Trim(), out var n) ? Math.Clamp(n, NguongMinPhut, NguongMaxPhut) : NguongMacDinhPhut;

    private void TryLog(string machineId, string level, string text)
    {
        try { _db.AppendLog(new AppendLogRequest(machineId, "", level, text)); }
        catch (Exception ex) { _log.LogWarning(ex, "Cảnh báo máy offline: lỗi ghi log."); }
    }
}
