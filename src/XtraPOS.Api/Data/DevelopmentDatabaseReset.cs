namespace XtraPOS.Api.Data;

public readonly record struct DevelopmentResetAuthorization(string ConfirmedDatabaseName);

public static class DevelopmentDatabaseReset
{
    public const string CommandName = "--reset-development-database";
    public const string ConfirmOption = "--confirm-database";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, CommandName, StringComparison.Ordinal));

    public static string? ReadConfirmedDatabase(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], ConfirmOption, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    public static bool TryAuthorize(
        bool isDevelopment,
        string? configuredDatabase,
        string? confirmedDatabase,
        out string denial)
    {
        if (!isDevelopment)
        {
            denial = "Database reset is refused outside the Development environment.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(configuredDatabase)
            || string.IsNullOrWhiteSpace(confirmedDatabase)
            || confirmedDatabase.Contains('*')
            || confirmedDatabase.Contains('%')
            || confirmedDatabase.Contains('?')
            || !string.Equals(confirmedDatabase, configuredDatabase, StringComparison.Ordinal))
        {
            denial = "Database reset requires --confirm-database with the exact configured database name.";
            return false;
        }

        denial = "";
        return true;
    }
}
