using Shopee.Core.Coordination;

namespace Shopee.Core.Accounts;

/// <summary>
/// Cấp 1 tài khoản Shopee THAY THẾ (bù) từ KHO CHUNG khi 1 tk trong khung/nhóm đang chạy dính captcha/lỗi
/// → để job KHÔNG bị cạn khung phải chạy lại. Điều kiện tk bù: còn bật (không Disabled), KHÔNG nằm trong
/// khung/nhóm hiện tại (<paramref name="excludeIds"/>), CHƯA module nào giữ cục bộ (<see cref="ShopeeAccountUsage"/>),
/// và (nếu có Hub) CHƯA máy khác lease xuyên máy. Tk trả về đã được:
///  • GIỮ CHỖ cục bộ (TryReserve) — chống module khác (Scrape↔Search) trên CÙNG máy lấy trùng,
///  • KHÓA LEASE trên Hub (nếu kết nối Hub) — chống MÁY KHÁC lấy trùng.
/// Caller CHỊU TRÁCH NHIỆM nhả khi xong: <see cref="ShopeeAccountUsage.ReleaseReservation(string)"/> (cục bộ)
/// + <c>CoordinationRuntime.Hub.ReleaseAccountsAsync</c> (Hub), và heartbeat lease nền như tk khung ban đầu.
/// null = hết tk rảnh để bù (kho không dư / mọi tk dư đang bị máy khác giữ) → caller giữ hành vi cũ (dừng).
/// </summary>
public static class AccountReplenisher
{
    public static async Task<ShopeeAccount?> TryAcquireSpareAsync(
        IReadOnlyCollection<string> excludeIds, HttpCoordinationHub? hub, CancellationToken ct)
    {
        // hub = Hub mà job đang dùng (caller bắt 1 lần lúc bắt đầu). KHÔNG tự đọc CoordinationRuntime.Hub live
        // ở đây — để việc lease/mark trong helper KHỚP với việc caller ghi nhận nhả ở finally (Hub nối/ngắt giữa
        // job sẽ làm lệch → rò lease Hub + rò dấu _hubLeased). null = job chạy 1 máy (chỉ giữ chỗ cục bộ).
        var setAside = new HashSet<string>(StringComparer.Ordinal);   // đã thử, không dùng được → bỏ qua trong LẦN gọi này
        // Trần vòng lặp = số acc trong kho + đệm → tránh lặp vô hạn nếu mọi ứng viên đều bị giành mất giữa chừng.
        var cap = AccountStore.Shared.Accounts.Count + 4;
        for (var attempt = 0; attempt < cap; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // Snapshot kho (tránh lỗi enumerate khi kho đổi ở luồng khác); chọn ứng viên đầu tiên đủ điều kiện.
            // Né tk module khác đang giữ chỗ cục bộ (IsReserved) HOẶC đang giữ lease Hub trên máy này (IsHubLeased
            // — Search nhả reserve cục bộ sớm nhưng vẫn giữ lease Hub → không được cướp, kẻo xóa nhầm dòng lease).
            var cand = AccountStore.Shared.Accounts.ToList().FirstOrDefault(a =>
                !a.Disabled
                && !excludeIds.Contains(a.Id)
                && !setAside.Contains(a.Id)
                && !ShopeeAccountUsage.Shared.IsReserved(a.Id)
                && !ShopeeAccountUsage.Shared.IsHubLeased(a.Id));
            if (cand is null) return null;                 // hết tk rảnh để bù

            // Giữ chỗ cục bộ NGUYÊN TỬ. Module khác vừa giành mất → thử tk khác.
            if (!ShopeeAccountUsage.Shared.TryReserve(cand.Id)) { setAside.Add(cand.Id); continue; }

            // Khóa lease xuyên máy. Máy khác đang giữ → nhả giữ-chỗ cục bộ rồi thử tk khác.
            if (hub is not null)
            {
                HashSet<string> granted;
                try { granted = await hub.ReserveAccountsAsync(new[] { cand.Id }).ConfigureAwait(false); }
                catch { granted = new HashSet<string>(StringComparer.Ordinal) { cand.Id }; }  // Hub lỗi → degrade như 1 máy
                if (!granted.Contains(cand.Id))
                {
                    ShopeeAccountUsage.Shared.ReleaseReservation(cand.Id);
                    setAside.Add(cand.Id);
                    continue;
                }
                // Lease Hub OK → đánh dấu để module khác cùng máy né (khỏi xóa nhầm dòng lease). Caller
                // PHẢI UnmarkHubLeased khi nhả lease ở finally (thường theo cùng list dùng để ReleaseAccountsAsync).
                ShopeeAccountUsage.Shared.MarkHubLeased(new[] { cand.Id });
            }
            return cand;
        }
        return null;
    }
}
