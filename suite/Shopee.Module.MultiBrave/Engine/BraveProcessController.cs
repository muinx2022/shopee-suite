using System.Diagnostics;
using System.Net.WebSockets;
using Shopee.Core.Browser;
using Shopee.Core.Cdp;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// TIẾN TRÌNH Brave của một instance: phóng (gắn Job Object + đăng ký fleet), đóng êm rồi kill + reap,
/// và chờ CDP port nhả hẳn trước khi cho mở lại. Tách khỏi <see cref="BraveInstanceSession"/> — phiên chỉ
/// còn quyết định KHI NÀO dựng/hạ, còn CÁCH dựng/hạ nằm trọn ở đây.
/// </summary>
internal sealed class BraveProcessController(
    int cdpPort,
    Func<DirectoryInfo?> profileRoot,
    Func<Task<string>> browserWebSocketUrl,
    Action<string> log)
{
    private Process? _braveProcess;

    /// <summary>Đã phóng Brave lần nào chưa (tiến trình còn được theo dõi).</summary>
    public bool HasProcess => _braveProcess is not null;

    /// <summary>Tiến trình đang theo dõi đã thoát chưa (chưa phóng lần nào → false).</summary>
    public bool HasExited => _braveProcess is not null && _braveProcess.HasExited;

    /// <summary>Đưa cửa sổ Brave của instance này lên trước toàn bộ (gọi khi click dòng tiến trình).</summary>
    public void BringWindowToFront() => WindowFocus.BringProcessWindowToFront(_braveProcess);

    /// <summary>Bỏ theo dõi tiến trình đã chết (Brave tự tắt) để vòng sau khởi động lại từ đầu.</summary>
    public void DiscardExitedProcess()
    {
        try { _braveProcess!.Dispose(); } catch { }
        _braveProcess = null;
    }

    public void Launch(string exePath, string arguments)
    {
        Kill();
        // Đăng ký profile vào "fleet": trình dọn Brave mồ côi (BraveFleet) sẽ CHỪA cửa sổ đang sống này,
        // chỉ giết Brave thuộc app mà KHÔNG còn session nào nhận (sót sau treo/crash). Đăng ký TRƯỚC khi
        // phóng để con-trình Brave xuất hiện là đã được bảo vệ.
        var root = profileRoot();
        if (root is not null)
            BraveFleet.RegisterActiveProfile(root.FullName);
        // Phóng Brave GẮN vào Job Object KILL_ON_JOB_CLOSE của app → app chết kiểu gì (kể cả crash /
        // force-kill) thì OS tự dọn sạch Brave con, không còn tiến trình mồ côi ăn RAM.
        // startMinimized: TẮT theo yêu cầu user 2026-07-11 — mở BÌNH THƯỜNG; bản thu-nhỏ cũ kèm watchdog
        // BraveWindowMinimizer đè cửa sổ ~10s gây "nhấp nháy mở lên mở xuống" (Brave tự bung, watchdog lại đè).
        _braveProcess = BraveJobObject.Start(exePath, arguments, startMinimized: false);
        log($"Brave PID={_braveProcess?.Id}");
    }

    public void Kill(int maxWaitMs = 1500)
    {
        // Brave sắp chết → đóng kết nối CDP DÙNG CHUNG của port (WS cũ chết theo); lần dùng sau hub tự
        // nối lại tới Brave mới. Best-effort, không chặn.
        if (cdpPort > 0) PortCdpHub.For(cdpPort).ResetSoon();

        // Kịch bản kill + reap dùng chung (Core). Riêng bản này: thử đóng ÊM trước (giữ phiên/profile sạch)
        // rồi mới Kill, và CHỜ tiến trình thoát trước khi reaper quét.
        BraveTeardown.KillAndReap(ref _braveProcess, profileRoot()?.FullName, new BraveTeardownOptions
        {
            GracefulClose = () => TryCloseBraveGracefully(maxWaitMs),
            WaitForExitMs = maxWaitMs,
            Log = log,
        });
    }

    private void TryCloseBraveGracefully(int maxWaitMs)
    {
        if (_braveProcess is null || _braveProcess.HasExited)
            return;

        var waitMs = Math.Max(2500, maxWaitMs);
        try
        {
            _braveProcess.CloseMainWindow();
            if (_braveProcess.WaitForExit(waitMs))
                return;
        }
        catch
        {
            // fall through to CDP Browser.close
        }

        try
        {
            using var browser = new ClientWebSocket();
            browser.ConnectAsync(new Uri(browserWebSocketUrl().GetAwaiter().GetResult()), CancellationToken.None)
                .GetAwaiter().GetResult();
            CdpClient.SendAsync(browser, 501, "Browser.close", null).GetAwaiter().GetResult();
            _braveProcess.WaitForExit(waitMs);
        }
        catch
        {
            // fallback kill happens in caller
        }
    }

    /// <summary>
    /// Kill tiến trình Brave đang theo dõi, RỒI đảm bảo CDP port đã nhả hẳn trước khi cho launch lại.
    /// Nếu sau khi kill mà port vẫn còn (một Brave cũ — vd. instance lỗi proxy — vẫn giữ port/profile),
    /// gửi Browser.close qua CDP để đuổi nốt. Nếu bỏ qua bước này, brave.exe mới chỉ forward URL sang
    /// instance cũ rồi tự thoát → không có browser mới → runner treo ở "Đang chờ extension trên CDP".
    /// </summary>
    public async Task KillAndWaitPortFreeAsync(int maxWaitMs = 8000)
    {
        Kill();

        var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
        var evicted = false;
        while (DateTime.UtcNow < deadline)
        {
            if (!await IsCdpPortReachableAsync(cdpPort).ConfigureAwait(false))
                return; // port đã nhả — sạch, có thể launch lại

            // Còn một Brave nào đó giữ port → đuổi bằng Browser.close (kill theo PID không bắt được
            // vì brave.exe gốc có thể đã fork+exit, browser thật chạy ở PID khác).
            if (!evicted)
                log($"CDP port {cdpPort} vẫn còn Brave cũ giữ — đóng nốt trước khi mở lại…");
            evicted = true;
            try
            {
                using var browser = new ClientWebSocket();
                var wsUrl = await browserWebSocketUrl().ConfigureAwait(false);
                await browser.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
                await CdpClient.SendAsync(browser, 502, "Browser.close", null).ConfigureAwait(false);
            }
            catch { /* port đang đóng dở; vòng lặp sẽ kiểm tra lại */ }
            await Task.Delay(400).ConfigureAwait(false);
        }

        if (await IsCdpPortReachableAsync(cdpPort).ConfigureAwait(false))
            log($"Cảnh báo: CDP port {cdpPort} vẫn bận sau khi chờ — Brave mới có thể không khởi động sạch.");
    }

    private static async Task<bool> IsCdpPortReachableAsync(int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connectTask = tcp.ConnectAsync("127.0.0.1", port);
            return await Task.WhenAny(connectTask, Task.Delay(1200)).ConfigureAwait(false) == connectTask
                   && connectTask.IsCompletedSuccessfully;
        }
        catch
        {
            return false;
        }
    }
}
