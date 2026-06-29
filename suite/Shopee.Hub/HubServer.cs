using Microsoft.AspNetCore.Builder;
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

        // ── Khoá việc theo shop+op ──
        app.MapPost("/leases/acquire", (LeaseAcquireRequest r) => Results.Json(db.AcquireLease(r)));
        app.MapPost("/leases/heartbeat", (LeaseHeartbeatRequest r) => { db.HeartbeatLease(r.Key, r.MachineId); return Results.Ok(); });
        app.MapPost("/leases/release", (LeaseReleaseRequest r) => { db.ReleaseLease(r.Key, r.MachineId); return Results.Ok(); });

        // ── Khoá tài khoản Shopee (chống dùng trùng xuyên máy) ──
        app.MapPost("/accounts/reserve", (AccountReserveRequest r) => Results.Json(db.ReserveAccounts(r)));
        app.MapPost("/accounts/release", (AccountReleaseRequest r) => { db.ReleaseAccounts(r); return Results.Ok(); });
        app.MapPost("/accounts/heartbeat", (AccountReleaseRequest r) => { db.HeartbeatAccounts(r); return Results.Ok(); });
        app.MapGet("/accounts/active", () => Results.Json(db.ActiveAccountLeases()));

        // ── Sổ hoàn thành ──
        app.MapPost("/ledger", (WorkLedgerRecord r) => { db.PublishLedger(r); return Results.Ok(); });
        app.MapGet("/ledger", () => Results.Json(db.AllLedger()));

        // ── Nhịp máy + bảng trạng thái ──
        app.MapPost("/machines/heartbeat", (MachineHeartbeatRequest r) => { db.MachineHeartbeat(r); return Results.Ok(); });
        app.MapGet("/fleet", () => Results.Json(db.Fleet()));

        // ── Vai trò máy + giao việc ──
        app.MapGet("/roles", () => Results.Json(db.AllRoles()));
        app.MapPost("/roles", (SetRoleRequest r) => { db.SetRole(r.MachineId, r.Role); return Results.Ok(); });
        app.MapGet("/assignments", () => Results.Json(db.ListAssignments()));
        app.MapPost("/assignments", (CreateAssignmentRequest r) => Results.Json(db.CreateAssignment(r)));
        app.MapPost("/assignments/claim", (ClaimAssignmentsRequest r) => Results.Json(db.ClaimNext(r.MachineId, r.Role, r.Max)));
        app.MapPost("/assignments/status", (AssignmentStatusRequest r) => { db.UpdateAssignmentStatus(r.Id, r.MachineId, r.Status, r.Error); return Results.Ok(); });
        app.MapPost("/assignments/cancel", (CancelAssignmentRequest r) => { db.CancelAssignment(r.Id); return Results.Ok(); });
    }
}
