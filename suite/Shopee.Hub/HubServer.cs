using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;

namespace Shopee.Hub;

/// <summary>
/// Mini-server Hub chạy NHÚNG trong app (Kestrel + minimal API) khi máy bật chế độ Hub. Lắng nghe
/// 127.0.0.1:Port (chỉ local + qua Cloudflare Tunnel), bảo vệ bằng header X-Api-Token. Start/Stop
/// điều khiển từ app WPF; mọi dữ liệu nằm trong <see cref="HubDatabase"/>.
/// </summary>
public sealed class HubServer
{
    /// <summary>Trần kích thước 1 lần upload (workbook Excel có thể lớn). Có chặn trên để tránh OOM/đầy đĩa do client lỗi/độc.</summary>
    private const long MaxUploadBytes = 256L * 1024 * 1024;

    private WebApplication? _app;
    private HubDatabase? _db;

    public bool Running => _app is not null;
    public string? DataDir { get; private set; }

    public async Task StartAsync(HubServerConfig cfg)
    {
        if (_app is not null) return;

        var dataDir = string.IsNullOrWhiteSpace(cfg.DataDir) ? SuitePaths.ModuleDir("hub-data") : cfg.DataDir;
        DataDir = dataDir;
        var db = new HubDatabase(dataDir);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Mặc định Kestrel ~28.6MB → workbook lớn bị 413 và đồng bộ "im lặng" không xong. Nâng trần (có chặn trên).
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);
        var app = builder.Build();
        app.Urls.Add($"http://127.0.0.1:{cfg.Port}");

        var token = cfg.ApiToken ?? "";
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == "/health") { await next(); return; }
            var provided = ctx.Request.Headers["X-Api-Token"].ToString();
            if (string.IsNullOrEmpty(token) || !string.Equals(provided, token, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync("unauthorized");
                return;
            }
            await next();
        });

        Map(app, db);

        _db = db;
        _app = app;
        await app.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_app is not null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
            try { await _app.DisposeAsync(); } catch { }
            _app = null;
        }
        _db?.Dispose();
        _db = null;
        DataDir = null;
    }

    private static void Map(WebApplication app, HubDatabase db)
    {
        app.MapGet("/health", () => Results.Json(new { ok = true, ts = DateTimeOffset.UtcNow }));

        // ── File-sync (manifest + blob) ──
        app.MapGet("/manifest", () => Results.Json(db.ListFiles()));
        app.MapGet("/files/{*name}", (string name) =>
        {
            var bytes = db.ReadFile(name);
            return bytes is null ? Results.NotFound() : Results.Bytes(bytes, "application/octet-stream");
        });
        app.MapPut("/files/{*name}", async (string name, HttpRequest req) =>
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            int? ifMatch = int.TryParse(req.Headers["If-Match"].ToString(), out var v) ? v : null;
            var updatedBy = req.Headers["X-Machine-Id"].ToString();
            var res = db.PutFile(name, ms.ToArray(), ifMatch, updatedBy);
            return res.Ok ? Results.Json(res) : Results.Json(res, statusCode: StatusCodes.Status409Conflict);
        });

        // ── Khoá việc theo shop+op ── (body null/sai → 400 thay vì NRE 500)
        app.MapPost("/leases/acquire", (LeaseAcquireRequest? r) => r is null ? Results.BadRequest() : Results.Json(db.AcquireLease(r)));
        app.MapPost("/leases/heartbeat", (LeaseHeartbeatRequest? r) => { if (r is null) return Results.BadRequest(); db.HeartbeatLease(r.Key, r.MachineId); return Results.Ok(); });
        app.MapPost("/leases/release", (LeaseReleaseRequest? r) => { if (r is null) return Results.BadRequest(); db.ReleaseLease(r.Key, r.MachineId); return Results.Ok(); });

        // ── Khoá tài khoản Shopee (chống dùng trùng xuyên máy) ──
        app.MapPost("/accounts/reserve", (AccountReserveRequest? r) => r?.AccountIds is null ? Results.BadRequest() : Results.Json(db.ReserveAccounts(r)));
        app.MapPost("/accounts/release", (AccountReleaseRequest? r) => { if (r?.AccountIds is null) return Results.BadRequest(); db.ReleaseAccounts(r); return Results.Ok(); });
        app.MapPost("/accounts/heartbeat", (AccountReleaseRequest? r) => { if (r?.AccountIds is null) return Results.BadRequest(); db.HeartbeatAccounts(r); return Results.Ok(); });
        app.MapGet("/accounts/active", () => Results.Json(db.ActiveAccountLeases()));

        // ── Sổ hoàn thành ──
        app.MapPost("/ledger", (WorkLedgerRecord? r) => { if (r is null) return Results.BadRequest(); db.PublishLedger(r); return Results.Ok(); });
        app.MapPost("/ledger/set", (SetLedgerStatusRequest? r) => { if (r is null) return Results.BadRequest(); db.SetLedgerStatus(r.Key, r.BigsellerId, r.ShopId, r.Sheet, r.Op, r.Status); return Results.Ok(); });
        app.MapGet("/ledger", () => Results.Json(db.AllLedger()));

        // ── Nhịp máy + bảng trạng thái ──
        app.MapPost("/machines/heartbeat", (MachineHeartbeatRequest? r) => { if (r is null) return Results.BadRequest(); db.MachineHeartbeat(r); return Results.Ok(); });
        app.MapPost("/machines/leave", (MachineLeaveRequest? r) => { if (r is null) return Results.BadRequest(); db.RemoveMachine(r.MachineId); return Results.Ok(); });
        app.MapGet("/fleet", () => Results.Json(db.Fleet()));

        // ── Vai trò máy + giao việc ── (body null/sai → 400 thay vì NRE 500)
        app.MapGet("/roles", () => Results.Json(db.AllRoles()));
        app.MapPost("/roles", (SetRoleRequest? r) => { if (r is null) return Results.BadRequest(); db.SetRole(r.MachineId, r.Role); return Results.Ok(); });
        app.MapGet("/assignments", () => Results.Json(db.ListAssignments()));
        app.MapPost("/assignments", (CreateAssignmentRequest? r) => r is null ? Results.BadRequest() : Results.Json(db.CreateAssignment(r)));
        app.MapPost("/assignments/claim", (ClaimAssignmentsRequest? r) => r is null ? Results.BadRequest() : Results.Json(db.ClaimNext(r.MachineId, r.Role, r.Max)));
        app.MapPost("/assignments/status", (AssignmentStatusRequest? r) => { if (r is null) return Results.BadRequest(); db.UpdateAssignmentStatus(r.Id, r.MachineId, r.Status, r.Error); return Results.Ok(); });
        app.MapPost("/assignments/cancel", (CancelAssignmentRequest? r) => { if (r is null) return Results.BadRequest(); db.CancelAssignment(r.Id); return Results.Ok(); });

        // ── Kho gộp kết quả Search (client đẩy sản phẩm → Hub gộp) ──
        app.MapPost("/search-products", (SearchProductsPushRequest? r) => { if (r is null) return Results.BadRequest(); db.SaveSearchProducts(r); return Results.Ok(); });
        app.MapGet("/search-products", () => Results.Json(db.AllSearchProductJson()));
        app.MapGet("/search-products/count", () => Results.Json(db.SearchProductCount()));
        app.MapPost("/search-products/clear", () => { db.ClearSearchProducts(); return Results.Ok(); });

        // ── Log tập trung (nhiều máy gửi → Hub gom, tab Log xem) ──
        app.MapPost("/logs", (AppendLogRequest? r) => { if (r is null) return Results.BadRequest(); db.AppendLog(r); return Results.Ok(); });
        app.MapGet("/logs", (long? after, int? max) => Results.Json(db.GetLogs(after ?? 0, Math.Clamp(max ?? 300, 1, 1000))));
        app.MapPost("/logs/clear", () => { db.ClearLogs(); return Results.Ok(); });

        // ── Client báo acc Shopee lỗi/captcha ──
        app.MapPost("/accounts/errored", (AccountErrorRequest? r) => { if (r is null) return Results.BadRequest(); db.ReportAccountError(r); return Results.Ok(); });
        app.MapGet("/accounts/errored", () => Results.Json(db.AllAccountErrors()));
        app.MapPost("/accounts/errored/clear", (ClearAccountErrorRequest? r) => { if (r is null) return Results.BadRequest(); db.ClearAccountError(r.AccountId); return Results.Ok(); });
    }
}
