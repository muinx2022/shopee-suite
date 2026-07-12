using Shopee.Hub;

namespace Shopee.Hub.Web.Services;

/// <summary>
/// Chạy <see cref="ProductDb.InitAsync"/> (connect + migrate) ở NỀN, retry tới khi thành công hoặc app dừng.
/// Postgres (Docker) có thể lên SAU hub lúc VM boot → hub KHÔNG được crash: thất bại thì log warning + đợi 15s
/// thử lại. Chỉ đăng ký khi có conn string (Program.cs) → ctor luôn nhận được ProductDb (không null).
/// </summary>
public sealed class ProductDbInitService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly ProductDb _pdb;
    private readonly ILogger<ProductDbInitService> _log;

    public ProductDbInitService(ProductDb pdb, ILogger<ProductDbInitService> log)
    { _pdb = pdb; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _pdb.InitAsync(ct);
                _log.LogInformation("Postgres sẵn sàng — schema sản phẩm đã migrate.");
                // Bật UNIQUE INDEX SKU per-shop RỜI migration (fail = còn trùng trong shop → chỉ warning, KHÔNG chặn hub).
                var idxErr = await _pdb.TryEnsureSkuIndexAsync(ct);
                if (idxErr is null) _log.LogInformation("Index SKU per-shop (ux_pr_shop_sku) đã bật.");
                else _log.LogWarning("Chưa bật được index SKU per-shop (còn SKU trùng trong cùng shop?): {Error}", idxErr);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Chưa kết nối/migrate được Postgres — thử lại sau {Delay}s.", RetryDelay.TotalSeconds);
            }

            try { await Task.Delay(RetryDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
