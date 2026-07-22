using Shopee.Core.Coordination;
using Shopee.Hub;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Api;

/// <summary>
/// API kho SẢN PHẨM (Postgres) — thay dần workbook Excel sync-qua-file. TẤT CẢ dùng policy "Client"
/// (X-Api-Token). Guard đầu handler: <see cref="ProductDb"/> chưa đăng ký DI (không cấu hình Postgres) HOẶC
/// chưa <see cref="ProductDb.IsReady"/> → 503 {error:"pg-not-ready"} (KHÔNG crash — Postgres có thể lên sau).
/// <c>updated_by</c> lấy từ header X-Machine-Id (như PUT /files). Đọc DTO ở Shopee.Core → client dùng lại.
/// </summary>
public static class ProductApiEndpoints
{
    private static IResult PgNotReady() =>
        Results.Json(new { error = "pg-not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    public static void MapProductApi(this WebApplication app)
    {
        var api = app.MapGroup("").RequireAuthorization("Client");

        // ── Đọc: tóm tắt sheet ──
        api.MapGet(HubRoutes.ProductsSheets, async (string? acct, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            return Results.Json(await pdb.GetSheetsAsync(acct ?? "", ct));
        });

        // ── Đọc: link để scrape (chỉ-số-dồn) ──
        api.MapGet(HubRoutes.ProductsLinks, async (string? acct, string? sheet, int? fromDense, int? toDense,
            IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            return Results.Json(await pdb.GetLinksAsync(acct ?? "", sheet ?? "", fromDense ?? 0, toDense ?? 0, ct));
        });

        // ── Đọc: dòng đã có tên-sửa để update ──
        api.MapGet(HubRoutes.ProductsRecordMap, async (string? acct, string? sheet, int? fromRow, int? toRow,
            IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            return Results.Json(await pdb.GetRecordMapAsync(acct ?? "", sheet ?? "", fromRow ?? 0, toRow ?? 0, ct));
        });

        // ── Đọc: dòng để import (itemId/link) ──
        api.MapGet(HubRoutes.ProductsImportIds, async (string? acct, string? sheet, int? fromRow, int? toRow,
            IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            return Results.Json(await pdb.GetImportIdsAsync(acct ?? "", sheet ?? "", fromRow ?? 0, toRow ?? 0, ct));
        });

        // ── Đọc: dòng chờ rewrite ──
        api.MapGet(HubRoutes.ProductsRewritePending, async (string? acct, string? sheet, int? fromRow, int? toRow,
            IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            return Results.Json(await pdb.GetRewritePendingAsync(acct ?? "", sheet ?? "", fromRow ?? 0, toRow ?? 0, ct));
        });

        // ── Ghi: tên-sửa (batch, idempotent) ──
        api.MapPost(HubRoutes.ProductsRewritten, async (ProductRewrittenRequest? r, HttpRequest req,
            IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            var by = req.Headers["X-Machine-Id"].ToString();
            return Results.Json(await pdb.SetRewrittenAsync(r, by, ct));
        });

        // ── Ghi: nối dòng vào cuối sheet ──
        api.MapPost(HubRoutes.ProductsAppend, async (ProductAppendRequest? r, HttpRequest req,
            IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            var by = req.Headers["X-Machine-Id"].ToString();
            return Results.Json(await pdb.AppendRowsAsync(r, by, ct));
        });

        // ── RESUME: đánh dấu đã Import-to-store N itemId (import lại là SAI → GetImportIds lọc bỏ) ──
        api.MapPost(HubRoutes.ProductsMarkImported, async (ProductMarkStoreRequest? r, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            return Results.Json(new ProductMarkStoreResponse(await pdb.MarkImportedAsync(r.Acct, r.Sheet, r.ItemIds ?? [], ct)));
        });

        // ── RESUME: đánh dấu đã Update N itemId (store_updated_name = tên hiện tại → record-map loại tới khi đổi tên) ──
        api.MapPost(HubRoutes.ProductsMarkUpdated, async (ProductMarkStoreRequest? r, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            return Results.Json(new ProductMarkStoreResponse(await pdb.MarkUpdatedAsync(r.Acct, r.Sheet, r.ItemIds ?? [], ct)));
        });

        // ── RESUME: xoá tiến độ store (op="import"|"update") của 1 (acc + sheet) — "Chạy lại từ đầu" ──
        api.MapPost(HubRoutes.ProductsResetStoreProgress, async (ProductResetStoreRequest? r, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            return Results.Json(new ProductResetStoreResponse(await pdb.ResetStoreProgressAsync(r.Acct, r.Sheet, r.Op ?? "", ct)));
        });

        // ── Import: body = bytes xlsx (đọc Request.Body như PUT /files, KHÔNG [FromBody]) ──
        api.MapPost(HubRoutes.ProductsImportXlsx, async (string? acct, string? mode, string? file,
            int? linkCol, int? priceCol, int? skuCol, int? itemCol, int? nameCol, int? rewrittenCol,
            HttpRequest req, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (string.IsNullOrEmpty(acct)) return Results.BadRequest();

            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();

            var ov = new ProductXlsxCodec.ColumnOverrides(
                linkCol ?? 1, priceCol ?? 3, skuCol ?? 4, itemCol ?? 5, nameCol ?? 6, rewrittenCol ?? 7);
            var by = req.Headers["X-Machine-Id"].ToString();
            var res = await pdb.ImportXlsxAsync(acct, mode ?? "replace", file, bytes, ov, by, ct);
            return Results.Json(res);
        });

        // ── Export: file xlsx dựng lại từ kho (worksheet đặt tên = TÊN SHOP, hết lộ GUID ngăn dữ liệu) ──
        api.MapGet(HubRoutes.ProductsExportXlsx, async (string? acct, string? sheet, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (string.IsNullOrEmpty(acct)) return Results.BadRequest();
            var (bytes, fileName) = await pdb.ExportXlsxAsync(acct, sheet, ct, ShopTitles(sp, acct));
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        });

        // ══ Trang "📦 Dữ liệu" (mọi shop) — client desktop thao tác qua HTTP như Blazor gọi in-process ══

        // ── Đọc: đếm + 1 trang khớp lọc (Limit kẹp [1..500], Offset ≥ 0) trong 1 round-trip ──
        // Dùng chung ProductDbDataOps (một nguồn logic với ProductGridEngine phía UI); pdb đã ready ở guard trên.
        api.MapPost(HubRoutes.ProductsAllData, async (AllDataQueryRequest? r, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            var f = r.Filter ?? new AllDataFilter(null, null, null, null, null, false, false, null);   // JSON thiếu filter → không lọc
            var limit = Math.Clamp(r.Limit, 1, 500);
            var offset = Math.Max(0, r.Offset);
            var page = await new ProductDbDataOps(pdb, "").QueryAllAsync(f, offset, limit, ct);   // đọc: updated_by không dùng
            return Results.Json(page);
        });

        // ── Ghi: đánh dấu "đã bán" cho các khoá vị trí ──
        api.MapPost(HubRoutes.ProductsMarkSold, async (ProductKeysRequest? r, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            var keys = r.Keys.Select(k => (k.Acct, k.Sheet, k.RowNo)).ToList();
            return Results.Json(new ProductCountResponse(await pdb.MarkSoldAsync(keys, ct)));
        });

        // ── Ghi: +1 "đã bán" theo SKU khớp tuyệt đối (mọi shop) — module Đơn hàng gọi khi đơn chuyển sang đã-giao ──
        api.MapPost(HubRoutes.ProductsMarkSoldBySku, async (ProductMarkSoldBySkuRequest? r, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            return Results.Json(new ProductCountResponse(await pdb.MarkSoldBySkuAsync(r.Skus ?? [], ct)));
        });

        // ── Ghi: đặt "đã bán" về 0 (xoá lịch sử bán) cho các khoá vị trí ──
        api.MapPost(HubRoutes.ProductsResetSold, async (ProductKeysRequest? r, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            var keys = r.Keys.Select(k => (k.Acct, k.Sheet, k.RowNo)).ToList();
            return Results.Json(new ProductCountResponse(await pdb.ResetSoldAsync(keys, ct)));
        });

        // ── Ghi: cấp lại SKU mới cho các khoá vị trí ──
        api.MapPost(HubRoutes.ProductsRegenSkus, async (ProductKeysRequest? r, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            var keys = r.Keys.Select(k => (k.Acct, k.Sheet, k.RowNo)).ToList();
            return Results.Json(new ProductCountResponse(await pdb.RegenerateSkusAsync(keys, ct)));
        });

        // ── Ghi: xoá các dòng theo khoá vị trí (kèm lịch sử bán) ──
        api.MapPost(HubRoutes.ProductsDeleteRows, async (ProductKeysRequest? r, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            var keys = r.Keys.Select(k => (k.Acct, k.Sheet, k.RowNo)).ToList();
            return Results.Json(new ProductCountResponse(await pdb.DeleteRowsByKeysAsync(keys, ct)));
        });

        // ── Ghi: sửa 1 dòng (Ok=false = không tìm thấy, ví dụ đã bị xoá) ──
        api.MapPost(HubRoutes.ProductsUpdateRow, async (ProductUpdateRowRequest? r, HttpRequest req,
            IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            var by = req.Headers["X-Machine-Id"].ToString();
            var ok = await pdb.UpdateRowAsync(r.Acct, r.Sheet, r.RowNo, r.Data, by, ct);
            return Results.Json(new ProductUpdateRowResponse(ok));
        });

        // ── Ghi: thêm 1 dòng vào cuối sheet — SKU trống → server tự sinh B##### rồi trả về ──
        // Auto-SKU + chèn gói trong ProductDbDataOps.InsertRowAsync (hết trùng logic với adapter engine).
        api.MapPost(HubRoutes.ProductsInsertRow, async (ProductInsertRowRequest? r, HttpRequest req,
            IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (r is null) return Results.BadRequest();
            if (string.IsNullOrEmpty(r.Acct) || string.IsNullOrEmpty(r.Sheet)) return Results.BadRequest();
            var by = req.Headers["X-Machine-Id"].ToString();
            var (rowNo, sku) = await new ProductDbDataOps(pdb, by).InsertRowAsync(r.Acct, r.Sheet, r.Data, ct);
            return Results.Json(new ProductInsertRowResponse(rowNo, sku));
        });

        // ── Đọc: có dòng KHÁC trong shop cùng SKU? (sku rỗng → false; excludeRowNo mặc định -1 = không loại dòng nào) ──
        api.MapGet(HubRoutes.ProductsSkuExists, async (string? acct, string? sheet, string? sku, int? excludeRowNo,
            IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            var s = (sku ?? "").Trim();
            if (s.Length == 0) return Results.Json(new ProductSkuExistsResponse(false));
            var exists = await pdb.ExistsSkuInShopAsync(acct ?? "", sheet ?? "", s, excludeRowNo ?? -1, ct);
            return Results.Json(new ProductSkuExistsResponse(exists));
        });
    }

    /// <summary>Ánh xạ ShopeeDataSheet (khoá ngăn) → TÊN SHOP cho 1 acc, để export đặt tên worksheet. Đọc config
    /// dùng-chung qua <see cref="FileStoreConfigService"/>; acc không có/không cấu hình → null (export giữ khoá ngăn).</summary>
    private static IReadOnlyDictionary<string, string>? ShopTitles(IServiceProvider sp, string acct)
    {
        var a = sp.GetService<FileStoreConfigService>()?.BigSellerAccounts().FirstOrDefault(x => x.Id == acct);
        if (a is null) return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in a.Shops)
            if (!string.IsNullOrWhiteSpace(s.ShopeeDataSheet))
                map[s.ShopeeDataSheet] = s.Name;
        return map;
    }
}
