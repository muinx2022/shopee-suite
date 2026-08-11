using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.ViewModels;

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
/// Hai card (Tự động hóa / Đồng bộ Google Sheet) có ô thông báo RIÊNG
/// (<see cref="SavedMessage"/> / <see cref="GsheetSavedMessage"/>): lưu card nào thì chỉ hiện thông báo ở card
/// đó, clear card kia.
/// <para>Card "Trình duyệt" (ComboBox Auto/Chrome/Edge/Brave/Chromium đóng gói) đã BỎ 11/08/2026: app chỉ chạy
/// được bằng Brave nên không còn gì để chọn — xem <see cref="XuLyDonShopee.Core.Services.BrowserLocator"/>.</para>
/// <para>
/// Webhook thông báo KHÔNG còn cấu hình ở client: đó là cấu hình HUB-OWNED (đặt trên trang Cài đặt của Hub
/// web, Hub gửi tin). Backend giữ nguyên — <c>SettingsRepository</c> vẫn đọc/ghi được và
/// <c>OrderNotifyService</c> vẫn dùng giá trị đã lưu từ trước cho máy chạy độc lập.
/// </para>
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

    /// <summary>Tên tab (sheet) đích để ghi đơn — OVERRIDE: có giá trị = ghi cố định tab đó; để trống = TỰ ĐỘNG
    /// theo tháng ("Tháng MM-yyyy"). Trống thì form GIỮ trống (không tự điền "tháng 4" như trước).</summary>
    [ObservableProperty]
    private string _gsheetTabName = string.Empty;

    /// <summary>Link (hoặc ID) bảng tính Google Sheet THỨ HAI — file phụ nhận thêm cột A–E; để trống = không ghi
    /// file phụ. KHÁC <see cref="GsheetWebAppUrl"/>: đây là link docs.google.com/spreadsheets/…, không phải /exec.</summary>
    [ObservableProperty]
    private string _gsheetSheet2 = string.Empty;

    /// <summary>Thông báo sau khi lưu của card TỰ ĐỘNG HÓA (thư mục hóa đơn / chu kỳ) (null = ẩn).</summary>
    [ObservableProperty]
    private string? _savedMessage;

    /// <summary>Thông báo sau khi lưu của card ĐỒNG BỘ GOOGLE SHEET (link/tab) (null = ẩn) — RIÊNG để không
    /// hiện lẫn ở card kia.</summary>
    [ObservableProperty]
    private string? _gsheetSavedMessage;

    /// <summary>MainViewModel gọi khi chuyển sang màn này: nạp lại cấu hình từ DB (thư mục hóa đơn + chu kỳ + link/tab GSheet).</summary>
    public void Reload()
    {
        InvoiceFolder = _services.Settings.GetInvoiceFolder();
        OrderIntervalMinutes = _services.Settings.GetOrderIntervalMinutes();
        GsheetWebAppUrl = _services.Settings.GetGsheetWebAppUrl() ?? string.Empty;
        GsheetTabName = _services.Settings.GetGsheetTabName();
        GsheetSheet2 = _services.Settings.GetGsheetSheet2() ?? string.Empty;

        SavedMessage = null;
        GsheetSavedMessage = null;
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
    }

    /// <summary>Nút "Lưu": chuẩn hóa (kẹp [1,1440]) + ghi chu kỳ theo dõi đơn xuống DB, phản ánh lại lên form.</summary>
    [RelayCommand]
    private void SaveInterval()
    {
        _services.Settings.SetOrderIntervalMinutes(OrderIntervalMinutes);
        OrderIntervalMinutes = _services.Settings.GetOrderIntervalMinutes(); // phản ánh bản đã kẹp
        SavedMessage = "Đã lưu chu kỳ theo dõi đơn (áp cho các phiên mở sau khi lưu).";
        GsheetSavedMessage = null; // dọn thông báo các card kia
    }

    /// <summary>
    /// Nút "Lưu" cấu hình Google Sheet: validate URL qua <see cref="GsheetConfigSync.KiemTraUrl"/> — cho phép
    /// TRỐNG (tắt đồng bộ) hoặc URL bắt đầu bằng <c>https://script.google.com/</c> (URL Web App Apps Script);
    /// ô "Google Sheet 2" validate RIÊNG qua <see cref="GsheetConfigSync.KiemTraSheet2"/> (link bảng tính / ID —
    /// KHÔNG dùng chung validator Web App, kẻo báo lỗi oan). Sai dạng → báo lỗi qua
    /// <see cref="GsheetSavedMessage"/>, KHÔNG lưu. Hợp lệ → lưu CẢ BA field (tab/sheet2 để trống → lưu trống,
    /// nghĩa là "tự động theo tháng" / "không ghi file phụ"). Thông báo hiện ở RIÊNG card GSheet (dọn
    /// <see cref="SavedMessage"/> của card kia). Lưu xong phản ánh lại giá trị đã chuẩn hóa lên form.
    /// <para>
    /// LƯU XONG còn ĐẨY cấu hình lên Hub (hook <see cref="AppServices.PushGsheetConfigToHub"/>) để các máy khác
    /// nhận về — chỉ khi URL KHÁC TRỐNG (máy chưa cấu hình không được xoá cấu hình của cả fleet). Gọi bất đồng bộ,
    /// KHÔNG chặn UI; hook null (app chạy độc lập / hub chưa cấu hình) → y hành vi cũ, chỉ lưu local.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task SaveGsheetUrlAsync()
    {
        var url = GsheetWebAppUrl?.Trim() ?? string.Empty;
        var loi = GsheetConfigSync.KiemTraUrl(url) ?? GsheetConfigSync.KiemTraSheet2(GsheetSheet2);
        if (loi is not null)
        {
            GsheetSavedMessage = loi;
            SavedMessage = null; // dọn thông báo các card kia
            return;
        }

        _services.Settings.SetGsheetWebAppUrl(url);
        _services.Settings.SetGsheetTabName(GsheetTabName);          // trống → Get trả "" = tự động theo tháng
        _services.Settings.SetGsheetSheet2(GsheetSheet2);            // trống → Get trả null = không ghi file phụ
        GsheetWebAppUrl = _services.Settings.GetGsheetWebAppUrl() ?? string.Empty; // phản ánh bản đã chuẩn hóa
        GsheetTabName = _services.Settings.GetGsheetTabName();       // phản ánh (trống → GIỮ trống)
        GsheetSheet2 = _services.Settings.GetGsheetSheet2() ?? string.Empty;
        GsheetSavedMessage = "Đã lưu cấu hình Google Sheet.";
        SavedMessage = null; // dọn thông báo các card kia

        // CHỤP giá trị vào biến cục bộ TRƯỚC await (không đọc lại field mutable sau await).
        var tab = GsheetTabName;
        var sheet2 = GsheetSheet2;
        var push = _services.PushGsheetConfigToHub;
        if (push is null)
        {
            return; // app Đơn hàng chạy ĐỘC LẬP / hub chưa cấu hình → y hành vi cũ (chỉ lưu local)
        }
        if (!GsheetConfigSync.NenDayLenHub(url))
        {
            // URL trống → KHÔNG đẩy (bảo vệ cấu hình của cả fleet), và nói rõ là Hub sẽ áp lại bản của nó.
            GsheetSavedMessage = "Đã lưu (URL trống = tắt ghi sheet trên máy này; Hub có cấu hình thì máy sẽ tự nhận lại).";
            return;
        }

        bool ok;
        try { ok = await push(url, tab, sheet2, System.Threading.CancellationToken.None); }
        catch { ok = false; }   // lỗi mạng/hub → chỉ ảnh hưởng thông báo, bản local ĐÃ lưu
        GsheetSavedMessage = ok
            ? "Đã lưu + đồng bộ lên Hub (các máy khác nhận trong ~1 phút)."
            : "Đã lưu (Hub chưa kết nối — sẽ dùng bản của Hub khi có).";
    }

}
