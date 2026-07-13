using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shopee.Core.Coordination;
using Shopee.Core.Infrastructure;

namespace Shopee.Suite.Services;

/// <summary>
/// Xử lý lệnh UPDATE app do operator ra từ Hub. Kênh lệnh: phản hồi POST /machines/heartbeat mang
/// <c>UpdateRequestedAt</c> (chuỗi ISO lúc ra lệnh) — dùng NGUYÊN VĂN làm ID dedup, mỗi lệnh xử đúng 1 lần.
/// Luồng lệnh mới: ack "checking" → CheckAsync (check + tải nền) → có bản mới thì ack "restarting" rồi dừng-êm +
/// ApplyAndRestart; bản restart mang version mới, heartbeat kế báo lên Hub → Hub TỰ clear cờ. Không có bản mới →
/// ack "already-latest"; chạy dev/bin (không cài Velopack) → ack "unsupported"; lỗi → ack "failed: …". Ack TERMINAL
/// (already-latest/unsupported/failed*) khiến Hub thôi đẩy lệnh.
///
/// VÌ SAO PERSIST (update-request.json): ca hiểm nhất là apply HỎNG kiểu "restart xong version KHÔNG đổi" — Hub
/// không tự biết (chỉ biết clear khi thấy version đổi) nên cứ đẩy lại lệnh → VÒNG LẶP restart vô hạn. Ta ghi
/// State="restarting" TRƯỚC khi restart (bền qua restart); mở lại thấy CÙNG lệnh còn "restarting" = đã thử mà
/// version không đổi → ack "failed" đúng 1 lần rồi thôi. State "acked:&lt;status&gt;" nhớ đã ack terminal cho lệnh
/// này để (nếu ack thất lạc mà Hub còn đẩy) GỬI LẠI đúng status, KHÔNG chạy lại update.
///
/// VÌ SAO ack lỗi mạng thì ĐỂ LƯỢT POLL SAU xử: offline → Hub vẫn giữ cờ, poll 12s sau lệnh lại về, guard
/// in-flight lúc đó đã nhả → handler chạy lại đúng nhánh (nhờ persist) → tự lành, khỏi cần vòng thử-lại riêng.
/// </summary>
public sealed class RemoteUpdateService
{
    public static RemoteUpdateService Shared { get; } = new();

    private static readonly string FilePath = SuitePaths.RootFile("update-request.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _ioLock = new();
    private int _inflight;   // 0/1 chống xử lý chồng lấn khi poll 12s bắn lệnh trong lúc lượt trước chưa xong

    private RemoteUpdateService() { }

    /// <summary>Trạng thái đã xử lý cho 1 lệnh (persist qua restart). State: "restarting" = đã ack restarting +
    /// sắp ApplyAndRestart; "acked:&lt;status&gt;" = đã ack terminal cho lệnh này. File hỏng → coi như chưa có.</summary>
    private sealed class UpdateRequestState
    {
        public string RequestedAt { get; set; } = "";
        public string State { get; set; } = "";
    }

    /// <summary>Hub bắn lệnh update (từ PollAsync, NỀN — KHÔNG được block poll). Guard in-flight: đang xử lý thì
    /// bỏ lượt gọi này (poll 12s sau tự gọi lại nếu còn lệnh). Toàn bộ chạy trên Task nền.</summary>
    public void OnCommand(HttpCoordinationHub hub, string requestedAt)
    {
        if (string.IsNullOrWhiteSpace(requestedAt)) return;
        if (Interlocked.Exchange(ref _inflight, 1) == 1) return;   // đang xử lý → bỏ, lượt poll sau bù
        _ = Task.Run(async () =>
        {
            try { await HandleAsync(hub, requestedAt); }
            finally { Interlocked.Exchange(ref _inflight, 0); }
        });
    }

    private async Task HandleAsync(HttpCoordinationHub hub, string requestedAt)
    {
        try
        {
            var st = Load();

            // ── Lệnh này đã gặp trước đó (cùng RequestedAt) ──
            if (st is not null && st.RequestedAt == requestedAt)
            {
                if (st.State == "restarting")
                {
                    // Đã restart cho ĐÚNG lệnh này mà Hub CÒN đẩy → version không đổi = apply hỏng. Ack "failed" 1
                    // lần để Hub thôi đẩy (chống vòng lặp restart vô hạn). Ack lỗi mạng → giữ state, lượt sau thử lại.
                    const string status = "failed: đã khởi động lại nhưng phiên bản không đổi";
                    HubLog.Warn("⬆ Lệnh update: đã khởi động lại nhưng phiên bản KHÔNG đổi — báo Hub thất bại (thôi đẩy lại).");
                    if (await hub.TryAckUpdateAsync(status)) Save(requestedAt, "acked:" + status);
                }
                else if (st.State.StartsWith("acked:", StringComparison.Ordinal))
                {
                    // Đã ack terminal rồi mà Hub còn đẩy → ack lần trước có thể thất lạc → GỬI LẠI đúng status đã lưu.
                    await hub.TryAckUpdateAsync(st.State.Substring("acked:".Length));
                }
                return;
            }

            // ── Lệnh MỚI (RequestedAt khác lần lưu) ──
            HubLog.Info("⬆ Nhận lệnh update từ Hub — đang kiểm tra bản mới…");
            if (!UpdateService.Shared.IsSupported)
            {
                if (await hub.TryAckUpdateAsync("unsupported")) Save(requestedAt, "acked:unsupported");
                return;
            }

            await hub.TryAckUpdateAsync("checking");   // best-effort, chưa làm gì bất hồi → không cần persist

            // CheckAsync trả null SỚM khi Busy (check nền lúc boot / nút "Kiểm tra" tay đang chạy) → kết luận ngay
            // sẽ ack nhầm "already-latest" trong lúc bản mới đang tải dở. Chờ lượt check kia nhả (trần 2') rồi mới
            // check lượt mình — đã tải xong thì lượt này chỉ xác nhận lại, rẻ.
            for (var waited = 0; waited < 120 && UpdateService.Shared.Busy; waited++)
                await Task.Delay(1000);

            var err = await UpdateService.Shared.CheckAsync();
            if (err is not null)
            {
                var status = "failed: " + err;
                if (await hub.TryAckUpdateAsync(status)) Save(requestedAt, "acked:" + status);
                return;
            }
            if (!UpdateService.Shared.UpdateReady)
            {
                if (await hub.TryAckUpdateAsync("already-latest")) Save(requestedAt, "acked:already-latest");
                return;
            }

            // Có bản mới → ack "restarting" + PERSIST TRƯỚC khi restart (bền qua restart để phát hiện apply hỏng).
            HubLog.Info($"⬆ Có bản v{UpdateService.Shared.AvailableVersion} — dừng việc + khởi động lại…");
            await hub.TryAckUpdateAsync("restarting");
            Save(requestedAt, "restarting");
            await UpdateService.Shared.ApplyAfterPrepareAsync();   // bình thường app THOÁT tại đây

            // Còn sống tới đây = pending mất / không áp dụng được → ack failed để Hub thôi đẩy.
            const string applyFail = "failed: không áp dụng được bản đã tải";
            if (await hub.TryAckUpdateAsync(applyFail)) Save(requestedAt, "acked:" + applyFail);
        }
        catch (Exception ex)
        {
            // Exception bất ngờ → cố ack failed + persist, đừng để lệnh kẹt "checking" mãi trên Hub.
            try
            {
                var status = "failed: " + ex.Message;
                if (await hub.TryAckUpdateAsync(status)) Save(requestedAt, "acked:" + status);
            }
            catch { }
        }
    }

    private UpdateRequestState? Load()
    {
        lock (_ioLock)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                return JsonSerializer.Deserialize<UpdateRequestState>(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch { return null; }   // file hỏng → coi như chưa có (xử như lệnh mới)
        }
    }

    private void Save(string requestedAt, string state)
    {
        lock (_ioLock)
        {
            try
            {
                Directory.CreateDirectory(SuitePaths.Root);
                var json = JsonSerializer.Serialize(new UpdateRequestState { RequestedAt = requestedAt, State = state }, JsonOpts);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }
    }
}
