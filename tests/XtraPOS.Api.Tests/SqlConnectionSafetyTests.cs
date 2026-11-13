using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using Xunit;

namespace XtraPOS.Api.Tests;

public class SqlConnectionSafetyTests
{
    [Fact]
    public void MissingServerIsTheFailingCondition()
    {
        var factory = Factory(new SqlServerOptions
        {
            Server = "",
            Database = StagingSqlDefaults.BranchDatabaseName,
            ConnectTimeoutSeconds = 15,
        });

        Assert.False(factory.IsSafeConfiguration(out var problem));
        Assert.Equal("لم يتم إعداد الاتصال بخادم البيانات.", problem);
    }

    [Fact]
    public void UserWithoutPasswordIsTheFailingCondition()
    {
        var factory = Factory(new SqlServerOptions
        {
            Server = "localhost",
            Database = StagingSqlDefaults.BranchDatabaseName,
            User = "xtrapos_staging",
            Password = "",
            ConnectTimeoutSeconds = 15,
        });

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateBranchConnectionString());

        Assert.Contains("SQL Server configuration is not safe to use:", exception.Message);
        Assert.Contains("اسم مستخدم SQL وكلمة المرور يجب أن يُضبطا معاً أو يُتركا فارغين.", exception.Message);
        Assert.DoesNotContain("xtrapos_staging", exception.Message);
    }

    [Fact]
    public void PasswordWithoutUserDoesNotExposeThePassword()
    {
        var factory = Factory(new SqlServerOptions
        {
            Server = "localhost",
            Database = StagingSqlDefaults.BranchDatabaseName,
            User = "",
            Password = "super-secret-value",
            ConnectTimeoutSeconds = 15,
        });

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateBranchConnectionString());

        Assert.Contains("يُضبطا معاً", exception.Message);
        Assert.DoesNotContain("super-secret-value", exception.Message);
    }

    [Fact]
    public void ConnectTimeoutOutsideTheAllowedRangeIsTheFailingCondition()
    {
        var factory = Factory(new SqlServerOptions
        {
            Server = "localhost",
            Database = StagingSqlDefaults.BranchDatabaseName,
            ConnectTimeoutSeconds = 0,
        });

        Assert.False(factory.IsSafeConfiguration(out var problem));
        Assert.Equal("مهلة الاتصال يجب أن تكون بين 1 و 120 ثانية.", problem);
    }

    [Fact]
    public void StagingRequiresTheBranchTestDatabase()
    {
        var factory = Factory(new SqlServerOptions
        {
            Server = "localhost",
            Database = BranchDatabase.Name,
            ConnectTimeoutSeconds = 15,
        }, staging: true);

        Assert.False(factory.IsSafeConfiguration(out var problem));
        Assert.Equal("قاعدة الفرع في بيئة الاختبار يجب أن تكون XtraPOS_Branch_Test_Staging.", problem);
    }

    [Fact]
    public void StagingAcceptsPairedEmptyCredentialsAndTheTestDatabase()
    {
        var factory = Factory(new SqlServerOptions
        {
            Server = "69.30.235.148,10173",
            Database = StagingSqlDefaults.BranchDatabaseName,
            User = "",
            Password = "",
            ConnectTimeoutSeconds = 15,
        }, staging: true);

        Assert.True(factory.IsSafeConfiguration(out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void StagingDefaultsReplaceOnlyTheOperationalBranchName()
    {
        var missing = StagingSqlDefaults.MissingValues(BranchDatabase.Name);

        Assert.False(missing.ContainsKey("SqlServer:Server"));
        Assert.Equal(StagingSqlDefaults.BranchDatabaseName, missing["SqlServer:Database"]);

        var configured = StagingSqlDefaults.MissingValues("XtraPOS_Branch_01");

        Assert.Empty(configured);
        Assert.Equal(StagingSqlDefaults.CentralDatabaseName, "XtraPOS_Central_Test_Staging");
    }

    [Fact]
    public void ActiveOperationalBranchSatisfiesSafetyWithoutBootstrapServer()
    {
        var operational = new XtraPOS.Api.Setup.OperationalBranchConnection();
        operational.Replace(new XtraPOS.Api.Sync.SqlEndpointSettings(
            DatabaseRole.Branch,
            XtraPOS.Api.Sync.SqlEndpointSettings.Local,
            "localhost",
            null,
            null,
            "XtraPOS_Branch2",
            XtraPOS.Api.Sync.SqlEndpointSettings.Integrated,
            null,
            null,
            true,
            true));

        var factory = new SqlConnectionFactory(
            Options.Create(new SqlServerOptions
            {
                Server = "",
                Database = BranchDatabase.Name,
                ConnectTimeoutSeconds = 15,
            }),
            environment: null,
            operational);

        Assert.True(factory.IsSafeConfiguration(out var problem));
        Assert.Null(problem);
        Assert.Equal("XtraPOS_Branch2", factory.EffectiveDatabase);
    }

    private static SqlConnectionFactory Factory(SqlServerOptions options, bool staging = false) =>
        new(Options.Create(options), staging ? new StagingEnvironment() : null);

    private sealed class StagingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Staging;
        public string ApplicationName { get; set; } = "XtraPOS.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
