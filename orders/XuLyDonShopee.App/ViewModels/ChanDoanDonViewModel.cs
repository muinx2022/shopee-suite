using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Một dòng của màn chẩn đoán "đơn kết thúc chưa dọn được": đơn nào, của tài khoản/shop nào, trạng thái gì, và
/// CÒN NỢ những nghĩa vụ nào (<see cref="OrderPersistPipeline.ChanDoanDonKetThuc"/>).
/// </summary>
public sealed class DonKetRow
{
    public long AccountId { get; init; }
    public string TenTaiKhoan { get; init; } = "";
    public string OrderSn { get; init; } = "";
    public string ShopLabel { get; init; } = "";
    public string Status { get; init; } = "";

    /// <summary>Các nghĩa vụ còn thiếu, đã ghép thành một chuỗi đọc được (vd "chưa ghi Google Sheet · chưa đẩy lên Hub").</summary>
    public string ThieuText { get; init; } = "";
}

/// <summary>
/// Màn CHẨN ĐOÁN "đơn kết thúc chưa dọn được" (H2.5) — mở từ badge "⏳ Chờ đẩy" trên thanh trạng thái. Trước đây
/// chỗ này chỉ có MỘT dòng log đếm tổng ("N đơn kết thúc chờ lượt sau"), không nói được đơn nào kẹt vì cái gì.
/// <para>
/// Lý do kẹt suy từ ĐÚNG bộ điều kiện của luật dọn (<see cref="OrderPersistPipeline.ChanDoanDonKetThuc"/> — chính
/// là thân của <see cref="OrderPersistPipeline.NenXoaDonKetThuc"/>), và nghĩa vụ Google Sheet hỏi ĐÚNG hàm mà lượt
/// đẩy sheet dùng (<see cref="HubOutbox.ConNghiaVuGhiSheet"/>) — không có luật thứ hai để trôi lệch.
/// </para>
/// <para>
/// VM chạy được HEADLESS (chỉ đụng repository + settings) nên test được không cần dựng cửa sổ.
/// </para>
/// </summary>
public partial class ChanDoanDonViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public ChanDoanDonViewModel(AppServices services)
    {
        _services = services;
        // Quét ĐỒNG BỘ ở ctor: cửa sổ phải mở ra là có dữ liệu ngay (mở xong mới quét thì thấy bảng trống nháy
        // một nhịp). Các lượt SAU (nút Làm mới, sau khi Đẩy lại) đi đường nền — xem ReloadAsync.
        Reload();
    }

    /// <summary>Các đơn kết thúc đang kẹt (mọi tài khoản), đơn cũ trước.</summary>
    public ObservableCollection<DonKetRow> Rows { get; } = new();

    [ObservableProperty]
    private string _summaryText = "";

    /// <summary>Thông báo sau thao tác "Đẩy lại" (null = ẩn).</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Quét lại MỌI tài khoản: lấy đơn ứng viên đẩy sheet (<c>GetForGsheetPush</c> = superset mọi đơn), giữ đơn
    /// KẾT THÚC, rồi chấm nghĩa vụ còn thiếu. Đơn kết thúc mà không thiếu gì thì lượt outbox kế sẽ dọn — không
    /// đưa lên màn (nó không kẹt).
    /// </summary>
    public void Reload()
    {
        Rows.Clear();
        foreach (var r in QuetDonKet())
        {
            Rows.Add(r);
        }

        SummaryText = Rows.Count == 0
            ? "Không có đơn kết thúc nào đang kẹt — mọi đơn đã hoàn tất nghĩa vụ và sẽ được dọn."
            : $"{Rows.Count} đơn kết thúc chưa dọn được (đang chờ hoàn tất nghĩa vụ bên dưới).";
    }

    /// <summary>Phần QUÉT thuần (không đụng UI): duyệt mọi tài khoản × mọi đơn, đọc file phiếu trên đĩa. Tách
    /// khỏi <see cref="Reload"/> để chạy được ngoài UI thread — kho đơn lớn thì vòng này mất vài giây và cửa sổ
    /// sẽ đứng nếu chạy thẳng trên luồng UI.</summary>
    private List<DonKetRow> QuetDonKet()
    {
        var ketQua = new List<DonKetRow>();
        var hubHookActive = _services.PushOrdersToHub is not null;
        // URL Web App trống = người dùng KHÔNG dùng Google Sheet → mọi đơn coi như đã xong nghĩa vụ sheet (cùng
        // quy ước với PushOrdersToGsheetAsync, chứ không phải "chưa ghi").
        var coSheetUrl = !string.IsNullOrWhiteSpace(_services.Settings.GetGsheetWebAppUrl());
        var slipDir = _services.Settings.GetInvoiceFolder();

        foreach (var acc in _services.Accounts.GetAll())
        {
            var tenTk = string.IsNullOrWhiteSpace(acc.Email) ? $"TK {acc.Id}" : acc.Email!;
            foreach (var p in _services.Orders.GetForGsheetPush(acc.Id))
            {
                if (!OrderPersistPipeline.LaDonKetThuc(p))
                {
                    continue; // đơn còn đang chạy → không phải "kẹt"
                }

                var slipPath = Path.Combine(slipDir, ShopeeShippingNav.SanitizeFileName(p.OrderSn) + ".pdf");
                var coPhieuLocal = SlipFiles.SlipFileIsValidPdf(slipPath);
                // coFileBoSung: chỉ đơn CHƯA có link phiếu trên sheet mới cần đính kèm file (y hệt lượt đẩy sheet).
                // Khác biệt duy nhất so với TryReadSlipBase64 ở đó: hàm này không áp trần 5MB — file phiếu >5MB là
                // ca không tồn tại trong thực tế (phiếu ~100–300KB) và lệch cũng chỉ làm màn báo THỪA một nghĩa vụ.
                var coFileBoSung = string.IsNullOrEmpty(p.FileUrl) && coPhieuLocal;
                var gsheetSettled = !coSheetUrl || !HubOutbox.ConNghiaVuGhiSheet(p, coFileBoSung);
                var coPhieuLocalChuaDayHub = hubHookActive && !p.DaDayPhieuHub && coPhieuLocal;

                var thieu = OrderPersistPipeline.ChanDoanDonKetThuc(
                    p, gsheetSettled, hubHookActive, coPhieuLocalChuaDayHub);
                if (thieu.Count == 0)
                {
                    continue; // đủ điều kiện dọn → lượt outbox kế xoá, không phải đơn kẹt
                }

                ketQua.Add(new DonKetRow
                {
                    AccountId = acc.Id,
                    TenTaiKhoan = tenTk,
                    OrderSn = p.OrderSn,
                    ShopLabel = string.IsNullOrWhiteSpace(p.ShopLogin) ? "(shop ?)" : p.ShopLogin!,
                    Status = p.Status ?? "",
                    ThieuText = string.Join(" · ", thieu.Select(OrderPersistPipeline.MoTaNghiaVu)),
                });
            }
        }

        return ketQua;
    }

    /// <summary>Quét NỀN rồi đổ về UI — dùng khi mở màn / bấm Làm mới để cửa sổ không đứng trên kho đơn lớn.</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        SummaryText = "Đang quét…";
        var rows = await Task.Run(QuetDonKet).ConfigureAwait(true);
        Rows.Clear();
        foreach (var r in rows)
        {
            Rows.Add(r);
        }

        SummaryText = Rows.Count == 0
            ? "Không có đơn kết thúc nào đang kẹt — mọi đơn đã hoàn tất nghĩa vụ và sẽ được dọn."
            : $"{Rows.Count} đơn kết thúc chưa dọn được (đang chờ hoàn tất nghĩa vụ bên dưới).";
    }

    /// <summary>
    /// Nút "Đẩy lại" của một dòng: XÁC NHẬN rồi đặt lại bộ cờ đã-đẩy tối thiểu của ĐÚNG đơn đó
    /// (<see cref="Core.Data.OrdersRepository.DatLaiCoDayLai"/>) để lượt outbox sau gửi lại lên Hub + Google Sheet.
    /// Không xác nhận → không ghi gì. Ghi xong phát <see cref="AppServices.RaiseOrdersChanged"/> + quét lại.
    /// </summary>
    [RelayCommand]
    private async Task DayLaiAsync(DonKetRow? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.OrderSn))
        {
            StatusMessage = "Không xác định được đơn để đẩy lại.";
            return;
        }

        var ok = await DialogService.ConfirmAsync(
            "Đẩy lại đơn này?",
            $"Đơn {row.OrderSn} sẽ được xếp lại vào hàng chờ đẩy lên Hub và Google Sheet ở lượt sau "
            + "(dòng sheet cũ được cập nhật tại chỗ, KHÔNG tạo dòng mới; lượt đếm \"Đã bán\" không bị đếm lại).");
        if (!ok)
        {
            return;
        }

        int n;
        try
        {
            n = _services.Orders.DatLaiCoDayLai(row.AccountId, row.OrderSn);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Đẩy lại đơn {row.OrderSn} lỗi: {ex.Message}";
            _services.Log.Append(row.TenTaiKhoan, $"Đẩy lại đơn {row.OrderSn} lỗi: {ex}");
            return;
        }

        if (n == 0)
        {
            StatusMessage = $"Đơn {row.OrderSn} không còn trong app (có thể vừa được dọn) — không cần đẩy lại.";
        }
        else
        {
            StatusMessage = $"Đã xếp đơn {row.OrderSn} vào hàng chờ đẩy — lượt sau (≤ 2 phút) sẽ gửi lại.";
            _services.Log.Append(row.TenTaiKhoan, $"Đẩy lại theo yêu cầu: đơn {row.OrderSn} đã được xếp lại vào hàng chờ đẩy.");
            _services.RaiseOrdersChanged();
        }

        await ReloadAsync();
    }
}
