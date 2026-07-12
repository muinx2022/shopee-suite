using Shopee.Core.Coordination;

namespace Shopee.Core.Products;

/// <summary>Cổng thao tác kho sản phẩm cho <see cref="ProductGridEngine"/> — 2 hiện thực: hub web gọi ProductDb
/// in-process (<c>ProductDbDataOps</c>), client desktop gọi HTTP qua HubClient (<see cref="HubApiProductDataOps"/>).
/// Kho chưa sẵn sàng → ném <see cref="ProductStoreNotReadyException"/>; lỗi hạ tầng khác cứ ném (engine bắt và đổ
/// vào Status).</summary>
public interface IProductDataOps
{
    Task<AllDataPage> QueryAllAsync(AllDataFilter f, int offset, int limit, CancellationToken ct);
    Task<int> MarkSoldAsync(List<ProductRowKey> keys, CancellationToken ct);
    Task<int> ResetSoldAsync(List<ProductRowKey> keys, CancellationToken ct);
    Task<int> RegenSkusAsync(List<ProductRowKey> keys, CancellationToken ct);
    Task<int> DeleteRowsAsync(List<ProductRowKey> keys, CancellationToken ct);
    Task<bool> UpdateRowAsync(string acct, string sheet, int rowNo, ProductRowData data, CancellationToken ct);
    /// <summary>Thêm dòng cuối sheet; data.Sku trống → hiện thực TỰ SINH B#####. Trả (RowNo, Sku cuối cùng).</summary>
    Task<(int RowNo, string Sku)> InsertRowAsync(string acct, string sheet, ProductRowData data, CancellationToken ct);
    Task<bool> SkuExistsAsync(string acct, string sheet, string sku, int excludeRowNo, CancellationToken ct);
}
