using System.Collections.Generic;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Chia một danh sách thành các lô kích thước tối đa N (giữ nguyên thứ tự). Hàm THUẦN — tách riêng khỏi
/// scheduler để test được không cần session/thời gian.
/// </summary>
public static class AutoRunBatcher
{
    /// <summary>
    /// Chia <paramref name="items"/> thành các lô mỗi lô tối đa <paramref name="batchSize"/> phần tử (lô cuối
    /// có thể ngắn hơn nếu dư). <paramref name="batchSize"/> &lt; 1 được ép về 1 (không cho lô rỗng / vòng vô
    /// hạn). Danh sách rỗng → trả danh sách rỗng. Thứ tự phần tử được giữ nguyên.
    /// </summary>
    public static List<List<T>> Split<T>(IReadOnlyList<T> items, int batchSize)
    {
        if (batchSize < 1)
        {
            batchSize = 1;
        }

        var result = new List<List<T>>();
        if (items is null || items.Count == 0)
        {
            return result;
        }

        for (var i = 0; i < items.Count; i += batchSize)
        {
            var batch = new List<T>();
            for (var j = i; j < i + batchSize && j < items.Count; j++)
            {
                batch.Add(items[j]);
            }

            result.Add(batch);
        }

        return result;
    }
}
