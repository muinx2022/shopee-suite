using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Shopee.Hub;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Auth;

/// <summary>
/// Scheme xác thực client (bản WPF/Avalonia): header <c>X-Api-Token</c> khớp token dùng chung của fleet
/// (lưu ở bảng settings, seed từ hub-server.json cũ hoặc env HUB_API_TOKEN). GIỮ NGUYÊN giao thức để client
/// cũ chạy tiếp không sửa gì. So sánh FixedTimeEquals. Token rỗng → từ chối HẾT (an toàn: chưa cấu hình).
/// </summary>
public sealed class ApiTokenOptions : AuthenticationSchemeOptions { }

public sealed class ApiTokenHandler : AuthenticationHandler<ApiTokenOptions>
{
    public const string SchemeName = "ApiToken";
    private readonly HubDatabase _db;

    public ApiTokenHandler(IOptionsMonitor<ApiTokenOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, HubDatabase db) : base(options, logger, encoder)
    {
        _db = db;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = _db.GetSetting(SettingKeys.ApiToken) ?? "";
        var provided = Request.Headers["X-Api-Token"].ToString();
        if (string.IsNullOrEmpty(configured)
            || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(configured)))
        {
            // Log danh tính máy dùng token SAI (máy ma đang spam 401) để admin biết máy nào cần sửa token/tắt.
            var badMid = Request.Headers["X-Machine-Id"].ToString();
            var badIp = Request.Headers["CF-Connecting-IP"].ToString();
            Logger.LogWarning("BAD-TOKEN từ machine={Mid} ip={Ip} path={Path} tokenTail={Tail}",
                string.IsNullOrEmpty(badMid) ? "(none)" : badMid,
                string.IsNullOrEmpty(badIp) ? "(none)" : badIp,
                Request.Path.Value,
                provided.Length >= 6 ? provided[^6..] : "(short)");
            return Task.FromResult(AuthenticateResult.Fail("bad-token"));
        }

        var machineId = Request.Headers["X-Machine-Id"].ToString();
        // Máy bị CHẶN (revoke) → 401 dù token đúng (admin đã "xoá client" trên web).
        if (_db.IsMachineRevoked(machineId))
            return Task.FromResult(AuthenticateResult.Fail("revoked-machine"));
        var claims = new[] { new Claim(ClaimTypes.Name, "client"), new Claim("machine_id", machineId) };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>Trả 401 body "unauthorized" GIỐNG HỆT hub nhúng cũ (byte-compat cho client phát hiện token sai).</summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsync("unauthorized");
    }
}
