using System;
using System.IO;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test phần THUẦN hệ-điều-hành của <see cref="OrdersBridgeLauncher"/>: chép extension ra bản mới.
/// Bối cảnh: <c>background.js</c> của <c>extensions/shopee-orders</c> là ES module <c>import</c> từ
/// <c>./shared/*.js</c>. Bản chép chỉ-file-top-level (lỗi 2026-07-30 → 2026-08-02) làm service worker chết
/// lúc nạp ⇒ extension không gửi <c>ready</c> ⇒ cầu nối treo hết 45s rồi báo "hết thời gian chờ".
/// </summary>
public class OrdersBridgeLauncherTests
{
    /// <summary>Cấp một thư mục tạm cho test, tự dọn khi Dispose.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"xlds_ext_{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* bỏ qua lỗi dọn */ }
        }
    }

    [Fact]
    public void CopyDirectory_ChepCaThuMucCon_KhongRungShared()
    {
        using var root = new TempDir();
        var src = Path.Combine(root.Path, "shopee-orders");
        Directory.CreateDirectory(Path.Combine(src, "shared"));
        File.WriteAllText(Path.Combine(src, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(src, "background.js"), "import { sleep } from './shared/util.js';");
        File.WriteAllText(Path.Combine(src, "shared", "util.js"), "export const sleep = () => {};");

        var dest = Path.Combine(root.Path, "copy");
        OrdersBridgeLauncher.CopyDirectory(src, dest);

        Assert.True(File.Exists(Path.Combine(dest, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(dest, "background.js")));
        // Chặng quan trọng: thư mục con shared/ PHẢI theo sang, không thì SW MV3 chết lúc import.
        Assert.True(File.Exists(Path.Combine(dest, "shared", "util.js")));
        Assert.Equal("export const sleep = () => {};", File.ReadAllText(Path.Combine(dest, "shared", "util.js")));
    }

    [Fact]
    public void CopyDirectory_ChepLong_NhieuCap()
    {
        using var root = new TempDir();
        var src = Path.Combine(root.Path, "src");
        Directory.CreateDirectory(Path.Combine(src, "a", "b"));
        File.WriteAllText(Path.Combine(src, "a", "b", "sau.js"), "sâu");

        var dest = Path.Combine(root.Path, "dest");
        OrdersBridgeLauncher.CopyDirectory(src, dest);

        Assert.Equal("sâu", File.ReadAllText(Path.Combine(dest, "a", "b", "sau.js")));
    }
}
