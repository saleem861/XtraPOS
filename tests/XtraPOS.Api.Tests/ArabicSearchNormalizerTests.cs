using Xunit;
using XtraPOS.Api.Catalog;

namespace XtraPOS.Api.Tests;

public sealed class ArabicSearchNormalizerTests
{
    [Theory]
    [InlineData("أحمد", "احمد")]
    [InlineData("احمد", "احمد")]
    [InlineData("إحمد", "احمد")]
    [InlineData("إبراهيم", "ابراهيم")]
    [InlineData("آدم", "ادم")]
    [InlineData("على", "علي")]
    [InlineData("مُحَمَّد", "محمد")]
    [InlineData("مــحــمــد", "محمد")]
    [InlineData("احمد    علي", "احمد علي")]
    [InlineData(" احمد علي ", "احمد علي")]
    [InlineData("یوسف کريم", "يوسف كريم")]
    public void Strict_normalizes_orthography_without_changing_other_letters(string input, string expected)
    {
        Assert.Equal(expected, ArabicSearchNormalizer.NormalizeStrict(input));
    }

    [Fact]
    public void Loose_matches_final_teh_marbuta_and_heh_only()
    {
        Assert.Equal(ArabicSearchNormalizer.NormalizeLoose("أرجيلة"), ArabicSearchNormalizer.NormalizeLoose("ارجيله"));
        Assert.Equal(ArabicSearchNormalizer.NormalizeLoose("هدية"), ArabicSearchNormalizer.NormalizeLoose("هديه"));
        Assert.NotEqual(ArabicSearchNormalizer.NormalizeLoose("هند"), ArabicSearchNormalizer.NormalizeLoose("ةند"));
        Assert.Equal("هذه", ArabicSearchNormalizer.NormalizeStrict("هذه"));
        Assert.Equal("هذه", ArabicSearchNormalizer.NormalizeLoose("هذه"));
    }

    [Fact]
    public void Digits_normalize_without_rewriting_letters()
    {
        Assert.Equal("443", ArabicSearchNormalizer.NormalizeDigits("٤٤٣"));
        Assert.Equal("443", ArabicSearchNormalizer.NormalizeDigits("۴۴۳"));
    }
}
