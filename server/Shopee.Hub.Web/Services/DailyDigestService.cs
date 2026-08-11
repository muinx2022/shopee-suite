using Shopee.Hub;
using XuLyDonShopee.Core.Services;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Tin TỔNG KẾT CUỐI NGÀY (H2.1): mỗi <see cref="QuetMoi"/> kiểm tra đã tới giờ hẹn (giờ VN) của ngày hôm nay
/// chưa, chưa gửi thì gom số liệu rồi bắn MỘT tin qua <see cref="WebhookQueueService"/>.
/// <para>Quyết định nằm hết ở lõi thuần <see cref="DailyDigest"/> (test được); service này chỉ lo nhịp, đọc cấu
/// hình, dựng tin, xếp hàng gửi và GHI MỐC "đã gửi ngày d" vào settings (bền qua restart — khác cảnh báo máy
/// offline giữ trạng thái trong bộ nhớ).</para>
/// <para><b>Mốc ghi SAU khi tin được XỬ XONG</b> (callback <c>OnDone</c> của <see cref="WebhookNotification"/> —
/// T8, review 11/08). Bản trước ghi TRƯỚC khi xếp hàng nên restart hub đúng lúc (tin còn trong hàng đợi bị vứt)
/// là mất hẳn tin của ngày, không gửi bù. Chống trùng trong lúc chờ gửi bằng chốt in-flight
/// <see cref="_ngayDangGui"/> (bộ nhớ): nhịp 60s không xếp thêm tin cho ngày đang bay. Mốc vẫn ghi KỂ CẢ khi
/// gửi fail đủ mọi lần thử — giữ đúng chính sách cũ "webhook chết thì mất tin của hôm đó, có log", vì đổi sang
/// chỉ-ghi-khi-thành-công là mỗi nhịp 60s lại bắn thêm một tin suốt buổi tối.</para>
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

    /// <summary>Ngày (nhãn VN) đang có tin NẰM TRONG hàng đợi gửi — chốt in-flight trong bộ nhớ để nhịp 60s
    /// không xếp trùng trong lúc chờ worker gửi. Restart giữa chừng thì chốt lẫn mốc bền đều chưa có ⇒ nhịp đầu
    /// sau restart xếp lại tin — đúng đường gửi bù mà T8 đòi. <c>volatile</c>: ghi ở thread worker (OnDone),
    /// đọc ở thread nhịp quét.</summary>
    private volatile string? _ngayDangGui;

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

    /// <summary>Một lượt kiểm tra (tách ra để try/catch gọn + gọi được từ test). <paramref name="xepHang"/> chỉ
    /// test rót (mặc định null → <see cref="WebhookQueueService.TryQueue"/>) — để test được đường mốc/in-flight
    /// mà không phải gửi HTTP thật.</summary>
    internal void MotLuot(DateTimeOffset now, Func<WebhookNotification, bool>? xepHang = null)
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
        if (string.Equals(_ngayDangGui, ngay, StringComparison.Ordinal)) return; // đã xếp hàng, đang chờ worker gửi

        var nguong = TimeSpan.FromMinutes(
            MachineOfflineWatchService.KepNguong(_db.GetSetting(SettingKeys.NotifyMayOfflinePhut)));
        var so = DailyDigest.GomSoLieu(_db, _fleet.Snapshot, now, nguong);
        var text = OrderNotifyService.TaoTinNhanTongKetNgay(
            GioVietNam.Doi(now).DateTime, so.TongDonCho, so.TheoShop, so.MaTraMoi, so.ShopCanhBaoDiaChi, so.MayOffline);

        var tin = new WebhookNotification(
            "", new[] { url }, text, NhanKenh, $"tổng kết ngày {ngay}: {so.TongDonCho} đơn đã chuẩn bị")
        {
            // Mốc ghi Ở ĐÂY — sau khi worker xử xong tin (kể cả gửi fail, xem xmldoc lớp). OnDone chạy trên
            // thread worker: SetSetting tự khoá; _ngayDangGui ghi đè đơn thuần (nhịp quét đọc giá trị cũ nhất
            // cũng chỉ dẫn tới một lượt DenLuotGui đọc mốc bền — mốc đã ghi nên vẫn không xếp trùng).
            OnDone = _ =>
            {
                try { _db.SetSetting(SettingKeys.NotifyTongKetDaGuiNgay, ngay); }
                catch (Exception ex)
                {
                    // GHI MỐC HỎNG thì GIỮ NGUYÊN chốt in-flight (không nhả): nhả mà mốc trống là nhịp 60s bắn
                    // lại 1 tin/phút suốt buổi tối — đúng thứ thiết kế cũ cố ý chặn. Giữ chốt thì mất tin của
                    // NGÀY đó (có log), sang ngày mới chốt tự hết tác dụng vì so theo nhãn ngày.
                    _log.LogWarning(ex, "Tổng kết ngày: lỗi ghi mốc đã-gửi — giữ chốt in-flight, tin của ngày này coi như đã xử.");
                    return;
                }
                _ngayDangGui = null;
            },
        };

        // Chốt in-flight đặt TRƯỚC khi xếp hàng: worker có thể gửi xong (OnDone xoá chốt) trước cả khi TryQueue
        // trả về — đặt sau là tự khoá mình ở trạng thái "đang gửi" vĩnh viễn. Xếp không được (queue đầy) → nhả
        // chốt, không đụng mốc: nhịp sau thử lại từ đầu.
        _ngayDangGui = ngay;
        if (!(xepHang ?? _queue.TryQueue)(tin))
        {
            _ngayDangGui = null;
        }
    }

    private void TryLog(string level, string text)
    {
        try { _db.AppendLog(new Shopee.Core.Coordination.AppendLogRequest("", "", level, text)); }
        catch (Exception ex) { _log.LogWarning(ex, "Tổng kết ngày: lỗi ghi log."); }
    }
}
