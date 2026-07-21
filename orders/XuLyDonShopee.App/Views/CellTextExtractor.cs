using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace XuLyDonShopee.App.Views;

/// <summary>
/// Rút text hiển thị của MỘT ô <see cref="DataGrid"/> (đọc-chỉ) để copy khi người dùng double-click.
/// Bền với cột template của màn Đơn hàng:
/// <list type="bullet">
///   <item>ô text (Mã đơn, Người mua, Mã vận đơn…) → <see cref="TextBlock"/> → lấy <c>.Text</c>;</item>
///   <item>ô "Trạng thái" = pill <see cref="Border"/> chứa <see cref="TextBlock"/> → lấy text con;</item>
///   <item>ô "Phiếu" = <see cref="Button"/> "In phiếu" → KHÔNG có text dữ liệu ý nghĩa → trả null (bỏ qua).</item>
/// </list>
/// Viết thuần (không phụ thuộc bước render) để test được: duyệt gộp cả visual tree (lúc app chạy, sau khi ô
/// đã render) LẪN logical tree (lúc test dựng control bằng tay, chưa render — <c>Border.Child</c>/
/// <c>Panel.Children</c> đã là con ngay khi gán). Phòng thủ null, không ném khi gặp ô lạ.
/// </summary>
public static class CellTextExtractor
{
    /// <summary>
    /// Trả text ô để copy, hoặc <c>null</c> nếu ô không có text đáng copy: ô chứa <see cref="Button"/>
    /// (cột "Phiếu") hoặc không có <see cref="TextBlock"/> nào có chữ (ô trống).
    /// </summary>
    public static string? ExtractCellText(Control? cell)
    {
        if (cell is null)
        {
            return null;
        }

        // Ô nút "Phiếu": chỉ có nút "In phiếu" (không phải dữ liệu đơn) → bỏ qua, không copy chuỗi "In phiếu".
        if (ContainsButton(cell))
        {
            return null;
        }

        var text = FindFirstTextBlock(cell)?.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Có bất kỳ <see cref="Button"/> nào trong cây con của ô không (nhận diện ô "Phiếu").</summary>
    private static bool ContainsButton(Control node)
    {
        if (node is Button)
        {
            return true;
        }

        foreach (var child in Children(node))
        {
            if (ContainsButton(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><see cref="TextBlock"/> đầu tiên theo thứ tự duyệt sâu (DFS) trong cây con của ô, hoặc null.</summary>
    private static TextBlock? FindFirstTextBlock(Control node)
    {
        if (node is TextBlock tb)
        {
            return tb;
        }

        foreach (var child in Children(node))
        {
            var found = FindFirstTextBlock(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Con trực tiếp của một control: gộp visual children (đầy đủ sau khi render, lúc app chạy) và logical
    /// children (đủ cho control dựng tay lúc test), loại trùng theo tham chiếu.
    /// </summary>
    private static IEnumerable<Control> Children(Control node)
    {
        var seen = new HashSet<Control>();

        foreach (var v in node.GetVisualChildren())
        {
            if (v is Control c && seen.Add(c))
            {
                yield return c;
            }
        }

        foreach (var l in ((ILogical)node).LogicalChildren)
        {
            if (l is Control c && seen.Add(c))
            {
                yield return c;
            }
        }
    }
}
