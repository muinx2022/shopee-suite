using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shopee.Core.Infrastructure;

namespace Shopee.Core.Tests;

/// <summary>
/// Hành vi của <see cref="JsonAtomicFile"/> — khuôn đọc/ghi mà 13 store JSON của suite dùng chung.
/// Mọi test ghi vào thư mục tạm RIÊNG (không đụng %AppData%\ShopeeSuite của máy đang chạy).
/// </summary>
public sealed class JsonAtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shopee-core-tests", Guid.NewGuid().ToString("N"));

    private string Path_(string name) => Path.Combine(_dir, name);

    public JsonAtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Sample
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    // ── Đọc ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryLoad_ThieuFile_TraNull_KhongNem()
    {
        Assert.Null(JsonAtomicFile.TryLoad<Sample>(Path_("khong-co.json")));
    }

    [Fact]
    public void TryLoad_JsonHong_TraNull_VaBaoLog()
    {
        var path = Path_("hong.json");
        File.WriteAllText(path, "{ day khong phai json");

        var logs = new List<string>();
        Assert.Null(JsonAtomicFile.TryLoad<Sample>(path, log: logs.Add));
        Assert.Single(logs);
        Assert.Contains("hong.json", logs[0]);
    }

    [Fact]
    public void TryLoad_NoiDungNull_TraNull()
    {
        var path = Path_("null.json");
        File.WriteAllText(path, "null");
        Assert.Null(JsonAtomicFile.TryLoad<Sample>(path));
    }

    [Fact]
    public void TryLoad_DungOptionsDuocTruyenVao()
    {
        var path = Path_("hoa-thuong.json");
        File.WriteAllText(path, """{"NAME":"abc","COUNT":7}""");

        // Không options → tên field lệch hoa/thường không khớp (mặc định phân biệt hoa/thường).
        var mac_dinh = JsonAtomicFile.TryLoad<Sample>(path);
        Assert.Equal("", mac_dinh!.Name);

        // Có options (khuôn ReadOpts của AppModeStore) → khớp.
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var doc = JsonAtomicFile.TryLoad<Sample>(path, opts);
        Assert.Equal("abc", doc!.Name);
        Assert.Equal(7, doc.Count);
    }

    [Fact]
    public void TryLoad_DocDuocFileCoBom()
    {
        var path = Path_("bom.json");
        // Đúng cách 13 store đang ghi: File.WriteAllText(..., Encoding.UTF8) ⇒ CÓ BOM.
        File.WriteAllText(path, """{"Name":"có dấu","Count":3}""", Encoding.UTF8);

        var doc = JsonAtomicFile.TryLoad<Sample>(path);
        Assert.Equal("có dấu", doc!.Name);
        Assert.Equal(3, doc.Count);
    }

    // ── Ghi ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_TaoThuMucChaConThieu()
    {
        var path = Path.Combine(_dir, "chua-co", "sau-nua", "x.json");
        Assert.True(JsonAtomicFile.Save(path, new Sample { Name = "a", Count = 1 }));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_GhiUtf8CoBom_GiuNguyenDinhDangCu()
    {
        // BẤT BIẾN: file cấu hình production đang là UTF-8 CÓ BOM (File.WriteAllText + Encoding.UTF8).
        // Đổi sang không-BOM là đổi byte mọi file trên máy người dùng.
        var path = Path_("bom-out.json");
        JsonAtomicFile.Save(path, new Sample { Name = "a", Count = 1 });

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
    }

    [Fact]
    public void Save_KhongDeLaiFileTmp()
    {
        var path = Path_("sach.json");
        JsonAtomicFile.Save(path, new Sample { Name = "a", Count = 1 });

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(new[] { "sach.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_GhiDeBanCu()
    {
        var path = Path_("de.json");
        JsonAtomicFile.Save(path, new Sample { Name = "cu", Count = 1 });
        JsonAtomicFile.Save(path, new Sample { Name = "moi", Count = 2 });

        var doc = JsonAtomicFile.TryLoad<Sample>(path);
        Assert.Equal("moi", doc!.Name);
        Assert.Equal(2, doc.Count);
    }

    [Fact]
    public void SaveText_GhiDungChuoiDuocTruyenVao()
    {
        // Lối dùng của AiConfigStore/HubClientConfigStore/HubServerConfigStore: serialize TRONG lock rồi ghi.
        var path = Path_("text.json");
        var json = JsonSerializer.Serialize(new Sample { Name = "x", Count = 9 }, JsonAtomicFileRoundTripTests.Indented);

        Assert.True(JsonAtomicFile.SaveText(path, json));
        Assert.Equal(json, File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public async Task Save_HaiLuongCungGhiMotFile_CaHaiThanhCong_KhongMatFile()
    {
        // Trước đây mọi store ghi qua CÙNG tên tạm "<file>.tmp": 2 tiến trình/luồng cùng lưu là giẫm lên
        // nhau — bên này Move xong thì tmp của bên kia biến mất ⇒ Save trả false ⇒ caller (AccountStore.Add…)
        // HOÀN TÁC dù dữ liệu đã ghi được. Tên tạm duy nhất + retry Move phải làm cả hai lượt cùng thành công.
        var path = Path_("dua.json");

        var loi = new System.Collections.Concurrent.ConcurrentBag<string>();

        for (var vong = 0; vong < 30; vong++)
        {
            using var vach = new Barrier(2);
            var t1 = Task.Run(() =>
            {
                vach.SignalAndWait();
                return JsonAtomicFile.Save(path, new Sample { Name = "A", Count = 1 }, log: loi.Add);
            });
            var t2 = Task.Run(() =>
            {
                vach.SignalAndWait();
                return JsonAtomicFile.Save(path, new Sample { Name = "B", Count = 2 }, log: loi.Add);
            });
            var ket = await Task.WhenAll(t1, t2);

            Assert.True(ket[0] && ket[1],
                $"vòng {vong}: Save báo hỏng dù chỉ là tranh ghi — {string.Join(" | ", loi)}");
            Assert.True(File.Exists(path), $"vòng {vong}: mất file sau khi 2 luồng cùng ghi");

            // File còn nguyên vẹn (không bị ghi dở/trộn) và là MỘT trong hai bản, không phải thứ gì khác.
            var doc = JsonAtomicFile.TryLoad<Sample>(path);
            Assert.NotNull(doc);
            Assert.Contains(doc!.Name, new[] { "A", "B" });
        }

        // Không để lại file tạm mồ côi nào (tên tạm giờ là <file>.<pid>-<guid>.tmp).
        Assert.Equal(new[] { "dua.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_DuongDanKhongGhiDuoc_TraFalse_VaBaoLog()
    {
        // "thu-muc" là FILE, nên "thu-muc/x.json" không thể tạo được → Save phải trả false, KHÔNG ném.
        var chan = Path_("thu-muc");
        File.WriteAllText(chan, "toi la file");

        var logs = new List<string>();
        Assert.False(JsonAtomicFile.Save(Path.Combine(chan, "x.json"), new Sample(), log: logs.Add));
        Assert.Single(logs);
    }
}
