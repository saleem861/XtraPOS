using Xunit;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Customers;

namespace XtraPOS.Api.Tests;

public class WhatsAppAndSpecialPriceTests
{
    [Theory]
    [InlineData("0592594707", "970", "0592594707", "+970592594707")]
    [InlineData("0592594707", "972", "0592594707", "+972592594707")]
    [InlineData("00970592594707", "972", "0592594707", "+970592594707")]
    [InlineData("00972592594707", "970", "0592594707", "+972592594707")]
    [InlineData("+972592594707", "970", "0592594707", "+972592594707")]
    [InlineData("+970592594707", null, "0592594707", "+970592594707")]
    public void Formats_one_country(string input, string? selected, string local, string e164)
    {
        var phone = WhatsAppPhoneFormatter.Format(input, selected);
        Assert.Equal(local, phone.LocalNumber);
        Assert.Equal(e164, phone.E164);
    }

    [Fact]
    public void Otp_is_hashed_and_not_stored_as_plaintext()
    {
        var code = "1234";
        var hash = OtpHasher.Hash(code);
        Assert.DoesNotContain(code, hash);
        Assert.True(OtpHasher.Matches(code, hash));
        Assert.False(OtpHasher.Matches("0000", hash));
    }

    [Fact]
    public void Calling_codes_cover_shared_and_regional_numbers()
    {
        var codes = CallingCodes.Distinct();
        Assert.True(codes.Count >= 180);
        Assert.Contains(codes, code => code.Code == "972" && code.Name == "إسرائيل");
        Assert.Contains(codes, code => code.Code == "970" && code.Name == "فلسطين");
        Assert.Contains(codes, code => code.Code == "33" && code.Name == "فرنسا");
        Assert.Contains(codes, code => code.Code == "1" && code.Name == "الولايات المتحدة");
        Assert.Contains(codes, code => code.Code == "1" && code.Name == "كندا");
        Assert.Contains(codes, code => code.Code == "86" && code.Name == "الصين");
        Assert.Contains(codes, code => code.Code == "234" && code.Name == "نيجيريا");
        Assert.Equal(codes.Count, codes.Select(code => code.Code + code.Name).Distinct().Count());
    }

    [Fact]
    public void Special_price_beats_tier_and_tier_remains_when_absent()
    {
        var special = CustomerItemSpecialPriceService.SelectResolvedPrice(0.5m, 1m, PriceSources.SharedDefault);
        Assert.Equal(0.5m, special.Price);
        Assert.Equal(PriceSources.CustomerItemSpecial, special.Source);
        var tier = CustomerItemSpecialPriceService.SelectResolvedPrice(null, 12m, "Price2");
        Assert.Equal(12m, tier.Price);
        Assert.Equal("Price2", tier.Source);
    }

    [Fact]
    public void Quantity_special_price_applies_only_at_or_above_the_threshold()
    {
        var migrated = CustomerItemSpecialPriceService.MigratedRule(4.50m);
        Assert.Equal(1m, migrated.MinimumQuantity);
        Assert.Equal(4.50m, migrated.BundlePrice);
        Assert.Equal(4.50m, CustomerItemSpecialPriceService.TargetUnitPrice(45m, 10m));
        Assert.Throws<CustomerMasterException>(() => CustomerItemSpecialPriceService.TargetUnitPrice(45m, 0m));

        var below = CustomerItemSpecialPriceService.SelectResolvedPrice(4.50m, 5.50m, "Price0", 10m, 9m);
        var exact = CustomerItemSpecialPriceService.SelectResolvedPrice(4.50m, 5.50m, "Price0", 10m, 10m);
        var above = CustomerItemSpecialPriceService.SelectResolvedPrice(4.50m, 5.50m, "Price0", 10m, 15m);
        var back = CustomerItemSpecialPriceService.SelectResolvedPrice(4.50m, 5.50m, "Price0", 10m, 9m);
        var otherCustomer = CustomerItemSpecialPriceService.SelectResolvedPrice(null, 5.50m, "Price0", 1m, 10m);
        Assert.Equal(5.50m, below.Price);
        Assert.Equal(4.50m, exact.Price);
        Assert.Equal(PriceSources.CustomerItemSpecial, exact.Source);
        Assert.Equal(4.50m, above.Price);
        Assert.Equal(5.50m, back.Price);
        Assert.Equal(5.50m, otherCustomer.Price);

        var sync = new CustomerItemSpecialPriceService.SpecialPriceSyncPayload(Guid.NewGuid(), 10m, 45m, 4.50m);
        Assert.Equal(10m, sync.MinimumQuantity);
        Assert.Equal(45m, sync.BundlePrice);
        Assert.Equal(4.50m, sync.TargetUnitPrice);
    }
}
