using Shopee.Core.Accounts;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Hub;
using Shopee.Hub.Web.Auth;
using Shopee.Hub.Web.Services;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace Shopee.Hub.Web.Api;

/// <summary>
/// API cho CLIENT (bản WPF/Avalonia). Port NGUYÊN bảng route Map() của suite\Shopee.Hub\HubServer.cs — GIỮ
/// đường dẫn + hình dạng JSON (camelCase, minimal-API defaults) để client cũ kết nối y hệt, khỏi sửa gì.
/// Khác biệt: mỗi route yêu cầu policy "Client" (scheme X-Api-Token) thay vì middleware token thủ công;
/// /health mở. THÊM: /accounts/append, /accounts/remove, /dispatcher; cờ AllowClientConfigPush chặn client
/// đè config/*.json sau cutover.
/// </summary>
public static class ClientApiEndpoints
{
    public static void MapClientApi(this WebApplication app, HubDatabase db)
    {
        // /health — KHÔNG auth (client dò kết nối trước khi có token). Postgres CHƯA cấu hình → GIỮ NGUYÊN shape
        // cũ {ok,ts} (client cũ đang parse, đừng thêm field); CÓ cấu hình → thêm pg = IsReady && ping OK.
        app.MapGet(HubRoutes.Health, async (IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null) return Results.Json(new { ok = true, ts = DateTimeOffset.UtcNow });
            var pg = pdb.IsReady && await pdb.PingAsync(ct);
            return Results.Json(new { ok = true, ts = DateTimeOffset.UtcNow, pg });
        }).AllowAnonymous();

        // Gom mọi route client vào 1 group yêu cầu policy "Client" (X-Api-Token).
        var api = app.MapGroup("").RequireAuthorization("Client");

        // ── File-sync (manifest + blob) ──
        api.MapGet(HubRoutes.Manifest, () => Results.Json(db.ListFiles()));
        api.MapGet("/files/{*name}", (string name) =>
        {
            var stream = db.OpenFileRead(name);
            return stream is null ? Results.NotFound() : Results.Stream(stream, "application/octet-stream");
        });
        api.MapPut("/files/{*name}", async (string name, HttpRequest req, HubOptions opts) =>
        {
            // Sau cutover: chặn client đè config/*.json (web là nguồn sự thật). Trả 403 → client nuốt lỗi, vô hại.
            if (!opts.AllowClientConfigPush && name.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            int? ifMatch = int.TryParse(req.Headers["If-Match"].ToString(), out var v) ? v : null;
            var updatedBy = req.Headers["X-Machine-Id"].ToString();
            var res = db.PutFile(name, ms.ToArray(), ifMatch, updatedBy);
            return res.Ok ? Results.Json(res) : Results.Json(res, statusCode: StatusCodes.Status409Conflict);
        });

        // ── Khoá việc theo shop+op ──
        api.MapPost(HubRoutes.LeasesAcquire, (LeaseAcquireRequest? r) => r is null ? Results.BadRequest() : Results.Json(db.AcquireLease(r)));
        api.MapPost(HubRoutes.LeasesHeartbeat, (LeaseHeartbeatRequest? r) => { if (r is null) return Results.BadRequest(); db.HeartbeatLease(r.Key, r.MachineId); return Results.Ok(); });
        api.MapPost(HubRoutes.LeasesRelease, (LeaseReleaseRequest? r) => { if (r is null) return Results.BadRequest(); db.ReleaseLease(r.Key, r.MachineId); return Results.Ok(); });

        // ── Khoá tài khoản Shopee ──
        api.MapPost(HubRoutes.AccountsReserve, (AccountReserveRequest? r) => r?.AccountIds is null ? Results.BadRequest() : Results.Json(db.ReserveAccounts(r)));
        api.MapPost(HubRoutes.AccountsRelease, (AccountReleaseRequest? r) => { if (r?.AccountIds is null) return Results.BadRequest(); db.ReleaseAccounts(r); return Results.Ok(); });
        api.MapPost(HubRoutes.AccountsHeartbeat, (AccountReleaseRequest? r) => { if (r?.AccountIds is null) return Results.BadRequest(); db.HeartbeatAccounts(r); return Results.Ok(); });

        // ── Sổ hoàn thành ──
        api.MapPost(HubRoutes.Ledger, (WorkLedgerRecord? r) => { if (r is null) return Results.BadRequest(); db.PublishLedger(r); return Results.Ok(); });
        api.MapPost(HubRoutes.LedgerSet, (SetLedgerStatusRequest? r) => { if (r is null) return Results.BadRequest(); db.SetLedgerStatus(r.Key, r.BigsellerId, r.ShopId, r.Sheet, r.Op, r.Status); return Results.Ok(); });
        api.MapGet(HubRoutes.Ledger, () => Results.Json(db.AllLedger()));

        // ── Nhịp máy + bảng trạng thái ──
        // Heartbeat giờ TRẢ JSON (lệnh update trong body). Client cũ bỏ qua body → đổi shape vô hại.
        api.MapPost(HubRoutes.MachineHeartbeat, (MachineHeartbeatRequest? r) => r is null ? Results.BadRequest() : Results.Json(db.MachineHeartbeat(r)));
        api.MapPost(HubRoutes.MachineUpdateAck, (UpdateAckRequest? r) => { if (r is null) return Results.BadRequest(); db.AckUpdate(r.MachineId, r.Status); return Results.Ok(); });
        api.MapPost(HubRoutes.MachineLeave, (MachineLeaveRequest? r) => { if (r is null) return Results.BadRequest(); db.RemoveMachine(r.MachineId); return Results.Ok(); });
        api.MapGet(HubRoutes.Fleet, () => Results.Json(db.Fleet()));

        // ── Vai trò máy + giao việc ──
        api.MapPost(HubRoutes.Roles, (SetRoleRequest? r) => { if (r is null) return Results.BadRequest(); db.SetRole(r.MachineId, r.Role); return Results.Ok(); });
        api.MapPost(HubRoutes.Assignments, (CreateAssignmentRequest? r) => r is null ? Results.BadRequest() : Results.Json(db.CreateAssignment(r)));
        api.MapPost(HubRoutes.AssignmentsClaim, (ClaimAssignmentsRequest? r) => r is null ? Results.BadRequest() : Results.Json(db.ClaimNext(r.MachineId, r.Role, r.Max)));
        api.MapPost(HubRoutes.AssignmentsStatus, (AssignmentStatusRequest? r) => { if (r is null) return Results.BadRequest(); db.UpdateAssignmentStatus(r.Id, r.MachineId, r.Status, r.Error); return Results.Ok(); });
        api.MapPost(HubRoutes.AssignmentsCancel, (CancelAssignmentRequest? r) => { if (r is null) return Results.BadRequest(); db.CancelAssignment(r.Id); return Results.Ok(); });
        // Tiếp tục 1 việc đã dừng/huỷ → 'queued'. error null = OK; ngược lại là lý do từ chối (client hiển thị).
        api.MapPost(HubRoutes.AssignmentsResume, (ResumeAssignmentRequest? r) => r is null ? Results.BadRequest() : Results.Ok(new { error = db.ResumeAssignment(r.Id) }));
        // Client khởi động lại xin nhận lại việc dở của chính máy mình → trả số việc đưa lại về 'queued'.
        api.MapPost(HubRoutes.AssignmentsResumeMine, (ResumeMineRequest? r) => r is null ? Results.BadRequest() : Results.Ok(new { requeued = db.ResumeMachineWork(r.MachineId) }));

        // ── Kho gộp kết quả Search ──
        api.MapPost(HubRoutes.SearchProducts, (SearchProductsPushRequest? r) => { if (r is null) return Results.BadRequest(); db.SaveSearchProducts(r); return Results.Ok(); });
        api.MapGet(HubRoutes.SearchProducts, () => Results.Json(db.AllSearchProductJson()));
        api.MapGet(HubRoutes.SearchProductsCount, () => Results.Json(db.SearchProductCount()));
        api.MapPost(HubRoutes.SearchProductsClear, () => { db.ClearSearchProducts(); return Results.Ok(); });

        // ── Log tập trung ──
        api.MapPost(HubRoutes.Logs, (AppendLogRequest? r) => { if (r is null) return Results.BadRequest(); db.AppendLog(r); return Results.Ok(); });
        api.MapGet(HubRoutes.Logs, (long? after, int? max) => Results.Json(db.GetLogs(after ?? 0, Math.Clamp(max ?? 300, 1, 1000))));
        api.MapPost(HubRoutes.LogsClear, () => { db.ClearLogs(); return Results.Ok(); });

        // ── Client báo acc Shopee lỗi/captcha ──
        api.MapPost(HubRoutes.AccountsErrored, (AccountErrorRequest? r) => { if (r is null) return Results.BadRequest(); db.ReportAccountError(r); return Results.Ok(); });
        api.MapGet(HubRoutes.AccountsErrored, () => Results.Json(db.AllAccountErrors()));
        api.MapPost(HubRoutes.AccountsErroredClear, (ClearAccountErrorRequest? r) => { if (r is null) return Results.BadRequest(); db.ClearAccountError(r.AccountId); return Results.Ok(); });

        // ── MỚI: client đẩy acc Shopee OK mới check lên (web-hub là nguồn sự thật; client hết push accounts.json) ──
        api.MapPost("/accounts/append", (List<ShopeeAccount>? r, FileStoreConfigService cfg) =>
        {
            if (r is null) return Results.BadRequest();
            var added = cfg.AppendShopeeAccounts(r);
            return Results.Json(new { added });
        });
        api.MapPost("/accounts/remove", (AccountRemoveRequest? r, FileStoreConfigService cfg) =>
        {
            if (r is null || string.IsNullOrWhiteSpace(r.Id)) return Results.BadRequest();
            return Results.Json(new { removed = cfg.RemoveShopeeAccount(r.Id) });
        });

        // ── MỚI: client đẩy (upsert) acc/shop BigSeller lên hub. Client GIỜ phát sinh acc/shop; hub là nguồn sự
        //    thật nhưng client không có đường đẩy → lượt pull (MergeBigSeller mirror) xoá mất acc client vừa thêm.
        //    Hub gộp KHÔNG XÓA; chỉ thêm/cập nhật field chung. KHÔNG bị AllowClientConfigPush chặn (đây là đường
        //    hợp lệ để client góp acc/shop, khác PUT /files/config/* bị chặn sau cutover). ──
        api.MapPost(HubRoutes.BigSellerUpsert, (List<BigSellerAccount>? r, FileStoreConfigService cfg) =>
        {
            if (r is null) return Results.BadRequest();
            return Results.Json(cfg.UpsertBigSellerAccounts(r));
        });

        // ── Nghiệp vụ đơn hàng ──
        // GET /api/shops → danh sách shop (hub tự đăng ký theo username khi client push).
        api.MapGet(HubRoutes.Shops, () => Results.Json(db.ListShops()));

        // POST /api/orders/push → hub tự đăng ký shop theo username rồi upsert lô đơn + ghi log; có đơn MỚI
        // (Added>0) → bắn tin về webhook cấu hình (fire-and-forget, KHÔNG chặn response).
        api.MapPost(HubRoutes.OrdersPush, (OrdersPushRequest? r, HttpRequest req, ILoggerFactory lf) =>
        {
            if (r is null || string.IsNullOrWhiteSpace(r.ShopUsername) || r.Orders is null) return Results.BadRequest();
            var username = r.ShopUsername.Trim();
            var shopId = db.GetOrCreateShopByUsername(username, r.ShopName);
            var res = db.UpsertOrders(shopId, r.Orders);
            var mid = req.Headers["X-Machine-Id"].ToString();
            db.AppendLog(new AppendLogRequest(mid, "", "info",
                $"orders/push shop={username} (id {shopId}): +{res.Added} mới, {res.Updated} cập nhật (tổng gửi {r.Orders.Count})"));
            if (res.Added > 0 && res.InsertedItems.Count > 0)
            {
                var shopName = string.IsNullOrWhiteSpace(r.ShopName) ? username : r.ShopName!.Trim();
                FireNotifyNewOrders(db, lf.CreateLogger("OrderNotify"), shopName, res.InsertedItems);
            }
            return Results.Json(new OrdersPushResult(res.Added, res.Updated));
        });

        // GET /api/orders?shopId=&status=&q=&page=&pageSize= → xem đơn (admin lẫn client).
        api.MapGet(HubRoutes.Orders, (long? shopId, string? status, string? q, int? page, int? pageSize) =>
        {
            var ps = Math.Clamp(pageSize ?? 50, 1, 500);
            var p = Math.Max(1, page ?? 1);
            var total = db.CountOrders(shopId, status, q);
            var items = db.QueryOrders(shopId, status, q, ps, (p - 1) * ps);
            return Results.Json(new { items, total, page = p, pageSize = ps });
        });

        // ── MỚI: xem/đổi trạng thái điều phối (web UI + có thể client đọc) ──
        api.MapGet("/dispatcher", (DispatcherService d) => Results.Json(new { enabled = d.Enabled, auto = d.AutoMode }));
        api.MapPost("/dispatcher", (DispatcherStateRequest? r, DispatcherService d) =>
        {
            if (r is null) return Results.BadRequest();
            d.Enabled = r.Enabled; d.AutoMode = r.Auto;
            return Results.Ok();
        });
    }

    /// <summary>Fire-and-forget báo "đơn mới" tới TỪNG webhook cấu hình (mỗi dòng 1 URL). TUYỆT ĐỐI không chặn
    /// response push: dựng tin nhắn xong thì <see cref="Task.Run(Action)"/> gửi ở nền, nuốt mọi lỗi vào
    /// <see cref="ILogger.LogWarning(string, object?[])"/>. Chưa cấu hình webhook → bỏ qua im lặng.</summary>
    private static void FireNotifyNewOrders(HubDatabase db, ILogger logger, string shopName, IReadOnlyList<OrderPushItem> inserted)
    {
        var raw = db.GetSetting(SettingKeys.NotifyWebhooks);
        if (string.IsNullOrWhiteSpace(raw)) return; // chưa cấu hình → không notify

        var urls = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Where(u => u.Length > 0).ToList();
        if (urls.Count == 0 || inserted.Count == 0) return;

        var donMoi = inserted.Select(ToSyncedOrder).ToList();
        var text = OrderNotifyService.TaoTinNhanDonMoi(shopName, donMoi, DateTime.Now);
        var svc = new OrderNotifyService();
        _ = Task.Run(async () =>
        {
            foreach (var url in urls)
            {
                // SendAsync tự thêm tiền tố "Notify: " vào từng dòng log → KHÔNG prefix lại (tránh "Notify: Notify:").
                try { await svc.SendAsync(url, text, m => logger.LogWarning("{Message}", m), CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Notify: lỗi gửi tới webhook."); }
            }
        });
    }

    /// <summary>Map <see cref="OrderPushItem"/> (DTO client) → <see cref="SyncedOrder"/> (type
    /// <see cref="OrderNotifyService"/> dùng dựng tin nhắn). Hai type mirror nhau field-by-field.</summary>
    private static SyncedOrder ToSyncedOrder(OrderPushItem o) => new()
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
}

/// <summary>Body cho /accounts/remove.</summary>
public sealed record AccountRemoveRequest(string Id);
/// <summary>Body cho POST /dispatcher.</summary>
public sealed record DispatcherStateRequest(bool Enabled, bool Auto);
