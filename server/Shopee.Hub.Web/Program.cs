using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Shopee.Hub;
using Shopee.Hub.Web.Api;
using Shopee.Hub.Web.Auth;
using Shopee.Hub.Web.Components;
using Shopee.Hub.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Chạy được cả systemd (Linux) lẫn Windows Service — no-op khi không áp dụng.
builder.Host.UseSystemd();
builder.Host.UseWindowsService();

// ── Thư mục dữ liệu: hub.db + files\ + dp-keys\ + backups\ ──
var dataDir = Environment.GetEnvironmentVariable("HUB_DATA_DIR")
              ?? builder.Configuration["Hub:DataDir"];
if (string.IsNullOrWhiteSpace(dataDir))
    dataDir = Path.Combine(builder.Environment.ContentRootPath, "hub-data");
dataDir = Path.GetFullPath(dataDir);
Directory.CreateDirectory(dataDir);

var hubOptions = new HubOptions
{
    DataDir = dataDir,
    AllowClientConfigPush = builder.Configuration.GetValue("Hub:AllowClientConfigPush", true),
};

// Trần upload = 256MB (workbook Excel lớn). Giống hub nhúng cũ (mặc định Kestrel ~28MB gây 413).
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 256L * 1024 * 1024);

// ── DI ──
var db = new HubDatabase(dataDir);
builder.Services.AddSingleton(db);
builder.Services.AddSingleton(hubOptions);
builder.Services.AddSingleton<FileStoreConfigService>();
builder.Services.AddSingleton<SearchBoardService>();
builder.Services.AddSingleton<BigSellerLoginService>();
// Rewrite tên chạy NGAY TRÊN HUB (hub quản toàn bộ dữ liệu Postgres) — 1 worker nền, resolve ProductDb tuỳ lúc
// (có thể chưa cấu hình Postgres). Đăng ký LUÔN (không phụ thuộc pgConn) → UI hiện được cả khi pg chưa sẵn sàng.
builder.Services.AddSingleton<RewriteJobService>();
builder.Services.AddSingleton<AdminAccountService>();
builder.Services.AddSingleton<LoginRateLimit>();
// Đọc bản release mới nhất từ GitHub (cache) → trang /machines đánh dấu máy cũ + ra lệnh update.
builder.Services.AddSingleton<LatestReleaseService>();

// ── Postgres (kho sản phẩm — Docker cạnh hub): env HUB_PG_CONN ưu tiên → config Hub:PgConn → rỗng = KHÔNG bật
// (hub chạy y như cũ, không cần Postgres). Cùng pattern HUB_DATA_DIR ở trên. ──
var pgConn = Environment.GetEnvironmentVariable("HUB_PG_CONN")
             ?? builder.Configuration["Hub:PgConn"];
ProductDb? productDb = null;
if (!string.IsNullOrWhiteSpace(pgConn))
{
    productDb = new ProductDb(pgConn);
    builder.Services.AddSingleton(productDb);
    builder.Services.AddHostedService<ProductDbInitService>();   // connect + migrate ở nền, retry tới khi Postgres lên
}

// FleetStateService: vừa là IHostedService (nền refresh 2s) vừa được inject vào trang Blazor → 1 singleton.
builder.Services.AddSingleton<FleetStateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FleetStateService>());
// SheetMapService: đọc + cache cấu trúc dòng workbook cho "bản đồ dòng" trang Thống kê.
builder.Services.AddSingleton<SheetMapService>();
// DispatcherService: BackgroundService + được inject (endpoint /dispatcher, trang Fleet) → 1 singleton.
builder.Services.AddSingleton<DispatcherService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DispatcherService>());
builder.Services.AddHostedService<MaintenanceService>();
// Re-login BigSeller định kỳ ~7 ngày, RẢI 1 acc/giờ → device-trust không hết hạn, không bị đòi mã email đồng loạt.
builder.Services.AddHostedService<BigSellerReloginScheduler>();

// Khoá Data Protection lưu ra đĩa → cookie đăng nhập web sống qua restart/redeploy.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "dp-keys")))
    .SetApplicationName("ShopeeHubWeb");

// ── Xác thực 2 scheme song song ──
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.LogoutPath = "/logout";
        o.AccessDeniedPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.Cookie.Name = "shopee_hub_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    })
    .AddScheme<ApiTokenOptions, ApiTokenHandler>(ApiTokenHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(o =>
{
    // Client API: chỉ scheme X-Api-Token.
    o.AddPolicy("Client", p => p
        .AddAuthenticationSchemes(ApiTokenHandler.SchemeName)
        .RequireAuthenticatedUser());
    // Web UI: chỉ scheme cookie.
    o.AddPolicy("Web", p => p
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser());
});
builder.Services.AddCascadingAuthenticationState();

// Cloudflared chạy cùng máy (loopback) → tin proxy để lấy IP thực (rate-limit login) + scheme.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                         | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

// db đăng ký AddSingleton(instance) nên DI KHÔNG tự Dispose khi tắt → tự đăng ký để đóng SqliteConnection sạch.
app.Lifetime.ApplicationStopped.Register(db.Dispose);
// ProductDb cũng AddSingleton(instance) → tự đóng NpgsqlDataSource sạch khi tắt (nếu có cấu hình Postgres).
if (productDb is not null)
    app.Lifetime.ApplicationStopped.Register(() => productDb.DisposeAsync().AsTask().GetAwaiter().GetResult());

// Seed token API + admin (từ env) nếu DB chưa có.
{
    var token = Environment.GetEnvironmentVariable("HUB_API_TOKEN");
    if (!string.IsNullOrWhiteSpace(token) && string.IsNullOrEmpty(db.GetSetting(SettingKeys.ApiToken)))
        db.SetSetting(SettingKeys.ApiToken, token);
    app.Services.GetRequiredService<AdminAccountService>().SeedFromEnvIfEmpty();
}

app.UseForwardedHeaders();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ── Đăng nhập / cài đặt admin (minimal endpoints, HTML thuần — KHÔNG cần Blazor để render) ──
app.MapGet("/login", (HttpContext ctx, AdminAccountService admin) =>
{
    if (!admin.HasAdmin) return Results.Redirect("/setup");
    var err = ctx.Request.Query["e"].ToString() == "1" ? "Sai tên đăng nhập hoặc mật khẩu." : "";
    return Results.Content(LoginPages.Login(err), "text/html; charset=utf-8");
}).AllowAnonymous();

app.MapPost("/login", async (HttpContext ctx, AdminAccountService admin, LoginRateLimit rl) =>
{
    var ip = LoginRateLimit.IpOf(ctx);
    if (!rl.Allow(ip)) return Results.Content(LoginPages.Login("Thử quá nhiều lần. Đợi vài phút rồi thử lại."), "text/html; charset=utf-8");
    var form = await ctx.Request.ReadFormAsync();
    var user = form["u"].ToString();
    var pass = form["p"].ToString();
    if (!admin.Verify(user, pass))
    {
        rl.RecordFailure(ip);
        return Results.Redirect("/login?e=1");
    }
    rl.Reset(ip);
    var claims = new[] { new Claim(ClaimTypes.Name, user) };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true });
    return Results.Redirect("/");
}).AllowAnonymous().DisableAntiforgery();

app.MapGet("/setup", (AdminAccountService admin) =>
    admin.HasAdmin ? Results.NotFound()
        : Results.Content(LoginPages.Setup(), "text/html; charset=utf-8")).AllowAnonymous();

app.MapPost("/setup", async (HttpContext ctx, AdminAccountService admin) =>
{
    if (admin.HasAdmin) return Results.NotFound();
    var form = await ctx.Request.ReadFormAsync();
    var user = form["u"].ToString();
    var pass = form["p"].ToString();
    if (string.IsNullOrWhiteSpace(user) || pass.Length < 6)
        return Results.Content(LoginPages.Setup("Tên đăng nhập trống hoặc mật khẩu < 6 ký tự."), "text/html; charset=utf-8");
    admin.SetAdmin(user, pass);
    return Results.Redirect("/login");
}).AllowAnonymous().DisableAntiforgery();

app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// ── Tải file cho ADMIN qua trình duyệt (khoá cookie, khác /files/{*name} của client dùng X-Api-Token) ──
app.MapGet("/dl/{*name}", (string name, HubDatabase database) =>
{
    var stream = database.OpenFileRead(name);
    if (stream is null) return Results.NotFound();
    var download = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
    return Results.Stream(stream, "application/octet-stream", download);
}).RequireAuthorization("Web");

app.MapGet("/exports/{name}", (string name, HubOptions opts) =>
{
    // Chặn traversal: chỉ tên file trần trong exports\.
    if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return Results.BadRequest();
    var path = Path.Combine(opts.DataDir, "exports", name);
    return File.Exists(path)
        ? Results.File(path, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name)
        : Results.NotFound();
}).RequireAuthorization("Web");

// ── Chẩn đoán Chromium/Playwright (admin) — verify hạ tầng login BigSeller trên VM ──
app.MapGet("/admin/chromium-test", async (BigSellerLoginService svc) =>
    Results.Text(await svc.DiagnoseAsync())).RequireAuthorization("Web");

// ── API client (giữ nguyên giao thức) ──
app.MapClientApi(db);

// ── API kho sản phẩm (Postgres) — 503 pg-not-ready nếu chưa cấu hình/chưa migrate ──
app.MapProductApi();

// ── Blazor UI (khoá sau cookie) ──
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization("Web");

app.Run();

/// <summary>Trang HTML tĩnh cho login/setup (không dùng Blazor để tránh vòng lặp auth với cookie).</summary>
internal static class LoginPages
{
    private const string Style = """
        <script>(function(){try{var t=localStorage.getItem('theme');if(t==='dark')document.documentElement.classList.add('dark');else if(t==='light')document.documentElement.classList.add('light');}catch(e){}})();</script>
        <style>
          :root{color-scheme:light;--bg:#f1f5f9;--card:#fff;--text:#1c2434;--sub:#64748b;--stroke:#e2e8f0;--lbl:#334155;--err-bg:#fbe9eb;--err-tx:#b3283a;--err-bd:#f0b8bf}
          html.dark{color-scheme:dark;--bg:#0f1623;--card:#1a2332;--text:#e7edf6;--sub:#97a6bd;--stroke:#2a3648;--lbl:#97a6bd;--err-bg:rgba(248,113,113,.16);--err-tx:#f87171;--err-bd:rgba(248,113,113,.35)}
          @media (prefers-color-scheme:dark){html:not(.light){color-scheme:dark;--bg:#0f1623;--card:#1a2332;--text:#e7edf6;--sub:#97a6bd;--stroke:#2a3648;--lbl:#97a6bd;--err-bg:rgba(248,113,113,.16);--err-tx:#f87171;--err-bd:rgba(248,113,113,.35)}}
          *{box-sizing:border-box}
          body{font-family:"Inter",system-ui,-apple-system,Segoe UI,Roboto,sans-serif;display:flex;min-height:100vh;
               align-items:center;justify-content:center;margin:0;background:var(--bg);color:var(--text)}
          .card{background:var(--card);padding:34px 32px;border-radius:14px;width:340px;
                box-shadow:0 10px 40px rgba(16,24,40,.1),0 1px 3px rgba(16,24,40,.06);border:1px solid var(--stroke)}
          .logo{width:46px;height:46px;border-radius:12px;background:linear-gradient(135deg,#3c50e0,#6f81f0);
                display:flex;align-items:center;justify-content:center;color:#fff;font-weight:800;font-size:23px;
                margin-bottom:16px;box-shadow:0 6px 16px rgba(60,80,224,.4)}
          h1{font-size:20px;margin:0 0 4px;color:var(--text)} p.sub{margin:0 0 22px;color:var(--sub);font-size:13px}
          label{display:block;font-size:13px;margin:14px 0 5px;color:var(--lbl);font-weight:500}
          input{width:100%;padding:11px 12px;border-radius:8px;border:1px solid var(--stroke);background:var(--card);
                color:var(--text);font-size:16px;transition:border-color .13s,box-shadow .13s}
          input:focus{outline:none;border-color:#3c50e0;box-shadow:0 0 0 3px rgba(60,80,224,.1)}
          button{width:100%;margin-top:22px;padding:12px;border:0;border-radius:8px;background:#3c50e0;
                 color:#fff;font-size:14px;font-weight:600;cursor:pointer;transition:background .13s}
          button:hover{background:#2f41c4}
          .err{background:var(--err-bg);color:var(--err-tx);padding:9px 12px;border-radius:8px;font-size:13px;margin-bottom:14px;border:1px solid var(--err-bd)}
        </style>
        """;

    public static string Login(string err) => $"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Đăng nhập · Shopee Hub</title>{Style}</head><body>
        <form class="card" method="post" action="/login">
          <div class="logo">S</div>
          <h1>Shopee Hub</h1><p class="sub">Điều khiển fleet đa máy</p>
          {(string.IsNullOrEmpty(err) ? "" : $"<div class=\"err\">{System.Net.WebUtility.HtmlEncode(err)}</div>")}
          <label>Tên đăng nhập</label><input name="u" autofocus autocomplete="username">
          <label>Mật khẩu</label><input name="p" type="password" autocomplete="current-password">
          <button type="submit">Đăng nhập</button>
        </form></body></html>
        """;

    public static string Setup(string err = "") => $"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Tạo admin · Shopee Hub</title>{Style}</head><body>
        <form class="card" method="post" action="/setup">
          <div class="logo">S</div>
          <h1>Tạo tài khoản admin</h1><p class="sub">Lần đầu chạy — đặt user/mật khẩu quản trị</p>
          {(string.IsNullOrEmpty(err) ? "" : $"<div class=\"err\">{System.Net.WebUtility.HtmlEncode(err)}</div>")}
          <label>Tên đăng nhập</label><input name="u" autofocus autocomplete="username">
          <label>Mật khẩu (≥ 6 ký tự)</label><input name="p" type="password" autocomplete="new-password">
          <button type="submit">Tạo & tiếp tục</button>
        </form></body></html>
        """;
}
