namespace Shopee.Core.Infrastructure;

/// <summary>
/// Vị trí dữ liệu dùng chung của Shopee Suite. Mọi dữ liệu người dùng (settings, profile, file
/// kết quả) nằm dưới <see cref="Root"/> = %AppData%\ShopeeSuite\&lt;module&gt; để không bị xoá khi
/// build lại, và để các module chia sẻ một gốc thống nhất.
/// </summary>
public static class SuitePaths
{
    /// <summary>%AppData%\ShopeeSuite</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShopeeSuite");

    /// <summary>Thư mục dữ liệu riêng của một module (tự tạo nếu chưa có).</summary>
    public static string ModuleDir(string module)
    {
        var dir = Path.Combine(Root, module);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>%AppData%\ShopeeStatApp — dữ liệu của app shopee-stat cũ (đích copy account).</summary>
    public static string ShopeeStatDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShopeeStatApp");

    /// <summary>
    /// Repo root chứa cả <c>shopee-stat</c> lẫn <c>open-multi-brave-v31</c> — để biết nơi đăng ký
    /// account + copy profile sang các app cũ. Đi ngược từ thư mục chạy lên tới khi thấy cả hai.
    /// </summary>
    public static string RepoRoot { get; } = ResolveRepoRoot();

    private static string ResolveRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "shopee-stat")) &&
                Directory.Exists(Path.Combine(dir.FullName, "open-multi-brave-v31")))
                return dir.FullName;
        }
        return AppContext.BaseDirectory;
    }
}
