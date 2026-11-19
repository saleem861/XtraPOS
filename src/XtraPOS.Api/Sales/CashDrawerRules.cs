namespace XtraPOS.Api.Sales;

public static class CashDrawerRules
{
    public const string Matched = "Matched";
    public const string Shortage = "Shortage";
    public const string Surplus = "Surplus";
    public const string KeepShortage = "KeepShortage";
    public const string KeepSurplus = "KeepSurplus";
    public const string Waive = "Waive";

    public static decimal Difference(decimal counted, decimal expected) =>
        decimal.Round(counted - expected, 4, MidpointRounding.AwayFromZero);

    public static string Status(decimal difference) =>
        difference == 0m ? Matched : difference < 0m ? Shortage : Surplus;

    public static bool NeedsJustification(string status) => status != Matched;

    public static bool ChargesEmployee(string status) => status == Shortage;

    public static bool DecisionMatches(string status, string decisionCode) =>
        decisionCode switch
        {
            KeepShortage => status == Shortage,
            KeepSurplus => status == Surplus,
            Waive => status == Shortage,
            _ => false,
        };
}
