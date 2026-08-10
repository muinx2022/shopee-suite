using Acornima;
using Acornima.Ast;

namespace Shopee.Core.Tests;

/// <summary>
/// Lưới an toàn cho JS của <c>extensions/</c>: mọi file phải PARSE được như ES module, và mọi lệnh
/// <c>import ... from "./x.js"</c> phải trỏ tới file CÓ THẬT.
/// <para>
/// Vì sao cần: extension nạp vào Brave dưới dạng service worker MV3 kiểu module. Một dấu ngoặc thừa ở BẤT KỲ
/// file nào trong cây import là service worker chết NGAY lúc nạp — extension không bao giờ gửi <c>ready</c>,
/// cầu nối C# ngồi chờ hết 45s rồi báo "Extension không nối cầu", và TOÀN BỘ luồng check đơn đứng. Không có
/// test C# nào chạm tới JS nên lỗi kiểu này đi thẳng ra bản phát hành dù 1900+ test xanh (đã xảy ra thật:
/// bản 1.8.6, <c>flow-returns.js</c> thiếu một dấu <c>}</c> đóng object của <c>send(...)</c>).
/// </para>
/// Test này canh CẢ BA extension (<c>shopee-orders</c>, <c>shopee-scrape</c>, <c>shopee-search</c>) vì cả ba
/// đều được <c>Shopee.Suite.csproj</c> chép vào bản phát hành.
/// </summary>
public class ExtensionJsCuPhapTests
{
    /// <summary>Số file .js tối thiểu phải quét được. Chốt chặn kiểu "test xanh vì không tìm thấy file nào":
    /// đường dẫn repo hỏng ⇒ danh sách rỗng ⇒ vòng lặp không chạy ⇒ xanh giả. Cây hiện có ~40 file.</summary>
    private const int SoFileJsToiThieu = 20;

    /// <summary>Đi từ thư mục chạy test ngược lên tìm gốc repo (có <c>ShopeeSuite.sln</c> + <c>extensions/</c>).</summary>
    private static string ThuMucExtensions()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var ext = Path.Combine(dir.FullName, "extensions");
            if (File.Exists(Path.Combine(dir.FullName, "ShopeeSuite.sln")) && Directory.Exists(ext))
            {
                return ext;
            }
        }

        throw new DirectoryNotFoundException(
            $"Không tìm thấy gốc repo (ShopeeSuite.sln + extensions/) từ {AppContext.BaseDirectory}");
    }

    private static IReadOnlyList<string> MoiFileJs() =>
        Directory.GetFiles(ThuMucExtensions(), "*.js", SearchOption.AllDirectories);

    [Fact]
    public void MoiFileJs_PhaiParseDuocLaEsModule()
    {
        var files = MoiFileJs();
        Assert.True(files.Count >= SoFileJsToiThieu,
            $"Chỉ thấy {files.Count} file .js trong extensions/ — nghi đường dẫn hỏng, test đang xanh giả.");

        var parser = new Parser();
        var hong = new List<string>();
        foreach (var f in files)
        {
            try { parser.ParseModule(File.ReadAllText(f), f); }
            catch (ParseErrorException ex) { hong.Add($"{f}: {ex.Message}"); }
        }

        Assert.True(hong.Count == 0, "File JS lỗi cú pháp (service worker sẽ chết lúc nạp):\n" + string.Join("\n", hong));
    }

    [Fact]
    public void MoiImportTuongDoi_PhaiTroToiFileCoThat()
    {
        var files = MoiFileJs();
        Assert.True(files.Count >= SoFileJsToiThieu,
            $"Chỉ thấy {files.Count} file .js trong extensions/ — nghi đường dẫn hỏng, test đang xanh giả.");

        var parser = new Parser();
        var thieu = new List<string>();
        foreach (var f in files)
        {
            Module module;
            // File lỗi cú pháp đã có test riêng lo; ở đây bỏ qua để thông điệp không bị nhân đôi.
            try { module = parser.ParseModule(File.ReadAllText(f), f); }
            catch (ParseErrorException) { continue; }

            foreach (var spec in ImportTuongDoi(module))
            {
                var dich = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, spec));
                if (!File.Exists(dich))
                {
                    thieu.Add($"{f}: import \"{spec}\" → không có file {dich}");
                }
            }
        }

        Assert.True(thieu.Count == 0, "Import trỏ vào file không tồn tại (SW chết lúc nạp):\n" + string.Join("\n", thieu));
    }

    [Fact]
    public void MoiImportTenGoi_PhaiCoExportTuongUng()
    {
        var files = MoiFileJs();
        Assert.True(files.Count >= SoFileJsToiThieu,
            $"Chỉ thấy {files.Count} file .js trong extensions/ — nghi đường dẫn hỏng, test đang xanh giả.");

        var parser = new Parser();
        var cay = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            try { cay[Path.GetFullPath(f)] = parser.ParseModule(File.ReadAllText(f), f); }
            catch (ParseErrorException) { /* đã có test cú pháp lo */ }
        }

        var thieu = new List<string>();
        foreach (var (f, module) in cay)
        {
            foreach (var stmt in module.Body)
            {
                if (stmt is not ImportDeclaration imp) continue;
                var spec = imp.Source.Value;
                if (!spec.StartsWith("./", StringComparison.Ordinal) && !spec.StartsWith("../", StringComparison.Ordinal)) continue;

                var dich = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, spec));
                if (!cay.TryGetValue(dich, out var dichModule)) continue; // thiếu file → test kia lo
                var coSan = TenDaXuat(dichModule);

                foreach (var s in imp.Specifiers)
                {
                    var ten = s switch
                    {
                        ImportSpecifier isp => (isp.Imported as Identifier)?.Name,
                        ImportDefaultSpecifier => "default",
                        _ => null, // import * as ns → không kiểm được từng tên, bỏ qua
                    };
                    if (ten is not null && !coSan.Contains(ten))
                    {
                        thieu.Add($"{f}: import {{ {ten} }} from \"{spec}\" — file đích KHÔNG export tên này");
                    }
                }
            }
        }

        Assert.True(thieu.Count == 0,
            "Import tên không có thật (SW chết lúc link module, y như lỗi cú pháp):\n" + string.Join("\n", thieu));
    }

    /// <summary>Tập tên mà một module XUẤT ra. Bao các dạng đang dùng trong <c>extensions/</c>:
    /// <c>export const/let/var</c>, <c>export function/async function</c>, <c>export class</c>,
    /// <c>export { a, b as c }</c>, <c>export default</c>. Chưa bao <c>export * from</c> (repo không dùng) —
    /// thêm dạng đó thì phải mở rộng hàm này, không thì lưới hở âm thầm.</summary>
    private static HashSet<string> TenDaXuat(Module module)
    {
        var ten = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stmt in module.Body)
        {
            switch (stmt)
            {
                case ExportDefaultDeclaration:
                    ten.Add("default");
                    break;
                case ExportNamedDeclaration en:
                    foreach (var s in en.Specifiers)
                    {
                        if (s.Exported is Identifier id) ten.Add(id.Name);
                    }
                    switch (en.Declaration)
                    {
                        case VariableDeclaration vd:
                            foreach (var d in vd.Declarations)
                            {
                                if (d.Id is Identifier vid) ten.Add(vid.Name);
                            }
                            break;
                        case FunctionDeclaration fd when fd.Id is not null:
                            ten.Add(fd.Id.Name);
                            break;
                        case ClassDeclaration cd when cd.Id is not null:
                            ten.Add(cd.Id.Name);
                            break;
                    }
                    break;
            }
        }

        return ten;
    }

    /// <summary>Lấy specifier của mọi lệnh nhập/xuất-lại ở TOP LEVEL có đường dẫn tương đối (<c>./</c>, <c>../</c>).
    /// Extension không dùng <c>import()</c> động nên duyệt <c>Body</c> là đủ — thêm import động thì phải mở rộng
    /// hàm này, không thì lưới hở âm thầm.</summary>
    private static IEnumerable<string> ImportTuongDoi(Module module)
    {
        foreach (var stmt in module.Body)
        {
            var spec = stmt switch
            {
                ImportDeclaration imp => imp.Source.Value,
                ExportNamedDeclaration en when en.Source is not null => en.Source.Value,
                ExportAllDeclaration ea => ea.Source.Value,
                _ => null,
            };

            if (spec is not null && (spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal)))
            {
                yield return spec;
            }
        }
    }
}
