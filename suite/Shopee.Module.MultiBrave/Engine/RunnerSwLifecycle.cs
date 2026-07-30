using System.Net.WebSockets;
using System.Text.Json;
using Shopee.Core.Cdp;
using Shopee.Core.Infrastructure;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// VÒNG ĐỜI service worker của extension runner: chờ SW sẵn sàng đầu phiên (đánh thức qua popup, reload khi
/// hook chưa nạp, leo thang mở lại profile — nhưng NHƯỜNG khi trang đang ở captcha), phân giải ID extension
/// "đã xác thực" dùng chung cho mọi lệnh sau đó, và giữ SW sống suốt vòng chạy bằng flat session.
/// Tra cứu target ở <see cref="RunnerExtensionTargets"/>, dọn tab ở <see cref="RunnerExtensionTabs"/>,
/// gửi lệnh ở <see cref="RunnerExtensionRpc"/>.
/// </summary>
internal static class RunnerSwLifecycle
{
    /// <summary>
    /// ID extension đã xác thực (có launcher hook) theo từng CDP port.
    /// Mọi thao tác (setDisplayState, executeScrapeStep, …) dùng CÙNG một ID
    /// để tránh ghi state vào extension khác với popup đang hiển thị.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _resolvedExtensionByPort = new();

    /// <summary>
    /// Số lần mở lại popup tươi liên tiếp mà SW vẫn không phản hồi trước khi leo thang sang
    /// relaunch profile. Khi chạy NHIỀU profile song song (vd 16 lane), CPU nghẽn nên SW
    /// cold-start cần lâu hơn để thức — nếu vội relaunch (đóng/mở cả Brave) sau 2 lần thì rất
    /// hao và gây "nhảy trình duyệt" liên tục. Để 6 (~30s đánh thức nhẹ qua popup) cho SW kịp
    /// lên; nếu SW câm thật thì vẫn relaunch (fallback, MaxExtensionRelaunchRetries) trong deadline 90s.
    /// </summary>
    private const int MaxPopupReopenBeforeRelaunch = 6;

    /// <summary>Xóa ID cache khi Brave khởi động lại / dừng instance.</summary>
    public static void ClearResolvedExtension(int cdpPort) =>
        _resolvedExtensionByPort.TryRemove(cdpPort, out _);

    public static async Task<string> EnsureRunnerExtensionReadyAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        Action<string>? log,
        CancellationToken cancellationToken = default,
        int timeoutSeconds = 90,
        Action<bool>? onCaptchaState = null)   // true = đang ở /verify chờ giải tay; false = đã qua captcha
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        // Khi trang đang ở captcha/verify mà SW câm: KHÔNG mở lại profile (reload sẽ mất captcha người
        // dùng đang giải). Chờ giải tay tối đa 3' rồi mới leo thang. Mốc hết hạn chờ-captcha (đặt lần đầu
        // phát hiện captcha).
        DateTime? captchaWaitUntil = null;
        var captchaWaitLogged = false;
        var lastLog = DateTime.MinValue;
        var lastWake = DateTime.MinValue;
        var lastPopupReopen = DateTime.MinValue;
        // Đếm số lần probe timeout/không-phản-hồi liên tiếp để leo thang sang reload SW.
        // Mở lại popup KHÔNG cứu được SW kẹt cold-start — chỉ chrome.runtime.reload() mới dựng lại được.
        var noResponseStreak = 0;
        var expectedId = RunnerExtensionPaths.TryGetLoadedExtensionId()
            ?? RunnerExtensionTargets.TryGetRunnerExtensionIdFromProfile(profileRoot)
            ?? throw new InvalidOperationException(
                "Không tìm thấy thư mục extension Shopee Data Runner — build lại launcher.");

        // Theo dõi CDP unreachable để chờ browser hồi lại sau khi bị đóng/restart.
        // Khi Brave tắt, đừng coi ngay là lỗi chết của extension; cho phép phiên được tái nối nếu
        // browser quay lại trong cùng lượt chạy.
        var cdpUnreachableSince = (DateTime?)null;
        const int CdpUnreachableTimeoutSeconds = 120;

        // Phiên mới → xóa cache cũ để re-resolve đúng extension đang chạy
        ClearResolvedExtension(cdpPort);

        await RunnerExtensionTabs.CloseRunnerExtensionPopupTabsAsync(cdpPort, profileRoot, cancellationToken);

        log?.Invoke($"Đánh thức extension {expectedId[..8]}… (popup mới)");
        await TryWakeServiceWorkerAsync(cdpPort, expectedId, cancellationToken, forceNewPopup: true);
        await Task.Delay(1500, cancellationToken);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Kiểm tra nhanh xem Brave có đang lắng nghe trên CDP không
            var cdpReachable = await RunnerExtensionTargets.IsCdpPortReachableAsync(cdpPort, cancellationToken);
            if (!cdpReachable)
            {
                cdpUnreachableSince ??= DateTime.UtcNow;
                ClearResolvedExtension(cdpPort);

                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 5)
                {
                    var waited = (DateTime.UtcNow - cdpUnreachableSince.Value).TotalSeconds;
                    log?.Invoke($"CDP port {cdpPort} đang mất kết nối ({waited:0}s) — chờ Brave quay lại…");
                    lastLog = DateTime.UtcNow;
                }

                if ((DateTime.UtcNow - cdpUnreachableSince.Value).TotalSeconds >= CdpUnreachableTimeoutSeconds)
                {
                    throw new InvalidOperationException(
                        $"Brave không lắng nghe trên CDP port {cdpPort} quá {CdpUnreachableTimeoutSeconds}s. " +
                        "Nếu đã đóng Brave thì mở lại cùng profile để nối tiếp phiên.");
                }

                // Gia hạn deadline vòng ngoài (giống nhánh chờ-captcha bên dưới) tới đủ ngưỡng CDP-unreachable:
                // deadline mặc định 90s NGẮN HƠN 120s nên trước đây hết 90s là ném lỗi CHUNG ("Đóng profile →
                // Mở profile lại") — thông điệp riêng "mở lại cùng profile để nối tiếp phiên" ở trên KHÔNG bao
                // giờ chạy được. Chỉ nới, không rút ngắn deadline đang dài hơn.
                var cdpDeadline = cdpUnreachableSince.Value.AddSeconds(CdpUnreachableTimeoutSeconds);
                if (deadline < cdpDeadline)
                    deadline = cdpDeadline;

                await Task.Delay(1000, cancellationToken);
                continue;
            }

            cdpUnreachableSince = null;

            var ids = await RunnerExtensionTargets.DiscoverRunnerExtensionIdsAsync(cdpPort, profileRoot, cancellationToken);

            if ((DateTime.UtcNow - lastLog).TotalSeconds >= 5)
            {
                var idList = ids.Count > 0 ? string.Join(", ", ids) : "(không tìm thấy)";
                log?.Invoke($"Đang chờ extension Shopee Data Runner trên CDP… [IDs: {idList}]");
                lastLog = DateTime.UtcNow;
            }

            foreach (var id in ids)
            {
                if (DateTime.UtcNow >= deadline)
                    break;

                var (probeOk, probeMsg) = await ProbeExtensionWithReasonAsync(
                    cdpPort, id, cancellationToken, deadline);
                if (probeOk)
                {
                    onCaptchaState?.Invoke(false);   // SW phản hồi lại = đã rời /verify (captcha giải xong) → cột Trạng thái về cũ
                    // Ghi nhớ ID đã xác thực — mọi thao tác sau dùng CÙNG ID này
                    _resolvedExtensionByPort[cdpPort] = id;
                    await RunnerExtensionTabs.CloseRunnerExtensionPopupTabsAsync(cdpPort, profileRoot, cancellationToken);
                    return id;
                }
                log?.Invoke($"  Probe {id[..8]}…: {probeMsg}");
                if (probeMsg.Contains("hasScrapeStep=false", StringComparison.OrdinalIgnoreCase) &&
                    DateTime.UtcNow - lastWake >= TimeSpan.FromSeconds(4))
                {
                    noResponseStreak = 0;
                    log?.Invoke("  → Reload extension vì service worker chưa nạp runner hook…");
                    await TryReloadExtensionAsync(cdpPort, id, cancellationToken);
                    lastWake = DateTime.UtcNow;
                    await Task.Delay(1800, cancellationToken);
                }
                else if ((RunnerExtensionRpc.IsPopupBridgeError(probeMsg) ||
                          probeMsg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                          probeMsg.Contains("quá thời gian", StringComparison.OrdinalIgnoreCase) ||
                          probeMsg.Contains("không phản hồi", StringComparison.OrdinalIgnoreCase)) &&
                         DateTime.UtcNow - lastPopupReopen >= TimeSpan.FromSeconds(5))
                {
                    noResponseStreak++;
                    lastPopupReopen = DateTime.UtcNow;
                    lastWake = DateTime.UtcNow;

                    // KHÔNG dùng chrome.runtime.reload() cho ca này: reload làm Brave disable
                    // extension unpacked → popup mới mất chrome.runtime (TypeError sendMessage) lặp vô hạn.
                    // Mở lại popup tươi đủ cứu SW chỉ chậm cold-start; nếu SW câm thật thì leo thang
                    // sang relaunch profile (caller bắt lỗi → BringUpProfileAsync nạp lại extension → SW mới).
                    if (noResponseStreak >= MaxPopupReopenBeforeRelaunch)
                    {
                        var swSummary = await RunnerExtensionTargets.GetAllSwTargetsSummaryAsync(cdpPort, cancellationToken);

                        // ĐANG ở captcha/verify → KHÔNG mở lại profile (reload sẽ xoá captcha đang giải).
                        // Chờ giải tay tối đa 3'; giải xong → trang rời /verify, SW phản hồi lại → probe OK.
                        var onCaptcha = swSummary.Contains("/verify", StringComparison.OrdinalIgnoreCase)
                                     || swSummary.Contains("captcha", StringComparison.OrdinalIgnoreCase);
                        if (onCaptcha)
                        {
                            captchaWaitUntil ??= DateTime.UtcNow.AddMinutes(3);
                            if (DateTime.UtcNow < captchaWaitUntil.Value)
                            {
                                onCaptchaState?.Invoke(true);   // cột Trạng thái → "🚫 Captcha" suốt lúc chờ giải tay
                                if (!captchaWaitLogged)
                                {
                                    log?.Invoke("  ⚠ Trang đang ở captcha/verify — CHỜ giải tay (tối đa 3'), KHÔNG mở lại profile…");
                                    captchaWaitLogged = true;
                                }
                                noResponseStreak = 0;
                                deadline = DateTime.UtcNow.AddSeconds(20); // gia hạn vòng ngoài để không hết 90s khi đang chờ captcha
                                await Task.Delay(3000, cancellationToken);
                                continue;
                            }
                            // Quá 3' vẫn còn captcha → bỏ cuộc (mở lại / hand off) như thường.
                        }

                        log?.Invoke($"  → SW vẫn không phản hồi sau {noResponseStreak} lần mở popup [json/list: {swSummary}] — mở lại profile để nạp lại extension…");
                        throw new InvalidOperationException(
                            "Service worker extension Shopee Data Runner không phản hồi qua CDP sau nhiều lần thử — mở lại profile.");
                    }

                    log?.Invoke("  → Mở lại popup extension tươi (SW chưa phản hồi)…");
                    // Dọn popup cũ/chết (kể cả popup bị orphan) trước khi mở popup mới để tránh bám tab chết.
                    await RunnerExtensionTabs.CloseRunnerExtensionPopupTabsAsync(cdpPort, profileRoot, cancellationToken);
                    await TryWakeServiceWorkerAsync(cdpPort, id, cancellationToken, forceNewPopup: true);
                    await Task.Delay(1800, cancellationToken);
                }
            }

            if (DateTime.UtcNow - lastWake >= TimeSpan.FromSeconds(4))
            {
                var wakeId = ids.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(wakeId))
                {
                    log?.Invoke("  -> Đánh thức extension...");
                    await TryWakeServiceWorkerAsync(cdpPort, wakeId, cancellationToken);
                    lastWake = DateTime.UtcNow;
                    await Task.Delay(1500, cancellationToken);
                }
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Không kết nối được extension \"{RunnerExtensionPaths.ExtensionDisplayName}\" (dự kiến {expectedId}). " +
            "Đóng profile → Mở profile lại từ launcher.");
    }

    /// <summary>
    /// Đánh thức MV3 service worker.
    /// Brave không hỗ trợ ServiceWorker CDP domain.
    /// Cách đáng tin cậy nhất: mở popup.html trong tab mới → browser tự start SW.
    /// </summary>
    internal static async Task TryWakeServiceWorkerAsync(
        int cdpPort,
        string extensionId,
        CancellationToken ct,
        bool forceNewPopup = false)
    {
        // SW có trong /json/list không đủ: SW pinner có thể giữ flat session trước khi hook nạp xong.
        // Chỉ skip khi popup extension đã mở — popup là cầu nối sendMessage tới SW đáng tin cậy nhất.
        var popupUrl = $"chrome-extension://{extensionId}/popup.html";

        // Nếu popup đã mở thì dùng lại; /json/new luôn mở tab foreground nên chỉ dùng fallback cuối.
        var existingPopupTarget = await RunnerExtensionTargets.FindExtensionPopupTargetIdAsync(cdpPort, extensionId, ct);
        if (existingPopupTarget is not null)
        {
            if (!forceNewPopup)
                return;

            await CdpClient.CloseTargetAsync(cdpPort, existingPopupTarget, ct);
            await Task.Delay(350, ct);
        }

        ClientWebSocket? browser = null;
        try
        {
            browser = await RunnerExtensionTargets.ConnectBrowserWebSocketAsync(cdpPort, ct);
            await CdpClient.SendAsync(browser, 50, "Target.createTarget", new
            {
                url = popupUrl,
                background = true,
            }, ct, receiveTimeoutMs: RunnerExtensionRpc.CdpReceiveTimeoutMs);
            // Không đóng tab — để popup làm cầu nối gửi SW cho các lệnh launcher.
        }
        catch { }
        finally
        {
            if (browser is not null)
            {
                try { if (browser.State == WebSocketState.Open) await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct); } catch { }
                browser.Dispose();
            }
        }

        await Task.Delay(300, ct);
        existingPopupTarget = await RunnerExtensionTargets.FindExtensionPopupTargetIdAsync(cdpPort, extensionId, ct);
        if (existingPopupTarget is not null)
            return;

        // Fallback: Chrome/Brave remote endpoint /json/new tạo tab extension ổn định hơn,
        // nhưng có thể giành focus nên chỉ dùng khi Target.createTarget không tạo được popup.
        try
        {
            using var response = await AppServices.DirectHttp.PutAsync(
                CdpEndpoints.New(cdpPort, popupUrl),
                content: null,
                ct);
            if (response.IsSuccessStatusCode)
                return;
        }
        catch
        {
            // fallback below
        }

        // Cách 2 (fallback): Target.activateTarget qua /json/list id (khi SW đang có nhưng chưa active)
        ClientWebSocket? browser2 = null;
        try
        {
            foreach (var target in await CdpClient.TryListTargetsAsync(cdpPort, ct))
            {
                if (!target.IsServiceWorker ||
                    !target.Url.Contains(extensionId, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(target.Id))
                    continue;

                browser2 = await RunnerExtensionTargets.ConnectBrowserWebSocketAsync(cdpPort, ct);
                await CdpClient.SendAsync(browser2, 33, "Target.activateTarget", new { targetId = target.Id }, ct,
                    receiveTimeoutMs: RunnerExtensionRpc.CdpReceiveTimeoutMs);
                await Task.Delay(500, ct);
                return;
            }
        }
        catch { }
        finally
        {
            if (browser2 is not null)
            {
                try { if (browser2.State == WebSocketState.Open) await browser2.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct); } catch { }
                browser2.Dispose();
            }
        }
    }

    private static async Task TryReloadExtensionAsync(int cdpPort, string extensionId, CancellationToken ct)
    {
        const string expression = "(() => { try { chrome.runtime.reload(); } catch (_) {} return { ok: true }; })()";

        try
        {
            var swWsUrl = await RunnerExtensionTargets.GetSwDebuggerUrlFromListAsync(cdpPort, extensionId, ct);
            if (swWsUrl is not null)
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(swWsUrl), ct);
                await CdpClient.SendAsync(socket, 1, "Runtime.enable", null, ct,
                    receiveTimeoutMs: RunnerExtensionRpc.CdpReceiveTimeoutMs);
                await CdpClient.SendAsync(socket, 2, "Runtime.evaluate", new
                {
                    expression,
                    awaitPromise = true,
                    returnByValue = true,
                }, ct, receiveTimeoutMs: RunnerExtensionRpc.CdpReceiveTimeoutMs);
                return;
            }
        }
        catch
        {
            // fallback to popup below
        }

        try
        {
            await TryWakeServiceWorkerAsync(cdpPort, extensionId, ct);
            var popupWsUrl = await RunnerExtensionTargets.FindExtensionPopupDebuggerUrlAsync(cdpPort, extensionId, ct);
            if (popupWsUrl is null)
                return;

            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(popupWsUrl), ct);
            await CdpClient.SendAsync(socket, 1, "Runtime.enable", null, ct,
                receiveTimeoutMs: RunnerExtensionRpc.CdpReceiveTimeoutMs);
            await CdpClient.SendAsync(socket, 2, "Runtime.evaluate", new
            {
                expression,
                awaitPromise = true,
                returnByValue = true,
            }, ct, receiveTimeoutMs: RunnerExtensionRpc.CdpReceiveTimeoutMs);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Giữ SW sống bằng flat session (Target.attachToTarget qua browser WS).
    /// Khác với direct WS, flat session KHÔNG làm SW target biến khỏi /json/list —
    /// probe vẫn thấy và kết nối được SW target trong khi pinner đang giữ.
    /// </summary>
    public static async Task PinSwWithFlatSessionAsync(
        int cdpPort, string extensionId, Action<string> log, CancellationToken ct)
    {
        // Giữ SW sống SUỐT phiên runner: nếu SW bị Brave recycle (idle ~30s) thì tự attach lại.
        // Trước đây dùng for 40 lần rồi dừng → sau khi pin lần đầu, lúc nghỉ 2–4 phút giữa các link
        // SW chết, không ai dựng lại → link kế bị SW_NO_RESPONSE / mất hook (treo ~14 phút). Nay loop
        // tới khi runner dừng (ct hủy).
        var firstAttach = true;
        var hub = PortCdpHub.For(cdpPort);   // kết nối browser DÙNG CHUNG của port (không mở WS mới mỗi vòng)
        while (!ct.IsCancellationRequested)
        {
            string? sess = null;
            try
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
                var swId = await RunnerExtensionTargets.GetSwTargetIdFromListAsync(cdpPort, extensionId, ct).ConfigureAwait(false);
                if (swId is null) continue;

                if (firstAttach)
                    log($"SW pinner: attach flat session tới target {swId[..Math.Min(swId.Length, 16)]}…");

                // Gắn flat-session SW qua hub (tái dùng kết nối; SW recycle chỉ re-attach, KHÔNG mở WS mới).
                sess = await hub.AttachAsync(swId, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sess)) continue;

                // Bật Runtime trên session của SW để có thể evaluate giữ SW bận.
                try { await hub.SendAsync("Runtime.enable", null, sess, ct).ConfigureAwait(false); } catch { }

                if (firstAttach)
                {
                    log("SW pinner: flat session OK, đang giữ SW sống…");
                    firstAttach = false;
                }

                // QUAN TRỌNG: chạy code THẬT bên trong SW mỗi 15s. Evaluate trong context SW mới tính là
                // "hoạt động" và reset bộ đếm idle ~30s của MV3 → giữ SW sống thật. Nếu evaluate ném (SW bị
                // recycle / session chết / Brave relaunch) → thoát vòng trong, vòng ngoài tự attach lại (hub
                // tự nối lại tới Brave mới nếu cần).
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(15_000, ct).ConfigureAwait(false);
                    await hub.SendAsync("Runtime.evaluate", new { expression = "true", returnByValue = true }, sess, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* SW bị recycle hoặc Brave bận — vòng while sẽ tìm & attach lại */ }
            finally
            {
                // BẮT BUỘC: nhả session SW của vòng này TRƯỚC khi vòng ngoài attach session mới. Không detach
                // thì mỗi SW recycle/relaunch lại để lại 1 session mồ côi trên kết nối DÙNG CHUNG → tích tụ
                // dần (hàng chục session sau vài giờ) → nghẽn multiplex → cửa sổ đứng im "Đã dừng". Best-effort;
                // kết nối đã chết thì detach fail vô hại (Brave tự bỏ session theo target đã huỷ).
                if (!string.IsNullOrWhiteSpace(sess))
                    try { await hub.SendAsync("Target.detachFromTarget", new { sessionId = sess }, null, CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }

        log("SW pinner: dừng giữ SW (runner kết thúc).");
    }

    internal static async Task<string?> ResolveExtensionIdAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken ct)
    {
        // Dùng lại ID đã xác thực từ EnsureRunnerExtensionReadyAsync (nhất quán + tránh re-probe)
        if (_resolvedExtensionByPort.TryGetValue(cdpPort, out var cached))
            return cached;

        foreach (var id in await RunnerExtensionTargets.DiscoverRunnerExtensionIdsAsync(cdpPort, profileRoot, ct))
        {
            if (await ExtensionHasLauncherHookAsync(cdpPort, id, ct))
            {
                _resolvedExtensionByPort[cdpPort] = id;
                return id;
            }
        }

        return null;
    }

    private static async Task<bool> ExtensionHasLauncherHookAsync(int cdpPort, string extensionId, CancellationToken ct)
    {
        var (ok, _) = await ProbeExtensionWithReasonAsync(cdpPort, extensionId, ct);
        return ok;
    }

    private static async Task<(bool ok, string reason)> ProbeExtensionWithReasonAsync(
        int cdpPort, string extensionId, CancellationToken ct, DateTime? deadline = null)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (deadline is { } dl && DateTime.UtcNow >= dl)
                return (false, "hết thời gian chờ extension");

            var popupUrl = await RunnerExtensionTargets.FindExtensionPopupDebuggerUrlAsync(cdpPort, extensionId, ct);
            if (popupUrl is null)
            {
                await TryWakeServiceWorkerAsync(cdpPort, extensionId, ct);
                await Task.Delay(800, ct);
                popupUrl = await RunnerExtensionTargets.FindExtensionPopupDebuggerUrlAsync(cdpPort, extensionId, ct);
                if (popupUrl is null)
                {
                    var swSummary = await RunnerExtensionTargets.GetAllSwTargetsSummaryAsync(cdpPort, ct);
                    return (false, $"không có popup extension [json/list SWs: {swSummary}]");
                }
            }

            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            JsonElement? val;
            try
            {
                val = await RunnerExtensionRpc.EvaluateExtensionMethodAsync(
                    cdpPort, extensionId, "probe", null, probeTimeout.Token, maxAttempts: 2);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (false, "probe timeout — extension không phản hồi");
            }

            if (val is null)
                return (false, "evaluate trả về null");

            var ok = val.Value.TryGetProperty("hasScrapeStep", out var hook) && hook.GetBoolean();
            return (ok, ok ? "OK" : $"hasScrapeStep=false (val={val.Value})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
