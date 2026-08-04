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
    /// Hub dismissed → luôn dismiss local. Hub active + local dismiss mới hơn/bằng <paramref name="hubCreatedAt"/>
    /// (hoặc Hub thiếu mốc tạo) → giữ local + re-push dismiss. Còn lại → upsert local.
    /// </summary>
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

        var localUtc = localDismissedAt.Value.Kind switch
        {
            DateTimeKind.Utc => localDismissedAt.Value,
            DateTimeKind.Local => localDismissedAt.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(localDismissedAt.Value, DateTimeKind.Utc),
        };

        return localUtc >= hubCreatedAt.Value.UtcDateTime
            ? MergePickupAlertAction.KeepLocalDismissRepushHub
            : MergePickupAlertAction.LocalUpsert;
    }
}
