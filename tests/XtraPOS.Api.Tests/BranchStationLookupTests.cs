using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Setup;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class BranchStationLookupTests
{
    [Fact]
    public async Task MissingBranchDatabase_IsNotCreatedWhenStationsAreRequested()
    {
        const string name = "XtraPOS_Branch_Test_StationDraft";
        var lookup = new BranchStationLookup();
        var missing = await lookup.ListAsync(new SqlProbeRequest
        {
            DatabaseName = name,
            Server = "localhost",
            ExpectedRole = DatabaseRole.Branch,
        }, CancellationToken.None);

        Assert.False(missing.Ok);
        Assert.Equal("قاعدة البيانات غير موجودة.", missing.Message);
        Assert.Empty(missing.Stations);
        Assert.False(await ExistsAsync(name));

        var refused = await lookup.ListAsync(new SqlProbeRequest
        {
            Intent = "new",
            DatabaseName = name,
            Server = "localhost",
        }, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Equal("لا يمكن قراءة نقاط البيع قبل إنشاء الفرع.", refused.Message);
        Assert.False(await ExistsAsync(name));
    }

    [Fact]
    public async Task CentralAndUnknownNames_AreRefusedWithoutAStationQuery()
    {
        var lookup = new BranchStationLookup();
        var central = await lookup.ListAsync(new SqlProbeRequest { DatabaseName = CentralDatabase.Name, Server = "localhost" }, CancellationToken.None);
        var unknown = await lookup.ListAsync(new SqlProbeRequest { DatabaseName = "saleemPOSDB", Server = "localhost" }, CancellationToken.None);

        Assert.False(central.Ok);
        Assert.False(unknown.Ok);
        Assert.Equal("اسم قاعدة البيانات غير مسموح.", central.Message);
        Assert.Equal("اسم قاعدة البيانات غير مسموح.", unknown.Message);
    }

    private static async Task<bool> ExistsAsync(string name)
    {
        await using var connection = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END;", connection);
        command.Parameters.AddWithValue("@name", name);
        return (int)(await command.ExecuteScalarAsync() ?? 0) == 1;
    }
}
