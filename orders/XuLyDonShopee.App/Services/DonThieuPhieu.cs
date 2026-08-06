using System;
using System.Collections.Generic;
using System.Linq;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Một đơn ỨNG VIÊN của bước "tự tải lại phiếu thiếu" (một dòng <c>orders</c> của shop đang được check).
/// </summary>
/// <param name="OrderSn">Mã đơn — khóa gửi xuống extension VÀ tên file phiếu.</param>
/// <param name="Tracking">Mã vận đơn. Rỗng = đơn CHƯA chuẩn bị hàng ⇒ chưa có phiếu nào để tải lại.</param>
/// <param name="CoFilePhieu">Đã có file PDF phiếu HỢP LỆ trên đĩa chưa — người gọi tính bằng
/// <see cref="SlipFiles.SlipFileIsValidPdf"/> (CÙNG luật với nút "Tải lại" ở màn Đơn hàng).</param>
/// <param name="Moc">Mốc xếp MỚI NHẤT TRƯỚC — số càng LỚN càng mới. Đang dùng <c>orders.id</c> (thứ tự ghi nhận
/// lần đầu trên máy này). CỐ Ý không dùng <c>synced_at</c>: mọi đơn của shop đều được sync lại mỗi vòng nên
/// <c>synced_at</c> gần như bằng nhau, xếp theo nó là xếp ngẫu nhiên.</param>
/// <param name="DaHuy">Đơn đã hủy (<c>ShopeeShippingNav.LaDonHuy</c>). Đơn hủy KHÔNG còn nút "In phiếu giao"
/// trên Seller Centre nên mọi lượt tải lại đều trượt — mà nó vẫn có mã vận đơn + thiếu file, tức là sẽ ngốn
/// suất trần MỖI VÒNG, VĨNH VIỄN. Xếp "mới nhất trước" chỉ đẩy nó xuống đáy chứ không loại được.</param>
internal readonly record struct DonUngVienTaiLaiPhieu(
    string? OrderSn, string? Tracking, bool CoFilePhieu, long Moc, bool DaHuy = false);

/// <summary>
/// Luật chọn đơn cho bước "tự tải lại phiếu thiếu" (chạy cuối mỗi lượt check shop). Tách khỏi
/// <see cref="AccountSession"/> để test được KHÔNG cần trình duyệt/DB: phần đụng đĩa (đọc DB, kiểm file PDF)
/// nằm ở người gọi, ở đây chỉ còn luật thuần.
/// </summary>
internal static class DonThieuPhieu
{
    /// <summary>
    /// HÀM THUẦN — lọc ra <c>order_sn</c> các đơn ĐANG THIẾU PHIẾU và xếp MỚI NHẤT TRƯỚC:
    /// <list type="bullet">
    /// <item>bỏ đơn KHÔNG có mã vận đơn (chưa chuẩn bị hàng → Shopee chưa có phiếu giao để in);</item>
    /// <item>bỏ đơn ĐÃ có file PDF hợp lệ (đúng luật ẩn/hiện nút "Tải lại");</item>
    /// <item>bỏ đơn ĐÃ HỦY — Seller Centre không còn nút "In phiếu giao" cho đơn hủy nên lượt tải lại LUÔN
    /// trượt; không loại thì mấy đơn này chiếm suất trần của mọi vòng, mãi mãi;</item>
    /// <item>bỏ mã rỗng, gộp mã trùng (giữ bản có <see cref="DonUngVienTaiLaiPhieu.Moc"/> lớn nhất);</item>
    /// <item>xếp <see cref="DonUngVienTaiLaiPhieu.Moc"/> GIẢM DẦN — trần của
    /// <c>ShopFlowRunner.TranTaiLaiPhieuMoiShop</c> cắt từ đuôi, nên đơn mới nhất luôn được tải trước.</item>
    /// </list>
    /// KHÔNG áp trần ở đây: trần là của phía chạy trình duyệt (mỗi lượt tải là một vòng điều hướng thật) và nằm
    /// cùng chỗ với câu log "còn k đơn để vòng sau" — xem <c>ShopFlowRunner.ChiaTheoTranTaiLaiPhieu</c>.
    /// </summary>
    internal static IReadOnlyList<string> ChonDonThieuPhieu(IEnumerable<DonUngVienTaiLaiPhieu>? nguon)
    {
        if (nguon is null)
        {
            return Array.Empty<string>();
        }

        return nguon
            .Where(d => !string.IsNullOrWhiteSpace(d.OrderSn)
                        && !string.IsNullOrWhiteSpace(d.Tracking)
                        && !d.CoFilePhieu
                        && !d.DaHuy)
            .OrderByDescending(d => d.Moc)
            .Select(d => d.OrderSn!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
