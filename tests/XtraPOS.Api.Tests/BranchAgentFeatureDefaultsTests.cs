using Microsoft.Extensions.Configuration;
using XtraPOS.Api.Setup;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class BranchAgentFeatureDefaultsTests
{
    [Fact]
    public void Branch_devices_offer_deferred_payment_and_collection_by_default()
    {
        var configuration = new ConfigurationManager();
        configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource { InitialData = BranchAgentFeatureDefaults.Values });

        Assert.True(configuration.GetValue<bool?>("CreditSales:Enabled"));
        Assert.True(configuration.GetValue<bool?>("CreditCollections:Enabled"));
        Assert.Null(configuration.GetValue<bool?>("CreditReversals:Enabled"));
    }

    [Fact]
    public void A_device_setting_still_turns_deferred_payment_off()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CreditSales:Enabled"] = "false" });
        configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource { InitialData = BranchAgentFeatureDefaults.Values });

        Assert.False(configuration.GetValue<bool?>("CreditSales:Enabled"));
        Assert.True(configuration.GetValue<bool?>("CreditCollections:Enabled"));
    }
}
