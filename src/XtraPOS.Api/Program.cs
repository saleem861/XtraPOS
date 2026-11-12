using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Credit;
using XtraPOS.Api.Customers;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Diagnostics;
using XtraPOS.Api.Devices;
using XtraPOS.Api.Endpoints;
using XtraPOS.Api.Import;
using XtraPOS.Api.Membership;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

if (builder.Environment.IsStaging())
{
    var stagingSql = StagingSqlDefaults.MissingValues(builder.Configuration["SqlServer:Database"]);
    if (stagingSql.Count > 0)
    {
        builder.Configuration.AddInMemoryCollection(stagingSql);
    }
}

builder.Services.Configure<SqlServerOptions>(builder.Configuration.GetSection(SqlServerOptions.SectionName));
builder.Services.Configure<LoginProtectionOptions>(builder.Configuration.GetSection(LoginProtectionOptions.SectionName));
builder.Services.Configure<LegacyImportOptions>(builder.Configuration.GetSection(LegacyImportOptions.SectionName));
builder.Services.AddSingleton<OperationalBranchConnection>();
builder.Services.AddSingleton<OperationalBranchConnectionStore>();
builder.Services.AddSingleton<SchemaPropagationService>();
builder.Services.AddHostedService<SchemaPropagationWorker>();
builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddSingleton<DeviceDatabaseDirectory>();
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddSingleton<SetupCoordinator>();
builder.Services.AddDbContext<BranchDbContext>((services, options) =>
{
    var sql = services.GetRequiredService<SqlConnectionFactory>();
    var branchConnection = sql.TryCreateBranchConnectionString();
    options.UseSqlServer(branchConnection ?? sql.CreateMasterConnectionString());
});
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddScoped<SelectedDatabaseProvisioner>();
builder.Services.AddScoped<BranchDestinationRouter>();
builder.Services.AddScoped<SetupService>();
builder.Services.AddSingleton<DevicePairingService>();
builder.Services.AddSingleton<BranchAgentRegistry>();
builder.Services.AddSingleton<BranchAgentRelay>();
builder.Services.AddSingleton<BranchAgentWorker>();
builder.Services.AddScoped<BranchAgentExecutor>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<SecondaryDeviceRegistration>();
builder.Services.AddScoped<LocalDeviceRecovery>();
builder.Services.AddScoped<BranchDatabaseProvisioner>();
builder.Services.AddScoped<NewTestBranchService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<UserAdministrationService>();
builder.Services.Configure<MembershipOptions>(builder.Configuration.GetSection(MembershipOptions.SectionName));
if (builder.Environment.IsStaging())
{
    builder.Services.PostConfigure<MembershipOptions>(options =>
    {
        options.Enabled = true;
        options.GoogleLoginEnabled = true;
        options.AppleLoginEnabled = false;
        options.RequireVerificationForSpecialPrices = false;
    });
}
builder.Services.AddScoped<MembershipService>();
builder.Services.AddScoped<MembershipCodeExchange>();
builder.Services.AddScoped<IMembershipIdTokenValidator, MembershipIdTokenValidator>();
builder.Services.AddScoped<MembershipAuthService>();
builder.Services.AddScoped<BranchCardOutboxSync>();
builder.Services.AddScoped<CustomerMasterService>();
builder.Services.AddScoped<CustomerPasskeyService>();
builder.Services.AddScoped<PhoneVerificationService>();
builder.Services.AddScoped<CustomerItemSpecialPriceService>();
builder.Services.AddHttpClient("WhatsApp");
builder.Services.AddHttpClient("TwilioVerify");
builder.Services.AddHttpClient("MembershipOidc");
builder.Services.AddSingleton<IWhatsAppVerificationProvider>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["WhatsApp:AccessToken"])
        ? new UnconfiguredWhatsAppProvider()
        : ActivatorUtilities.CreateInstance<MetaWhatsAppCloudProvider>(services));
builder.Services.AddScoped<IPhoneVerificationProvider>(services =>
{
    var selected = services.GetRequiredService<IConfiguration>()["PhoneVerification:Provider"];
    return string.Equals(selected, "MetaWhatsApp", StringComparison.OrdinalIgnoreCase)
        ? new MetaPhoneVerificationProvider(services.GetRequiredService<IWhatsAppVerificationProvider>())
        : ActivatorUtilities.CreateInstance<TwilioVerifyProvider>(services);
});
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<ILegacyEmployeeSource, SqlLegacyEmployeeSource>();
builder.Services.AddScoped<LegacyUserImporter>();
var configuredKeyPath = builder.Configuration["DataProtection:KeyPath"];
var dataProtectionKeys = new DirectoryInfo(!string.IsNullOrWhiteSpace(configuredKeyPath)
    ? configuredKeyPath!
    : builder.Environment.IsStaging()
        ? "/opt/xtrapos-staging/shared/data-protection-keys"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XtraPOS", "data-protection-keys"));
Directory.CreateDirectory(dataProtectionKeys.FullName);
var userKeyRing = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ASP.NET",
    "DataProtection-Keys");
if (!builder.Environment.IsStaging() && Directory.Exists(userKeyRing))
{
    foreach (var keyFile in Directory.GetFiles(userKeyRing, "key-*.xml"))
    {
        var destination = Path.Combine(dataProtectionKeys.FullName, Path.GetFileName(keyFile));
        if (!File.Exists(destination))
        {
            File.Copy(keyFile, destination);
        }
    }
}

var applicationName = builder.Configuration["DataProtection:ApplicationName"];
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(dataProtectionKeys)
    .SetApplicationName(string.IsNullOrWhiteSpace(applicationName)
        ? builder.Environment.IsStaging()
            ? "XtraPOS.Staging"
            : @"C:\Users\Saleem Jayousi\OneDrive\Projects\XtraPOS\src\XtraPOS.Api"
        : applicationName);
builder.Services.AddSingleton<CentralMigrationRunner>();
builder.Services.AddScoped<CentralDatabaseService>();
builder.Services.AddScoped<CentralSetupExecutor>();
builder.Services.AddSingleton<CentralConnectionFileStore>();
builder.Services.AddSingleton<SyncStatusStore>();
builder.Services.AddSingleton<CentralPosRegistryService>();
builder.Services.AddSingleton<CentralPosReplicaSync>();
builder.Services.AddSingleton<BranchSqlSecretStore>();
builder.Services.AddHostedService<PosReplicaHostedService>();
builder.Services.AddScoped<SharedDataSync>();
builder.Services.AddScoped<SyncEndpointStore>();
builder.Services.AddScoped<StockPostingService>();
builder.Services.AddScoped<ItemCatalogService>();
builder.Services.AddScoped<PricingResolver>();
builder.Services.AddScoped<VariableValuePricingResolver>();
builder.Services.AddScoped<SalePostingService>();
builder.Services.AddScoped<CashSessionService>();
builder.Services.AddScoped<SalePaymentService>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<HeldSaleService>();
builder.Services.AddScoped<CentralItemApi>();
builder.Services.AddScoped<ItemEditService>();
builder.Services.AddSingleton<LegacyItemImportService>();
builder.Services.AddSingleton<ILegacyItemSource, SqlLegacyItemSource>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "XtraPOS.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddScoped<CreditPolicyService>();
builder.Services.AddScoped<CreditSaleDocumentWriter>();
builder.Services.AddScoped<CreditReservationService>();
builder.Services.AddScoped<CreditSaleCoordinator>();
builder.Services.AddScoped<CreditOutboxProcessor>();
builder.Services.AddScoped<CreditCollectionService>();
builder.Services.AddScoped<CreditReversalService>();
builder.Services.AddScoped<CreditDebtReversalService>();
builder.Services.AddScoped<CreditHistoryService>();
builder.Services.AddHostedService<CreditOutboxWorker>();
builder.Services.AddHostedService<FinancialInboxWorker>();
builder.Services.AddHostedService<CustomerReplicaSyncWorker>();
builder.Services.AddHostedService<BranchDatabaseHostedService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalWeb", policy =>
    {
        var webOrigins = new List<string> { "http://localhost:5173", "http://127.0.0.1:5173" };
        if (builder.Environment.IsDevelopment())
        {
            webOrigins.Add("http://192.168.0.77:5173");
            webOrigins.Add("http://192.168.0.114:5173");
        }

        policy.WithOrigins(webOrigins.ToArray())
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

if (DevelopmentDatabaseStatus.IsRequested(args))
{
    using var statusHost = builder.Build();
    var statusConnections = statusHost.Services.GetRequiredService<SqlConnectionFactory>();
    Environment.ExitCode = await DevelopmentDatabaseStatus.WriteAsync(statusConnections, Console.Out, CancellationToken.None);
    return;
}

if (DevelopmentDatabaseReset.IsRequested(args))
{
    var confirmed = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    var configured = builder.Configuration[$"{SqlServerOptions.SectionName}:Database"];
    if (!DevelopmentDatabaseReset.TryAuthorize(builder.Environment.IsDevelopment(), configured, confirmed, out var denial))
    {
        Console.WriteLine(denial);
        Environment.ExitCode = 1;
        return;
    }

    using var resetHost = builder.Build();
    var connections = resetHost.Services.GetRequiredService<SqlConnectionFactory>();
    Console.WriteLine($"Reset target database '{confirmed}' on server '{connections.ConfiguredServer}'. The database will not be recreated.");
    await connections.DropBranchDatabaseAsync(
        new DevelopmentResetAuthorization(confirmed!),
        resetHost.Services.GetRequiredService<IHostEnvironment>().IsDevelopment(),
        CancellationToken.None);
    Console.WriteLine("Development database was dropped. Startup will not recreate it until setup runs.");
    return;
}

if (args.Any(arg => string.Equals(arg, "--write-staging-central-connection", StringComparison.Ordinal)))
{
    if (!builder.Environment.IsStaging())
    {
        Console.WriteLine("Staging central connection can be written only when ASPNETCORE_ENVIRONMENT is Staging.");
        Environment.ExitCode = 1;
        return;
    }

    var confirmedStagingCentral = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    if (!string.Equals(confirmedStagingCentral, StagingSqlDefaults.CentralDatabaseName, StringComparison.Ordinal))
    {
        Console.WriteLine($"This command requires --confirm-database {StagingSqlDefaults.CentralDatabaseName}.");
        Environment.ExitCode = 1;
        return;
    }

    static string? Option(string[] source, string name)
    {
        for (var index = 0; index < source.Length - 1; index++)
        {
            if (string.Equals(source[index], name, StringComparison.Ordinal))
            {
                return source[index + 1];
            }
        }

        return null;
    }

    var centralServer = Option(args, "--server")?.Trim();
    var centralUser = Option(args, "--username")?.Trim();
    var centralPortText = Option(args, "--port")?.Trim();
    if (string.IsNullOrWhiteSpace(centralServer)
        || string.IsNullOrWhiteSpace(centralUser)
        || !int.TryParse(centralPortText, out var centralPort)
        || centralPort is < 1 or > 65535
        || centralServer.Contains("XtraPOS_Central", StringComparison.OrdinalIgnoreCase)
        || centralUser.Contains("XtraPOS_Central", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Required arguments: --server, --port, and --username. The password is read from stdin and is not printed.");
        Environment.ExitCode = 1;
        return;
    }

    var centralPassword = Console.IsInputRedirected ? Console.In.ReadLine() : null;
    if (string.IsNullOrWhiteSpace(centralPassword))
    {
        Console.WriteLine("A password was not provided on stdin.");
        Environment.ExitCode = 1;
        return;
    }

    using var stagingCentralHost = builder.Build();
    var stagingContentRoot = stagingCentralHost.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;
    var stagingAppData = Path.GetFullPath(Path.Combine(stagingContentRoot, "App_Data"));
    if (!stagingAppData.StartsWith("/opt/xtrapos-staging/", StringComparison.Ordinal))
    {
        Console.WriteLine("Refusing to write a central connection outside /opt/xtrapos-staging.");
        Environment.ExitCode = 1;
        return;
    }

    await stagingCentralHost.Services.GetRequiredService<CentralConnectionFileStore>().SaveAsync(
        new SqlEndpointSettings(
            DatabaseRole.Central,
            SqlEndpointSettings.Remote,
            centralServer,
            null,
            centralPort,
            StagingSqlDefaults.CentralDatabaseName,
            SqlEndpointSettings.SqlLogin,
            centralUser,
            centralPassword.Trim(),
            true,
            true),
        CancellationToken.None);
    Console.WriteLine($"Saved staging central connection for database '{confirmedStagingCentral}'.");
    return;
}

if (args.Any(arg => string.Equals(arg, "--apply-central-migrations", StringComparison.Ordinal)))
{
    var confirmedCentral = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    var configuredBranch = builder.Configuration[$"{SqlServerOptions.SectionName}:Database"];
    using var centralHost = builder.Build();
    var centralStore = centralHost.Services.GetRequiredService<CentralConnectionFileStore>();
    var savedCentral = await centralStore.ReadAsync(CancellationToken.None);
    if (savedCentral is null && string.Equals(configuredBranch, "XtraPOS_Branch_01", StringComparison.Ordinal))
    {
        using var endpointScope = centralHost.Services.CreateScope();
        savedCentral = await endpointScope.ServiceProvider.GetRequiredService<SyncEndpointStore>()
            .ReadSettingsAsync(DatabaseRole.Central, CancellationToken.None);
    }

    var stagingCentralRejected = builder.Environment.IsStaging()
        && !string.Equals(confirmedCentral, StagingSqlDefaults.CentralDatabaseName, StringComparison.Ordinal);
    if (savedCentral is null
        || string.IsNullOrWhiteSpace(confirmedCentral)
        || !string.Equals(confirmedCentral, savedCentral.DatabaseName, StringComparison.Ordinal)
        || !CentralDatabase.IsAllowedName(confirmedCentral)
        || stagingCentralRejected)
    {
        Console.WriteLine(savedCentral is null
            ? "Central migration could not read the saved central connection."
            : stagingCentralRejected
                ? $"Staging central migration requires --confirm-database {StagingSqlDefaults.CentralDatabaseName}."
                : $"Central migration requires --confirm-database with the exact saved central database name '{savedCentral.DatabaseName}'.");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine($"Applying central migrations for database '{confirmedCentral}'. No database will be dropped.");
    try
    {
        var centralConnection = savedCentral.BuildConnectionString(savedCentral.DatabaseName);
        await UserAdministrationLiveCommand.PrintCountsAsync(centralConnection, "CentralBefore", false, Console.Out, CancellationToken.None);
        await centralHost.Services.GetRequiredService<CentralMigrationRunner>()
            .ApplyAsync(centralConnection, CancellationToken.None);
        await UserAdministrationLiveCommand.PrintCountsAsync(centralConnection, "CentralAfter", false, Console.Out, CancellationToken.None);
        Console.WriteLine($"Central migrations completed for database '{confirmedCentral}'.");
    }
    catch (Exception exception) when (BranchMigrationCommand.IsCatalogUnavailable(exception))
    {
        Console.WriteLine($"Central database '{confirmedCentral}' is not available to the saved login. No database was created and no migration was applied.");
    }
    catch (Exception exception)
    {
        Console.WriteLine(exception.Message);
        Environment.ExitCode = 1;
    }

    return;
}

if (args.Any(arg => string.Equals(arg, "--copy-customer-to-branch", StringComparison.Ordinal)))
{
    var customerArg = args.SkipWhile(arg => !string.Equals(arg, "--customer-id", StringComparison.Ordinal)).Skip(1).FirstOrDefault();
    if (!Guid.TryParse(customerArg, out var copyCustomerId))
    {
        Console.WriteLine("Branch copy requires --customer-id with the existing central customer id.");
        Environment.ExitCode = 1;
        return;
    }

    using var copyHost = builder.Build();
    await using var copyScope = copyHost.Services.CreateAsyncScope();
    await copyScope.ServiceProvider.GetRequiredService<CustomerMasterService>()
        .ReapplyBranchCopyAsync(copyCustomerId, CancellationToken.None);
    Console.WriteLine("Existing central customer was copied to the branch.");
    return;
}

if (BranchMigrationCommand.IsRequested(args))
{
    var confirmed = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    var configured = builder.Configuration[$"{SqlServerOptions.SectionName}:Database"];
    if (!BranchMigrationCommand.TryAuthorize(configured, confirmed, out var migrationDenial))
    {
        Console.WriteLine(migrationDenial);
        Environment.ExitCode = 1;
        return;
    }

    using var migrationHost = builder.Build();
    var migrationConnections = migrationHost.Services.GetRequiredService<SqlConnectionFactory>();
    var migrationRunner = migrationHost.Services.GetRequiredService<MigrationRunner>();
    try
    {
        await UserAdministrationLiveCommand.PrintCountsAsync(
            migrationConnections.CreateBranchConnectionString(),
            "BranchBefore",
            true,
            Console.Out,
            CancellationToken.None);
        await BranchMigrationCommand.ExecuteAsync(
            migrationConnections,
            migrationRunner,
            confirmed!,
            Console.Out,
            CancellationToken.None);
        await UserAdministrationLiveCommand.PrintCountsAsync(
            migrationConnections.CreateBranchConnectionString(),
            "BranchAfter",
            true,
            Console.Out,
            CancellationToken.None);
    }
    catch (Exception exception) when (BranchMigrationCommand.IsCatalogUnavailable(exception))
    {
        Console.WriteLine($"Branch database '{confirmed}' is not available to the staging login. No database was created and no migration was applied.");
    }
    catch (Exception exception)
    {
        Console.WriteLine(exception.Message);
        Environment.ExitCode = 1;
    }

    return;
}

if (UserAdministrationLiveCommand.IsRequested(args))
{
    var confirmedUsers = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    var configuredUsers = builder.Configuration[$"{SqlServerOptions.SectionName}:Database"];
    using var userHost = builder.Build();
    try
    {
        using var userScope = userHost.Services.CreateScope();
        Environment.ExitCode = await UserAdministrationLiveCommand.ExecuteAsync(
            userScope.ServiceProvider.GetRequiredService<UserAdministrationService>(),
            userScope.ServiceProvider.GetRequiredService<SqlConnectionFactory>(),
            configuredUsers,
            confirmedUsers,
            Console.Out,
            CancellationToken.None);
    }
    catch (Exception exception)
    {
        Console.WriteLine(exception.Message);
        Environment.ExitCode = 1;
    }

    return;
}

if (UserPasswordResetCommand.IsRequested(args))
{
    var userKey = UserPasswordResetCommand.ReadUserKey(args);
    var confirmed = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    var configured = builder.Configuration[$"{SqlServerOptions.SectionName}:Database"];
    if (!UserPasswordResetCommand.TryAuthorize(configured, confirmed, userKey, out var passwordDenial))
    {
        Console.WriteLine(passwordDenial);
        Environment.ExitCode = 1;
        return;
    }

    using var passwordHost = builder.Build();
    var passwordConnections = passwordHost.Services.GetRequiredService<SqlConnectionFactory>();
    Console.WriteLine(
        $"Password reset target server '{passwordConnections.ConfiguredServer}' database '{confirmed}'. No migrations will be applied.");
    await using var passwordScope = passwordHost.Services.CreateAsyncScope();
    var reset = await UserPasswordResetCommand.ExecuteAsync(
        passwordScope.ServiceProvider.GetRequiredService<BranchDbContext>(),
        passwordScope.ServiceProvider.GetRequiredService<PasswordService>(),
        userKey!,
        Console.Out,
        CancellationToken.None);
    if (!reset.Updated || reset.TemporaryPassword is null)
    {
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine("TEMPORARY PASSWORD: " + reset.TemporaryPassword);
    return;
}

if (CentralMasterImportCommand.IsRequested(args))
{
    using var importHost = builder.Build();
    var store = importHost.Services.GetRequiredService<CentralConnectionFileStore>();
    var central = await store.ReadAsync(CancellationToken.None);
    if (central is null)
    {
        Console.WriteLine("Central import requires the saved Central SQL connection.");
        Environment.ExitCode = 1;
        return;
    }

    Environment.ExitCode = await CentralMasterImportCommand.ExecuteAsync(
        args,
        central,
        importHost.Services.GetRequiredService<PasswordService>(),
        importHost.Services.GetRequiredService<CentralMigrationRunner>(),
        Console.Out,
        CancellationToken.None);
    return;
}

if (LinkedBranchSetupCommand.IsRequested(args))
{
    var confirmedBranch = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    if (!string.Equals(confirmedBranch, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal))
    {
        Console.WriteLine("Linked branch setup requires --confirm-database XtraPOS_Branch_01.");
        Environment.ExitCode = 1;
        return;
    }

    using var branchHost = builder.Build();
    var branchStore = branchHost.Services.GetRequiredService<CentralConnectionFileStore>();
    var linkedCentral = await branchStore.ReadAsync(CancellationToken.None);
    if (linkedCentral is null)
    {
        Console.WriteLine("Linked branch setup requires the saved Central SQL connection.");
        Environment.ExitCode = 1;
        return;
    }

    Environment.ExitCode = await LinkedBranchSetupCommand.ExecuteAsync(
        args,
        branchHost.Services,
        linkedCentral,
        Console.Out,
        CancellationToken.None);
    return;
}

if (args.Any(arg => string.Equals(arg, "create-central-database", StringComparison.Ordinal)
    || string.Equals(arg, "promote-shared-data-to-central", StringComparison.Ordinal)
    || string.Equals(arg, "bootstrap-central-shared-data", StringComparison.Ordinal)))
{
    var confirmed = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    if (!CentralDatabase.IsAllowedName(confirmed))
    {
        Console.WriteLine("Central command requires --confirm-database with the exact central database name.");
        Environment.ExitCode = 1;
        return;
    }

    using var centralHost = builder.Build();
    await using var centralScope = centralHost.Services.CreateAsyncScope();
    var settings = new SqlEndpointSettings(
        DatabaseRole.Central,
        SqlEndpointSettings.Local,
        centralHost.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SqlServerOptions>>().Value.Server,
        null,
        null,
        confirmed!,
        SqlEndpointSettings.Integrated,
        null,
        null,
        true,
        true);
    var databases = centralScope.ServiceProvider.GetRequiredService<CentralDatabaseService>();
    var sync = centralScope.ServiceProvider.GetRequiredService<SharedDataSync>();
    var store = centralScope.ServiceProvider.GetRequiredService<SyncEndpointStore>();
    if (args.Any(arg => string.Equals(arg, "create-central-database", StringComparison.Ordinal)))
    {
        await databases.CreateIfMissingAsync(settings, CancellationToken.None);
        await store.SaveAsync(settings, SqlEndpointSettings.Linked, CancellationToken.None);
        Console.WriteLine("CENTRAL_DATABASE_READY");
    }

    if (args.Any(arg => string.Equals(arg, "promote-shared-data-to-central", StringComparison.Ordinal)))
    {
        var items = await sync.PromoteAsync(settings, CancellationToken.None);
        Console.WriteLine($"PROMOTED_ITEMS={items}");
    }

    if (args.Any(arg => string.Equals(arg, "bootstrap-central-shared-data", StringComparison.Ordinal)))
    {
        var items = await sync.BootstrapAsync(settings, CancellationToken.None);
        var endpoint = await store.GetAsync(DatabaseRole.Central, CancellationToken.None);
        if (endpoint is not null)
        {
            await sync.PullAsync(endpoint.EndpointId, settings, CancellationToken.None);
        }

        Console.WriteLine($"BOOTSTRAPPED_ITEMS={items}");
    }

    return;
}

if (args.Any(arg => string.Equals(arg, "import-legacy-users", StringComparison.Ordinal)))
{
    using var importHost = builder.Build();
    await using var scope = importHost.Services.CreateAsyncScope();
    var importer = scope.ServiceProvider.GetRequiredService<LegacyUserImporter>();
    var importAllRemaining = args.Any(arg => string.Equals(arg, "--all-remaining", StringComparison.Ordinal));
    if (importAllRemaining)
    {
        var bulkReport = await importer.ImportRemainingAsync(CancellationToken.None);
        Environment.ExitCode = WriteBulkImportReport(bulkReport);
    }
    else
    {
        var report = await importer.ImportAsync(CancellationToken.None);
        Environment.ExitCode = WriteImportReport(report);
    }

    return;
}

if (LegacyCustomerImportCommand.IsRequested(args))
{
    var customerImportRefusal = LegacyCustomerImportCommand.RefuseBeforeConnection(args);
    if (customerImportRefusal is not null)
    {
        Console.WriteLine(customerImportRefusal);
        Environment.ExitCode = 1;
        return;
    }

    using var customerImportHost = builder.Build();
    Environment.ExitCode = await LegacyCustomerImportCommand.ExecuteAsync(
        args,
        customerImportHost.Services,
        Console.Out,
        CancellationToken.None);
    return;
}

if (args.Any(arg => string.Equals(arg, "import-shop1-inventory", StringComparison.Ordinal)))
{
    if (args.Any(arg => string.Equals(arg, "--commit", StringComparison.Ordinal)))
    {
        Console.WriteLine("Shop1 branch commit is not enabled. No branch, inventory, opening balance, or price override was written.");
        Environment.ExitCode = 1;
        return;
    }

    using var shop1Host = builder.Build();
    Environment.ExitCode = await Shop1InventoryDryRunCommand.RunAsync(shop1Host.Services, CancellationToken.None);
    return;
}

if (SalesFoundationAuditCommand.IsRequested(args))
{
    var confirmedSales = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    var configuredSales = builder.Configuration[$"{SqlServerOptions.SectionName}:Database"];
    if (!string.Equals(confirmedSales, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal)
        || !string.Equals(configuredSales, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal))
    {
        Console.WriteLine("Sales foundation audit requires --confirm-database XtraPOS_Branch_01.");
        Environment.ExitCode = 1;
        return;
    }

    using var salesHost = builder.Build();
    var salesStore = salesHost.Services.GetRequiredService<CentralConnectionFileStore>();
    var salesCentral = await salesStore.ReadAsync(CancellationToken.None);
    if (salesCentral is null)
    {
        using var salesScope = salesHost.Services.CreateScope();
        salesCentral = await salesScope.ServiceProvider.GetRequiredService<SyncEndpointStore>()
            .ReadSettingsAsync(DatabaseRole.Central, CancellationToken.None);
    }

    if (salesCentral is null || !string.Equals(salesCentral.DatabaseName, CentralDatabase.Name, StringComparison.Ordinal))
    {
        Console.WriteLine("Sales foundation audit requires the saved Central SQL connection.");
        Environment.ExitCode = 1;
        return;
    }

    var salesConnections = salesHost.Services.GetRequiredService<SqlConnectionFactory>();
    Environment.ExitCode = args.Any(arg => string.Equals(arg, "--sales-gaps-only", StringComparison.Ordinal))
        ? await SalesFoundationAuditCommand.ExecuteGapsAsync(salesCentral, salesConnections, Console.Out, CancellationToken.None)
        : await SalesFoundationAuditCommand.ExecuteAsync(salesCentral, salesConnections, Console.Out, CancellationToken.None);
    return;
}

if (Shop1OperationalAuditCommand.IsRequested(args))
{
    var confirmedShop = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    if (!string.Equals(confirmedShop, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal))
    {
        Console.WriteLine("Shop1 audit requires --confirm-database XtraPOS_Branch_01.");
        Environment.ExitCode = 1;
        return;
    }

    using var auditHost = builder.Build();
    var auditStore = auditHost.Services.GetRequiredService<CentralConnectionFileStore>();
    var auditCentral = await auditStore.ReadAsync(CancellationToken.None);
    if (auditCentral is null)
    {
        Console.WriteLine("Shop1 audit requires the saved Central SQL connection.");
        Environment.ExitCode = 1;
        return;
    }

    Environment.ExitCode = await Shop1OperationalAuditCommand.ExecuteAsync(
        args,
        auditHost.Services,
        auditCentral,
        Console.Out,
        CancellationToken.None);
    return;
}

if (Shop1OperationalImportCommand.IsRequested(args))
{
    var confirmedImport = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    if (!string.Equals(confirmedImport, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal)
        && !string.Equals(confirmedImport, Shop1OperationalImportCommand.TestDatabaseName, StringComparison.Ordinal))
    {
        Console.WriteLine("Shop1 operational import requires --confirm-database XtraPOS_Branch_01 or XtraPOS_Branch_Test_Shop1Import.");
        Environment.ExitCode = 1;
        return;
    }

    using var importHost = builder.Build();
    var importStore = importHost.Services.GetRequiredService<CentralConnectionFileStore>();
    var importCentral = await importStore.ReadAsync(CancellationToken.None);
    if (importCentral is null && !args.Any(arg => string.Equals(arg, Shop1OperationalImportCommand.DropCommand, StringComparison.Ordinal)))
    {
        Console.WriteLine("Shop1 operational import requires the saved Central SQL connection.");
        Environment.ExitCode = 1;
        return;
    }

    var importSettings = importCentral ?? new SqlEndpointSettings(
        DatabaseRole.Central,
        SqlEndpointSettings.Remote,
        "localhost",
        null,
        null,
        CentralDatabase.Name,
        SqlEndpointSettings.Integrated,
        null,
        null,
        true,
        true);
    Environment.ExitCode = await Shop1OperationalImportCommand.ExecuteAsync(
        args,
        importHost.Services,
        importSettings,
        Console.Out,
        CancellationToken.None);
    return;
}

if (args.Any(arg => string.Equals(arg, "import-legacy-items", StringComparison.Ordinal)))
{
    var dryRun = args.Any(arg => string.Equals(arg, "--dry-run", StringComparison.Ordinal));
    var commit = args.Any(arg => string.Equals(arg, "--commit", StringComparison.Ordinal));
    if (commit && dryRun)
    {
        Console.WriteLine("Choose either --dry-run or --commit.");
        Environment.ExitCode = 1;
        return;
    }

    if (!commit && !dryRun)
    {
        Console.WriteLine("Legacy item import requires --dry-run or --commit. Nothing was written.");
        Environment.ExitCode = 1;
        return;
    }

    using var dryRunHost = builder.Build();
    Environment.ExitCode = commit
        ? await LegacyMasterDryRunCommand.RunCommitAsync(dryRunHost.Services, CancellationToken.None)
        : await LegacyMasterDryRunCommand.RunAsync(dryRunHost.Services, CancellationToken.None);
    return;
}

var app = builder.Build();

var savedBranch = await app.Services.GetRequiredService<OperationalBranchConnectionStore>().ReadAsync(CancellationToken.None);
if (savedBranch is not null && OperationalDatabaseGuard.IsProductionCatalog(savedBranch.DatabaseName))
{
    app.Services.GetRequiredService<OperationalBranchConnection>().Replace(savedBranch);
}

if (app.Environment.IsStaging())
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    };
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

app.Use(async (context, next) =>
{
    var directory = context.RequestServices.GetRequiredService<DeviceDatabaseDirectory>();
    if (Guid.TryParse(context.Request.Headers["X-Device-Id"], out var deviceId))
    {
        var database = directory.Find(deviceId);
        if (BranchDatabase.IsTestName(database))
        {
            BranchDatabaseOverride.Set(database);
        }
    }

    var requested = context.Request.Headers["X-Setup-Branch-Database"].ToString();
    if (BranchDatabase.IsTestName(requested))
    {
        BranchDatabaseOverride.Set(requested);
    }

    try
    {
        await next();
    }
    finally
    {
        BranchDatabaseOverride.Set(null);
    }
});

app.UseCors("LocalWeb");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
}));
app.MapPost("/api/local-update", (HttpContext http) =>
{
    var remote = http.Connection.RemoteIpAddress;
    if (remote is null || !System.Net.IPAddress.IsLoopback(remote))
    {
        return Results.NotFound();
    }

    var script = @"D:\Google\XtraPOS\scripts\update-local.ps1";
    if (!File.Exists(script))
    {
        return Results.Json(new { message = "ملف التحديث غير موجود." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
        WorkingDirectory = @"D:\Google\XtraPOS",
        UseShellExecute = true,
        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
    });
    return Results.Ok(new { message = "بدأ التحديث. حدّث الصفحة بعد ثوانٍ." });
});
if (app.Environment.IsStaging())
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/api/diagnostics"))
        {
            await next();
            return;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await next();
            DiagnosticLog.Write(app.Environment, "api", $"{context.Request.Method} {context.Request.Path} {context.Response.StatusCode} {watch.ElapsedMilliseconds}ms");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(app.Environment, "api-error", $"{context.Request.Method} {context.Request.Path} {exception.GetType().Name} {exception.Message}");
            throw;
        }
    });
    app.MapGet("/api/diagnostics/log", (IHostEnvironment environment) => Results.Text(DiagnosticLog.Read(environment), "text/plain; charset=utf-8"));
    app.MapPost("/api/diagnostics/events", async (HttpContext http, IHostEnvironment environment) =>
    {
        var entry = await http.Request.ReadFromJsonAsync<DiagnosticEvent>(http.RequestAborted);
        DiagnosticLog.Write(environment, "ui", $"{entry?.Area} {entry?.Action} {entry?.Detail}");
        return Results.NoContent();
    });
}

app.MapSetupEndpoints();
app.MapDeviceEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapCustomerEndpoints();
app.MapMembershipEndpoints();
app.MapCustomerPasskeyEndpoints();
app.MapItemEndpoints();
app.MapPricingEndpoints();
app.MapCashSessionEndpoints();
app.MapCheckoutEndpoints();
app.MapHeldSaleEndpoints();
app.MapSalesInquiryEndpoints();
app.MapCreditEndpoints();
app.MapCreditCollectionEndpoints();
app.MapCreditReversalEndpoints();
app.MapCreditHistoryEndpoints();
app.MapOperationLogEndpoints();
app.MapCentralEndpoints();

if (args.Any(arg => string.Equals(arg, "--ensure-customer-replica", StringComparison.Ordinal)))
{
    var confirmed = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
    var connections = app.Services.GetRequiredService<SqlConnectionFactory>();
    if (!string.Equals(confirmed, "XtraPOS_Branch_01", StringComparison.Ordinal)
        || !string.Equals(connections.ConfiguredDatabase, confirmed, StringComparison.Ordinal))
    {
        Console.WriteLine("Customer replica bootstrap requires --confirm-database XtraPOS_Branch_01 and that exact configured database.");
        Environment.ExitCode = 1;
        return;
    }

    using var scope = app.Services.CreateScope();
    var store = scope.ServiceProvider.GetRequiredService<SyncEndpointStore>();
    var sync = scope.ServiceProvider.GetRequiredService<SharedDataSync>();
    var endpoint = await store.GetAsync(DatabaseRole.Central, CancellationToken.None);
    var settings = await store.ReadSettingsAsync(DatabaseRole.Central, CancellationToken.None);
    if (endpoint is null || settings is null || !string.Equals(endpoint.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal))
    {
        Console.WriteLine("Customer replica bootstrap requires the saved linked central endpoint.");
        Environment.ExitCode = 1;
        return;
    }

    var state = await sync.EnsureCustomerReplicaAsync(endpoint.EndpointId, settings, CancellationToken.None);
    await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
    await connection.OpenAsync();
    await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.Customers;", connection);
    var customers = (int)(await count.ExecuteScalarAsync() ?? 0);
    Console.WriteLine($"CustomerReplicaState={state} LocalCustomers={customers}");
    return;
}

if (app.Environment.IsStaging())
{
    try
    {
        var added = await XtraPOS.Api.Catalog.StagingCatalogSeed.EnsureAsync(
            app.Services.GetRequiredService<SqlConnectionFactory>(),
            CancellationToken.None);
        Console.WriteLine($"StagingCatalogSeed={added}");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"StagingCatalogSeedFailed={exception.GetType().Name}");
    }
}

if (args.Any(arg => string.Equals(arg, "--branch-agent", StringComparison.Ordinal)))
{
    string? agentServer = null;
    string? pairingCode = null;
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], "--server", StringComparison.Ordinal))
        {
            agentServer = args[index + 1];
        }

        if (string.Equals(args[index], "--pairing-code", StringComparison.Ordinal))
        {
            pairingCode = args[index + 1];
        }
    }

    if (string.IsNullOrWhiteSpace(agentServer))
    {
        Console.WriteLine("Branch agent requires --server.");
        Environment.ExitCode = 1;
        return;
    }

    using var agentStop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        agentStop.Cancel();
    };
    await app.Services.GetRequiredService<BranchAgentWorker>().RunAsync(agentServer, pairingCode, agentStop.Token);
    return;
}

app.Run();

static int WriteImportReport(LegacyUserImportReport report)
{
    foreach (var item in report.Items)
    {
        Console.WriteLine(
            $"{item.RequestedName}\t{item.Outcome}\t{item.EmployeeNumber}\t{item.Username}\t{item.RoleCode}\t{item.Message}");
        if (item.TemporaryPassword is not null)
        {
            Console.WriteLine($"TemporaryPassword\t{item.RequestedName}\t{item.TemporaryPassword}");
        }
    }

    Console.WriteLine($"DevelopmentAdminReused\t{report.DevelopmentAdminReused}");
    Console.WriteLine($"DevelopmentAdminRemainedSeparate\t{report.DevelopmentAdminRemainedSeparate}");
    return report.Items.All(item => item.Outcome is LegacyImportOutcome.Created or LegacyImportOutcome.Updated)
        ? 0
        : 1;
}

static int WriteBulkImportReport(LegacyBulkImportReport report)
{
    Console.WriteLine($"Examined\t{report.Examined}");
    Console.WriteLine($"Matched\t{report.Matched}");
    Console.WriteLine($"Created\t{report.Created}");
    Console.WriteLine($"Updated\t{report.Updated}");
    Console.WriteLine($"ActiveCreated\t{report.ActiveCreated}");
    Console.WriteLine($"InactiveCreated\t{report.InactiveCreated}");
    Console.WriteLine($"Skipped\t{report.Skipped}");
    Console.WriteLine($"Failed\t{report.Failed}");

    foreach (var item in report.Items.Where(item => item.Outcome is "Created" or "Matched"))
    {
        Console.WriteLine(
            $"{item.Outcome}\t{item.EmployeeNumber}\t{item.Username}\t{item.DisplayName}\t{item.FullName}\t{item.IsActive}\t{item.RoleCode}\t{item.BranchAccessActive}\t{item.MustChangePassword}");
    }

    foreach (var item in report.Items.Where(item => item.Outcome is "Skipped" or "Failed"))
    {
        Console.WriteLine($"{item.Outcome}\t{item.EmployeeNumber}\t{item.UserShortName}\t{item.UserFullName}\t{item.Reason}");
    }

    Console.WriteLine("TemporaryPasswords");
    foreach (var item in report.Items.Where(item => item.TemporaryPassword is not null))
    {
        Console.WriteLine($"{item.EmployeeNumber}\t{item.Username}\t{item.TemporaryPassword}");
    }

    return report.Failed == 0 ? 0 : 1;
}

public partial class Program;
