using Shopee.Core.Coordination;
using Shopee.Core.Products;

namespace Shopee.Hub;

/// <summary>
/// Hiện thực <see cref="IProductDataOps"/> cho hub web — bọc <see cref="ProductDb"/> gọi THẲNG in-process (khớp
/// cách API /products/* + trang /data gọi ProductDb). Guard đầu mỗi method: ProductDb chưa đăng ký DI (null) HOẶC
/// chưa <see cref="ProductDb.IsReady"/> → ném <see cref="ProductStoreNotReadyException"/> (đối xứng client gặp 503).
/// <paramref name="updatedBy"/> = X-Machine-Id (như PUT /files) ghi vào updated_by cho Update/Insert.
/// </summary>
public sealed class ProductDbDataOps : IProductDataOps
{
    private readonly ProductDb? _pdb;
    private readonly string _by;

    public ProductDbDataOps(ProductDb? pdb, string updatedBy)
    {
        _pdb = pdb;
        _by = updatedBy ?? "";
    }

    // Kho sẵn sàng thì trả ProductDb (non-null); ngược lại ném để engine đặt PgReady=false thay vì crash.
    private ProductDb Ready() =>
        _pdb is { IsReady: true } pdb ? pdb : throw new ProductStoreNotReadyException();

    public async Task<AllDataPage> QueryAllAsync(AllDataFilter f, int offset, int limit, CancellationToken ct)
    {
        var pdb = Ready();
        var total = await pdb.CountAllAsync(f, ct);
        var rows = await pdb.QueryAllAsync(f, offset, limit, ct);
        return new AllDataPage(total, rows);
    }

    public async Task<int> MarkSoldAsync(List<ProductRowKey> keys, CancellationToken ct)
        => await Ready().MarkSoldAsync(ToTuples(keys), ct);

    public async Task<int> ResetSoldAsync(List<ProductRowKey> keys, CancellationToken ct)
        => await Ready().ResetSoldAsync(ToTuples(keys), ct);

    public async Task<int> RegenSkusAsync(List<ProductRowKey> keys, CancellationToken ct)
        => await Ready().RegenerateSkusAsync(ToTuples(keys), ct);

    public async Task<int> DeleteRowsAsync(List<ProductRowKey> keys, CancellationToken ct)
        => await Ready().DeleteRowsByKeysAsync(ToTuples(keys), ct);

    public async Task<bool> UpdateRowAsync(string acct, string sheet, int rowNo, ProductRowData data, CancellationToken ct)
        => await Ready().UpdateRowAsync(acct, sheet, rowNo, data, _by, ct);

    // SKU trống → tự sinh 1 mã B##### (duy nhất trong shop) rồi chèn cuối sheet — gói auto-SKU tại 1 chỗ (endpoint
    // /rows/insert cũ trùng logic này giờ gọi qua đây). Trả (row_no vừa cấp, sku cuối cùng).
    public async Task<(int RowNo, string Sku)> InsertRowAsync(string acct, string sheet, ProductRowData data, CancellationToken ct)
    {
        var pdb = Ready();
        var d = data;
        if (string.IsNullOrWhiteSpace(d.Sku))
        {
            var codes = await pdb.GenerateSkusAsync(acct, sheet, 1, ct);
            d = d with { Sku = codes.Count > 0 ? codes[0] : "" };
        }
        var rowNo = await pdb.InsertRowAtEndAsync(acct, sheet, d, _by, ct);
        return (rowNo, d.Sku);
    }

    public async Task<bool> SkuExistsAsync(string acct, string sheet, string sku, int excludeRowNo, CancellationToken ct)
        => await Ready().ExistsSkuInShopAsync(acct, sheet, sku, excludeRowNo, ct);

    private static List<(string Acct, string Sheet, int RowNo)> ToTuples(List<ProductRowKey> keys)
        => keys.Select(k => (k.Acct, k.Sheet, k.RowNo)).ToList();
}
