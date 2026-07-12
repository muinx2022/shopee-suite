namespace Shopee.Core.Coordination;

// ── DTO kho SẢN PHẨM trên Hub (Postgres) — dùng chung client + server ──────────
// Thay dần workbook Excel: 1 acc BigSeller = 1 kho, 1 shop = 1 "sheet". Mọi ô để text
// (copy nguyên trạng như Excel); runner phía client tự parse số/tiền. Hai kiểu đánh số (khớp ScrapeWorkbook):
//  · "chỉ-số-dồn" (dense) = vị trí 1-based trong danh sách DÒNG-CÓ-THẬT đã nén → dùng cho scrape. Dòng thiếu
//    link/tên gốc VẪN chiếm chỗ dense (như ws.RowsUsed() của Excel) nhưng KHÔNG được trả trong /products/links.
//  · "số dòng tuyệt đối" (rowNo) = số dòng thật trên sheet Excel gốc (có lỗ hổng) → dùng cho import/update/rewrite.

/// <summary>Tóm tắt 1 sheet (shop) của tài khoản: đếm dòng + mốc file nguồn. <see cref="Rows"/> = TỔNG DỀN (mọi
/// dòng có thật) — nguồn TotalDataRows của scrape (khớp Excel); <see cref="DenseRows"/> = số dòng HỢP LỆ (link +
/// tên gốc non-blank), giờ chỉ còn là thông tin.</summary>
public sealed record ProductSheetInfo(
    string Sheet, int Rows, int LastRow, int DenseRows, int RewrittenCount,
    string? SourceFile, DateTimeOffset? ImportedAt);

/// <summary>1 dòng link để scrape — <see cref="DenseIndex"/> là chỉ-số-dồn (đánh trên MỌI dòng-có-thật, chỉ dòng
/// hợp lệ được trả về), <see cref="RowNo"/> là dòng tuyệt đối.</summary>
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

/// <summary>RESUME per-SP: đánh dấu N itemId đã Import / đã Update lên BigSeller cho 1 (acc + sheet). Khớp theo
/// item_id trên server (tối ưu lọc lượt sau); chốt chặn CHÍNH là OpProgressStore local phía client.</summary>
public sealed record ProductMarkStoreRequest(string Acct, string Sheet, string[] ItemIds);
/// <summary><see cref="Updated"/> = số dòng product_rows đã cập nhật mốc.</summary>
public sealed record ProductMarkStoreResponse(int Updated);

/// <summary>RESUME per-SP: xoá tiến độ store của 1 (acc + sheet) cho 1 op ("import"|"update") — "Chạy lại từ đầu".</summary>
public sealed record ProductResetStoreRequest(string Acct, string Sheet, string Op);
/// <summary><see cref="Reset"/> = số dòng đã xoá mốc store.</summary>
public sealed record ProductResetStoreResponse(int Reset);

/// <summary>Số dòng đã ghi cho 1 sheet trong 1 lần import.</summary>
public sealed record ProductSheetImport(string Sheet, int Rows);
/// <summary>Kết quả import xlsx: từng sheet + tổng dòng.</summary>
public sealed record ProductImportResult(List<ProductSheetImport> Sheets, int Total);

// ── DTO trang "📦 Dữ liệu" (mọi shop) — client desktop làm các thao tác này QUA API HTTP ─────

/// <summary>Bộ lọc trang "📦 Dữ liệu" (mọi shop): mỗi field null/blank/0 = KHÔNG lọc chiều đó.
/// <see cref="PriceMin"/>/<see cref="PriceMax"/> so trên SỐ tách từ text price_sale (dòng không parse được bị loại
/// khi có lọc giá). <see cref="SoldOnly"/> = chỉ dòng đã có bản ghi product_sold (sold_count &gt; 0).
/// <see cref="DupSkuOnly"/> = chỉ dòng có SKU non-blank TRÙNG với dòng khác TRONG CÙNG shop (soi trùng để dọn).</summary>
public sealed record AllDataFilter(
    string? Acct, string? Sheet, string? Sku, long? PriceMin, long? PriceMax, bool SoldOnly, bool DupSkuOnly);

/// <summary>1 dòng cho lưới "📦 Dữ liệu": khoá vị trí (AccountId, Sheet, RowNo) + ĐỦ 17 ô dữ liệu (để sửa inline;
/// lưới vẫn hiển thị 1 phần) + số đã bán (0 nếu chưa có bản ghi product_sold) + lúc sửa.</summary>
public sealed record AllDataRow(
    string AccountId, string Sheet, int RowNo, ProductRowData Data, int SoldCount, DateTimeOffset UpdatedAt);

/// <summary>Khoá vị trí 1 dòng (như tuple (acct, sheet, row_no) phía DB) — dùng chung mark-sold/regen/delete.</summary>
public sealed record ProductRowKey(string Acct, string Sheet, int RowNo);

/// <summary>Truy vấn 1 trang trang "📦 Dữ liệu": bộ lọc + phân trang (Offset/Limit).</summary>
public sealed record AllDataQueryRequest(AllDataFilter Filter, int Offset, int Limit);
/// <summary>Đếm + 1 trang trong 1 round-trip (client ở xa qua tunnel, đừng bắt 2 lượt).</summary>
public sealed record AllDataPage(int Total, List<AllDataRow> Rows);

/// <summary>Danh sách khoá vị trí — dùng chung mark-sold / regen-skus / delete.</summary>
public sealed record ProductKeysRequest(List<ProductRowKey> Keys);
/// <summary>Số dòng đã xử lý (đánh dấu bán / cấp lại SKU / xoá).</summary>
public sealed record ProductCountResponse(int Count);

/// <summary>Sửa 1 dòng theo khoá vị trí — Data = ĐỦ 17 ô.</summary>
public sealed record ProductUpdateRowRequest(string Acct, string Sheet, int RowNo, ProductRowData Data);
/// <summary><see cref="Ok"/>=false = không tìm thấy dòng (ví dụ đã bị xoá).</summary>
public sealed record ProductUpdateRowResponse(bool Ok);

/// <summary>Thêm 1 dòng vào CUỐI sheet — SKU trong Data trống/blank → server TỰ SINH B##### (duy nhất trong shop).</summary>
public sealed record ProductInsertRowRequest(string Acct, string Sheet, ProductRowData Data);
/// <summary>Dòng vừa thêm: <see cref="RowNo"/> server cấp + <see cref="Sku"/> cuối cùng (đã sinh nếu để trống).</summary>
public sealed record ProductInsertRowResponse(int RowNo, string Sku);

/// <summary>Có dòng KHÁC trong shop mang cùng SKU non-blank? (cảnh báo trùng trước khi lưu).</summary>
public sealed record ProductSkuExistsResponse(bool Exists);
