using System.Buffers.Binary;

namespace UpdateProduct;

/// <summary>
/// Đọc thời lượng MP4 (giây) bằng cách parse atom moov/mvhd — self-contained, không cần ffprobe.
/// Trả null nếu không đọc được (khi đó KHÔNG được coi là &gt;=60s để khỏi bỏ nhầm video).
/// </summary>
internal static class Mp4Duration
{
    public static double? TryGetSeconds(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return FindMvhd(fs, fs.Length);
        }
        catch
        {
            return null;
        }
    }

    private static double? FindMvhd(Stream s, long end)
    {
        while (s.Position + 8 <= end)
        {
            var header = new byte[8];
            if (!ReadExact(s, header, 8)) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            long boxStart = s.Position - 8;

            long headerLen = 8;
            if (size == 1)
            {
                var big = new byte[8];
                if (!ReadExact(s, big, 8)) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(big);
                headerLen = 16;
            }
            else if (size == 0)
            {
                size = end - boxStart;
            }

            if (size < headerLen) return null;
            long contentEnd = boxStart + size;

            if (type == "moov")
            {
                // recurse vào moov để tìm mvhd
                var r = FindMvhd(s, contentEnd);
                if (r != null) return r;
                s.Position = contentEnd;
                continue;
            }

            if (type == "mvhd")
            {
                var ver = s.ReadByte();
                s.Position += 3; // flags
                double timescale, duration;
                if (ver == 1)
                {
                    var buf = new byte[8 + 8 + 4 + 8];
                    if (!ReadExact(s, buf, buf.Length)) return null;
                    timescale = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16, 4));
                    duration = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(20, 8));
                }
                else
                {
                    var buf = new byte[4 + 4 + 4 + 4];
                    if (!ReadExact(s, buf, buf.Length)) return null;
                    timescale = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(8, 4));
                    duration = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(12, 4));
                }
                if (timescale <= 0) return null;
                return duration / timescale;
            }

            // box khác: nhảy qua
            s.Position = contentEnd;
        }
        return null;
    }

    private static bool ReadExact(Stream s, byte[] buf, int count)
    {
        var off = 0;
        while (off < count)
        {
            var n = s.Read(buf, off, count - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }
}
