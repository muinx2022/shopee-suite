using Shopee.Core.Coordination;

namespace Shopee.Hub;

/// <summary>
/// Điều phối chế độ Hub: bật/tắt mini-server (Kestrel+SQLite) + Cloudflare Tunnel cùng lúc. Singleton
/// để App (lúc khởi động) và Settings (nút bật/tắt) dùng chung một instance.
/// </summary>
public sealed class HubRuntime
{
    public static HubRuntime Shared { get; } = new();
    private HubRuntime() { }

    private readonly HubServer _server = new();
    private readonly CloudflaredRunner _tunnel = new();

    public bool Running { get; private set; }
    public string PublicUrl { get; private set; } = "";

    /// <summary>Dòng log tiến trình (server/tunnel) để UI hiển thị nếu cần.</summary>
    public event Action<string>? Log;

    /// <summary>Phát khi <see cref="Running"/> đổi (bật/tắt) — để UI cập nhật nút/trạng thái.</summary>
    public event Action? StateChanged;

    public async Task StartAsync(HubServerConfig cfg)
    {
        if (Running) return;
        await _server.StartAsync(cfg);
        Emit($"Hub API chạy local: http://127.0.0.1:{cfg.Port}");

        if (!string.IsNullOrWhiteSpace(cfg.TunnelToken))
        {
            await _tunnel.EnsureInstalledAsync(Emit);
            _tunnel.Start(cfg.TunnelToken, Emit);
            PublicUrl = cfg.PublicUrl;
            Emit(string.IsNullOrWhiteSpace(PublicUrl)
                ? "Tunnel đang chạy (đặt domain để hiện URL công khai)."
                : $"Tunnel đang chạy → {PublicUrl}");
        }
        else
        {
            Emit("ℹ Không chạy cloudflared từ app. Nếu bạn đã cài cloudflared như Windows service riêng " +
                 "(cloudflared service install <token>) thì tunnel vẫn ra Internet; nếu chưa, API chỉ truy cập trong máy này.");
        }

        Running = true;
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        _tunnel.Stop();
        await _server.StopAsync();
        Running = false;
        PublicUrl = "";
        StateChanged?.Invoke();
        Emit("Đã dừng Hub.");
    }

    /// <summary>Dừng đồng bộ-chặn (gọi lúc app thoát để không bỏ sót cloudflared mồ côi).</summary>
    public void StopBlocking()
    {
        try { _tunnel.Stop(); } catch { }
        try { _server.StopAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
        Running = false;
        StateChanged?.Invoke();
    }

    private void Emit(string m) { try { Log?.Invoke(m); } catch { } }
}
