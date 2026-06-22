using CommunityToolkit.Mvvm.ComponentModel;

namespace Shopee.Suite.Modules.CheckAccount;

/// <summary>Một dòng trong lưới "TK OK": chuỗi login + có được chọn để copy + trạng thái copy.</summary>
public sealed partial class OkAccountRow : ObservableObject
{
    public OkAccountRow(string line)
    {
        Line = line;
        Account = line.Split('|')[0];
    }

    public string Line { get; }
    public string Account { get; }

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private string _status = "";
}
