using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class CustomerPhoneNormalizerTests
{
    [Theory]
    [InlineData("059 123 4567", "0591234567")]
    [InlineData("059-123-4567", "0591234567")]
    [InlineData("(059)1234567", "0591234567")]
    [InlineData("  0591234567  ", "0591234567")]
    [InlineData("٠٥٩١٢٣٤٥٦٧", "0591234567")]
    [InlineData("۰۵۹۱۲۳۴۵۶۷", "0591234567")]
    [InlineData("٠٥٩-123 4567", "0591234567")]
    [InlineData("01", "01")]
    [InlineData("1", "1")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("()", null)]
    public void Normalize_PreservesLeadingZero_AndMapsDigits(string? phone, string? expected)
    {
        Assert.Equal(expected, XtraPOS.Api.Customers.PhoneNormalizer.Normalize(phone));
    }

    [Fact]
    public void WalkInPlaceholder_IsOnlyTheExactDigitOne()
    {
        Assert.True(XtraPOS.Api.Customers.PhoneNormalizer.IsWalkInPlaceholder("1"));
        Assert.False(XtraPOS.Api.Customers.PhoneNormalizer.IsWalkInPlaceholder("01"));
        Assert.False(XtraPOS.Api.Customers.PhoneNormalizer.IsWalkInPlaceholder("0591234567"));
    }
}
