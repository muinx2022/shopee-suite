using System.Threading;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;

namespace Shopee.Suite.Modules.BigSeller;

/// <summary>
/// Đẩy (upsert) acc/shop BigSeller của máy này LÊN Hub sau khi user thêm/sửa — client GIỜ là nguồn phát sinh
/// acc/shop (thêm ở client PHẢI lên hub, kẻo lượt pull kế BackupService.MergeBigSeller mirror-XOÁ mất). Gộp
/// nhiều thay đổi liên tiếp thành 1 lần POST ~2s sau lần sửa cuối (giống <see cref="PersistDebounce"/>
/// cho ghi đĩa). Fire-and-forget, NUỐT lỗi (hub offline → thôi; nút "Đồng bộ acc" tay upsert-trước-pull sẽ bù).
/// Hub gộp KHÔNG XÓA + không đổi thật thì không bump version → chạy thừa vô hại.
///
/// LƯU Ý Id (bài học lặp nhiều lần): sau upsert + pull, acc client trùng EMAIL với acc hub sẽ bị mirror đồng
/// nhất Id theo hub (hub là nguồn sự thật của Id) — chấp nhận theo hợp đồng sync hiện có.
/// </summary>
internal static class HubBigSellerUpsert
{
    private static readonly object _lock = new();
    private static Timer? _timer;

    /// <summary>Hẹn 1 lượt upsert (debounce 2s). No-op khi chưa nối Hub (chạy độc lập như cũ).</summary>
    public static void Schedule()
    {
        if (!CoordinationRuntime.Active) return;
        lock (_lock)
        {
            _timer ??= new Timer(_ => _ = FlushAsync());
            _timer.Change(2000, Timeout.Infinite);
        }
    }

    private static async Task FlushAsync()
    {
        var client = CoordinationRuntime.Client;
        if (client is null) return;
        try
        {
            // Gửi BẢN ĐẦY ĐỦ store local (hub khớp theo Id/email → idempotent cho acc đã có, thêm acc/shop mới).
            await client.PostBigSellerUpsertAsync(BigSellerStore.Shared.Accounts.ToList());
        }
        catch (Exception ex)
        {
            HttpCoordinationHub.DiagLog?.Invoke("⚠ đẩy acc BigSeller lên Hub thất bại: " + ex.Message);
        }
    }
}
