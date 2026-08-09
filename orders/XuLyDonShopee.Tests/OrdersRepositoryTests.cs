using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test upsert bảng <c>orders</c>: thêm mới đếm đúng inserted; sync lại cùng mã đơn thì CẬP NHẬT (không tạo
/// trùng) + giữ created_at; khóa là cặp (account_id, order_sn); đơn không có mã bị bỏ; lưu đủ trường.
/// </summary>
public class OrdersRepositoryTests
{
    private static SyncedOrder Sample(string sn) => new()
    {
        OrderSn = sn,
        ShopeeOrderId = "237900524283161",
        BuyerUsername = "quynhsuugiacshoppi",
        ItemsJson = "[{\"name\":\"Giày\",\"variation\":\"ĐEN,37\",\"amount\":\"1\",\"image\":\"x\"}]",
        ItemCount = 1,
        ItemSummary = "Giày",
        TotalPriceText = "₫166.500",
        TotalPrice = 166500,
        FinalAmount = 160000,
        FinalAmountText = "₫160.000",
        PaymentMethod = "Thanh toán khi nhận hàng",
        Status = "Đã hủy",
        StatusDescription = "Đã hủy tự động bởi hệ thống Shopee",
        CancelReason = "Hủy đơn hàng vì hành vi giao dịch bất thường.",
        Channel = "Nhanh",
        Carrier = "SPX Express",
        TrackingNumber = "SPXVN068067521447",
    };

    [Fact]
    public void UpsertMany_ThemMoi_DemDungInserted()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        var (inserted, updated, insertedOrders) = repo.UpsertMany(1, new[] { Sample("SN1"), Sample("SN2") }, DateTime.UtcNow);

        Assert.Equal(2, inserted);
        Assert.Equal(0, updated);
        Assert.Equal(2, repo.CountByAccount(1));
        Assert.Equal(new[] { "SN1", "SN2" }, insertedOrders.Select(o => o.OrderSn)); // đơn MỚI trả đủ, đúng thứ tự
    }

    [Fact]
    public void UpsertMany_TraDanhSachDonMoi_ChiDonInsert()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        // Lần 1: 2 đơn mới → InsertedOrders có đúng 2 đơn (chính các SyncedOrder đầu vào).
        var lan1 = repo.UpsertMany(1, new[] { Sample("SN1"), Sample("SN2") }, DateTime.UtcNow);
        Assert.Equal(2, lan1.Inserted);
        Assert.Equal(new[] { "SN1", "SN2" }, lan1.InsertedOrders.Select(o => o.OrderSn));

        // Lần 2: cùng 2 đơn → cập nhật, KHÔNG có đơn mới (list rỗng), Updated = 2.
        var lan2 = repo.UpsertMany(1, new[] { Sample("SN1"), Sample("SN2") }, DateTime.UtcNow);
        Assert.Equal(0, lan2.Inserted);
        Assert.Equal(2, lan2.Updated);
        Assert.Empty(lan2.InsertedOrders);

        // Trộn: 1 đơn cũ (SN1) + 1 đơn mới (SN3) + 1 đơn rỗng (bỏ qua) → InsertedOrders chỉ SN3.
        var lan3 = repo.UpsertMany(1, new[] { Sample("SN1"), Sample("SN3"), Sample("") }, DateTime.UtcNow);
        Assert.Equal(1, lan3.Inserted);
        Assert.Equal(1, lan3.Updated);
        Assert.Equal(new[] { "SN3" }, lan3.InsertedOrders.Select(o => o.OrderSn));
    }

    [Fact]
    public void UpsertMany_SyncLai_CapNhat_KhongTaoTrung_GiuCreatedAt()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        repo.UpsertMany(1, new[] { Sample("SN1") }, t1);
        var created1 = ReadString(db, "SN1", "created_at");

        // Sync lại cùng mã đơn, dữ liệu đổi, thời điểm sync KHÁC.
        var t2 = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var again = Sample("SN1");
        again.Status = "Chờ lấy hàng";
        again.TotalPrice = 200000;
        var (inserted, updated, insertedOrders) = repo.UpsertMany(1, new[] { again }, t2);

        Assert.Equal(0, inserted);
        Assert.Equal(1, updated);
        Assert.Empty(insertedOrders);                 // cập nhật → KHÔNG có đơn mới
        Assert.Equal(1, repo.CountByAccount(1)); // KHÔNG tạo dòng trùng

        Assert.Equal("Chờ lấy hàng", ReadString(db, "SN1", "status"));      // dữ liệu cập nhật
        Assert.Equal("200000", ReadString(db, "SN1", "total_price"));
        Assert.Equal(created1, ReadString(db, "SN1", "created_at"));         // created_at GIỮ nguyên
        Assert.NotEqual(created1, ReadString(db, "SN1", "updated_at"));      // updated_at đổi theo lần sync mới
    }

    [Fact]
    public void UpsertMany_KhacTaiKhoan_CungMaDon_HaiDong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);
        var (inserted, updated, _) = repo.UpsertMany(2, new[] { Sample("SN1") }, DateTime.UtcNow);

        Assert.Equal(1, inserted);   // khóa là (account_id, order_sn) → tài khoản 2 là dòng MỚI
        Assert.Equal(0, updated);
        Assert.Equal(1, repo.CountByAccount(1));
        Assert.Equal(1, repo.CountByAccount(2));
    }

    [Fact]
    public void UpsertMany_BoQuaDonKhongCoMa()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        var noSn = Sample("");        // OrderSn rỗng → không làm khóa được, bị bỏ
        var (inserted, updated, insertedOrders) = repo.UpsertMany(1, new[] { noSn, Sample("SN1") }, DateTime.UtcNow);

        Assert.Equal(1, inserted);    // chỉ SN1 được thêm
        Assert.Equal(0, updated);
        Assert.Equal(1, repo.CountByAccount(1));
        // đơn OrderSn rỗng bị bỏ qua → KHÔNG vào danh sách đơn mới; chỉ SN1.
        Assert.Equal(new[] { "SN1" }, insertedOrders.Select(o => o.OrderSn));
    }

    [Fact]
    public void UpsertMany_LuuDayDuTruong()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        repo.UpsertMany(7, new[] { Sample("SN9") }, DateTime.UtcNow);

        Assert.Equal("237900524283161", ReadString(db, "SN9", "shopee_order_id"));
        Assert.Equal("quynhsuugiacshoppi", ReadString(db, "SN9", "buyer_username"));
        Assert.Equal("1", ReadString(db, "SN9", "item_count"));
        Assert.Equal("166500", ReadString(db, "SN9", "total_price"));
        Assert.Equal("₫166.500", ReadString(db, "SN9", "total_price_text"));
        Assert.Equal("160000", ReadString(db, "SN9", "final_amount"));
        Assert.Equal("₫160.000", ReadString(db, "SN9", "final_amount_text"));
        Assert.Equal("SPXVN068067521447", ReadString(db, "SN9", "tracking_number"));
        Assert.Equal("Hủy đơn hàng vì hành vi giao dịch bất thường.", ReadString(db, "SN9", "cancel_reason"));
    }

    [Fact]
    public void UpsertMany_CapNhat_FinalAmountNull_GiuGiaTriCu()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        // Lần 1: đơn có "Số tiền cuối cùng" (đã mở chi tiết lấy được).
        var first = Sample("SN1");
        first.FinalAmount = 292010;
        first.FinalAmountText = "₫292.010";
        repo.UpsertMany(1, new[] { first }, DateTime.UtcNow);

        // Lần 2: cùng đơn nhưng lần này KHÔNG lấy final (final = null) → COALESCE GIỮ số cũ, KHÔNG ghi đè null.
        var again = Sample("SN1");
        again.FinalAmount = null;
        again.FinalAmountText = null;
        again.TotalPrice = 200000; // trường khác vẫn cập nhật bình thường
        repo.UpsertMany(1, new[] { again }, DateTime.UtcNow);

        Assert.Equal("292010", ReadString(db, "SN1", "final_amount"));        // GIỮ số cũ
        Assert.Equal("₫292.010", ReadString(db, "SN1", "final_amount_text"));
        Assert.Equal("200000", ReadString(db, "SN1", "total_price"));         // trường khác vẫn đè
    }

    [Fact]
    public void UpsertMany_CapNhat_FinalAmountMoi_GhiDe()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        var first = Sample("SN1");
        first.FinalAmount = 100000;
        first.FinalAmountText = "₫100.000";
        repo.UpsertMany(1, new[] { first }, DateTime.UtcNow);

        // Lần 2 có final MỚI → đè giá trị cũ (COALESCE lấy giá trị mới vì khác null).
        var again = Sample("SN1");
        again.FinalAmount = 292010;
        again.FinalAmountText = "₫292.010";
        repo.UpsertMany(1, new[] { again }, DateTime.UtcNow);

        Assert.Equal("292010", ReadString(db, "SN1", "final_amount"));
        Assert.Equal("₫292.010", ReadString(db, "SN1", "final_amount_text"));
    }

    // ===================== items_json: bản NGHÈO không được đè bản GIÀU =====================

    /// <summary><c>items_json</c> bản GIÀU (2 SP) — đọc ở TRANG CHI TIẾT: đủ 8 khóa
    /// <c>[amount, donGia, image, name, phanLoai, sku, thanhTien, variation]</c> như đơn thật lúc 08:17.</summary>
    private const string ItemsGiau =
        "[{\"name\":\"Giày Boots Da Nữ Cổ Ngắn - A141\",\"variation\":\"Kem,36\",\"amount\":\"1\","
        + "\"image\":\"https://cf.shopee.vn/file/abc\",\"phanLoai\":\"Kem,36\",\"sku\":\"A141\","
        + "\"donGia\":303050,\"thanhTien\":303050},"
        + "{\"name\":\"Áo Khoác Gió Phối Màu B21913\",\"variation\":\"xám,m\",\"amount\":\"1\","
        + "\"image\":\"https://cf.shopee.vn/file/def\",\"phanLoai\":\"xám,m\",\"sku\":\"B21913\","
        + "\"donGia\":345000,\"thanhTien\":345000}]";

    /// <summary><c>items_json</c> bản NGHÈO (1 SP) — quét ở TRANG DANH SÁCH: chỉ 4 khóa
    /// <c>[amount, image, name, variation]</c> như CHÍNH đơn đó lúc 09:46 (sau khi bị đè).</summary>
    private const string ItemsNgheo =
        "[{\"name\":\"Giày Boots Da Nữ Cổ Ngắn - A141\",\"variation\":\"Kem,36 [A141 A141]\",\"amount\":\"1\","
        + "\"image\":\"https://cf.shopee.vn/file/abc\"}]";

    /// <summary>Đơn <paramref name="sn"/> với đúng cặp <c>items_json</c> + <c>item_count</c> muốn thử.</summary>
    private static SyncedOrder VoiItems(string sn, string itemsJson, int itemCount)
    {
        var o = Sample(sn);
        o.ItemsJson = itemsJson;
        o.ItemCount = itemCount;
        return o;
    }

    /// <summary>
    /// HỒI QUY (lỗi THẬT 28/07/2026): đơn đã đọc trang CHI TIẾT (items_json giàu) bị vòng sync sau — chỉ đọc trang
    /// DANH SÁCH — ghi đè bằng bản nghèo, mất SKU/phân loại VĨNH VIỄN (đơn đã có ước tính không được mở lại chi
    /// tiết nữa). Phải GIỮ bản giàu, và <c>item_count</c> đi theo bản được giữ (kẻo số nói dối).
    /// </summary>
    [Fact]
    public void UpsertMany_CapNhat_BanNgheoKhongDeBanGiau_ItemCountDiTheo()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        // Lượt 1: vừa mở TRANG CHI TIẾT → items_json GIÀU (2 SP, có sku + phanLoai).
        repo.UpsertMany(1, new[] { VoiItems("SN1", ItemsGiau, 2) }, DateTime.UtcNow);

        // Lượt 2 (vòng sync sau): chỉ có bản quét TRANG DANH SÁCH (nghèo, 1 SP) + lượt này không lấy được ước
        // tính/vận đơn — đúng hiện trường production.
        var danhSach = VoiItems("SN1", ItemsNgheo, 1);
        danhSach.FinalAmount = null;
        danhSach.FinalAmountText = null;
        danhSach.TrackingNumber = null;
        danhSach.TotalPrice = 200000;   // cột thường vẫn cập nhật bình thường
        repo.UpsertMany(1, new[] { danhSach }, DateTime.UtcNow);

        Assert.Equal(ItemsGiau, ReadString(db, "SN1", "items_json"));   // GIỮ bản giàu
        Assert.Equal("2", ReadString(db, "SN1", "item_count"));         // count ĐI THEO bản giữ (không tụt về 1)
        Assert.Equal("200000", ReadString(db, "SN1", "total_price"));   // cột thường vẫn đè
        // Các cột "đã lấy được" khác giữ nguyên luật COALESCE cũ.
        Assert.Equal("160000", ReadString(db, "SN1", "final_amount"));
        Assert.Equal("SPXVN068067521447", ReadString(db, "SN1", "tracking_number"));
    }

    [Fact]
    public void UpsertMany_CapNhat_BanGiauDeBanNgheo_LayBanGiau()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        repo.UpsertMany(1, new[] { VoiItems("SN1", ItemsNgheo, 1) }, DateTime.UtcNow);
        // Trang chi tiết đọc được sản phẩm → bản GIÀU thắng (dữ liệu mới nhất + đầy đủ hơn), count theo bản mới.
        repo.UpsertMany(1, new[] { VoiItems("SN1", ItemsGiau, 2) }, DateTime.UtcNow);

        Assert.Equal(ItemsGiau, ReadString(db, "SN1", "items_json"));
        Assert.Equal("2", ReadString(db, "SN1", "item_count"));
    }

    [Fact]
    public void UpsertMany_CapNhat_ItemsRong_KhongXoaBanDaCo()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        repo.UpsertMany(1, new[] { VoiItems("SN1", ItemsGiau, 2) }, DateTime.UtcNow);
        // Lượt sau không quét được sản phẩm nào ("[]" — mặc định của SyncedOrder) → KHÔNG xóa bản đã có.
        repo.UpsertMany(1, new[] { VoiItems("SN1", "[]", 0) }, DateTime.UtcNow);

        Assert.Equal(ItemsGiau, ReadString(db, "SN1", "items_json"));
        Assert.Equal("2", ReadString(db, "SN1", "item_count"));
    }

    [Fact]
    public void UpsertMany_CapNhat_CaHaiDeuNgheo_LayBanMoi()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        repo.UpsertMany(1, new[] { VoiItems("SN1", ItemsNgheo, 1) }, DateTime.UtcNow);

        // Hai bản cùng nghèo → giữ hành vi CŨ: bản mới nhất thắng (danh sách vừa quét là đúng hiện trạng đơn).
        var moi = VoiItems("SN1", "[{\"name\":\"Giày\",\"variation\":\"ĐEN,37\",\"amount\":\"2\",\"image\":\"y\"}]", 1);
        repo.UpsertMany(1, new[] { moi }, DateTime.UtcNow);

        Assert.Equal(moi.ItemsJson, ReadString(db, "SN1", "items_json"));
        Assert.Equal("1", ReadString(db, "SN1", "item_count"));
    }

    [Fact]
    public void GetOrderSnsWithFinalAmount_ChiTraDonCoFinalAmount_TheoTaiKhoan()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        var withFinal = Sample("HASFINAL");   // Sample mặc định có FinalAmount = 160000
        var noFinal = Sample("NOFINAL");
        noFinal.FinalAmount = null;
        noFinal.FinalAmountText = null;
        repo.UpsertMany(1, new[] { withFinal, noFinal }, DateTime.UtcNow);
        repo.UpsertMany(2, new[] { Sample("OTHERACC") }, DateTime.UtcNow); // tài khoản khác → không lẫn

        var set = repo.GetOrderSnsWithFinalAmount(1);

        Assert.Contains("HASFINAL", set);
        Assert.DoesNotContain("NOFINAL", set);    // final_amount NULL → không trả
        Assert.DoesNotContain("OTHERACC", set);   // của tài khoản 2
        Assert.Single(set);
    }

    [Fact]
    public void CountByAccount_KhongCoDon_TraVe0()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        Assert.Equal(0, repo.CountByAccount(999));
    }

    // ===================== Đẩy Google Sheet: GetForGsheetPush / MarkGsheetSynced =====================

    [Fact]
    public void GetForGsheetPush_Superset_MoiDon_KeCaKhongTracking_MapDuField()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        // Superset trả MỌI đơn của account (KHÔNG lọc tracking nữa — đơn "Chờ lấy hàng" chưa có vận đơn vẫn trả).
        var huy = Sample("HUYDON");
        huy.Status = "Đã hủy";
        huy.CancelReason = "Khách đổi ý";
        var choLay = Make("CHOLAY", status: "Chờ lấy hàng"); // Make KHÔNG set tracking → tracking null
        repo.UpsertMany(1, new[]
        {
            Sample("HASTRACK"),          // có tracking, chưa ghi sheet
            Sample("SYNCED_FULL"),       // sẽ đánh dấu đã ghi ĐỦ (có file, có vận đơn)
            huy,                         // đơn hủy — map Status/CancelReason
            choLay,                      // KHÔNG tracking → vẫn phải trả (superset)
        }, DateTime.UtcNow);

        repo.MarkGsheetSynced(1, "SYNCED_FULL", "https://drive/aaa", false, coVanDon: true, coUocTinh: true, coDonTraHang: false, tab: "Tháng 07-2026", DateTime.UtcNow, pushGen: 0);

        var pending = repo.GetForGsheetPush(1);
        var sns = pending.Select(p => p.OrderSn).ToList();

        Assert.Contains("HASTRACK", sns);
        Assert.Contains("SYNCED_FULL", sns);       // SUPERSET → vẫn trả (dù đã ghi đủ)
        Assert.Contains("HUYDON", sns);
        Assert.Contains("CHOLAY", sns);            // KHÔNG tracking VẪN trả (bỏ lọc tracking)

        // Map đúng các field mới, gồm cả cờ vận đơn.
        var full = pending.First(p => p.OrderSn == "SYNCED_FULL");
        Assert.True(full.DaGhiSheet);
        Assert.Equal("https://drive/aaa", full.FileUrl);
        Assert.Equal(0, full.GsheetDaHuy);          // daHuy=false → 0
        Assert.Equal(1, full.GsheetDaCoVanDon);     // coVanDon=true → 1
        Assert.Equal(1, full.GsheetDaCoUocTinh);    // coUocTinh=true → 1
        Assert.Equal("Tháng 07-2026", full.GsheetTab); // tab đã nhớ được map
        // items_json (nguồn suy cột "Phân loại" gửi lên sheet) — cột thêm ở CUỐI SELECT, chỉ số phải khớp.
        Assert.Equal(Sample("x").ItemsJson, full.ItemsJson);

        var fresh = pending.First(p => p.OrderSn == "HASTRACK");
        Assert.False(fresh.DaGhiSheet);
        Assert.Null(fresh.GsheetDaHuy);             // chưa đẩy → null
        Assert.Null(fresh.GsheetDaCoVanDon);        // chưa đẩy → null
        Assert.Null(fresh.GsheetDaCoUocTinh);       // chưa đẩy → null
        Assert.Null(fresh.GsheetTab);               // chưa đẩy → chưa nhớ tab
        Assert.Equal("SPXVN068067521447", fresh.TrackingNumber);
        Assert.Equal(160000, fresh.FinalAmount);    // "Ước tính" — số tiền đẩy lên sheet

        var choLayRow = pending.First(p => p.OrderSn == "CHOLAY");
        Assert.Null(choLayRow.TrackingNumber);      // chưa có vận đơn
        Assert.Equal("Chờ lấy hàng", choLayRow.Status);
        Assert.Null(choLayRow.FinalAmount);         // chưa mở trang chi tiết → chưa có ước tính

        var cancelled = pending.First(p => p.OrderSn == "HUYDON");
        Assert.Equal("Đã hủy", cancelled.Status);
        Assert.Equal("Khách đổi ý", cancelled.CancelReason);
    }

    [Fact]
    public void MarkGsheetSynced_GhiDeDaHuy_VaDaCoVanDon_FileUrlNullKhongXoa_SyncedAtGiu()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);

        // Lần 1: đã ghi, CHƯA có file, daHuy=false, coVanDon=false, coUocTinh=false → cả 3 cờ = 0;
        // tab lần đầu = "Tháng 06-2026".
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        repo.MarkGsheetSynced(1, "SN1", null, false, coVanDon: false, coUocTinh: false, coDonTraHang: false, tab: "Tháng 06-2026", t1, pushGen: 0);
        var synced1 = ReadString(db, "SN1", "gsheet_synced_at");
        Assert.NotNull(synced1);
        Assert.Null(ReadString(db, "SN1", "gsheet_file_url"));
        Assert.Equal("0", ReadString(db, "SN1", "gsheet_da_huy"));
        Assert.Equal("0", ReadString(db, "SN1", "gsheet_da_co_van_don"));
        Assert.Equal("0", ReadString(db, "SN1", "gsheet_da_co_uoc_tinh"));
        Assert.Equal("Tháng 06-2026", ReadString(db, "SN1", "gsheet_tab"));

        // Lần 2: bổ sung fileUrl + daHuy=true + coVanDon=true + coUocTinh=true, thời điểm KHÁC → synced_at GIỮ,
        // file_url điền, cả 3 cờ GHI ĐÈ = 1. Truyền tab KHÁC → gsheet_tab GIỮ tab lần đầu (COALESCE).
        var t2 = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        repo.MarkGsheetSynced(1, "SN1", "https://drive/aaa", true, coVanDon: true, coUocTinh: true, coDonTraHang: false, tab: "Tháng 08-2026", t2, pushGen: 0);
        Assert.Equal(synced1, ReadString(db, "SN1", "gsheet_synced_at"));            // KHÔNG đổi
        Assert.Equal("https://drive/aaa", ReadString(db, "SN1", "gsheet_file_url"));
        Assert.Equal("1", ReadString(db, "SN1", "gsheet_da_huy"));
        Assert.Equal("1", ReadString(db, "SN1", "gsheet_da_co_van_don"));
        Assert.Equal("1", ReadString(db, "SN1", "gsheet_da_co_uoc_tinh"));
        Assert.Equal("Tháng 06-2026", ReadString(db, "SN1", "gsheet_tab"));         // GIỮ tab lần đầu, KHÔNG đè

        // Lần 3: fileUrl null → KHÔNG xóa link; daHuy=false + coVanDon=false + coUocTinh=false → cả 3 cờ về 0
        // (ghi đè luôn).
        repo.MarkGsheetSynced(1, "SN1", null, false, coVanDon: false, coUocTinh: false, coDonTraHang: false, tab: "Tháng 09-2026", t2, pushGen: 0);
        Assert.Equal("https://drive/aaa", ReadString(db, "SN1", "gsheet_file_url"));
        Assert.Equal("0", ReadString(db, "SN1", "gsheet_da_huy"));
        Assert.Equal("0", ReadString(db, "SN1", "gsheet_da_co_van_don"));
        Assert.Equal("0", ReadString(db, "SN1", "gsheet_da_co_uoc_tinh"));
        Assert.Equal("Tháng 06-2026", ReadString(db, "SN1", "gsheet_tab"));         // vẫn GIỮ tab lần đầu
    }

    [Fact]
    public void GetForGsheetPush_UocTinhVuaXuatHien_DuDieuKienDayLai_DaDayKemUocTinh_ThiThoi()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        // Đơn được ghi sheet ở lượt ĐẦU khi CHƯA có ước tính (final_amount NULL → tiền tạm = tổng tiền).
        var chuaCoUocTinh = Sample("SN1");
        chuaCoUocTinh.FinalAmount = null;
        chuaCoUocTinh.FinalAmountText = null;
        repo.UpsertMany(1, new[] { chuaCoUocTinh }, DateTime.UtcNow);
        repo.MarkGsheetSynced(1, "SN1", null, false, coVanDon: true, coUocTinh: false, coDonTraHang: false, tab: "Tháng 07-2026", DateTime.UtcNow, pushGen: 0);

        // Lượt sync sau: trang chi tiết trả ước tính → final_amount có giá trị, cờ vẫn 0 → uocTinhMoi = true
        // (đủ điều kiện đẩy LẠI để ghi đè đúng số tiền).
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);
        var p = Assert.Single(repo.GetForGsheetPush(1));
        Assert.Equal(160000, p.FinalAmount);
        Assert.Equal(0, p.GsheetDaCoUocTinh);
        Assert.True(p.FinalAmount is not null && p.GsheetDaCoUocTinh != 1);

        // Đẩy lại KÈM ước tính → cờ = 1 → lượt sau KHÔNG đẩy lại nữa (chống spam Apps Script).
        repo.MarkGsheetSynced(1, "SN1", null, false, coVanDon: true, coUocTinh: true, coDonTraHang: false, tab: "Tháng 07-2026", DateTime.UtcNow, pushGen: 0);
        var p2 = Assert.Single(repo.GetForGsheetPush(1));
        Assert.Equal(1, p2.GsheetDaCoUocTinh);
        Assert.False(p2.FinalAmount is not null && p2.GsheetDaCoUocTinh != 1);
    }

    [Fact]
    public void GetForGsheetPush_MapCoDaDemDaBan_VaDaDayHub_TheoCotNull()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        repo.UpsertMany(1, new[] { Sample("A_NONE"), Sample("B_SOLD"), Sample("C_HUB"), Sample("D_BOTH") }, DateTime.UtcNow);

        // B_SOLD: chỉ đánh cờ sold_counted_at; C_HUB: chỉ hub_synced_at; D_BOTH: cả hai; A_NONE: không cờ.
        repo.MarkSoldCounted(1, new[] { "B_SOLD", "D_BOTH" }, DateTime.UtcNow);
        repo.MarkHubSynced(1, new[] { "C_HUB", "D_BOTH" }, DateTime.UtcNow);

        var pending = repo.GetForGsheetPush(1);

        var none = pending.First(p => p.OrderSn == "A_NONE");
        Assert.False(none.DaDemDaBan);
        Assert.False(none.DaDayHub);

        var sold = pending.First(p => p.OrderSn == "B_SOLD");
        Assert.True(sold.DaDemDaBan);
        Assert.False(sold.DaDayHub);

        var hub = pending.First(p => p.OrderSn == "C_HUB");
        Assert.False(hub.DaDemDaBan);
        Assert.True(hub.DaDayHub);

        var both = pending.First(p => p.OrderSn == "D_BOTH");
        Assert.True(both.DaDemDaBan);
        Assert.True(both.DaDayHub);
    }

    // ===================== Vòng đời đơn: GetOrderSns / DeleteOrders =====================

    [Fact]
    public void GetOrderSns_TraDungMaDon_DungTaiKhoan()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("SN1"), Make("SN2") }, DateTime.UtcNow);
        repo.UpsertMany(2, new[] { Make("SN3") }, DateTime.UtcNow);

        var sns = repo.GetOrderSns(1);
        Assert.Equal(2, sns.Count);
        Assert.Contains("SN1", sns);
        Assert.Contains("SN2", sns);
        Assert.DoesNotContain("SN3", sns);   // đơn của tài khoản khác

        Assert.Empty(repo.GetOrderSns(999));  // tài khoản không có đơn
    }

    [Fact]
    public void DeleteOrders_XoaDungDon_DungTaiKhoan_TraSoDong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("SN1"), Make("SN2"), Make("SN3") }, DateTime.UtcNow);
        repo.UpsertMany(2, new[] { Make("SN1") }, DateTime.UtcNow); // cùng mã SN1 nhưng tài khoản KHÁC

        var deleted = repo.DeleteOrders(1, new[] { "SN1", "SN2" });

        Assert.Equal(2, deleted);
        Assert.Equal(new[] { "SN3" }, repo.GetOrderSns(1).OrderBy(x => x)); // còn lại SN3
        Assert.Contains("SN1", repo.GetOrderSns(2));                        // tài khoản 2 KHÔNG bị đụng
    }

    [Fact]
    public void DeleteOrders_DanhSachRong_Tra0()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("SN1") }, DateTime.UtcNow);

        Assert.Equal(0, repo.DeleteOrders(1, Array.Empty<string>()));
        Assert.Equal(1, repo.CountByAccount(1)); // không xóa gì
    }

    [Fact]
    public void DeleteOrders_MaKhongTonTai_Tra0_KhongDungDonKhac()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("SN1") }, DateTime.UtcNow);

        Assert.Equal(0, repo.DeleteOrders(1, new[] { "KHONGCO" }));
        Assert.Equal(1, repo.CountByAccount(1));
    }

    // ===================== Đẩy Hub đơn hàng: GetForHubPush / MarkHubSynced =====================

    [Fact]
    public void GetForHubPush_DonMoi_CoMat_MapDayDuField()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        repo.UpsertMany(1, new[] { Sample("SN1"), Sample("SN2") }, DateTime.UtcNow);

        var pending = repo.GetForHubPush(1);

        Assert.Equal(new[] { "SN1", "SN2" }, pending.Select(o => o.OrderSn)); // đơn mới đều CHỜ đẩy hub, đúng thứ tự id

        // Dựng lại SyncedOrder đầy đủ từ cột bảng (mirror để client map 1-1 sang DTO hub).
        var o = pending.First(p => p.OrderSn == "SN1");
        Assert.Equal("237900524283161", o.ShopeeOrderId);
        Assert.Equal("quynhsuugiacshoppi", o.BuyerUsername);
        Assert.Equal(1, o.ItemCount);
        Assert.Equal("Giày", o.ItemSummary);
        Assert.Equal(166500, o.TotalPrice);
        Assert.Equal("₫166.500", o.TotalPriceText);
        Assert.Equal(160000, o.FinalAmount);
        Assert.Equal("₫160.000", o.FinalAmountText);
        Assert.Equal("Đã hủy", o.Status);
        Assert.Equal("SPX Express", o.Carrier);
        Assert.Equal("SPXVN068067521447", o.TrackingNumber);
        Assert.Equal("Hủy đơn hàng vì hành vi giao dịch bất thường.", o.CancelReason);
    }

    [Fact]
    public void MarkHubSynced_DanhDau_BienMatKhoiPending()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Sample("SN1"), Sample("SN2"), Sample("SN3") }, DateTime.UtcNow);

        // Đánh dấu SN1 + SN2 đã đẩy hub → chỉ SN3 còn CHỜ.
        repo.MarkHubSynced(1, new[] { "SN1", "SN2" }, DateTime.UtcNow);

        var pending = repo.GetForHubPush(1);
        Assert.Equal(new[] { "SN3" }, pending.Select(o => o.OrderSn));
    }

    [Fact]
    public void MarkHubSynced_TatCa_PendingRong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Sample("SN1"), Sample("SN2") }, DateTime.UtcNow);

        repo.MarkHubSynced(1, new[] { "SN1", "SN2" }, DateTime.UtcNow);

        Assert.Empty(repo.GetForHubPush(1));
    }

    [Fact]
    public void MarkHubSynced_HaiLan_GiuMocDau()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);

        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        repo.MarkHubSynced(1, new[] { "SN1" }, t1);
        var mark1 = ReadString(db, "SN1", "hub_synced_at");
        Assert.NotNull(mark1);

        // Đánh dấu LẦN 2 với mốc KHÁC → COALESCE giữ mốc lần đầu, KHÔNG đè.
        var t2 = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        repo.MarkHubSynced(1, new[] { "SN1" }, t2);
        Assert.Equal(mark1, ReadString(db, "SN1", "hub_synced_at"));
    }

    [Fact]
    public void UpsertMany_SoTienCuoiCungVuaCo_ResetCoDayHub()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        // Lượt 1: đơn lên hub khi CHƯA có "Số tiền cuối cùng" (chưa mở trang chi tiết) — tracking đã có sẵn
        // để chắc chắn nhánh reset KHÔNG phải do vận đơn.
        var chuaCoTien = Sample("SN1");
        chuaCoTien.FinalAmount = null;
        chuaCoTien.FinalAmountText = null;
        repo.UpsertMany(1, new[] { chuaCoTien }, DateTime.UtcNow);
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);
        Assert.Empty(repo.GetForHubPush(1));

        // Lượt 2: lấy được số tiền cuối cùng → PHẢI reset cờ để đẩy LẠI, kẻo hub hiển thị "—" vĩnh viễn.
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);

        var pending = repo.GetForHubPush(1);
        Assert.Equal(new[] { "SN1" }, pending.Select(o => o.OrderSn));
        Assert.Equal(160000, pending[0].FinalAmount);
    }

    [Fact]
    public void UpsertMany_SoTienCuoiCungDaCo_KhongResetCoDayHub()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        // Đơn ĐÃ có số tiền cuối cùng ngay từ đầu + đã đẩy hub.
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);

        // Sync lại (vẫn số tiền đó) → KHÔNG được reset cờ, tránh đẩy lại vô hạn mỗi lượt sync.
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);

        Assert.Empty(repo.GetForHubPush(1));
    }

    [Fact]
    public void UpsertMany_TrangThaiDoi_ResetCoDayHub()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        // Lượt 1: đơn "Chờ lấy hàng" đã lên hub (vận đơn + số tiền cuối cùng đã có sẵn để chắc chắn nhánh reset
        // KHÔNG phải do hai điều kiện cũ).
        var choLay = Sample("SN1");
        choLay.Status = "Chờ lấy hàng";
        repo.UpsertMany(1, new[] { choLay }, DateTime.UtcNow);
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);
        Assert.Empty(repo.GetForHubPush(1));

        // Lượt 2: Shopee hủy đơn → status đổi → PHẢI reset cờ, kẻo hub kẹt "Chờ lấy hàng" vĩnh viễn.
        var daHuy = Sample("SN1");
        daHuy.Status = "Đã hủy";
        repo.UpsertMany(1, new[] { daHuy }, DateTime.UtcNow);

        var pending = repo.GetForHubPush(1);
        Assert.Equal(new[] { "SN1" }, pending.Select(o => o.OrderSn));
        Assert.Equal("Đã hủy", pending[0].Status);
    }

    [Fact]
    public void UpsertMany_ChiStatusDescriptionDoi_KhongResetCoDayHub()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);

        // status + cancel_reason Y HỆT, chỉ status_description đổi (mô tả hay dao động: đếm ngược, nhắc nhở…)
        // → KHÔNG reset, kẻo đơn bị đẩy lại hub MỖI lượt sync.
        var moTaKhac = Sample("SN1");
        moTaKhac.StatusDescription = "Vui lòng giao hàng trước 23:59 hôm nay";
        repo.UpsertMany(1, new[] { moTaKhac }, DateTime.UtcNow);

        Assert.Empty(repo.GetForHubPush(1));
    }

    [Fact]
    public void UpsertMany_LyDoHuyVuaCo_ResetCoDayHub()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        // Lượt 1: đơn đã lên hub khi CHƯA có lý do hủy.
        var chuaCoLyDo = Sample("SN1");
        chuaCoLyDo.CancelReason = null;
        repo.UpsertMany(1, new[] { chuaCoLyDo }, DateTime.UtcNow);
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);
        Assert.Empty(repo.GetForHubPush(1));

        // Lượt 2: đọc được lý do hủy (status không đổi) → vẫn PHẢI reset để hub nhận lý do.
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);

        var pending = repo.GetForHubPush(1);
        Assert.Equal(new[] { "SN1" }, pending.Select(o => o.OrderSn));
        Assert.Equal("Hủy đơn hàng vì hành vi giao dịch bất thường.", pending[0].CancelReason);
    }

    [Fact]
    public void UpsertMany_VanDonKhongDocDuoc_GiuMaCu()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);

        // Lượt sau KHÔNG đọc được vận đơn (đơn "Đã hủy" nên danh sách không hiện cột) → COALESCE GIỮ mã cũ.
        // Mất vận đơn kéo theo đơn hủy rơi vào nhánh bỏ-qua của GSheet (không tô đỏ) và hub mất dữ liệu.
        var matVanDon = Sample("SN1");
        matVanDon.TrackingNumber = null;
        matVanDon.TotalPrice = 200000; // trường khác vẫn cập nhật bình thường
        repo.UpsertMany(1, new[] { matVanDon }, DateTime.UtcNow);

        Assert.Equal("SPXVN068067521447", ReadString(db, "SN1", "tracking_number"));
        Assert.Equal("200000", ReadString(db, "SN1", "total_price"));
    }

    [Fact]
    public void GetForHubPush_MarkHubSynced_KhongLanTaiKhoanKhac()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);
        repo.UpsertMany(2, new[] { Sample("SN1") }, DateTime.UtcNow); // cùng mã đơn, tài khoản KHÁC

        // Đánh dấu SN1 của tài khoản 1 → KHÔNG ảnh hưởng SN1 của tài khoản 2.
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);

        Assert.Empty(repo.GetForHubPush(1));                              // tài khoản 1 đã đẩy
        Assert.Equal(new[] { "SN1" }, repo.GetForHubPush(2).Select(o => o.OrderSn)); // tài khoản 2 vẫn chờ
    }

    [Fact]
    public void GetForHubPush_MangShopLogin_NullChoDonCu()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        // SN1: có shop_login (đẩy hub nhóm theo shop); OLD: đơn cũ (shop_login NULL → fallback subaccount).
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow, shopId: "1001", shopLogin: "alina99.store");
        repo.UpsertMany(1, new[] { Sample("OLD") }, DateTime.UtcNow);

        var pending = repo.GetForHubPush(1);
        Assert.Equal("alina99.store", pending.First(p => p.OrderSn == "SN1").ShopLogin);
        Assert.Null(pending.First(p => p.OrderSn == "OLD").ShopLogin);
    }

    // ===================== Tra shop_login theo mã đơn (đẩy PHIẾU nhóm theo shop) =====================

    [Fact]
    public void GetShopLoginsByOrderSns_TraMap_NullChoDonCu_BoDonKhongCoMa()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow, shopLogin: "alina99.store");
        repo.UpsertMany(1, new[] { Sample("SN2") }, DateTime.UtcNow, shopLogin: "shop9x.store");
        repo.UpsertMany(1, new[] { Sample("OLD") }, DateTime.UtcNow); // shop_login NULL (đơn cũ)

        var map = repo.GetShopLoginsByOrderSns(1, new[] { "SN1", "SN2", "OLD", "GHOST" });

        Assert.Equal("alina99.store", map["SN1"]);
        Assert.Equal("shop9x.store", map["SN2"]);
        Assert.Null(map["OLD"]);                 // đơn cũ → null trong map (caller fallback subaccount)
        Assert.False(map.ContainsKey("GHOST"));  // đơn không tồn tại → KHÔNG có trong map
    }

    [Fact]
    public void GetShopLoginsByOrderSns_Rong_TraDictRong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow, shopLogin: "alina99.store");

        Assert.Empty(repo.GetShopLoginsByOrderSns(1, Array.Empty<string>()));
    }

    [Fact]
    public void GetShopLoginsByOrderSns_KhongLanTaiKhoanKhac()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow, shopLogin: "alina99.store");
        repo.UpsertMany(2, new[] { Sample("SN1") }, DateTime.UtcNow, shopLogin: "shop9x.store"); // cùng mã, account KHÁC

        var map = repo.GetShopLoginsByOrderSns(1, new[] { "SN1" });
        Assert.Equal("alina99.store", Assert.Single(map).Value); // chỉ account 1
    }

    // ===================== Đẩy PHIẾU lên Hub: GetForHubSlipPush / MarkHubSlipSynced =====================

    [Fact]
    public void GetForHubSlipPush_ChiDon_DaLenHub_CoVanDon_ChuaDayPhieu()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        // SN1/SN2: Sample() → CÓ vận đơn; NOTRACK: Make() → KHÔNG vận đơn.
        repo.UpsertMany(1, new[] { Sample("SN1"), Sample("SN2"), Make("NOTRACK", status: "Chờ lấy hàng") }, DateTime.UtcNow);

        // Chưa đơn nào lên hub → hàng đợi đẩy phiếu RỖNG (điều kiện hub_synced_at IS NOT NULL).
        Assert.Empty(repo.GetForHubSlipPush(1));

        // SN1 + NOTRACK lên hub; SN2 CHƯA lên hub.
        repo.MarkHubSynced(1, new[] { "SN1", "NOTRACK" }, DateTime.UtcNow);

        // Chỉ SN1 đủ điều kiện: đã lên hub + có vận đơn + chưa đẩy phiếu. NOTRACK loại (không vận đơn), SN2 loại (chưa lên hub).
        var pending = repo.GetForHubSlipPush(1);
        Assert.Equal(new[] { "SN1" }, pending.Select(p => p.OrderSn));
        Assert.Equal("SPXVN068067521447", pending[0].TrackingNumber);
    }

    [Fact]
    public void MarkHubSlipSynced_DanhDau_BienMatKhoiPending()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Sample("SN1"), Sample("SN2") }, DateTime.UtcNow);
        repo.MarkHubSynced(1, new[] { "SN1", "SN2" }, DateTime.UtcNow); // cả 2 lên hub

        Assert.Equal(new[] { "SN1", "SN2" }, repo.GetForHubSlipPush(1).Select(p => p.OrderSn));

        // Đánh dấu SN1 đã đẩy phiếu → chỉ SN2 còn chờ đẩy phiếu.
        repo.MarkHubSlipSynced(1, new[] { "SN1" }, DateTime.UtcNow);
        Assert.Equal(new[] { "SN2" }, repo.GetForHubSlipPush(1).Select(p => p.OrderSn));
    }

    [Fact]
    public void MarkHubSlipSynced_HaiLan_GiuMocDau()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);

        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        repo.MarkHubSlipSynced(1, new[] { "SN1" }, t1);
        var mark1 = ReadString(db, "SN1", "hub_slip_synced_at");
        Assert.NotNull(mark1);

        // Đánh dấu LẦN 2 với mốc KHÁC → COALESCE giữ mốc lần đầu, KHÔNG đè.
        var t2 = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        repo.MarkHubSlipSynced(1, new[] { "SN1" }, t2);
        Assert.Equal(mark1, ReadString(db, "SN1", "hub_slip_synced_at"));
    }

    [Fact]
    public void GetForGsheetPush_MapDaDayPhieuHub_TheoCotNull()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Sample("A_NONE"), Sample("B_SLIP") }, DateTime.UtcNow);

        // B_SLIP: đánh cờ hub_slip_synced_at; A_NONE: không.
        repo.MarkHubSlipSynced(1, new[] { "B_SLIP" }, DateTime.UtcNow);

        var pending = repo.GetForGsheetPush(1);
        Assert.False(pending.First(p => p.OrderSn == "A_NONE").DaDayPhieuHub);
        Assert.True(pending.First(p => p.OrderSn == "B_SLIP").DaDayPhieuHub);
    }

    // ===================== Query / AllStatuses (màn xem — plan 2) =====================

    /// <summary>Tạo nhanh một đơn với vài trường phục vụ test lọc.</summary>
    private static SyncedOrder Make(string sn, string? status = null, string? buyer = null,
        string? summary = null, int itemCount = 1) => new()
        {
            OrderSn = sn,
            Status = status,
            BuyerUsername = buyer,
            ItemSummary = summary,
            ItemCount = itemCount,
        };

    [Fact]
    public void Query_KhongLoc_TraTatCa_SyncMoiNhatTruoc()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        repo.UpsertMany(1, new[] { Make("OLD") }, t1);
        repo.UpsertMany(1, new[] { Make("NEW") }, t2);

        var rows = repo.Query();

        Assert.Equal(2, rows.Count);
        Assert.Equal("NEW", rows[0].OrderSn); // synced_at mới hơn đứng trước
        Assert.Equal("OLD", rows[1].OrderSn);
    }

    [Fact]
    public void Query_MapDayDuTruong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        var syncedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        repo.UpsertMany(42, new[] { Sample("SN1") }, syncedAt);

        var row = Assert.Single(repo.Query());
        Assert.Equal(42, row.AccountId);
        Assert.Equal("SN1", row.OrderSn);
        Assert.Equal("quynhsuugiacshoppi", row.BuyerUsername);
        Assert.Equal("Giày", row.ItemSummary);
        Assert.Equal(166500, row.TotalPrice);
        Assert.Equal("₫166.500", row.TotalPriceText);
        Assert.Equal(160000, row.FinalAmount);
        Assert.Equal("₫160.000", row.FinalAmountText);
        Assert.Equal("Đã hủy", row.Status);
        Assert.Equal("SPX Express", row.Carrier);
        Assert.Equal("SPXVN068067521447", row.TrackingNumber);
        Assert.Equal(syncedAt, row.SyncedAt);
        // items_json (nguồn suy cột "Phân loại" của lưới) — cột thêm ở CUỐI SELECT, chỉ số phải khớp.
        Assert.Equal(Sample("x").ItemsJson, row.ItemsJson);
    }

    [Fact]
    public void Query_LocTheoTaiKhoan()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("A1"), Make("A2") }, DateTime.UtcNow);
        repo.UpsertMany(2, new[] { Make("B1") }, DateTime.UtcNow);

        Assert.Equal(2, repo.Query(accountId: 1).Count);
        var only2 = Assert.Single(repo.Query(accountId: 2));
        Assert.Equal("B1", only2.OrderSn);
    }

    [Fact]
    public void Query_LocTrangThai_ChinhXac_KhongDinhChuoiCon()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            Make("S1", status: "Đã hủy"),
            Make("S2", status: "Đã hủy một phần"),
            Make("S3", status: "Chờ lấy hàng"),
        }, DateTime.UtcNow);

        var huy = repo.Query(status: "Đã hủy");
        var only = Assert.Single(huy); // KHÔNG dính "Đã hủy một phần"
        Assert.Equal("S1", only.OrderSn);
    }

    [Fact]
    public void Query_TimKiem_TheoMaDon_NguoiMua_SanPham()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            Make("ABC123", buyer: "nguyenvana", summary: "Áo thun"),
            Make("XYZ999", buyer: "tranthib", summary: "Quần jean"),
        }, DateTime.UtcNow);

        Assert.Single(repo.Query(searchText: "ABC"));      // theo mã đơn
        Assert.Single(repo.Query(searchText: "tranthi"));  // theo người mua
        Assert.Single(repo.Query(searchText: "jean"));     // theo tên sản phẩm
        Assert.Equal(2, repo.Query(searchText: "  ").Count); // chỉ khoảng trắng = không lọc
    }

    [Fact]
    public void Query_TimKiem_KyTuDaiDien_DuocEscape()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            Make("N100", summary: "Giày 50%"),
            Make("N200", summary: "Giày x"),
        }, DateTime.UtcNow);

        // "%" phải khớp ĐÚNG ký tự '%' (nếu không escape, LIKE %50%% sẽ dính cả hai dòng).
        var rows = repo.Query(searchText: "50%");
        var only = Assert.Single(rows);
        Assert.Equal("N100", only.OrderSn);
    }

    [Fact]
    public void Query_KetHop_TaiKhoan_TrangThai_TimKiem()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            Make("K1", status: "Đã hủy", buyer: "alpha"),
            Make("K2", status: "Đã hủy", buyer: "beta"),
            Make("K3", status: "Chờ lấy hàng", buyer: "alpha"),
        }, DateTime.UtcNow);
        repo.UpsertMany(2, new[] { Make("K4", status: "Đã hủy", buyer: "alpha") }, DateTime.UtcNow);

        var rows = repo.Query(accountId: 1, status: "Đã hủy", searchText: "alpha");
        var only = Assert.Single(rows);
        Assert.Equal("K1", only.OrderSn);
    }

    [Fact]
    public void Query_Count_LocTheoCreatedAt_BienDongMo_DungNgay()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        repo.UpsertMany(1, new[] { Make("BEFORE") }, new DateTime(2026, 7, 9, 23, 59, 59, DateTimeKind.Utc));
        repo.UpsertMany(1, new[] { Make("START") }, new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc));
        repo.UpsertMany(1, new[] { Make("INSIDE") }, new DateTime(2026, 7, 10, 23, 59, 59, DateTimeKind.Utc));
        repo.UpsertMany(1, new[] { Make("AFTER") }, new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));

        var fromUtc = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        var beforeUtc = new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc);

        var rows = repo.Query(createdFromUtc: fromUtc, createdBeforeUtc: beforeUtc);

        Assert.Equal(new[] { "INSIDE", "START" }, rows.Select(r => r.OrderSn));
        Assert.Equal(2, repo.Count(createdFromUtc: fromUtc, createdBeforeUtc: beforeUtc));
    }

    [Fact]
    public void Query_Count_LocTheoCreatedAt_KetHopShop_VaBoTrongThiGiuHanhViCu()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        var fromUtc = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        var beforeUtc = new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc);

        repo.UpsertMany(1, new[] { Make("A_IN") }, new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc), shopLogin: "shop-a");
        repo.UpsertMany(1, new[] { Make("A_OUT") }, new DateTime(2026, 7, 12, 8, 0, 0, DateTimeKind.Utc), shopLogin: "shop-a");
        repo.UpsertMany(1, new[] { Make("B_IN") }, new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc), shopLogin: "shop-b");

        var filtered = repo.Query(
            shopLogin: "shop-a",
            shopExact: true,
            createdFromUtc: fromUtc,
            createdBeforeUtc: beforeUtc);

        Assert.Equal(new[] { "A_IN" }, filtered.Select(r => r.OrderSn));
        Assert.Equal(1, repo.Count(
            shopLogin: "shop-a",
            shopExact: true,
            createdFromUtc: fromUtc,
            createdBeforeUtc: beforeUtc));

        Assert.Equal(3, repo.Query().Count);
        Assert.Equal(3, repo.Count());
    }

    [Fact]
    public void AllStatuses_Distinct_SapXep_BoNull()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            Make("A", status: "Chờ lấy hàng"),
            Make("B", status: "Đã hủy"),
            Make("C", status: "Chờ lấy hàng"), // trùng → gộp
            Make("D", status: null),           // null → bỏ
        }, DateTime.UtcNow);

        Assert.Equal(new[] { "Chờ lấy hàng", "Đã hủy" }, repo.AllStatuses());
    }

    [Fact]
    public void AllStatuses_LocTheoTaiKhoan()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("A", status: "Đã hủy") }, DateTime.UtcNow);
        repo.UpsertMany(2, new[] { Make("B", status: "Chờ lấy hàng") }, DateTime.UtcNow);

        Assert.Equal(new[] { "Đã hủy" }, repo.AllStatuses(accountId: 1));
        Assert.Equal(new[] { "Chờ lấy hàng" }, repo.AllStatuses(accountId: 2));
    }

    // ===================== Phân trang (LIMIT/OFFSET) + Count + accountIds IN (...) =====================

    [Fact]
    public void Query_LimitOffset_TraDungLatCat_TheoThuTuSapXep()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        // Cùng synced_at → ORDER BY synced_at DESC, id DESC → id DESC: SN5, SN4, SN3, SN2, SN1.
        repo.UpsertMany(1, new[] { Make("SN1"), Make("SN2"), Make("SN3"), Make("SN4"), Make("SN5") }, DateTime.UtcNow);

        var p1 = repo.Query(limit: 2, offset: 0);
        Assert.Equal(new[] { "SN5", "SN4" }, p1.Select(r => r.OrderSn));

        var p2 = repo.Query(limit: 2, offset: 2);
        Assert.Equal(new[] { "SN3", "SN2" }, p2.Select(r => r.OrderSn));

        var p3 = repo.Query(limit: 2, offset: 4); // trang cuối lẻ 1 đơn
        Assert.Equal(new[] { "SN1" }, p3.Select(r => r.OrderSn));

        // offset null → 0 (chỉ giới hạn số dòng, từ đầu).
        Assert.Equal(new[] { "SN5", "SN4", "SN3" }, repo.Query(limit: 3).Select(r => r.OrderSn));
    }

    [Fact]
    public void Count_DungTheoTungBoLoc_KhopSoDongQuery()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[]
        {
            Make("K1", status: "Đã hủy", buyer: "alpha"),
            Make("K2", status: "Đã hủy", buyer: "beta"),
            Make("K3", status: "Chờ lấy hàng", buyer: "alpha"),
        }, DateTime.UtcNow);
        repo.UpsertMany(2, new[] { Make("K4", status: "Đã hủy", buyer: "alpha") }, DateTime.UtcNow);

        Assert.Equal(4, repo.Count());                                  // không lọc
        Assert.Equal(3, repo.Count(accountId: 1));                      // theo tài khoản
        Assert.Equal(3, repo.Count(status: "Đã hủy"));                  // theo trạng thái
        Assert.Equal(2, repo.Count(accountId: 1, searchText: "alpha")); // kết hợp
        // Count phải khớp số dòng Query (không phân trang) cho cùng bộ lọc.
        Assert.Equal(repo.Query(accountId: 1).Count, repo.Count(accountId: 1));
    }

    [Fact]
    public void Query_Count_AccountIds_NhieuId_UuTien_TapRong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("A1") }, DateTime.UtcNow);
        repo.UpsertMany(2, new[] { Make("B1"), Make("B2") }, DateTime.UtcNow);
        repo.UpsertMany(3, new[] { Make("C1") }, DateTime.UtcNow);

        // HỢP tài khoản 1 và 3 → A1 + C1 (KHÔNG lẫn tài khoản 2).
        var rows = repo.Query(accountIds: new long[] { 1, 3 });
        Assert.Equal(new[] { "A1", "C1" }, rows.Select(r => r.OrderSn).OrderBy(s => s));
        Assert.Equal(2, repo.Count(accountIds: new long[] { 1, 3 }));

        // Tập RỖNG → list rỗng + Count 0 (short-circuit, KHÔNG lỗi IN ()).
        Assert.Empty(repo.Query(accountIds: Array.Empty<long>()));
        Assert.Equal(0, repo.Count(accountIds: Array.Empty<long>()));

        // accountIds được ưu tiên hơn accountId khi truyền cả hai.
        var both = repo.Query(accountId: 2, accountIds: new long[] { 1 });
        Assert.Equal(new[] { "A1" }, both.Select(r => r.OrderSn));

        // accountIds kết hợp phân trang + bộ lọc khác.
        var page = repo.Query(accountIds: new long[] { 1, 2, 3 }, status: null, limit: 2, offset: 0);
        Assert.Equal(2, page.Count);
        Assert.Equal(4, repo.Count(accountIds: new long[] { 1, 2, 3 }));
    }

    // ===================== shop_id (mô hình 1 subaccount = nhiều shop) =====================

    [Fact]
    public void UpsertMany_GanShopId_LuuVaoCotShopId_UpsertLaiKhongTruyen_GiuShopId()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow, shopId: "SHOP_A");
        Assert.Equal("SHOP_A", ReadString(db, "SN1", "shop_id"));

        // Upsert lại KHÔNG truyền shopId (null) → COALESCE GIỮ shop_id cũ, KHÔNG xóa.
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);
        Assert.Equal("SHOP_A", ReadString(db, "SN1", "shop_id"));
    }

    [Fact]
    public void UpsertMany_KhongTruyenShopId_ShopIdNull()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);
        Assert.Null(ReadString(db, "SN1", "shop_id"));
    }

    [Fact]
    public void GetForGsheetPush_LocTheoShop_ChiTraDonCuaShop_NullTraTatCa()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        // Cùng account 1, 2 shop khác nhau (mã đơn gắn shop tương ứng).
        repo.UpsertMany(1, new[] { Sample("A1"), Sample("A2") }, DateTime.UtcNow, shopId: "SHOP_A");
        repo.UpsertMany(1, new[] { Sample("B1") }, DateTime.UtcNow, shopId: "SHOP_B");

        Assert.Equal(new[] { "A1", "A2" }, repo.GetForGsheetPush(1, "SHOP_A").Select(p => p.OrderSn));
        Assert.Equal(new[] { "B1" }, repo.GetForGsheetPush(1, "SHOP_B").Select(p => p.OrderSn));

        // shopId null → MỌI đơn của account (hành vi cũ, giữ tương thích).
        Assert.Equal(new[] { "A1", "A2", "B1" }, repo.GetForGsheetPush(1).Select(p => p.OrderSn));

        // Shop không có đơn → rỗng (KHÔNG ném).
        Assert.Empty(repo.GetForGsheetPush(1, "SHOP_X"));
    }

    [Fact]
    public void GetForGsheetPush_LocShop_BoDonShopIdNull()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        // Đơn CŨ (shop_id NULL — vd đơn trước bản này) + đơn shop mới.
        repo.UpsertMany(1, new[] { Sample("OLD") }, DateTime.UtcNow);                   // shop_id NULL
        repo.UpsertMany(1, new[] { Sample("NEW") }, DateTime.UtcNow, shopId: "SHOP_A");

        // Lọc theo shop → chỉ đơn shop đó (đơn shop_id NULL KHÔNG lọt → không đẩy nhầm tên shop).
        Assert.Equal(new[] { "NEW" }, repo.GetForGsheetPush(1, "SHOP_A").Select(p => p.OrderSn));

        // Không lọc → cả 2 (hành vi cũ theo account).
        Assert.Equal(2, repo.GetForGsheetPush(1).Count);
    }

    // ===================== shop_login (tên shop cho màn Đơn hàng: hiển thị + lọc) =====================

    [Fact]
    public void UpsertMany_GanShopLogin_LuuVaoCot_UpsertLaiKhongTruyen_GiuShopLogin()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow, shopId: "1001", shopLogin: "alina99.store");
        Assert.Equal("alina99.store", ReadString(db, "SN1", "shop_login"));

        // Upsert lại KHÔNG truyền shopLogin (null) → COALESCE GIỮ shop_login cũ, KHÔNG xóa.
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);
        Assert.Equal("alina99.store", ReadString(db, "SN1", "shop_login"));
    }

    [Fact]
    public void UpsertMany_KhongTruyenShopLogin_ShopLoginNull()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var repo = new OrdersRepository(db);

        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);
        Assert.Null(ReadString(db, "SN1", "shop_login"));
    }

    [Fact]
    public void Query_MapShopLogin()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("SN1") }, DateTime.UtcNow, shopLogin: "shop9x.store");

        var row = Assert.Single(repo.Query());
        Assert.Equal("shop9x.store", row.ShopLogin);
    }

    [Fact]
    public void Query_Count_LocTheoShopLogin_Exact_KhopChinhXac()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        // 2 shop khác nhau (cùng account để chứng minh lọc theo shop, KHÔNG theo account).
        repo.UpsertMany(1, new[] { Make("A1"), Make("A2") }, DateTime.UtcNow, shopLogin: "alina99.store");
        repo.UpsertMany(1, new[] { Make("B1") }, DateTime.UtcNow, shopLogin: "alina99.store.vip"); // chứa chuỗi con

        var exact = repo.Query(shopLogin: "alina99.store", shopExact: true);
        Assert.Equal(new[] { "A1", "A2" }, exact.Select(r => r.OrderSn).OrderBy(s => s));  // KHÔNG dính "alina99.store.vip"
        Assert.Equal(2, repo.Count(shopLogin: "alina99.store", shopExact: true));

        // shopLogin null → mọi đơn (không lọc shop).
        Assert.Equal(3, repo.Query(shopLogin: null).Count);
        Assert.Equal(3, repo.Count());
    }

    [Fact]
    public void Query_Count_LocTheoShopLogin_Like_GoDo()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("M1") }, DateTime.UtcNow, shopLogin: "mexico1.store");
        repo.UpsertMany(2, new[] { Make("M2") }, DateTime.UtcNow, shopLogin: "mexstyle.store");
        repo.UpsertMany(3, new[] { Make("O1") }, DateTime.UtcNow, shopLogin: "alpha.store");

        // Gõ dở "mex" (shopExact=false) → LIKE %mex% → 2 shop chứa "mex", KHÔNG dính "alpha.store".
        var like = repo.Query(shopLogin: "mex", shopExact: false);
        Assert.Equal(new[] { "M1", "M2" }, like.Select(r => r.OrderSn).OrderBy(s => s));
        Assert.Equal(2, repo.Count(shopLogin: "mex", shopExact: false));

        // Không khớp shop nào → 0 dòng (KHÔNG ném).
        Assert.Empty(repo.Query(shopLogin: "zzz", shopExact: false));
        Assert.Equal(0, repo.Count(shopLogin: "zzz", shopExact: false));
    }

    [Fact]
    public void Query_LocShop_BoDonShopLoginNull()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("OLD") }, DateTime.UtcNow);                          // shop_login NULL (đơn cũ)
        repo.UpsertMany(1, new[] { Make("NEW") }, DateTime.UtcNow, shopLogin: "alina99.store");

        // Lọc theo shop → chỉ đơn shop đó (đơn shop_login NULL KHÔNG lọt).
        Assert.Equal(new[] { "NEW" }, repo.Query(shopLogin: "alina99.store", shopExact: true).Select(r => r.OrderSn));
        // Không lọc → cả 2.
        Assert.Equal(2, repo.Query().Count);
    }

    [Fact]
    public void AllShopLogins_Distinct_SapXep_BoNullVaRong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("A") }, DateTime.UtcNow, shopLogin: "shop9x.store");
        repo.UpsertMany(1, new[] { Make("B") }, DateTime.UtcNow, shopLogin: "alina99.store");
        repo.UpsertMany(2, new[] { Make("C") }, DateTime.UtcNow, shopLogin: "shop9x.store"); // trùng → gộp
        repo.UpsertMany(2, new[] { Make("D") }, DateTime.UtcNow);                            // NULL → bỏ
        repo.UpsertMany(2, new[] { Make("E") }, DateTime.UtcNow, shopLogin: "   ");          // rỗng/khoảng trắng → bỏ

        Assert.Equal(new[] { "alina99.store", "shop9x.store" }, repo.AllShopLogins());
    }

    [Fact]
    public void AllStatuses_LocTheoShopLogin()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Make("A", status: "Đã hủy") }, DateTime.UtcNow, shopLogin: "alina99.store");
        repo.UpsertMany(1, new[] { Make("B", status: "Chờ lấy hàng") }, DateTime.UtcNow, shopLogin: "shop9x.store");

        Assert.Equal(new[] { "Đã hủy" }, repo.AllStatuses(shopLogin: "alina99.store"));
        Assert.Equal(new[] { "Chờ lấy hàng" }, repo.AllStatuses(shopLogin: "shop9x.store"));
    }

    [Fact]
    public void MaxSyncedAtByAccount_MoiTaiKhoan_LayLuotSyncMoiNhat()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        var cu = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);
        var moi = new DateTime(2026, 7, 20, 8, 30, 0, DateTimeKind.Utc);
        repo.UpsertMany(1, new[] { Make("A") }, cu);
        repo.UpsertMany(1, new[] { Make("B") }, moi);     // tài khoản 1: lấy mốc MỚI nhất
        repo.UpsertMany(2, new[] { Make("C") }, cu);

        var map = repo.MaxSyncedAtByAccount();

        Assert.Equal(moi, map[1]);
        Assert.Equal(cu, map[2]);
        Assert.False(map.ContainsKey(3));                 // tài khoản chưa có đơn → KHÔNG có khóa
    }

    [Fact]
    public void MaxSyncedAtByAccount_KhoDonRong_TraMapRong()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());

        Assert.Empty(repo.MaxSyncedAtByAccount());
    }

    /// <summary>Đọc 1 cột (dạng chuỗi) của đơn theo order_sn — kiểm chứng trực tiếp trên DB.</summary>
    private static string? ReadString(Database db, string orderSn, string column)
    {
        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM orders WHERE order_sn = $sn;";
        cmd.Parameters.AddWithValue("$sn", orderSn);
        var res = cmd.ExecuteScalar();
        return res is null || res == DBNull.Value ? null : res.ToString();
    }
}
