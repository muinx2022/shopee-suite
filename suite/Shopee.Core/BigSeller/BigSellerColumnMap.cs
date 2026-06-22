namespace Shopee.Core.BigSeller;

/// <summary>
/// Ánh xạ field BigSeller ↔ cột Excel (1-based) trong sheet của shop. Thay cho việc map CỨNG
/// trong code: mỗi shop có thể có layout sheet khác nhau. Mặc định theo layout cũ
/// (A=link, C=giá, D=SKU, E=item id, F=tên gốc, G=tên đã sửa) để tương thích ngược.
/// 0 = không dùng field đó.
/// </summary>
public sealed class BigSellerColumnMap
{
    /// <summary>Cột link sản phẩm Shopee (mặc định A).</summary>
    public int LinkColumn { get; set; } = 1;
    /// <summary>Cột giá (mặc định C).</summary>
    public int PriceColumn { get; set; } = 3;
    /// <summary>Cột SKU (mặc định D).</summary>
    public int SkuColumn { get; set; } = 4;
    /// <summary>Cột Shopee item id để khớp dòng (mặc định E). Trống → suy ra từ link.</summary>
    public int ItemIdColumn { get; set; } = 5;
    /// <summary>Cột tên sản phẩm gốc — input cho rewrite AI (mặc định F).</summary>
    public int ProductNameColumn { get; set; } = 6;
    /// <summary>Cột tên đã sửa — output rewrite + tên dùng để update lên BigSeller (mặc định G).</summary>
    public int RewrittenNameColumn { get; set; } = 7;
}
