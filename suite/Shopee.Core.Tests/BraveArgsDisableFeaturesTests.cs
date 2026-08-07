using Shopee.Toolkit.Browser;

namespace Shopee.Core.Tests;

/// <summary>
/// Test cho luật gộp <c>--disable-features</c> ở <see cref="BraveArgs"/> — bất biến dùng CHUNG cho cả 5 nơi app
/// phóng trình duyệt (orders cầu nối + POC, Search, scrape, BrowserLauncher, BigSeller runner):
/// ĐÚNG MỘT cờ <c>--disable-features</c>, luôn có nhóm chặn tải model AI on-device (~4 GB/hồ sơ), start URL
/// vẫn nằm cuối.
/// </summary>
public class BraveArgsDisableFeaturesTests
{
    private static readonly string[] AiFeatures =
    {
        "OptimizationGuideOnDeviceModel",
        "OptimizationGuideModelDownloading",
        "TextSafetyClassifier",
    };

    private static string? CoDisableFeatures(IEnumerable<string> args) =>
        args.SingleOrDefault(a => a.StartsWith("--disable-features=", StringComparison.Ordinal));

    private static IReadOnlyList<string> Features(IEnumerable<string> args) =>
        (CoDisableFeatures(args) ?? string.Empty).Replace("--disable-features=", string.Empty).Split(',');

    [Fact]
    public void CallSiteKhongKhaiGi_VanCoDu3FeatureChanModelAi()
    {
        // BrowserLauncher (check tk / BigSeller login) và BigSellerBraveRunner (Update/Import) vốn KHÔNG có cờ
        // --disable-features nào → builder phải TỰ chèn, không thì hai nhánh đó vẫn tải model 4 GB.
        var args = BraveArgs.WindowRaw(@"C:\profiles\1").BuildList();

        var co = CoDisableFeatures(args);
        Assert.NotNull(co);
        foreach (var f in AiFeatures)
        {
            Assert.Contains(f, Features(args));
        }
    }

    [Fact]
    public void StartUrlVanODongCuoi_KhiPhaiChenCoMoi()
    {
        // Chromium đòi URL là tham số POSITIONAL cuối cùng → cờ chèn thêm phải đứng TRƯỚC nó.
        var raw = BraveArgs.WindowRaw(@"C:\p").StartUrl("https://shopee.vn/").BuildList();
        Assert.Equal("https://shopee.vn/", raw[^1]);

        // Chế độ CHUỖI: URL bị bọc ngoặc kép, vẫn phải là phần tử cuối.
        var quoted = BraveArgs.Window(@"C:\p").StartUrl("https://shopee.vn/").Build();
        Assert.EndsWith("\"https://shopee.vn/\"", quoted);
    }

    [Fact]
    public void GopMoiDisableFeaturesThanhDungMotCo()
    {
        // BẪY CHÍNH: Chromium giữ switch trong map theo tên → hai cờ --disable-features thì chỉ MỘT cái sống.
        // Mất DisableLoadExtensionCommandLineSwitch = extension ngừng nạp mà không báo lỗi.
        var args = BraveArgs.CreateRaw()
            .DisableFeatures("Translate,CalculateNativeWinOcclusion")
            .Add("--disable-features=DisableLoadExtensionCommandLineSwitch")
            .BuildList();

        Assert.Single(args, a => a.StartsWith("--disable-features=", StringComparison.Ordinal));
        var features = Features(args);
        Assert.Contains("Translate", features);
        Assert.Contains("CalculateNativeWinOcclusion", features);
        Assert.Contains("DisableLoadExtensionCommandLineSwitch", features);
        foreach (var f in AiFeatures)
        {
            Assert.Contains(f, features);
        }
    }

    [Fact]
    public void KhongTrungLapKhiCallSiteTuKhaiFeatureAi()
    {
        var args = BraveArgs.CreateRaw()
            .DisableFeatures("TextSafetyClassifier", "Translate")
            .BuildList();

        var features = Features(args);
        Assert.Equal(1, features.Count(f => f == "TextSafetyClassifier"));
        // Thứ tự: feature của call-site trước, nhóm AI còn thiếu nối sau.
        Assert.Equal(
            new[]
            {
                "TextSafetyClassifier", "Translate",
                "OptimizationGuideOnDeviceModel", "OptimizationGuideModelDownloading",
            },
            features);
    }

    [Fact]
    public void GoiBuildHaiLan_KetQuaGiongNhau()
    {
        // Chuẩn hoá là hàm THUẦN, không được tích luỹ vào builder (gọi Build rồi BuildList vẫn phải khớp).
        var b = BraveArgs.WindowRaw(@"C:\p").DisableFeatures("Translate").StartUrl("https://shopee.vn/");

        var lan1 = b.BuildList();
        var lan2 = b.BuildList();

        Assert.Equal(lan1, lan2);
        Assert.Single(lan2, a => a.StartsWith("--disable-features=", StringComparison.Ordinal));
    }
}
