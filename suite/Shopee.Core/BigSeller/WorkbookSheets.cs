using System.IO.Compression;
using System.Xml.Linq;
using ClosedXML.Excel;

namespace Shopee.Core.BigSeller;

/// <summary>Đọc danh sách tên sheet trong một file Excel (workbook) để chọn sheet cho shop.</summary>
public static class WorkbookSheets
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static List<string> ListSheetNames(string workbookPath)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(workbookPath) || !File.Exists(workbookPath))
            return names;

        // Nhanh: .xlsx/.xlsm là file zip — chỉ đọc entry nhỏ xl/workbook.xml để lấy tên sheet,
        // KHÔNG nạp toàn bộ workbook (ClosedXML mở cả file → chậm vài giây với file lớn, làm
        // app khởi động lâu vì sheet được liệt kê ngay lúc tạo ViewModel).
        try
        {
            using var zip = ZipFile.OpenRead(workbookPath);
            var entry = zip.GetEntry("xl/workbook.xml");
            if (entry is not null)
            {
                using var s = entry.Open();
                var doc = XDocument.Load(s);
                foreach (var sheet in doc.Descendants(Ns + "sheet"))
                {
                    var name = sheet.Attribute("name")?.Value;
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
                if (names.Count > 0) return names;
            }
        }
        catch { names.Clear(); }

        // Fallback (file không phải zip chuẩn / hỏng entry): dùng ClosedXML cho chắc.
        try
        {
            using var wb = new XLWorkbook(workbookPath);
            foreach (var ws in wb.Worksheets)
                names.Add(ws.Name);
        }
        catch { }
        return names;
    }
}
