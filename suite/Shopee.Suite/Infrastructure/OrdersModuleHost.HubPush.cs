using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Shopee.Core.Coordination;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace Shopee.Suite.Infrastructure;

// Partial của OrdersModuleHost: các hook ĐẨY dữ liệu lên hub (đơn, +1 "Đã bán" theo SKU, file phiếu) + map
// DTO client → hub. Pure move từ OrdersModuleHost.cs, KHÔNG đổi thứ tự/hành vi.
public static partial class OrdersModuleHost
{
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
                        allOk = false; // nhóm này hub KHÔNG nhận → cả lô CHƯA đánh dấu, lượt sau đẩy lại
                    }
                }
                return allOk;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // hủy CHỦ ĐỘNG → cho xuyên để AccountSession xử như hủy
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Đẩy đơn lên hub lỗi: " + ex.Message);
                return false;
            }
        };

        // Báo lỗi app lên Hub — Hub quyết định gửi webhook (không để client tự Slack khi đã nối Hub).
        services.ReportAppAlertToHub = async (kind, account, shop, detail, machine, ct) =>
        {
            try
            {
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null)
                {
                    return false;
                }
                await CoordinationRuntime.Client.ReportOrdersAppAlertAsync(
                    new OrdersAppAlertRequest
                    {
                        Kind = kind,
                        AccountLabel = account,
                        ShopName = shop,
                        Detail = detail,
                        MachineName = machine,
                    }, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Báo lỗi app lên hub lỗi: " + ex.Message);
                return false;
            }
        };

        // Trả rev Hub vừa cấp; null = CHƯA đẩy được → client giữ cờ cho_day và thử lại ở lượt sync sau.
        services.UpsertPickupAlertToHub = async (accountLogin, shopLogin, province, ct) =>
        {
            try
            {
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null) return null;
                return await CoordinationRuntime.Client.UpsertPickupAlertAsync(
                    new OrdersPickupAlertRequest
                    {
                        AccountLogin = accountLogin,
                        ShopLogin = shopLogin,
                        Province = province,
                    }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Upsert pickup-alert hub lỗi: " + ex.Message);
                return null;
            }
        };

        services.DismissPickupAlertToHub = async (accountLogin, shopLogin, ct) =>
        {
            try
            {
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null) return null;
                return await CoordinationRuntime.Client.DismissPickupAlertAsync(
                    new OrdersPickupAlertRequest
                    {
                        AccountLogin = accountLogin,
                        ShopLogin = shopLogin,
                    }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Dismiss pickup-alert hub lỗi: " + ex.Message);
                return null;
            }
        };

        services.FetchPickupAlertsFromHub = async (accountLogin, ct) =>
        {
            try
            {
                if (!CoordinationRuntime.Active || CoordinationRuntime.Client is null) return null;
                var list = await CoordinationRuntime.Client.GetPickupAlertsAsync(accountLogin, ct).ConfigureAwait(false);
                if (list is null) return null;
                return list
                    .Where(x => !string.IsNullOrWhiteSpace(x.ShopLogin))
                    .Select(x => new PickupAlertHubDong(
                        x.ShopLogin.Trim(),
                        x.Province ?? "",
                        x.Dismissed,
                        x.Rev))
                    .ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Trace.WriteLine("[OrdersModuleHost] Fetch pickup-alerts hub lỗi: " + ex.Message);
                return null;
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

    private static DateTimeOffset? ParseIsoOffset(string? iso)
        => !string.IsNullOrWhiteSpace(iso) && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var d)
            ? d
            : null;

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
        ReturnRequestCode = o.ReturnRequestCode,
        PreparedAt = o.PreparedAt?.ToString("o", CultureInfo.InvariantCulture),
        PreparedDay = o.PreparedAt?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        // Mốc đơn xuất hiện LẦN ĐẦU trên máy này → hub đặt first_seen_at theo mốc này thay vì giờ hub nhận gói.
        CreatedAt = o.CreatedAt?.ToString("o", CultureInfo.InvariantCulture),
    };
}
