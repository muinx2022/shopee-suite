namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Chọn SỐ TIỀN đẩy lên cột tiền của Google Sheet (<see cref="GsheetOrderRow.DoanhThu"/>). Tiền bán chuẩn là
/// "Ước tính" (<c>final_amount</c> — "Số tiền cuối cùng" đọc ở trang chi tiết đơn), nhưng đơn thường được ghi
/// dòng ở lượt sync ĐẦU khi CHƯA mở trang chi tiết nên chưa có số đó.
/// <para>
/// Hợp đồng với Apps Script (<c>doPost</c>) là "chỉ ghi ô đang trống" (field null bị BỎ khỏi JSON) → KHÔNG được
/// ghi tạm tổng tiền vào ô tiền của đơn thường: ô đã có số thì lần đẩy lại (khi ước tính xuất hiện) không đè
/// được nữa → kẹt số sai VĨNH VIỄN. Vì vậy chưa có ước tính thì để TRỐNG, lượt sync sau ước tính về sẽ điền vào
/// ô trống đó (cơ chế đẩy lại: cờ <c>gsheet_da_co_uoc_tinh</c> trong <c>AccountSession</c>).
/// </para>
/// Riêng đơn HỦY thì trả tổng tiền: đơn hủy KHÔNG BAO GIỜ có ước tính (<see cref="Models.SyncedOrder.FinalAmount"/>
/// chỉ lấy cho đơn khác "Đã hủy") nên không có gì để đè sau này — để trống chỉ làm mất số tiền đang hiển thị.
/// </summary>
public static class GsheetMoney
{
    /// <summary>
    /// Số tiền ghi lên sheet: <paramref name="finalAmount"/> nếu có; chưa có thì
    /// <paramref name="totalPrice"/> khi <paramref name="daHuy"/>, ngược lại <c>null</c> (để ô TRỐNG chờ ước tính).
    /// </summary>
    public static long? Chon(long? finalAmount, long? totalPrice, bool daHuy)
        => finalAmount ?? (daHuy ? totalPrice : null);
}
