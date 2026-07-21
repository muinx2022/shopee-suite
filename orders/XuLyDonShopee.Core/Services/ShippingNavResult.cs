namespace XuLyDonShopee.Core.Services;

/// <summary>Kết quả điều hướng "Cài Đặt Vận Chuyển" → tab "Địa Chỉ" — phân biệt bước hỏng để app báo đúng.</summary>
public enum ShippingNavResult
{
    /// <summary>Đã mở trang cài đặt vận chuyển và tab "Địa Chỉ" đang active.</summary>
    Ok,
    /// <summary>Không có trang/phiên (Pages rỗng) hoặc lỗi bất ngờ.</summary>
    Failed,
    /// <summary>Không đưa được trình duyệt tới trang cài đặt vận chuyển (click không ăn / URL không đổi, kể cả sau fallback Goto).</summary>
    PageNotOpened,
    /// <summary>Trang cài đặt vận chuyển ĐÃ mở nhưng không tìm thấy / không bấm được tab "Địa Chỉ" (Shopee đổi giao diện?).</summary>
    AddressTabNotFound,
}
