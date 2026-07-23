namespace XuLyDonShopee.Core.Services;

/// <summary>
/// Một shop trong bảng <c>/portal/shop</c> của Nền tảng tài khoản phụ (một tài khoản subaccount có nhiều shop).
/// </summary>
/// <param name="ShopId">Mã shop (thuộc tính <c>data-row-key</c> của dòng bảng, vd <c>1843718137</c>).</param>
/// <param name="ShopName">Tên hiển thị của shop (vd "Alina Store1").</param>
/// <param name="LoginName">Tên đăng nhập của shop (vd "alina99.store") — dùng làm cột Tên Shop trên Google Sheet.</param>
public sealed record ShopListItem(string ShopId, string ShopName, string LoginName);
