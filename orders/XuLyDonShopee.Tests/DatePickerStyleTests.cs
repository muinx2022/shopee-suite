using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Style ô CHỌN NGÀY của module (Styles/Controls.xaml, thêm 2026-08-06). Hai thứ build KHÔNG bắt được:
/// <list type="bullet">
/// <item><b>Tên PART</b> trong <c>ControlTemplate</c> của <see cref="DatePicker"/> là hợp đồng với
/// <c>DatePicker.OnApplyTemplate</c> — gõ sai <c>PART_TextBox</c>/<c>PART_Button</c>/<c>PART_Popup</c> thì build
/// vẫn xanh mà ô ngày CHẾT CÂM (bấm không bung lịch).</item>
/// <item><b>Định dạng ngày</b>: WPF lấy culture hiển thị từ <c>FrameworkElement.Language</c>; thiếu
/// <c>Language="vi-VN"</c> là máy đặt vùng Mỹ hiện <c>8/6/2026</c> trong khi cả app viết <c>06/08/2026</c>.</item>
/// </list>
/// Dựng control thật trên luồng STA ([StaFact]) rồi áp style lấy TỪ CHÍNH file XAML sản phẩm — không chép lại
/// template vào test (chép là test bản sao, sửa file thật vẫn xanh).
/// </summary>
public class DatePickerStyleTests
{
    /// <summary>Nạp Styles/Controls.xaml của module (kèm Colors.xaml nó tự merge) đúng như view làm lúc chạy.</summary>
    private static ResourceDictionary Controls() => new()
    {
        Source = new Uri("pack://application:,,,/XuLyDonShopee.App;component/Styles/Controls.xaml", UriKind.Absolute),
    };

    /// <summary>DatePicker đã áp style implicit của module + đã dựng cây template.</summary>
    private static DatePicker DungONgay(DateTime? chon = null)
    {
        var dp = new DatePicker { Style = (Style)Controls()[typeof(DatePicker)] };
        if (chon is not null)
        {
            dp.SelectedDate = chon;
        }
        dp.Measure(new Size(200, 40));   // ép dựng template (ApplyTemplate một mình không đủ cho mọi phần)
        dp.ApplyTemplate();
        return dp;
    }

    [StaFact]
    public void Template_GiuDung3TenPart_KhongLaOChet()
    {
        var dp = DungONgay();

        // Sai tên bất kỳ part nào ⇒ DatePicker không nối được ô chữ / nút / khay lịch ⇒ ô ngày chết câm.
        Assert.IsType<DatePickerTextBox>(dp.Template.FindName("PART_TextBox", dp));
        Assert.IsType<Button>(dp.Template.FindName("PART_Button", dp));
        Assert.IsType<Popup>(dp.Template.FindName("PART_Popup", dp));
    }

    [StaFact]
    public void DatePicker_NoiDUOC_VaoNutVaKhayLich()
    {
        // Không mở lịch thật được ở test headless (DatePicker ép IsDropDownOpen=false khi control chưa Loaded),
        // nên soi DẤU VẾT mà OnApplyTemplate để lại — chỉ có khi nó TÌM THẤY part đúng tên VÀ đúng kiểu:
        //  · Popup: DatePicker rót Calendar vào Child;
        //  · Button: DatePicker tự gán Content (chuỗi trợ năng) khi thấy Content còn null.
        var dp = DungONgay();

        var popup = (Popup)dp.Template.FindName("PART_Popup", dp);
        Assert.IsType<Calendar>(popup.Child);

        var nut = (Button)dp.Template.FindName("PART_Button", dp);
        Assert.NotNull(nut.Content);
    }

    [StaFact]
    public void HienNgayKieuViet_ddMMyyyy()
    {
        var dp = DungONgay(new DateTime(2026, 8, 6));
        var box = (DatePickerTextBox)dp.Template.FindName("PART_TextBox", dp);

        // 06/08/2026 chứ KHÔNG phải 8/6/2026 (locale Mỹ) — thứ dễ đọc nhầm ngày↔tháng nhất.
        Assert.Equal("06/08/2026", box.Text);
        // XmlLanguage chuẩn hóa về chữ thường ("vi-vn") — so không phân biệt hoa/thường.
        Assert.Equal("vi-VN", dp.Language.IetfLanguageTag, ignoreCase: true);
        Assert.Equal(DatePickerFormat.Short, dp.SelectedDateFormat);
    }

    [StaFact]
    public void ONgayVaOComboBox_CungChieuCaoVaCoChu()
    {
        // "Khung phẳng khớp ô ComboBox bên cạnh": cùng MinHeight 30 + FontSize 12.5 như style fieldCombo.
        var dict = Controls();
        var dp = new DatePicker { Style = (Style)dict[typeof(DatePicker)] };
        var combo = new ComboBox { Style = (Style)dict["fieldCombo"] };

        Assert.Equal(combo.MinHeight, dp.MinHeight);
        Assert.Equal(combo.FontSize, dp.FontSize);
    }

    [StaFact]
    public void LichBungRa_CoStyleRieng_KhongDeAero2Ve()
    {
        var dict = Controls();
        Assert.True(dict.Contains(typeof(Calendar)));
        Assert.True(dict.Contains(typeof(CalendarDayButton)));

        // Ô ngày trong lịch phải được VIẾT LẠI template (Aero2 vẽ gradient) — style chỉ đổi màu là chưa đủ.
        // Lần theo cả chuỗi BasedOn: bản implicit kế thừa từ bản có khoá `ngayTrongLich`.
        Assert.True(CoSetterTemplate((Style)dict[typeof(CalendarDayButton)]));
    }

    /// <summary>Style này (hoặc bất kỳ style cha nào qua <c>BasedOn</c>) có đặt <c>Template</c> không.</summary>
    private static bool CoSetterTemplate(Style? style)
    {
        for (var s = style; s is not null; s = s.BasedOn)
        {
            if (s.Setters.OfType<Setter>().Any(x => x.Property == Control.TemplateProperty)) return true;
        }
        return false;
    }

    /// <summary>
    /// HỒI QUY 06/08/2026 — bản vá đầu CHỈ khai style implicit cho <see cref="Calendar"/>/
    /// <see cref="CalendarDayButton"/>, và nó VÔ TÁC DỤNG: lịch của <see cref="DatePicker"/> nằm trong Popup =
    /// cây hiển thị RIÊNG, không tra tới ResourceDictionary của view (đo tận nơi:
    /// <c>CalendarDayButton.Style = null</c>, vẫn là Aero2 viền xanh + nền xám) trong khi build xanh và test cũ
    /// vẫn xanh vì nó chỉ kiểm "dictionary CÓ chứa style".
    /// <para>Nên test này đi theo ĐÚNG đường dây thật: style DatePicker → <c>CalendarStyle</c> →
    /// <c>CalendarDayButtonStyle</c> → template có <c>dayBd</c>.</para>
    /// </summary>
    [StaFact]
    public void StyleONgay_GanTuongMinh_StyleLichVaONgayVaoPopup()
    {
        var dict = Controls();
        var dpStyle = (Style)dict[typeof(DatePicker)];

        // 1) Style DatePicker PHẢI gắn CalendarStyle — thiếu setter này là lịch rơi về Aero2.
        Assert.Contains(dpStyle.Setters.OfType<Setter>(), s => s.Property == DatePicker.CalendarStyleProperty);

        // 2) Style lịch PHẢI gắn tiếp style ô ngày (Popup cũng không tra được style ô ngày).
        var lich = (Style)dict["lichBungRa"];
        var setterONgay = lich.Setters.OfType<Setter>()
            .FirstOrDefault(s => s.Property == Calendar.CalendarDayButtonStyleProperty);
        Assert.NotNull(setterONgay);

        // 3) Và style ô ngày đó đúng là bản viết lại template của module.
        var styleONgay = Assert.IsType<Style>(setterONgay!.Value);
        Assert.Contains(styleONgay.Setters.OfType<Setter>(), s => s.Property == Control.TemplateProperty);
    }
}
