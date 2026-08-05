// Hằng cấu hình của cầu nối Đơn hàng: URL Seller Centre, trần quét, regex nhận nhãn.
// Tách khỏi background.js đợt 2026-08-06 — nội dung GIỮ NGUYÊN, chỉ thêm `export`.

export const BANHANG_HOSTS = ["banhang.shopee.vn"];
export const SUBACCOUNT_HOSTS = ["subaccount.shopee.com", "accounts.shopee.vn"];
export const SHOP_LIST_URL = "https://banhang.shopee.vn/portal/shop";
export const ORDERS_URL = "https://banhang.shopee.vn/portal/sale/order";
export const ORDER_DETAIL_PREFIX = "https://banhang.shopee.vn/portal/sale/order/"; // + shopeeOrderId → trang chi tiết (đọc "Số tiền cuối cùng").
export const SHIPPING_SETTINGS_URL = "https://banhang.shopee.vn/portal/all-settings/shipping";
export const RETURNS_URL = "https://banhang.shopee.vn/portal/sale/returnrefundcancel"; // trang "Trả hàng/Hoàn tiền/Hủy".
export const MAX_ORDER_PAGES = 10; // chốt chặn số trang quét (khớp MaxSyncPages phía C#).
export const MAX_ORDER_PRODUCTS = 20;     // trần số sản phẩm đọc/đơn ở trang chi tiết (vượt → cắt + cờ bicat cho C# log).
export const MAX_RETURN_ROWS = 50;        // trần số dòng trả hàng đọc/lượt (chỉ trang ĐẦU, không phân trang).
export const MAX_RETURN_HEAD_HTML = 4000; // trần ký tự HTML mỗi dòng gửi về C# (giữ payload gọn).
// Mục sắp xếp cần chọn, so trên text KHÔNG dấu (_na): "Ngày yêu cầu (Mới - Cũ)". Ràng buộc "moi - cu" để KHÔNG
// dính nhầm mục ngược "Ngày yêu cầu (Cũ - Mới)".
export const SORT_NEWEST_RE = "ngay yeu cau.*moi\\s*[-–]\\s*cu";
// Tab cần chọn trên tab-strip trang trả hàng, so trên text KHÔNG dấu (_na): "Đơn Trả hàng Hoàn tiền". Tab-strip
// KHÔNG có data-testid nên buộc phải nhận theo TEXT — ngoại lệ so với tab điều hướng trái (pageLocateReturnTab
// vẫn dùng data-testid). Không chọn tab thì ô tổng là của tab mặc định "Tất cả", gộp cả Đơn Hủy / Đơn Giao hàng
// không thành công — hai loại KHÔNG có mã yêu cầu trả hàng, nên số phồng lên và lượt quét về tay không.
export const RETURN_TAB_RE = "don tra hang hoan tien";
