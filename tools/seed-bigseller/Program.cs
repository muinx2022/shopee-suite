using ClosedXML.Excel;
using Shopee.Core.BigSeller;

// Auto-map sheet cho các shop còn ShopeeDataSheet rỗng: chỉ map khi workbook tồn tại VÀ có sheet trùng
// TÊN shop (case-insensitive) → an toàn, không đoán bừa. Bù lại data cũ bị mất sheet do bug combo.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var mapped = 0;
foreach (var a in BigSellerStore.Shared.Accounts)
{
    var empties = a.Shops.Where(s => string.IsNullOrWhiteSpace(s.ShopeeDataSheet)).ToList();
    if (empties.Count == 0) continue;

    if (string.IsNullOrWhiteSpace(a.WorkbookPath) || !File.Exists(a.WorkbookPath))
    {
        Console.WriteLine($"SKIP {a.Email}: workbook không tồn tại ({a.WorkbookPath})");
        continue;
    }

    List<string> sheets;
    try
    {
        using var wb = new XLWorkbook(a.WorkbookPath);
        sheets = wb.Worksheets.Select(w => w.Name).ToList();
    }
    catch (Exception ex) { Console.WriteLine($"SKIP {a.Email}: lỗi đọc workbook — {ex.Message}"); continue; }

    foreach (var s in empties)
    {
        var match = sheets.FirstOrDefault(sh => string.Equals(sh.Trim(), s.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            s.ShopeeDataSheet = match;
            mapped++;
            Console.WriteLine($"MAP  {a.Email}: shop \"{s.Name}\" → sheet \"{match}\"");
        }
        else
        {
            Console.WriteLine($"NO-MATCH {a.Email}: shop \"{s.Name}\" (sheets: {string.Join(", ", sheets)})");
        }
    }
}

if (mapped > 0)
{
    BigSellerStore.Shared.Save();
    Console.WriteLine($"\nĐã map {mapped} shop → sheet và lưu.");
}
else
{
    Console.WriteLine("\nKhông có shop nào map được (đều đã có sheet hoặc thiếu workbook).");
}
Console.WriteLine($"Tổng BigSeller: {BigSellerStore.Shared.Accounts.Count}");
