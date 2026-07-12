using System.Net;
using System.Net.Http;
using Shopee.Core.Coordination;

namespace Shopee.Core.Products;

/// <summary>Hiện thực <see cref="IProductDataOps"/> cho client desktop — thao tác kho sản phẩm QUA HTTP bằng
/// <see cref="HubClient"/> (map 1-1 sang các method "📦 Dữ liệu"). 503 pg-not-ready từ Hub → đổi thành
/// <see cref="ProductStoreNotReadyException"/> để engine chỉ bắt 1 kiểu (đối xứng adapter hub in-process).</summary>
public sealed class HubApiProductDataOps : IProductDataOps
{
    private readonly HubClient _hub;

    public HubApiProductDataOps(HubClient hub) => _hub = hub;

    public Task<AllDataPage> QueryAllAsync(AllDataFilter f, int offset, int limit, CancellationToken ct)
        => Guard(async () =>
        {
            var page = await _hub.QueryProductAllDataAsync(new AllDataQueryRequest(f, offset, limit), ct);
            return page ?? new AllDataPage(0, new List<AllDataRow>());   // null (không nội dung) → trang rỗng
        });

    public Task<int> MarkSoldAsync(List<ProductRowKey> keys, CancellationToken ct)
        => Guard(() => _hub.MarkProductsSoldAsync(keys, ct));

    public Task<int> ResetSoldAsync(List<ProductRowKey> keys, CancellationToken ct)
        => Guard(() => _hub.ResetProductsSoldAsync(keys, ct));

    public Task<int> RegenSkusAsync(List<ProductRowKey> keys, CancellationToken ct)
        => Guard(() => _hub.RegenProductSkusAsync(keys, ct));

    public Task<int> DeleteRowsAsync(List<ProductRowKey> keys, CancellationToken ct)
        => Guard(() => _hub.DeleteProductRowsAsync(keys, ct));

    public Task<bool> UpdateRowAsync(string acct, string sheet, int rowNo, ProductRowData data, CancellationToken ct)
        => Guard(() => _hub.UpdateProductRowAsync(new ProductUpdateRowRequest(acct, sheet, rowNo, data), ct));

    public Task<(int RowNo, string Sku)> InsertRowAsync(string acct, string sheet, ProductRowData data, CancellationToken ct)
        => Guard(async () =>
        {
            var res = await _hub.InsertProductRowAsync(new ProductInsertRowRequest(acct, sheet, data), ct);
            if (res is null) throw new InvalidOperationException("Không thêm được dòng.");
            return (res.RowNo, res.Sku);
        });

    public Task<bool> SkuExistsAsync(string acct, string sheet, string sku, int excludeRowNo, CancellationToken ct)
        => Guard(() => _hub.ProductSkuExistsAsync(acct, sheet, sku, excludeRowNo, ct));

    // 503 (ServiceUnavailable) = pg-not-ready → đổi sang ProductStoreNotReadyException; lỗi mạng/timeout khác cứ ném.
    private static async Task<T> Guard<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        { throw new ProductStoreNotReadyException(); }
    }
}
