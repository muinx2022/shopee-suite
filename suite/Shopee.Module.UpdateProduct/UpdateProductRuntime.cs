using UpdateProduct;

namespace Shopee.Modules.UpdateProduct;

/// <summary>Khởi tạo runtime engine update-product (port block, repo root chứa update-product-python).</summary>
public static class UpdateProductRuntime
{
    public static void Initialize() => AppSession.Initialize();
}
