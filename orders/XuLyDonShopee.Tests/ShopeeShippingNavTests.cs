using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test các hàm thuần so khớp menu/tab của <see cref="ShopeeShippingNav"/> — dùng để điều hướng tới
/// "Cài Đặt Vận Chuyển" → tab "Địa Chỉ". Text lấy từ DOM có thể kèm xuống dòng / khoảng trắng thừa / rác
/// badge nên mọi so khớp phải chuẩn hóa trước.
/// </summary>
public class ShopeeShippingNavTests
{
    // ===== NormalizeUiText: null/rỗng/space thừa/xuống dòng/tab/hoa-thường =====
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Hello", "hello")]
    [InlineData("  Hello   World  ", "hello world")]
    [InlineData("Cài\nĐặt\tVận  Chuyển", "cài đặt vận chuyển")]
    [InlineData("CÀI ĐẶT VẬN CHUYỂN\n", "cài đặt vận chuyển")]
    public void NormalizeUiText_ChuanHoaDung(string? input, string expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.NormalizeUiText(input));
    }

    // ===== IsShippingSettingHref =====
    [Theory]
    [InlineData("/portal/all-settings/shipping", true)]
    [InlineData("https://banhang.shopee.vn/portal/all-settings/shipping?x=1", true)]
    [InlineData("/portal/all-settings/address", false)]
    [InlineData("/portal/sale/order", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsShippingSettingHref_DungSai(string? href, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsShippingSettingHref(href));
    }

    // ===== IsShippingSettingText: khớp cả có space thừa / xuống dòng / hoa-thường =====
    [Theory]
    [InlineData("Cài Đặt Vận Chuyển", true)]
    [InlineData("CÀI đặt  vận chuyển\n", true)]
    [InlineData("  cài đặt vận chuyển  ", true)]
    [InlineData("Cài Đặt", false)]
    [InlineData("Quản Lý Đơn Hàng", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsShippingSettingText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsShippingSettingText(input));
    }

    // ===== IsOrderMenuText =====
    [Theory]
    [InlineData("Quản Lý Đơn Hàng", true)]
    [InlineData("  quản lý  đơn hàng \n", true)]
    [InlineData("Cài Đặt Vận Chuyển", false)]
    [InlineData("Quản Lý", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOrderMenuText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsOrderMenuText(input));
    }

    // ===== IsAddressTabText: "chứa" địa chỉ (InnerText tab có thể kèm rác badge) =====
    [Theory]
    [InlineData("Địa Chỉ", true)]
    [InlineData("địa chỉ", true)]
    [InlineData("Địa Chỉ\n", true)]
    [InlineData("Địa Chỉ 99+", true)]        // kèm text badge
    [InlineData("Địa Chỉ\n12", true)]        // badge trên dòng khác
    [InlineData("Đơn vị vận chuyển", false)]
    [InlineData("Chứng từ vận chuyển", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAddressTabText_ChuaDiaChi(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsAddressTabText(input));
    }

    // ===== ParseLinkReadiness: chuỗi trạng thái từ JS → enum (chuẩn hóa hoa-thường/space/xuống dòng);
    //        null / rỗng / giá trị lạ → Unknown =====
    [Theory]
    [InlineData("ready", LinkReadiness.Ready)]
    [InlineData("Ready ", LinkReadiness.Ready)]
    [InlineData("collapsed", LinkReadiness.Collapsed)]
    [InlineData("COLLAPSED\n", LinkReadiness.Collapsed)]
    [InlineData("covered", LinkReadiness.Covered)]
    [InlineData("  Covered  ", LinkReadiness.Covered)]
    [InlineData("unknown", LinkReadiness.Unknown)]
    [InlineData("gibberish", LinkReadiness.Unknown)]
    [InlineData("", LinkReadiness.Unknown)]
    [InlineData(null, LinkReadiness.Unknown)]
    public void ParseLinkReadiness_ChuoiVeEnum(string? input, LinkReadiness expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.ParseLinkReadiness(input));
    }

    // ===== ProvinceCoreName: tên lõi tỉnh từ Account.PickupAddress (bỏ tiền tố loại đơn vị) =====
    [Theory]
    [InlineData("Hà Nội", "hà nội")]
    [InlineData("TP Hồ Chí Minh", "hồ chí minh")]
    [InlineData("Thanh Hóa", "thanh hóa")]
    [InlineData("Thành phố Hà Nội", "hà nội")]
    [InlineData("Tỉnh Thanh Hóa", "thanh hóa")]
    [InlineData("TP. Hồ Chí Minh", "hồ chí minh")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void ProvinceCoreName_BoTienTo(string? input, string expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.ProvinceCoreName(input));
    }

    // ===== AddressDetailMatchesProvince: khớp trên DÒNG CUỐI không rỗng của .detail =====
    [Theory]
    // ví dụ người dùng: địa chỉ Thanh Hóa 3 dòng → khớp "Thanh Hóa", KHÔNG khớp "Hà Nội"
    [InlineData("xom thang , dong quang\nPhường Đông Sơn\nTỉnh Thanh Hóa", "Thanh Hóa", true)]
    [InlineData("xom thang , dong quang\nPhường Đông Sơn\nTỉnh Thanh Hóa", "Hà Nội", false)]
    // địa chỉ Hà Nội: dòng cuối "Thành phố Hà Nội" → khớp "Hà Nội"
    [InlineData("Số 76, Ngõ 76 Đường Trung Văn, Phường Đại Mỗ, Thành phố Hà Nội, Việt Nam\nPhường Đại Mỗ\nThành phố Hà Nội", "Hà Nội", true)]
    // "Thành phố Hồ Chí Minh" (dòng cuối) khớp option app "TP Hồ Chí Minh"
    [InlineData("123 Đường ABC\nPhường 1\nThành phố Hồ Chí Minh", "TP Hồ Chí Minh", true)]
    // detail 1 dòng chứa tỉnh → khớp (fallback toàn chuỗi)
    [InlineData("Số 1, Đường X, Tỉnh Thanh Hóa", "Thanh Hóa", true)]
    // BẪY: dòng ĐẦU chứa "Thành phố Hà Nội" nhưng dòng CUỐI "Tỉnh Thanh Hóa" → chỉ khớp Thanh Hóa
    [InlineData("Ngõ 5, gần Thành phố Hà Nội, Việt Nam\nPhường Đông Sơn\nTỉnh Thanh Hóa", "Hà Nội", false)]
    [InlineData("Ngõ 5, gần Thành phố Hà Nội, Việt Nam\nPhường Đông Sơn\nTỉnh Thanh Hóa", "Thanh Hóa", true)]
    // dòng cuối rỗng (xuống dòng thừa) vẫn lấy dòng "Tỉnh Thanh Hóa"
    [InlineData("Phường Đông Sơn\nTỉnh Thanh Hóa\n\n", "Thanh Hóa", true)]
    // null/rỗng detail hoặc province → false
    [InlineData(null, "Thanh Hóa", false)]
    [InlineData("", "Thanh Hóa", false)]
    [InlineData("Tỉnh Thanh Hóa", null, false)]
    [InlineData("Tỉnh Thanh Hóa", "", false)]
    public void AddressDetailMatchesProvince_DungDongCuoi(string? detail, string? province, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.AddressDetailMatchesProvince(detail, province));
    }

    // ===== IsSetPickupCheckboxText: CHỈ khớp "đặt làm địa chỉ lấy hàng" =====
    [Theory]
    [InlineData("Đặt làm địa chỉ lấy hàng", true)]
    [InlineData("  đặt làm  địa chỉ lấy hàng \n", true)]
    [InlineData("Đặt làm địa chỉ mặc đinh", false)]   // chính tả THẬT của Shopee (thiếu dấu) — KHÔNG khớp
    [InlineData("Đặt làm địa chỉ mặc định", false)]
    [InlineData("Đặt làm địa chỉ trả hàng", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSetPickupCheckboxText_ChiKhopLayHang(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsSetPickupCheckboxText(input));
    }

    // ===== IsPickupTagText =====
    [Theory]
    [InlineData("Địa chỉ lấy hàng", true)]
    [InlineData("  địa chỉ  lấy hàng \n", true)]
    [InlineData("Default Address", false)]
    [InlineData("Địa chỉ trả hàng", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPickupTagText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsPickupTagText(input));
    }

    // ===== IsEditButtonText =====
    [Theory]
    [InlineData("Sửa", true)]
    [InlineData("  sửa \n", true)]
    [InlineData("Xóa", false)]
    [InlineData("Lưu", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEditButtonText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsEditButtonText(input));
    }

    // ===== IsCancelButtonText =====
    [Theory]
    [InlineData("Hủy", true)]
    [InlineData("  hủy ", true)]
    [InlineData("Lưu", false)]
    [InlineData("Sửa", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCancelButtonText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsCancelButtonText(input));
    }

    // ===== IsSaveButtonText =====
    [Theory]
    [InlineData("Lưu", true)]
    [InlineData("  lưu \n", true)]
    [InlineData("Hủy", false)]
    [InlineData("Lưu và thoát", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSaveButtonText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsSaveButtonText(input));
    }

    // ===== IsEditAddressModalTitle =====
    [Theory]
    [InlineData("Sửa Địa chỉ", true)]
    [InlineData("  sửa  địa chỉ \n", true)]
    [InlineData("Thêm Địa chỉ", false)]
    [InlineData("Địa chỉ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEditAddressModalTitle_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsEditAddressModalTitle(input));
    }

    // ===== IsConfirmButtonText: nút "Đồng ý" (primary) của hộp xác nhận đổi địa chỉ lấy hàng =====
    [Theory]
    [InlineData("Đồng ý", true)]
    [InlineData("  đồng ý ", true)]
    [InlineData("ĐỒNG Ý\n", true)]
    [InlineData("đồng", false)]
    [InlineData("Kiểm tra chi tiết", false)]
    [InlineData("Huỷ", false)]
    [InlineData("Lưu", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsConfirmButtonText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsConfirmButtonText(input));
    }

    // ===== IsCheckDetailButtonText: nút "Kiểm tra chi tiết" (dấu hiệu nhận đúng hộp xác nhận) =====
    [Theory]
    [InlineData("Kiểm tra chi tiết", true)]
    [InlineData("  kiểm tra  chi tiết \n", true)]
    [InlineData("KIỂM TRA CHI TIẾT", true)]
    [InlineData("kiểm tra", false)]
    [InlineData("Đồng ý", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCheckDetailButtonText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsCheckDetailButtonText(input));
    }

    // ===== Bước 3: điều hướng "Tất cả" → Chuẩn bị hàng → tự mang ra bưu cục → In phiếu giao =====

    // ===== IsAllOrdersHref: "chứa" /portal/sale/order =====
    [Theory]
    [InlineData("/portal/sale/order", true)]
    [InlineData("https://banhang.shopee.vn/portal/sale/order?type=toship", true)]
    [InlineData("/portal/all-settings/shipping", false)]
    [InlineData("/portal/sale/return", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllOrdersHref_DungSai(string? href, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsAllOrdersHref(href));
    }

    // ===== IsAllOrdersText =====
    [Theory]
    [InlineData("Tất cả", true)]
    [InlineData("  tất cả \n", true)]
    [InlineData("Chờ xử lý", false)]
    [InlineData("Tất cả đơn", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllOrdersText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsAllOrdersText(input));
    }

    // ===== IsToShipTabText: BẮT ĐẦU bằng "chờ lấy hàng" (nhãn tab có thể kèm badge số "(10)") =====
    [Theory]
    [InlineData("Chờ lấy hàng", true)]
    [InlineData("Chờ lấy hàng (10)", true)]           // kèm badge số
    [InlineData("  chờ  lấy  hàng \n(3)", true)]      // space thừa + xuống dòng + badge
    [InlineData("Tất cả", false)]
    [InlineData("Đang giao", false)]
    [InlineData("Chờ xác nhận", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsToShipTabText_BatDauChoLayHang(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsToShipTabText(input));
    }

    // ===== IsPrepareOrderButtonText =====
    [Theory]
    [InlineData("Chuẩn bị hàng", true)]
    [InlineData("  chuẩn bị  hàng \n", true)]
    [InlineData("CHUẨN BỊ HÀNG", true)]
    [InlineData("Chuẩn bị", false)]
    [InlineData("Hủy đơn", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPrepareOrderButtonText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsPrepareOrderButtonText(input));
    }

    // ===== IsConfirmArrangeButtonText =====
    [Theory]
    [InlineData("Xác nhận", true)]
    [InlineData("  xác  nhận \n", true)]
    [InlineData("XÁC NHẬN", true)]
    [InlineData("Xác nhận đơn", false)]
    [InlineData("Hủy", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsConfirmArrangeButtonText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsConfirmArrangeButtonText(input));
    }

    // ===== IsPrintSlipButtonText =====
    [Theory]
    [InlineData("In phiếu giao", true)]
    [InlineData("  in  phiếu giao \n", true)]
    [InlineData("IN PHIẾU GIAO", true)]
    [InlineData("In phiếu", false)]
    [InlineData("In", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPrintSlipButtonText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsPrintSlipButtonText(input));
    }

    // ===== IsDropoffTitleText: "chứa" tự mang hàng tới bưu cục =====
    [Theory]
    [InlineData("Tôi sẽ tự mang hàng tới Bưu cục", true)]
    [InlineData("tự mang hàng tới bưu cục", true)]
    [InlineData("Tôi sẽ tự mang hàng tới Bưu cục\n(Miễn phí)", true)]
    [InlineData("Giao cho đơn vị vận chuyển", false)]
    [InlineData("Shipper tới lấy hàng", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDropoffTitleText_ChuaChuoi(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsDropoffTitleText(input));
    }

    // ===== IsShipOrderModalTitle =====
    [Theory]
    [InlineData("Giao Đơn Hàng", true)]
    [InlineData("  giao  đơn hàng \n", true)]
    [InlineData("Thông Tin Chi Tiết", false)]
    [InlineData("Giao Đơn", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsShipOrderModalTitle_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsShipOrderModalTitle(input));
    }

    // ===== IsDetailModalTitle =====
    [Theory]
    [InlineData("Thông Tin Chi Tiết", true)]
    [InlineData("  thông tin  chi tiết \n", true)]
    [InlineData("Giao Đơn Hàng", false)]
    [InlineData("Thông Tin", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDetailModalTitle_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsDetailModalTitle(input));
    }

    // ===== ExtractOrderCode: lấy token cuối, GIỮ hoa/thường =====
    [Theory]
    [InlineData("Mã đơn hàng 260715ABC", "260715ABC")]
    [InlineData("260715ABC", "260715ABC")]
    [InlineData("Mã đơn hàng\n2507158T3E4RXYZ", "2507158T3E4RXYZ")]
    [InlineData("  Mã đơn hàng   250715abcDEF  ", "250715abcDEF")]  // giữ hoa/thường
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void ExtractOrderCode_LayTokenCuoi(string? input, string expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.ExtractOrderCode(input));
    }

    // ===== ExtractJobId: khớp job_id=([^&]+) =====
    [Theory]
    [InlineData("https://banhang.shopee.vn/awbprint?job_id=abc123&shop_id=1&lang=vi", "abc123")]
    [InlineData("https://banhang.shopee.vn/awbprint?shop_id=1&job_id=xyz789", "xyz789")]
    [InlineData("https://banhang.shopee.vn/awbprint?job_id=only", "only")]
    [InlineData("https://banhang.shopee.vn/awbprint?shop_id=1", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ExtractJobId_KhopRegex(string? input, string expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.ExtractJobId(input));
    }

    // ===== SanitizeFileName: giữ chữ/số/-_ , ký tự lạ → _ , cắt _ thừa hai đầu, rỗng → "phieu" =====
    [Theory]
    [InlineData("260715ABC", "260715ABC")]
    [InlineData("abc-def_ghi", "abc-def_ghi")]
    [InlineData("abc/def:ghi", "abc_def_ghi")]         // / và : → _
    [InlineData("a b c", "a_b_c")]                      // dấu cách giữa → _
    [InlineData("  260715ABC  ", "260715ABC")]          // trim khoảng trắng đầu vào
    [InlineData("__abc__", "abc")]                      // cắt _ thừa hai đầu
    [InlineData("!!!", "phieu")]                         // toàn ký tự lạ → rỗng → "phieu"
    [InlineData(null, "phieu")]
    [InlineData("", "phieu")]
    [InlineData("   ", "phieu")]
    public void SanitizeFileName_LamSachTenFile(string? input, string expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.SanitizeFileName(input));
    }

    // ===== ExtractSku: chuỗi ASCII chữ/số liên tục cuối cùng + phải có ít nhất 1 chữ số =====
    [Theory]
    [InlineData("Áo thun nam B02435", "B02435")]     // SKU ở cuối
    [InlineData("Áo thun B02435  ", "B02435")]        // khoảng trắng cuối → bỏ qua
    [InlineData("Váy màu đỏ", null)]                   // kết thúc bằng "đỏ" (không ASCII / không số) → null
    [InlineData("Đầm ABC123", "ABC123")]              // chữ + số
    [InlineData("SP 100% cotton", null)]              // kết thúc "cotton" (không số) → null
    [InlineData("12345", "12345")]                     // toàn số
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void ExtractSku_ChiNhanAsciiCoSo(string? input, string? expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.ExtractSku(input));
    }

    // ===== LaDonHuy: hủy khi có cancelReason, hoặc STATUS (không quét statusDescription) chứa "hủy"/"huỷ"/"cancel" =====
    [Theory]
    [InlineData("Đã hủy", null, null, true)]              // status chứa "hủy"
    [InlineData("Đơn Huỷ", null, null, true)]             // status chứa "huỷ" (biến thể dấu kia)
    [InlineData(null, null, "Khách đổi ý", true)]         // có cancelReason
    [InlineData("Cancelled", null, null, true)]           // "cancel"
    // statusDescription chứa "hủy" nhưng đơn đang chạy KHÔNG được tô đỏ (bỏ quét desc → tránh false positive).
    [InlineData("Chờ lấy hàng", "Đơn sẽ bị hủy nếu quá hạn giao", null, false)]
    [InlineData("Đang chuẩn bị", null, null, false)]      // không hủy
    [InlineData(null, null, null, false)]                 // rỗng
    [InlineData("", "  ", "", false)]                     // toàn trắng
    public void LaDonHuy_PhanLoaiHuy(string? status, string? desc, string? cancelReason, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.LaDonHuy(status, desc, cancelReason));
    }

    // ===== LaDaGiaoDaBan: KHỚP TUYỆT ĐỐI {"đã giao","hoàn thành","giao hàng thành công"} — +1 "Đã bán" theo SKU.
    //        CỐ Ý loại các trạng thái "giao dở/không thành công" (CHỨA "đã giao"/"giao hàng" nhưng KHÔNG bằng). =====
    [Theory]
    // Ba trạng thái tính "đã bán" — mọi hoa/thường/space thừa/xuống dòng.
    [InlineData("Đã giao", true)]
    [InlineData("đã giao", true)]
    [InlineData("  ĐÃ GIAO  ", true)]
    [InlineData("Đã  giao\n", true)]
    [InlineData("Hoàn thành", true)]
    [InlineData("HOÀN THÀNH", true)]
    [InlineData("  hoàn thành ", true)]
    [InlineData("Giao hàng thành công", true)]
    [InlineData("giao hàng thành công", true)]
    [InlineData("GIAO HÀNG\tTHÀNH CÔNG", true)]
    // Các chuỗi BIÊN phải KHÔNG khớp (chứa nhưng không bằng) — đây là điểm mấu chốt.
    [InlineData("Đã giao cho ĐVVC", false)]
    [InlineData("Đã giao cho đơn vị vận chuyển", false)]
    [InlineData("Giao hàng không thành công", false)]
    // Trạng thái khác.
    [InlineData("Đã hủy", false)]
    [InlineData("Đang giao", false)]
    [InlineData("Chờ lấy hàng", false)]
    [InlineData("Chờ xác nhận", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LaDaGiaoDaBan_KhopTuyetDoi(string? status, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.LaDaGiaoDaBan(status));
    }

    // ===== IsAllTabText: tab "Tất cả" trên thanh tab danh sách đơn (khớp tuyệt đối "tất cả") =====
    [Theory]
    [InlineData("Tất cả", true)]
    [InlineData("  tất cả \n", true)]
    [InlineData("TẤT CẢ", true)]
    [InlineData("Chờ lấy hàng", false)]
    [InlineData("Tất cả đơn", false)]
    [InlineData("Đang giao", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllTabText_KhopChuanHoa(string? input, bool expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.IsAllTabText(input));
    }

    // ===== ParseVndAmount: bỏ mọi ký tự không phải số → long?, không parse được → null =====
    [Theory]
    [InlineData("₫166.500", 166500L)]
    [InlineData("₫1.234.567", 1234567L)]
    [InlineData("166.500đ", 166500L)]
    [InlineData("  ₫ 89.000  ", 89000L)]
    [InlineData("0", 0L)]
    [InlineData("₫12", 12L)]
    public void ParseVndAmount_CoSo_TraVeSo(string? input, long expected)
    {
        Assert.Equal(expected, ShopeeShippingNav.ParseVndAmount(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("₫")]
    [InlineData("miễn phí")]
    public void ParseVndAmount_KhongCoSo_TraVeNull(string? input)
    {
        Assert.Null(ShopeeShippingNav.ParseVndAmount(input));
    }
}
