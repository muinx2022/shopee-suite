using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Suite.Infrastructure;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.Workspace;

/// <summary>
/// Một ô trên dải chip LỌC LOG của tab "Theo dõi Scrape" / "Theo dõi Update": chip "Tất cả" (buffer GỘP của
/// module) hoặc chip của MỘT tài khoản BigSeller (buffer riêng lấy từ <see cref="AccountLogRegistry"/> —
/// hạ tầng đã ghi sẵn từ trước, đợt này mới bind ra UI).
///
/// <para>Chỉ là lớp HIỂN THỊ: KHÔNG tạo buffer, KHÔNG đổi cách ghi log. Buffer sống ở registry của module VM
/// nên bền qua rebuild danh sách tài khoản.</para>
/// </summary>
public sealed partial class LogFilterChipViewModel : ObservableObject, IDisposable
{
    /// <summary>Id tài khoản BigSeller; rỗng = chip "Tất cả".</summary>
    public string AccountId { get; }

    /// <summary>Tên hiển thị trên chip (chưa kèm số dòng).</summary>
    public string Title { get; }

    /// <summary>Buffer log mà ô log hiển thị khi chip này được chọn.</summary>
    public LogBuffer Buffer { get; }

    public bool IsAll => AccountId.Length == 0;

    /// <summary>Nhãn chip: "tên (số dòng đang giữ)". Đếm là số dòng TRONG BUFFER UI (trần 500) — file log
    /// riêng vẫn giữ đủ.</summary>
    public string Label => $"{Title} ({Buffer.Count})";

    public string Tooltip => IsAll
        ? $"Xem log GỘP mọi tài khoản của module này ({Buffer.Count} dòng cuối)."
        : $"Chỉ xem log của tài khoản \"{Title}\" ({Buffer.Count} dòng cuối).";

    public LogFilterChipViewModel(string accountId, string title, LogBuffer buffer)
    {
        AccountId = accountId ?? "";
        Title = string.IsNullOrWhiteSpace(title) ? "(không tên)" : title;
        Buffer = buffer;
        // Số trên chip chạy theo log. Add/Clear của LogBuffer đều đi qua UI thread (mọi đường ghi log dùng
        // ModuleViewModelBase.OnUi) nhưng vẫn Post cho chắc — Post chạy NGAY nếu đã ở UI thread nên không tốn nhịp.
        Buffer.CollectionChanged += OnBufferChanged;
    }

    private void OnBufferChanged(object? sender, NotifyCollectionChangedEventArgs e) => UiThread.Post(() =>
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Tooltip));
    });

    /// <summary>Bỏ lắng nghe khi dải chip bị dựng lại — không thì mỗi lần rebuild lại rò thêm 1 handler trên
    /// buffer (buffer sống suốt vòng đời module VM nên handler cũ sẽ KHÔNG bao giờ được thu hồi).</summary>
    public void Dispose() => Buffer.CollectionChanged -= OnBufferChanged;
}
