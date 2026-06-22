namespace OpenMultiBraveLauncherV3;

internal static class RunnerExtensionPaths
{
    public const string ExtensionDisplayName = "Shopee Data Runner";

    /// <summary>Thư mục unpacked extension đủ file (background.js) để --load-extension.</summary>
    public static string? ResolveLoadDirectory()
    {
        foreach (var dir in CandidateDirectories())
        {
            if (File.Exists(Path.Combine(dir, "background.js")))
                return dir;
        }

        return null;
    }

    public static string? TryGetLoadedExtensionId()
    {
        var dir = ResolveLoadDirectory();
        return dir is null ? null : UnpackedExtensionId.FromPath(dir);
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "extensions", "shopee-scrape");

        var repo = FindRepoRoot();
        if (repo is not null)
            yield return Path.Combine(repo, "extensions", "shopee-scrape");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // Mốc repo root = file solution (Python đã bỏ nên không dùng api/main.py nữa).
            if (File.Exists(Path.Combine(dir.FullName, "ShopeeSuite.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
