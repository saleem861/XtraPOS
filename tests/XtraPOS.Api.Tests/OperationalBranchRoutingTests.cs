using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Devices;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class OperationalBranchRoutingTests
{
    [Fact]
    public async Task Selected_connection_survives_a_new_process_and_routes_the_factory()
    {
        var directory = Directory.CreateTempSubdirectory("xtrapos-operational");
        try
        {
            var protection = DataProtectionProvider.Create(directory.FullName);
            var store = new OperationalBranchConnectionStore(protection, directory.FullName);
            var selected = Endpoint("selected-host", "XtraPOS_Branch_94", 1433);
            await store.SaveAsync(selected, CancellationToken.None);

            var restarted = new OperationalBranchConnectionStore(protection, directory.FullName);
            var loaded = await restarted.ReadAsync(CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal("XtraPOS_Branch_94", loaded.DatabaseName);
            Assert.Equal("selected-host", loaded.Server);
            Assert.Equal("branch-secret", loaded.Password);

            var holder = new OperationalBranchConnection();
            holder.Replace(loaded);
            var factory = Factory("old-host", "XtraPOS_Branch_01", holder, environment: null);
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(factory.CreateBranchConnectionString());
            Assert.Equal("XtraPOS_Branch_94", builder.InitialCatalog);
            Assert.Contains("selected-host", builder.DataSource, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("old-host", builder.DataSource, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Staging_uses_the_selected_database_instead_of_the_bootstrap_catalog()
    {
        var holder = new OperationalBranchConnection();
        holder.Replace(new SqlEndpointSettings(
            DatabaseRole.Branch,
            SqlEndpointSettings.Local,
            "localhost",
            "BARTENDER",
            null,
            "XtraPOS_Branch_03",
            SqlEndpointSettings.SqlLogin,
            "branch-user",
            "branch-secret",
            true,
            true));
        var factory = Factory("69.30.235.148,10173", "XtraPOS_Branch_Test_Staging", holder, new HostEnv("Staging"));
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(factory.CreateBranchConnectionString());
        Assert.Equal("XtraPOS_Branch_03", factory.EffectiveDatabase);
        Assert.Equal("XtraPOS_Branch_03", builder.InitialCatalog);
        Assert.Equal(@"localhost\BARTENDER", builder.DataSource);
        Assert.DoesNotContain("XtraPOS_Branch_Test_Staging", builder.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("69.30.235.148", builder.DataSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Staging_without_a_selection_keeps_the_bootstrap_catalog()
    {
        var factory = Factory("staging-host", "XtraPOS_Branch_Test_Staging", new OperationalBranchConnection(), new HostEnv("Staging"));
        Assert.Equal("XtraPOS_Branch_Test_Staging", factory.EffectiveDatabase);
    }

    [Fact]
    public void Local_setup_uses_localhost_and_the_selected_database_name()
    {
        var request = new CreateBranchRequest
        {
            BranchLocation = "Local",
            BranchDatabaseName = "XtraPOS_Branch_03",
            BranchAuthentication = "Integrated",
        };
        var selected = SelectedBranchTarget.FromRequest(request);
        Assert.NotNull(selected);
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(selected.BuildConnectionString(selected.DatabaseName));
        Assert.Equal("XtraPOS_Branch_03", builder.InitialCatalog);
        Assert.Equal("localhost", builder.DataSource);
        Assert.True(builder.IntegratedSecurity);
        Assert.DoesNotContain("XtraPOS_Branch_Test_Staging", builder.ConnectionString, StringComparison.OrdinalIgnoreCase);

        var explicitRemote = SelectedBranchTarget.FromRequest(new CreateBranchRequest
        {
            BranchLocation = "Remote",
            BranchServer = "db.example",
            BranchInstance = "BARTENDER",
            BranchDatabaseName = "XtraPOS_Branch_04",
            BranchAuthentication = "Sql",
            BranchUsername = "operator",
            BranchPassword = "operator-secret",
        });
        Assert.NotNull(explicitRemote);
        var remoteBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(explicitRemote.BuildConnectionString(explicitRemote.DatabaseName));
        Assert.Equal(@"db.example\BARTENDER", remoteBuilder.DataSource);
        Assert.Equal("XtraPOS_Branch_04", remoteBuilder.InitialCatalog);
        Assert.Equal("operator", remoteBuilder.UserID);
    }

    [Fact]
    public void Rejected_names_are_not_production_catalogs()
    {
        Assert.False(OperationalDatabaseGuard.IsProductionCatalog("XtraPOS_Branch_Test_Staging"));
        Assert.False(OperationalDatabaseGuard.IsProductionCatalog("XtraPOS_Branch_03_Staging"));
        Assert.True(OperationalDatabaseGuard.IsProductionCatalog("XtraPOS_Branch_03"));
        Assert.Equal("تم الاتصال بالخادم، لكن حساب SQL لا يملك صلاحية إنشاء قاعدة بيانات.", SqlEndpointSettings.DescribeSqlFailure(262));
        Assert.Equal("فشل تسجيل الدخول إلى خادم البيانات.", SqlEndpointSettings.DescribeSqlFailure(18456));
        Assert.Equal("تعذر الوصول إلى خادم البيانات من خادم التطبيق.", SqlEndpointSettings.DescribeSqlFailure(26));
        Assert.Equal("تعذر الوصول إلى خادم البيانات من خادم التطبيق.", SqlEndpointSettings.DescribeSqlFailure(53));
        Assert.Equal("قاعدة البيانات موجودة مسبقاً ولم يتم حذفها.", SqlEndpointSettings.DescribeSqlFailure(1801));
    }

    [Fact]
    public void Localhost_and_loopback_match_the_configured_server()
    {
        var factory = Factory("localhost", "XtraPOS_Branch_01", new OperationalBranchConnection(), environment: null);
        Assert.True(factory.MatchesConfigured(Endpoint("127.0.0.1", "XtraPOS_Branch_01", 1433)));
        Assert.False(factory.MatchesConfigured(Endpoint("localhost", "XtraPOS_Branch_94", null)));
        Assert.False(factory.MatchesConfigured(Endpoint("branch-lan", "XtraPOS_Branch_01", 1433)));
    }

    [Fact]
    public async Task Protected_databases_are_not_propagated()
    {
        var service = new SchemaPropagationService(null!);
        var result = await service.PropagateBranchAsync("Server=unused", "XtraPOS_Central", CancellationToken.None);
        Assert.Equal("Skipped", result.Status);
        Assert.False(OperationalDatabaseGuard.IsProductionCatalog("XtraPOS_Branch_01"));
        Assert.False(OperationalDatabaseGuard.IsProductionCatalog("XtraPOS_Branch_Test_Staging"));
        Assert.True(OperationalDatabaseGuard.IsProductionCatalog("XtraPOS_Branch_94"));
        Assert.True(OperationalDatabaseGuard.IsProductionCatalog("SharbelBranch"));
    }

    [Fact]
    public void Shared_financial_tables_stay_blocked_and_cashbox_numbers_are_independent()
    {
        Assert.True(SchemaConvergence.IsBlocked("CustomerCreditLedger"));
        Assert.True(SchemaConvergence.IsBlocked("CustomerReceivables"));
        Assert.Contains("Sales", SchemaConvergence.BranchOnlyTables);
        Assert.Contains("CentralPosRegistry", SchemaConvergence.CentralOnlyTables);
        Assert.Equal(4, PosNumberAllocator.SuggestNext([1, 2, 3, 5]));
        Assert.Equal(2, PosNumberAllocator.SuggestNext([1]));
        Assert.False(PosNumberAllocator.IsAvailable([1, 4], 4));
        Assert.True(PosNumberAllocator.IsAvailable([1], 2));
    }

    private static SqlConnectionFactory Factory(
        string server,
        string database,
        OperationalBranchConnection holder,
        IHostEnvironment? environment) =>
        new(
            Options.Create(new SqlServerOptions
            {
                Server = server,
                Database = database,
                User = "branch-user",
                Password = "branch-secret",
                ConnectTimeoutSeconds = 15,
            }),
            environment,
            holder);

    private static SqlEndpointSettings Endpoint(string server, string database, int? port) =>
        new(
            DatabaseRole.Branch,
            SqlEndpointSettings.Remote,
            server,
            null,
            port,
            database,
            SqlEndpointSettings.SqlLogin,
            "branch-user",
            "branch-secret",
            true,
            true);

    private sealed class HostEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "XtraPOS";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
