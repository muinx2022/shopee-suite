using System.IO;
using CommunityToolkit.Mvvm.Input;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.Search;

/// <summary>Phần SearchViewModel: mảng NẠP FILE LINK + CHỌN LINK — chọn/xoá file nguồn, nạp danh sách link
/// để tick chọn, và hiển thị tiến độ từng link.</summary>
public sealed partial class SearchViewModel
{
    // ── Nạp file link + chọn link ───────────────────────────────────────────────
    [RelayCommand]
    private async Task ChooseFilesAsync()
    {
        var files = await FilePicker.OpenFilesAsync("Chọn file link category",
            "Text (mỗi dòng 1 link)|*.txt|Excel|*.xlsx;*.xlsm|Tất cả|*.*");
        if (files.Length == 0) return;
        _filePaths.Clear();
        _filePaths.AddRange(files);
        FilesDisplay = $"{_filePaths.Count} file: " + string.Join(", ", _filePaths.Select(Path.GetFileName));
        LoadLinks();
        SaveUiSettings();
    }

    [RelayCommand]
    private void ClearFiles()
    {
        _filePaths.Clear();
        LoadedLinks.Clear();
        FilesDisplay = "(chưa chọn file)";
        SaveUiSettings();
    }

    // Nạp link từ tất cả file đã chọn vào danh sách tick chọn (gộp lại).
    private void LoadLinks()
    {
        LoadedLinks.Clear();
        foreach (var f in _filePaths)
        {
            List<(int Row, string Link, string Status, long ShopId)> rows;
            try { rows = Db.LoadFileLinks(f).ToList(); }
            catch { rows = []; }
            foreach (var (row, link, status, _) in rows)
            {
                var item = new SearchFileLinkRow(row, link, f, status);
                item.Progress = FormatLinkProgress(Db.GetLinkProgress(link));
                LoadedLinks.Add(item);
            }
        }
        Status = $"Đã nạp {LoadedLinks.Count} link ({LoadedLinks.Count(x => x.IsSelected)} đang chọn).";
    }

    [RelayCommand]
    private void SelectAllLinks() { foreach (var l in LoadedLinks) l.IsSelected = true; }

    [RelayCommand]
    private void UnselectAllLinks() { foreach (var l in LoadedLinks) l.IsSelected = false; }

    /// <summary>Xóa các link đang tick khỏi danh sách đã nạp (không đụng file gốc; chọn lại file để nạp lại).</summary>
    [RelayCommand]
    private void RemoveSelectedLinks()
    {
        var sel = LoadedLinks.Where(l => l.IsSelected).ToList();
        if (sel.Count == 0) { Status = "Chưa tick link nào để xóa."; return; }
        foreach (var l in sel) LoadedLinks.Remove(l);
        Status = $"Đã xóa {sel.Count} link khỏi danh sách ({LoadedLinks.Count} còn lại).";
    }

    /// <summary>Định dạng tiến độ link để hiển thị ở cột "Tiến độ".</summary>
    private static string FormatLinkProgress((string Status, string Category, int Page, int CategoryIndex, int ProductCount)? p)
    {
        if (p is null) return "";
        var (st, cat, page, catIdx, count) = p.Value;
        var stVi = st switch
        {
            "Completed" => "✔ hoàn thành",
            "Running" => "▶ chưa kết thúc",
            "Failed" => "■ lỗi/dừng",
            "Stopped" => "■ dừng",
            _ => st,
        };
        var where = catIdx > 0
            ? $" · DM #{catIdx}" + (string.IsNullOrWhiteSpace(cat) ? "" : $" ({cat})") + $" · trang {page}"
            : "";
        return $"{stVi}{where} · {count} SP";
    }

    private void RefreshLinkProgress()
    {
        foreach (var l in LoadedLinks) l.Progress = FormatLinkProgress(Db.GetLinkProgress(l.Link));
    }
}
