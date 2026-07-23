using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shopee.Core.Coordination;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;

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
            return new MainViewModel(Services);
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
    /// hub chưa kết nối → trả false (đơn giữ CHƯA đánh dấu, thử lại lượt sau); ngược lại map lô đơn sang DTO hub
    /// rồi POST, non-null = OK. Nuốt mọi lỗi (log <c>Trace</c>) trả false — trừ hủy CHỦ ĐỘNG (ct) cho xuyên để
    /// phiên xử như dừng. Shop trên hub khóa theo <see cref="OrdersPushRequest.ShopUsername"/> = tên đăng nhập tài khoản.
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
                var shopUsername = ResolveShopUsername(acc, accountId);

                var req = new OrdersPushRequest
                {
                    ShopUsername = shopUsername,
                    ShopName = shopUsername,
                    Orders = orders.Select(ToPushItem).ToList(),
                };

                var res = await CoordinationRuntime.Client.PushOrdersAsync(req, ct).ConfigureAwait(false);
                return res is not null; // hub nhận OK (non-null) → phiên đánh dấu hub_synced_at
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
    /// sau); ngược lại map lô <c>(OrderSn, FileBase64)</c> sang DTO hub rồi POST. TRẢ VỀ danh sách <c>order_sn</c>
    /// hub ĐÃ LƯU = (lô gửi) − <c>missing</c> − <c>errors</c> (client mark đúng đơn). null = hub lỗi cả lô (hub cũ
    /// 404 / offline / timeout) → phiên KHÔNG mark, lượt sau đẩy lại. Nuốt mọi lỗi (log <c>Trace</c>) trả null —
    /// trừ hủy CHỦ ĐỘNG (ct) cho xuyên để phiên xử như dừng.
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
                var shopUsername = ResolveShopUsername(acc, accountId);

                var req = new OrdersSlipPushRequest
                {
                    ShopUsername = shopUsername,
                    ShopName = shopUsername,
                    Slips = slips.Select(s => new SlipPushItem { OrderSn = s.OrderSn, FileBase64 = s.FileBase64 }).ToList(),
                };

                var res = await CoordinationRuntime.Client.PushOrderSlipsAsync(req, ct).ConfigureAwait(false);
                if (res is null)
                {
                    return null; // hub lỗi cả lô → không mark, lượt sau thử lại
                }

                // ĐÃ LƯU = lô gửi − missing − errors. Đơn missing (chưa lên hub) / lỗi (base64/PDF) KHÔNG mark.
                var notSaved = new HashSet<string>(StringComparer.Ordinal);
                if (res.Missing is not null)
                {
                    foreach (var m in res.Missing) notSaved.Add(m);
                }
                if (res.Errors is not null)
                {
                    foreach (var e in res.Errors) notSaved.Add(e.OrderSn);
                }
                return slips.Where(s => !notSaved.Contains(s.OrderSn)).Select(s => s.OrderSn).ToList();
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

    /// <summary>Map một <see cref="SyncedOrder"/> (module Đơn hàng) sang <see cref="OrderPushItem"/> (DTO hub) —
    /// mirror field-by-field để client đẩy 1-1, khỏi lệch field.</summary>
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
        try { await svc.Sessions.StopAllAsync(); } catch { /* bỏ qua khi thoát */ }
    }
}
