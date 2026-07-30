using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Shopee.Core.Coordination;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Services;

namespace Shopee.Suite.Infrastructure;

// Partial của OrdersModuleHost: cấu hình ĐỒNG BỘ GOOGLE SHEET dùng chung — đẩy lên hub khi màn Cài đặt lưu +
// kéo về áp xuống CSDL module. Pure move từ OrdersModuleHost.cs.
public static partial class OrdersModuleHost
{
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
    /// URL + tab + link file phụ lên hub cho các máy khác nhận. URL TRỐNG thì KHÔNG đẩy (một máy chưa cấu hình
    /// không được xoá cấu hình của cả fleet).</item>
    /// <item><b>Kéo về</b> — <see cref="ApplyGsheetFromHubAsync"/> chạy ngay khi mở app rồi định kỳ.</item>
    /// </list>
    /// Nuốt mọi lỗi (log <c>Trace</c>) — lỗi hub KHÔNG được làm chết luồng đơn hàng.
    /// </summary>
    private static void WireGsheetConfig(AppServices services)
    {
        services.PushGsheetConfigToHub = async (url, tab, sheet2, ct) =>
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
                    new OrdersSharedConfig { GsheetWebAppUrl = url, GsheetTabName = tab, GsheetSheet2 = sheet2 },
                    ct).ConfigureAwait(false);
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
                cfg.GsheetWebAppUrl, cfg.GsheetTabName, cfg.GsheetSheet2,
                services.Settings.GetGsheetWebAppUrl(), services.Settings.GetGsheetTabName(),
                services.Settings.GetGsheetSheet2());
            if (!quyet.Ap)
            {
                return; // hub chưa cấu hình (KHÔNG đè) hoặc đã trùng bản local (khỏi ghi SQLite mỗi nhịp)
            }

            services.Settings.SetGsheetWebAppUrl(quyet.Url);
            services.Settings.SetGsheetTabName(quyet.Tab);
            services.Settings.SetGsheetSheet2(quyet.Sheet2);
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
}
