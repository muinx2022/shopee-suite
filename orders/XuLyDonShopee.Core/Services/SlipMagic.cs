namespace XuLyDonShopee.Core.Services;

/// <summary>
/// NHẬN DẠNG file phiếu giao là PDF thật qua magic đầu file. Một nguồn sự thật cho cả lúc GHI
/// (<c>ShopFlowRunner.TrySaveSlip</c> — dữ liệu extension gửi về) lẫn lúc ĐỌC (<c>SlipFiles</c> bên App —
/// đọc file trên đĩa để đính kèm/vẽ nút "Tải phiếu"), thay hai bản kiểm tự viết trước đây.
/// </summary>
public static class SlipMagic
{
    /// <summary>
    /// True nếu 5 byte đầu là magic <c>%PDF-</c> — nhận đúng PDF thật, tránh coi HTML/redirect (GET lại phiếu
    /// có thể ra HTML 200-OK) là phiếu.
    /// <para>Chuẩn 5 byte (kể cả dấu <c>-</c>): mọi PDF hợp lệ đều mở đầu bằng <c>%PDF-1.x</c>. Trước đây phía
    /// GHI chỉ kiểm 4 byte <c>%PDF</c> nên một file "4 byte đúng, byte thứ 5 sai" vẫn được lưu rồi phía ĐỌC mới
    /// từ chối — giờ chặn ngay từ lúc lưu.</para>
    /// </summary>
    public static bool LooksPdf(ReadOnlySpan<byte> b)
        => b.Length >= 5 && b[0] == (byte)'%' && b[1] == (byte)'P'
           && b[2] == (byte)'D' && b[3] == (byte)'F' && b[4] == (byte)'-';
}
