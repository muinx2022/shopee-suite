using Shopee.Core.Infrastructure;

namespace UpdateProduct;

/// <summary>
/// Phiên runtime của module Update Product. Thân nằm ở <see cref="AppSession"/> (Shopee.Core) — trước đây là
/// bản chép tay riêng của module. Probe 10000/10400 (dải cổng debug BigSeller, xem <see cref="BigSellerPorts"/>)
/// và 8112 theo CHỈ SỐ block (1 cổng API/1 block).
/// </summary>
internal static class UpdateProductSession
{
    public static AppSession Current { get; } = new(new AppSessionOptions
    {
        ProbePortsAtOffset = [10000, 10400],
        ProbePortsAtBlockIndex = [8112],
        CreatePersistentDataDir = true,
        NoFreeBlockMessage = "Khong tim duoc block port trong cho Update Product.",
    });
}

/// <summary>Pool cổng debug Brave của Update Product: 10000 + PortOffset, 400 cổng. Lớp RIÊNG (không gộp vào
/// <see cref="UpdateProductSession"/>): static ctor chỉ chạy khi lần đầu chạm tới, tức SAU khi phiên đã
/// Initialize và PortOffset đã có — gộp chung một lớp sẽ dựng pool với offset 0.</summary>
internal static class BigSellerPorts
{
    public static PortAllocator Shared { get; } = new(10000, 400, UpdateProductSession.Current, "BigSeller");
}
