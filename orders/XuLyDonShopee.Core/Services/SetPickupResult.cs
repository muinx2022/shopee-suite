namespace XuLyDonShopee.Core.Services;

/// <summary>Kết quả đặt "địa chỉ lấy hàng" theo tỉnh — phân biệt bước hỏng để app báo đúng.</summary>
public enum SetPickupResult
{
    /// <summary>Đã là địa chỉ lấy hàng sẵn, hoặc đã tick + Lưu thành công.</summary>
    Ok,
    /// <summary>Không có trang/phiên hoặc lỗi bất ngờ.</summary>
    Failed,
    /// <summary>Không thấy địa chỉ nào khớp tỉnh trong danh sách.</summary>
    AddressNotFound,
    /// <summary>Bấm "Sửa" nhưng modal "Sửa Địa chỉ" không mở (shop khóa sửa?).</summary>
    EditModalNotOpened,
    /// <summary>Modal mở nhưng không thấy ô "Đặt làm địa chỉ lấy hàng".</summary>
    CheckboxNotFound,
    /// <summary>Thấy ô nhưng click không tick được sau vài lần (bị che / hit-test loại / re-render).</summary>
    CheckboxClickFailed,
    /// <summary>Đã tick nhưng bấm "Lưu" không được / modal không đóng.</summary>
    SaveFailed,
}
