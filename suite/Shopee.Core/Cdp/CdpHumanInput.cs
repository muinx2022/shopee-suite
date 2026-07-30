namespace Shopee.Core.Cdp;

/// <summary>
/// Hai kênh gửi CDP mà human-input cần: <see cref="Send"/> chờ phản hồi, <see cref="SendNoReply"/>
/// bắn-rồi-quên. Truyền dưới dạng delegate để lớp nhập liệu KHÔNG dính vào transport của module
/// (Search/Check tài khoản đi qua <see cref="CdpSession"/>, Scrape đi qua WebSocket riêng).
/// </summary>
public readonly record struct CdpSender(
    Func<string, object?, CancellationToken, Task> Send,
    Func<string, object?, CancellationToken, Task> SendNoReply)
{
    public static CdpSender For(CdpSession cdp) => new(
        (method, @params, ct) => cdp.SendAsync(method, @params, ct),
        (method, @params, ct) => cdp.SendNoReplyAsync(method, @params, ct));
}

/// <summary>
/// Bộ hằng "chất người" RIÊNG của từng luồng. Mọi khoảng thời gian là bao gồm hai đầu
/// (min..max). Các hằng dùng chung (số bước di chuột 10-19, easing, biên độ rung, độ trễ
/// insertText 120-260ms, độ trễ phím đặc biệt 40-110ms) nằm thẳng trong
/// <see cref="CdpHumanInput"/> vì cả hai luồng vốn giống hệt nhau.
/// </summary>
/// <param name="MouseEventsCarryWheelFields">
/// Luồng Search luôn gửi kèm <c>clickCount</c>/<c>deltaX</c>/<c>deltaY</c>=0 trong mọi sự kiện
/// chuột; luồng Check tài khoản thì không. Giữ nguyên hình dạng payload của từng luồng.
/// </param>
public sealed record HumanInputProfile(
    int InitMouseXBase, int InitMouseXSpread,
    int InitMouseYBase, int InitMouseYSpread,
    int MoveStepMinMs, int MoveStepMaxMs,
    int ClickBeforePressMinMs, int ClickBeforePressMaxMs,
    int ClickHoldMinMs, int ClickHoldMaxMs,
    int AsciiKeyMinMs, int AsciiKeyMaxMs,
    bool MouseEventsCarryWheelFields)
{
    /// <summary>Search — cử chỉ do extension điều phối, gửi qua WebSocket của app.</summary>
    public static readonly HumanInputProfile Search = new(
        InitMouseXBase: 200, InitMouseXSpread: 400,
        InitMouseYBase: 150, InitMouseYSpread: 300,
        MoveStepMinMs: 8, MoveStepMaxMs: 24,
        ClickBeforePressMinMs: 180, ClickBeforePressMaxMs: 520,
        ClickHoldMinMs: 55, ClickHoldMaxMs: 150,
        AsciiKeyMinMs: 45, AsciiKeyMaxMs: 120,
        MouseEventsCarryWheelFields: true);

    /// <summary>Check tài khoản — điền form đăng nhập Shopee.</summary>
    public static readonly HumanInputProfile CheckAccount = new(
        InitMouseXBase: 220, InitMouseXSpread: 380,
        InitMouseYBase: 160, InitMouseYSpread: 260,
        MoveStepMinMs: 8, MoveStepMaxMs: 23,
        ClickBeforePressMinMs: 160, ClickBeforePressMaxMs: 480,
        ClickHoldMinMs: 50, ClickHoldMaxMs: 140,
        AsciiKeyMinMs: 55, AsciiKeyMaxMs: 149,
        MouseEventsCarryWheelFields: false);
}

/// <summary>
/// Nhập liệu "tin cậy" (isTrusted=true) qua domain Input của CDP, mô phỏng người thật: chuột di
/// theo đường cong có quán tính + rung nhẹ, gõ từng ký tự với độ trễ ngẫu nhiên. Giữ vị trí con
/// trỏ giữa các cử chỉ nên đường đi liên tục như tay người.
///
/// Truyền <paramref name="rng"/> nếu caller còn dùng chung một <see cref="Random"/> cho các độ
/// trễ khác — dùng chung giữ nguyên chuỗi ngẫu nhiên như trước khi tách lớp này ra.
/// </summary>
public sealed class CdpHumanInput
{
    private readonly HumanInputProfile _profile;
    private readonly Random _rng;
    private double _mouseX;
    private double _mouseY;

    public CdpHumanInput(HumanInputProfile profile, Random? rng = null)
    {
        _profile = profile;
        _rng = rng ?? new Random();
        _mouseX = profile.InitMouseXBase + _rng.Next(0, profile.InitMouseXSpread);
        _mouseY = profile.InitMouseYBase + _rng.Next(0, profile.InitMouseYSpread);
    }

    public async Task MoveMouseToAsync(CdpSender cdp, double tx, double ty, CancellationToken ct = default)
    {
        var sx = _mouseX;
        var sy = _mouseY;
        // Các bước trung gian bắn-rồi-quên: chờ phản hồi CDP từng bước trong lúc renderer bận sẽ
        // đẩy cả cử chỉ vượt quá thời gian chờ ack của extension.
        var steps = _rng.Next(10, 20);
        for (var i = 1; i < steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = (double)i / steps;
            var ease = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            var x = sx + (tx - sx) * ease + Math.Sin(t * Math.PI * 3) * Rand(-4, 4);
            var y = sy + (ty - sy) * ease + Math.Cos(t * Math.PI * 2) * Rand(-3, 3);
            await cdp.SendNoReply("Input.dispatchMouseEvent", MoveStepArgs(x, y), ct);
            await DelayAsync(_profile.MoveStepMinMs, _profile.MoveStepMaxMs, ct);
        }
        // Bước cuối CHỜ phản hồi để chắc chắn con trỏ đã tới đích trước khi bấm.
        await cdp.Send("Input.dispatchMouseEvent", MouseArgs("mouseMoved", tx, ty, "none", 0, 0), ct);
        _mouseX = tx;
        _mouseY = ty;
    }

    public async Task ClickAsync(
        CdpSender cdp, double tx, double ty, int clickCount = 1, CancellationToken ct = default)
    {
        await MoveMouseToAsync(cdp, tx, ty, ct);
        await DelayAsync(_profile.ClickBeforePressMinMs, _profile.ClickBeforePressMaxMs, ct);
        await cdp.Send("Input.dispatchMouseEvent", MouseArgs("mousePressed", tx, ty, "left", 1, clickCount), ct);
        await DelayAsync(_profile.ClickHoldMinMs, _profile.ClickHoldMaxMs, ct);
        await cdp.Send("Input.dispatchMouseEvent", MouseArgs("mouseReleased", tx, ty, "left", 0, clickCount), ct);
    }

    /// <summary>
    /// Cuộn = bắn-rồi-quên: KHÔNG chờ phản hồi CDP. Trang nặng làm phản hồi tới chậm quá timeout
    /// → "A task was canceled" → tụt về synthetic dù trang vẫn cuộn. Extension tự đọc lại scrollY
    /// sau mỗi lần cuộn nên không cần giá trị ack từ CDP.
    /// </summary>
    public Task WheelAsync(
        CdpSender cdp, double x, double y, double deltaX, double deltaY, CancellationToken ct = default) =>
        cdp.SendNoReply("Input.dispatchMouseEvent", new
        {
            type = "mouseWheel", x, y, button = "none", buttons = 0, clickCount = 0, deltaX, deltaY,
        }, ct);

    /// <summary>
    /// ASCII in được → gõ từng ký tự bằng sự kiện phím (giống người nhất). Có dấu/unicode →
    /// insertText cả chuỗi (tin cậy và chắc ăn hơn).
    /// </summary>
    public async Task TypeTextAsync(
        CdpSender cdp, string text, bool clearFirst = false, CancellationToken ct = default)
    {
        if (clearFirst) await SelectAllAndDeleteAsync(cdp, ct);
        if (string.IsNullOrEmpty(text)) return;

        var isAscii = text.All(ch => ch >= 0x20 && ch <= 0x7E);
        if (!isAscii)
        {
            await DelayAsync(120, 260, ct);
            await cdp.Send("Input.insertText", new { text }, ct);
            return;
        }

        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            var (code, vk) = KeyInfo(ch);
            var s = ch.ToString();
            await cdp.Send("Input.dispatchKeyEvent",
                new { type = "keyDown", text = s, key = s, code, windowsVirtualKeyCode = vk }, ct);
            await cdp.Send("Input.dispatchKeyEvent",
                new { type = "keyUp", key = s, code, windowsVirtualKeyCode = vk }, ct);
            await DelayAsync(_profile.AsciiKeyMinMs, _profile.AsciiKeyMaxMs, ct);
        }
    }

    public async Task SelectAllAndDeleteAsync(CdpSender cdp, CancellationToken ct = default)
    {
        // Ctrl+A rồi Delete (bitmask modifiers: 2 = Ctrl).
        await cdp.Send("Input.dispatchKeyEvent",
            new { type = "keyDown", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17, modifiers = 2 }, ct);
        await cdp.Send("Input.dispatchKeyEvent",
            new { type = "keyDown", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 2 }, ct);
        await cdp.Send("Input.dispatchKeyEvent",
            new { type = "keyUp", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 2 }, ct);
        await cdp.Send("Input.dispatchKeyEvent",
            new { type = "keyUp", key = "Control", code = "ControlLeft", windowsVirtualKeyCode = 17 }, ct);
        await DelayAsync(40, 110, ct);
        await PressKeyAsync(cdp, "Delete", ct);
    }

    public async Task PressKeyAsync(CdpSender cdp, string key, CancellationToken ct = default)
    {
        var (code, vk) = SpecialKeyInfo(key);
        // Enter phải là "keyDown" kèm text "\r" để renderer sinh đủ keydown+keypress —
        // form Shopee không submit với rawKeyDown (thiếu keypress).
        if (key == "Enter")
            await cdp.Send("Input.dispatchKeyEvent",
                new { type = "keyDown", key, code, windowsVirtualKeyCode = vk, text = "\r" }, ct);
        else
            await cdp.Send("Input.dispatchKeyEvent",
                new { type = "rawKeyDown", key, code, windowsVirtualKeyCode = vk }, ct);
        await DelayAsync(40, 110, ct);
        await cdp.Send("Input.dispatchKeyEvent",
            new { type = "keyUp", key, code, windowsVirtualKeyCode = vk }, ct);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private object MouseArgs(string type, double x, double y, string button, int buttons, int clickCount) =>
        _profile.MouseEventsCarryWheelFields
            ? new { type, x, y, button, buttons, clickCount, deltaX = 0d, deltaY = 0d }
            : (object)new { type, x, y, button, buttons, clickCount };

    private object MoveStepArgs(double x, double y) =>
        _profile.MouseEventsCarryWheelFields
            ? new { type = "mouseMoved", x, y, button = "none", buttons = 0, clickCount = 0, deltaX = 0d, deltaY = 0d }
            : (object)new { type = "mouseMoved", x, y, button = "none", buttons = 0 };

    private double Rand(double min, double max) => min + _rng.NextDouble() * (max - min);

    private Task DelayAsync(int minMs, int maxMs, CancellationToken ct) =>
        Task.Delay(_rng.Next(minMs, maxMs + 1), ct);

    private static (string code, int vk) KeyInfo(char ch)
    {
        if (ch >= '0' && ch <= '9') return ("Digit" + ch, ch);
        if (ch >= 'a' && ch <= 'z') return ("Key" + char.ToUpperInvariant(ch), char.ToUpperInvariant(ch));
        if (ch >= 'A' && ch <= 'Z') return ("Key" + ch, ch);
        if (ch == ' ') return ("Space", 32);
        return ("", 0);
    }

    private static (string code, int vk) SpecialKeyInfo(string key) => key switch
    {
        "Enter" => ("Enter", 13),
        "Delete" => ("Delete", 46),
        "Backspace" => ("Backspace", 8),
        "Escape" => ("Escape", 27),
        "Tab" => ("Tab", 9),
        _ => ("", 0),
    };
}
