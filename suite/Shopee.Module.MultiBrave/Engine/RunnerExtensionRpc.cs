using System.Net.WebSockets;
using System.Text.Json;
using Shopee.Core.Cdp;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// GỬI LỆNH vào extension runner rồi chờ kết quả: dựng biểu thức JS cho từng method, chạy qua service
/// worker (flat session) hoặc qua cầu popup <c>chrome.runtime.sendMessage</c>, phân loại lỗi tạm thời và
/// map kết quả về record. Phần tìm/đánh thức/giữ SW nằm ở <see cref="RunnerSwLifecycle"/>.
/// </summary>
internal static class RunnerExtensionRpc
{
    public sealed record ScrapeStepResult(
        bool ScrapeOk,
        bool ProxyError,
        bool Captcha,
        bool Aborted,
        string? Message,
        int? TabId,
        string? PageUrl,
        bool NeedLogin = false);

    public sealed record BeforeNextLinkCheckResult(
        bool Ok,
        bool Captcha,
        bool Aborted,
        bool Waited,
        string? Message,
        int? TabId,
        string? PageUrl);

    public static async Task<ScrapeStepResult> ExecuteScrapeStepAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        string link,
        int rowNumber,
        string statusText,
        string instanceName,
        string sku,
        int? tabId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var extensionId = await RunnerSwLifecycle.ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken)
                ?? throw new InvalidOperationException("Không tìm thấy extension Shopee Data Runner.");

            var payload = JsonSerializer.Serialize(new
            {
                link,
                rowNumber,
                statusText,
                instanceName,
                sku,
                tabId,
            });

            // Scrape-step CHỜ LÂU: BigSeller có thể crawl vài phút. Đặt receive-timeout = 600s (> mức chờ
            // 540s của extension) để C# KHÔNG hết-giờ-20s rồi retry gọi lại → reload + click GIỮA CHỪNG lúc
            // đang crawl (đúng triệu chứng "đang scrape chưa xong đã reload" ở tk crawl chậm). Giảm
            // maxAttempts xuống 2: với timeout dài, một lần hết-giờ nghĩa là treo thật, không nên retry nhiều.
            JsonElement? val;
            try
            {
                val = await EvaluateExtensionMethodAsync(
                    cdpPort, extensionId, "executeScrapeStep", payload, cancellationToken,
                    maxAttempts: 2, receiveTimeoutOverride: TimeSpan.FromSeconds(600));
            }
            catch (Exception ex) when (IsPopupBridgeError(ex.Message) ||
                                       ex.Message.Contains("No SW", StringComparison.OrdinalIgnoreCase))
            {
                // SW của extension chết/ngủ → lệnh executeScrapeStep KHÔNG tới được SW ("Receiving end does
                // not exist" / "No SW") = crawl CHƯA hề chạy → REVIVE SW (escalate reload nếu cần) rồi thử LẠI 1 lần.
                // An toàn, KHÔNG double-scrape (vì lệnh trước chưa được nhận). Đây là lý do "mở link nhưng
                // không bấm scrape được" — trước đây bỏ qua dòng (mất dữ liệu), giờ tự khôi phục + chạy lại.
                await RunnerSwLifecycle.EnsureRunnerExtensionReadyAsync(cdpPort, profileRoot, null, cancellationToken).ConfigureAwait(false);
                var extId2 = await RunnerSwLifecycle.ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken) ?? extensionId;
                val = await EvaluateExtensionMethodAsync(
                    cdpPort, extId2, "executeScrapeStep", payload, cancellationToken,
                    maxAttempts: 2, receiveTimeoutOverride: TimeSpan.FromSeconds(600));
            }
            if (val is null)
                return new ScrapeStepResult(false, false, false, false, "Extension không phản hồi.", tabId, link);

            return new ScrapeStepResult(
                val.Value.TryGetProperty("scrapeOk", out var s) && s.GetBoolean(),
                val.Value.TryGetProperty("proxyError", out var p) && p.GetBoolean(),
                val.Value.TryGetProperty("captcha", out var c) && c.GetBoolean(),
                val.Value.TryGetProperty("aborted", out var a) && a.GetBoolean(),
                val.Value.TryGetProperty("message", out var m) ? m.GetString() : null,
                val.Value.TryGetProperty("tabId", out var t) && t.ValueKind == JsonValueKind.Number
                    ? t.GetInt32()
                    : tabId,
                val.Value.TryGetProperty("pageUrl", out var u) ? u.GetString() : link,
                // BigSeller báo "Failed, log in BigSeller first" → token tk này đã chết. Truyền cờ này lên
                // launcher để DỪNG hẳn (không retry/hammer các dòng còn lại — vô nghĩa khi đã mất phiên).
                val.Value.TryGetProperty("needLogin", out var nl) && nl.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Contains("No tab with id", StringComparison.OrdinalIgnoreCase))
                return new ScrapeStepResult(false, false, false, false, "Tab scrape tạm mất kết nối — giữ tab để thử lại.", tabId, link);
            return new ScrapeStepResult(false, false, false, false, msg, tabId, link);
        }
    }

    public static async Task SetDisplayStateAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        object state,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await RunnerSwLifecycle.ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return;

        var stateJson = JsonSerializer.Serialize(state);

        // Ghi state cho extension chính (mở popup wake nếu cần)
        try
        {
            await EvaluateExtensionMethodAsync(
                cdpPort, extensionId, "setDisplayState", stateJson, cancellationToken, maxAttempts: 2);
        }
        catch (Exception ex) when (IsTransientSwError(ex.Message))
        {
            return;
        }

        // Cập nhật thêm BẤT KỲ extension trùng nào đang có popup MỞ SẴN (không mở tab mới).
        // Ph�ng tru?ng h?p ngu?i d�ng dang xem popup c?a b?n extension tr�ng (ID kh�c).
        try
        {
            foreach (var otherId in await RunnerExtensionTargets.DiscoverRunnerExtensionIdsAsync(cdpPort, profileRoot, cancellationToken))
            {
                if (string.Equals(otherId, extensionId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var popupWs = await RunnerExtensionTargets.FindExtensionPopupDebuggerUrlAsync(cdpPort, otherId, cancellationToken);
                if (popupWs is null)
                    continue;

                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(popupWs), cancellationToken);
                await CdpClient.SendAsync(socket, 1, "Runtime.enable", null, cancellationToken,
                    receiveTimeoutMs: CdpReceiveTimeoutMs);
                await CdpClient.SendAsync(socket, 2, "Runtime.evaluate", new
                {
                    expression = BuildPopupInvokeExpression("setDisplayState", PayloadExpression(stateJson)),
                    awaitPromise = true,
                    returnByValue = true,
                }, cancellationToken, receiveTimeoutMs: CdpReceiveTimeoutMs);
            }
        }
        catch { /* bản trùng không phản hồi — bỏ qua */ }
    }

    public static async Task<BeforeNextLinkCheckResult> CheckBeforeNextLinkAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        int tabId,
        int rowNumber,
        string instanceName,
        string sku,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var extensionId = await RunnerSwLifecycle.ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken)
                ?? throw new InvalidOperationException("Không tìm thấy extension Shopee Data Runner.");

            var payload = JsonSerializer.Serialize(new
            {
                tabId,
                rowNumber,
                instanceName,
                sku,
            });

            var val = await EvaluateExtensionMethodAsync(
                cdpPort, extensionId, "checkBeforeNextLink", payload, cancellationToken, maxAttempts: 4);
            if (val is null)
                return new BeforeNextLinkCheckResult(true, false, false, false, null, tabId, null);

            return new BeforeNextLinkCheckResult(
                val.Value.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                val.Value.TryGetProperty("captcha", out var c) && c.GetBoolean(),
                val.Value.TryGetProperty("aborted", out var a) && a.GetBoolean(),
                val.Value.TryGetProperty("waited", out var w) && w.GetBoolean(),
                val.Value.TryGetProperty("message", out var m) ? m.GetString() : null,
                val.Value.TryGetProperty("tabId", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : tabId,
                val.Value.TryGetProperty("pageUrl", out var u) ? u.GetString() : null);
        }
        catch (Exception ex)
        {
            return new BeforeNextLinkCheckResult(false, false, false, false, ex.Message, tabId, null);
        }
    }

    public static async Task ShowOverlayAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        int tabId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await RunnerSwLifecycle.ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return;

        var payload = JsonSerializer.Serialize(new { tabId, text });
        try
        {
            await EvaluateExtensionMethodAsync(
                cdpPort, extensionId, "showOverlay", payload, cancellationToken, maxAttempts: 2);
        }
        catch (Exception ex) when (IsTransientSwError(ex.Message))
        {
            return;
        }
    }

    public static async Task HideOverlayAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        int tabId,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await RunnerSwLifecycle.ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return;

        var payload = JsonSerializer.Serialize(new { tabId });
        try
        {
            await EvaluateExtensionMethodAsync(
                cdpPort, extensionId, "hideOverlay", payload, cancellationToken, maxAttempts: 2);
        }
        catch (Exception ex) when (IsTransientSwError(ex.Message))
        {
            return;
        }
    }

    public static async Task AbortScrapeStepAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await RunnerSwLifecycle.ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return;

        await EvaluateExtensionMethodAsync(
            cdpPort, extensionId, "abortStep", null, cancellationToken, maxAttempts: 2);
    }

    public static async Task StopRunAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await RunnerSwLifecycle.ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
        {
            log("Không tìm thấy extension trên CDP — bỏ qua bước dừng runner.");
            return;
        }

        log("Đang dừng runner…");
        await AbortScrapeStepAsync(cdpPort, profileRoot, cancellationToken).ConfigureAwait(false);

        var evalResult = await EvaluateExtensionRawAsync(
            cdpPort, extensionId, "stopRun", null, cancellationToken, maxAttempts: 4);
        if (evalResult.TryGetProperty("exceptionDetails", out var exDetails) &&
            exDetails.ValueKind == JsonValueKind.Object)
            throw new InvalidOperationException(FormatCdpException(exDetails));

        if (evalResult.TryGetProperty("result", out var res) &&
            res.TryGetProperty("value", out var val) &&
            val.ValueKind == JsonValueKind.Object)
        {
            var last = val.TryGetProperty("lastCompletedRow", out var l) && l.ValueKind == JsonValueKind.Number
                ? l.GetInt32()
                : (int?)null;
            var cur = val.TryGetProperty("currentRow", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : (int?)null;
            var sheet = val.TryGetProperty("sheetName", out var s) ? s.GetString() : "";
            log(
                last is > 0
                    ? $"Extension đã dừng - xong dòng {last}" + (cur is > 0 ? $", đang dừng tại {cur}" : "") +
                      (string.IsNullOrWhiteSpace(sheet) ? "" : $", sheet \"{sheet}\"")
                    : "Extension đã dừng.");
            return;
        }

        log("Extension đã nhận lệnh dừng.");
        await TryBroadcastRunnerStateAsync(cdpPort, profileRoot, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ExtensionRunnerState?> TryReadStateViaCdpAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await RunnerSwLifecycle.ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return null;

        var val = await EvaluateExtensionMethodAsync(
            cdpPort, extensionId, "getRunnerState", null, cancellationToken, maxAttempts: 6);
        return val is null ? null : MapStateFromCdp(val.Value);
    }

    public static async Task<bool> TryApplyFormConfigAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        string sheetName,
        int? startRow,
        int? endRow,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await RunnerSwLifecycle.ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return false;

        var payload = JsonSerializer.Serialize(new
        {
            sheetName = sheetName?.Trim() ?? "",
            startRow = startRow is > 0 ? startRow.Value : 0,
            endRow = endRow is > 0 ? endRow.Value : 0,
        });

        var val = await EvaluateExtensionMethodAsync(
            cdpPort,
            extensionId,
            "applyFormConfig",
            payload,
            cancellationToken,
            maxAttempts: 15);
        return val?.TryGetProperty("ok", out var ok) == true && ok.GetBoolean();
    }

    public static async Task TryBroadcastRunnerStateAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        CancellationToken cancellationToken = default)
    {
        var extensionId = await RunnerSwLifecycle.ResolveExtensionIdAsync(cdpPort, profileRoot, cancellationToken);
        if (extensionId is null)
            return;

        try
        {
            await EvaluateExtensionMethodAsync(
                cdpPort, extensionId, "notifyRunnerUi", null, cancellationToken, maxAttempts: 2);
        }
        catch
        {
            // popup có thể đang đóng
        }
    }

    private static string BuildPopupInvokeExpression(string method, string payloadExpression) =>
        "(async () => {" +
        // Popup bị orphan sau khi extension reload/disable → chrome.runtime mất. Báo rõ thay vì TypeError mơ hồ.
        "if (!(globalThis.chrome && chrome.runtime && chrome.runtime.sendMessage)) " +
        "throw new Error('POPUP_CONTEXT_DEAD: chrome.runtime không khả dụng (popup bị orphan)');" +
        // Đua với timeout 6s: SW câm thì reject nhanh (báo rõ) thay vì treo tới khi CDP cancel ở 10s.
        "const response = await Promise.race([" +
        "chrome.runtime.sendMessage({" +
        $"type:'LAUNCHER_INVOKE',method:{JsonSerializer.Serialize(method)},payload:{payloadExpression}" +
        "})," +
        "new Promise((_, rej) => setTimeout(() => rej(new Error('SW_NO_RESPONSE: service worker không phản hồi sendMessage')), 6000))" +
        "]);" +
        "if (!response?.ok) throw new Error(response?.error || 'Extension không phản hồi');" +
        "return response.result;" +
        "})()";

    private static string BuildServiceWorkerMethodExpression(string method, string payloadExpression) =>
        method switch
        {
            // Yêu cầu CẢ executeScrapeStep LẪN applyFormConfig: SW cũ (cache trong profile, thiếu hàm mới)
            // sẽ trượt probe → EnsureRunnerExtensionReadyAsync leo thang chrome.runtime.reload() dựng SW mới.
            "probe" => "({ hasScrapeStep: typeof globalThis.__launcherExecuteScrapeStep === 'function' && typeof globalThis.__launcherApplyFormConfig === 'function' })",
            "executeScrapeStep" => $"(async () => globalThis.__launcherExecuteScrapeStep({payloadExpression}))()",
            "setDisplayState" => $"(async () => globalThis.__launcherSetDisplayState({payloadExpression}))()",
            "getRunnerState" => "(async () => globalThis.__launcherGetRunnerState())()",
            "applyFormConfig" => $"(async () => globalThis.__launcherApplyFormConfig({payloadExpression}))()",
            "showOverlay" => $"(async () => globalThis.__launcherShowOverlay({payloadExpression}))()",
            "hideOverlay" => $"(async () => globalThis.__launcherHideOverlay({payloadExpression}))()",
            "abortStep" => "(async () => globalThis.__launcherAbortStep())()",
            "stopRun" => "(async () => globalThis.__launcherStopRun())()",
            "notifyRunnerUi" =>
                "(async () => { try { const s=(await chrome.storage.local.get('runnerState')).runnerState||{running:false};" +
                "await chrome.runtime.sendMessage({type:'RUNNER_STATE',state:s}); } catch(_){} return { ok: true }; })()",
            "checkBeforeNextLink" => $"(async () => globalThis.__launcherCheckBeforeNextLink({payloadExpression}))()",
            _ => BuildPopupInvokeExpression(method, payloadExpression),
        };

    private static string PayloadExpression(string? payloadJson) =>
        payloadJson is null ? "null" : $"JSON.parse({JsonSerializer.Serialize(payloadJson)})";

    internal static async Task<JsonElement?> EvaluateExtensionMethodAsync(
        int cdpPort,
        string extensionId,
        string method,
        string? payloadJson,
        CancellationToken ct,
        int maxAttempts = 15,
        TimeSpan? receiveTimeoutOverride = null)
    {
        var evalResult = await EvaluateExtensionRawAsync(
            cdpPort,
            extensionId,
            method,
            payloadJson,
            ct,
            maxAttempts,
            receiveTimeoutOverride);

        if (evalResult.TryGetProperty("exceptionDetails", out var exDetails) &&
            exDetails.ValueKind == JsonValueKind.Object)
            throw new InvalidOperationException(FormatCdpException(exDetails));

        if (evalResult.TryGetProperty("result", out var res) &&
            res.TryGetProperty("value", out var val) &&
            val.ValueKind == JsonValueKind.Object)
            return val.Clone();

        return null;
    }

    private static async Task<JsonElement> EvaluateExtensionRawAsync(
        int cdpPort,
        string extensionId,
        string method,
        string? payloadJson,
        CancellationToken ct,
        int maxAttempts = 15,
        TimeSpan? receiveTimeoutOverride = null)
    {
        var payloadExpr = PayloadExpression(payloadJson);
        var swExpression = BuildServiceWorkerMethodExpression(method, payloadExpr);
        var popupExpression = BuildPopupInvokeExpression(method, payloadExpr);
        var isProbe = string.Equals(method, "probe", StringComparison.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var swResult = await TryEvaluateOnServiceWorkerAsync(cdpPort, extensionId, swExpression, ct, receiveTimeoutOverride);
            if (swResult is not null &&
                !HasTransientSwException(swResult.Value) &&
                (!isProbe || IsReadyProbeResult(swResult.Value)))
                return swResult.Value;

            // ƯU TIÊN đường popup → chrome.runtime.sendMessage → SW.
            // Trong Brave, SW extension thường KHÔNG xuất hiện như target độc lập trong /json/list,
            // và Target.getTargets có thể trả về SW context rỗng (function chưa định nghĩa → false sai).
            // �u?ng popup d�ng sendMessage t? d�nh th?c SW v� g?i d�ng message handler ? d�ng tin c?y.
            var popupWsUrl = await RunnerExtensionTargets.FindExtensionPopupDebuggerUrlAsync(cdpPort, extensionId, ct);
            if (popupWsUrl is not null)
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(popupWsUrl), ct);
                await CdpClient.SendAsync(socket, 1, "Runtime.enable", null, ct,
                    receiveTimeoutMs: CdpReceiveTimeoutMs);
                var popupResult = await CdpClient.SendAsync(socket, 2, "Runtime.evaluate", new
                {
                    expression = popupExpression,
                    awaitPromise = true,
                    returnByValue = true,
                }, ct, receiveTimeoutMs: ReceiveTimeoutMsOf(receiveTimeoutOverride));

                if (!IsPopupBridgeError(popupResult) && !HasTransientSwException(popupResult))
                    return popupResult;

                var swFallback = await TryEvaluateOnServiceWorkerAsync(cdpPort, extensionId, swExpression, ct, receiveTimeoutOverride);
                if (swFallback is not null &&
                    !HasTransientSwException(swFallback.Value) &&
                    (!isProbe || IsReadyProbeResult(swFallback.Value)))
                    return swFallback.Value;

                await RunnerSwLifecycle.TryWakeServiceWorkerAsync(cdpPort, extensionId, ct);
                if (attempt < maxAttempts - 1)
                {
                    await Task.Delay(700, ct);
                    continue;
                }

                return isProbe && swFallback is not null && !IsReadyProbeResult(swFallback.Value)
                    ? popupResult
                    : swFallback ?? popupResult;
            }

            // Fallback: SW-direct (khi popup chưa mở nhưng SW target có trong /json/list)
            // Kh�ng c� popup l?n SW target ? m? popup d? d�nh th?c SW r?i th? l?i
            await RunnerSwLifecycle.TryWakeServiceWorkerAsync(cdpPort, extensionId, ct);
            if (attempt < maxAttempts - 1)
            {
                await Task.Delay(700, ct);
                continue;
            }

            return MakeTransientExtensionErrorResult();
        }

        return MakeTransientExtensionErrorResult();
    }

    private static bool IsReadyProbeResult(JsonElement evalResult)
    {
        if (!evalResult.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("hasScrapeStep", out var hasScrapeStep) ||
            hasScrapeStep.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            return false;

        return hasScrapeStep.GetBoolean();
    }

    private static JsonElement MakeTransientExtensionErrorResult() =>
        JsonDocument.Parse("{\"exceptionDetails\":{\"text\":\"No SW: extension chưa sẵn sàng trên CDP\"}}")
            .RootElement.Clone();

    private static async Task<JsonElement?> TryEvaluateOnServiceWorkerAsync(
        int cdpPort,
        string extensionId,
        string expression,
        CancellationToken ct,
        TimeSpan? receiveTimeoutOverride = null)
    {
        // Tìm SW target qua /json/list (HTTP nhẹ; Brave không trả SW qua Target.getTargets).
        string? swTargetId;
        try { swTargetId = await RunnerExtensionTargets.GetSwTargetIdFromListAsync(cdpPort, extensionId, ct); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(swTargetId))
            return null;

        // Gắn flat-session SW qua kết nối DÙNG CHUNG của port (KHÔNG mở WebSocket mới mỗi eval — đây CHÍNH
        // là chỗ cắt churn lớn nhất, nguồn cạn cổng CDP làm SW câm "đoạn sau"). Mỗi eval: attach → evaluate
        // → DETACH (để session không tích tụ trên kết nối). Brave relaunch / SW recycle → hub tự nối/attach lại.
        var hub = PortCdpHub.For(cdpPort);
        // 20s khớp timeout eval cũ (CdpEvaluateReceiveTimeout) — 30s làm stopRun/eval chậm hơn cần thiết khi
        // SW câm; ReadLoop-fix đã khiến kết nối chết fail-fast nên không cần nới rộng.
        var timeoutMs = (int)(receiveTimeoutOverride?.TotalMilliseconds ?? 20_000);
        string? sess = null;
        try
        {
            sess = await hub.AttachAsync(swTargetId, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sess))
                return null;
            try { await hub.SendAsync("Runtime.enable", null, sess, ct).ConfigureAwait(false); } catch { }
            return await hub.SendAsync(
                "Runtime.evaluate",
                new { expression, awaitPromise = true, returnByValue = true },
                sess, ct, timeoutMs).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            // Nhả session (không đóng kết nối) — tránh tích tụ session trên kết nối dùng chung.
            if (!string.IsNullOrWhiteSpace(sess))
                try { await hub.SendAsync("Target.detachFromTarget", new { sessionId = sess }, null, CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    private static bool IsPopupBridgeError(JsonElement evalResult)
    {
        if (!evalResult.TryGetProperty("exceptionDetails", out var exDetails) ||
            exDetails.ValueKind != JsonValueKind.Object)
            return false;

        var message = FormatCdpException(exDetails);
        return IsPopupBridgeError(message);
    }

    private static bool HasTransientSwException(JsonElement evalResult)
    {
        if (!evalResult.TryGetProperty("exceptionDetails", out var exDetails) ||
            exDetails.ValueKind != JsonValueKind.Object)
            return false;

        return IsTransientSwError(FormatCdpException(exDetails));
    }

    internal static bool IsPopupBridgeError(string message) =>
        message.Contains("Receiving end does not exist", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Could not establish connection", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("sendMessage", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("chrome.runtime", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransientSwError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return IsPopupBridgeError(message) ||
               message.Contains("No SW", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("service worker", StringComparison.OrdinalIgnoreCase) ||
               // SW vừa (đăng ký lại + ) khởi động, top-level background.js chưa chạy xong nên các hàm
               // globalThis.__launcher* CHƯA định nghĩa → "is not a function". Đây là trạng thái TẠM
               // THỜI (cold start), không phải lỗi thật: retry vài nhịp là SW init xong và hàm có mặt.
               message.Contains("is not a function", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("remote party closed the WebSocket", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Inspected target navigated or closed", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatCdpException(JsonElement exDetails)
    {
        if (exDetails.TryGetProperty("exception", out var ex) &&
            ex.TryGetProperty("description", out var desc))
        {
            var text = desc.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Split('\n')[0];
        }

        if (exDetails.TryGetProperty("text", out var t))
            return t.GetString() ?? exDetails.ToString();

        return exDetails.ToString();
    }

    private static ExtensionRunnerState MapStateFromCdp(JsonElement root)
    {
        if (!root.TryGetProperty("runnerState", out var rs) || rs.ValueKind != JsonValueKind.Object)
            rs = root;

        var sheetName = GetStringProp(rs, "sheetName", "lastSheetName");
        var startRow = GetIntProp(rs, "startRow");
        var endRow = GetIntProp(rs, "endRow");

        if (root.TryGetProperty("lastRunConfig", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace(sheetName))
                sheetName = GetStringProp(cfg, "sheetName");
            if (startRow is null or < 1)
                startRow = GetIntProp(cfg, "startRow");
            if (endRow is null or < 1)
                endRow = GetIntProp(cfg, "endRow");
        }

        return new ExtensionRunnerState
        {
            SheetName = sheetName,
            StartRow = startRow,
            EndRow = endRow,
            LastCompletedRow = GetIntProp(rs, "lastCompletedRow"),
            CurrentRow = GetIntProp(rs, "currentRow"),
            LastSku = GetStringProp(rs, "lastSku"),
            Phase = GetStringProp(rs, "phase"),
            Running = GetBoolProp(rs, "running"),
            LastMessage = GetStringProp(rs, "lastMessage"),
        };
    }

    private static string? GetStringProp(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el))
                continue;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
            else if (el.ValueKind == JsonValueKind.Number)
            {
                return el.GetRawText();
            }
        }

        return null;
    }

    private static int? GetIntProp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n) && n > 0)
            return n;
        if (el.ValueKind == JsonValueKind.String &&
            int.TryParse(el.GetString(), out var parsed) &&
            parsed > 0)
            return parsed;
        return null;
    }

    private static bool? GetBoolProp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    // Trần chờ phản hồi CDP của module (CdpClient mặc định 30s — ở đây phải là 20s):
    // 8s quá ngắn khi chạy nhiều Brave song song vì handshake (Runtime.enable) dồn cục làm CDP đáp
    // chậm → "Runtime.enable quá thời gian chờ (8s)" dù SW vẫn sống; 20s vừa đủ cho tải cao.
    internal const int CdpReceiveTimeoutMs = 20_000;

    /// <summary>Đổi trần chờ tuỳ chọn sang ms cho <see cref="CdpClient.SendAsync"/>. Dùng cho lệnh CHỜ LÂU
    /// (vd executeScrapeStep — BigSeller có thể crawl vài phút): để 20s thì C# tưởng hết giờ → gọi lại
    /// executeScrapeStep → reload + click lại GIỮA CHỪNG khi đang crawl.</summary>
    private static int ReceiveTimeoutMsOf(TimeSpan? receiveTimeoutOverride) =>
        (int)(receiveTimeoutOverride?.TotalMilliseconds ?? CdpReceiveTimeoutMs);
}
