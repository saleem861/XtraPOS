namespace XtraPOS.Api.Sales;

internal static class MoneyAmount
{
    public static bool Fits(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.AwayFromZero) == value;
}
