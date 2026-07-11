namespace Shopee.Core.Coordination;

// ── DTO kho SẢN PHẨM trên Hub (Postgres) — dùng chung client + server ──────────
// Thay dần workbook Excel: 1 acc BigSeller = 1 kho, 1 shop = 1 "sheet". Mọi ô để text
// (copy nguyên trạng như Excel); runner phía client tự parse số/tiền. Hai kiểu đánh số:
//  · "chỉ-số-dồn" (dense) = vị trí 1-based trong tập dòng HỢP LỆ (link + tên gốc non-blank) → dùng cho scrape.
//  · "số dòng tuyệt đối" (rowNo) = số dòng thật trên sheet Excel gốc (có lỗ hổng) → dùng cho import/update/rewrite.

/// <summary>Tóm tắt 1 sheet (shop) của tài khoản: đếm dòng + mốc file nguồn.</summary>
public sealed record ProductSheetInfo(
    string Sheet, int Rows, int LastRow, int DenseRows, int RewrittenCount,
    string? SourceFile, DateTimeOffset? ImportedAt);

/// <summary>1 dòng link để scrape — <see cref="DenseIndex"/> là chỉ-số-dồn, <see cref="RowNo"/> là dòng tuyệt đối.</summary>
public sealed record ProductLinkRow(int DenseIndex, int RowNo, string Link, string Sku, string NameOriginal);

/// <summary>1 dòng đã có tên-sửa để đẩy (update) lên BigSeller. Trả cả itemId LẪN link thô — client tự suy
/// itemId từ link như hiện tại.</summary>
public sealed record ProductRecordRow(int RowNo, string ItemId, string Link, string Sku, string NameRewritten, string PriceSale);

/// <summary>1 dòng để import: có itemId hoặc link để khớp sản phẩm trên BigSeller.</summary>
public sealed record ProductImportIdRow(int RowNo, string ItemId, string Link);

/// <summary>1 dòng còn CHỜ rewrite tên (có tên gốc + SKU, chưa có tên-sửa).</summary>
public sealed record ProductRewritePendingRow(int RowNo, string Sku, string NameOriginal);

/// <summary>1 kết quả rewrite của 1 dòng (client gửi lên sau khi gọi AI).</summary>
public sealed record ProductRewrittenItem(int RowNo, string NameRewritten);
/// <summary>Batch ghi tên-sửa cho 1 (acc + sheet). Idempotent.</summary>
public sealed record ProductRewrittenRequest(string Acct, string Sheet, List<ProductRewrittenItem> Items);
/// <summary><see cref="Missing"/> = các rowNo không tồn tại (không làm hỏng cả batch).</summary>
public sealed record ProductRewrittenResponse(int Updated, int[] Missing);

/// <summary>17 ô của 1 dòng sản phẩm (đúng thứ tự cột A..Q của workbook chuẩn).</summary>
public sealed record ProductRowData(
    string Link, string PriceOriginal, string PriceSale, string Sku, string ItemId,
    string NameOriginal, string NameRewritten, string Category, string ShopName, string Rating,
    string SoldMonth, string Likes, string Reviews, string Region, string Image,
    string MetaShopId, string MetaItemId);

/// <summary>Nối thêm N dòng vào cuối 1 sheet — server tự cấp <c>row_no</c> tuần tự.</summary>
public sealed record ProductAppendRequest(string Acct, string Sheet, List<ProductRowData> Rows);
/// <summary>Khoảng row_no đã cấp cho lô vừa nối ([FromRow..ToRow]).</summary>
public sealed record ProductAppendResponse(int Added, int FromRow, int ToRow);

/// <summary>Số dòng đã ghi cho 1 sheet trong 1 lần import.</summary>
public sealed record ProductSheetImport(string Sheet, int Rows);
/// <summary>Kết quả import xlsx: từng sheet + tổng dòng.</summary>
public sealed record ProductImportResult(List<ProductSheetImport> Sheets, int Total);
