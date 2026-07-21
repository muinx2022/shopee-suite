using System.Net.Mail;

namespace XuLyDonShopee.Core.Validation;

/// <summary>
/// Kiểm tra định dạng email, dùng chung cho UI và tầng lưu trữ.
/// </summary>
public static class EmailValidator
{
    /// <summary>Trả về true nếu <paramref name="email"/> là địa chỉ email hợp lệ.</summary>
    public static bool IsValid(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        // Không cho khoảng trắng ở giữa; MailAddress khá dễ dãi nên siết thêm.
        var trimmed = email.Trim();
        if (trimmed != email || trimmed.Contains(' '))
        {
            return false;
        }

        // Phải có đúng một '@' và tên miền có dấu chấm.
        var atIndex = trimmed.IndexOf('@');
        if (atIndex <= 0 || atIndex != trimmed.LastIndexOf('@'))
        {
            return false;
        }

        var domain = trimmed[(atIndex + 1)..];
        if (!domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.'))
        {
            return false;
        }

        try
        {
            var addr = new MailAddress(trimmed);
            return addr.Address == trimmed;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
