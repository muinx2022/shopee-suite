using System.Text;
using System.Text.Json;
using Shopee.Core.BigSeller;

namespace OpenMultiBraveLauncherV3;

internal static class LauncherRunnerLoop
{
    private const int MinRestMs = 120_000;
    private const int MaxRestMs = 240_000;
    private const int VideoMaxDurationS = 60;
    private const int MaxDownloadRetries = 1;
    private static readonly Random RestRandom = new();

    /// <summary>Dòng "Bigseller Account: … | Shopee Account: …" hiển thị trên overlay thông báo.</summary>
    private static string BuildAccountHeader(InstanceConfig config)
    {
        var bigSeller = string.IsNullOrWhiteSpace(config.BigSellerAccountName) ? "?" : config.BigSellerAccountName.Trim();
        return $"Bigseller Account: {bigSeller} | Shopee Account: {ShopeeAccountName(config)}";
    }

    private static string ShopeeAccountName(InstanceConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Label))
            return config.Label.Trim();
        // Fallback: lấy phần đầu (email/username) của dòng đăng nhập "email|pass|…".
        var first = (config.ShopeeAccountLogin ?? "").Split('|', '\t', ' ')[0].Trim();
        return string.IsNullOrWhiteSpace(first) ? "?" : first;
    }

    public static async Task RunAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        InstanceConfig config,
        Action<string> log,
        Action onProgress,
        bool preferSuggestedResume,
        CancellationToken cancellationToken,
        Func<Task>? onBeforeExtensionReady = null,
        Func<Task>? onAfterExtensionReady = null,
        Action<bool>? onCaptchaState = null,
        Func<Task>? onScrapeSucceeded = null)
    {
        var sheet = config.DataSheet?.Trim()
            ?? throw new InvalidOperationException("Thiếu sheet.");

        var runRow = config.GetEffectiveRunRow() ?? config.StartRow;
        if (runRow is not > 0)
            throw new InvalidOperationException("Thiếu dòng tiếp theo / từ dòng.");

        if (!config.TryValidateRunRow(runRow.Value, out var rangeError))
            throw new InvalidOperationException(rangeError ?? "Dòng chạy ngoài phạm vi.");

        var startRow = runRow.Value;

        var workbookPath = config.WorkbookPath;
        if (string.IsNullOrWhiteSpace(workbookPath) || !File.Exists(workbookPath))
            throw new InvalidOperationException(
                "Workbook không tồn tại — kiểm tra cấu hình ở mục BigSeller.");

        var endRow = config.EndRow;
        if (endRow is null || endRow < startRow)
            endRow = await ExtensionRunnerAutomation.ResolveEndRowAsync(workbookPath, sheet, startRow, cancellationToken);

        config.EndRow = endRow;

        log($"Đang tải dữ liệu: {sheet} dòng {startRow}–{endRow}…");
        var fetch = await ExtensionRunnerAutomation.FetchSheetLinksAsync(
            workbookPath, sheet, startRow, endRow.Value, cancellationToken);
        var items = fetch.Items;

        if (items.Count == 0)
            throw new InvalidOperationException("Không có link hợp lệ trong khoảng dòng đã chọn.");

        if (fetch.SkippedMissingProductName > 0)
            log($"Bỏ qua {fetch.SkippedMissingProductName} dòng vì cột F (tên SP) trống.");
        if (fetch.SkippedMissingLink > 0)
            log($"Bỏ qua {fetch.SkippedMissingLink} dòng vì cột A không có link hợp lệ.");

        if (onBeforeExtensionReady is not null)
            await onBeforeExtensionReady().ConfigureAwait(false);

        await ExtensionRunnerAutomation.EnsureRunnerExtensionReadyAsync(
            cdpPort, profileRoot, log, cancellationToken, onCaptchaState: onCaptchaState).ConfigureAwait(false);

        if (onAfterExtensionReady is not null)
            await onAfterExtensionReady().ConfigureAwait(false);

        await ExtensionRunnerAutomation.TrimAuxiliaryTabsAsync(cdpPort, cancellationToken)
            .ConfigureAwait(false);

        await ExtensionRunnerAutomation.TryApplyFormConfigAsync(
            cdpPort, profileRoot, sheet, config.StartRow, config.EndRow ?? endRow, cancellationToken).ConfigureAwait(false);

        config.RunLog ??= [];
        config.RunnerRunning = true;
        config.RunnerPhase = "starting";
        config.LastRunnerMessage = "Đang chuẩn bị…";

        int? tabId = null;
        var totalLinks = items.Count;

        await PushDisplayStateAsync(
            cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
            0, totalLinks, null, cancellationToken).ConfigureAwait(false);

        // Tiêu đề tài khoản (BigSeller + Shopee) — cố định trong 1 lượt RunAsync; hiện trên overlay.
        var accountHeader = BuildAccountHeader(config);

        try
        {
            var captchaWaitedRow = -1;   // dòng đã CHỜ-giải-captcha 1 lần (chống lặp vô hạn nếu captcha lại hiện)
            for (var index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = items[index];
                var rowNumber = item.RowNumber;
                var rowData = item.RowData;
                var link = item.Link;
                var sku = ExtractSkuFromRow(rowData, rowNumber);

                config.CurrentRow = rowNumber;
                config.RunnerPhase = "opening";
                config.LastRunnerMessage = $"Đang mở link {index + 1}/{totalLinks} — dòng {rowNumber} (SKU: {sku}).";

                await PushDisplayStateAsync(
                    cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                    index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
                onProgress();

                var statusText = $"{accountHeader}\nĐang mở link {index + 1}/{totalLinks} — dòng {rowNumber}.";

                var step = await ExtensionRunnerAutomation.ExecuteScrapeStepAsync(
                    cdpPort,
                    profileRoot,
                    link,
                    rowNumber,
                    statusText,
                    config.DisplayName,
                    sku,
                    tabId,
                    cancellationToken).ConfigureAwait(false);

                if (step.Aborted)
                    throw new OperationCanceledException();

                if (step.ProxyError)
                {
                    config.RunnerPhase = "paused";
                    config.LastRunnerMessage = step.Message ?? $"Lỗi proxy dòng {rowNumber}.";
                    await PushDisplayStateAsync(
                        cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                        index + 1, totalLinks, step.TabId, cancellationToken).ConfigureAwait(false);
                    onProgress();
                    log(config.LastRunnerMessage);
                    return;
                }

                if (step.Captcha && !step.ScrapeOk)
                {
                    // Captcha giữa lúc scrape → BÁO NGAY ở cột Trạng thái + GIỮ cửa sổ Brave MỞ để giải tay
                    // (trước đây đóng cửa sổ ngay → chưa kịp giải, Shopee đã hủy phiên). Chờ giải tay 1 lần
                    // (extension giữ trang /verify + chờ). Giải xong → cào LẠI đúng dòng này; quá hạn / chưa
                    // giải → dừng (paused) cho user xử lý. captchaWaitedRow chống lặp vô hạn nếu captcha lại hiện.
                    config.RunnerPhase = "paused";
                    config.CaptchaError = true;   // cột "Trạng thái" hiện "🚫 Captcha" NGAY (RowStatus + RefreshRunStatusFromConfig)
                    config.CaptchaUrl = link;   // lưu LINK ĐÍCH đang mở (KHÔNG lưu trang /verify) → "Kiểm tra tk lỗi" mở lại đúng link này
                    config.LastRunnerMessage = $"🚫 Captcha dòng {rowNumber} ({config.DisplayName}) — GIẢI TAY trong cửa sổ Brave (đang giữ mở)…";
                    await PushDisplayStateAsync(
                        cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                        index + 1, totalLinks, step.TabId, cancellationToken).ConfigureAwait(false);
                    onProgress();
                    log(config.LastRunnerMessage);

                    if (step.TabId is int captchaTab && rowNumber != captchaWaitedRow)
                    {
                        captchaWaitedRow = rowNumber;
                        var chk = await ExtensionRunnerAutomation.CheckBeforeNextLinkAsync(
                            cdpPort, profileRoot, captchaTab, rowNumber, config.DisplayName, sku, cancellationToken)
                            .ConfigureAwait(false);
                        if (chk.Aborted)
                            throw new OperationCanceledException();
                        if (chk.Ok)
                        {
                            // Giải captcha xong → cào LẠI đúng dòng này (lùi index để for lặp lại item hiện tại).
                            config.CaptchaError = false;
                            config.RunnerPhase = "opening";
                            tabId = chk.TabId ?? tabId;
                            onProgress();
                            log($"Đã giải captcha dòng {rowNumber} → cào lại dòng này.");
                            index--;
                            continue;
                        }
                    }
                    // Chưa giải / quá hạn → dừng tại chỗ (giữ tiến độ) cho user xử lý (bấm Tiếp tục để chạy lại).
                    config.LastRunnerMessage =
                        $"🚫 Dừng vì captcha - {config.DisplayName}, dòng {rowNumber} (SKU: {sku}). Giải tay rồi bấm Tiếp tục.";
                    await PushDisplayStateAsync(
                        cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                        index + 1, totalLinks, step.TabId, cancellationToken).ConfigureAwait(false);
                    onProgress();
                    log(config.LastRunnerMessage);
                    return;
                }

                if (step.NeedLogin && !step.ScrapeOk)
                {
                    // BigSeller báo "log in first" → token tk BigSeller đã mất phiên. Mọi dòng còn lại sẽ
                    // fail y hệt → DỪNG hẳn job tk này (phase "needlogin" để ScrapeRunner.Classify nhận biết
                    // và KHÔNG vá/retry), báo rõ cần đăng nhập lại BigSeller.
                    // CHẨN ĐOÁN "mất đi đâu": đọc token NGAY lúc bị login-first.
                    //  • còn muc_token + còn hạn → SERVER đá phiên (nhiều phiên/IP), không phải mất token client.
                    //  • không có muc_token → token bị mất/clobber phía client.
                    var tokenAtFail = await BigSellerCookieEngine.GetAuthCookieDebugAsync(cdpPort).ConfigureAwait(false);
                    config.RunnerPhase = "needlogin";
                    config.LastRunnerMessage =
                        step.Message ?? $"⚠ BigSeller mất đăng nhập (log in first) - {config.DisplayName}, dừng tại dòng {rowNumber}. Cần đăng nhập lại tài khoản BigSeller.";
                    await PushDisplayStateAsync(
                        cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                        index + 1, totalLinks, step.TabId, cancellationToken).ConfigureAwait(false);
                    onProgress();
                    log($"{config.LastRunnerMessage} [token lúc bị login-first: {tokenAtFail}]");
                    return;
                }

                // SW của extension chết KHÔNG hồi (vd sau khi relaunch đổi proxy giữa chừng) → mọi link sau
                // báo "No SW", KHÔNG click được nút scrape (chỉ tải video rồi sang dòng kế → "chạy mà không
                // cào"). Ném lỗi extension để runner tự ĐÓNG/MỞ LẠI profile (dựng lại service worker) rồi chạy
                // tiếp ĐÚNG dòng, thay vì im lặng "No SW" mọi dòng. (retryExtensionStart cap 4 lần chống lặp.)
                if (!step.ScrapeOk && (step.Message?.Contains("No SW", StringComparison.OrdinalIgnoreCase) ?? false))
                    throw new InvalidOperationException(
                        $"Extension Shopee Data Runner mất kết nối CDP (No SW) tại dòng {rowNumber} — mở lại profile để dựng lại service worker.");

                tabId = step.TabId ?? tabId;

                var scrapeOk = step.ScrapeOk;
                if (scrapeOk)
                {
                    config.CaptchaError = false;   // cào được lại → hết captcha → cột Trạng thái về bình thường
                    // GHI NHẬN dòng đã scrape NGAY (trước bước video). Nếu extension/CDP rớt giữa chừng,
                    // reload có thể làm rớt service worker → launcher relaunch profile và chạy lại RunAsync.
                    // Nếu chưa ghi nhận, RunAsync resume ĐÚNG dòng cũ → mở lại link → scrape → reload → LẶP VÔ HẠN.
                    // Ghi nhận sớm ⇒ relaunch resume ở dòng KẾ. Video tải sau là phụ; mất video 1 dòng ≪ kẹt lặp.
                    config.LastCompletedRow = rowNumber;
                    config.NextRunRow = rowNumber + 1;

                    if (step.Captcha)
                        log($"Đã giải captcha và click scrape - dòng {rowNumber}.");
                    log($"Đã click scrape - dòng {rowNumber}.");
                    // GHI NGƯỢC token BigSeller server-VỪA-XOAY về file: cào xong = token browser CÒN SỐNG +
                    // server vừa cấp token mới → lưu token MỚI NHẤT vào file để process khác (bị rớt token)
                    // nạp lại token HIỆN HÀNH thay vì token tĩnh đã "ôi" → không đập server bằng token chết
                    // (chuỗi auth-thất-bại dồn dập chính là thứ châm ngòi BigSeller đá nguyên account).
                    if (onScrapeSucceeded is not null)
                        await onScrapeSucceeded().ConfigureAwait(false);
                    await TryShowOverlayAsync(
                        cdpPort, profileRoot, tabId,
                        $"Đã click scrape - dòng {rowNumber}.\nĐang quét video SKU {sku}…",
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    log($"Dòng {rowNumber}: {step.Message ?? "không scrape được."}");
                }

                config.RunnerPhase = "video";
                config.LastRunnerMessage = $"Đang quét video SKU {sku}…";
                log($"Đang quét video SKU {sku} — dòng {rowNumber}…");
                await PushDisplayStateAsync(
                    cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                    index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
                onProgress();

                await TryShowOverlayAsync(
                    cdpPort, profileRoot, tabId,
                    $"Đang quét video SKU {sku}…",
                    cancellationToken).ConfigureAwait(false);

                var videoOk = false;
                var videoPath = "";
                var pageUrl = step.PageUrl ?? link;
                var rowOutcomeOverlay = ""; // thông điệp kết quả cuối để giữ lại khi bước chờ ghi đè overlay

                try
                {
                    var candidates = await PageCdpHelper.CollectVideoCandidatesAsync(
                        cdpPort, pageUrl, cancellationToken).ConfigureAwait(false);
                    var downloadable = candidates
                        .Where(c => c.Duration is null or < VideoMaxDurationS)
                        .ToList();

                    if (downloadable.Count > 0)
                    {
                        Exception? downloadError = null;
                        for (var attempt = 0; attempt <= MaxDownloadRetries; attempt++)
                        {
                            try
                            {
                                var downloadMsg = attempt == 0
                                    ? $"Đang tải video tốt nhất cho SKU {sku}…"
                                    : $"Thử lại tải video (lần {attempt}) — SKU {sku}…";
                                config.LastRunnerMessage = downloadMsg;
                                log($"{downloadMsg} ({downloadable.Count} video tìm thấy)");
                                await PushDisplayStateAsync(
                                    cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                                    index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
                                onProgress();
                                await TryShowOverlayAsync(
                                    cdpPort, profileRoot, tabId, downloadMsg, cancellationToken)
                                    .ConfigureAwait(false);

                                var result = await DownloadBestVideoAsync(sku, downloadable, cancellationToken)
                                    .ConfigureAwait(false);
                                videoOk = true;
                                videoPath = result.SavedPath;
                                var savedMsg = string.IsNullOrWhiteSpace(videoPath)
                                    ? $"Đã tải {sku}.mp4 - dòng {rowNumber}"
                                    : $"Đã tải {sku}.mp4\n{videoPath}";
                                config.LastRunnerMessage = savedMsg;
                                log($"Đã tải {sku}.mp4 - dòng {rowNumber}.");
                                rowOutcomeOverlay = savedMsg;
                                await TryShowOverlayAsync(
                                    cdpPort, profileRoot, tabId, savedMsg, cancellationToken)
                                    .ConfigureAwait(false);
                                downloadError = null;
                                break;
                            }
                            catch (Exception ex)
                            {
                                downloadError = ex;
                            }
                        }

                        if (downloadError is not null)
                        {
                            var errMsg = $"Dòng {rowNumber}: lỗi tải video SKU {sku}";
                            config.LastRunnerMessage = errMsg;
                            log($"Không tải được video SKU {sku}: {downloadError.Message}");
                        rowOutcomeOverlay = errMsg;
                            await TryShowOverlayAsync(
                                cdpPort, profileRoot, tabId, errMsg, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var noVideoMsg = $"Dòng {rowNumber}: không có video phù hợp (SKU {sku})";
                        config.LastRunnerMessage = noVideoMsg;
                        log($"Không có video phù hợp cho SKU {sku} — dòng {rowNumber}.");
                    rowOutcomeOverlay = noVideoMsg;
                        await TryShowOverlayAsync(
                            cdpPort, profileRoot, tabId, noVideoMsg, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    var errMsg = $"Lỗi quét video dòng {rowNumber}: {ex.Message}";
                    config.LastRunnerMessage = errMsg;
                    log(errMsg);
                rowOutcomeOverlay = errMsg;
                    await TryShowOverlayAsync(
                        cdpPort, profileRoot, tabId, errMsg, cancellationToken)
                        .ConfigureAwait(false);
                }

                config.RunLog.Add(new RunnerLogEntry
                {
                    RowNumber = rowNumber,
                    Sku = sku,
                    ScrapeOk = scrapeOk,
                    VideoOk = videoOk,
                    VideoPath = videoPath,
                });
                if (config.RunLog.Count > 200)
                    config.RunLog.RemoveRange(0, config.RunLog.Count - 200);

                config.LastCompletedRow = rowNumber;
                config.NextRunRow = rowNumber + 1;
                config.LastSku = sku;
                config.LastRunnerMessage = videoOk
                    ? $"Xong dòng {rowNumber} - đã tải video."
                    : $"Xong dòng {rowNumber}.";

                await PushDisplayStateAsync(
                    cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                    index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
                onProgress();

                if (index < items.Count - 1)
                {
                    var restMs = RestRandom.Next(MinRestMs, MaxRestMs + 1);
                    var nextRow = rowNumber + 1;
                    var nextAt = DateTimeOffset.Now.AddMilliseconds(restMs);
                    var nextMsg = $"Link tiếp theo (dòng {nextRow}) lúc {nextAt.LocalDateTime:HH:mm:ss}";
                    var waitMsg = string.IsNullOrWhiteSpace(rowOutcomeOverlay)
                        ? $"{accountHeader}\n{nextMsg}"
                        : $"{accountHeader}\n{nextMsg}\n{rowOutcomeOverlay}";

                    config.RunnerPhase = "waiting";
                    config.LastRunnerMessage = waitMsg;
                    await PushDisplayStateAsync(
                        cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                        index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);

                    if (tabId is not null)
                    {
                        try
                        {
                            await ExtensionRunnerAutomation.ShowOverlayAsync(
                                cdpPort, profileRoot, tabId.Value, waitMsg, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // Giữ tabId — link tiếp theo vẫn navigate trong cùng tab.
                        }
                    }

                    log(waitMsg);
                    var preCheckDelay = Math.Max(0, restMs - 30_000);
                    // Trong lúc chờ link kế: giả lập người dùng xem trang (cuộn nhẹ từng nhịp) thay vì
                    // để cửa sổ đứng im — trông tự nhiên hơn với Shopee.
                    await SimulateBrowsingDuringRestAsync(
                        cdpPort, pageUrl, preCheckDelay, cancellationToken).ConfigureAwait(false);

                    if (tabId is not null)
                    {
                        config.LastRunnerMessage = $"Kiểm tra captcha trước link tiếp theo — dòng {nextRow}.";
                        await PushDisplayStateAsync(
                            cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                            index + 1, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
                        onProgress();

                        var check = await ExtensionRunnerAutomation.CheckBeforeNextLinkAsync(
                            cdpPort,
                            profileRoot,
                            tabId.Value,
                            rowNumber,
                            config.DisplayName,
                            sku,
                            cancellationToken).ConfigureAwait(false);

                        if (check.Aborted)
                            throw new OperationCanceledException();

                        if (!check.Ok)
                        {
                            config.RunnerPhase = check.Captcha ? "paused" : "error";
                            if (check.Captcha) config.CaptchaError = true;   // cột Trạng thái → "🚫 Captcha" ngay
                            if (check.Captcha) config.CaptchaUrl = pageUrl;   // lưu LINK trang vừa cào (KHÔNG lưu trang /verify)
                            config.LastRunnerMessage =
                                check.Message ?? (check.Captcha
                                    ? $"Dừng vì captcha trước link tiếp theo - {config.DisplayName}, dòng {rowNumber} (SKU: {sku})."
                                    : $"URL hiện tại không hợp lệ trước link tiếp theo - dòng {rowNumber}.");
                            await PushDisplayStateAsync(
                                cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                                index + 1, totalLinks, check.TabId ?? tabId, cancellationToken).ConfigureAwait(false);
                            onProgress();
                            log(config.LastRunnerMessage);
                            return;
                        }

                        if (check.Waited)
                        {
                            log($"Đã giải captcha trước link tiếp theo - dòng {rowNumber}.");
                            await TryShowOverlayAsync(
                                cdpPort, profileRoot, tabId,
                                $"Đã giải captcha - chuẩn bị mở link tiếp theo (dòng {nextRow})",
                                cancellationToken).ConfigureAwait(false);
                        }
                    }

                    var remainingDelay = (int)Math.Max(
                        0,
                        (nextAt - DateTimeOffset.Now).TotalMilliseconds);
                    if (remainingDelay > 0)
                        await Task.Delay(remainingDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            config.RunnerRunning = false;
            config.RunnerPhase = "finished";
            config.LastRunnerMessage = $"Hoàn tất {totalLinks} link của sheet \"{sheet}\".";
            log(config.LastRunnerMessage);

            if (tabId is not null)
                await ExtensionRunnerAutomation.HideOverlayAsync(cdpPort, profileRoot, tabId.Value, cancellationToken)
                    .ConfigureAwait(false);

            await PushDisplayStateAsync(
                cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                totalLinks, totalLinks, tabId, cancellationToken).ConfigureAwait(false);
            onProgress();
        }
        catch (OperationCanceledException)
        {
            config.RunnerRunning = false;
            config.RunnerPhase = "stopped";
            config.LastRunnerMessage = "Đã dừng từ Multi Brave Manager.";
            log(config.LastRunnerMessage);

            if (tabId is not null)
            {
                try
                {
                    await ExtensionRunnerAutomation.HideOverlayAsync(
                        cdpPort, profileRoot, tabId.Value, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }

            await PushDisplayStateAsync(
                cdpPort, profileRoot, config, sheet, startRow, endRow.Value,
                config.RunLog?.Count ?? 0, totalLinks, tabId, CancellationToken.None).ConfigureAwait(false);
            onProgress();
            throw;
        }
        finally
        {
            config.RunnerRunning = false;
            config.ProgressSyncedAt = DateTimeOffset.Now;
        }
    }

    private static async Task TryShowOverlayAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        int? tabId,
        string text,
        CancellationToken cancellationToken)
    {
        if (tabId is null || string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            await ExtensionRunnerAutomation.ShowOverlayAsync(
                cdpPort, profileRoot, tabId.Value, text, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // tab có thể đã đóng
        }
    }

    /// <summary>
    /// Chờ <paramref name="totalMs"/>, nhưng chia thành nhịp ngẫu nhiên 12–35s, sau mỗi nhịp cuộn trang
    /// Shopee hiện tại để giả lập người dùng đang xem. Best-effort, tôn trọng cancellation (nút Dừng).
    /// </summary>
    private static async Task SimulateBrowsingDuringRestAsync(
        int cdpPort,
        string pageUrlHint,
        int totalMs,
        CancellationToken cancellationToken)
    {
        if (totalMs <= 0)
            return;

        var deadline = DateTimeOffset.Now.AddMilliseconds(totalMs);
        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = (deadline - DateTimeOffset.Now).TotalMilliseconds;
            if (remaining <= 0)
                break;

            var chunk = (int)Math.Min(remaining, Random.Shared.Next(12_000, 35_000));
            await Task.Delay(chunk, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || DateTimeOffset.Now >= deadline)
                break;

            await PageCdpHelper.SimulateHumanBrowsingAsync(cdpPort, pageUrlHint, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task PushDisplayStateAsync(
        int cdpPort,
        DirectoryInfo profileRoot,
        InstanceConfig config,
        string sheet,
        int startRow,
        int endRow,
        int currentLinkIndex,
        int totalLinks,
        int? tabId,
        CancellationToken cancellationToken)
    {
        var state = new
        {
            running = config.RunnerRunning == true,
            phase = config.RunnerPhase ?? "",
            sheetName = sheet,
            startRow,
            endRow,
            currentRow = config.CurrentRow,
            currentLinkIndex,
            totalLinks,
            lastCompletedRow = config.LastCompletedRow,
            lastSheetName = sheet,
            lastSku = config.LastSku ?? "",
            lastMessage = config.LastRunnerMessage ?? "",
            tabId,
            runLog = (config.RunLog ?? []).Select(e => new
            {
                rowNumber = e.RowNumber,
                sku = e.Sku,
                scrapeOk = e.ScrapeOk,
                videoOk = e.VideoOk,
                videoPath = e.VideoPath,
            }).ToList(),
        };

        await ExtensionRunnerAutomation.SetDisplayStateAsync(
            cdpPort, profileRoot, state, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<VideoDownloadResult> DownloadBestVideoAsync(
        string sku,
        List<VideoCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            sku,
            candidates = candidates.Select(c => new
            {
                url = c.Url,
                duration = c.Duration,
                label = c.Label,
            }),
        };

        // TẢI NATIVE (HttpClient) thay cho API Python.
        var coreCandidates = candidates.Select(c => new Shopee.Core.Scrape.VideoCandidate(c.Url, c.Duration ?? 0, c.Label));
        var r = await Shopee.Core.Scrape.VideoDownloader.DownloadBestAsync(
            sku, coreCandidates, ScrapeNativeSettings.VideoOutputDir, cancellationToken);
        if (!r.Success)
            throw new InvalidOperationException(r.Error ?? "Tải video thất bại.");
        return new VideoDownloadResult(r.SavedPath ?? "");
    }

    private static string ExtractSkuFromRow(Dictionary<string, object?> row, int rowNumber)
    {
        foreach (var key in row.Keys)
        {
            if (string.Equals(key, "sku", StringComparison.OrdinalIgnoreCase))
            {
                var sku = row[key]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(sku))
                    return sku;
            }
        }

        foreach (var key in row.Keys)
        {
            if (key.Contains("sku", StringComparison.OrdinalIgnoreCase))
            {
                var sku = row[key]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(sku))
                    return sku;
            }
        }

        return $"row_{rowNumber}";
    }

internal sealed record VideoDownloadResult(string SavedPath);
}

public sealed class RunnerLogEntry
{
    public int RowNumber { get; set; }
    public string Sku { get; set; } = "";
    public bool ScrapeOk { get; set; }
    public bool VideoOk { get; set; }
    public string VideoPath { get; set; } = "";
}
