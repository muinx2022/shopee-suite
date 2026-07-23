using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Một dòng hiển thị (ĐỌC-CHỈ) trên bảng màn "Đơn hàng": bọc <see cref="OrderRow"/> + nhãn tài khoản,
/// tính sẵn các chuỗi hiển thị (sản phẩm "(+n)", tổng tiền định dạng ₫, giờ sync giờ địa phương) để
/// DataGrid bind thẳng và để dựng dòng CSV. Không cần INotifyPropertyChanged: mỗi lần lọc, các dòng
/// được TẠO LẠI thay vì sửa tại chỗ. <c>partial</c> để <see cref="PrintSlipCommand"/> được sinh từ
/// <c>[RelayCommand]</c> (không cần kế thừa ObservableObject).
/// </summary>
public sealed partial class OrderRowViewModel
{
    /// <summary>Định dạng số VND: nhóm nghìn bằng dấu chấm (₫1.234.567).</summary>
    private static readonly NumberFormatInfo VndFormat = new() { NumberGroupSeparator = ".", NumberGroupSizes = new[] { 3 } };

    private readonly OrderRow _row;

    /// <summary>Thư mục lưu phiếu (đọc từ Cài đặt qua <c>SettingsRepository.GetInvoiceFolder()</c>) — OrdersViewModel
    /// đọc MỘT LẦN khi nạp danh sách rồi truyền vào mỗi dòng để <see cref="SlipPath"/> khớp nơi TẢI phiếu.</summary>
    private readonly string _invoiceDir;

    /// <summary>Callback báo trạng thái ra màn (OrdersViewModel gán vào StatusMessage). Null → im lặng.</summary>
    private readonly Action<string>? _notify;

    /// <summary>Callback tải lại phiếu cho dòng này (OrdersViewModel nối tới phiên của tài khoản). Null → nút
    /// "Tải phiếu" không làm gì (dòng dựng cho CSV/in hàng loạt không cần).</summary>
    private readonly Func<OrderRowViewModel, Task>? _redownloadSlip;

    public OrderRowViewModel(
        OrderRow row, string accountLabel, string invoiceDir,
        Action<string>? notify = null, Func<OrderRowViewModel, Task>? redownloadSlip = null)
    {
        _row = row;
        AccountLabel = accountLabel;
        _invoiceDir = invoiceDir;
        _notify = notify;
        _redownloadSlip = redownloadSlip;
    }

    /// <summary>Nhãn tài khoản (email) — do ViewModel map từ account_id.</summary>
    public string AccountLabel { get; }

    /// <summary>Id tài khoản sở hữu đơn — để hộp thoại đổi trạng thái khóa đúng <c>(account_id, order_sn)</c>.</summary>
    public long AccountId => _row.AccountId;

    public string OrderSn => _row.OrderSn;
    public string Buyer => _row.BuyerUsername ?? string.Empty;

    /// <summary>"Tên SP đầu (+n)" với n = số sản phẩm còn lại khi đơn có &gt;1 sản phẩm.</summary>
    public string Product => BuildProduct(_row.ItemSummary, _row.ItemCount);

    /// <summary>SKU sản phẩm (chuỗi alphanumeric liên tục cuối cùng của tên sản phẩm, vd "B02435").</summary>
    public string Sku => _row.Sku ?? string.Empty;

    /// <summary>Tổng tiền: ưu tiên số đã parse (₫1.234.567), thiếu thì dùng nguyên văn.</summary>
    public string Total => BuildTotal(_row.TotalPrice, _row.TotalPriceText);

    /// <summary>Cột "Ước tính" = "Số tiền cuối cùng" từ trang chi tiết: ưu tiên số đã parse (₫...), thiếu thì
    /// nguyên văn, rỗng nếu chưa lấy (đơn "Đã hủy" hoặc chưa mở chi tiết).</summary>
    public string Estimate => BuildTotal(_row.FinalAmount, _row.FinalAmountText);

    public string Payment => _row.PaymentMethod ?? string.Empty;
    public string Status => _row.Status ?? string.Empty;

    /// <summary>
    /// Có hiện nút "In phiếu" cho dòng này không: FALSE khi trạng thái CHỨA "hủy" (đơn "Đã hủy" / "Đã hủy
    /// một phần" chưa qua xử lý nên KHÔNG có file phiếu giao → ẩn nút). Chuẩn hóa (bỏ hoa/thường + gộp khoảng
    /// trắng) rồi so "chứa" — bền với biến thể chữ, giống <c>OrderStatusPillConverter</c>.
    /// </summary>
    public bool CanPrintSlip => !NormalizeStatus(Status).Contains("hủy");

    /// <summary>
    /// Đơn "Chờ lấy hàng" (đã arrange, chờ bưu cục lấy) — dùng để LỌC in hàng loạt phiếu giao ở màn Đơn hàng.
    /// Nhận diện bằng chuẩn hóa CHỨA "chờ lấy hàng" (bền với biến thể chữ/khoảng trắng).
    /// </summary>
    public bool IsPendingPickup => NormalizeStatus(Status).Contains("chờ lấy hàng");

    /// <summary>Cột "Mô tả/Lý do hủy": ưu tiên lý do hủy (nếu có) rồi tới mô tả trạng thái.</summary>
    public string Note => !string.IsNullOrWhiteSpace(_row.CancelReason)
        ? _row.CancelReason!
        : (_row.StatusDescription ?? string.Empty);

    public string Carrier => _row.Carrier ?? string.Empty;
    public string Tracking => _row.TrackingNumber ?? string.Empty;

    /// <summary>Giờ sync theo giờ địa phương, định dạng dd/MM/yyyy HH:mm (rỗng nếu chưa có).</summary>
    public string SyncedAtDisplay => _row.SyncedAt == default
        ? string.Empty
        : _row.SyncedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Chuyển sang dòng xuất CSV (đúng thứ tự cột như bảng — "Ước tính" ngay sau "Tổng tiền").</summary>
    public OrderExportRow ToExportRow() => new(
        AccountLabel, OrderSn, Buyer, Product, Total, Estimate, Payment, Status, Note, Carrier, Tracking, SyncedAtDisplay);

    /// <summary>
    /// Đường dẫn file PDF phiếu giao đã tải lúc xử lý đơn: <c>{thư mục hóa đơn cấu hình}\{sanitize(order_sn)}.pdf</c>.
    /// KHỚP TUYỆT ĐỐI cách <c>SaveSlipAsync</c> đặt tên: CÙNG thư mục (đọc từ Cài đặt qua
    /// <c>SettingsRepository.GetInvoiceFolder()</c>, truyền vào <c>_invoiceDir</c> — cùng nguồn với nơi xử lý đơn
    /// LƯU phiếu) + cùng <see cref="ShopeeShippingNav.SanitizeFileName"/>. Chỉ SUY RA đường dẫn — KHÔNG kiểm tồn
    /// tại lúc render (tránh IO mỗi dòng); chỉ kiểm khi bấm in trong <see cref="PrintSlip"/>.
    /// </summary>
    public string SlipPath => Path.Combine(
        _invoiceDir, ShopeeShippingNav.SanitizeFileName(OrderSn) + ".pdf");

    /// <summary>
    /// Link "In phiếu": GỬI THẲNG file PDF phiếu đã tải tới MÁY IN MẶC ĐỊNH của Windows qua
    /// <see cref="PdfPrinter.TryPrint"/> (ShellExecute verb <c>"print"</c> — KHÔNG chạy binary tự build nên WDAC
    /// không chặn), KHÔNG mở file để người dùng tự bấm in nữa. Kiểm <see cref="File.Exists"/> ngay lúc bấm:
    /// thiếu file → báo nhẹ (đơn chưa xử lý / phiếu chưa tải), KHÔNG lỗi. In lỗi (máy không có app PDF mặc định
    /// hỗ trợ lệnh in...) → <c>TryPrint</c> trả false → báo qua callback, KHÔNG ném.
    /// </summary>
    [RelayCommand]
    private void PrintSlip()
    {
        if (!File.Exists(SlipPath))
        {
            _notify?.Invoke($"Chưa có file phiếu đã tải cho đơn {OrderSn} (đơn chưa xử lý hoặc phiếu chưa tải).");
            return;
        }

        if (PdfPrinter.TryPrint(SlipPath))
        {
            _notify?.Invoke($"Đã gửi in phiếu đơn {OrderSn} tới máy in mặc định.");
        }
        else
        {
            _notify?.Invoke($"Không in được phiếu {SlipPath} (máy chưa có app PDF mặc định hỗ trợ lệnh in?).");
        }
    }

    /// <summary>
    /// Đã có file PDF phiếu HỢP LỆ (tồn tại + magic <c>%PDF-</c>) chưa — tính lúc nạp dòng (đọc 5 byte đầu qua
    /// <see cref="AccountSession.SlipFileIsValidPdf"/>). Dùng để hiện/ẩn nút "Tải phiếu": thiếu file → hiện.
    /// </summary>
    public bool HasSlipFile => AccountSession.SlipFileIsValidPdf(SlipPath);

    /// <summary>
    /// Hiện nút "Tải phiếu" khi đơn ĐÃ có mã vận đơn (arrange xong, phiếu đáng lẽ phải có) NHƯNG file phiếu
    /// đang thiếu/không hợp lệ (<see cref="HasSlipFile"/> = false). Đơn chưa xử lý (chưa có vận đơn) → không hiện.
    /// </summary>
    public bool ShowRedownloadSlip => !string.IsNullOrWhiteSpace(Tracking) && !HasSlipFile;

    /// <summary>
    /// Nút "Tải phiếu": nhờ OrdersViewModel (đã nối tới phiên của tài khoản) tải lại file phiếu cho đơn này.
    /// Phiên không chạy / bận → OrdersViewModel báo hướng dẫn qua StatusMessage (xem callback). Chạy NỀN (async
    /// command) — không treo UI. Callback null (dòng CSV/in hàng loạt) → báo nhẹ, không làm gì.
    /// </summary>
    [RelayCommand]
    private async Task RedownloadSlipAsync()
    {
        if (_redownloadSlip is null)
        {
            _notify?.Invoke("Chức năng tải phiếu chưa sẵn sàng ở ngữ cảnh này.");
            return;
        }

        await _redownloadSlip(this);
    }

    /// <summary>Chuẩn hóa trạng thái để so khớp từ khóa: bỏ hoa/thường + gộp khoảng trắng thừa (giống
    /// <c>OrderStatusPillConverter.Classify</c>) → so "chứa" bền với biến thể chữ/khoảng trắng.</summary>
    private static string NormalizeStatus(string? status)
        => string.Join(' ', (status ?? string.Empty).ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Dựng chuỗi cột "Sản phẩm": tên SP đầu, thêm "(+n)" (n = số SP còn lại) khi đơn nhiều SP.</summary>
    public static string BuildProduct(string? summary, int itemCount)
    {
        var name = summary ?? string.Empty;
        if (itemCount > 1)
        {
            return string.IsNullOrEmpty(name) ? $"(+{itemCount - 1})" : $"{name} (+{itemCount - 1})";
        }
        return name;
    }

    /// <summary>Dựng chuỗi cột "Tổng tiền": ₫ + số có phân nhóm nghìn; thiếu số thì dùng nguyên văn.</summary>
    public static string BuildTotal(long? totalPrice, string? totalPriceText)
        => totalPrice is not null
            ? "₫" + totalPrice.Value.ToString("#,##0", VndFormat)
            : (totalPriceText ?? string.Empty);
}
