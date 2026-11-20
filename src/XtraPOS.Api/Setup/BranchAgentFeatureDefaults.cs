namespace XtraPOS.Api.Setup;

/// <summary>
/// Feature switches that are on by default on an installed branch device. A device can still turn one off
/// in its own appsettings.json, which the update button never replaces. Credit reversals stay off.
/// </summary>
public static class BranchAgentFeatureDefaults
{
    public static readonly IReadOnlyDictionary<string, string?> Values = new Dictionary<string, string?>
    {
        ["CreditSales:Enabled"] = "true",
        ["CreditCollections:Enabled"] = "true",
    };
}
