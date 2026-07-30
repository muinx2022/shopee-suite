using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shopee.Core.Coordination;
using XuLyDonShopee.App.Services;

namespace Shopee.Suite.Infrastructure;

// Partial của OrdersModuleHost: KHÓA tài khoản xuyên máy cho module Đơn hàng (xin/nhả + heartbeat + tra máy
// đang giữ). Pure move từ OrdersModuleHost.cs.
public static partial class OrdersModuleHost
{
    /// <summary>Tiền tố khóa account-lease của module Đơn hàng — để KHÔNG đụng khóa của kho tài khoản Shopee
    /// (Scrape/Search dùng thẳng <c>ShopeeAccount.Id</c> làm khóa trên cùng bảng <c>account_leases</c>).</summary>
    private const string LeaseKeyPrefix = "orders:";

    /// <summary>Nhịp heartbeat khóa tài khoản — lease hết hạn ~5' phía hub mà phiên đơn hàng chạy hàng giờ (có
    /// đoạn nghỉ 3–4' giữa hai shop) nên phải giữ nhịp, y <c>AccountLeaseScope.StartHeartbeat</c>.</summary>
    private static readonly TimeSpan LeaseHeartbeatEvery = TimeSpan.FromSeconds(60);

    /// <summary>Danh tính gắn vào request lease = id SUẤT ĐƠN HÀNG (<c>&lt;id-máy&gt;:orders</c>, xem
    /// <see cref="MachineSlots"/>) chứ không phải id PC: hub mới đăng ký mỗi loại suất một dòng máy, khoá tài
    /// khoản đơn hàng phải quy về ĐÚNG suất đơn hàng để trang Máy client / điều phối tra ra máy đang giữ. Đọc
    /// LIVE từ <see cref="MachineIdentity"/> y cách <c>HttpCoordinationHub</c> dựng
    /// <see cref="AccountReserveRequest"/> (đổi tên trong Cài đặt có hiệu lực ngay lượt gửi kế tiếp).</summary>
    private static string LeaseMachineId =>
        MachineSlots.SlotId(MachineIdentity.Shared.MachineId, MachineSlots.Orders);

    /// <summary>Tên máy hiển thị gửi kèm lease — máy khác đọc được "ai đang giữ" (xem <see cref="TenMayDangGiuAsync"/>).</summary>
    private static string LeaseHostname => MachineIdentity.Shared.DisplayName;

    /// <summary>Khóa cho <see cref="_heldLeases"/> + <see cref="_leaseHeartbeat"/> (nhiều phiên tài khoản xin/nhả
    /// song song, timer đọc từ thread khác).</summary>
    private static readonly object _leaseLock = new();

    /// <summary>Các khóa tài khoản máy này ĐANG giữ — TẬP HỢP vì một máy chạy nhiều tài khoản cùng lúc là bình thường.</summary>
    private static readonly HashSet<string> _heldLeases = new(StringComparer.Ordinal);

    /// <summary>Timer heartbeat DUY NHẤT cho cả module (null = đang không giữ khóa nào). PHẢI giữ tham chiếu
    /// static, không thì Timer bị GC gom là hết nhịp.</summary>
    private static Timer? _leaseHeartbeat;

    /// <summary>
    /// KHÓA tài khoản xuyên máy = <c>"orders:" + login</c> đã <c>Trim</c> + hạ chữ. Chuẩn hóa Ở ĐÂY, một chỗ duy
    /// nhất, để module Đơn hàng không phải biết quy ước khóa của hub.
    /// <para><b>Dựng từ LOGIN, TUYỆT ĐỐI không từ <c>accountId</c> cục bộ:</b> mỗi máy tự tạo bản ghi tài khoản nên
    /// Id của CÙNG một tài khoản LỆCH giữa các máy → khóa theo Id sẽ vô tác dụng mà không ai biết.</para>
    /// </summary>
    private static string LeaseKey(string login) => LeaseKeyPrefix + login.Trim().ToLowerInvariant();

    /// <summary>
    /// RÓT 2 hook XIN/NHẢ khóa chạy tài khoản vào bộ dịch vụ module Đơn hàng (mẫu <see cref="WireHubPush"/>) —
    /// chống hai máy cùng chạy MỘT subaccount (tranh đơn "chuẩn bị hàng" + đăng nhập song song một tài khoản
    /// Shopee). Gọi THẲNG 3 route account-lease qua <see cref="CoordinationRuntime.Client"/> (khóa trên hub là
    /// chuỗi BẤT KỲ) — CỐ Ý không dùng lại <see cref="AccountLeaseScope"/>: lớp đó gắn với <c>ShopeeAccountUsage</c>
    /// (kho tài khoản của Scrape/Search), tài khoản module Đơn hàng là thực thể KHÁC nên dùng chung sẽ làm bẩn dấu
    /// per-máy.
    /// <para><b>Mất hub ⇒ VẪN CHO CHẠY</b> (hub chưa cấu hình / offline / lỗi → <c>Ok = true</c>): không có hub thì
    /// cũng không phối hợp được với ai, chặn sẽ làm app vô dụng khi mất mạng (y cách Scrape/Search degrade).</para>
    /// <para>Cổng kiểm là <c>Client</c> — ĐÚNG cổng các hook đẩy đơn đang dùng, nên khóa chạy được ở CẢ chế độ
    /// <b>Full/Workspace</b> lẫn chế độ <b>Shopee</b> (chế độ Shopee chỉ dựng <c>Client</c> + nhịp sống suất đơn
    /// hàng, KHÔNG dựng <c>HttpCoordinationHub</c> — mà máy chạy riêng module đơn hàng lại chính là máy cần khóa
    /// nhất). Khóa ở đây có tiền tố <c>orders:</c> nên không đụng khóa tài khoản của Scrape/Search; và hai bản
    /// Full + Shopee trên CÙNG máy vật lý dùng chung <see cref="MachineIdentity"/> nên id SUẤT ĐƠN HÀNG
    /// (<see cref="LeaseMachineId"/>) của chúng TRÙNG NHAU ⇒ hub cấp lại cho cùng <c>machine_id</c>, không tự
    /// chặn nhau (giữ nguyên hành vi cũ — chỉ đổi id từ id PC sang id suất).</para>
    /// Bị từ chối → BỎ QUA lượt (không xếp hàng chờ), kèm tên máy đang giữ để người dùng biết chỗ nào đang chạy.
    /// </summary>
    private static void WireAccountLease(AppServices services)
    {
        services.AcquireAccountLease = async (login, ct) =>
        {
            // Login trống → không khóa được gì (khóa rỗng vô nghĩa, lại đụng nhau giữa các tài khoản chưa điền
            // login) → cho chạy như khi chưa có hub.
            if (string.IsNullOrWhiteSpace(login))
            {
                return new OrdersLeaseResult(true, null);
            }

            var key = LeaseKey(login);
            try
            {
                // Hub chưa kết nối (chưa cấu hình / offline) → chạy như MỘT máy.
                if (CoordinationRuntime.Client is not { } client)
                {
                    return new OrdersLeaseResult(true, null);
                }

                var res = await client.ReserveAccountsAsync(
                    new AccountReserveRequest(new List<string> { key }, LeaseMachineId, LeaseHostname), ct).ConfigureAwait(false);
                if (res.Granted.Contains(key))
                {
                    lock (_leaseLock)
                    {
                        _heldLeases.Add(key);
                        // Bảo đảm timer đang chạy (một timer cho CẢ module, không phải mỗi khóa một cái).
                        _leaseHeartbeat ??= new Timer(_ => HeartbeatLeases(), null, LeaseHeartbeatEvery, LeaseHeartbeatEvery);
                    }
                    return new OrdersLeaseResult(true, null);
                }

                // CHỈ ở nhánh bị từ chối (đường hiếm) mới hỏi thêm hub tên máy đang giữ — đường được cấp KHÔNG
                // tốn thêm request nào.
                return new OrdersLeaseResult(false, await TenMayDangGiuAsync(client, key, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // hủy CHỦ ĐỘNG (dừng phiên) → cho xuyên để AccountSession xử như hủy
            }
            catch (Exception ex)
            {
                // Gồm cả timeout tunnel → KHÔNG chặn người dùng: degrade như một máy.
                Trace.WriteLine("[OrdersModuleHost] Xin khóa tài khoản trên hub lỗi: " + ex.Message);
                return new OrdersLeaseResult(true, null);
            }
        };

        services.ReleaseAccountLease = async login =>
        {
            if (string.IsNullOrWhiteSpace(login))
            {
                return; // không xin thì không có gì để nhả
            }

            var key = LeaseKey(login);
            lock (_leaseLock)
            {
                _heldLeases.Remove(key);
                if (_heldLeases.Count == 0)
                {
                    // Không còn khóa nào → dừng timer (bật lại ở lần giành kế tiếp).
                    try { _leaseHeartbeat?.Dispose(); } catch { /* bỏ qua */ }
                    _leaseHeartbeat = null;
                }
            }

            try
            {
                if (CoordinationRuntime.Client is { } client)
                {
                    await client.ReleaseAccountsAsync(
                        new AccountReleaseRequest(new List<string> { key }, LeaseMachineId)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Nhả hỏng KHÔNG được ném ngược vào đường dọn dẹp của phiên; lease tự hết hạn ~5' phía hub.
                Trace.WriteLine("[OrdersModuleHost] Nhả khóa tài khoản trên hub lỗi: " + ex.Message);
            }
        };
    }

    /// <summary>Một nhịp heartbeat cho MỌI khóa đang giữ — CHỤP tập dưới <see cref="_leaseLock"/> rồi mới gọi mạng
    /// (không giữ khóa qua <c>await</c>), y khuôn <c>AccountLeaseScope.StartHeartbeat</c>. Tập rỗng / mất hub →
    /// không gọi gì.</summary>
    private static void HeartbeatLeases()
    {
        List<string> snap;
        lock (_leaseLock)
        {
            snap = _heldLeases.ToList();
        }
        if (snap.Count == 0 || CoordinationRuntime.Client is not { } client)
        {
            return;
        }
        Shopee.Core.Infrastructure.TaskExt.FireAndForget(
            client.HeartbeatAccountsAsync(new AccountReleaseRequest(snap, LeaseMachineId)),
            "heartbeat khóa tài khoản đơn hàng");
    }

    /// <summary>
    /// TÊN MÁY đang giữ khóa <paramref name="key"/> — hỏi <c>/fleet</c> rồi tra <c>AccountLeases</c> (hostname,
    /// thiếu thì id máy). CHỈ gọi ở nhánh BỊ TỪ CHỐI để đường chạy bình thường không tốn thêm request.
    /// <para>null = không tra được (hub lỗi / dòng lease vừa biến mất) → câu báo rơi về "máy khác", <b>vẫn KHÔNG
    /// cho chạy</b>: đã bị từ chối là có máy khác giữ, thiếu tên không đổi được kết luận đó.</para>
    /// </summary>
    private static async Task<string?> TenMayDangGiuAsync(HubClient client, string key, CancellationToken ct)
    {
        try
        {
            var fleet = await client.FleetAsync(ct).ConfigureAwait(false);
            var holder = fleet.AccountLeases
                .FirstOrDefault(l => string.Equals(l.AccountId, key, StringComparison.Ordinal));
            var machine = holder is null ? null
                : (!string.IsNullOrWhiteSpace(holder.Hostname) ? holder.Hostname : holder.MachineId);
            return string.IsNullOrWhiteSpace(machine) ? null : machine;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // hủy CHỦ ĐỘNG (dừng phiên) → cho xuyên, KHÔNG hóa thành "máy khác đang giữ"
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[OrdersModuleHost] Đọc máy đang giữ khóa tài khoản lỗi: " + ex.Message);
            return null;
        }
    }
}
