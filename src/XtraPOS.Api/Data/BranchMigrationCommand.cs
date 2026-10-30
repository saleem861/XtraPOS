namespace XtraPOS.Api.Data;

public static class BranchMigrationCommand
{
    public const string CommandName = "--apply-branch-migrations";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, CommandName, StringComparison.Ordinal));

    public static bool TryAuthorize(string? configuredDatabase, string? confirmedDatabase, out string denial)
    {
        if (string.IsNullOrWhiteSpace(configuredDatabase)
            || string.IsNullOrWhiteSpace(confirmedDatabase)
            || confirmedDatabase.Contains('*')
            || confirmedDatabase.Contains('%')
            || confirmedDatabase.Contains('?')
            || !string.Equals(confirmedDatabase, configuredDatabase, StringComparison.Ordinal)
            || !BranchDatabase.IsAllowedName(confirmedDatabase))
        {
            denial = "Branch migration requires --confirm-database with the exact configured database name.";
            return false;
        }

        denial = "";
        return true;
    }

    public static async Task ExecuteAsync(
        SqlConnectionFactory connections,
        Migrations.MigrationRunner migrations,
        string confirmedDatabase,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(
            $"Applying branch migrations on server '{connections.ConfiguredServer}' database '{confirmedDatabase}'. No database will be dropped or recreated.");
        await migrations.ApplyPendingAsync(cancellationToken);
        var pending = await migrations.ListPendingAsync(cancellationToken);
        if (pending.Count > 0)
        {
            await output.WriteLineAsync("Branch migrations did not finish: " + string.Join(", ", pending));
            throw new InvalidOperationException("Pending branch migrations remain.");
        }

        await output.WriteLineAsync($"Branch migrations completed for server '{connections.ConfiguredServer}' database '{confirmedDatabase}'.");
    }
}
