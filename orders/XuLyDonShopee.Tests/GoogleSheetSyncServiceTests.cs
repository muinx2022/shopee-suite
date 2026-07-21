using System.Text.Json;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test các hàm thuần (static) của <see cref="GoogleSheetSyncService"/> — hợp đồng với Apps Script Web App:
/// chia lô ≤ 10 đơn; body JSON có "tab" + camelCase / bỏ field null / số để dạng số / "daHuy" luôn có mặt;
/// parse phản hồi results, và {"error":…} → ném.
/// </summary>
public class GoogleSheetSyncServiceTests
{
    private static GsheetOrderRow Row(string maDon, bool daHuy = false) =>
        new(maDon, null, null, null, null, null, null, null, daHuy);

    // ===== ChiaLo: chia lô tối đa 10 đơn, giữ đủ phần tử =====
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(25, 3)]
    public void ChiaLo_ChiaDungSoLo(int count, int expectedBatches)
    {
        var rows = Enumerable.Range(0, count).Select(i => Row($"D{i}")).ToList();

        var batches = GoogleSheetSyncService.ChiaLo(rows, 10);

        Assert.Equal(expectedBatches, batches.Count);
        Assert.Equal(count, batches.Sum(b => b.Count));         // không mất/thêm phần tử
        Assert.All(batches, b => Assert.True(b.Count <= 10));   // mỗi lô ≤ 10
    }

    // ===== TaoJsonBody: có "tab", camelCase, bỏ field null, số KHÔNG bọc nháy, daHuy LUÔN có mặt =====
    [Fact]
    public void TaoJsonBody_CoTab_BoFieldNull_SoLaSo_CamelCase_DaHuyLuonCo()
    {
        var rows = new[]
        {
            new GsheetOrderRow("D1", "SPXVN1", "sully", 166500, "20/07/2026", "B02435", null, null, false),
        };

        var json = GoogleSheetSyncService.TaoJsonBody("tháng 4", rows);

        Assert.Contains("\"tab\":\"tháng 4\"", json);       // tab đích
        Assert.Contains("\"orders\"", json);
        Assert.Contains("\"maDon\":\"D1\"", json);
        Assert.Contains("\"maVanDon\":\"SPXVN1\"", json);
        Assert.Contains("\"tenShop\":\"sully\"", json);
        Assert.Contains("\"doanhThu\":166500", json);       // số — KHÔNG bọc nháy
        Assert.Contains("\"ngay\":\"20/07/2026\"", json);
        Assert.Contains("\"sku\":\"B02435\"", json);
        Assert.Contains("\"daHuy\":false", json);           // LUÔN có mặt kể cả false
        Assert.DoesNotContain("fileName", json);            // null → bỏ
        Assert.DoesNotContain("fileBase64", json);          // null → bỏ
    }

    [Fact]
    public void TaoJsonBody_DaHuyTrue_XuatHien()
    {
        var rows = new[]
        {
            new GsheetOrderRow("D9", null, null, null, null, null, null, null, true),
        };

        var json = GoogleSheetSyncService.TaoJsonBody("tháng 4", rows);

        Assert.Contains("\"daHuy\":true", json);
    }

    [Fact]
    public void TaoJsonBody_CoFile_GiuFieldFile_BoFieldNullKhac()
    {
        var rows = new[]
        {
            new GsheetOrderRow("D1", null, null, null, null, null, "D1.pdf", "QUJD", false),
        };

        var json = GoogleSheetSyncService.TaoJsonBody("tháng 5", rows);

        Assert.Contains("\"tab\":\"tháng 5\"", json);
        Assert.Contains("\"fileName\":\"D1.pdf\"", json);
        Assert.Contains("\"fileBase64\":\"QUJD\"", json);
        Assert.DoesNotContain("maVanDon", json);   // null → bỏ
        Assert.DoesNotContain("doanhThu", json);   // null → bỏ
    }

    // ===== DocKetQua: parse results, thiếu field → mặc định an toàn, {"error"} → ném, JSON rác → ném =====
    [Fact]
    public void DocKetQua_JsonChuan_ParseDayDu()
    {
        const string json =
            "{\"results\":[{\"maDon\":\"D1\",\"ok\":true,\"added\":true,\"fileUrl\":\"http://x/1\",\"error\":null}]}";

        var r = Assert.Single(GoogleSheetSyncService.DocKetQua(json));

        Assert.Equal("D1", r.MaDon);
        Assert.True(r.Ok);
        Assert.True(r.Added);
        Assert.Equal("http://x/1", r.FileUrl);
        Assert.Null(r.Error);
    }

    [Fact]
    public void DocKetQua_ThieuField_MacDinhAnToan()
    {
        const string json = "{\"results\":[{\"maDon\":\"D2\",\"ok\":true}]}";

        var r = Assert.Single(GoogleSheetSyncService.DocKetQua(json));

        Assert.Equal("D2", r.MaDon);
        Assert.True(r.Ok);
        Assert.False(r.Added);   // thiếu → false
        Assert.Null(r.FileUrl);  // thiếu → null
        Assert.Null(r.Error);
    }

    [Fact]
    public void DocKetQua_CoLoi_ParseError()
    {
        const string json =
            "{\"results\":[{\"maDon\":\"D3\",\"ok\":false,\"added\":false,\"fileUrl\":null,\"error\":\"boom\"}]}";

        var r = Assert.Single(GoogleSheetSyncService.DocKetQua(json));

        Assert.Equal("D3", r.MaDon);
        Assert.False(r.Ok);
        Assert.Equal("boom", r.Error);
    }

    [Fact]
    public void DocKetQua_ErrorCapScript_NemKemMessage()
    {
        const string json = "{\"error\":\"Không tìm thấy tab \\\"tháng 4\\\". Các tab hiện có: A, B\"}";

        var ex = Assert.Throws<InvalidOperationException>(() => GoogleSheetSyncService.DocKetQua(json));
        Assert.Contains("Không tìm thấy tab", ex.Message);
        Assert.Contains("Các tab hiện có: A, B", ex.Message);
    }

    [Fact]
    public void DocKetQua_JsonRac_Nem()
    {
        Assert.ThrowsAny<JsonException>(() => GoogleSheetSyncService.DocKetQua("not json {"));
    }
}
