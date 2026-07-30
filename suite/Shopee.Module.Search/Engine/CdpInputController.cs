namespace ShopeeStatApp.Services;

/// <summary>
/// Executes trusted browser input via the Chrome DevTools Protocol (CDP) Input domain,
/// driven by gesture-level requests the extension sends over the app WebSocket.
///
/// The extension stays in charge of orchestration (it resolves DOM coordinates with
/// getBoundingClientRect), but instead of dispatching synthetic JS events (isTrusted=false)
/// it sends a single message per gesture — {kind:"cdpInput", id, op, ...} — and awaits an
/// ack {kind:"cdpInputAck", id, ok}. Human-like motion (easing, jitter, per-step delays) is
/// interpolated here so each gesture is one round-trip, not one-per-micro-step.
/// </summary>
public sealed class CdpInputController : IAsyncDisposable
{
    private readonly WebSocketServer _ws;
    private readonly int _cdpPort;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Chuyển động/độ trễ kiểu người dùng chung với Check tài khoản (Core) — chỉ khác bộ hằng.
    private readonly CdpHumanInput _input = new(HumanInputProfile.Search);

    private CdpSession? _cdp;
    private bool _disposed;
    private bool _dprLogged;

    public event Action<string>? Log;

    public CdpInputController(WebSocketServer ws, int cdpPort)
    {
        _ws = ws;
        _cdpPort = cdpPort;
    }

    /// <summary>Attaches a CDP session to the Shopee search tab and starts listening for gestures.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await EnsureSessionAsync(ct);
        _ws.MessageReceived += OnMessage;
    }

    private async Task<bool> EnsureSessionAsync(CancellationToken ct = default)
    {
        if (_cdp is { IsOpen: true }) return true;
        try
        {
            if (_cdp is not null) await _cdp.DisposeAsync();
            _cdp = await CdpSession.ConnectToPageMatchingAsync(
                _cdpPort,
                url => url.Contains("shopee.vn", StringComparison.OrdinalIgnoreCase)
                       && !url.Contains("shopee.vn/api/", StringComparison.OrdinalIgnoreCase),
                ct);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke("CDP input connect failed: " + ex.Message);
            return false;
        }
    }

    // Read every needed field synchronously: the JsonDocument is disposed once this
    // handler returns, so we must not touch the JsonElement across the await below.
    private void OnMessage(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("kind", out var kindProp) || kindProp.GetString() != "cdpInput")
            return;

        var id = root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var n) ? n : 0;
        var op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";
        var x = GetDouble(root, "x");
        var y = GetDouble(root, "y");
        var deltaY = GetDouble(root, "deltaY");
        var deltaX = GetDouble(root, "deltaX");
        var text = root.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? "" : "";
        var key = root.TryGetProperty("key", out var kEl) ? kEl.GetString() ?? "" : "";
        var clearFirst = root.TryGetProperty("clearFirst", out var cEl) && cEl.ValueKind == JsonValueKind.True;
        var clickCount = root.TryGetProperty("clickCount", out var ccEl) && ccEl.TryGetInt32(out var cc) ? cc : 1;
        var dpr = root.TryGetProperty("dpr", out var dEl) && dEl.TryGetDouble(out var d) ? d : 1.0;

        // dpr != 1 do Windows scale (màn 4K) là bình thường — tọa độ CDP tính bằng CSS px
        // nên không bị lệch. Chỉ log một lần để tham khảo, không phải cảnh báo lỗi.
        if (Math.Abs(dpr - 1.0) > 0.01 && !_dprLogged)
        {
            _dprLogged = true;
            Log?.Invoke($"devicePixelRatio={dpr} (Windows scale/zoom). Tọa độ CDP dùng CSS px nên không ảnh hưởng; chỉ lệch nếu zoom trình duyệt khác 100%.");
        }

        _ = ExecuteAsync(id, op, x, y, deltaX, deltaY, text, key, clearFirst, clickCount);
    }

    private async Task ExecuteAsync(
        int id, string op, double x, double y, double deltaX, double deltaY,
        string text, string key, bool clearFirst, int clickCount)
    {
        var ok = false;
        string? error = null;

        await _gate.WaitAsync();
        try
        {
            if (!await EnsureSessionAsync())
            {
                error = "CDP session not available";
            }
            else
            {
                var cdp = CdpSender.For(_cdp!);
                switch (op)
                {
                    case "moveTo": await _input.MoveMouseToAsync(cdp, x, y); ok = true; break;
                    case "click":  await _input.ClickAsync(cdp, x, y, clickCount); ok = true; break;
                    case "wheel":  await _input.WheelAsync(cdp, x, y, deltaX, deltaY); ok = true; break;
                    case "type":   await _input.TypeTextAsync(cdp, text, clearFirst); ok = true; break;
                    case "pressKey": await _input.PressKeyAsync(cdp, string.IsNullOrEmpty(key) ? "Enter" : key); ok = true; break;
                    default: error = "unknown op: " + op; break;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log?.Invoke($"CDP gesture '{op}' failed: {ex.Message}");
            // Phiên CDP có thể đã hỏng (vd timeout do socket nửa-chết). Bỏ phiên để gesture KẾ
            // kết nối lại thay vì dùng lại phiên hỏng → tránh kẹt fallback synthetic cả từ khóa.
            try { if (_cdp is not null) { await _cdp.DisposeAsync(); _cdp = null; } } catch { }
        }
        finally
        {
            _gate.Release();
        }

        try { await _ws.SendAsync(new { kind = "cdpInputAck", id, ok, error }); }
        catch { }
    }

    // â”€â”€ helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static double GetDouble(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? d : 0;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _ws.MessageReceived -= OnMessage;
        if (_cdp is not null) await _cdp.DisposeAsync();
        _gate.Dispose();
    }
}
