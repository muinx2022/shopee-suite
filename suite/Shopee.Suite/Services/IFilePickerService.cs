namespace Shopee.Suite.Services;

/// <summary>
/// Chọn file/thư mục, không dính framework UI để ViewModel không phải biết tầng view.
/// <paramref name="filter"/> theo cú pháp WPF: "Tên|*.ext;*.ext2|Tất cả|*.*". Trả null khi người dùng hủy.
/// </summary>
public interface IFilePickerService
{
    Task<string?> OpenFileAsync(string title, string filter);

    /// <summary>Chọn nhiều file. Trả mảng rỗng khi hủy.</summary>
    Task<string[]> OpenFilesAsync(string title, string filter);

    Task<string?> SaveFileAsync(string title, string filter, string? defaultFileName = null, bool overwritePrompt = true);

    Task<string?> PickFolderAsync(string title);
}
