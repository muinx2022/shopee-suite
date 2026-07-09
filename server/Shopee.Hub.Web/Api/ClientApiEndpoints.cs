using Shopee.Core.Accounts;
using Shopee.Core.Coordination;
using Shopee.Hub;
using Shopee.Hub.Web.Auth;
using Shopee.Hub.Web.Services;

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
        // /health — KHÔNG auth (client dò kết nối trước khi có token).
        app.MapGet("/health", () => Results.Json(new { ok = true, ts = DateTimeOffset.UtcNow })).AllowAnonymous();

        // Gom mọi route client vào 1 group yêu cầu policy "Client" (X-Api-Token).
        var api = app.MapGroup("").RequireAuthorization("Client");

        // ── File-sync (manifest + blob) ──
        api.MapGet("/manifest", () => Results.Json(db.ListFiles()));
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
        api.MapPost("/leases/acquire", (LeaseAcquireRequest? r) => r is null ? Results.BadRequest() : Results.Json(db.AcquireLease(r)));
        api.MapPost("/leases/heartbeat", (LeaseHeartbeatRequest? r) => { if (r is null) return Results.BadRequest(); db.HeartbeatLease(r.Key, r.MachineId); return Results.Ok(); });
        api.MapPost("/leases/release", (LeaseReleaseRequest? r) => { if (r is null) return Results.BadRequest(); db.ReleaseLease(r.Key, r.MachineId); return Results.Ok(); });

        // ── Khoá tài khoản Shopee ──
        api.MapPost("/accounts/reserve", (AccountReserveRequest? r) => r?.AccountIds is null ? Results.BadRequest() : Results.Json(db.ReserveAccounts(r)));
        api.MapPost("/accounts/release", (AccountReleaseRequest? r) => { if (r?.AccountIds is null) return Results.BadRequest(); db.ReleaseAccounts(r); return Results.Ok(); });
        api.MapPost("/accounts/heartbeat", (AccountReleaseRequest? r) => { if (r?.AccountIds is null) return Results.BadRequest(); db.HeartbeatAccounts(r); return Results.Ok(); });

        // ── Sổ hoàn thành ──
        api.MapPost("/ledger", (WorkLedgerRecord? r) => { if (r is null) return Results.BadRequest(); db.PublishLedger(r); return Results.Ok(); });
        api.MapPost("/ledger/set", (SetLedgerStatusRequest? r) => { if (r is null) return Results.BadRequest(); db.SetLedgerStatus(r.Key, r.BigsellerId, r.ShopId, r.Sheet, r.Op, r.Status); return Results.Ok(); });
        api.MapGet("/ledger", () => Results.Json(db.AllLedger()));

        // ── Nhịp máy + bảng trạng thái ──
        api.MapPost("/machines/heartbeat", (MachineHeartbeatRequest? r) => { if (r is null) return Results.BadRequest(); db.MachineHeartbeat(r); return Results.Ok(); });
        api.MapPost("/machines/leave", (MachineLeaveRequest? r) => { if (r is null) return Results.BadRequest(); db.RemoveMachine(r.MachineId); return Results.Ok(); });
        api.MapGet("/fleet", () => Results.Json(db.Fleet()));

        // ── Vai trò máy + giao việc ──
        api.MapPost("/roles", (SetRoleRequest? r) => { if (r is null) return Results.BadRequest(); db.SetRole(r.MachineId, r.Role); return Results.Ok(); });
        api.MapPost("/assignments", (CreateAssignmentRequest? r) => r is null ? Results.BadRequest() : Results.Json(db.CreateAssignment(r)));
        api.MapPost("/assignments/claim", (ClaimAssignmentsRequest? r) => r is null ? Results.BadRequest() : Results.Json(db.ClaimNext(r.MachineId, r.Role, r.Max)));
        api.MapPost("/assignments/status", (AssignmentStatusRequest? r) => { if (r is null) return Results.BadRequest(); db.UpdateAssignmentStatus(r.Id, r.MachineId, r.Status, r.Error); return Results.Ok(); });
        api.MapPost("/assignments/cancel", (CancelAssignmentRequest? r) => { if (r is null) return Results.BadRequest(); db.CancelAssignment(r.Id); return Results.Ok(); });

        // ── Kho gộp kết quả Search ──
        api.MapPost("/search-products", (SearchProductsPushRequest? r) => { if (r is null) return Results.BadRequest(); db.SaveSearchProducts(r); return Results.Ok(); });
        api.MapGet("/search-products", () => Results.Json(db.AllSearchProductJson()));
        api.MapGet("/search-products/count", () => Results.Json(db.SearchProductCount()));
        api.MapPost("/search-products/clear", () => { db.ClearSearchProducts(); return Results.Ok(); });

        // ── Log tập trung ──
        api.MapPost("/logs", (AppendLogRequest? r) => { if (r is null) return Results.BadRequest(); db.AppendLog(r); return Results.Ok(); });
        api.MapGet("/logs", (long? after, int? max) => Results.Json(db.GetLogs(after ?? 0, Math.Clamp(max ?? 300, 1, 1000))));
        api.MapPost("/logs/clear", () => { db.ClearLogs(); return Results.Ok(); });

        // ── Client báo acc Shopee lỗi/captcha ──
        api.MapPost("/accounts/errored", (AccountErrorRequest? r) => { if (r is null) return Results.BadRequest(); db.ReportAccountError(r); return Results.Ok(); });
        api.MapGet("/accounts/errored", () => Results.Json(db.AllAccountErrors()));
        api.MapPost("/accounts/errored/clear", (ClearAccountErrorRequest? r) => { if (r is null) return Results.BadRequest(); db.ClearAccountError(r.AccountId); return Results.Ok(); });

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

        // ── MỚI: xem/đổi trạng thái điều phối (web UI + có thể client đọc) ──
        api.MapGet("/dispatcher", (DispatcherService d) => Results.Json(new { enabled = d.Enabled, auto = d.AutoMode }));
        api.MapPost("/dispatcher", (DispatcherStateRequest? r, DispatcherService d) =>
        {
            if (r is null) return Results.BadRequest();
            d.Enabled = r.Enabled; d.AutoMode = r.Auto;
            return Results.Ok();
        });
    }
}

/// <summary>Body cho /accounts/remove.</summary>
public sealed record AccountRemoveRequest(string Id);
/// <summary>Body cho POST /dispatcher.</summary>
public sealed record DispatcherStateRequest(bool Enabled, bool Auto);
