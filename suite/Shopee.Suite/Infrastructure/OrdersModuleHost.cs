using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shopee.Core.Coordination;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Glue tĩnh cắm module "Xử lý đơn Shopee" (app đơn hàng đã module hóa — phase 1b) vào shell suite.
/// <see cref="TryCreate"/> mở SQLite + migration của app đơn hàng và dựng <see cref="MainViewModel"/>;
/// <see cref="StopAsync"/> kill hết phiên Brave khi thoát app.
/// Giữ tối thiểu để nếu init hỏng thì suite vẫn chạy (chỉ thiếu module đơn hàng).
/// </summary>
public static class OrdersModuleHost
{
    /// <summary>Bộ dịch vụ của app đơn hàng (DB + repository + phiên). null nếu chưa/không khởi tạo được.</summary>
    public static AppServices? Services { get; private set; }

    // Chống dừng đúp: ShutdownRequested và UpdateService.PrepareShutdownAsync đều gọi StopAsync. Lệnh dừng
    // bên dưới vốn idempotent (StopAllAsync thao tác list rỗng), cờ này chỉ để khỏi lặp công vô ích.
    private static bool _stopped;

    /// <summary>VÒNG CHỜ ĐẨY chạy theo vòng đời APP (không theo phiên) — đẩy bù hàng tồn lên Hub/GSheet/đếm
    /// "Đã bán" mỗi ~2 phút. Giữ tham chiếu static để Dispose khi thoát (và để GC không gom).</summary>
    private static HubOutboxWorker? _outboxWorker;

    /// <summary>
    /// Khởi tạo bộ dịch vụ đơn hàng (ctor <see cref="AppServices"/> mở SQLite <c>%APPDATA%\XuLyDonShopee\app.db</c>
    /// + chạy migration) và dựng ViewModel gốc của module. Lỗi (đĩa/khóa DB…) → ghi log, trả null để suite vẫn boot.
    /// </summary>
    public static MainViewModel? TryCreate()
    {
        try
        {
            Services = new AppServices();
            WireHubPush(Services);
            WireIncrementSoldBySku(Services);
            WireHubSlipPush(Services);
            WireGsheetConfig(Services);
            WireHubOrdersRead(Services);
            WirePrepareStatsRead(Services);
            var vm = new MainViewModel(Services);
            // Vòng chờ đẩy: dựng SAU khi AppServices (DB + migration) và các hook hub đã sẵn sàng; tự hoãn ~15s
            // rồi chạy lượt đầu (bắt đúng ý "khi client chạy, còn vòng chờ thì đẩy") và lặp mỗi ~2 phút.
            _outboxWorker = new HubOutboxWorker(Services);
            _outboxWorker.Start();
            return vm;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[OrdersModuleHost] Không khởi tạo được module đơn hàng: " + ex);
            Services = null;
            return null;
        }
    }

    /// <summary>
    /// RÓT hook đẩy đơn lên hub vào bộ dịch vụ module Đơn hàng (module không tham chiếu <c>Shopee.Core</c> nên
    /// KHÔNG tự biết hub — shell suite thấy cả hai làm cầu nối). Hook được phiên gọi CHẠY NỀN sau mỗi Sync:
    /// hub chưa kết nối → trả false (đơn giữ CHƯA đánh dấu, thử lại lượt sau); ngược lại NHÓM lô đơn theo SHOP
    /// (mỗi shop 1 request) rồi POST, trả true CHỈ khi MỌI nhóm OK (nhóm nào null → false, cả lô đẩy lại lượt sau).
    /// Nuốt mọi lỗi (log <c>Trace</c>) trả false — trừ hủy CHỦ ĐỘNG (ct) cho xuyên để phiên xử như dừng. Shop trên
    /// hub khóa theo <see cref="OrdersPushRequest.ShopUsername"/> = <c>shop_login</c> của đơn (đơn cũ thiếu shop_login
    /// → fallback tên đăng nhập subaccount qua <see cref="ResolveShopUsername"/>).
    /// </summary>
    private static void WireHubPush(AppServices services)
    {
        services.PushOrdersToHub = async (accountId, orders, ct) =>
        {
            try
            {
                // Hub chưa kết nối (chưa cấu hình / offline) → KHÔNG đánh dấu đơn, để lượt sync sau đẩy lại.
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null)
                {
                    return false;
                }

                var acc = services.Accounts.GetById(accountId);

                // NHÓM theo shop (mô hình subaccount → nhiều shop): mỗi shop 1 request để hub keyed đúng shop_id,
                // KHÔNG dồn mọi shop vào 1 "shop" subaccount. Đơn thiếu shop_login (đơn cũ) → fallback username subaccount.
                var allOk = true;
                foreach (var group in orders.GroupBy(o =>
                    string.IsNullOrWhiteSpace(o.ShopLogin) ? ResolveShopUsername(acc, accountId) : o.ShopLogin.Trim()))
                {
                    var shopUsername = group.Key;
                    var req = new OrdersPushRequest
                    {
                        ShopUsername = shopUsername,
                        ShopName = shopUsername,
                        Orders = group.Select(ToPushItem).ToList(),
                    };

                    var res = await CoordinationRuntime.Client.PushOrdersAsync(req, ct).ConfigureAwait(false);
                    if (res is null)
                    {
                        allOk = false; // nhóm này hub KHÔNG nhận → cả lô CHƯA đánh dấu (phiên mark theo lô), lượt sau đẩy lại
                    }
                }
                return allOk; // MỌI nhóm OK → phiên đánh dấu hub_synced_at
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // hủy CHỦ ĐỘNG (dừng phiên) → cho xuyên để AccountSession xử như hủy
            }
            catch (Exception ex)
            {
                // Gồm cả timeout tunnel (TaskCanceledException khi ct CHƯA hủy) → coi như hub lỗi, thử lại lượt sau.
                Trace.WriteLine("[OrdersModuleHost] Đẩy đơn lên hub lỗi: " + ex.Message);
                return false;
            }
        };
    }

    /// <summary>
    /// RÓT hook +1 "Đã bán" theo SKU vào bộ dịch vụ module Đơn hàng (mẫu <see cref="WireHubPush"/>). Hook được phiên
    /// gọi CHẠY NỀN sau mỗi Sync với danh sách SKU các đơn VỪA chuyển sang đã-giao: hub chưa kết nối → trả false
    /// (đơn giữ CHƯA đánh cờ, thử lại lượt sau); ngược lại gọi <c>MarkProductsSoldBySkuAsync</c> (+1 mọi dòng khớp
    /// SKU tuyệt đối, mọi shop), 2xx = true. Nuốt mọi lỗi (log <c>Trace</c>) trả false — trừ hủy CHỦ ĐỘNG (ct) cho
    /// xuyên để phiên xử như dừng.
    /// </summary>
    private static void WireIncrementSoldBySku(AppServices services)
    {
        services.IncrementSoldBySku = async (skus, ct) =>
        {
            try
            {
                // Hub chưa kết nối (chưa cấu hình / offline) → KHÔNG đánh cờ đơn, để lượt sync sau +1 lại.
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null)
                {
                    return false;
                }
                return await CoordinationRuntime.Client.MarkProductsSoldBySkuAsync(skus, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // hủy CHỦ ĐỘNG (dừng phiên) → cho xuyên để AccountSession xử như hủy
            }
            catch (Exception ex)
            {
                // Gồm cả timeout tunnel (TaskCanceledException khi ct CHƯA hủy) → coi như hub lỗi, thử lại lượt sau.
                Trace.WriteLine("[OrdersModuleHost] +1 Đã bán theo SKU lên hub lỗi: " + ex.Message);
                return false;
            }
        };
    }

    /// <summary>
    /// RÓT hook đẩy FILE PHIẾU lên hub vào bộ dịch vụ module Đơn hàng (mẫu <see cref="WireHubPush"/>). Hook được
    /// phiên gọi CHẠY NỀN sau <c>StartHubPushInBackground</c>: hub chưa kết nối → trả null (không mark, thử lại lượt
    /// sau); ngược lại NHÓM lô <c>(OrderSn, FileBase64)</c> theo SHOP (tra <c>shop_login</c> từng đơn, mỗi shop 1
    /// request) rồi POST. TRẢ VỀ danh sách <c>order_sn</c> hub ĐÃ LƯU = HỢP các nhóm (mỗi nhóm: lô gửi − <c>missing</c>
    /// − <c>errors</c>); nhóm nào null (hub lỗi nhóm đó) → đơn nhóm ấy KHÔNG mark, lượt sau đẩy lại. Toàn bộ lỗi/hub
    /// chưa kết nối → null (hub cũ 404 / offline / timeout) → phiên KHÔNG mark. Nuốt mọi lỗi (log <c>Trace</c>) trả
    /// null — trừ hủy CHỦ ĐỘNG (ct) cho xuyên để phiên xử như dừng.
    /// </summary>
    private static void WireHubSlipPush(AppServices services)
    {
        services.PushOrderSlipsToHub = async (accountId, slips, ct) =>
        {
            try
            {
                // Hub chưa kết nối (chưa cấu hình / offline) → KHÔNG mark, để lượt sync sau đẩy lại.
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null)
                {
                    return null;
                }

                var acc = services.Accounts.GetById(accountId);

                // Phiếu chỉ mang OrderSn → tra shop_login từng đơn để NHÓM theo SHOP (mỗi shop 1 request), khớp shop
                // trên hub như /orders/push. Đơn thiếu shop_login (đơn cũ) → fallback username subaccount.
                var map = services.Orders.GetShopLoginsByOrderSns(accountId, slips.Select(s => s.OrderSn));

                // ĐÃ LƯU = HỢP các nhóm (mỗi nhóm: lô gửi − missing − errors). Nhóm trả null (hub lỗi nhóm đó) → không
                // mark nhóm đó, lượt sau đẩy lại. Đơn missing (chưa lên hub) / lỗi (base64/PDF) KHÔNG mark.
                var saved = new List<string>();
                var groups = slips.GroupBy(s =>
                {
                    var shopLogin = map.TryGetValue(s.OrderSn, out var sl) ? sl : null;
                    return string.IsNullOrWhiteSpace(shopLogin) ? ResolveShopUsername(acc, accountId) : shopLogin.Trim();
                });

                foreach (var group in groups)
                {
                    var shopUsername = group.Key;
                    var batch = group.ToList();
                    var req = new OrdersSlipPushRequest
                    {
                        ShopUsername = shopUsername,
                        ShopName = shopUsername,
                        Slips = batch.Select(s => new SlipPushItem { OrderSn = s.OrderSn, FileBase64 = s.FileBase64 }).ToList(),
                    };

                    var res = await CoordinationRuntime.Client.PushOrderSlipsAsync(req, ct).ConfigureAwait(false);
                    if (res is null)
                    {
                        continue; // hub lỗi NHÓM này → không mark, lượt sau thử lại
                    }

                    var notSaved = new HashSet<string>(StringComparer.Ordinal);
                    if (res.Missing is not null)
                    {
                        foreach (var m in res.Missing) notSaved.Add(m);
                    }
                    if (res.Errors is not null)
                    {
                        foreach (var e in res.Errors) notSaved.Add(e.OrderSn);
                    }
                    saved.AddRange(batch.Where(s => !notSaved.Contains(s.OrderSn)).Select(s => s.OrderSn));
                }
                return saved;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // hủy CHỦ ĐỘNG (dừng phiên) → cho xuyên để AccountSession xử như hủy
            }
            catch (Exception ex)
            {
                // Gồm cả hub cũ 404 (EnsureSuccessStatusCode ném) + timeout tunnel → coi như hub lỗi, thử lại lượt sau.
                Trace.WriteLine("[OrdersModuleHost] Đẩy phiếu lên hub lỗi: " + ex.Message);
                return null;
            }
        };
    }

    /// <summary>
    /// RÓT 2 hook ĐỌC đơn toàn hệ thống từ hub vào bộ dịch vụ module Đơn hàng (mẫu <see cref="WireHubPush"/>) —
    /// nguồn của màn "Đơn toàn hệ thống" (CHỈ XEM). Hub chưa kết nối → trả null (màn báo "chưa kết nối Hub");
    /// hub lỗi/offline → <c>HubClient</c> đã tự nuốt thành null (màn báo "Hub không phản hồi"). Nuốt mọi lỗi
    /// (log <c>Trace</c>) trả null — trừ hủy CHỦ ĐỘNG (ct: người dùng đổi bộ lọc liên tục) cho xuyên để màn bỏ
    /// lượt cũ. <b>KHÔNG</b> ghi gì vào CSDL module: đơn lấy về chỉ đi thẳng ra lưới.
    /// </summary>
    private static void WireHubOrdersRead(AppServices services)
    {
        services.QueryHubOrders = async (query, ct) =>
        {
            try
            {
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null)
                {
                    return null; // hub chưa kết nối → màn hiện "Máy này chưa kết nối Hub"
                }

                var page = await CoordinationRuntime.Client.QueryOrdersAsync(
                    query.ShopId, query.Status, query.Search, query.Page, query.PageSize, ct).ConfigureAwait(false);
                if (page is null)
                {
                    return null; // hub không phản hồi / hub cũ chưa có route
                }

                return new HubOrdersResult(
                    page.Items.Select(ToHubOrderView).ToList(), page.Total, page.Page, page.PageSize);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // hủy CHỦ ĐỘNG (đổi bộ lọc) → cho xuyên để màn bỏ lượt cũ
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Đọc đơn từ hub lỗi: " + ex.Message);
                return null;
            }
        };

        services.ListHubShops = async ct =>
        {
            try
            {
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null)
                {
                    return null;
                }

                var shops = await CoordinationRuntime.Client.ListShopsAsync(ct).ConfigureAwait(false);
                // Tên hiển thị: Name của hub; hub cũ để trống Name → lùi về Username rồi "shop #id".
                return shops?.Select(s => (
                    Id: s.Id,
                    Name: !string.IsNullOrWhiteSpace(s.Name) ? s.Name
                        : (!string.IsNullOrWhiteSpace(s.Username) ? s.Username! : $"shop #{s.Id}"))).ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Đọc danh sách shop từ hub lỗi: " + ex.Message);
                return null;
            }
        };
    }

    /// <summary>
    /// RÓT hook đọc SỐ ĐƠN "chuẩn bị hàng" chung toàn hệ thống (tab "Kết quả" của màn Tài khoản) — mẫu
    /// <see cref="WireHubOrdersRead"/>. Hub trả list <c>(shopUsername, count)</c> → đổi thành map
    /// <c>shop_login → count</c> (hub khoá shop theo ĐÚNG <c>shop_login</c> client đẩy lên nên tra thẳng được).
    /// <para><b>BẪY null vs rỗng:</b> hub chưa kết nối / lỗi / hub CŨ chưa có route → trả <c>null</c> = "không hỏi
    /// được" (màn GIỮ số cục bộ). TUYỆT ĐỐI không trả map rỗng ở các ca này — rỗng nghĩa là "hub bảo 0 đơn" và sẽ
    /// làm lưới về 0 mỗi lần rớt mạng.</para>
    /// Nuốt mọi lỗi (log <c>Trace</c>) trả null — trừ hủy CHỦ ĐỘNG (ct: người dùng đổi ngày/đổi tài khoản liên tục)
    /// cho xuyên để màn bỏ lượt cũ.
    /// </summary>
    private static void WirePrepareStatsRead(AppServices services)
    {
        services.QueryPrepareStats = async (day, ct) =>
        {
            try
            {
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null)
                {
                    return null; // hub chưa kết nối → "không hỏi được", KHÔNG phải "0 đơn"
                }

                var stats = await CoordinationRuntime.Client.GetPrepareStatsAsync(day, ct).ConfigureAwait(false);
                if (stats is null)
                {
                    return null; // hub không phản hồi / hub cũ chưa có route
                }

                // Khoá map = shop_login, so khớp KHÔNG phân biệt hoa/thường: nhãn shop giữa account_shops của máy
                // và shops.username trên hub có thể lệch HOA/thường → tra Ordinal sẽ ra 0 một cách LẶNG. Indexer
                // chứ không ToDictionary để hub trả dòng trùng khoá cũng không ném.
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in stats)
                {
                    if (!string.IsNullOrWhiteSpace(s.ShopUsername))
                    {
                        map[s.ShopUsername.Trim()] = s.Count;
                    }
                }
                return map;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // hủy CHỦ ĐỘNG (lượt sau đè lượt trước) → cho xuyên để màn bỏ lượt cũ
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Đọc số chuẩn bị hàng từ hub lỗi: " + ex.Message);
                return null;
            }
        };
    }

    /// <summary>Map một <see cref="HubOrderItem"/> (DTO hub) sang <see cref="HubOrderView"/> (kiểu của module Đơn
    /// hàng) — map TAY từng field như <see cref="ToPushItem"/> ở chiều ngược lại, để đổi tên field một bên là gãy
    /// build chứ không âm thầm làm rỗng cột trên lưới.</summary>
    private static HubOrderView ToHubOrderView(HubOrderItem o) => new()
    {
        ShopId = o.ShopId,
        OrderSn = o.OrderSn,
        BuyerUsername = o.BuyerUsername,
        ItemCount = o.ItemCount,
        ItemSummary = o.ItemSummary,
        Sku = o.Sku,
        TotalPrice = o.TotalPrice,
        TotalPriceText = o.TotalPriceText,
        FinalAmount = o.FinalAmount,
        FinalAmountText = o.FinalAmountText,
        PaymentMethod = o.PaymentMethod,
        Status = o.Status,
        StatusDescription = o.StatusDescription,
        CancelReason = o.CancelReason,
        Carrier = o.Carrier,
        TrackingNumber = o.TrackingNumber,
        SyncedAt = o.SyncedAt,
    };

    /// <summary>Nhịp kéo cấu hình GSheet dùng chung từ hub về. Khớp TTL 60s của <see cref="HubOrdersConfig"/> →
    /// máy client thấy cấu hình admin vừa đổi trong ~1 phút mà KHÔNG cần khởi động lại app.</summary>
    private static readonly TimeSpan GsheetPullEvery = TimeSpan.FromSeconds(60);

    /// <summary>Timer kéo cấu hình GSheet — PHẢI giữ tham chiếu static, không thì Timer bị GC gom là hết nhịp.</summary>
    private static Timer? _gsheetTimer;

    /// <summary>Chốt chống chồng lấn 2 lượt kéo cấu hình GSheet (nhịp fleet 12s + timer 60s có thể cùng bắn).</summary>
    private static int _gsheetPulling;

    /// <summary>
    /// RÓT cầu nối cấu hình ĐỒNG BỘ GOOGLE SHEET dùng chung (mẫu <see cref="WireHubPush"/>), gồm 2 chiều:
    /// <list type="bullet">
    /// <item><b>Đẩy lên</b> — hook <see cref="AppServices.PushGsheetConfigToHub"/>: màn Cài đặt lưu xong thì đẩy
    /// URL + tab lên hub cho các máy khác nhận. URL TRỐNG thì KHÔNG đẩy (một máy chưa cấu hình không được xoá
    /// cấu hình của cả fleet).</item>
    /// <item><b>Kéo về</b> — <see cref="ApplyGsheetFromHubAsync"/> chạy ngay khi mở app rồi định kỳ.</item>
    /// </list>
    /// Nuốt mọi lỗi (log <c>Trace</c>) — lỗi hub KHÔNG được làm chết luồng đơn hàng.
    /// </summary>
    private static void WireGsheetConfig(AppServices services)
    {
        services.PushGsheetConfigToHub = async (url, tab, ct) =>
        {
            try
            {
                // Hub chưa kết nối (chưa cấu hình / offline) → không đẩy; màn Cài đặt báo "Hub chưa kết nối".
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null)
                {
                    return false;
                }
                if (!GsheetConfigSync.NenDayLenHub(url))
                {
                    return false; // URL local trống → KHÔNG đẩy đè hub
                }

                var ok = await CoordinationRuntime.Client.PostOrdersConfigAsync(
                    new OrdersSharedConfig { GsheetWebAppUrl = url, GsheetTabName = tab }, ct).ConfigureAwait(false);
                if (ok)
                {
                    HubOrdersConfig.Invalidate(); // lượt kéo kế hỏi hub NGAY (khỏi chờ hết TTL mới thấy bản vừa đẩy)
                }
                return ok;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // hủy CHỦ ĐỘNG → cho xuyên
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Đẩy cấu hình GSheet lên hub lỗi: " + ex.Message);
                return false;
            }
        };

        // Kéo NGAY lúc mở app (dueTime 0) rồi định kỳ. Dùng Timer chứ không chỉ dựa vào nhịp fleet vì chế độ
        // "Shopee" (chỉ module đơn hàng) KHÔNG dựng poller fleet → sẽ không có nhịp nào. Số request thật sự do
        // TTL 60s của HubOrdersConfig chặn, nên đăng ký thêm nhịp fleet 12s là vô hại (chỉ giúp nhận sớm hơn).
        _gsheetTimer = new Timer(_ => _ = ApplyGsheetFromHubAsync(services), null, TimeSpan.Zero, GsheetPullEvery);
        Coordination.Hub.Changed += () => _ = ApplyGsheetFromHubAsync(services);
    }

    /// <summary>
    /// Kéo cấu hình GSheet dùng chung từ hub và ÁP xuống CSDL module Đơn hàng — có hiệu lực NGAY từ lượt đẩy
    /// sheet kế tiếp (phiên đọc setting tươi mỗi lượt), KHÔNG cần khởi động lại app.
    /// <para><b>Các chốt an toàn (làm sai là hỏng cả fleet):</b> hub trả <c>null</c> (offline / hub cũ chưa có
    /// route) = "không biết" → GIỮ NGUYÊN bản local; hub trả bản RỖNG (chưa ai điền URL) → cũng KHÔNG đè, vì URL
    /// trống là công tắc TẮT ghi sheet (xem <see cref="GsheetConfigSync.QuyetDinhApBanHub"/>); máy HUB không tự
    /// kéo đè chính nó (y <c>HubConfigSync</c>).</para>
    /// </summary>
    private static async Task ApplyGsheetFromHubAsync(AppServices services)
    {
        if (Interlocked.Exchange(ref _gsheetPulling, 1) == 1)
        {
            return; // lượt trước chưa xong → bỏ nhịp này
        }

        try
        {
            if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null)
            {
                return; // hub chưa kết nối → không đụng cấu hình local
            }
            if (HubServerConfigStore.Shared.Current.Enabled)
            {
                return; // máy HUB: giữ bản gốc của chính nó
            }

            var cfg = await HubOrdersConfig.GetAsync().ConfigureAwait(false);
            if (cfg is null)
            {
                return; // "không biết" → giữ nguyên bản local
            }

            var quyet = GsheetConfigSync.QuyetDinhApBanHub(
                cfg.GsheetWebAppUrl, cfg.GsheetTabName,
                services.Settings.GetGsheetWebAppUrl(), services.Settings.GetGsheetTabName());
            if (!quyet.Ap)
            {
                return; // hub chưa cấu hình (KHÔNG đè) hoặc đã trùng bản local (khỏi ghi SQLite mỗi nhịp)
            }

            services.Settings.SetGsheetWebAppUrl(quyet.Url);
            services.Settings.SetGsheetTabName(quyet.Tab);
            services.Log.Append("Cấu hình", quyet.Tab.Length == 0
                ? "Đã nhận cấu hình Google Sheet từ Hub (tab: tự động theo tháng)."
                : $"Đã nhận cấu hình Google Sheet từ Hub (tab: {quyet.Tab}).");
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[OrdersModuleHost] Kéo cấu hình GSheet từ hub lỗi: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _gsheetPulling, 0);
        }
    }

    /// <summary>
    /// <see cref="OrdersPushRequest.ShopUsername"/> (KHÓA đăng ký shop trên hub) = <see cref="Account.Email"/>
    /// (tên đăng nhập người dùng nhập, đã trim); trống → <see cref="Account.Phone"/>; vẫn trống → <c>"account-{id}"</c>.
    /// </summary>
    private static string ResolveShopUsername(Account? acc, long accountId)
    {
        var email = acc?.Email?.Trim();
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email;
        }
        var phone = acc?.Phone?.Trim();
        if (!string.IsNullOrWhiteSpace(phone))
        {
            return phone;
        }
        return $"account-{accountId}";
    }

    /// <summary>Map một <see cref="SyncedOrder"/> (module Đơn hàng) sang <see cref="OrderPushItem"/> (DTO hub) —
    /// mirror field-by-field để client đẩy 1-1, khỏi lệch field.
    /// <para><see cref="OrderPushItem.PreparedDay"/> tính TẠI ĐÂY (giờ ĐỊA PHƯƠNG của máy đã chuẩn bị đơn) chứ
    /// không để hub suy từ <see cref="OrderPushItem.PreparedAt"/> — hub không biết múi giờ của máy nào.
    /// <c>PreparedAt</c> NULL → cả hai NULL (đơn arrange trước bản này; hub giữ nguyên giá trị đang có).</para></summary>
    private static OrderPushItem ToPushItem(SyncedOrder o) => new()
    {
        OrderSn = o.OrderSn,
        ShopeeOrderId = o.ShopeeOrderId,
        BuyerUsername = o.BuyerUsername,
        ItemsJson = o.ItemsJson,
        ItemCount = o.ItemCount,
        ItemSummary = o.ItemSummary,
        Sku = o.Sku,
        TotalPrice = o.TotalPrice,
        TotalPriceText = o.TotalPriceText,
        FinalAmount = o.FinalAmount,
        FinalAmountText = o.FinalAmountText,
        PaymentMethod = o.PaymentMethod,
        Status = o.Status,
        StatusDescription = o.StatusDescription,
        CancelReason = o.CancelReason,
        Channel = o.Channel,
        Carrier = o.Carrier,
        TrackingNumber = o.TrackingNumber,
        PreparedAt = o.PreparedAt?.ToString("o", CultureInfo.InvariantCulture),
        PreparedDay = o.PreparedAt?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Thoát app: dừng TẤT CẢ phiên (kill hết Brave, tránh tiến trình mồ côi giữ khóa hồ sơ). No-op khi module
    /// không khởi tạo được.
    /// </summary>
    public static async Task StopAsync()
    {
        var svc = Services;
        if (svc is null || _stopped) return;
        _stopped = true;
        try { _gsheetTimer?.Dispose(); } catch { /* bỏ qua khi thoát */ }   // dừng nhịp kéo cấu hình GSheet
        try { _outboxWorker?.Dispose(); } catch { /* bỏ qua khi thoát */ }  // dừng vòng chờ đẩy
        try { await svc.Sessions.StopAllAsync(); } catch { /* bỏ qua khi thoát */ }
    }
}
