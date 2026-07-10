namespace Shopee.Core.BigSeller;

/// <summary>
/// Cấu hình CHẠY của một tài khoản BigSeller — 1 bộ DÙNG CHUNG cho MỌI op (scrape / import / update),
/// shop KHÔNG set riêng nữa. Gộp về mức account để khỏi rải config 3 nơi (scrape-store theo account +
/// field theo shop). RIÊNG-MÁY: mỗi máy giữ cấu hình chạy của nó (xem chú thích ở BigSellerAccount.RunConfig).
/// </summary>
public sealed class BigSellerRunConfig
{
    /// <summary>Bắt đầu từ dòng nào của sheet (dòng 1 là header → mặc định 2).</summary>
    public int StartRow { get; set; } = 2;
    /// <summary>Đến dòng nào thì DỪNG (0 = chạy hết sheet).</summary>
    public int EndRow { get; set; }
    /// <summary>Số cửa sổ Brave / lane chạy song song — áp dụng MỌI op (scrape/import/update).</summary>
    public int Processes { get; set; } = 2;
    /// <summary>Số tk Shopee "đóng khung" cố định cho tk này (scrape) — chỉ xoay vòng trong khung.</summary>
    public int FrameSize { get; set; } = 10;
    /// <summary>Số dòng mỗi khối — mỗi tk Shopee nhận 1 khối kế tiếp theo số này (scrape).</summary>
    public int RowsPerAccount { get; set; } = 60;
    /// <summary>Reload trang listing mỗi N giây (update/import).</summary>
    public int ReloadSeconds { get; set; } = 20;
}
