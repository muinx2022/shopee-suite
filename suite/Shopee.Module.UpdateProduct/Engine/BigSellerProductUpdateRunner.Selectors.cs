using System.Text.RegularExpressions;

namespace UpdateProduct;

// Partial của BigSellerProductUpdateRunner: hằng SELECTOR (CSS/XPath) + Regex — tách khỏi file chính (A2, pure move).
internal sealed partial class BigSellerProductUpdateRunner
{
    // ── SELECTORS ──
    // BigSeller đã đổi bảng listing NHIỀU LẦN: ant-table (cũ) → vxe-table → nay bảng Vue riêng
    // (dòng = tr.product_native_row, scoped data-v-…; KHÔNG có thuộc tính rowid — khóa dòng nằm ở
    // checkbox input[name]; nút Sửa vẫn a.action_btn.addEditProduct). Gộp cả 3 kiểu selector để
    // chạy bất kể BigSeller đang render bảng nào.
    private const string ListingRows = "tr.product_native_row, tr.vxe-body--row, tbody.ant-table-tbody tr";
    private const string ListingEditButton = "a.action_btn.addEditProduct";   // (vxe + bảng Vue mới vẫn giữ class này)
    private const string ListingReadySelector = "tr.product_native_row, tr.vxe-body--row, tbody.ant-table-tbody tr, a.action_btn.addEditProduct, .ant-empty, .ant-table-placeholder, .vxe-table--empty-block, .vxe-table--empty-placeholder";
    private const string ListingRowKeyAttr = "rowid";   // vxe: <tr rowid="..."> (cũ: data-row-key; bảng Vue mới KHÔNG có → fallback hash ảnh, xem DraftRowKeyAsync)
    private const string DeleteBtn1 = "a.action_btn[title='Xóa'], a.action_btn[title='Delete']";
    private const string DeleteBtn2 = "a[title='Xóa'], a[title='Delete']";
    private const string DeleteBtn3 = "a.action_btn:has(span.bsicon_trash_2)";
    private const string DeleteConfirmPrimary = ".ant-modal-confirm-btns button.ant-btn-primary";
    private const string BlockingModalVisible = "div.ant-modal-wrap:visible";
    private static readonly string[] DismissBtns =
    {
        ".ant-modal-confirm-btns button:not(.ant-btn-primary)", ".ant-modal-close",
        ".ant-modal-footer button:not(.ant-btn-primary)", ".ant-modal-confirm-btns button", ".ant-modal-footer button",
    };

    private const string SourceLinkInput = "input[autoid='product_source_link_text']";
    private const string SourceLinkCopyButton = "div.com_input_box:has(input[autoid='product_source_link_text']) button";
    private const string ProductNameInput = "input[autoid='product_name_text']";

    private const string Md5Button = "span.sell_md5";
    private const string Md5CompleteStatus = "div.ant-modal:visible div.complete_Status";
    private const string CloseModalAny = "div.ant-modal:visible";
    private static readonly string[] CloseModalSels =
    {
        "div.ant-modal:visible:has(div.complete_Status) .ant-modal-footer button.ant-btn",
        "div.ant-modal:visible:has(div.complete_Status) button.ant-modal-close",
        "div.ant-modal:visible .ant-modal-footer button", "div.ant-modal:visible button.ant-btn",
        "div.ant-modal:visible button.ant-modal-close", "div.ant-modal:visible .ant-modal-close",
        "div.ant-modal:visible .ant-modal-close-x",
    };

    private const string UploadImageRadioWrapper = "label.ant-radio-wrapper";
    // Radio "tự upload ảnh": khớp text 2 ngôn ngữ (VN "Tải lên hình ảnh" / EN "Upload Image"). Fallback theo
    // input value='1' (value='0' = dùng template Seller Center) — độc lập ngôn ngữ, scope trong div.sizeContent
    // để không dính radio khác trên trang. BẮT BUỘC tick radio này thì div.sizeChartContent (mặc định display:none)
    // mới hiện, kéo theo div.spc_box (menu chọn ảnh) — không tick = không chọn được ảnh.
    private static readonly Regex UploadImageRadioText = new("Tải lên hình ảnh|Upload Image", RegexOptions.IgnoreCase);
    private const string UploadImageRadioByValue = "div.sizeContent label.ant-radio-wrapper:has(input.ant-radio-input[value='1'])";
    private const string ParentSkuInput = "input[autoid='parent_sku_text']";
    private const string VariationSkuInputs = "input[autoid^='variation_sku_text_']";
    private const string VariationStockInputs = "input[autoid^='variation_stock_text_']";
    private const string VariationPriceInputs = "input[autoid^='variation_price_text_']";
    private const string ShippingFastWrapper = "label.ant-checkbox-wrapper";
    private const string ShippingCheckedMark = ".ant-checkbox-checked";
    private const string WeightInput = "input[autoid='weight_text']";

    private const string BrandBoxXPath1 = "//div[contains(@class, 'page_edit_item')][.//*[contains(normalize-space(), 'Thương hiệu') or contains(normalize-space(), 'Brand')]]//div[contains(@class, 'ant-select-selection')]";
    private const string BrandBoxXPath2 = "//div[contains(@class, 'ant-form-item')][.//*[contains(normalize-space(), 'Thương hiệu') or contains(normalize-space(), 'Brand')] or .//div[contains(@class, 'ant-form-explain') and contains(normalize-space(), 'Brand cannot be empty')]]//div[contains(@class, 'ant-select-selection')]";
    private const string BrandBoxCss = "div.ant-form-item:has(div.ant-form-explain:has-text('Brand cannot be empty')) div.ant-select-selection";
    private const string BrandSelectedValue = ".ant-select-selection-selected-value";
    private const string BrandSearchInput1 = ".ant-select-open input.ant-select-search__field";
    private const string BrandSearchInput2 = "input.ant-select-search__field:visible";
    private const string BrandDropdownReady = ".ant-select-dropdown:not(.ant-select-dropdown-hidden), div.option";
    private static readonly string[] BrandOptions =
    {
        ".ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-dropdown-menu-item",
        ".ant-select-dropdown:not(.ant-select-dropdown-hidden) [role='option']", "div.option:visible",
    };
    private static readonly Regex NoBrandRegex = new(@"^\s*No\s*brand\s*$", RegexOptions.IgnoreCase);

    private const string ImageUploadedImg = "div.supp_size_chat div.page_edit_img_item.comm_img_module img[src]";
    private const string ImageGalleryBox = "div.spc_box";
    private const string ImageUploadMenuItem = "div.spc_box ul.spc_cho li";

    private const string AddVideoButton = "button:has-text('Thêm video')";
    private const string UploadLocalVideoOpt = "li[autoid='upload_local_video_option']";
    private const string VideoBoxes = "div.pro_vid_box div.page_edit_img_item.comm_img_module";
    private const string VideoSuccessSignal = "span.top_status.bk_green:has-text('Tải lên thành công')";
    private static readonly string[] VideoErrSels =
        { ".ant-message-error", ".ant-notification-notice-message", ".ant-notification-notice-description", ".ant-message-notice-content", ".toast", ".el-message--error" };

    private const string DescriptionTextarea = "textarea[autoid='product_description_text']";
    private const string DescriptionCountBox = "span.count_box";

    private const string SaveButtonWrapper = "div[autoid='save_and_publish_button']";
    private const string SaveOption = "li[autoid='save_and_publish_option']";
    private const string ConfirmPrimaryBtn = "div.ant-modal-confirm-btns button.ant-btn-primary";
    private static readonly string[] SaveErrSels =
        { "div.ant-message", "div.ant-message-notice", "div.ant-message-notice-content", "div.ant-notification", "div.ant-modal-root", "div[role='alert']", "body" };
    // (Selector modal "thành công" sau khi Lưu đã DỜI sang BigSellerSaveSuccessHelper — nơi duy nhất nhận diện success.)

    private static readonly Regex EditIdRegex = new(@"/edit/(\d+)\.htm", RegexOptions.IgnoreCase);
}
