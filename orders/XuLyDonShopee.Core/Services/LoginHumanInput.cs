using Microsoft.Playwright;

namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Thao tác chuột/bàn phím <b>kiểu người</b> trên trang Playwright của luồng đăng nhập: di chuột theo đường cong
/// (<see cref="HumanMouse"/>), gõ từng ký tự có delay (<see cref="HumanTyping"/>), click có/không hit-test.
/// <para><b>Anti-bot:</b> mọi khoảng delay + biên độ jitter ở đây là giá trị đã hiệu chỉnh — sửa là đổi dấu vết
/// hành vi, TUYỆT ĐỐI không "gọn hóa" các con số.</para>
/// </summary>
internal static class LoginHumanInput
{
    /// <summary>
    /// Điền một ô kiểu người: di chuột cong tới ô + click, rồi gõ <b>từng ký tự</b> với delay ngẫu
    /// nhiên (<see cref="HumanTyping.NextCharDelayMs"/>). Trả về vị trí chuột mới (tâm ô).
    /// </summary>
    internal static async Task<(double X, double Y)> HumanFillAsync(
        IPage page, IElementHandle el, string text, double mx, double my, Random rng, CancellationToken ct)
    {
        (mx, my) = await HumanMoveAndClickAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);

        // Ô có thể ĐÃ CÓ SẴN text (trình duyệt autofill / thông tin đã lưu sau khi bấm Save) → gõ đè sẽ NỐI
        // vào text cũ. Xóa SẠCH ô trước khi gõ lại: ưu tiên FillAsync("") (clear chuẩn của Playwright); lỗi
        // thì clear bằng phím (đã click nên focus đang ở ô → Ctrl+A chọn hết text TRONG ô rồi Delete).
        try
        {
            await el.FillAsync("").ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await page.Keyboard.PressAsync("Control+A").ConfigureAwait(false);
                await Task.Delay(rng.Next(40, 100), ct).ConfigureAwait(false);
                await page.Keyboard.PressAsync("Delete").ConfigureAwait(false);
            }
            catch { /* bỏ qua — vẫn thử gõ ở dưới */ }
        }
        await Task.Delay(rng.Next(60, 160), ct).ConfigureAwait(false);

        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            // Gõ TỪNG ký tự (KHÔNG fill/dán) + delay kiểu người.
            await page.Keyboard.TypeAsync(ch.ToString()).ConfigureAwait(false);
            await Task.Delay(HumanTyping.NextCharDelayMs(rng), ct).ConfigureAwait(false);
        }

        return (mx, my);
    }

    /// <summary>
    /// Di chuột theo <b>đường cong</b> từ (<paramref name="mx"/>,<paramref name="my"/>) tới tâm phần tử
    /// (+jitter nhỏ), tự <c>Mouse.MoveAsync</c> <b>từng điểm</b> (KHÔNG dùng <c>steps</c> lớn để đi
    /// thẳng). <b>Chỉ đưa chuột tới đích — KHÔNG click.</b> Trả về (vị trí chuột cuối, có bounding box
    /// hay không): box null → kéo phần tử vào tầm nhìn, GIỮ nguyên vị trí chuột, <c>HasBox=false</c>.
    /// </summary>
    internal static async Task<(double X, double Y, bool HasBox)> HumanMoveToAsync(
        IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
    {
        // Handle có thể đã DETACHED (Vue vẽ lại form sau khi map/modal re-render) → BoundingBoxAsync ném.
        // Bọc try: lỗi handle → coi như không có box (HasBox=false), KHÔNG để exception rò lên catch ngoài.
        ElementHandleBoundingBoxResult? box;
        try { box = await el.BoundingBoxAsync().ConfigureAwait(false); }
        catch { box = null; }

        double tx, ty;
        bool hasBox;
        if (box is not null)
        {
            // Tâm ô + jitter nhỏ (không luôn nhấn đúng chính giữa).
            tx = box.X + box.Width / 2.0 + (rng.NextDouble() - 0.5) * Math.Min(box.Width * 0.3, 20);
            ty = box.Y + box.Height / 2.0 + (rng.NextDouble() - 0.5) * Math.Min(box.Height * 0.3, 8);
            hasBox = true;
        }
        else
        {
            // Không lấy được bounding box → kéo phần tử vào tầm nhìn, giữ nguyên vị trí chuột.
            try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
            tx = mx;
            ty = my;
            hasBox = false;
        }

        // Số điểm theo khoảng cách (đường dài → nhiều điểm), giới hạn [12, 60] cho mượt.
        var dist = Math.Sqrt((tx - mx) * (tx - mx) + (ty - my) * (ty - my));
        var steps = Math.Clamp((int)(dist / 8) + 10, 12, 60);

        foreach (var (px, py) in HumanMouse.GeneratePath(mx, my, tx, ty, steps, rng))
        {
            ct.ThrowIfCancellationRequested();
            // Đi TỪNG điểm (steps mặc định = 1) để đường thật sự cong theo path đã sinh.
            await page.Mouse.MoveAsync((float)px, (float)py).ConfigureAwait(false);
            await Task.Delay(rng.Next(5, 26), ct).ConfigureAwait(false); // 5–25ms giữa các điểm
        }

        return (tx, ty, hasBox);
    }

    /// <summary>
    /// Di chuột theo <b>đường cong</b> tới tâm phần tử rồi click kiểu người (down + trễ + up). Trả về
    /// vị trí chuột cuối (điểm đích). <b>Click MÙ theo tọa độ — KHÔNG hit-test</b>: CHỈ dùng cho luồng
    /// đăng nhập (<c>TryHumanLoginAsync</c> — form login đơn giản, không có submenu cụp/flyout
    /// đè). Mọi thao tác NGHIỆP VỤ (menu/modal) dùng <see cref="HumanMoveAndClickVerifiedAsync"/>.
    /// </summary>
    internal static async Task<(double X, double Y)> HumanMoveAndClickAsync(
        IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
    {
        (double tx, double ty, _) = await HumanMoveToAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);

        // Click kiểu người: nhấn giữ một khoảng ngắn rồi nhả.
        await page.Mouse.DownAsync().ConfigureAwait(false);
        await Task.Delay(rng.Next(40, 121), ct).ConfigureAwait(false);
        await page.Mouse.UpAsync().ConfigureAwait(false);

        return (tx, ty);
    }

    /// <summary>
    /// Primitive click <b>kiểu người CÓ HIT-TEST</b> cho thao tác nghiệp vụ: đưa chuột theo đường cong
    /// tới phần tử (<see cref="HumanMoveToAsync"/>), rồi TRƯỚC KHI nhả click <b>kiểm tra
    /// <c>document.elementFromPoint</c></b> tại điểm click có đúng là phần tử đích (hoặc con/tổ tiên
    /// của nó) — chống <b>click nhầm link khác</b> khi submenu bị cụp hoặc flyout/popover đè lên toạ độ.
    /// Poll hit-test tối đa ~2s với chuột ĐỨNG YÊN tại đích (giống người dừng nhìn rồi mới bấm; popover
    /// hover của item khác tự tắt khi chuột rời item đó). Chỉ <c>Down/trễ/Up</c> khi hit-test PASS. Trả
    /// về (vị trí chuột cuối, đã click hay chưa) — <c>Clicked=false</c> khi không có bounding box hoặc
    /// hit-test fail suốt ~2s (KHÔNG bao giờ click mù vào tọa độ).
    /// </summary>
    internal static async Task<(double X, double Y, bool Clicked)> HumanMoveAndClickVerifiedAsync(
        IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
    {
        (double tx, double ty, bool hasBox) =
            await HumanMoveToAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);

        // Không có bounding box → thử kéo vào tầm nhìn + move lại MỘT lần; vẫn không có box → KHÔNG click.
        if (!hasBox)
        {
            try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
            (tx, ty, hasBox) = await HumanMoveToAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);
            if (!hasBox)
            {
                return (mx, my, false);
            }
        }

        // Poll hit-test tối đa ~2s: chuột ĐỨNG YÊN tại đích, dừng ngẫu nhiên rồi kiểm — giống người dừng
        // nhìn rồi mới bấm (popover hover của item khác tự tắt vì chuột không còn trên item đó).
        var deadline = DateTime.UtcNow.AddMilliseconds(2000);
        do
        {
            ct.ThrowIfCancellationRequested();
            if (await LoginPageProbe.IsPointOnElementAsync(el, tx, ty).ConfigureAwait(false))
            {
                // Hit-test PASS → click kiểu người: nhấn giữ một khoảng ngắn rồi nhả.
                await page.Mouse.DownAsync().ConfigureAwait(false);
                await Task.Delay(rng.Next(40, 121), ct).ConfigureAwait(false);
                await page.Mouse.UpAsync().ConfigureAwait(false);
                return (tx, ty, true);
            }

            await Task.Delay(rng.Next(150, 301), ct).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        // Poll fail suốt ~2s → điểm click đang thuộc phần tử khác (bị che/cụp) → KHÔNG Down/Up.
        return (tx, ty, false);
    }

    /// <summary>
    /// Click <b>kiểu người CÓ HIT-TEST</b> nhưng chỉ khi phần tử đang hiển thị
    /// (<c>BoundingBoxAsync() != null</c>): scroll vào tầm nhìn trước, box vẫn null → KHÔNG click và trả
    /// <c>Clicked=false</c>. Có box → gọi <see cref="HumanMoveAndClickVerifiedAsync"/> (chỉ nhả chuột khi
    /// <c>elementFromPoint</c> tại điểm click đúng là phần tử đích — chống click nhầm link khác khi bị
    /// che/cụp); <c>Clicked</c> lấy từ kết quả verified (hit-test fail → false, KHÔNG click mù). Trả về
    /// vị trí chuột mới + đã click hay chưa.
    /// </summary>
    internal static async Task<(double X, double Y, bool Clicked)> TryHumanClickVisibleAsync(
        IPage page, IElementHandle el, double mx, double my, Random rng, CancellationToken ct)
    {
        try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }

        if (!await LoginPageProbe.HasBoundingBoxAsync(el).ConfigureAwait(false))
        {
            try { await el.ScrollIntoViewIfNeededAsync().ConfigureAwait(false); } catch { /* bỏ qua */ }
            if (!await LoginPageProbe.HasBoundingBoxAsync(el).ConfigureAwait(false))
            {
                return (mx, my, false);
            }
        }

        bool clicked;
        (mx, my, clicked) = await HumanMoveAndClickVerifiedAsync(page, el, mx, my, rng, ct).ConfigureAwait(false);
        return (mx, my, clicked);
    }
}
