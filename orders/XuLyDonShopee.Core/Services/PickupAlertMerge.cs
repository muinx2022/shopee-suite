namespace XuLyDonShopee.Core.Services;

/// <summary>Một dòng banner lỗi địa chỉ kéo từ Hub (kể cả đã dismiss) — dùng merge local.</summary>
public readonly record struct PickupAlertHubDong(
    string ShopLogin,
    string Province,
    bool Dismissed,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? DismissedAt);

/// <summary>Quyết định sau khi so một dòng Hub với trạng thái dismiss local của cùng shop.</summary>
public enum MergePickupAlertAction
{
    /// <summary>Hub (hoặc máy khác) đã đóng → dismiss local.</summary>
    LocalDismiss,

    /// <summary>Hub còn active và mới hơn dismiss local (hoặc local chưa dismiss) → upsert local.</summary>
    LocalUpsert,

    /// <summary>Local đã dismiss mới hơn (hoặc bằng) Hub active → giữ dismiss, đẩy lại tombstone lên Hub.</summary>
    KeepLocalDismissRepushHub,
}

/// <summary>Hàm thuần merge banner lỗi địa chỉ Hub ↔ local (test được, không đụng DB/UI).</summary>
public static class PickupAlertMerge
{
    /// <summary>
    /// So một dòng Hub với dòng local cùng shop:
    /// <list type="bullet">
    /// <item>Hub dismissed → <see cref="MergePickupAlertAction.LocalDismiss"/>. Bấm X LUÔN thắng, không so mốc.</item>
    /// <item>Hub active mới hơn dismiss local → <see cref="MergePickupAlertAction.LocalUpsert"/>.</item>
    /// <item>Local dismiss mới hơn/bằng Hub active (hoặc Hub thiếu mốc) → <see cref="MergePickupAlertAction.KeepLocalDismissRepushHub"/>.</item>
    /// </list>
    /// <para>CỐ Ý để "dismiss thắng" vô điều kiện: đồng hồ các máy độc lập nhau, hễ đem mốc của máy này so mốc
    /// của máy kia là có ca banner KẸT không gỡ được (máy lệch giờ giữ banner + đẩy lại upsert → hồi sinh trên
    /// máy vừa bấm X → lặp vô hạn). Đổi lại, tombstone cũ có thể xoá nhầm banner của lỗi vừa phát hiện mà Hub
    /// chưa kịp biết — ca này TỰ LÀNH: vòng shop kế (3–5 phút) phát hiện lại và upsert với mốc mới hơn
    /// <c>dismissed_at</c> nên Hub bỏ tombstone, banner hiện lại ở mọi máy.</para>
    /// </summary>
    /// <param name="localDismissedAt">Mốc bấm X ở local; null = local đang hiện banner (hoặc chưa có dòng).</param>
    public static MergePickupAlertAction QuyetDinh(
        DateTime? localDismissedAt,
        bool hubDismissed,
        DateTimeOffset? hubCreatedAt)
    {
        if (hubDismissed)
        {
            return MergePickupAlertAction.LocalDismiss;
        }

        if (localDismissedAt is null)
        {
            return MergePickupAlertAction.LocalUpsert;
        }

        // Local đã đóng: thiếu mốc Hub → không dựng lại (an toàn hơn là resurrect).
        if (hubCreatedAt is null)
        {
            return MergePickupAlertAction.KeepLocalDismissRepushHub;
        }

        return ToUtc(localDismissedAt)!.Value >= hubCreatedAt.Value.UtcDateTime
            ? MergePickupAlertAction.KeepLocalDismissRepushHub
            : MergePickupAlertAction.LocalUpsert;
    }

    /// <summary>Mốc local về UTC — <see cref="DateTimeKind.Unspecified"/> (DB đời cũ) coi như đã là UTC.</summary>
    private static DateTime? ToUtc(DateTime? moc) => moc?.Kind switch
    {
        null => null,
        DateTimeKind.Utc => moc,
        DateTimeKind.Local => moc.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(moc!.Value, DateTimeKind.Utc),
    };
}
