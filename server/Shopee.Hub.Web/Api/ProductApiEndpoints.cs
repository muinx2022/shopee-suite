using Shopee.Core.Coordination;
using Shopee.Hub;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Api;

/// <summary>
/// API kho SẢN PHẨM (Postgres) — thay dần workbook Excel sync-qua-file. TẤT CẢ dùng policy "Client"
/// (X-Api-Token). Guard đầu handler nằm gọn trong <see cref="WithPg"/>: <see cref="ProductDb"/> chưa đăng ký DI
/// (không cấu hình Postgres) HOẶC chưa <see cref="ProductDb.IsReady"/> → 503 {error:"pg-not-ready"} (KHÔNG crash —
/// Postgres có thể lên sau); guard chạy TRƯỚC mọi kiểm tra body, nên body rỗng lúc Pg chưa lên vẫn ra 503 chứ
/// không phải 400. <c>updated_by</c> lấy từ header X-Machine-Id (như PUT /files). Đọc DTO ở Shopee.Core → client
/// dùng lại.
/// </summary>
public static class ProductApiEndpoints
{
    private static IResult PgNotReady() =>
        Results.Json(new { error = "pg-not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>Bọc thân handler bằng guard "Postgres đã sẵn sàng chưa" — chưa thì 503, rồi mới chạy
    /// <paramref name="body"/> với <see cref="ProductDb"/> đã chắc chắn khác null + ready.</summary>
    private static async Task<IResult> WithPg(IServiceProvider sp, Func<ProductDb, Task<IResult>> body)
    {
        var pdb = sp.GetService<ProductDb>();
        if (pdb is null || !pdb.IsReady) return PgNotReady();
        return await body(pdb);
    }

    public static void MapProductApi(this WebApplication app)
    {
        var api = app.MapGroup("").RequireAuthorization("Client");

        // ── Đọc: tóm tắt sheet ──
        api.MapGet(HubRoutes.ProductsSheets, (string? acct, IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb => Results.Json(await pdb.GetSheetsAsync(acct ?? "", ct))));

        // ── Đọc: link để scrape (chỉ-số-dồn) ──
        api.MapGet(HubRoutes.ProductsLinks, (string? acct, string? sheet, int? fromDense, int? toDense,
            IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
                Results.Json(await pdb.GetLinksAsync(acct ?? "", sheet ?? "", fromDense ?? 0, toDense ?? 0, ct))));

        // ── Đọc: dòng đã có tên-sửa để update ──
        api.MapGet(HubRoutes.ProductsRecordMap, (string? acct, string? sheet, int? fromRow, int? toRow,
            IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
                Results.Json(await pdb.GetRecordMapAsync(acct ?? "", sheet ?? "", fromRow ?? 0, toRow ?? 0, ct))));

        // ── Đọc: dòng để import (itemId/link) ──
        api.MapGet(HubRoutes.ProductsImportIds, (string? acct, string? sheet, int? fromRow, int? toRow,
            IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
                Results.Json(await pdb.GetImportIdsAsync(acct ?? "", sheet ?? "", fromRow ?? 0, toRow ?? 0, ct))));

        // ── Đọc: dòng chờ rewrite ──
        api.MapGet(HubRoutes.ProductsRewritePending, (string? acct, string? sheet, int? fromRow, int? toRow,
            IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
                Results.Json(await pdb.GetRewritePendingAsync(acct ?? "", sheet ?? "", fromRow ?? 0, toRow ?? 0, ct))));

        // ── Ghi: tên-sửa (batch, idempotent) ──
        api.MapPost(HubRoutes.ProductsRewritten, (ProductRewrittenRequest? r, HttpRequest req,
            IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                var by = req.Headers["X-Machine-Id"].ToString();
                return Results.Json(await pdb.SetRewrittenAsync(r, by, ct));
            }));

        // ── Ghi: nối dòng vào cuối sheet ── HẾT consumer từ đợt dọn 06/08 (HubClient bỏ PostProductAppendAsync).
        //    KHÔNG xoá vội — soak 2–3 tuần xem log VM có lượt trúng nào không rồi mới gỡ.
        api.MapPost(HubRoutes.ProductsAppend, (ProductAppendRequest? r, HttpRequest req,
            IServiceProvider sp, CancellationToken ct) =>
        {
            // Log NGOÀI WithPg: Pg chưa sẵn sàng thì WithPg trả sớm không chạy body — mất đúng bằng chứng soak.
            app.Logger.LogWarning("legacy endpoint hit: {path} tu {ip}", req.Path.Value, req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?");
            return WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                var by = req.Headers["X-Machine-Id"].ToString();
                return Results.Json(await pdb.AppendRowsAsync(r, by, ct));
            });
        });

        // ── RESUME: đánh dấu đã Import-to-store N itemId (import lại là SAI → GetImportIds lọc bỏ) ──
        api.MapPost(HubRoutes.ProductsMarkImported, (ProductMarkStoreRequest? r, IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                return Results.Json(new ProductMarkStoreResponse(await pdb.MarkImportedAsync(r.Acct, r.Sheet, r.ItemIds ?? [], ct)));
            }));

        // ── RESUME: đánh dấu đã Update N itemId (store_updated_name = tên hiện tại → record-map loại tới khi đổi tên) ──
        api.MapPost(HubRoutes.ProductsMarkUpdated, (ProductMarkStoreRequest? r, IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                return Results.Json(new ProductMarkStoreResponse(await pdb.MarkUpdatedAsync(r.Acct, r.Sheet, r.ItemIds ?? [], ct)));
            }));

        // ── RESUME: xoá tiến độ store (op="import"|"update") của 1 (acc + sheet) — "Chạy lại từ đầu" ──
        api.MapPost(HubRoutes.ProductsResetStoreProgress, (ProductResetStoreRequest? r, IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                return Results.Json(new ProductResetStoreResponse(await pdb.ResetStoreProgressAsync(r.Acct, r.Sheet, r.Op ?? "", ct)));
            }));

        // ══ Trang "📦 Dữ liệu" (mọi shop) — client desktop thao tác qua HTTP như Blazor gọi in-process ══

        // ── Đọc: đếm + 1 trang khớp lọc (Limit kẹp [1..500], Offset ≥ 0) trong 1 round-trip ──
        // Dùng chung ProductDbDataOps (một nguồn logic với ProductGridEngine phía UI); pdb đã ready ở guard trên.
        api.MapPost(HubRoutes.ProductsAllData, (AllDataQueryRequest? r, IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                var f = r.Filter ?? new AllDataFilter(null, null, null, null, null, false, false, null);   // JSON thiếu filter → không lọc
                var limit = Math.Clamp(r.Limit, 1, 500);
                var offset = Math.Max(0, r.Offset);
                var page = await new ProductDbDataOps(pdb, "").QueryAllAsync(f, offset, limit, ct);   // đọc: updated_by không dùng
                return Results.Json(page);
            }));

        // ── Ghi: đánh dấu "đã bán" cho các khoá vị trí ──
        api.MapPost(HubRoutes.ProductsMarkSold, (ProductKeysRequest? r, IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                var keys = r.Keys.Select(k => (k.Acct, k.Sheet, k.RowNo)).ToList();
                return Results.Json(new ProductCountResponse(await pdb.MarkSoldAsync(keys, ct)));
            }));

        // ── Ghi: +1 "đã bán" theo SKU khớp tuyệt đối (mọi shop) — module Đơn hàng gọi khi đơn chuyển sang đã-giao ──
        api.MapPost(HubRoutes.ProductsMarkSoldBySku, (ProductMarkSoldBySkuRequest? r, IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                return Results.Json(new ProductCountResponse(await pdb.MarkSoldBySkuAsync(r.Skus ?? [], ct)));
            }));

        // ── Ghi: đặt "đã bán" về 0 (xoá lịch sử bán) cho các khoá vị trí ──
        api.MapPost(HubRoutes.ProductsResetSold, (ProductKeysRequest? r, IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                var keys = r.Keys.Select(k => (k.Acct, k.Sheet, k.RowNo)).ToList();
                return Results.Json(new ProductCountResponse(await pdb.ResetSoldAsync(keys, ct)));
            }));

        // ── Ghi: cấp lại SKU mới cho các khoá vị trí ──
        api.MapPost(HubRoutes.ProductsRegenSkus, (ProductKeysRequest? r, IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                var keys = r.Keys.Select(k => (k.Acct, k.Sheet, k.RowNo)).ToList();
                return Results.Json(new ProductCountResponse(await pdb.RegenerateSkusAsync(keys, ct)));
            }));

        // ── Ghi: xoá các dòng theo khoá vị trí (kèm lịch sử bán) ──
        api.MapPost(HubRoutes.ProductsDeleteRows, (ProductKeysRequest? r, IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                var keys = r.Keys.Select(k => (k.Acct, k.Sheet, k.RowNo)).ToList();
                return Results.Json(new ProductCountResponse(await pdb.DeleteRowsByKeysAsync(keys, ct)));
            }));

        // ── Ghi: sửa 1 dòng (Ok=false = không tìm thấy, ví dụ đã bị xoá) ──
        api.MapPost(HubRoutes.ProductsUpdateRow, (ProductUpdateRowRequest? r, HttpRequest req,
            IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                var by = req.Headers["X-Machine-Id"].ToString();
                var ok = await pdb.UpdateRowAsync(r.Acct, r.Sheet, r.RowNo, r.Data, by, ct);
                return Results.Json(new ProductUpdateRowResponse(ok));
            }));

        // ── Ghi: thêm 1 dòng vào cuối sheet — SKU trống → server tự sinh B##### rồi trả về ──
        // Auto-SKU + chèn gói trong ProductDbDataOps.InsertRowAsync (hết trùng logic với adapter engine).
        api.MapPost(HubRoutes.ProductsInsertRow, (ProductInsertRowRequest? r, HttpRequest req,
            IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                if (r is null) return Results.BadRequest();
                if (string.IsNullOrEmpty(r.Acct) || string.IsNullOrEmpty(r.Sheet)) return Results.BadRequest();
                var by = req.Headers["X-Machine-Id"].ToString();
                var (rowNo, sku) = await new ProductDbDataOps(pdb, by).InsertRowAsync(r.Acct, r.Sheet, r.Data, ct);
                return Results.Json(new ProductInsertRowResponse(rowNo, sku));
            }));

        // ── Đọc: có dòng KHÁC trong shop cùng SKU? (sku rỗng → false; excludeRowNo mặc định -1 = không loại dòng nào) ──
        api.MapGet(HubRoutes.ProductsSkuExists, (string? acct, string? sheet, string? sku, int? excludeRowNo,
            IServiceProvider sp, CancellationToken ct) =>
            WithPg(sp, async pdb =>
            {
                var s = (sku ?? "").Trim();
                if (s.Length == 0) return Results.Json(new ProductSkuExistsResponse(false));
                var exists = await pdb.ExistsSkuInShopAsync(acct ?? "", sheet ?? "", s, excludeRowNo ?? -1, ct);
                return Results.Json(new ProductSkuExistsResponse(exists));
            }));
    }
}
