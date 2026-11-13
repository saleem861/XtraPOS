using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Devices;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed record LocalAppHostState(string? BaseUrl);

public static class BranchAgentHostCommand
{
    public const string ServiceName = "XtraPOSBranchAgent";
    public const int LocalAppPort = 47335;
    public static string LocalAppUrl => $"http://127.0.0.1:{LocalAppPort}";

    public static bool IsRequested(string[] args) =>
        args.Any(arg => string.Equals(arg, "--branch-agent", StringComparison.Ordinal));

    public static bool TryReadOptions(string[] args, out string? server, out string? pairingCode)
    {
        server = null;
        pairingCode = null;
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--server", StringComparison.Ordinal))
            {
                server = args[index + 1];
            }

            if (string.Equals(args[index], "--pairing-code", StringComparison.Ordinal))
            {
                pairingCode = args[index + 1];
            }
        }

        return !string.IsNullOrWhiteSpace(server);
    }

    public static IHost BuildHost(string[] args, string server, string? pairingCode, string? contentRootPath = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = string.IsNullOrWhiteSpace(contentRootPath) ? AppContext.BaseDirectory : contentRootPath,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = ServiceName;
        });

        builder.Services.Configure<SqlServerOptions>(builder.Configuration.GetSection(SqlServerOptions.SectionName));
        builder.Services.Configure<LoginProtectionOptions>(builder.Configuration.GetSection(LoginProtectionOptions.SectionName));
        builder.Services.AddSingleton(new LocalAppHostState(null));
        builder.Services.AddSingleton<LocalPosSessionStore>();
        builder.Services.AddSingleton<OperationalBranchConnection>();
        builder.Services.AddSingleton<OperationalBranchConnectionStore>();
        builder.Services.AddSingleton<SqlConnectionFactory>();
        builder.Services.AddSingleton<DeviceDatabaseDirectory>();
        builder.Services.AddSingleton<MigrationRunner>();
        builder.Services.AddSingleton<SetupCoordinator>();
        builder.Services.AddDbContext<BranchDbContext>((services, options) =>
        {
            var sql = services.GetRequiredService<SqlConnectionFactory>();
            var branchConnection = sql.TryCreateBranchConnectionString();
            options.UseSqlServer(branchConnection ?? "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True");
        });
        builder.Services.AddSingleton<PasswordService>();
        builder.Services.AddScoped<SelectedDatabaseProvisioner>();
        builder.Services.AddScoped<BranchDestinationRouter>();
        builder.Services.AddScoped<SetupService>();
        builder.Services.AddSingleton<BranchAgentWorker>();
        builder.Services.AddScoped<BranchAgentExecutor>();
        builder.Services.AddScoped<DeviceService>();
        builder.Services.AddScoped<LocalDeviceRecovery>();
        builder.Services.AddSingleton<CentralMigrationRunner>();
        builder.Services.AddScoped<CentralDatabaseService>();
        builder.Services.AddSingleton<CentralConnectionFileStore>();
        builder.Services.AddSingleton<SyncStatusStore>();
        builder.Services.AddSingleton<CentralPosRegistryService>();
        builder.Services.AddScoped<SharedDataSync>();
        builder.Services.AddScoped<SyncEndpointStore>();

        var configuredKeyPath = builder.Configuration["DataProtection:KeyPath"];
        var dataProtectionKeys = new DirectoryInfo(!string.IsNullOrWhiteSpace(configuredKeyPath)
            ? configuredKeyPath!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XtraPOS", "data-protection-keys"));
        Directory.CreateDirectory(dataProtectionKeys.FullName);

        var applicationName = builder.Configuration["DataProtection:ApplicationName"];
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(dataProtectionKeys)
            .SetApplicationName(string.IsNullOrWhiteSpace(applicationName)
                ? @"C:\Users\Saleem Jayousi\OneDrive\Projects\XtraPOS\src\XtraPOS.Api"
                : applicationName);

        builder.Services.AddHostedService(services => new BranchAgentHostedService(
            services.GetRequiredService<BranchAgentWorker>(),
            server,
            pairingCode));

        return builder.Build();
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryReadOptions(args, out var server, out var pairingCode) || string.IsNullOrWhiteSpace(server))
        {
            Console.WriteLine("Branch agent requires --server.");
            return 1;
        }

        using var host = BuildHost(args, server, pairingCode);
        await host.RunAsync();
        return 0;
    }

    public sealed class BranchAgentHostedService(
        BranchAgentWorker worker,
        string server,
        string? pairingCode) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            worker.RunAsync(server, pairingCode, stoppingToken);
    }
}
