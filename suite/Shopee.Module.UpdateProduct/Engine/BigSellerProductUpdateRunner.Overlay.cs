using Microsoft.Playwright;

namespace UpdateProduct;

/// <summary>Phần BigSellerProductUpdateRunner: overlay tiến độ vẽ trên mọi tab Brave + nhóm helper
/// đóng modal / duyệt phần tử đang hiển thị — tách khỏi file chính, đợt D pure move.</summary>
internal sealed partial class BigSellerProductUpdateRunner
{
    // ── overlay tiến độ ngay trên trang Brave để THEO DÕI log (best-effort: lỗi overlay KHÔNG bao giờ
    // chặn luồng cập nhật). Phát lên MỌI tab đang mở trong context → nhìn tab nào cũng thấy. ──
    private const string OverlayJs = @"(line) => {
  let box = document.getElementById('__ssyncOverlay');
  if (!box) {
    box = document.createElement('div');
    box.id = '__ssyncOverlay';
    box.style.cssText = 'position:fixed;z-index:2147483647;right:10px;bottom:10px;width:430px;max-height:260px;overflow:hidden;background:rgba(17,17,17,.86);color:#7CFC7C;font:12px/1.5 Consolas,Menlo,monospace;padding:8px 10px;border-radius:10px;box-shadow:0 4px 16px rgba(0,0,0,.55);pointer-events:none;white-space:pre-wrap;word-break:break-word';
    const t = document.createElement('div');
    t.textContent = '● ShopeeSuite — tiến độ Update';
    t.style.cssText = 'color:#67d3ff;font-weight:700;margin-bottom:5px';
    box.appendChild(t);
    const b = document.createElement('div'); b.id = '__ssyncOverlayBody'; box.appendChild(b);
    (document.body || document.documentElement).appendChild(box);
  }
  const body = document.getElementById('__ssyncOverlayBody');
  const r = document.createElement('div');
  const n = new Date();
  const p = x => String(x).padStart(2, '0');
  r.textContent = '[' + p(n.getHours()) + ':' + p(n.getMinutes()) + ':' + p(n.getSeconds()) + '] ' + line;
  body.appendChild(r);
  while (body.childNodes.length > 12) body.removeChild(body.firstChild);
}";

    /// <summary>Đẩy 1 dòng lên overlay của MỌI trang đang mở (listing + edit). Lỗi → bỏ qua.</summary>
    private async Task OverlayAsync(string line)
    {
        var ctx = _context;
        if (ctx is null) return;
        foreach (var pg in ctx.Pages)
        {
            if (pg.IsClosed) continue;
            try { await pg.EvaluateAsync(OverlayJs, line); } catch { /* overlay best-effort */ }
        }
    }

    /// <summary>Báo bắt đầu một bước: ghi log app ("▶ …") + hiện trên overlay Brave.</summary>
    private async Task StepAsync(string text)
    {
        _log("  ▶ " + text);
        await OverlayAsync("▶ " + text);
    }

    private async Task DismissBlockingModalAsync(IPage page)
    {
        try
        {
            // Popup hướng dẫn đổi ngôn ngữ (KHÔNG phải ant-modal) cũng chặn click Edit → thử chọn Tiếng Việt/đóng trước.
            if (await BigSellerCrawlHelper.DismissLanguageGuideAsync(page, _log, CancellationToken.None)) return;
            if (await page.Locator(BlockingModalVisible).CountAsync() > 0)
            {
                foreach (var sel in DismissBtns)
                {
                    var loc = page.Locator(sel).First;
                    if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync()) { await loc.ClickAsync(); return; }
                }
            }
            await page.Keyboard.PressAsync("Escape");
        }
        catch { }
    }

    private async Task CloseVisibleAntModalAsync(IPage page, int timeoutMs)
    {
        var deadline = timeoutMs;
        while (deadline > 0)
        {
            if (await page.Locator(CloseModalAny).CountAsync() == 0) return;
            var clicked = false;
            foreach (var sel in CloseModalSels)
            {
                var loc = page.Locator(sel);
                var n = await loc.CountAsync();
                for (var i = n - 1; i >= 0; i--)
                {
                    var el = loc.Nth(i);
                    if (!await el.IsVisibleAsync()) continue;
                    try { await el.ClickAsync(); clicked = true; } catch { }
                    break;
                }
                if (clicked) break;
            }
            if (!clicked) { try { await page.Keyboard.PressAsync("Escape"); } catch { } }
            await page.WaitForTimeoutAsync(300);
            deadline -= 300;
        }
    }

    private static async Task ClosePageAcceptingDialogAsync(IPage page)
    {
        try
        {
            page.Dialog += async (_, d) => { try { await d.AcceptAsync(); } catch { } };
            await page.CloseAsync(new() { RunBeforeUnload = true });
        }
        catch { try { await page.CloseAsync(); } catch { } }
    }

    private async Task ForEachVisibleAsync(ILocator locator, Func<ILocator, Task> action)
    {
        var n = await locator.CountAsync();
        for (var i = 0; i < n; i++)
        {
            var el = locator.Nth(i);
            try { if (await el.IsVisibleAsync()) await action(el); } catch { }
        }
    }

    private static async Task<ILocator?> FirstVisibleAsync(params ILocator[] locators)
    {
        foreach (var loc in locators)
        {
            try
            {
                var n = await loc.CountAsync();
                for (var i = 0; i < n; i++)
                {
                    var el = loc.Nth(i);
                    if (await el.IsVisibleAsync()) return el;
                }
            }
            catch { }
        }
        return null;
    }
}
