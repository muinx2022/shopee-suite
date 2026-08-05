using System.Globalization;
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
/// /health mở; cờ AllowClientConfigPush chặn client đè config/*.json sau cutover.
/// (Hai route legacy /accounts/append + /accounts/remove ĐÃ XOÁ 30/07 — log VM 10→30/07 không có lượt trúng nào.)
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

        // Logger để đánh dấu các endpoint HẾT consumer (client 08/2026 không còn gọi). KHÔNG xoá vội — soak
        // 2–3 tuần xem log VM có lượt trúng nào không rồi mới gỡ (tiền lệ /accounts/append, gỡ 30/07).
        var log = app.Logger;

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

        // ── Affinity tk↔máy (Scrape): ghi "nhà" (POST) + đọc home + cờ binding (GET) ──
        api.MapPost(HubRoutes.AccountsHome, (SetAccountHomeRequest? r) =>
        {
            if (r?.AccountIds is null) return Results.BadRequest();
            db.SetAccountHome(r);
            return Results.Ok();
        });
        api.MapGet(HubRoutes.AccountsHome, () => Results.Json(db.AccountHomes()));

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
        // Hub TỪ CHỐI giao việc BigSeller vào suất ĐƠN HÀNG → 400 kèm lý do (thay vì 200 với một việc kẹt
        // 'queued' vĩnh viễn). Client hiện tại nuốt lỗi HTTP → vô hại; đường giao việc thật là UI web.
        api.MapPost(HubRoutes.Assignments, (CreateAssignmentRequest? r) =>
        {
            if (r is null) return Results.BadRequest();
            var a = db.CreateAssignment(r);
            return a.Status == HubDatabase.RejectedStatus
                ? Results.BadRequest(new { error = a.LastError })
                : Results.Json(a);
        });
        api.MapPost(HubRoutes.AssignmentsClaim, (ClaimAssignmentsRequest? r) => r is null ? Results.BadRequest() : Results.Json(db.ClaimNext(r.MachineId, r.Role, r.Max)));
        api.MapPost(HubRoutes.AssignmentsStatus, (AssignmentStatusRequest? r) => { if (r is null) return Results.BadRequest(); db.UpdateAssignmentStatus(r.Id, r.MachineId, r.Status, r.Error); return Results.Ok(); });
        api.MapPost(HubRoutes.AssignmentsCancel, (CancelAssignmentRequest? r) => { if (r is null) return Results.BadRequest(); db.CancelAssignment(r.Id); return Results.Ok(); });
        // Tiếp tục 1 việc đã dừng/huỷ → 'queued'. error null = OK; ngược lại là lý do từ chối (client hiển thị).
        api.MapPost(HubRoutes.AssignmentsResume, (ResumeAssignmentRequest? r) => r is null ? Results.BadRequest() : Results.Ok(new { error = db.ResumeAssignment(r.Id) }));
        // Client khởi động lại xin nhận lại việc dở của chính máy mình → trả số việc đưa lại về 'queued'.
        api.MapPost(HubRoutes.AssignmentsResumeMine, (ResumeMineRequest? r) => r is null ? Results.BadRequest() : Results.Ok(new { requeued = db.ResumeMachineWork(r.MachineId) }));

        // ── Kho gộp kết quả Search ──
        api.MapPost(HubRoutes.SearchProducts, (SearchProductsPushRequest? r) => { if (r is null) return Results.BadRequest(); db.SaveSearchProducts(r); return Results.Ok(); });
        // 3 route dưới HẾT consumer từ đợt dọn 06/08 (HubClient bỏ SearchProductsAsync/…Count/…Clear).
        api.MapGet(HubRoutes.SearchProducts, (HttpRequest req) =>
        {
            log.LogWarning("legacy endpoint hit: {path} tu {ip}", req.Path.Value, req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?");
            return Results.Json(db.AllSearchProductJson());
        });
        api.MapGet(HubRoutes.SearchProductsCount, (HttpRequest req) =>
        {
            log.LogWarning("legacy endpoint hit: {path} tu {ip}", req.Path.Value, req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?");
            return Results.Json(db.SearchProductCount());
        });
        api.MapPost(HubRoutes.SearchProductsClear, (HttpRequest req) =>
        {
            log.LogWarning("legacy endpoint hit: {path} tu {ip}", req.Path.Value, req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?");
            db.ClearSearchProducts();
            return Results.Ok();
        });

        // ── Log tập trung ──
        api.MapPost(HubRoutes.Logs, (AppendLogRequest? r) => { if (r is null) return Results.BadRequest(); db.AppendLog(r); return Results.Ok(); });
        api.MapGet(HubRoutes.Logs, (long? after, int? max) => Results.Json(db.GetLogs(after ?? 0, Math.Clamp(max ?? 300, 1, 1000))));
        api.MapPost(HubRoutes.LogsClear, () => { db.ClearLogs(); return Results.Ok(); });

        // ── Client báo acc Shopee lỗi/captcha ──
        api.MapPost(HubRoutes.AccountsErrored, (AccountErrorRequest? r) => { if (r is null) return Results.BadRequest(); db.ReportAccountError(r); return Results.Ok(); });
        api.MapGet(HubRoutes.AccountsErrored, () => Results.Json(db.AllAccountErrors()));
        api.MapPost(HubRoutes.AccountsErroredClear, (ClearAccountErrorRequest? r) => { if (r is null) return Results.BadRequest(); db.ClearAccountError(r.AccountId); return Results.Ok(); });

        // ── MỚI: client đẩy (upsert) acc/shop BigSeller lên hub. Client GIỜ phát sinh acc/shop; hub là nguồn sự
        //    thật nhưng client không có đường đẩy → lượt pull (MergeBigSeller mirror) xoá mất acc client vừa thêm.
        //    Hub gộp KHÔNG XÓA; chỉ thêm/cập nhật field chung. KHÔNG bị AllowClientConfigPush chặn (đây là đường
        //    hợp lệ để client góp acc/shop, khác PUT /files/config/* bị chặn sau cutover). ──
        api.MapPost(HubRoutes.BigSellerUpsert, (List<BigSellerAccount>? r, FileStoreConfigService cfg) =>
        {
            if (r is null) return Results.BadRequest();
            return Results.Json(cfg.UpsertBigSellerAccounts(r));
        });

        // ── MỚI: client NHỜ hub đăng nhập lại 1 acc BigSeller (gặp verify code / mất phiên) ──
        // Hub có sẵn cả bộ máy: Playwright headless + giải captcha AI + TỰ đọc mã OTP từ hòm thư + nạp lại
        // device-trust; xong thì ghi TOÀN BỘ cookie vào kho cookies/{acctId}.json để client kéo về. Client chỉ gửi
        // AccountId — mật khẩu acc/hòm thư KHÔNG đi qua dây.
        // Accepted=false KHÔNG phải lỗi (client cứ chờ rồi hỏi lại): đã có phiên lo acc này, hoặc hub đang bận
        // login acc khác. Acc không có / thiếu mật khẩu → Status="failed" + lý do (KHÔNG ném exception).
        api.MapPost(HubRoutes.BigSellerRelogin, (BigSellerReloginRequest? r, BigSellerLoginService login, FileStoreConfigService cfg, HttpRequest req) =>
        {
            if (r is null || string.IsNullOrWhiteSpace(r.AccountId)) return Results.BadRequest();
            var acctId = r.AccountId.Trim();

            // Đã có phiên cho ĐÚNG acc này (admin/scheduler/máy khác vừa xin) → "đã có người lo", đừng dựng phiên 2.
            if (login.GetState(acctId) is { Status: "running" or "needsOtp" } cur)
                return Results.Json(new BigSellerReloginResponse(false, cur.Status, cur.Message));

            var acct = cfg.BigSellerAccounts().FirstOrDefault(a => a.Id == acctId);
            if (acct is null)
                return Results.Json(new BigSellerReloginResponse(false, "failed", $"Hub không có acc BigSeller id '{acctId}' (acc chỉ nằm ở máy client?)."));
            if (string.IsNullOrWhiteSpace(acct.Email) || string.IsNullOrWhiteSpace(acct.Password))
                return Results.Json(new BigSellerReloginResponse(false, "failed", $"Acc {acct.DisplayName}: hub thiếu Email/Mật khẩu — điền ở trang cấu hình acc rồi thử lại."));

            // KHÔNG quá 1 phiên login ĐỒNG THỜI toàn hub (tính cả phiên admin/scheduler — cùng luật với
            // BigSellerReloginScheduler.TickAsync): nhiều máy mất phiên một lúc mà login song song thì BigSeller
            // siết cả cụm. Client giữ acc trong danh sách chờ và xin lại nhịp sau → tự xếp hàng.
            if (login.AnyActive)
                return Results.Json(new BigSellerReloginResponse(false, "idle", "Hub đang đăng nhập một acc khác — chờ tới lượt."));

            var started = login.Start(acctId, acct.Email.Trim(), acct.Password, acct.EmailPassword);
            var machine = string.IsNullOrWhiteSpace(r.MachineId) ? req.Headers["X-Machine-Id"].ToString() : r.MachineId;
            db.AppendLog(new AppendLogRequest(machine, "", "info",
                $"bigseller/relogin: xin đăng nhập lại acc {acct.DisplayName} → {(started ? "hub đã bắt đầu" : "đã có phiên đang chạy")}"));
            var st = login.GetState(acctId);
            return Results.Json(new BigSellerReloginResponse(started, st?.Status ?? "idle", st?.Message ?? ""));
        });

        // GET ?accountId= → trạng thái phiên login (chưa có phiên → "idle"). Accepted luôn false (chỉ POST mới mở phiên).
        api.MapGet(HubRoutes.BigSellerRelogin, (string? accountId, BigSellerLoginService login) =>
        {
            if (string.IsNullOrWhiteSpace(accountId)) return Results.BadRequest();
            var st = login.GetState(accountId.Trim());
            return Results.Json(new BigSellerReloginResponse(false, st?.Status ?? "idle", st?.Message ?? ""));
        });

        // ── Cấu hình DÙNG CHUNG module Đơn hàng (khối GSheet) ──
        // GET: client kéo về (rỗng = hub chưa cấu hình → client GIỮ NGUYÊN bản local, xem GsheetConfigSync).
        // POST: client vừa sửa ở màn Cài đặt → gộp lên hub, CHỈ field khác trống (không xoá field kia).
        // KHÔNG bị AllowClientConfigPush chặn (route riêng, ngoài tiền tố config/) — đây là đường hợp lệ để
        // client góp cấu hình, như /bigseller/upsert.
        api.MapGet(HubRoutes.OrdersConfig, (FileStoreConfigService cfg) => Results.Json(cfg.Orders()));
        api.MapPost(HubRoutes.OrdersConfig, (OrdersSharedConfig? r, FileStoreConfigService cfg) =>
        {
            if (r is null) return Results.BadRequest();
            return Results.Json(new { ok = cfg.MergeOrdersConfig(r) });
        });

        // ── Gương danh bạ tài khoản Đơn hàng + ack lệnh hub giao ──
        // POST /orders/accounts → máy đẩy TOÀN BỘ danh bạ tài khoản Đơn hàng của CHÍNH NÓ (login + shop con +
        // trạng thái phiên). Hub thay trọn danh bạ của đúng machine_id đó, KHÔNG đụng máy khác. Payload CỐ Ý
        // không có mật khẩu/cookie — hub chỉ làm gương, tài khoản vẫn thuộc máy.
        api.MapPost(HubRoutes.OrdersAccounts, (OrdersAccountsPushRequest? r) =>
        {
            if (r is null || string.IsNullOrWhiteSpace(r.MachineId)) return Results.BadRequest();
            return Results.Json(new { accounts = db.UpsertOrdersAccounts(r) });
        });

        // GET /orders/accounts/directory → DANH BẠ sub-acc Đơn hàng GỘP TỪ MỌI MÁY (login + shop con), distinct
        // theo login. KHÔNG mật khẩu/cookie (Hub không giữ). Máy MỚI kéo về để tạo sẵn bản ghi tài khoản
        // rỗng-mật-khẩu; người dùng tự nhập mật khẩu rồi đăng nhập.
        api.MapGet(HubRoutes.OrdersAccountsDirectory, () =>
            Results.Json(db.AllOrdersAccountsDistinct()
                .Select(a => new OrdersDirectoryAccount(a.Login, a.Shops
                    .Select(s => new OrdersShopItem(s.Login, s.Name)).ToList()))
                .ToList()));

        // POST /orders/commands/ack → client báo kết quả lệnh hub giao (lệnh ĐI kèm phản hồi heartbeat).
        api.MapPost(HubRoutes.OrdersCommandsAck, (OrdersCommandAckRequest? r) =>
        {
            if (r is null || string.IsNullOrWhiteSpace(r.Id)) return Results.BadRequest();
            db.AckOrdersCommand(r.Id, r.Status, r.Error);
            return Results.Ok();
        });

        // ── Nghiệp vụ đơn hàng ──
        // GET /api/shops → danh sách shop (hub tự đăng ký theo username khi client push). Map TƯỜNG MINH sang
        // HubShopItem: vừa khoá hợp đồng với client, vừa CẮT password/cookie/proxy của bản Shop đầy đủ.
        // HẾT consumer (client không còn gọi) — đang soak trước khi gỡ.
        api.MapGet(HubRoutes.Shops, (HttpRequest req) =>
        {
            log.LogWarning("legacy endpoint hit: {path} tu {ip}", req.Path.Value, req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?");
            return Results.Json(db.ListShops().Select(ToHubShopItem).ToList());
        });

        // POST /api/orders/push → hub tự đăng ký shop theo username rồi upsert lô đơn + ghi log; có đơn MỚI
        // (Added>0) → bắn tin về webhook cấu hình (fire-and-forget, KHÔNG chặn response).
        api.MapPost(HubRoutes.OrdersPush, (OrdersPushRequest? r, HttpRequest req, WebhookQueueService webhookQueue) =>
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
                FireNotifyDonMoi(db, webhookQueue, mid, shopName, res.InsertedItems);
            }
            if (res.ReturnCodeChangedItems.Count > 0)
            {
                var shopName = string.IsNullOrWhiteSpace(r.ShopName) ? username : r.ShopName!.Trim();
                FireNotifyDonTra(db, webhookQueue, mid, shopName, res.ReturnCodeChangedItems);
            }
            return Results.Json(new OrdersPushResult(res.Added, res.Updated));
        });

        // GET /api/orders/stats → thống kê đơn dùng chung, trả ảnh chụp gom trên hub để mọi client thấy cùng số.
        // Tham số là hai MỐC UTC (ISO-8601 "o") client tính SẴN từ ngày địa phương của người dùng — hub KHÔNG suy
        // diễn múi giờ (máy chủ chạy giờ UTC, mỗi client một múi giờ). Parse với InvariantCulture + RoundtripKind,
        // KHÔNG dùng TryParse một tham số (phụ thuộc culture máy chủ).
        // PHÁ TƯƠNG THÍCH có chủ ý: client CŨ gọi ?from=&to= → BadRequest → tab Thống kê bản cũ tự về số local.
        api.MapGet(HubRoutes.OrdersStats, (string? fromUtc, string? toUtc, string? shop) =>
        {
            if (!DateTime.TryParse(fromUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var from)
                || !DateTime.TryParse(toUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var to))
            {
                return Results.BadRequest();
            }
            return Results.Json(db.GetSharedOrderStatistics(AsUtc(from), AsUtc(to), shop));
        });

        // POST /api/orders/app-alert → client báo SỰ KIỆN app; Hub quyết định kênh webhook (fire-and-forget).
        // Kind="don_tra" là tin NGHIỆP VỤ (mã trả hàng của đơn đã bị dọn khỏi app — hub không thấy qua orders/push
        // vì không còn đơn để so mã) → đi kênh ĐƠN TRẢ, không phải kênh lỗi app. Kind khác → kênh lỗi app như cũ.
        api.MapPost(HubRoutes.OrdersAppAlert, (OrdersAppAlertRequest? r, HttpRequest req, WebhookQueueService webhookQueue) =>
        {
            if (r is null || string.IsNullOrWhiteSpace(r.Kind)) return Results.BadRequest();
            var mid = req.Headers["X-Machine-Id"].ToString();
            var kind = r.Kind.Trim();
            db.AppendLog(new AppendLogRequest(mid, "", "warn",
                $"orders/app-alert kind={kind} account={r.AccountLabel} shop={r.ShopName} detail={r.Detail}"));
            if (string.Equals(kind, KindDonTra, StringComparison.OrdinalIgnoreCase))
            {
                FireNotifyDonTraTuAppAlert(db, webhookQueue, mid, r);
            }
            else
            {
                FireNotifyLoiApp(db, webhookQueue, mid, r);
            }
            return Results.Ok();
        });

        // Banner lỗi địa chỉ bền (đồng bộ đa máy theo account_login + shop_login).
        api.MapPost(HubRoutes.OrdersPickupAlertsUpsert, (OrdersPickupAlertRequest? r, HttpRequest req) =>
        {
            if (r is null || string.IsNullOrWhiteSpace(r.AccountLogin) || string.IsNullOrWhiteSpace(r.ShopLogin))
                return Results.BadRequest();
            var mid = req.Headers["X-Machine-Id"].ToString();
            var rev = db.UpsertPickupAlert(r.AccountLogin, r.ShopLogin, r.Province, mid);
            if (rev == 0)
                return Results.BadRequest();
            db.AppendLog(new AppendLogRequest(mid, "", "info",
                $"orders/pickup-alerts/upsert account={r.AccountLogin.Trim()} shop={r.ShopLogin.Trim()} rev={rev}"));
            // Client cũ (≤ v1.7.18) bỏ qua body này; client mới lưu rev để merge.
            return Results.Json(new OrdersPickupAlertAck { Rev = rev });
        });
        api.MapPost(HubRoutes.OrdersPickupAlertsDismiss, (OrdersPickupAlertRequest? r, HttpRequest req) =>
        {
            if (r is null || string.IsNullOrWhiteSpace(r.AccountLogin) || string.IsNullOrWhiteSpace(r.ShopLogin))
                return Results.BadRequest();
            var mid = req.Headers["X-Machine-Id"].ToString();
            var rev = db.DismissPickupAlert(r.AccountLogin, r.ShopLogin, mid);
            if (rev == 0)
                return Results.BadRequest();
            db.AppendLog(new AppendLogRequest(mid, "", "info",
                $"orders/pickup-alerts/dismiss account={r.AccountLogin.Trim()} shop={r.ShopLogin.Trim()} rev={rev}"));
            return Results.Json(new OrdersPickupAlertAck { Rev = rev });
        });
        api.MapGet(HubRoutes.OrdersPickupAlerts, (string? accountLogin) =>
        {
            if (string.IsNullOrWhiteSpace(accountLogin)) return Results.BadRequest();
            var rows = db.ListPickupAlerts(accountLogin);
            var items = rows.Select(x => new OrdersPickupAlertItem
            {
                ShopLogin = x.ShopLogin,
                Province = x.Province,
                Dismissed = !string.IsNullOrEmpty(x.DismissedAt),
                Rev = x.Rev,
                // BẮT BUỘC giữ cho client ≤ v1.7.18 (chúng merge bằng 2 mốc này) — xem xmldoc OrdersPickupAlertItem.
                CreatedAt = x.CreatedAt,
                DismissedAt = x.DismissedAt,
            }).ToList();
            return Results.Json(items);
        });

        // POST /api/orders/slip → hub lưu file phiếu PDF cho các đơn ĐÃ có trên hub. Lô ≤5 (client tự chia).
        // Mỗi phiếu: đơn CHƯA có trên hub → missing (client thử lại lượt sau, sau khi orders/push xong); base64
        // hỏng / không phải PDF (%PDF-) / >5MB → errors; hợp lệ → ghi <DataDir>/slips/<shopId>/<sn>.pdf (đè, bản
        // mới thắng) + set slip_at. KHÔNG đụng hợp đồng orders/push.
        api.MapPost(HubRoutes.OrdersSlip, (OrdersSlipPushRequest? r, HttpRequest req, HubOptions opts) =>
        {
            if (r is null || string.IsNullOrWhiteSpace(r.ShopUsername) || r.Slips is null) return Results.BadRequest();
            var username = r.ShopUsername.Trim();
            var shopId = db.GetOrCreateShopByUsername(username, r.ShopName);

            var saved = 0;
            var missing = new List<string>();
            var errors = new List<SlipPushError>();
            foreach (var s in r.Slips)
            {
                if (s is null || string.IsNullOrWhiteSpace(s.OrderSn)) continue;
                var sn = s.OrderSn.Trim();
                var safe = HubDatabase.SanitizeSlipFileName(sn);
                if (safe is null) { errors.Add(new SlipPushError(sn, "ten-don-khong-hop-le")); continue; }

                // Đơn PHẢI đã có trên hub (orders/push chạy trước) — chưa có → missing, KHÔNG ghi file.
                if (!db.OrderExists(shopId, sn)) { missing.Add(sn); continue; }

                byte[] bytes;
                try { bytes = Convert.FromBase64String(s.FileBase64 ?? ""); }
                catch { errors.Add(new SlipPushError(sn, "base64-loi")); continue; }
                if (bytes.LongLength > MaxSlipBytes) { errors.Add(new SlipPushError(sn, "file-qua-lon")); continue; }
                if (!LooksPdf(bytes)) { errors.Add(new SlipPushError(sn, "khong-phai-pdf")); continue; }

                try
                {
                    var dir = Path.Combine(opts.DataDir, "slips", shopId.ToString());
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, safe + ".pdf"), bytes);
                    db.SetOrderSlipAt(shopId, sn, DateTimeOffset.UtcNow);
                    saved++;
                }
                catch (Exception ex) { errors.Add(new SlipPushError(sn, "ghi-file-loi: " + ex.Message)); }
            }

            var mid = req.Headers["X-Machine-Id"].ToString();
            db.AppendLog(new AppendLogRequest(mid, "", "info",
                $"orders/slip shop={username} (id {shopId}): {saved} lưu, {missing.Count} thiếu đơn, {errors.Count} lỗi (tổng gửi {r.Slips.Count})"));
            return Results.Json(new OrdersSlipPushResult(saved, missing, errors));
        });

        // GET /api/orders?shopId=&status=&q=&page=&pageSize= → xem đơn (admin lẫn client API). LỌC + PHÂN TRANG
        // chạy Ở ĐÂY. Map tường minh OrderRecord → HubOrderItem.
        // HẾT consumer (client không còn gọi) — đang soak trước khi gỡ.
        api.MapGet(HubRoutes.Orders, (long? shopId, string? status, string? q, int? page, int? pageSize, HttpRequest req) =>
        {
            log.LogWarning("legacy endpoint hit: {path} tu {ip}", req.Path.Value, req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?");
            var ps = Math.Clamp(pageSize ?? 50, 1, 500);
            var p = Math.Max(1, page ?? 1);
            var result = db.QueryOrdersPage(shopId, status, q, ps, (p - 1) * ps);
            return Results.Json(new HubOrdersPage
            {
                Items = result.Items.Select(ToHubOrderItem).ToList(),
                Total = result.Total,
                Page = p,
                PageSize = ps,
            });
        });

        // GET /prepare-stats?day=yyyy-MM-dd → số đơn ĐÃ "chuẩn bị hàng" theo shop trong ĐÚNG một ngày. Hub đếm
        // THẲNG từ bảng orders (mỗi đơn 1 dòng) nên là con số CHUNG toàn hệ thống — nhiều máy cùng chạy không cộng
        // trùng. day thiếu/sai định dạng → 400; ngày không có đơn nào → list RỖNG (KHÔNG 404): client phân biệt
        // "hub bảo 0 đơn" (list) với "không hỏi được hub" (null → giữ số cục bộ).
        api.MapGet(HubRoutes.PrepareStats, (string? day) =>
        {
            if (string.IsNullOrWhiteSpace(day) || !DateOnly.TryParseExact(day, "yyyy-MM-dd", out _))
            {
                return Results.BadRequest();
            }
            return Results.Json(db.PrepareStatsByDay(day)
                .Select(s => new PrepareStatItem { ShopUsername = s.ShopUsername, Count = s.Count })
                .ToList());
        });
    }

    /// <summary>Chuẩn hoá mốc thời gian client gửi lên thành DateTime Kind=Utc. Chuỗi có "Z"/offset → parser đã ra
    /// đúng thời điểm (Local chỉ là cách biểu diễn, <c>ToUniversalTime</c> lấy lại ĐÚNG mốc, không đoán múi giờ);
    /// chuỗi KHÔNG hậu tố (Unspecified) → hiểu là UTC theo hợp đồng route, KHÔNG quy đổi theo giờ máy chủ.</summary>
    private static DateTime AsUtc(DateTime v) => v.Kind switch
    {
        DateTimeKind.Utc => v,
        DateTimeKind.Local => v.ToUniversalTime(),
        _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };

    /// <summary>Map <see cref="OrderRecord"/> (kiểu nội bộ hub) → <see cref="HubOrderItem"/> (DTO dùng chung với
    /// client). Map TAY từng field — KHÔNG serialize thẳng <c>OrderRecord</c> — để đổi tên field bên hub làm gãy
    /// build ngay, thay vì âm thầm trả cột rỗng cho client.</summary>
    private static HubOrderItem ToHubOrderItem(OrderRecord o) => new()
    {
        Id = o.Id,
        ShopId = o.ShopId,
        OrderSn = o.OrderSn,
        ShopeeOrderId = o.ShopeeOrderId,
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
        Channel = o.Channel,
        Carrier = o.Carrier,
        TrackingNumber = o.TrackingNumber,
        SyncedAt = o.SyncedAt,
        SlipAt = o.SlipAt,
    };

    /// <summary>Map <see cref="Shop"/> (kiểu nội bộ hub) → <see cref="HubShopItem"/> (DTO dùng chung). CỐ Ý chỉ
    /// lấy 3 field hiển thị — password/cookie/proxy_key KHÔNG đi qua dây.</summary>
    private static HubShopItem ToHubShopItem(Shop s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Username = s.Username,
    };

    /// <summary>Trần kích thước một file phiếu hub nhận qua POST /api/orders/slip (khớp trần client đọc phiếu).</summary>
    private const long MaxSlipBytes = 5 * 1024 * 1024;

    /// <summary>True nếu 5 byte đầu là magic <c>%PDF-</c> — nhận đúng PDF thật, tránh lưu HTML/redirect (GET lại
    /// phiếu có thể ra HTML 200-OK) làm rác.</summary>
    private static bool LooksPdf(ReadOnlySpan<byte> b)
        => b.Length >= 5 && b[0] == (byte)'%' && b[1] == (byte)'P'
           && b[2] == (byte)'D' && b[3] == (byte)'F' && b[4] == (byte)'-';

    /// <summary>Fire-and-forget báo "đơn mới" (webhook đơn mới + fallback legacy multiline).</summary>
    private static void FireNotifyDonMoi(HubDatabase db, WebhookQueueService queue, string mid, string shopName, IReadOnlyList<OrderPushItem> inserted)
    {
        if (inserted.Count == 0) return;
        var moTa = $"{inserted.Count} đơn mới ({shopName})";
        var urls = ResolveWebhooks(db, SettingKeys.NotifyWebhookDonMoi);
        if (urls.Count == 0) { LogChuaCauHinh(db, mid, "đơn mới", SettingKeys.NotifyWebhookDonMoi, moTa); return; }
        var text = OrderNotifyService.TaoTinNhanDonMoi(shopName, inserted.Select(ToSyncedOrder).ToList(), GioVietNam.BayGio());
        QueueSend(queue, mid, urls, text, "đơn mới", moTa);
    }

    /// <summary>Fire-and-forget báo "đơn trả hàng" khi mã trả mới/đổi sau push.</summary>
    private static void FireNotifyDonTra(HubDatabase db, WebhookQueueService queue, string mid, string shopName, IReadOnlyList<OrderPushItem> items)
    {
        if (items.Count == 0) return;
        var pairs = items
            .Where(o => !string.IsNullOrWhiteSpace(o.ReturnRequestCode))
            .Select(o => (o.OrderSn, o.ReturnRequestCode!.Trim()))
            .ToList();
        if (pairs.Count == 0) return;

        var moTa = $"{pairs.Count} đơn trả ({shopName})";
        var urls = ResolveWebhooks(db, SettingKeys.NotifyWebhookDonTra);
        if (urls.Count == 0) { LogChuaCauHinh(db, mid, "đơn trả", SettingKeys.NotifyWebhookDonTra, moTa); return; }
        var text = OrderNotifyService.TaoTinNhanDonTra(shopName, pairs, GioVietNam.BayGio());
        QueueSend(queue, mid, urls, text, "đơn trả", moTa);
    }

    /// <summary><see cref="OrdersAppAlertRequest.Kind"/> đi kênh ĐƠN TRẢ thay vì kênh lỗi app.</summary>
    private const string KindDonTra = "don_tra";

    /// <summary>
    /// Fire-and-forget báo "đơn trả hàng" từ app-alert <c>Kind="don_tra"</c>: client gửi mã trả của các đơn ĐÃ bị
    /// dọn khỏi app (không còn đi qua <c>orders/push</c> nên hub không tự phát hiện được). Dùng ĐÚNG kênh + định
    /// dạng tin của <see cref="FireNotifyDonTra"/>; <c>ShopName</c> = shop_login, <c>Detail</c> = các cặp
    /// <c>SN=CODE</c> ngăn bằng <c>;</c>. Không tách được cặp nào → thôi (không bắn tin rỗng).
    /// </summary>
    private static void FireNotifyDonTraTuAppAlert(HubDatabase db, WebhookQueueService queue, string mid, OrdersAppAlertRequest r)
    {
        var pairs = TachCapDonTra(r.Detail);
        if (pairs.Count == 0) return;

        var shopName = string.IsNullOrWhiteSpace(r.ShopName) ? "?" : r.ShopName!.Trim();
        var moTa = $"{pairs.Count} đơn trả ({shopName})";
        var urls = ResolveWebhooks(db, SettingKeys.NotifyWebhookDonTra);
        if (urls.Count == 0) { LogChuaCauHinh(db, mid, "đơn trả", SettingKeys.NotifyWebhookDonTra, moTa); return; }
        var text = OrderNotifyService.TaoTinNhanDonTra(shopName, pairs, GioVietNam.BayGio());
        QueueSend(queue, mid, urls, text, "đơn trả", moTa);
    }

    /// <summary>HÀM THUẦN (test được): tách <c>"SN1=CODE1; SN2=CODE2"</c> thành các cặp (mã đơn, mã yêu cầu). Bỏ
    /// phần tử thiếu <c>=</c> hoặc có vế rỗng; null/rỗng → list rỗng.</summary>
    internal static List<(string MaDon, string MaYeuCau)> TachCapDonTra(string? detail)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(detail)) return list;
        foreach (var phan in detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = phan.IndexOf('=');
            if (eq <= 0) continue;
            var sn = phan[..eq].Trim();
            var code = phan[(eq + 1)..].Trim();
            if (sn.Length > 0 && code.Length > 0) list.Add((sn, code));
        }
        return list;
    }

    /// <summary>Fire-and-forget báo lỗi app (hiện: không đặt được địa chỉ).</summary>
    private static void FireNotifyLoiApp(HubDatabase db, WebhookQueueService queue, string mid, OrdersAppAlertRequest r)
    {
        var moTa = $"lỗi app {r.Kind?.Trim()} ({r.AccountLabel} · {r.ShopName})";
        var urls = ResolveWebhooks(db, SettingKeys.NotifyWebhookLoiApp);
        if (urls.Count == 0) { LogChuaCauHinh(db, mid, "lỗi app", SettingKeys.NotifyWebhookLoiApp, moTa); return; }

        string text;
        if (string.Equals(r.Kind?.Trim(), "khong_dat_duoc_dia_chi", StringComparison.OrdinalIgnoreCase))
        {
            text = OrderNotifyService.TaoTinNhanLoiDiaChi(
                r.AccountLabel ?? "", r.ShopName ?? "", r.Detail ?? "", r.MachineName ?? "", GioVietNam.BayGio());
        }
        else
        {
            text = $"⚠ Lỗi app ({r.Kind})\nMáy: {r.MachineName ?? "?"} · Tài khoản: {r.AccountLabel ?? "?"} · Shop: {r.ShopName ?? "?"}\n{r.Detail}";
        }
        QueueSend(queue, mid, urls, text, "lỗi app", moTa);
    }

    /// <summary>Ghi log CẢNH BÁO khi có sự kiện notify mà không giải ra được URL nào (cả ô riêng lẫn ô legacy đều
    /// trống) — trước đây hàm <c>FireNotify*</c> lặng lẽ <c>return</c>, nhìn từ ngoài tưởng client không gửi.
    /// MỘT dòng cho mỗi LÔ/sự kiện (không phải mỗi đơn) nên không ngập log.</summary>
    private static void LogChuaCauHinh(HubDatabase db, string mid, string nhan, string keyRieng, string moTa)
        => db.AppendLog(new AppendLogRequest(mid, "", "warn",
            $"notify \"{nhan}\": có {moTa} nhưng CHƯA cấu hình webhook (trống cả {keyRieng} lẫn {SettingKeys.NotifyWebhooks})"
            + " — điền ô webhook tương ứng ở Hub → Cài đặt"));

    /// <summary>Enqueue MỘT lô webhook; worker nền gửi tuần tự và ghi kết quả.</summary>
    private static void QueueSend(WebhookQueueService queue, string mid, IReadOnlyList<string> urls,
        string text, string nhan, string moTa)
        => queue.TryQueue(new WebhookNotification(mid, urls, text, nhan, moTa));

    /// <summary>URL(s) của MỘT kênh notify: ô riêng <paramref name="keyRieng"/> có giá trị → dùng đúng nó (1 URL);
    /// trống → lùi về toàn bộ dòng của ô LEGACY <c>notify.webhooks</c> (mỗi dòng 1 URL, gửi TẤT CẢ). Lưới an toàn
    /// cho lúc admin chưa tách kênh — không có nó thì kênh chưa điền ô riêng sẽ chết âm thầm.</summary>
    private static IReadOnlyList<string> ResolveWebhooks(HubDatabase db, string keyRieng)
        => ResolveWebhooks(db.GetSetting(keyRieng), db.GetSetting(SettingKeys.NotifyWebhooks));

    /// <summary>HÀM THUẦN (test được) của <see cref="ResolveWebhooks(HubDatabase, string)"/>.</summary>
    internal static IReadOnlyList<string> ResolveWebhooks(string? rieng, string? legacy)
    {
        var r = rieng?.Trim();
        if (!string.IsNullOrEmpty(r)) return new[] { r };
        if (string.IsNullOrWhiteSpace(legacy)) return Array.Empty<string>();
        return legacy.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => u.Length > 0)
            .ToList();
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
        ReturnRequestCode = o.ReturnRequestCode,
    };
}
