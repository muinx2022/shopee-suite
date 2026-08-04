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

    /// <summary>
    /// Local đang HIỆN banner với mốc lỗi mới hơn tombstone Hub → giữ banner, đẩy lại upsert lên Hub.
    /// Ca điển hình: lỗi địa chỉ mới phát hiện lúc Hub chết (upsert Hub fail) trong khi Hub còn tombstone cũ —
    /// nếu nghe theo Hub thì banner của lỗi CHƯA sửa bị xoá mất.
    /// </summary>
    KeepLocalActiveRepushHub,
}

/// <summary>Hàm thuần merge banner lỗi địa chỉ Hub ↔ local (test được, không đụng DB/UI).</summary>
public static class PickupAlertMerge
{
    /// <summary>
    /// So một dòng Hub với dòng local cùng shop. Bên nào mang mốc thời gian MỚI HƠN thì thắng, hai chiều:
    /// <list type="bullet">
    /// <item>Hub dismiss mới hơn mốc lỗi local → <see cref="MergePickupAlertAction.LocalDismiss"/> (lan dismiss đa máy).</item>
    /// <item>Local đang hiện banner với mốc lỗi mới hơn dismiss Hub → <see cref="MergePickupAlertAction.KeepLocalActiveRepushHub"/>.</item>
    /// <item>Hub active mới hơn dismiss local → <see cref="MergePickupAlertAction.LocalUpsert"/>.</item>
    /// <item>Local dismiss mới hơn/bằng Hub active (hoặc Hub thiếu mốc) → <see cref="MergePickupAlertAction.KeepLocalDismissRepushHub"/>.</item>
    /// </list>
    /// Thiếu mốc bên Hub → giữ nguyên trạng local (không dựng lại, cũng không xoá bừa).
    /// </summary>
    /// <param name="localCreatedAt">Mốc lỗi của dòng local; null = local chưa có dòng nào cho shop này.</param>
    /// <param name="localDismissedAt">Mốc bấm X ở local; null = local đang hiện banner.</param>
    public static MergePickupAlertAction QuyetDinh(
        DateTime? localCreatedAt,
        DateTime? localDismissedAt,
        bool hubDismissed,
        DateTimeOffset? hubCreatedAt,
        DateTimeOffset? hubDismissedAt)
    {
        if (hubDismissed)
        {
            // Local đang HIỆN banner: chỉ nghe Hub khi tombstone Hub mới hơn mốc lỗi local. Ngược lại là lỗi
            // vừa phát hiện mà Hub chưa hay (upsert fail) → giữ banner + sửa lại Hub.
            if (localDismissedAt is null
                && ToUtc(localCreatedAt) is DateTime localMoc
                && hubDismissedAt is DateTimeOffset hubDong
                && localMoc > hubDong.UtcDateTime)
            {
                return MergePickupAlertAction.KeepLocalActiveRepushHub;
            }

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
