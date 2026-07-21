using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>Một mục trong ComboBox chọn trình duyệt: giá trị enum + nhãn tiếng Việt hiển thị.</summary>
public sealed record BrowserChoiceOption(BrowserChoice Value, string Label);

/// <summary>
/// Màn "Cài đặt" (HOẠT ĐỘNG THẬT): các mục lưu xuống DB qua <see cref="SettingsRepository"/>:
/// <list type="bullet">
/// <item><b>Thư mục lưu hóa đơn</b> — chọn qua folder picker; là NGUỒN DUY NHẤT cho nơi xử lý đơn LƯU phiếu
/// và link "In phiếu" ở màn Đơn hàng (mặc định <c>{thư mục app.db}\Phieu-giao-hang</c>).</item>
/// <item><b>Chu kỳ theo dõi đơn (phút)</b> — số phút giữa các lần tự đọc "Chờ Lấy Hàng"; áp cho các phiên
/// mở SAU khi lưu.</item>
/// <item><b>Đồng bộ Google Sheet</b> — link Web App Apps Script + tên tab đích; sau mỗi lần Sync app tự ghi
/// đơn + link phiếu vào Google Sheet.</item>
/// </list>
/// Ba card (Tự động hóa / Đồng bộ Google Sheet / Thông báo đơn mới) có ô thông báo RIÊNG
/// (<see cref="SavedMessage"/> / <see cref="GsheetSavedMessage"/> / <see cref="NotifySavedMessage"/>): lưu
/// card nào thì chỉ hiện thông báo ở card đó, clear hai card kia. Quản lý API key KiotProxy đã chuyển sang
/// màn Proxy (<see cref="ProxiesViewModel"/>).
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        Reload();
    }

    /// <summary>Đường dẫn thư mục lưu hóa đơn đang dùng (config đã chọn hoặc mặc định cạnh app.db).</summary>
    [ObservableProperty]
    private string _invoiceFolder = string.Empty;

    /// <summary>Chu kỳ theo dõi đơn (phút) — số phút giữa các lần tự đọc số "Chờ Lấy Hàng".</summary>
    [ObservableProperty]
    private int _orderIntervalMinutes = AppGeneralSettings.DefaultOrderIntervalMinutes;

    /// <summary>URL Web App Google Sheet (Apps Script <c>/exec</c>) — để trống = tắt đồng bộ.</summary>
    [ObservableProperty]
    private string _gsheetWebAppUrl = string.Empty;

    /// <summary>Tên tab (sheet) đích để ghi đơn (để trống → mặc định "tháng 4").</summary>
    [ObservableProperty]
    private string _gsheetTabName = string.Empty;

    /// <summary>URL webhook báo "đơn mới" (Slack / Discord / Telegram) — để trống = tắt thông báo.</summary>
    [ObservableProperty]
    private string _notifyWebhookUrl = string.Empty;

    /// <summary>Thông báo sau khi lưu của card TỰ ĐỘNG HÓA (thư mục hóa đơn / chu kỳ) (null = ẩn).</summary>
    [ObservableProperty]
    private string? _savedMessage;

    /// <summary>Thông báo sau khi lưu của card ĐỒNG BỘ GOOGLE SHEET (link/tab) (null = ẩn) — RIÊNG để không
    /// hiện lẫn ở card kia.</summary>
    [ObservableProperty]
    private string? _gsheetSavedMessage;

    /// <summary>Thông báo sau khi lưu của card THÔNG BÁO ĐƠN MỚI (webhook) (null = ẩn) — RIÊNG để không hiện
    /// lẫn ở card kia.</summary>
    [ObservableProperty]
    private string? _notifySavedMessage;

    /// <summary>Các lựa chọn trình duyệt (Tự động · Chrome · Edge · Brave · Chromium đóng gói) cho ComboBox.</summary>
    public ObservableCollection<BrowserChoiceOption> BrowserOptions { get; } =
        new(BrowserChoices.All.Select(c => new BrowserChoiceOption(c, BrowserChoices.VnLabel(c))));

    /// <summary>Lựa chọn trình duyệt đang chọn ở ComboBox (đổi → cập nhật <see cref="DetectedBrowserText"/>).</summary>
    [ObservableProperty]
    private BrowserChoiceOption? _selectedBrowser;

    /// <summary>Mô tả trình duyệt THỰC sẽ dùng cho lựa chọn hiện tại (Chrome/Edge/Brave + path, hoặc Chromium đóng gói).</summary>
    [ObservableProperty]
    private string _detectedBrowserText = string.Empty;

    /// <summary>Thông báo sau khi lưu của card TRÌNH DUYỆT (null = ẩn) — RIÊNG để không hiện lẫn ở card kia.</summary>
    [ObservableProperty]
    private string? _browserSavedMessage;

    /// <summary>MainViewModel gọi khi chuyển sang màn này: nạp lại cấu hình từ DB (thư mục hóa đơn + chu kỳ + link/tab GSheet + trình duyệt).</summary>
    public void Reload()
    {
        InvoiceFolder = _services.Settings.GetInvoiceFolder();
        OrderIntervalMinutes = _services.Settings.GetOrderIntervalMinutes();
        GsheetWebAppUrl = _services.Settings.GetGsheetWebAppUrl() ?? string.Empty;
        GsheetTabName = _services.Settings.GetGsheetTabName();
        NotifyWebhookUrl = _services.Settings.GetNotifyWebhookUrl() ?? string.Empty;

        var choice = _services.Settings.GetBrowserChoice();
        SelectedBrowser = BrowserOptions.FirstOrDefault(o => o.Value == choice) ?? BrowserOptions[0];
        DetectedBrowserText = ShopeeLoginService.DescribeBrowser(choice); // set tường minh (đề phòng SelectedBrowser không đổi → handler không chạy)
        BrowserSavedMessage = null;

        SavedMessage = null;
        GsheetSavedMessage = null;
        NotifySavedMessage = null;
    }

    /// <summary>Đổi lựa chọn ở ComboBox → cập nhật ngay dòng "Đang dùng" (trình duyệt THỰC sẽ dùng).</summary>
    partial void OnSelectedBrowserChanged(BrowserChoiceOption? value)
    {
        DetectedBrowserText = ShopeeLoginService.DescribeBrowser(value?.Value ?? BrowserChoice.Auto);
    }

    /// <summary>
    /// Nút "Chọn…": mở folder picker (thư mục hiện tại làm điểm mở đầu). Chọn xong → LƯU config + cập nhật
    /// hiển thị theo giá trị đã lưu. Hủy → không đổi gì. KHÔNG đọc lại field mutable sau await (chỉ dùng biến
    /// cục bộ + đọc lại từ repo).
    /// </summary>
    [RelayCommand]
    private async Task ChooseInvoiceFolderAsync()
    {
        var picked = await DialogService.PickInvoiceFolderAsync(InvoiceFolder);
        if (string.IsNullOrWhiteSpace(picked))
        {
            return; // người dùng bấm Hủy
        }

        _services.Settings.SetInvoiceFolder(picked);
        InvoiceFolder = _services.Settings.GetInvoiceFolder(); // phản ánh giá trị đã lưu (đã chuẩn hóa)
        SavedMessage = "Đã lưu thư mục lưu hóa đơn.";
        GsheetSavedMessage = null; // dọn thông báo các card kia
        NotifySavedMessage = null;
    }

    /// <summary>Nút "Lưu": chuẩn hóa (kẹp [1,1440]) + ghi chu kỳ theo dõi đơn xuống DB, phản ánh lại lên form.</summary>
    [RelayCommand]
    private void SaveInterval()
    {
        _services.Settings.SetOrderIntervalMinutes(OrderIntervalMinutes);
        OrderIntervalMinutes = _services.Settings.GetOrderIntervalMinutes(); // phản ánh bản đã kẹp
        SavedMessage = "Đã lưu chu kỳ theo dõi đơn (áp cho các phiên mở sau khi lưu).";
        GsheetSavedMessage = null; // dọn thông báo các card kia
        NotifySavedMessage = null;
    }

    /// <summary>
    /// Nút "Lưu" cấu hình Google Sheet: validate URL — cho phép TRỐNG (tắt đồng bộ) hoặc URL bắt đầu bằng
    /// <c>https://script.google.com/</c> (URL Web App Apps Script). URL khác dạng → báo lỗi qua
    /// <see cref="GsheetSavedMessage"/>, KHÔNG lưu. Hợp lệ → lưu CẢ URL lẫn tên tab đích (tab để trống → lưu
    /// trống, Get sẽ trả mặc định "tháng 4"). Thông báo hiện ở RIÊNG card GSheet (dọn <see cref="SavedMessage"/>
    /// của card kia). Lưu xong phản ánh lại giá trị đã chuẩn hóa lên form.
    /// </summary>
    [RelayCommand]
    private void SaveGsheetUrl()
    {
        var url = GsheetWebAppUrl?.Trim() ?? string.Empty;
        if (url.Length > 0 &&
            !url.StartsWith("https://script.google.com/", System.StringComparison.OrdinalIgnoreCase))
        {
            GsheetSavedMessage = "Link không hợp lệ — phải bắt đầu bằng https://script.google.com/";
            SavedMessage = null; // dọn thông báo các card kia
            NotifySavedMessage = null;
            return;
        }

        _services.Settings.SetGsheetWebAppUrl(url);
        _services.Settings.SetGsheetTabName(GsheetTabName);          // trống → Get trả mặc định "tháng 4"
        GsheetWebAppUrl = _services.Settings.GetGsheetWebAppUrl() ?? string.Empty; // phản ánh bản đã chuẩn hóa
        GsheetTabName = _services.Settings.GetGsheetTabName();       // phản ánh (trống → "tháng 4")
        GsheetSavedMessage = "Đã lưu cấu hình Google Sheet.";
        SavedMessage = null; // dọn thông báo các card kia
        NotifySavedMessage = null;
    }

    /// <summary>
    /// Nút "Lưu" cấu hình THÔNG BÁO ĐƠN MỚI: cho phép TRỐNG (tắt thông báo). Khác trống thì
    /// <see cref="OrderNotifyService.KiemTraUrl"/> phải trả <c>null</c> (hợp lệ) — trả về THÔNG ĐIỆP LỖI cụ
    /// thể (URL lạ / Telegram sai dạng sendMessage / thiếu chat_id) thì hiện đúng message đó, KHÔNG lưu. Hợp
    /// lệ → lưu URL, thông báo kèm tên kênh. Thông báo hiện ở RIÊNG card này (dọn hai card kia). Lưu xong phản
    /// ánh lại bản đã chuẩn hóa.
    /// </summary>
    [RelayCommand]
    private void SaveNotifyUrl()
    {
        var url = NotifyWebhookUrl?.Trim() ?? string.Empty;
        var loi = OrderNotifyService.KiemTraUrl(url);
        if (loi is not null)
        {
            NotifySavedMessage = loi;
            SavedMessage = null; // dọn thông báo các card kia
            GsheetSavedMessage = null;
            return;
        }

        _services.Settings.SetNotifyWebhookUrl(url);
        NotifyWebhookUrl = _services.Settings.GetNotifyWebhookUrl() ?? string.Empty; // phản ánh bản đã chuẩn hóa
        NotifySavedMessage = url.Length == 0
            ? "Đã tắt thông báo đơn mới (URL trống)."
            : $"Đã lưu cấu hình thông báo ({OrderNotifyService.NhanDienKenh(url)}).";
        SavedMessage = null; // dọn thông báo các card kia
        GsheetSavedMessage = null;
    }

    /// <summary>
    /// Nút "Lưu" của card TRÌNH DUYỆT: lưu lựa chọn xuống DB (thiếu → Auto), thông báo RIÊNG ở card này (dọn
    /// ba card kia), cập nhật lại dòng "Đang dùng". Áp cho các phiên MỞ SAU khi lưu (phiên đang chạy giữ nguyên).
    /// </summary>
    [RelayCommand]
    private void SaveBrowser()
    {
        var choice = SelectedBrowser?.Value ?? BrowserChoice.Auto;
        _services.Settings.SetBrowserChoice(choice);
        DetectedBrowserText = ShopeeLoginService.DescribeBrowser(choice);
        BrowserSavedMessage = "Đã lưu trình duyệt (áp cho các phiên mở sau khi lưu).";
        SavedMessage = null; // dọn thông báo các card kia
        GsheetSavedMessage = null;
        NotifySavedMessage = null;
    }
}
