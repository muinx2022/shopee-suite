using XuLyDonShopee.Core.Validation;

namespace XuLyDonShopee.Tests;

public class EmailValidatorTests
{
    [Theory]
    [InlineData("a@b.com")]
    [InlineData("ten.nguyen@shopee.vn")]
    [InlineData("user+tag@gmail.com")]
    [InlineData("abc123@sub.domain.co")]
    public void IsValid_EmailHopLe_TraTrue(string email)
    {
        Assert.True(EmailValidator.IsValid(email));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("a@b")]
    [InlineData("@b.com")]
    [InlineData("a@@b.com")]
    [InlineData("a b@c.com")]
    [InlineData("a@b.com ")]
    [InlineData("a@.com")]
    [InlineData(null)]
    public void IsValid_EmailKhongHopLe_TraFalse(string? email)
    {
        Assert.False(EmailValidator.IsValid(email));
    }
}
