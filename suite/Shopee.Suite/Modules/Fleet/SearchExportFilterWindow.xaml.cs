using System.Collections.Generic;
using System.Windows;
using Shopee.Modules.Search;

namespace Shopee.Suite.Modules.Fleet;

/// <summary>Hộp thoại chọn bộ lọc (giá / đã bán / danh mục) trước khi xuất Excel gộp — cùng bộ lọc tab Search.</summary>
public partial class SearchExportFilterWindow : Window
{
    /// <summary>Bộ lọc người dùng chọn (chỉ có giá trị sau khi bấm Xuất).</summary>
    public SearchFilter? Filter { get; private set; }

    private const string AllCategories = "(Tất cả)";

    public SearchExportFilterWindow(IEnumerable<string> categories)
    {
        InitializeComponent();
        CategoryBox.Items.Add(AllCategories);
        foreach (var c in categories) CategoryBox.Items.Add(c);
        CategoryBox.SelectedIndex = 0;
    }

    private static long ParseLong(string? s) => long.TryParse((s ?? "").Trim(), out var v) && v > 0 ? v : 0;
    private static int ParseInt(string? s) => int.TryParse((s ?? "").Trim(), out var v) && v > 0 ? v : 0;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var cat = CategoryBox.SelectedItem as string;
        if (cat == AllCategories) cat = null;
        Filter = new SearchFilter(ParseLong(MinPriceBox.Text), ParseInt(MinSoldFromBox.Text), ParseInt(MinSoldToBox.Text), cat);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
