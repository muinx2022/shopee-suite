using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shopee.Core.Coordination;
using XuLyDonShopee.App.Services;

namespace Shopee.Suite.Infrastructure;

// Partial của OrdersModuleHost: GƯƠNG danh bạ tài khoản Đơn hàng đẩy lên hub + thực thi/ack lệnh hub giao cho
// suất đơn hàng. Pure move từ OrdersModuleHost.cs.
public static partial class OrdersModuleHost
{
    // ── GƯƠNG danh bạ tài khoản Đơn hàng + lệnh hub giao ─────────────────────────
    // Hub KHÔNG sở hữu tài khoản Đơn hàng (chúng nằm trong CSDL cục bộ từng máy); máy tự đẩy một BẢN GƯƠNG
    // (login + shop con + trạng thái phiên + 3 ô đăng nhập) để trang điều phối biết máy nào có tài khoản nào mà
    // ra lệnh, và để máy MỚI kéo về khỏi phải gõ tay mật khẩu. TUYỆT ĐỐI KHÔNG đẩy Cookie (phiên đăng nhập
    // sống — xem khối chú thích trên OrdersAccountsPushRequest, sửa 11/08/2026).

    /// <summary>Nhịp kiểm của worker gương — cũng là khoảng cách TỐI THIỂU giữa 2 lượt đẩy: nhiều thay đổi phiên
    /// liên tiếp (Changed bắn dồn khi mở/đóng trình duyệt) chỉ tốn MỘT lượt đẩy.</summary>
    private static readonly TimeSpan MirrorTick = TimeSpan.FromSeconds(3);

    /// <summary>Nhịp NỀN: không có gì đổi thì cứ 60s đẩy lại một lượt (hub restart / mất gói vẫn hội tụ).
    /// CỐ Ý không bám nhịp heartbeat 12s — danh bạ đổi chậm, đẩy 12s là phí băng thông qua tunnel.</summary>
    private static readonly TimeSpan MirrorIdlePush = TimeSpan.FromSeconds(60);

    /// <summary>Timer worker gương — PHẢI giữ tham chiếu static, không thì Timer bị GC gom là hết nhịp.</summary>
    private static Timer? _mirrorTimer;

    /// <summary>Có thay đổi CẦN đẩy (khởi động / phiên đổi trạng thái / vừa chạy lệnh hub). true lúc khởi tạo
    /// để lượt đầu đẩy ngay. volatile: đặt từ thread phiên, đọc từ thread timer.</summary>
    private static volatile bool _mirrorDirty = true;

    /// <summary>Chốt chống chồng lấn 2 lượt đẩy gương (0/1).</summary>
    private static int _mirrorPushing;

    /// <summary>Mốc lượt đẩy gần nhất (kể cả lượt HỎNG) — chặn hub offline làm worker gọi mạng mỗi 3s.</summary>
    private static DateTimeOffset _mirrorLastPush = DateTimeOffset.MinValue;

    /// <summary>
    /// Bật worker đẩy GƯƠNG danh bạ + nhận lệnh hub. Chỉ được gọi từ <see cref="TryCreate"/> (tức chỉ ở chế độ
    /// có module Đơn hàng — cùng nơi <c>OrdersSlotHeartbeat</c> sống). Hub chưa cấu hình → worker quay không tải:
    /// mỗi nhịp chỉ kiểm tra <see cref="CoordinationRuntime.Client"/> rồi thôi.
    /// </summary>
    private static void WireOrdersMirror(AppServices services)
    {
        // Phiên đổi trạng thái (mở/xếp hàng/dừng) → đánh dấu bẩn; worker gộp lại đẩy 1 lượt ở nhịp kế.
        services.Sessions.Changed += () => _mirrorDirty = true;
        // Hub giao lệnh cho suất đơn hàng → thực thi ở đây (module Đơn hàng không biết hub, suite làm cầu nối).
        OrdersSlotHeartbeat.CommandsReceived = cmds => RunOrdersCommands(services, cmds);
        // Backlog "chờ đẩy" đi kèm NHỊP SỐNG (không phải gương): đó là một con số, đọc tại chỗ từ bộ nhớ
        // (HubOutboxWorker cập nhật sau mỗi lượt quét ~2 phút) nên không tốn thêm request nào. Chỉ rót ở ĐÂY ⇒
        // chế độ không có module Đơn hàng thì hook vẫn null và nhịp không mang field → hub hiện "—".
        OrdersSlotHeartbeat.OutboxPendingProvider = () => services.PendingOutbox.Tong;
        _mirrorTimer = new Timer(_ => _ = MirrorTickAsync(services), null, TimeSpan.Zero, MirrorTick);
    }

    /// <summary>Một nhịp worker gương: có thay đổi → đẩy ngay; không thì chờ đủ <see cref="MirrorIdlePush"/>.</summary>
    private static async Task MirrorTickAsync(AppServices services)
    {
        if (!_mirrorDirty && (DateTimeOffset.UtcNow - _mirrorLastPush) < MirrorIdlePush)
        {
            return;
        }
        await PushOrdersMirrorAsync(services).ConfigureAwait(false);
    }

    /// <summary>
    /// Đẩy MỘT lượt gương danh bạ lên hub. Cờ bẩn được xoá TRƯỚC khi chụp dữ liệu: thay đổi xảy ra trong lúc đẩy
    /// sẽ bật lại cờ → nhịp sau đẩy tiếp (không nuốt mất). Lỗi mạng → nuốt + thử lượt sau (payload là ảnh chụp
    /// TOÀN BỘ danh bạ nên bỏ một lượt không mất dữ liệu, chỉ trễ tối đa 60s).
    /// </summary>
    private static async Task PushOrdersMirrorAsync(AppServices services)
    {
        if (Interlocked.Exchange(ref _mirrorPushing, 1) == 1)
        {
            return; // lượt trước chưa xong → bỏ nhịp này
        }

        try
        {
            // Cổng kiểm là Client (y các hook đẩy đơn): chạy được ở CẢ chế độ Full/Workspace lẫn chế độ Shopee.
            if (CoordinationRuntime.Client is not { } client)
            {
                return; // hub chưa cấu hình → không dựng payload, không gọi mạng
            }

            _mirrorDirty = false;
            await client.PushOrdersAccountsAsync(BuildOrdersMirror(services)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Gồm cả hub CŨ 404 (EnsureSuccessStatusCode ném) + timeout tunnel → lượt sau đẩy lại.
            Trace.WriteLine("[OrdersModuleHost] Đẩy gương danh bạ tài khoản lên hub lỗi: " + ex.Message);
        }
        finally
        {
            // Đặt mốc kể cả khi HỎNG: hub offline thì worker chỉ thử lại mỗi 60s, không gọi mạng mỗi 3s.
            _mirrorLastPush = DateTimeOffset.UtcNow;
            Interlocked.Exchange(ref _mirrorPushing, 0);
        }
    }

    /// <summary>
    /// Chụp danh bạ tài khoản Đơn hàng của máy này thành payload gương. Tài khoản KHÔNG có login (Email trống)
    /// bị BỎ: khoá trên hub là login, không có thì không định danh xuyên máy được.
    /// </summary>
    private static OrdersAccountsPushRequest BuildOrdersMirror(AppServices services)
    {
        var lastSync = services.Orders.MaxSyncedAtByAccount();
        var items = new List<OrdersAccountItem>();
        foreach (var acc in services.Accounts.GetAll())
        {
            var login = acc.Email?.Trim() ?? "";
            if (login.Length == 0)
            {
                continue;
            }

            // Shop con: đúng thứ tự trang /portal/shop của subaccount (sort_order do UpsertShops ghi).
            var shops = services.Results.GetShops(acc.Id)
                .Select(s => new OrdersShopItem(
                    s.ShopLogin, string.IsNullOrWhiteSpace(s.ShopName) ? s.ShopLogin : s.ShopName!))
                .ToList();

            items.Add(new OrdersAccountItem(
                login,
                MirrorSessionState(services.Sessions.Get(acc.Id)?.State),
                shops,
                acc.VerifyFailedAt is not null,
                lastSync.TryGetValue(acc.Id, out var at)
                    ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc))
                    : null)
            {
                // Ô nào máy này chưa nhập thì gửi RỖNG — hub hiểu là "không có gì để góp" và giữ nguyên giá trị
                // đang có, KHÔNG xoá (nếu không, một máy chưa nhập mật khẩu sẽ quét sạch dữ liệu của máy khác
                // ngay nhịp đẩy kế — gương đẩy lại mỗi 3s khi bẩn / 60s khi rảnh).
                Password = acc.Password ?? "",
                VerifyEmail = acc.VerifyEmail ?? "",
                VerifyEmailPassword = acc.VerifyEmailPassword ?? "",
            });
        }
        return new OrdersAccountsPushRequest(LeaseMachineId, LeaseHostname, items);
    }

    /// <summary>Trạng thái phiên → chuỗi gương. Stopped/Error/không có phiên đều là "không chạy" (rỗng): trang
    /// điều phối chỉ cần biết có đang chiếm chỗ hay không, chi tiết lỗi nằm ở app máy đó.</summary>
    private static string MirrorSessionState(SessionState? state) => state switch
    {
        SessionState.Queued => OrdersSessionStates.Queued,
        SessionState.Opening => OrdersSessionStates.Opening,
        SessionState.Running => OrdersSessionStates.Running,
        SessionState.Stopping => OrdersSessionStates.Stopping,
        _ => OrdersSessionStates.Idle,
    };

    /// <summary>Khóa cho <see cref="_cmdSeen"/> (lệnh có thể tới từ nhiều nhịp heartbeat chồng nhau).</summary>
    private static readonly object _cmdLock = new();

    /// <summary>Id các lệnh ĐÃ thực thi (dedup) kèm lúc nhận — mạng chập chờn có thể làm hub gửi lại lệnh cũ,
    /// mà chạy lại 'run' giữa chừng một phiên đang chạy là mở lại trình duyệt (hỏng thật).</summary>
    private static readonly Dictionary<string, DateTimeOffset> _cmdSeen = new(StringComparer.Ordinal);

    /// <summary>Giữ dấu lệnh đã xử trong bao lâu — dài hơn hẳn ngưỡng hub bỏ lệnh không ack (5') để không có cửa
    /// sổ nào một lệnh cũ chạy lại được.</summary>
    private static readonly TimeSpan CmdSeenKeep = TimeSpan.FromHours(1);

    /// <summary>
    /// Thực thi lô lệnh hub vừa giao cho suất đơn hàng rồi ACK từng lệnh. Chạy trên thread nhịp heartbeat: mọi
    /// việc ở đây phải NHANH (<see cref="AccountSessionManager.Start"/>/<c>Stop</c> đều fire-and-forget) và
    /// KHÔNG được ném ra ngoài.
    /// </summary>
    private static void RunOrdersCommands(AppServices services, IReadOnlyList<OrdersCommandDto> cmds)
    {
        foreach (var cmd in cmds)
        {
            if (cmd is null || string.IsNullOrWhiteSpace(cmd.Id))
            {
                continue;
            }

            lock (_cmdLock)
            {
                var cut = DateTimeOffset.UtcNow - CmdSeenKeep;
                foreach (var stale in _cmdSeen.Where(kv => kv.Value < cut).Select(kv => kv.Key).ToList())
                {
                    _cmdSeen.Remove(stale);
                }
                if (!_cmdSeen.TryAdd(cmd.Id, DateTimeOffset.UtcNow))
                {
                    continue; // lệnh này đã chạy rồi → BỎ (không ack lại: hub đã có kết quả lượt trước)
                }
            }

            var (status, error) = ExecuteOrdersCommand(services, cmd);
            _mirrorDirty = true;   // trạng thái vừa đổi → đẩy gương ở nhịp kế (khỏi chờ 60s)
            Shopee.Core.Infrastructure.TaskExt.FireAndForget(
                AckOrdersCommandAsync(cmd.Id, status, error), "ack lệnh đơn hàng");
        }
    }

    /// <summary>
    /// Chạy MỘT lệnh. Map <c>login</c> → tài khoản cục bộ bằng Email (ordinal-ignore-case) — KHÔNG dùng Id
    /// (mỗi máy tự sinh Id nên lệch nhau). Trả (status, lý do) đúng khuôn ack.
    /// <para><c>sync-once</c> / <c>relogin</c>: CHƯA hỗ trợ — client không có điểm vào cấp service cho hai việc
    /// này (một phiên = login subaccount rồi lặp mọi shop trong một vòng liên tục), nhái lại logic phiên là rủi
    /// ro lớn hơn lợi ích. Ack 'failed' kèm lý do rõ thay vì im lặng.</para>
    /// </summary>
    private static (string Status, string? Error) ExecuteOrdersCommand(AppServices services, OrdersCommandDto cmd)
    {
        try
        {
            var login = (cmd.Login ?? "").Trim();
            var acc = services.Accounts.GetAll().FirstOrDefault(a =>
                string.Equals(a.Email?.Trim(), login, StringComparison.OrdinalIgnoreCase));
            if (acc is null)
            {
                return (OrdersCommandStatuses.Failed, $"máy này không có tài khoản {login}");
            }

            switch (cmd.Action)
            {
                case OrdersCommandActions.Run:
                    // IDEMPOTENT: đang mở/đang chạy/đang xếp hàng/đang dừng đều tính là "đã chạy sẵn" — KHÔNG
                    // gọi Start (Start vốn idempotent, nhưng nói rõ để operator biết lệnh không tạo phiên mới).
                    if (services.Sessions.IsRunning(acc.Id))
                    {
                        return (OrdersCommandStatuses.Done, "đã chạy sẵn");
                    }
                    services.Log.Append(login, "Hub ra lệnh: ▶ Chạy — mở phiên (đăng nhập rồi lặp qua các shop)...");
                    services.Sessions.Start(acc.Id);
                    return (OrdersCommandStatuses.Done, null);

                case OrdersCommandActions.Stop:
                    if (!services.Sessions.IsRunning(acc.Id))
                    {
                        return (OrdersCommandStatuses.Done, "không có phiên nào đang chạy");
                    }
                    services.Log.Append(login, "Hub ra lệnh: ✖ Dừng — huỷ vòng lặp + đóng trình duyệt...");
                    services.Sessions.Stop(acc.Id);
                    return (OrdersCommandStatuses.Done, null);

                default:
                    return (OrdersCommandStatuses.Failed, $"bản client này chưa hỗ trợ lệnh '{cmd.Action}'");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[OrdersModuleHost] Chạy lệnh hub giao lỗi: " + ex);
            return (OrdersCommandStatuses.Failed, ex.Message);
        }
    }

    /// <summary>Báo kết quả một lệnh về hub. Mất mạng → nuốt: hub tự quy lệnh 'sent' quá 5' về 'failed' nên
    /// không có lệnh nào kẹt vĩnh viễn.</summary>
    private static async Task AckOrdersCommandAsync(string id, string status, string? error)
    {
        try
        {
            if (CoordinationRuntime.Client is { } client)
            {
                await client.AckOrdersCommandAsync(new OrdersCommandAckRequest(id, status, error)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[OrdersModuleHost] Ack lệnh đơn hàng lên hub lỗi: " + ex.Message);
        }
    }
}
