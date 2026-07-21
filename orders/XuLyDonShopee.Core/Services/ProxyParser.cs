using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>Một dòng không parse được, kèm số thứ tự dòng và lý do.</summary>
public record ProxyParseError(int LineNumber, string Content, string Reason);

/// <summary>Kết quả parse danh sách proxy: các proxy hợp lệ và các dòng lỗi.</summary>
public class ProxyParseResult
{
    public List<ProxyEntry> Valid { get; } = new();
    public List<ProxyParseError> Errors { get; } = new();
}

/// <summary>
/// Parse văn bản nhiều dòng thành danh sách <see cref="ProxyEntry"/>.
/// Mỗi dòng: <c>host:port</c> hoặc <c>host:port:user:pass</c>.
/// Dòng trống được bỏ qua (không tính là lỗi).
/// </summary>
public static class ProxyParser
{
    public static ProxyParseResult Parse(string? text)
    {
        var result = new ProxyParseResult();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var raw = lines[i];
            var line = raw.Trim();

            // Bỏ qua dòng trống, không tính là lỗi.
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(':');
            if (parts.Length != 2 && parts.Length != 4)
            {
                result.Errors.Add(new ProxyParseError(lineNumber, line,
                    "Sai định dạng (cần host:port hoặc host:port:user:pass)"));
                continue;
            }

            var host = parts[0].Trim();
            if (host.Length == 0)
            {
                result.Errors.Add(new ProxyParseError(lineNumber, line, "Thiếu host"));
                continue;
            }

            if (!int.TryParse(parts[1].Trim(), out var port) || port < 1 || port > 65535)
            {
                result.Errors.Add(new ProxyParseError(lineNumber, line,
                    "Port không hợp lệ (phải là số từ 1 đến 65535)"));
                continue;
            }

            var entry = new ProxyEntry
            {
                Host = host,
                Port = port,
                Type = ProxyType.Http,
                Status = ProxyStatus.ChuaKiemTra
            };

            if (parts.Length == 4)
            {
                var user = parts[2].Trim();
                var pass = parts[3].Trim();
                entry.Username = user.Length > 0 ? user : null;
                entry.Password = pass.Length > 0 ? pass : null;
            }

            result.Valid.Add(entry);
        }

        return result;
    }
}
