namespace ShopeeStatApp.Services;

/// <summary>
/// Đọc danh sách link từ file và theo dõi trạng thái xử lý từng dòng để resume.
/// Hỗ trợ 2 định dạng:
///  - <b>.txt</b> (mới): mỗi dòng 1 link; trạng thái lưu ở file sidecar "&lt;path&gt;.status.tsv".
///  - <b>.xlsx</b> (cũ): link nằm trong 1 cột; trạng thái ghi vào cột "status" cuối bảng.
/// </summary>
public sealed class LinkFileStore
{
    public const string Processing = "processing";
    public const string Processed = "Processed";

    public sealed record LinkRow(int RowNumber, string Link, string Status)
    {
        public bool IsDone => string.Equals(Status, Processed, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex LinkRx = new(@"-i\.\d+\.\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Path { get; }
    public int StatusColumn { get; private set; }
    public string SheetName { get; private set; } = "";

    private readonly bool _isTxt;

    public LinkFileStore(string path)
    {
        Path = path;
        _isTxt = path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private string StatusSidecarPath => Path + ".status.tsv";

    /// <summary>Loads all rows that contain a link, plus their current status.</summary>
    public List<LinkRow> Load() => _isTxt ? LoadTxt() : LoadXlsx();

    /// <summary>Clears every status (reset resume state).</summary>
    public void ClearAllStatuses()
    {
        if (_isTxt)
        {
            try { if (File.Exists(StatusSidecarPath)) File.Delete(StatusSidecarPath); } catch { }
            return;
        }
        ClearAllStatusesXlsx();
    }

    /// <summary>Writes a status value for a row and persists it.</summary>
    public void MarkStatus(int rowNumber, string status)
    {
        if (_isTxt) { MarkStatusTxt(rowNumber, status); return; }
        MarkStatusXlsx(rowNumber, status);
    }

    // ── .txt (mỗi dòng 1 link) ──────────────────────────────────────────────────
    private List<LinkRow> LoadTxt()
    {
        var statuses = ReadTxtStatuses();
        var rows = new List<LinkRow>();
        var lines = File.ReadAllLines(Path);
        for (var i = 0; i < lines.Length; i++)
        {
            var link = lines[i].Trim();
            if (link.Length == 0 || link.StartsWith('#')) continue;
            if (!link.Contains("shopee", StringComparison.OrdinalIgnoreCase)) continue;
            var row = i + 1;   // 1-based line number
            statuses.TryGetValue(row, out var st);
            rows.Add(new LinkRow(row, link, st ?? ""));
        }
        return rows;
    }

    private Dictionary<int, string> ReadTxtStatuses()
    {
        var map = new Dictionary<int, string>();
        try
        {
            if (!File.Exists(StatusSidecarPath)) return map;
            foreach (var line in File.ReadAllLines(StatusSidecarPath))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                if (int.TryParse(line[..tab], out var row))
                    map[row] = line[(tab + 1)..];
            }
        }
        catch { }
        return map;
    }

    private void MarkStatusTxt(int rowNumber, string status)
    {
        try
        {
            var map = ReadTxtStatuses();
            map[rowNumber] = status;
            var lines = map.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}\t{kv.Value}");
            var tmp = StatusSidecarPath + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, StatusSidecarPath, overwrite: true);
        }
        catch { }
    }

    // ── .xlsx (giữ tương thích) ─────────────────────────────────────────────────
    private List<LinkRow> LoadXlsx()
    {
        using var wb = new XLWorkbook(Path);
        var ws = wb.Worksheets.First();
        SheetName = ws.Name;

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastRow == 0 || lastCol == 0)
        {
            StatusColumn = 1;
            return [];
        }

        StatusColumn = ResolveStatusColumn(ws, lastRow, lastCol);

        var rows = new List<LinkRow>();
        for (var r = 1; r <= lastRow; r++)
        {
            var link = FindLinkInRow(ws, r, lastCol);
            if (string.IsNullOrWhiteSpace(link)) continue;
            var status = ws.Cell(r, StatusColumn).GetString().Trim();
            rows.Add(new LinkRow(r, link, status));
        }
        return rows;
    }

    private void ClearAllStatusesXlsx()
    {
        using var wb = new XLWorkbook(Path);
        var ws = wb.Worksheets.First();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastRow == 0 || lastCol == 0) return;

        var statusCol = ResolveStatusColumn(ws, lastRow, lastCol);
        for (var r = 1; r <= lastRow; r++)
            ws.Cell(r, statusCol).Value = "";
        wb.Save();
    }

    private void MarkStatusXlsx(int rowNumber, string status)
    {
        using var wb = new XLWorkbook(Path);
        var ws = wb.Worksheets.First();
        var col = StatusColumn > 0 ? StatusColumn : (ws.LastColumnUsed()?.ColumnNumber() ?? 1) + 1;
        ws.Cell(rowNumber, col).Value = status;
        wb.Save();
    }

    private static int ResolveStatusColumn(IXLWorksheet ws, int lastRow, int lastCol)
    {
        // Reuse the last column if it already holds only status-like values (a re-run),
        // otherwise put the status in a fresh column right after the data.
        return IsStatusColumn(ws, lastCol, lastRow) ? lastCol : lastCol + 1;
    }

    private static bool IsStatusColumn(IXLWorksheet ws, int col, int lastRow)
    {
        var sawAny = false;
        for (var r = 1; r <= lastRow; r++)
        {
            var v = ws.Cell(r, col).GetString().Trim();
            if (v.Length == 0) continue;
            sawAny = true;
            var isStatus = v.Equals(Processing, StringComparison.OrdinalIgnoreCase)
                || v.Equals(Processed, StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("Trạng thái", StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase);
            if (!isStatus) return false;
        }
        return sawAny;
    }

    private static string FindLinkInRow(IXLWorksheet ws, int row, int lastCol)
    {
        for (var c = 1; c <= lastCol; c++)
        {
            var v = ws.Cell(row, c).GetString().Trim();
            if (v.Length == 0) continue;
            if (LinkRx.IsMatch(v) || v.Contains("shopee.vn/", StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return "";
    }
}
