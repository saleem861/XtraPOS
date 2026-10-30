namespace XtraPOS.Api.Import;

public static class ConfirmedTechnicalItemCleanup
{
    public const string DeleteSafetyItemNumber = "XTRAPOS_DELETE_TEST_20260917115506";

    public const string DeactivateSql =
        """
        UPDATE dbo.Items
        SET IsActive = 0,
            UpdatedAt = SYSUTCDATETIME()
        WHERE ItemNumber = N'XTRAPOS_DELETE_TEST_20260917115506'
          AND IsActive = 1;
        """;

    public static bool IsConfirmedTechnicalItem(string? itemNumber) =>
        string.Equals(itemNumber?.Trim(), DeleteSafetyItemNumber, StringComparison.Ordinal);

    public static bool IncludeInBranchCutover(string? itemNumber) =>
        !IsConfirmedTechnicalItem(itemNumber);
}
