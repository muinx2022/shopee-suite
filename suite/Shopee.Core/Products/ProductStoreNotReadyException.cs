namespace Shopee.Core.Products;

/// <summary>Kho Postgres chưa sẵn sàng (hub chưa cấu hình HUB_PG_CONN / client gặp 503 pg-not-ready). Hai hiện
/// thực <see cref="IProductDataOps"/> đều ném KIỂU NÀY để <see cref="ProductGridEngine"/> chỉ cần bắt 1 loại
/// → đặt PgReady=false + lưới rỗng thay vì hiện lỗi thô.</summary>
public sealed class ProductStoreNotReadyException : Exception
{
}
