using Shopee.Core.Infrastructure;

namespace OpenMultiBraveLauncherV3;

/// <summary>
/// Phiên runtime của module Scrape (engine v3). Thân nằm ở <see cref="AppSession"/> (Shopee.Core) — trước đây
/// là bản chép tay riêng của module. Dải cổng của phiên: Shopee instance 9330+ (600 cổng, xem
/// <see cref="ScrapePorts"/>); probe thêm 9430/9700 và dải 10000/10400 của Update Product để một BLOCK chỉ được
/// nhận khi cả bản đồ cổng của suite còn trống.
/// </summary>
internal static class ScrapeSession
{
    public static AppSession Current { get; } = new(new AppSessionOptions
    {
        ProbePortsAtOffset = [9330, 9430, 10000, 10400, 9700],
        NoFreeBlockMessage = "Khong tim duoc block port trong cho phien v3 moi.",
    });
}

/// <summary>Pool cổng debug Brave của Scrape: 9330 + PortOffset, 600 cổng. Lớp RIÊNG (không gộp vào
/// <see cref="ScrapeSession"/>): static ctor chỉ chạy khi lần đầu chạm tới, tức SAU khi phiên đã Initialize
/// và PortOffset đã có — gộp chung một lớp sẽ dựng pool với offset 0.</summary>
internal static class ScrapePorts
{
    public static PortAllocator Shared { get; } = new(9330, 600, ScrapeSession.Current, "Shopee instance");
}
