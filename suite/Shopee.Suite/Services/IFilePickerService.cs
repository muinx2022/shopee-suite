namespace Shopee.Suite.Services;

/// <summary>
/// Chọn file/thư mục, không dính framework UI để ViewModel dùng chung WPF ↔ Avalonia.
/// <paramref name="filter"/> theo cú pháp WPF quen thuộc: "Tên|*.ext;*.ext2|Tất cả|*.*"
/// (impl Avalonia tự parse). Trả null khi người dùng hủy.
/// </summary>
public interface IFilePickerService
{
    Task<string?> OpenFileAsync(string title, string filter);

    /// <summary>Chọn nhiều file. Trả mảng rỗng khi hủy.</summary>
    Task<string[]> OpenFilesAsync(string title, string filter);

    Task<string?> SaveFileAsync(string title, string filter, string? defaultFileName = null, bool overwritePrompt = true);

    Task<string?> PickFolderAsync(string title);
}
