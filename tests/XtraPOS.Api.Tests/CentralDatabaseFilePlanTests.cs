using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class CentralDatabaseFilePlanTests
{
    [Fact]
    public void CreateStatement_KeepsTheVisibleNameAndServerDirectories()
    {
        var directories = new CentralDatabaseDirectories(@"D:\sql\data\", @"D:\sql\log\");
        var first = CentralDatabaseFiles.CreatePlan(CentralDatabase.Name, directories, "abc123def456");
        var second = CentralDatabaseFiles.CreatePlan(CentralDatabase.Name, directories, "789abc456def");
        var sql = CentralDatabaseFiles.BuildCreateSql(CentralDatabase.Name, first);

        Assert.Contains("CREATE DATABASE [XtraPOS_Central]", sql, StringComparison.Ordinal);
        Assert.Contains("COLLATE Arabic_CI_AS", sql, StringComparison.Ordinal);
        Assert.Equal(@"D:\sql\data\XtraPOS_Central_abc123def456.mdf", first.DataFileName);
        Assert.Equal(@"D:\sql\log\XtraPOS_Central_abc123def456_log.ldf", first.LogFileName);
        Assert.NotEqual(first.DataFileName, second.DataFileName);
        Assert.NotEqual(first.LogFileName, second.LogFileName);
        Assert.DoesNotContain("/cloudclusters", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users", sql, StringComparison.Ordinal);
        Assert.Contains("N'D:\\sql\\o''brien\\XtraPOS_Central_abc123def456.mdf'", CentralDatabaseFiles.SqlString(@"D:\sql\o'brien\XtraPOS_Central_abc123def456.mdf"), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoGeneratedSuffixes_AreDifferent()
    {
        var directories = new CentralDatabaseDirectories("/var/opt/mssql/data/", "/var/opt/mssql/data/");
        var first = CentralDatabaseFiles.CreatePlan(CentralDatabase.Name, directories);
        var second = CentralDatabaseFiles.CreatePlan(CentralDatabase.Name, directories);
        Assert.NotEqual(first.DataFileName, second.DataFileName);
        Assert.NotEqual(first.LogFileName, second.LogFileName);
        Assert.StartsWith("/var/opt/mssql/data/XtraPOS_Central_", first.DataFileName, StringComparison.Ordinal);
        Assert.EndsWith("_log.ldf", first.LogFileName, StringComparison.Ordinal);
    }
}
