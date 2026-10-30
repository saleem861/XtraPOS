namespace XtraPOS.Api.Import;

public sealed class LegacyItemImportService
{
    public LegacyItemImportReport DryRun(IReadOnlyList<LegacyItemObservation> rows) =>
        LegacyItemCanonicalizer.Plan(rows);

    public LegacyItemImportReport Commit(IReadOnlyList<LegacyItemObservation> rows)
    {
        _ = rows;
        throw new InvalidOperationException(
            "Legacy item commit is not enabled. No items, branch inventory, or opening balances were written.");
    }
}
