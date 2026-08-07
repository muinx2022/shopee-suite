using Shopee.Core.Browser;

namespace Shopee.Core.Tests;

/// <summary>
/// Test bước DỌN model AI on-device ở phía suite: <see cref="BraveCachePolicy.PruneProfileCache"/> phải xoá thư
/// mục <c>OptGuideOnDeviceModel</c> (Chrome tự tải ~4 GB về GỐC hồ sơ) mà KHÔNG đụng cookie/đăng nhập.
/// Hàm này là thứ StartupJanitor và bước chuẩn bị hồ sơ scrape gọi cho mọi hồ sơ của suite.
/// </summary>
public class BraveCachePolicyOptGuideTests
{
    [Fact]
    public void PruneProfileCache_XoaThuMucModelAi_GiuNguyenCookie()
    {
        var root = Path.Combine(Path.GetTempPath(), "sc_optguide_" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = Path.Combine(root, "OptGuideOnDeviceModel", "2025.8.8.1141");
            Directory.CreateDirectory(model);
            File.WriteAllBytes(Path.Combine(model, "weights.bin"), new byte[4096]);

            var network = Path.Combine(root, "Default", "Network");
            Directory.CreateDirectory(network);
            File.WriteAllText(Path.Combine(network, "Cookies"), "giu-nguyen");

            var freed = BraveCachePolicy.PruneProfileCache(root);

            Assert.False(Directory.Exists(Path.Combine(root, "OptGuideOnDeviceModel")));
            Assert.True(freed >= 4096, $"phải tính được dung lượng đã giải phóng, nhận {freed}");
            // Cookie/đăng nhập là thứ TUYỆT ĐỐI không được đụng (mất là Shopee bắn captcha hàng loạt).
            Assert.Equal("giu-nguyen", File.ReadAllText(Path.Combine(network, "Cookies")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
