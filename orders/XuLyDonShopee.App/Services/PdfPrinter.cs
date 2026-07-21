using System.Diagnostics;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Gửi một file PDF tới MÁY IN MẶC ĐỊNH của Windows qua ShellExecute verb <c>"print"</c> — ứng dụng PDF mặc
/// định (Edge / Acrobat / SumatraPDF...) sẽ in ra máy in mặc định. KHÔNG chạy binary tự build nên WDAC không
/// chặn (ShellExecute). Dùng chung cho nút "In phiếu" từng đơn (<see cref="OrderRowViewModel"/>) và "In nhiều đơn".
/// <para>
/// Hạn chế đã biết (chấp nhận — ngoài phạm vi in-im-lặng-tuyệt-đối): phụ thuộc app PDF mặc định có hỗ trợ
/// verb <c>"print"</c>; máy KHÔNG có app PDF mặc định → lệnh in ném (bắt lại → trả false); một số app mở cửa
/// sổ rồi mới in (không hoàn toàn im lặng). Đây là cách đơn giản/bền nhất để in hàng loạt file phiếu.
/// </para>
/// </summary>
public static class PdfPrinter
{
    /// <summary>
    /// In một file PDF tới máy in mặc định. Trả <c>true</c> nếu ĐÃ khởi chạy được lệnh in; <c>false</c> nếu
    /// lỗi (không có app PDF mặc định hỗ trợ verb print, file đang khóa...). NUỐT mọi lỗi (KHÔNG ném) để một
    /// file hỏng không làm sập vòng in hàng loạt.
    /// </summary>
    public static bool TryPrint(string pdfPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(pdfPath)
            {
                Verb = "print",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            // Không có app PDF mặc định hỗ trợ "print" / file khóa / lỗi shell → coi như in thất bại.
            return false;
        }
    }
}
