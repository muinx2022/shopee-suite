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

    // Khóa ghi THEO FILE (dùng chung cho mọi instance trong process): nhiều lane cùng đánh dấu một file link
    // — mỗi lượt là đọc-sửa-ghi cả file — chồng nhau thì mất dấu hoặc hỏng workbook. Khóa theo đường dẫn đầy
    // đủ, không phân biệt hoa/thường (Windows). KHÔNG chống được file bị process khác khóa: lỗi đó ném ra
    // ngoài cho người gọi báo lên UI.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public LinkFileStore(string path)
    {
        Path = path;
        _isTxt = path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private object WriteLock => FileLocks.GetOrAdd(LockKey(Path), _ => new object());

    private static string LockKey(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch { return path; }   // đường dẫn dị dạng: khóa theo chuỗi thô còn hơn không khóa
    }

    private string StatusSidecarPath => Path + ".status.tsv";

    /// <summary>Loads all rows that contain a link, plus their current status.</summary>
    public List<LinkRow> Load() => _isTxt ? LoadTxt() : LoadXlsx();

    /// <summary>Clears every status (reset resume state).</summary>
    public void ClearAllStatuses()
    {
        lock (WriteLock)
        {
            if (_isTxt)
            {
                try { if (File.Exists(StatusSidecarPath)) File.Delete(StatusSidecarPath); } catch { }
                return;
            }
            ClearAllStatusesXlsx();
        }
    }

    /// <summary>Writes a status value for a row and persists it. Ném lỗi ra ngoài (file đang bị khóa/mở
    /// trong Excel) — người gọi phải báo lên UI, nuốt lặng là mất dấu "đã xử lý" không ai biết.</summary>
    public void MarkStatus(int rowNumber, string status)
    {
        lock (WriteLock)
        {
            if (_isTxt) { MarkStatusTxt(rowNumber, status); return; }
            MarkStatusXlsx(rowNumber, status);
        }
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
        var map = ReadTxtStatuses();
        map[rowNumber] = status;
        var lines = map.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}\t{kv.Value}");
        // Tên tmp DUY NHẤT mỗi lượt ghi: khóa ở trên chỉ chặn trong process, còn hai process cùng ghi một
        // đường tmp thì file rename ra là bản trộn.
        var tmp = $"{StatusSidecarPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, StatusSidecarPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
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
            rows.Add(new LinkRow(r, link, ReadStatus(ws, r, StatusColumn, lastCol)));
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

        // Xoá CẢ DÃY cột trạng thái: file do bản cũ đẻ thêm cột mỗi lượt đánh dấu, chỉ xoá một cột thì
        // "đặt lại" xong đọc lên vẫn còn Processed ở các cột thừa.
        var statusCol = ResolveStatusColumn(ws, lastRow, lastCol);
        for (var r = 1; r <= lastRow; r++)
            for (var c = statusCol; c <= lastCol; c++)
                ws.Cell(r, c).Value = "";
        wb.Save();
    }

    private void MarkStatusXlsx(int rowNumber, string status)
    {
        using var wb = new XLWorkbook(Path);
        var ws = wb.Worksheets.First();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        // Chưa Load() thì tự dò lại cột trạng thái NHƯ Load — bản cũ lấy "cột cuối + 1" nên mỗi lượt đánh
        // dấu đẻ ra một cột mới, nạp lại chỉ đọc cột cuối ⇒ mất sạch dấu của các lượt trước.
        var col = StatusColumn > 0 ? StatusColumn : ResolveStatusColumn(ws, lastRow, Math.Max(lastCol, 1));
        ws.Cell(rowNumber, col).Value = status;
        wb.Save();
    }

    /// <summary>Cột trạng thái = cột TRÁI NHẤT của dãy cột "toàn giá trị trạng thái" liền nhau ở cuối bảng.
    /// Không có dãy nào (lượt đầu) → cột trống ngay sau dữ liệu. Nhờ vậy file đã bị bản cũ đẻ nhiều cột vẫn
    /// quy về đúng một cột để ghi, các cột thừa để nguyên và vẫn được đọc gộp (xem <see cref="ReadStatus"/>).</summary>
    private static int ResolveStatusColumn(IXLWorksheet ws, int lastRow, int lastCol)
    {
        var col = lastCol + 1;
        for (var c = lastCol; c >= 1 && IsStatusColumn(ws, c, lastRow); c--)
            col = c;
        return col;
    }

    /// <summary>Trạng thái của một dòng = giá trị khác rỗng ĐẦU TIÊN trong dãy cột trạng thái — dấu cũ có
    /// thể nằm ở bất kỳ cột thừa nào do bản cũ sinh ra.</summary>
    private static string ReadStatus(IXLWorksheet ws, int row, int from, int to)
    {
        for (var c = from; c <= to; c++)
        {
            var v = ws.Cell(row, c).GetString().Trim();
            if (v.Length > 0) return v;
        }
        return "";
    }

    private static bool IsStatusColumn(IXLWorksheet ws, int col, int lastRow)
    {
        var sawAny = false;
        for (var r = 1; r <= lastRow; r++)
        {
            var v = ws.Cell(r, col).GetString().Trim();
            if (v.Length == 0) continue;
            sawAny = true;
            // CHỈ nhận giá trị do CHÍNH APP ghi + ô tiêu đề "Trạng thái" (ở BẤT KỲ hàng nào: file user hay
            // có dòng tiêu đề bảng ở hàng 1, tên cột nằm hàng 2 — khoá cứng hàng 1 là các file đó mất sạch
            // dấu). Nhận cả tiền tố "Lỗi" như trước thì cột GHI CHÚ của user ("Lỗi ảnh dòng 2"…) bị nhận
            // nhầm thành cột trạng thái → MarkStatus GHI ĐÈ mất ghi chú, ClearAllStatuses xoá lan sang,
            // ReadStatus lấy nhầm ghi chú che mất dấu Processed thật.
            var isStatus = v.Equals(Processing, StringComparison.OrdinalIgnoreCase)
                || v.Equals(Processed, StringComparison.OrdinalIgnoreCase)
                || v.StartsWith("Trạng thái", StringComparison.OrdinalIgnoreCase);
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
