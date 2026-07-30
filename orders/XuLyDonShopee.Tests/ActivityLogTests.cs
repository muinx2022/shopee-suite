using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;
using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test phần THUẦN của <see cref="ActivityLog"/>: định dạng dòng, ring-buffer RIÊNG từng nguồn, ghi file gom
/// nhóm. Truyền <c>uiPost: a =&gt; a()</c> để sự kiện <see cref="ActivityLog.SourceUpdated"/> nổ đồng bộ (không
/// cần Avalonia dispatcher). Ghi file chạy ở timer nền → test gọi <see cref="ActivityLog.Flush"/> để khỏi chờ nhịp.
/// </summary>
public class ActivityLogTests
{
    private readonly ITestOutputHelper _output;

    public ActivityLogTests(ITestOutputHelper output) => _output = output;

    /// <summary>Cấp một thư mục tạm cho test, tự dọn khi Dispose.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"xlds_log_{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* bỏ qua lỗi dọn */ }
        }
    }

    [Fact]
    public void FormatLine_DungDinhDang_GioNguonNoiDung()
    {
        var entry = new LogEntry(new DateTime(2026, 7, 15, 9, 8, 7), "abc@mail.com", "Đang mở trình duyệt");

        Assert.Equal("09:08:07 [abc@mail.com] Đang mở trình duyệt", ActivityLog.FormatLine(entry));
        // Display là hàm tiện dùng lại FormatLine.
        Assert.Equal(ActivityLog.FormatLine(entry), entry.Display);
    }

    [Fact]
    public void Append_VuotCap_GiuDungSoDongMoiNhat()
    {
        using var dir = new TempDir();
        using var log = new ActivityLog(dir.Path, uiPost: a => a(), maxLinesPerSource: 3);

        for (int i = 0; i < 5; i++)
        {
            log.Append("tk", $"m{i}");
        }

        // Ring-buffer: chỉ giữ 3 dòng, là 3 dòng MỚI NHẤT (m2, m3, m4); m0, m1 bị loại.
        var snap = log.Snapshot("tk");
        Assert.Equal(3, snap.Count);
        Assert.Equal(new[] { "m2", "m3", "m4" }, snap.Select(e => e.Message).ToArray());
    }

    [Fact]
    public void Append_CapRiengTungNguon_NguonOnKhongDayVangNguonKhac()
    {
        using var dir = new TempDir();
        using var log = new ActivityLog(dir.Path, uiPost: a => a(), maxLinesPerSource: 3);

        log.Append("im@mail.com", "chi mot dong");
        for (int i = 0; i < 100; i++)
        {
            log.Append("on@mail.com", $"on{i}");
        }

        // Trần là của RIÊNG từng nguồn: tài khoản ồn chạy 100 dòng KHÔNG đẩy văng dòng của tài khoản kia.
        Assert.Equal(new[] { "chi mot dong" }, log.Snapshot("im@mail.com").Select(e => e.Message).ToArray());
        Assert.Equal(new[] { "on97", "on98", "on99" }, log.Snapshot("on@mail.com").Select(e => e.Message).ToArray());
    }

    [Fact]
    public void Snapshot_KhongPhanBietHoaThuong_VaTraBanSao()
    {
        using var dir = new TempDir();
        using var log = new ActivityLog(dir.Path, uiPost: a => a());

        log.Append("Abc@Mail.com", "m1");

        // Nguồn là email → tra không phân biệt hoa/thường (nhãn có thể lệch giữa các đường gọi).
        Assert.Single(log.Snapshot("abc@mail.com"));

        // Snapshot là BẢN SAO: append thêm không làm đổi mảng đã lấy ra.
        var snap = log.Snapshot("abc@mail.com");
        log.Append("Abc@Mail.com", "m2");
        Assert.Single(snap);
        Assert.Equal(2, log.Snapshot("abc@mail.com").Count);
    }

    [Fact]
    public void Snapshot_NguonChuaCoLog_TraRong()
    {
        using var dir = new TempDir();
        using var log = new ActivityLog(dir.Path, uiPost: a => a());

        Assert.Empty(log.Snapshot("chua-co@mail.com"));
    }

    [Fact]
    public void Append_GhiFile_TonTaiVaChuaCacDong()
    {
        using var dir = new TempDir();
        using var log = new ActivityLog(dir.Path, uiPost: a => a());

        log.Append("tk1", "dong mot");
        log.Append("tk2", "dong hai");
        log.Flush(); // ghi file chạy ở timer nền → đẩy tay cho khỏi chờ nhịp

        // File hoatdong-YYYYMMDD.log được tạo trong thư mục và chứa cả hai dòng.
        Assert.True(File.Exists(log.CurrentLogPath));
        var content = File.ReadAllText(log.CurrentLogPath);
        Assert.Contains("[tk1] dong mot", content);
        Assert.Contains("[tk2] dong hai", content);

        // Đúng mẫu tên file hoatdong-*.log.
        var files = Directory.GetFiles(dir.Path, "hoatdong-*.log");
        Assert.Single(files);
    }

    [Fact]
    public void Append_VuotCapHienThi_FileVanDuDong()
    {
        using var dir = new TempDir();
        using var log = new ActivityLog(dir.Path, uiPost: a => a(), maxLinesPerSource: 3);

        for (int i = 0; i < 50; i++)
        {
            log.Append("tk", $"m{i}");
        }
        log.Flush();

        // Cắt là cắt phần HIỂN THỊ; file trên đĩa phải đủ 50 dòng (không mất lịch sử).
        Assert.Equal(3, log.Snapshot("tk").Count);
        Assert.Equal(50, File.ReadAllLines(log.CurrentLogPath).Length);
    }

    [Fact]
    public void Clear_TheoNguon_ChiXoaDongCuaNguonDo()
    {
        using var dir = new TempDir();
        using var log = new ActivityLog(dir.Path, uiPost: a => a());

        log.Append("a", "a1");
        log.Append("b", "b1");
        log.Append("a", "a2");
        log.Append("b", "b2");

        log.Clear("a");

        // Nguồn "a" sạch; nguồn "b" giữ nguyên, đúng thứ tự.
        Assert.Empty(log.Snapshot("a"));
        Assert.Equal(new[] { "b1", "b2" }, log.Snapshot("b").Select(e => e.Message).ToArray());
    }

    [Fact]
    public void Clear_KhongThamSo_XoaHet()
    {
        using var dir = new TempDir();
        using var log = new ActivityLog(dir.Path, uiPost: a => a());

        log.Append("a", "a1");
        log.Append("b", "b1");

        log.Clear();

        Assert.Empty(log.Snapshot("a"));
        Assert.Empty(log.Snapshot("b"));
    }

    [Fact]
    public void Clear_BaoNgayChoUI_KhongDoiNhipGom()
    {
        using var dir = new TempDir();
        using var log = new ActivityLog(dir.Path, uiPost: a => a());
        log.Append("a", "a1");

        // Đăng ký SAU khi đã có log để không đếm lẫn nhịp gom của Append.
        var baoVe = 0;
        log.SourceUpdated += _ => Interlocked.Increment(ref baoVe);

        log.Clear("a");

        // Bấm nút là hành động người dùng → panel phải sạch NGAY, không chờ nhịp 250ms.
        Assert.Equal(1, Volatile.Read(ref baoVe));
    }

    /// <summary>
    /// Đo hiệu năng theo mục "Đo hiệu năng" của plan <c>2026-07-30-log-don-hang-het-do</c>: bơm 5000 dòng từ 5
    /// luồng song song rồi kiểm 4 chỉ số. Ngưỡng assert để RẤT rộng (chỉ đủ bắt hồi quy kiểu "quay lại ghi file
    /// đồng bộ từng dòng"), số thật in ra qua <see cref="ITestOutputHelper"/>.
    /// </summary>
    [Fact]
    public void Append_5000DongTu5Luong_NhanhKhongChamDia_BaoUIGomNhom_FileDuDong()
    {
        const int soLuong = 5;
        const int moiLuong = 1000;
        const int tong = soLuong * moiLuong;

        using var dir = new TempDir();
        using var log = new ActivityLog(dir.Path, uiPost: a => a());

        var soLanBao = 0;
        log.SourceUpdated += _ => Interlocked.Increment(ref soLanBao);

        // (0) Mốc so sánh: chính là việc bản CŨ làm ở mỗi Append — mở/ghi/đóng file một dòng.
        var fileCu = Path.Combine(dir.Path, "moc-cu.log");
        var swCu = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
        {
            File.AppendAllText(fileCu, $"dong {i}{Environment.NewLine}", System.Text.Encoding.UTF8);
        }
        swCu.Stop();
        var trungBinhCuUs = swCu.Elapsed.TotalMilliseconds * 1000 / 200;

        // (1) Bơm 5000 dòng từ 5 luồng, đo từng lượt Append.
        var nhip = new long[tong];
        var swTong = Stopwatch.StartNew();
        Parallel.For(0, soLuong, t =>
        {
            for (int i = 0; i < moiLuong; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                log.Append($"tk{t}@mail.com", $"dong {i}");
                nhip[(t * moiLuong) + i] = Stopwatch.GetTimestamp() - t0;
            }
        });
        swTong.Stop();

        var heSo = 1_000_000.0 / Stopwatch.Frequency; // tick → micro-giây
        var trungBinhUs = nhip.Average() * heSo;
        var teNhatUs = nhip.Max() * heSo;
        var sapXep = nhip.OrderBy(x => x).ToArray();
        var p50Us = sapXep[tong / 2] * heSo;
        var p99Us = sapXep[(int)(tong * 0.99)] * heSo;
        var viTriTeNhat = Array.IndexOf(nhip, nhip.Max());

        // Chờ nốt nhịp báo UI cuối rồi mới chốt số lần bắn (nhịp gom là 250ms).
        Thread.Sleep(600);
        var lanBao = Volatile.Read(ref soLanBao);

        // (3) Đẩy nốt hàng đợi rồi đếm dòng trong file.
        log.Flush();
        var soDongFile = File.ReadAllLines(log.CurrentLogPath).Length;

        _output.WriteLine($"[1] Append trung binh = {trungBinhUs:0.000} us | p50 = {p50Us:0.000} us "
                          + $"| p99 = {p99Us:0.000} us | te nhat = {teNhatUs:0.0} us (o luot #{viTriTeNhat % moiLuong}) "
                          + $"| tong {tong} dong het {swTong.Elapsed.TotalMilliseconds:0.0} ms");
        _output.WriteLine($"[1b] Moc BAN CU (File.AppendAllText 1 dong/lan) = {trungBinhCuUs:0.0} us/dong");
        _output.WriteLine($"[2] So lan SourceUpdated ban ra = {lanBao} (khong gom nhom se la {tong})");
        _output.WriteLine($"[3] So dong trong file = {soDongFile}");
        _output.WriteLine($"[4] Buffer moi nguon = {log.Snapshot("tk0@mail.com").Count} dong "
                          + $"| dau = '{log.Snapshot("tk0@mail.com")[0].Message}' "
                          + $"| cuoi = '{log.Snapshot("tk0@mail.com")[^1].Message}'");

        // (1) Append chỉ enqueue → phải ở mức micro-giây, không phải mili-giây của một lượt chạm đĩa.
        Assert.True(trungBinhUs < 50, $"Append trung binh {trungBinhUs:0.000} us — nghi da cham dia tro lai");

        // (2) Báo UI gom nhóm: phải nhỏ hơn tổng số dòng RẤT nhiều.
        Assert.True(lanBao > 0, "Khong bao UI lan nao");
        Assert.True(lanBao < tong / 10, $"So lan bao UI = {lanBao}, khong con gom nhom");

        // (3) Cắt hiển thị KHÔNG được cắt file: đủ 5000 dòng.
        Assert.Equal(tong, soDongFile);

        // (4) Mỗi nguồn giữ đúng trần và là các dòng MỚI NHẤT.
        for (int t = 0; t < soLuong; t++)
        {
            var snap = log.Snapshot($"tk{t}@mail.com");
            Assert.Equal(ActivityLog.MaxLinesPerSource, snap.Count);
            Assert.Equal($"dong {moiLuong - ActivityLog.MaxLinesPerSource}", snap[0].Message);
            Assert.Equal($"dong {moiLuong - 1}", snap[^1].Message);
        }
    }
}
