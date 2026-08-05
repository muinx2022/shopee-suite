using System.IO;
using System.Text.Json;
using Shopee.Core.Infrastructure;

namespace Shopee.Suite.Modules.Search;

/// <summary>Phần SearchViewModel: mảng LƯU/NẠP CẤU HÌNH UI nhỏ của module Search
/// (file <c>search-ui.json</c>: đường dẫn docx, thư mục xuất, khu vực, danh sách file link).</summary>
public sealed partial class SearchViewModel
{
    // ── Lưu/nạp cấu hình UI nhỏ ─────────────────────────────────────────────────
    private static string UiSettingsPath => Path.Combine(SuitePaths.ModuleDir("search"), "search-ui.json");
    private sealed record UiSettings(string Docx, string Output, string? Region = null, List<string>? FilePaths = null);
    private bool _loadingUi;

    private void LoadUiSettings()
    {
        _loadingUi = true;
        try
        {
            if (File.Exists(UiSettingsPath) &&
                JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(UiSettingsPath)) is { } j)
            {
                // Gán THẲNG field (không qua property) để KHÔNG bắn PropertyChanged/OnChanged khi đang nạp UI.
#pragma warning disable MVVMTK0034
                if (!string.IsNullOrWhiteSpace(j.Docx)) _categoryDocxPath = j.Docx;
                if (!string.IsNullOrWhiteSpace(j.Output)) _outputDir = j.Output;
                if (!string.IsNullOrWhiteSpace(j.Region)) _region = j.Region;
#pragma warning restore MVVMTK0034
                if (j.FilePaths is { } fps)
                {
                    foreach (var f in fps) if (!string.IsNullOrWhiteSpace(f) && File.Exists(f)) _filePaths.Add(f);
                    if (_filePaths.Count > 0)
                        FilesDisplay = $"{_filePaths.Count} file: " + string.Join(", ", _filePaths.Select(Path.GetFileName));
                }
            }
        }
        catch { }
        finally { _loadingUi = false; }
    }

    private void SaveUiSettings()
    {
        if (_loadingUi) return;
        try
        {
            Directory.CreateDirectory(SuitePaths.ModuleDir("search"));
            File.WriteAllText(UiSettingsPath,
                JsonSerializer.Serialize(new UiSettings(CategoryDocxPath, OutputDir, Region, _filePaths.ToList())));
        }
        catch { }
    }
}
