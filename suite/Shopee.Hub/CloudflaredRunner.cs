using System.Diagnostics;
using System.Net.Http;
using Shopee.Core.Infrastructure;

namespace Shopee.Hub;

/// <summary>
/// Chạy Cloudflare Tunnel (cloudflared) để lộ Hub local ra `https://api.&lt;domain&gt;` mà không mở port.
/// Dùng "tunnel token" (người dùng tạo tunnel 1 lần trên dashboard CF rồi dán token). Nếu chưa có
/// cloudflared.exe thì TỰ TẢI bản chính chủ về (vào hub-data) ở lần đầu.
/// </summary>
public sealed class CloudflaredRunner
{
    // Để GHIM phiên bản (chống bản "latest" đổi đột ngột/độc): đổi "latest/download" → "download/<phiên-bản>"
    // và điền ExpectedSha256 bằng hash đã tự xác minh của đúng file đó.
    private const string DownloadUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    /// <summary>SHA-256 (hex) đã xác minh của file tải về. null = CHƯA ghim → chỉ log hash để bạn điền; đã ghim mà
    /// KHÔNG khớp → từ chối cài (chống chuỗi cung ứng).</summary>
    private static readonly string? ExpectedSha256 = null;

    private Process? _proc;

    public bool Running => _proc is { HasExited: false };

    /// <summary>Vị trí cloudflared.exe của app (trong hub-data).</summary>
    public static string ExePath => Path.Combine(SuitePaths.ModuleDir("hub-data"), "cloudflared.exe");

    /// <summary>Bảo đảm có cloudflared.exe: ưu tiên file sẵn → PATH → tải về.</summary>
    public async Task EnsureInstalledAsync(Action<string>? log, CancellationToken ct = default)
    {
        if (File.Exists(ExePath)) return;

        var inPath = ResolveFromPath();
        if (inPath is not null) { try { File.Copy(inPath, ExePath, overwrite: true); return; } catch { } }

        log?.Invoke("Đang tải cloudflared (~50MB) lần đầu…");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var tmp = ExePath + ".tmp";
        await using (var fs = File.Create(tmp))
            await resp.Content.CopyToAsync(fs, ct);

        // Chống chuỗi cung ứng: nếu đã GHIM hash mà bản tải không khớp → xoá + từ chối cài (không chạy file lạ).
        var sha = await Sha256FileAsync(tmp, ct);
        if (ExpectedSha256 is not null && !string.Equals(sha, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException($"cloudflared SHA-256 không khớp (mong đợi {ExpectedSha256}, nhận {sha}) — huỷ cài để an toàn.");
        }
        File.Move(tmp, ExePath, overwrite: true);
        log?.Invoke(ExpectedSha256 is null
            ? $"Đã tải xong cloudflared. SHA-256={sha} (ghim giá trị này vào ExpectedSha256 để chống giả mạo)."
            : "Đã tải & xác minh cloudflared.");
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(fs, ct));
    }

    /// <summary>Chạy `cloudflared tunnel run --token &lt;token&gt;` ở tiến trình nền.</summary>
    public void Start(string tunnelToken, Action<string>? log)
    {
        Stop();
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("tunnel");
        psi.ArgumentList.Add("--no-autoupdate");
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--token");
        psi.ArgumentList.Add(tunnelToken);

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log?.Invoke("[cf] " + e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log?.Invoke("[cf] " + e.Data); };
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    public void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }

    private static string? ResolveFromPath()
    {
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var cand = Path.Combine(p.Trim(), "cloudflared.exe");
                if (File.Exists(cand)) return cand;
            }
            catch { }
        }
        return null;
    }
}
