using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// <b>Hậu xử lý một lượt cầu nối</b> của MỘT tài khoản: nhận kết quả extension đọc được (đơn đã sync, mã yêu cầu
/// trả hàng, sự cố địa chỉ lấy hàng) rồi ghi DB → đẩy GSheet/hub → +1 "Đã bán" → báo tin ra ngoài. Tách khỏi
/// <see cref="AccountSession"/> (đợt dọn 2026-07-30) vì phần này KHÔNG đụng trình duyệt/UI — chỉ DTO + DB + HTTP —
/// nên test được bằng SQLite tạm và hook stub, trong khi phiên thì không.
/// <para>
/// Một pipeline sống theo MỘT <see cref="AccountSession"/> (cùng vòng đời): giữ shop-context của lượt đang chạy
/// (<see cref="SetShopContext"/>) và cờ chống spam log "chưa cấu hình GSheet".
/// </para>
/// <para>
/// Mọi lượt đẩy đều CHẠY NỀN (fire-and-forget) sau khi DB đã ghi xong: chúng chỉ đụng DB + file + HTTP nên chạy
/// song song được với nhịp đọc/xử đơn của cầu nối; <see cref="PushGate"/> (chốt TOÀN TIẾN TRÌNH) chống hai lượt
/// cùng loại chồng nhau — kể cả khi lượt kia do <see cref="HubOutboxWorker"/> kích hoạt.
/// </para>
/// </summary>
internal sealed class OrderPersistPipeline
{
    private readonly long _accountId;
    private readonly AppServices _services;

    // ===== Mô hình 1 subaccount = nhiều shop =====
    // Shop ĐANG xử lý trong vòng lặp shop (cầu nối rót trước mỗi lượt lưu). PersistSyncedOrdersAsync gắn shop_id
    // này vào đơn khi upsert; HubOutbox.PushOrdersToGsheetAsync lọc đơn theo shop + lấy Tên Shop = tên đăng nhập.
    // volatile: vòng cầu nối (thread nền) đặt, lượt đẩy GSheet nền đọc (nhưng đã CHỤP giá trị lúc kích hoạt để tránh đua).
    private volatile string? _currentShopId;
    private volatile string? _currentShopLogin;

    // Cờ chống spam log "chưa cấu hình GSheet": phiên chạy cả buổi, mỗi shop một lượt đẩy sheet → chỉ báo 1 dòng
    // cho cả phiên là đủ để người dùng thấy máy đang KHÔNG ghi sheet. volatile: lượt đẩy chạy trên thread nền.
    private volatile bool _daBaoThieuGsheetUrl;

    public OrderPersistPipeline(long accountId, AppServices services)
    {
        _accountId = accountId;
        _services = services;
    }

    /// <summary>Nhãn shop (tên đăng nhập) của lượt ĐANG chạy — dùng cho cột "Shop" + khóa đếm; null khi chưa vào
    /// shop nào.</summary>
    public string? CurrentShopLogin => _currentShopLogin;

    /// <summary>Rót shop-context cho lượt lưu sắp tới (cầu nối gọi ngay trước <see cref="PersistSyncedOrdersAsync"/>):
    /// GSheet lấy đúng Tên Shop, đơn được gắn đúng <c>shop_id</c>. Nhãn rỗng → null (không có shop).</summary>
    public void SetShopContext(string? shopId, string? shopLogin)
    {
        _currentShopId = shopId;
        _currentShopLogin = string.IsNullOrWhiteSpace(shopLogin) ? null : shopLogin;
    }

    /// <summary>Cờ "được phép báo THIẾU URL Web App lần này" truyền cho
    /// <see cref="HubOutbox.PushOrdersToGsheetAsync"/>: trả true ĐÚNG một lần cho mỗi phiên (xem
    /// <see cref="_daBaoThieuGsheetUrl"/>), các lần sau false.</summary>
    private bool NenBaoThieuGsheetUrl()
    {
        if (_daBaoThieuGsheetUrl)
        {
            return false;
        }
        _daBaoThieuGsheetUrl = true;
        return true;
    }

    /// <summary>Kết quả một lượt <see cref="PersistSyncedOrdersAsync"/> — số đơn thêm mới / cập nhật / bỏ qua (ngoài theo dõi).</summary>
    public readonly record struct PersistOrdersResult(int Inserted, int Updated, int BoQua);

    /// <summary>
    /// <b>Phần LƯU của một lượt sync</b> — thao tác THUẦN trên DTO <paramref name="orders"/> + DB/GSheet/hub,
    /// KHÔNG đụng trình duyệt: lọc "chỉ giữ đơn Chuẩn bị hàng"/đã-theo-dõi → detect "Đã bán" (đọc status CŨ trước
    /// upsert) → <see cref="OrdersRepository.UpsertMany"/> gắn <paramref name="shopId"/> → đánh cờ sold ngay cho
    /// nhóm không +1 → phát <see cref="AppServices.RaiseOrdersChanged"/> → đẩy GSheet/hub/hub-slip/sold/notify chạy
    /// NỀN (fire-and-forget; hook chưa rót → im lặng bên trong). Trả về số đơn thêm/cập nhật/bỏ qua để caller tổng kết.
    /// </summary>
    public Task<PersistOrdersResult> PersistSyncedOrdersAsync(
        string? shopId, IReadOnlyList<SyncedOrder> orders, Action<string> log, CancellationToken tok)
    {
        // Lọc đơn được LƯU: đơn ĐÃ theo dõi (mã đã có trong DB) LUÔN cập nhật; đơn MỚI chỉ nhận khi Chuẩn bị hàng.
        // (Filter này ĐỒNG THỜI chặn đơn đã-bị-dọn được insert lại → không lặp ghi-xóa.)
        var existing = _services.Orders.GetOrderSns(_accountId);
        var toUpsert = orders
            .Where(o => existing.Contains(o.OrderSn) || ShopeeShippingNav.LaChuanBiHang(o.Status))
            .ToList();

        // "Đã bán" theo SKU: đọc trạng thái CŨ TRƯỚC khi UpsertMany ghi đè (tuần tự nên tương đương cùng transaction).
        var soldDetect = _services.Orders.DetectNewlyDelivered(_accountId, toUpsert);

        // Upsert theo (account_id, order_sn), gắn shopId + shopLogin (tên shop, cho cột "Shop" màn Đơn hàng) của lượt
        // này. insertedOrders = đơn VỪA thêm mới (để notify).
        var (inserted, updated, insertedOrders) = _services.Orders.UpsertMany(_accountId, toUpsert, DateTime.UtcNow, shopId, _currentShopLogin);

        // Đánh cờ NGAY cho nhóm KHÔNG cần +1 (grandfather + đã-giao-không-SKU). Nhóm CÓ SKU đánh cờ SAU khi hub +1 OK.
        if (soldDetect.ImmediateMarkOrderSns.Count > 0)
        {
            _services.Orders.MarkSoldCounted(_accountId, soldDetect.ImmediateMarkOrderSns, DateTime.UtcNow);
        }

        // Vừa ghi đơn → phát tín hiệu để màn "Đơn hàng" đang mở tự nạp lại.
        _services.RaiseOrdersChanged();

        // Đẩy GSheet/hub/sold/notify chạy NỀN (chỉ đụng DB + file + HTTP, KHÔNG trình duyệt).
        // Hub: đẩy ĐƠN rồi mới đẩy PHIẾU (StartHubPushInBackground tự nối phiếu SAU khi đơn lên hub — xem lý do ở đó,
        // tránh đua với reset hub_synced_at khi mã vận đơn vừa xuất hiện).
        StartGsheetPushInBackground(log, tok);
        StartHubPushInBackground(log, tok);
        StartSoldCountInBackground(soldDetect.SkusToIncrement, soldDetect.PendingMarkOrderSns, log, tok);
        if (insertedOrders.Count > 0)
        {
            StartNotifyInBackground(insertedOrders, log, tok);
        }

        return Task.FromResult(new PersistOrdersResult(inserted, updated, orders.Count - toUpsert.Count));
    }

    /// <summary>
    /// Kích hoạt đẩy GSheet CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết. KHÔNG await trong luồng sync
    /// vì push chỉ đụng DB + file + HTTP, không đụng trình duyệt → chạy
    /// song song được với nhịp đọc "Chờ Lấy Hàng"/Xử lý đơn. <see cref="PushGate"/> (chốt TOÀN TIẾN TRÌNH) chống
    /// 2 lượt đẩy chồng nhau — cả khi lượt kia do <see cref="HubOutboxWorker"/> kích hoạt: lượt trước còn chạy →
    /// bỏ qua, log 1 dòng (lượt sau tự đẩy phần thiếu nhờ cờ DB). <paramref name="ct"/> là token phiên → dừng
    /// phiên thì lượt đẩy tự hủy (worker sẽ nhặt lại phần còn tồn).
    /// <see cref="HubOutbox.PushOrdersToGsheetAsync"/> đã tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartGsheetPushInBackground(Action<string> log, CancellationToken ct)
    {
        if (!PushGate.TryEnter(_accountId, PushKind.Gsheet))
        {
            log("GSheet: lượt đẩy trước còn đang chạy — bỏ qua (lượt sync sau tự đẩy phần thiếu).");
            return;
        }

        // CHỤP shop hiện tại NGAY (mô hình nhiều-shop): task nền chạy sau khi vòng lặp đã XÓA _currentShopId/Login
        // → phải truyền giá trị đã chụp, KHÔNG đọc field trong task. Null (chưa vào loop) → đẩy như cũ theo account.
        var shopId = _currentShopId;
        var shopLogin = _currentShopLogin;

        _ = Task.Run(async () =>
        {
            try
            {
                await HubOutbox.PushOrdersToGsheetAsync(
                    _accountId, _services, shopId, shopLogin,
                    NenBaoThieuGsheetUrl, imLangKhiKhongCoDonMoi: false, log, ct).ConfigureAwait(false);
                // Lượt NHẸ đi kèm: mã trả hàng của đơn ĐÃ BỊ DỌN khỏi app (bảng return_codes sống độc lập với
                // vòng đời đơn). Đường trên không bao giờ chạm tới chúng vì nó duyệt bảng `orders`.
                await HubOutbox.PushReturnCodesToGsheetAsync(_accountId, _services, log, ct).ConfigureAwait(false);
            }
            finally { PushGate.Exit(_accountId, PushKind.Gsheet); }
        }, CancellationToken.None);
    }

    /// <summary>Kích thước LÔ tối đa mỗi lần đẩy đơn lên hub — chia nhỏ để không nghẽn tunnel; timeout 5' của
    /// <c>_bulkHttp</c> phía hub-client đủ rộng cho một lô.</summary>
    public const int HubPushBatchSize = 200;

    /// <summary>
    /// Kích hoạt đẩy đơn lên HUB đơn hàng CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết — y pattern
    /// <see cref="StartGsheetPushInBackground"/>. <see cref="PushGate"/> (chốt TOÀN TIẾN TRÌNH) chống 2 lượt đẩy
    /// chồng nhau — cả khi lượt kia do <see cref="HubOutboxWorker"/> kích hoạt: lượt trước còn chạy → bỏ qua, log
    /// 1 dòng (lượt sau tự đẩy phần thiếu nhờ cờ DB <c>hub_synced_at</c>). <paramref name="ct"/> là token phiên →
    /// dừng phiên thì lượt đẩy tự hủy (worker sẽ nhặt lại phần còn tồn).
    /// <see cref="HubOutbox.PushOrdersToHubAsync"/> tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartHubPushInBackground(Action<string> log, CancellationToken ct)
    {
        if (!PushGate.TryEnter(_accountId, PushKind.Hub))
        {
            log("Hub: lượt đẩy trước còn đang chạy — bỏ qua (lượt sync sau tự đẩy phần thiếu).");
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await HubOutbox.PushOrdersToHubAsync(_accountId, _services, log, ct).ConfigureAwait(false); }
            finally { PushGate.Exit(_accountId, PushKind.Hub); }
            // PHIẾU đẩy SAU khi ĐƠN đã lên hub (hub_synced_at set) — KHÔNG chạy song song với đẩy đơn: khi mã vận đơn
            // vừa xuất hiện, UpsertMany RESET hub_synced_at về NULL để re-push đơn; nếu đẩy phiếu song song, nó đọc
            // GetForHubSlipPush (đòi đơn ĐÃ hub-synced) TRÚNG lúc hub_synced_at đang NULL → bỏ sót phiếu. Tuần tự thì
            // tới lượt phiếu, đơn đã re-push xong (hub_synced_at set lại) → phiếu khớp đơn trên hub.
            StartHubSlipPushInBackground(log, ct);
        }, CancellationToken.None);
    }

    /// <summary>
    /// Kích hoạt +1 "Đã bán" theo SKU lên HUB CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết — y pattern
    /// <see cref="StartHubPushInBackground"/>. <paramref name="skus"/> = SKU các đơn VỪA chuyển sang đã-giao trong
    /// lượt này (có SKU); <paramref name="orderSns"/> = mã đơn tương ứng để đánh cờ SAU khi hub +1 OK. Không có SKU
    /// nào → return ngay (không chiếm chỗ ở gate). <see cref="PushGate"/> (chốt TOÀN TIẾN TRÌNH) chống 2 lượt chồng
    /// nhau — ĐẶC BIỆT quan trọng với loại này: phiên và <see cref="HubOutboxWorker"/> cùng +1 một đơn = <b>+2</b>
    /// sai số liệu kho. <paramref name="ct"/> là token phiên → dừng phiên thì lượt +1 tự hủy (worker đếm bù sau).
    /// <see cref="HubOutbox.IncrementSoldBySkuAsync"/> tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartSoldCountInBackground(
        IReadOnlyList<string> skus, IReadOnlyList<string> orderSns, Action<string> log, CancellationToken ct)
    {
        if (skus is null || skus.Count == 0)
        {
            return; // không có đơn chuyển-sang-đã-giao có SKU → không +1 (grandfather đã đánh cờ ở luồng chính)
        }
        if (!PushGate.TryEnter(_accountId, PushKind.SoldCount))
        {
            log("Đã bán: lượt +1 trước còn đang chạy — bỏ qua (lượt sync sau tự đếm phần thiếu).");
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await HubOutbox.IncrementSoldBySkuAsync(_accountId, _services, skus, orderSns, log, ct).ConfigureAwait(false); }
            finally { PushGate.Exit(_accountId, PushKind.SoldCount); }
        }, CancellationToken.None);
    }

    /// <summary>
    /// LÕI THUẦN (không đụng trình duyệt/DB trực tiếp → test được) của việc đẩy đơn lên hub: chia
    /// <paramref name="pending"/> thành các LÔ ≤ <paramref name="batchSize"/> rồi đẩy TUẦN TỰ qua
    /// <paramref name="push"/> (đúng chữ ký hook <see cref="AppServices.PushOrdersToHub"/>). Mỗi lô trả
    /// <c>true</c> → gọi <paramref name="markSynced"/> cho đúng các mã đơn của lô (đánh dấu đã đẩy, chống đẩy
    /// trùng lượt sau); trả <c>false</c> → DỪNG các lô còn lại (giữ đơn CHƯA đánh dấu để lượt sync sau đẩy lại —
    /// thà đẩy lặp, hub idempotent, còn hơn mất đơn). <paramref name="push"/> null (hook chưa rót) hoặc
    /// <paramref name="pending"/> rỗng → không làm gì, trả 0. Trả về SỐ đơn đã đánh dấu thành công.
    /// <paramref name="ct"/> hủy → <see cref="OperationCanceledException"/> cho XUYÊN (caller phân biệt hủy chủ động).
    /// </summary>
    public static async Task<int> PushPendingToHubAsync(
        long accountId,
        IReadOnlyList<SyncedOrder> pending,
        Func<long, IReadOnlyList<SyncedOrder>, CancellationToken, Task<bool>>? push,
        Action<IReadOnlyList<string>> markSynced,
        int batchSize,
        CancellationToken ct)
    {
        if (push is null || pending is null || pending.Count == 0)
        {
            return 0;
        }

        var marked = 0;
        for (var i = 0; i < pending.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var count = Math.Min(batchSize, pending.Count - i);
            var batch = new List<SyncedOrder>(count);
            for (var j = 0; j < count; j++)
            {
                batch.Add(pending[i + j]);
            }

            var ok = await push(accountId, batch, ct).ConfigureAwait(false);
            if (!ok)
            {
                break; // hub offline / hook trả false → dừng các lô sau, lượt sync sau tự đẩy lại
            }

            var sns = new List<string>(batch.Count);
            foreach (var o in batch)
            {
                sns.Add(o.OrderSn);
            }
            markSynced(sns);
            marked += batch.Count;
        }
        return marked;
    }

    /// <summary>Kích thước LÔ tối đa mỗi lần đẩy PHIẾU lên hub — lô ≤5 PDF ~1,5MB qua tunnel (trần hub 5MB/phiếu).</summary>
    public const int HubSlipPushBatchSize = 5;

    /// <summary>
    /// Kích hoạt đẩy FILE PHIẾU lên HUB CHẠY NỀN (fire-and-forget) sau khi Sync đã tổng kết — y pattern
    /// <see cref="StartHubPushInBackground"/>. <see cref="PushGate"/> (chốt TOÀN TIẾN TRÌNH) chống 2 lượt đẩy chồng
    /// nhau — cả khi lượt kia do <see cref="HubOutboxWorker"/> kích hoạt: lượt trước còn chạy → bỏ qua, log 1 dòng
    /// (lượt sau tự đẩy phần thiếu nhờ cờ DB <c>hub_slip_synced_at</c>). <paramref name="ct"/> là token phiên →
    /// dừng phiên thì lượt đẩy tự hủy (worker sẽ nhặt lại phần còn tồn).
    /// <see cref="HubOutbox.PushSlipsToHubAsync"/> tự nuốt mọi exception nên task nền KHÔNG bao giờ ném unobserved.
    /// </summary>
    private void StartHubSlipPushInBackground(Action<string> log, CancellationToken ct)
    {
        if (!PushGate.TryEnter(_accountId, PushKind.HubSlip))
        {
            log("Hub phiếu: lượt đẩy trước còn đang chạy — bỏ qua (lượt sync sau tự đẩy phần thiếu).");
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await HubOutbox.PushSlipsToHubAsync(_accountId, _services, log, ct).ConfigureAwait(false); }
            finally { PushGate.Exit(_accountId, PushKind.HubSlip); }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Kích hoạt báo "đơn MỚI" (Slack/Discord/Telegram) CHẠY NỀN sau Sync — y pattern GSheet.
    /// Khi <see cref="AppServices.PushOrdersToHub"/> đã rót: <b>bỏ qua</b> (Hub báo sau push, tránh trùng tin).
    /// URL trống / không có đơn → im lặng. Exception nuốt + log.
    /// </summary>
    private void StartNotifyInBackground(IReadOnlyList<SyncedOrder> insertedOrders, Action<string> log, CancellationToken ct)
    {
        // Tránh trùng tin với Hub: khi hook push đã rót, Hub FireNotifyNewOrders sau orders/push.
        // Ô "có đơn mới" trên client chỉ dùng khi chạy độc lập (không nối Hub).
        if (_services.PushOrdersToHub is not null)
        {
            return;
        }

        var url = _services.Settings.GetNotifyWebhookUrlDonMoi();
        if (string.IsNullOrWhiteSpace(url) || insertedOrders is null || insertedOrders.Count == 0)
        {
            return; // người dùng chưa dùng tính năng / không có đơn mới → im lặng
        }

        // Tên shop = tên đăng nhập tài khoản (như GSheet); fallback "TK {id}" nếu chưa đọc được email.
        var tenShop = _services.Accounts.GetById(_accountId)?.Email;
        if (string.IsNullOrWhiteSpace(tenShop))
        {
            tenShop = $"TK {_accountId}";
        }
        var luc = DateTime.Now;

        _ = Task.Run(async () =>
        {
            try
            {
                var text = OrderNotifyService.TaoTinNhanDonMoi(tenShop, insertedOrders, luc);
                var ok = await _services.Notify.SendAsync(url, text, log, ct).ConfigureAwait(false);
                if (ok)
                {
                    var kenh = OrderNotifyService.NhanDienKenh(url);
                    log($"Notify: đã báo {insertedOrders.Count} đơn mới ({kenh}).");
                }
            }
            catch (OperationCanceledException)
            {
                // Hủy chủ động (dừng phiên) — thôi.
            }
            catch (Exception ex)
            {
                // Lỗi báo đơn KHÔNG phá lượt sync (đã báo thành công) — chỉ ghi log.
                log("Notify: lỗi — " + ex.ToString());
            }
        }, CancellationToken.None);
    }

    /// <summary>Ngưỡng CHỐNG SPAM cảnh báo "không đặt được địa chỉ lấy hàng": tối đa 1 tin / tài khoản / 60 phút
    /// (vòng chạy tự lặp lại sau ~30' — không chặn thì mỗi vòng một tin).</summary>
    internal static readonly TimeSpan NguongCanhBaoDiaChi = TimeSpan.FromMinutes(60);

    // Mốc gửi cảnh báo địa chỉ GẦN NHẤT theo TÀI KHOẢN. TĨNH: mỗi vòng dựng một phiên cầu nối mới nên mốc phải
    // sống ngoài phiên (suốt lần chạy app này). Concurrent: nhiều tài khoản chạy song song trên cùng máy.
    private static readonly ConcurrentDictionary<long, DateTime> _mocCanhBaoDiaChi = new();

    /// <summary>HÀM THUẦN (test được): có nên GỬI cảnh báo lúc <paramref name="bayGio"/> không — chưa từng gửi
    /// (<paramref name="mocGanNhat"/> null) hoặc đã qua <paramref name="nguong"/> kể từ lần gửi gần nhất.</summary>
    internal static bool CoNenGuiCanhBao(DateTime? mocGanNhat, DateTime bayGio, TimeSpan nguong)
        => mocGanNhat is null || bayGio - mocGanNhat.Value >= nguong;

    /// <summary>
    /// Cảnh báo ra kênh ngoài (Slack/Discord/Telegram) khi có shop bị BỎ QUA vì KHÔNG đặt được địa chỉ lấy hàng —
    /// y pattern <see cref="StartNotifyInBackground"/>: fire-and-forget, nuốt mọi exception, KHÔNG nằm trên đường
    /// quyết định vòng (shop đã bỏ qua trong <see cref="OrdersBridgeSession"/> trước khi hàm này được gọi; webhook
    /// trống / mạng hỏng chỉ làm mất TIN, không làm app đổi hành vi).
    /// <para>
    /// Chống spam <see cref="NguongCanhBaoDiaChi"/> theo tài khoản — nhưng MỌI trường hợp không gửi đều GHI LOG:
    /// im lặng hoàn toàn sẽ khiến người trực tưởng đã hết lỗi. <b>Mốc chỉ được GIỮ khi ít nhất MỘT kênh đã nhận
    /// tin</b>; mọi lối ra không-gửi-được đều nhả mốc để vòng sau báo lại (Hub 502 đúng lúc + chưa cấu hình
    /// webhook local từng làm câm 60' dù chưa ai được báo).
    /// </para>
    /// </summary>
    public void StartCanhBaoDiaChiInBackground(string? tenShop, string tinh, Action<string> log, CancellationToken ct)
    {
        var bayGio = DateTime.Now;
        DateTime? mocGanNhat = _mocCanhBaoDiaChi.TryGetValue(_accountId, out var m) ? m : null;
        if (!CoNenGuiCanhBao(mocGanNhat, bayGio, NguongCanhBaoDiaChi))
        {
            log($"Cảnh báo địa chỉ: đã báo lúc {mocGanNhat!.Value:HH:mm}, không gửi lại trong {NguongCanhBaoDiaChi.TotalMinutes:0}' — lỗi VẪN còn, shop bị bỏ qua, vòng vẫn chạy shop khác.");
            return;
        }

        // Đánh dấu mốc TRƯỚC khi gửi: hai vòng lỗi sát nhau không cùng bắn tin.
        _mocCanhBaoDiaChi[_accountId] = bayGio;

        var tenTaiKhoan = _services.Accounts.GetById(_accountId)?.Email;
        if (string.IsNullOrWhiteSpace(tenTaiKhoan))
        {
            tenTaiKhoan = $"TK {_accountId}";
        }
        var tenMay = Environment.MachineName;

        _ = Task.Run(async () =>
        {
            try
            {
                // Ưu tiên Hub quyết định gửi; chỉ Slack local khi chưa nối Hub / báo Hub thất bại.
                var report = _services.ReportAppAlertToHub;
                if (report is not null)
                {
                    var okHub = await report(
                        "khong_dat_duoc_dia_chi",
                        tenTaiKhoan,
                        tenShop,
                        tinh,
                        tenMay,
                        ct).ConfigureAwait(false);
                    if (okHub)
                    {
                        log("Cảnh báo địa chỉ: đã báo lên Hub (Hub gửi webhook lỗi app).");
                        return;
                    }
                    log("Cảnh báo địa chỉ: Hub chưa nhận — thử webhook local (fallback).");
                }

                var url = _services.Settings.GetNotifyWebhookUrlLoiApp();
                if (string.IsNullOrWhiteSpace(url))
                {
                    // KHÔNG kênh nào nhận được tin → NHẢ mốc: giữ mốc ở đây là câm 60' dù chưa ai được báo.
                    // Quy tắc: mốc chỉ được giữ khi ÍT NHẤT MỘT kênh đã nhận (xem hai nhánh TryRemove bên dưới).
                    _mocCanhBaoDiaChi.TryRemove(new KeyValuePair<long, DateTime>(_accountId, bayGio));
                    log("Cảnh báo địa chỉ: chưa cấu hình webhook lỗi app (Hub hoặc Cài đặt local) — không gửi được tin ra ngoài; shop vẫn bị bỏ qua, vòng vẫn chạy shop khác.");
                    return;
                }

                var text = OrderNotifyService.TaoTinNhanLoiDiaChi(tenTaiKhoan, tenShop ?? string.Empty, tinh, tenMay, bayGio);
                var ok = await _services.Notify.SendAsync(url, text, log, ct).ConfigureAwait(false);
                if (ok)
                {
                    log($"Cảnh báo địa chỉ: đã báo ra {OrderNotifyService.NhanDienKenh(url)} (local).");
                }
                else
                {
                    _mocCanhBaoDiaChi.TryRemove(new KeyValuePair<long, DateTime>(_accountId, bayGio));
                    log("Cảnh báo địa chỉ: gửi KHÔNG thành công — sẽ báo lại ở vòng sau (shop bị bỏ qua, vòng vẫn chạy shop khác).");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _mocCanhBaoDiaChi.TryRemove(new KeyValuePair<long, DateTime>(_accountId, bayGio));
                log("Cảnh báo địa chỉ: lỗi — " + ex.ToString());
            }
        }, CancellationToken.None);
    }

    /// <summary>HÀM THUẦN: tách danh sách shop từ <c>PickupFailedShop</c> (có thể nối bằng <c>", "</c>).</summary>
    internal static IReadOnlyList<string> TachTenShopLoiDiaChi(string? pickupFailedShop)
    {
        if (string.IsNullOrWhiteSpace(pickupFailedShop))
        {
            return ["(không rõ shop)"];
        }

        var parts = pickupFailedShop.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parts.Count > 0 ? parts : ["(không rõ shop)"];
    }

    /// <summary>
    /// Ghi banner bền trên tab Kết quả (mỗi shop một dòng): upsert local ngay + fire-and-forget Hub;
    /// rồi <see cref="AppServices.RaiseAddressAlertsChanged"/>. Nuốt lỗi Hub — local vẫn đúng khi offline.
    /// </summary>
    public void GhiBannerLoiDiaChi(string? pickupFailedShop, string tinh, Action<string> log, CancellationToken ct)
    {
        var shops = TachTenShopLoiDiaChi(pickupFailedShop);
        var occurredAt = DateTimeOffset.UtcNow;
        foreach (var shop in shops)
        {
            try
            {
                _services.PickupAlerts.Upsert(_accountId, shop, tinh);
            }
            catch (Exception ex)
            {
                log("Banner địa chỉ (local): lỗi ghi — " + ex.ToString());
            }
        }

        _services.RaiseAddressAlertsChanged(_accountId);

        var accountLogin = _services.Accounts.GetById(_accountId)?.Email?.Trim() ?? "";
        var upsertHub = _services.UpsertPickupAlertToHub;
        if (upsertHub is null || accountLogin.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var shop in shops)
                {
                    var ok = await PickupAlertHubGate.RunAsync(accountLogin, shop, () =>
                        upsertHub(accountLogin, shop, tinh, occurredAt, ct)).ConfigureAwait(false);
                    if (ok)
                    {
                        log($"Banner địa chỉ: đã đồng bộ Hub shop {shop}.");
                    }
                    else
                    {
                        log($"Banner địa chỉ: Hub chưa nhận shop {shop} — giữ local, sẽ kéo/đẩy lại sau.");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log("Banner địa chỉ (Hub): lỗi — " + ex.ToString());
            }
        }, CancellationToken.None);
    }

    /// <summary>HÀM THUẦN (test được): client có tự gửi tin notify local không. KHÔNG gửi khi đã nối Hub
    /// (<paramref name="daNoiHub"/> — Hub bắn tin sau <c>orders/push</c>, gửi nữa là người trực nhận hai tin),
    /// khi URL trống, hoặc khi chẳng có mục nào để báo. Máy chạy ĐỘC LẬP (chưa nối Hub) vẫn phải tự gửi.</summary>
    internal static bool CoNenGuiNotifyLocal(bool daNoiHub, string? url, int soMuc)
        => !daNoiHub && !string.IsNullOrWhiteSpace(url) && soMuc > 0;

    /// <summary>Kind của tin "có đơn trả hàng" gửi lên Hub (<c>/api/orders/app-alert</c>) — Hub nhận Kind này rồi
    /// bắn webhook. Hằng để hai đầu không lệch chuỗi.</summary>
    internal const string KindDonTra = "don_tra";

    /// <summary>HÀM THUẦN (test được): gói các cặp <c>(mã đơn, mã yêu cầu)</c> vừa ghi thành một dòng
    /// <c>Detail</c> cho Hub — <c>"SN1=CODE1; SN2=CODE2"</c>. Cặp thiếu vế nào bị bỏ (không gửi rác).</summary>
    internal static string MoTaCapDonTra(IEnumerable<(string OrderSn, string Code)>? cap)
        => string.Join("; ", (cap ?? Array.Empty<(string OrderSn, string Code)>())
            .Where(c => !string.IsNullOrWhiteSpace(c.OrderSn) && !string.IsNullOrWhiteSpace(c.Code))
            .Select(c => $"{c.OrderSn.Trim()}={c.Code.Trim()}"));

    /// <summary>
    /// HÀM THUẦN (test được): các cặp thuộc đơn <b>ĐÃ BỊ DỌN</b> khỏi bảng <c>orders</c> = có trong
    /// <paramref name="capMoi"/> (kho mã) mà KHÔNG có trong <paramref name="capDonConSong"/>
    /// (<c>SetReturnRequestCodes</c> vừa ghi được ⇒ đơn còn sống, sẽ theo <c>orders/push</c> lên Hub).
    /// <para>
    /// Đây là phần Hub KHÔNG tự biết được. Đơn CÒN SỐNG để Hub tự bắn tin qua <c>ReturnCodeChangedItems</c> của
    /// <c>orders/push</c> — client báo thêm là người trực nhận HAI tin cho cùng một mã.
    /// </para>
    /// <para>Đối chiếu theo <b>mã đơn</b> (một đơn một mã yêu cầu; hai đường nhận cùng một lô cặp).</para>
    /// </summary>
    internal static IReadOnlyList<(string OrderSn, string Code)> LocCapDonDaDon(
        IReadOnlyList<(string OrderSn, string Code)>? capMoi,
        IReadOnlyList<(string OrderSn, string Code)>? capDonConSong)
    {
        var moi = capMoi ?? Array.Empty<(string OrderSn, string Code)>();
        var conSong = new HashSet<string>(
            (capDonConSong ?? Array.Empty<(string OrderSn, string Code)>())
                .Select(c => (c.OrderSn ?? string.Empty).Trim()),
            StringComparer.Ordinal);
        return moi.Where(c => !conSong.Contains((c.OrderSn ?? string.Empty).Trim())).ToList();
    }

    /// <summary>
    /// <b>Lưu MÃ YÊU CẦU TRẢ HÀNG</b> vừa đọc được của shop đang chạy (cầu nối gọi ở bước CUỐI flow shop) rồi trả
    /// chuỗi tóm tắt để phiên ghi nhật ký.
    /// <para>
    /// GHI VÀO CẢ HAI: <c>return_codes</c> là nguồn sự thật MỚI (sống độc lập với vòng đời đơn — đơn đã dọn vẫn
    /// đẩy được mã lên GSheet), còn <c>orders.return_request_code</c> vẫn ghi để lưới app + hub hiển thị được với
    /// đơn CÒN sống. Bảng riêng phải ghi TRƯỚC: nó không bao giờ trượt vì "đơn không còn trong DB", nên dù đường
    /// kia bỏ hết mã thì mã vẫn được giữ.
    /// </para>
    /// </summary>
    public string LuuMaTraHang(IReadOnlyList<YeuCauTraHang> cap, Action<string> log, CancellationToken ct)
    {
        var pairs = cap.Select(c => (c.MaDon, c.MaYeuCau)).ToList();
        var kqMa = _services.ReturnCodes.LuuMaTraHang(
            _accountId, pairs, _currentShopLogin, DateTime.UtcNow);
        var kq = _services.Orders.SetReturnRequestCodes(_accountId, pairs);
        if (kq.DaGhi > 0)
        {
            _services.RaiseOrdersChanged();
        }
        // Notify theo KHO MÃ (`return_codes`), KHÔNG theo `kq.CapDaGhi`: phần lớn mã trả thuộc đơn
        // ĐÃ bị NenXoaDonKetThuc dọn khỏi `orders` nên đường kia rỗng — chính là lý do bảng riêng
        // tồn tại. Chỉ các cặp VỪA thêm/đổi mới được báo (chống báo lại cả lô quét). `kq.CapDaGhi`
        // đi kèm để nhánh Hub trừ ra phần đơn CÒN SỐNG (Hub tự bắn theo orders/push — xem
        // LocCapDonDaDon), khỏi hai tin một mã.
        if (kqMa.DaGhi > 0)
        {
            StartNotifyDonTraInBackground(kqMa.CapMoi, kq.CapDaGhi, _currentShopLogin, log, ct);
        }
        return $"{kq.DaGhi} đơn ghi mã mới, {kq.KhongDoi} đơn giữ nguyên, "
            + $"{kq.KhongCoDon} mã không khớp đơn nào trong app (đơn đã dọn — mã VẪN giữ ở kho "
            + "mã trả hàng, lượt đẩy sau vẫn điền lên Google Sheet).";
    }

    /// <summary>
    /// Báo "có đơn trả hàng" — fire-and-forget sau khi kho mã (<c>return_codes</c>) ghi được mã MỚI. Nhận đúng
    /// <see cref="KetQuaLuuMaTraHang.CapMoi"/> (cặp vừa thêm/đổi), KHÔNG phải cả lô quét.
    /// <list type="bullet">
    /// <item>Đã nối Hub → đẩy lên Hub (<see cref="KindDonTra"/>) <b>CHỈ phần đơn đã bị dọn</b>
    /// (<see cref="LocCapDonDaDon"/>); đơn còn sống để Hub tự bắn theo <c>orders/push</c>, tránh hai tin một mã.
    /// KHÔNG gửi local nữa.</item>
    /// <item>Chạy ĐỘC LẬP → tự gửi webhook "đơn trả" local cho TOÀN BỘ cặp mới (không có nguồn tin nào khác để
    /// trùng); URL trống → im lặng.</item>
    /// </list>
    /// <para><b>Vì sao không dùng thẳng <c>OrdersRepository.ReturnCodeSaveResult.CapDaGhi</c> làm nguồn:</b> nó
    /// chỉ có đơn CÒN trong <c>orders</c>, mà mã trả hàng thường tới sau khi đơn đã bị dọn ⇒ notify im lặng gần
    /// như luôn. Ở đây nó chỉ đóng vai "phần Hub đã biết rồi".</para>
    /// </summary>
    private void StartNotifyDonTraInBackground(
        IReadOnlyList<(string OrderSn, string Code)> capMoi,
        IReadOnlyList<(string OrderSn, string Code)> capDonConSong,
        string? shopLogin,
        Action<string> log,
        CancellationToken ct)
    {
        var pairs = (capMoi ?? Array.Empty<(string OrderSn, string Code)>()).ToList();
        if (pairs.Count == 0)
        {
            return;
        }

        var url = _services.Settings.GetNotifyWebhookUrlDonTra();
        var report = _services.ReportAppAlertToHub;
        // Luật thật nằm ở CoNenGuiNotifyLocal; vế url CHỈ để compiler thấy nó đã non-null ở nhánh local.
        var guiLocal = CoNenGuiNotifyLocal(_services.PushOrdersToHub is not null, url, pairs.Count)
                       && !string.IsNullOrWhiteSpace(url);

        // Đã nối Hub: chỉ báo phần Hub không tự biết (đơn đã bị dọn khỏi `orders`).
        var capHub = report is null
            ? Array.Empty<(string OrderSn, string Code)>()
            : LocCapDonDaDon(pairs, capDonConSong);
        if (report is not null && capHub.Count == 0)
        {
            log($"Notify đơn trả: {pairs.Count} mã mới đều thuộc đơn CÒN trong app — để Hub bắn tin theo lượt đẩy đơn (không báo trùng).");
            return;
        }
        if (report is null && !guiLocal)
        {
            return;
        }

        var tenTaiKhoan = _services.Accounts.GetById(_accountId)?.Email;
        if (string.IsNullOrWhiteSpace(tenTaiKhoan))
        {
            tenTaiKhoan = $"TK {_accountId}";
        }
        var tenMay = Environment.MachineName;
        var luc = DateTime.Now;

        _ = Task.Run(async () =>
        {
            try
            {
                if (report is not null)
                {
                    var okHub = await report(
                        KindDonTra,
                        tenTaiKhoan,
                        shopLogin,
                        MoTaCapDonTra(capHub),
                        tenMay,
                        ct).ConfigureAwait(false);
                    log(okHub
                        ? $"Notify đơn trả: đã báo {capHub.Count} mã (đơn đã dọn) lên Hub (Hub gửi tin)."
                        : $"Notify đơn trả: Hub chưa nhận {capHub.Count} mã — tin này không gửi được (mã VẪN nằm trong kho, vẫn lên Google Sheet).");
                    return;
                }

                var text = OrderNotifyService.TaoTinNhanDonTra(tenTaiKhoan, pairs, luc);
                var ok = await _services.Notify.SendAsync(url!, text, log, ct).ConfigureAwait(false);
                if (ok)
                {
                    log($"Notify: đã báo {pairs.Count} đơn trả hàng ({OrderNotifyService.NhanDienKenh(url!)}).");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                log("Notify đơn trả: lỗi — " + ex.ToString());
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// HÀM THUẦN (test được) quyết định một đơn KẾT THÚC có được XÓA khỏi app chưa. Trả true khi:
    /// <list type="bullet">
    /// <item>đơn KẾT THÚC — <c>LaDonHuy</c> (Đã hủy) hoặc <c>LaDaGiaoDaBan</c> (Đã giao); VÀ</item>
    /// <item><paramref name="gsheetSettled"/> — đã ghi sheet xong / không cần ghi / URL trống; VÀ</item>
    /// <item>KHÔNG (Đã giao + có SKU + chưa đếm "Đã bán") — nghĩa là đếm sold còn NULL thì GIỮ để lượt sau +1
    /// (xóa sớm là mất đếm); VÀ</item>
    /// <item>KHÔNG (hub bật + chưa đẩy hub) — hub đang nhận đơn mà đơn chưa <c>hub_synced_at</c> thì GIỮ, kẻo
    /// hub mất đơn.</item>
    /// <item>KHÔNG <paramref name="coPhieuLocalChuaDayHub"/> — còn file phiếu local HỢP LỆ chưa đẩy lên hub (hub
    /// đang bật) thì GIỮ, đợi phiếu lên hub xong (đẩy xong lượt sau mới dọn).</item>
    /// </list>
    /// Đơn trung gian (chưa kết thúc) hoặc chưa settled → false (GIỮ). Nghi ngờ thì GIỮ — đơn thừa vô hại.
    /// <paramref name="coPhieuLocalChuaDayHub"/> do caller tính: hub bật + <c>!p.DaDayPhieuHub</c> + file phiếu
    /// local hợp lệ tồn tại. File local KHÔNG tồn tại → false (không giữ vì phiếu, như cũ).
    /// </summary>
    internal static bool NenXoaDonKetThuc(GsheetPendingOrder p, bool gsheetSettled, bool hubHookActive, bool coPhieuLocalChuaDayHub)
    {
        var terminal = ShopeeShippingNav.LaDonHuy(p.Status, p.StatusDescription, p.CancelReason)
            || ShopeeShippingNav.LaDaGiaoDaBan(p.Status);
        return terminal
            && gsheetSettled
            && (!ShopeeShippingNav.LaDaGiaoDaBan(p.Status) || string.IsNullOrWhiteSpace(p.Sku) || p.DaDemDaBan)
            && (!hubHookActive || p.DaDayHub)
            && !coPhieuLocalChuaDayHub;
    }
}
