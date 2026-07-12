using System.Threading;
using Shopee.Core.BigSeller;

namespace Shopee.Suite.Modules.BigSeller;

/// <summary>
/// Gộp nhiều lần lưu liên tiếp (gõ từng phím trong ô cấu hình BigSeller) thành 1 lần ghi đĩa ~500ms sau khi
/// ngừng sửa. Thay <c>UpdateSourceTrigger=LostFocus</c> của WPF — Avalonia bind Text cập nhật MỖI PHÍM, nếu gọi
/// <see cref="BigSellerStore"/>.Save() thẳng sẽ ghi file JSON mỗi phím. Model đã cập nhật ngay (chỉ hoãn GHI ĐĨA),
/// nên chuyển module / bấm Lưu tay / auto-login vẫn đọc đúng dữ liệu mới nhất.
/// </summary>
internal static class PersistDebounce
{
    private static readonly object _lock = new();
    private static Timer? _timer;

    public static void Schedule()
    {
        lock (_lock)
        {
            _timer ??= new Timer(_ => { try { BigSellerStore.Shared.Save(); } catch { } });
            _timer.Change(500, Timeout.Infinite);
        }
        // Mọi lần sửa field acc/shop (gõ phím) cũng hẹn đẩy bản mới LÊN Hub (debounce riêng 2s) — client giờ là
        // nguồn phát sinh acc/shop; hub gộp KHÔNG xoá + không đổi thật thì không bump version → chạy thừa vô hại.
        HubBigSellerUpsert.Schedule();
    }
}
