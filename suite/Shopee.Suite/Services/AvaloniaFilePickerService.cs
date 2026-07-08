using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Shopee.Suite.Services;

/// <summary>Impl Avalonia của <see cref="IFilePickerService"/> — dùng StorageProvider của cửa sổ chính
/// (trên Ubuntu đi qua XDG portal). Parse chuỗi filter kiểu WPF "Tên|*.ext;*.ext2|Tất cả|*.*".</summary>
public sealed class AvaloniaFilePickerService : IFilePickerService
{
    public Task<string?> OpenFileAsync(string title, string filter) => OnUi(async () =>
    {
        var sp = Storage(); if (sp is null) return null;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false, FileTypeFilter = ParseFilter(filter),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    });

    public Task<string[]> OpenFilesAsync(string title, string filter) => OnUi(async () =>
    {
        var sp = Storage(); if (sp is null) return Array.Empty<string>();
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = true, FileTypeFilter = ParseFilter(filter),
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToArray();
    });

    public Task<string?> SaveFileAsync(string title, string filter, string? defaultFileName = null, bool overwritePrompt = true) => OnUi(async () =>
    {
        var sp = Storage(); if (sp is null) return null;
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = defaultFileName, FileTypeChoices = ParseFilter(filter),
        });
        return file?.TryGetLocalPath();
    });

    public Task<string?> PickFolderAsync(string title) => OnUi(async () =>
    {
        var sp = Storage(); if (sp is null) return null;
        var dirs = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return dirs.Count > 0 ? dirs[0].TryGetLocalPath() : null;
    });

    /// <summary>Chuyển "Tên|*.ext;*.ext2|Tất cả|*.*" → danh sách FilePickerFileType. Cặp (tên, patterns).</summary>
    private static List<FilePickerFileType> ParseFilter(string filter)
    {
        var list = new List<FilePickerFileType>();
        if (string.IsNullOrWhiteSpace(filter)) return list;
        var parts = filter.Split('|');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var name = parts[i];
            var patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            list.Add(new FilePickerFileType(name) { Patterns = patterns });
        }
        return list;
    }

    private static IStorageProvider? Storage() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.StorageProvider;

    private static async Task<T> OnUi<T>(Func<Task<T>> f)
    {
        if (Dispatcher.UIThread.CheckAccess()) return await f();
        return await Dispatcher.UIThread.InvokeAsync(f);
    }
}
