using Microsoft.Win32;

namespace Shopee.Suite.Services;

/// <summary>Impl WPF của <see cref="IFilePickerService"/> — bọc dialog Microsoft.Win32 (đồng bộ) trong Task.</summary>
public sealed class WpfFilePickerService : IFilePickerService
{
    public Task<string?> OpenFileAsync(string title, string filter) =>
        UiThread.InvokeAsync<string?>(() =>
        {
            var dlg = new OpenFileDialog { Title = title, Filter = filter };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        });

    public Task<string[]> OpenFilesAsync(string title, string filter) =>
        UiThread.InvokeAsync(() =>
        {
            var dlg = new OpenFileDialog { Title = title, Filter = filter, Multiselect = true };
            return dlg.ShowDialog() == true ? dlg.FileNames : Array.Empty<string>();
        });

    public Task<string?> SaveFileAsync(string title, string filter, string? defaultFileName = null, bool overwritePrompt = true) =>
        UiThread.InvokeAsync<string?>(() =>
        {
            var dlg = new SaveFileDialog { Title = title, Filter = filter, OverwritePrompt = overwritePrompt };
            if (!string.IsNullOrWhiteSpace(defaultFileName)) dlg.FileName = defaultFileName;
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        });

    public Task<string?> PickFolderAsync(string title) =>
        UiThread.InvokeAsync<string?>(() =>
        {
            var dlg = new OpenFolderDialog { Title = title };
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        });
}
