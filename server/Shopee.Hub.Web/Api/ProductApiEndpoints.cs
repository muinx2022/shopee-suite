using Shopee.Core.Coordination;
using Shopee.Hub;

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

        // ── Export: file xlsx dựng lại từ kho ──
        api.MapGet(HubRoutes.ProductsExportXlsx, async (string? acct, string? sheet, IServiceProvider sp, CancellationToken ct) =>
        {
            var pdb = sp.GetService<ProductDb>();
            if (pdb is null || !pdb.IsReady) return PgNotReady();
            if (string.IsNullOrEmpty(acct)) return Results.BadRequest();
            var (bytes, fileName) = await pdb.ExportXlsxAsync(acct, sheet, ct);
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        });
    }
}
