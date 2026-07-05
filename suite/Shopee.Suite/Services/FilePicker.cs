namespace Shopee.Suite.Services;

/// <summary>Facade tĩnh chọn file/thư mục (khớp phong cách .Shared của codebase). Ruột swap khi chuyển Avalonia.</summary>
public static class FilePicker
{
    public static IFilePickerService Service { get; set; } = new WpfFilePickerService();

    public static Task<string?> OpenFileAsync(string title, string filter) => Service.OpenFileAsync(title, filter);
    public static Task<string[]> OpenFilesAsync(string title, string filter) => Service.OpenFilesAsync(title, filter);
    public static Task<string?> SaveFileAsync(string title, string filter, string? defaultFileName = null, bool overwritePrompt = true) =>
        Service.SaveFileAsync(title, filter, defaultFileName, overwritePrompt);
    public static Task<string?> PickFolderAsync(string title) => Service.PickFolderAsync(title);
}
