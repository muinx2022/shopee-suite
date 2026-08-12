using System.Text.Json;
using Shopee.Core.Accounts;
using Shopee.Core.Coordination;
using Shopee.Modules.Search;
using Shopee.Suite.Infrastructure;
using ShopeeStatApp.Models;
using ShopeeStatApp.Services;

namespace Shopee.Suite.Modules.Search;

/// <summary>Phần SearchViewModel: mảng VIỆC HUB GIAO (đa máy) — quan sát/dừng việc, kết quả terminal,
/// lượt chạy silent kèm account-lease xuyên máy, và đẩy sản phẩm cào được lên Hub theo chu kỳ.</summary>
public sealed partial class SearchViewModel
{
    // ── Việc Search Hub giao (đa máy) ─────────────────────────────────────────────
    /// <summary>true nếu lượt chạy hiện tại CHÍNH LÀ việc Hub <paramref name="id"/> (cho AssignmentWorker quan sát).</summary>
    public bool IsRunningAssignment(string id) => IsRunning && _assignmentId == id;

    /// <summary>Dừng lượt chạy nếu nó thuộc việc Hub <paramref name="id"/> (Hub huỷ việc → client dừng).</summary>
    public void StopAssignment(string id) { if (_assignmentId == id) Stop(); }

    /// <summary>Lấy (và xoá) kết quả terminal của việc Search <paramref name="id"/>:
    /// <see cref="LedgerStatus.Completed"/> | <see cref="LedgerStatus.Stopped"/> |
    /// <see cref="AssignmentStatus.Failed"/>; null nếu chưa có (AssignmentWorker sẽ suy theo grace). Search
    /// KHÔNG ghi ledger nên đây là kênh nội bộ client, mượn đúng bộ chữ của sổ hoàn thành.</summary>
    public string? TakeAssignmentOutcome(string id) => _assignmentOutcomes.TryRemove(id, out var v) ? v : null;

    /// <summary>
    /// Chạy đúng KHỐI link Hub giao (silent, KHÔNG mở dialog). Khóa tối đa <c>AccountsPerClient</c> tài khoản
    /// Shopee qua Hub account-lease (máy khác không đụng), heartbeat nền 60s, chạy resume theo từng link, rồi
    /// nhả khóa. Bám đúng cơ chế account-lease của Scrape. Trả về khi xong/dừng.
    /// </summary>
    public async Task RunAssignmentAsync(string assignmentId, SearchJobPayload p, CancellationToken externalCt)
    {
        if (IsRunning) return;   // máy đang chạy 1 search khác — bỏ (AssignmentWorker đã tiền-kiểm)
        var links = (p.Links ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (links.Count == 0) return;
        if (_pool.Count == 0) { Log("⚠ Việc Search Hub giao: kho tài khoản Shopee trống — bỏ qua."); return; }

        var region = string.IsNullOrWhiteSpace(p.Region) ? Region : p.Region!;
        var source = string.IsNullOrWhiteSpace(p.SourceFile) ? "(Hub giao)" : p.SourceFile!;

        // Dựng tab cho từng link của khối (mỗi link 1 tab) — giống chạy tay để theo dõi tiến độ. Khối Hub
        // giao cũng phải khử link TRÙNG như đường chạy tay: 2 lane cùng một link ghi đè file Excel của nhau.
        var items = SearchRunner.DedupLinks(links.Select((link, i) => (Index: i + 1, Link: link, SourceFile: source)));
        if (items.Count < links.Count) Log($"⚠ bỏ {links.Count - items.Count} link trùng trong khối Hub giao.");
        foreach (var it in items)
        {
            var tab = LinkTabs.FirstOrDefault(t => t.Link == it.Link);
            if (tab is null) { tab = new SearchFileTab(it.Index, it.Link, source, FileRunCoordinator.CatLabel(it.Link)); LinkTabs.Add(tab); }
            tab.Status = "chờ";
        }
        SelectedLinkTab ??= LinkTabs.FirstOrDefault();

        _assignmentId = assignmentId;
        IsRunning = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _usedAccounts.Clear();
        lock (_pushedItemIds) _pushedItemIds.Clear();

        var accHub = CoordinationRuntime.Hub;
        AccountLeaseScope? accScope = null;   // khóa tk Shopee xuyên máy: reserve→heartbeat→bù→nhả (gói)
        System.Threading.Timer? pushTimer = null;
        var startedRun = false;
        var failedRun = false;
        // BeginRun TRƯỚC khi MarkHubLeased ở vòng giành acc (mirror Scrape) → _activeRuns của CHÍNH lượt này giữ
        // ≥1 suốt, để EndRun của module KHÁC (vd Scrape vừa xong) KHÔNG xóa nhầm dấu _hubLeased ta vừa đặt.
        ShopeeAccountUsage.Shared.BeginRun();
        try
        {
            // Khóa tk Shopee xuyên máy: giành cả pool (Hub trả tk KHÔNG bị máy khác giữ) rồi chỉ giữ N cái đầu,
            // nhả phần thừa để máy khác dùng. Offline (không Hub) → dùng cả pool như chạy 1 máy.
            var working = _pool.Select(a => a.Id).ToList();
            if (accHub is not null)
            {
                // Giành ĐÚNG số acc cần (N) — KHÔNG giành cả pool rồi trả. Cơ chế per-account (khớp lease cục bộ +
                // Hub từng cái, chống 2 module cùng máy xóa nhầm 1 dòng lease machine-scoped) gói trong
                // AccountLeaseScope; heartbeat nền 60s + bù tk cũng do scope lo. (Trước là bản mirror Scrape.)
                List<string> acquired;
                (accScope, acquired) = await AccountLeaseScope.AcquirePerAccountAsync(accHub, working, Math.Max(1, p.AccountsPerClient));
                working = acquired;   // finally (Dispose scope) nhả ĐÚNG những gì đã giành → không rò acc
                if (working.Count == 0)
                { Log("⚠ Việc Search Hub giao: mọi tài khoản Shopee đang được máy khác giữ — bỏ qua."); return; }
            }

            var specs = _pool.Where(a => working.Contains(a.Id)).Select(ShopeeAccountSpecFactory.ToSearchSpec).ToList();
            // Số lane do CHÍNH client quyết theo LaneCount cấu hình của MÁY NÀY (giống Scrape chạy tùy máy) —
            // KHÔNG theo p.Lanes của Hub. Vẫn kẹp theo số acc giành được và số link của khối (không thể nhiều
            // lane hơn acc/link).
            var lanes = Math.Max(1, Math.Min(Math.Min(LaneCount, specs.Count), items.Count));
            Log($"▶ Search (Hub giao) {items.Count} link · {specs.Count}/{_pool.Count} acc (khóa xuyên máy) · {lanes} lane · khu vực \"{region}\".");
            startedRun = true;
            // Đẩy sản phẩm cào được lên Hub theo CHU KỲ 20s trong lúc chạy → kết quả gộp cập nhật LIÊN TỤC.
            if (CoordinationRuntime.Client is not null)
                pushTimer = new System.Threading.Timer(_ => _ = PushNewCollectedAsync(_runner, source), null,
                    TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
            // BÙ TK THAY THẾ: khi 1 tk trong nhóm dính captcha, scope xin 1 tk RẢNH từ kho (đã khóa lease xuyên
            // máy), ghi vào sổ lease (heartbeat + nhả ở finally) rồi NHẢ giữ-chỗ cục bộ để lane borrow như tk ban đầu.
            Func<IReadOnlyCollection<string>, CancellationToken, Task<SearchAccountSpec?>>? acquireReplacement = null;
            if (accScope is not null)
                acquireReplacement = async (excludeIds, rct) =>
                {
                    var repl = await accScope.AcquireReplacementAsync(excludeIds, rct).ConfigureAwait(false);
                    return repl is null ? null : ShopeeAccountSpecFactory.ToSearchSpec(repl);
                };
            await RunCoreAsync(items, specs, lanes, region, resume: true, _cts.Token, acquireReplacement);
            Log(_cts.IsCancellationRequested ? "── Đã dừng việc Search (giữ phiên). ──" : "── Hoàn tất việc Search Hub giao. ──");
        }
        catch (OperationCanceledException) { Log("── Đã dừng việc Search. ──"); }
        catch (Exception ex) { failedRun = true; Log("✘ Lỗi việc Search Hub giao: " + ex.Message); }
        finally
        {
            if (pushTimer is not null) { try { await pushTimer.DisposeAsync(); } catch { } }
            // Nhả account-lease (heartbeat → UnmarkHubLeased → ReleaseAccountsAsync Hub), snapshot-under-lock chống rò.
            if (accScope is not null) { try { await accScope.DisposeAsync().ConfigureAwait(false); } catch { } }
            // Đẩy nốt phần sản phẩm còn lại lên Hub (kể cả khi dừng dở — gửi phần đã cào).
            if (startedRun) await PushNewCollectedAsync(_runner, source);
            // Kết quả terminal cho AssignmentWorker báo Hub đúng: chưa chạy được / lỗi = failed; bị dừng = stopped;
            // chạy hết bình thường = completed. (Search không ghi ledger nên phải tự ghi outcome ở đây.)
            var canceled = _cts?.IsCancellationRequested == true;
            _assignmentOutcomes[assignmentId] = !startedRun || failedRun
                ? AssignmentStatus.Failed
                : canceled ? LedgerStatus.Stopped : LedgerStatus.Completed;
            ResetUsedAccounts();
            _assignmentId = null;
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            ShopeeAccountUsage.Shared.EndRun();   // đối xứng BeginRun ở đầu (counter về, nhả lưới an toàn nếu là lượt cuối)
        }
    }

    /// <summary>Đẩy phần sản phẩm MỚI (chưa đẩy) của lượt chạy này lên Hub để gộp xuyên máy. Best-effort, chia
    /// lô 500; chỉ đánh dấu "đã đẩy" SAU khi gửi thành công (lỗi mạng → lần sau gửi lại). Gọi định kỳ + lúc kết thúc.</summary>
    private async Task PushNewCollectedAsync(SearchRunner? runner, string sourceFile)
    {
        var client = CoordinationRuntime.Client;
        if (client is null || runner is null) return;
        List<ProductResult> fresh;
        lock (_pushedItemIds)
            fresh = runner.CollectedProducts().Where(p => p.ItemId != 0 && !_pushedItemIds.Contains(p.ItemId)).ToList();
        if (fresh.Count == 0) return;
        var machineId = CoordinationRuntime.Hub?.MachineId ?? "";
        for (var i = 0; i < fresh.Count; i += 500)
        {
            var batch = fresh.GetRange(i, Math.Min(500, fresh.Count - i));
            var payload = batch.Select(p => new SearchProductItem(p.ItemId, JsonSerializer.Serialize(p))).ToList();
            try
            {
                await client.PushSearchProductsAsync(new SearchProductsPushRequest(machineId, sourceFile, payload));
                lock (_pushedItemIds) foreach (var p in batch) _pushedItemIds.Add(p.ItemId);
            }
            catch { }
        }
    }
}
