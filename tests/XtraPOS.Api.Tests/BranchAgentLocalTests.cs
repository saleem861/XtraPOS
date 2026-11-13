using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Setup;
using Xunit;

namespace XtraPOS.Api.Tests;

public class BranchAgentLocalTests
{
    [Fact]
    public void Ready_agent_matches_only_the_expected_site()
    {
        var status = new LocalAgentStatus(
            BranchAgentLocalDecision.Product,
            "0.3.0",
            "BRANCH-PC",
            true,
            "https://test-pos.sharbelfashion.com",
            Guid.NewGuid().ToString(),
            ["localhost", @"localhost\BARTENDER"],
            true);

        Assert.Equal("ready", BranchAgentLocalDecision.Decide(status, status.Server + "/", "0.3.0"));
        Assert.Equal("other-environment", BranchAgentLocalDecision.Decide(status, "https://other.example", "0.3.0"));
        Assert.Equal("outdated", BranchAgentLocalDecision.Decide(status with { Version = "0.2.0" }, status.Server, "0.3.0"));
        Assert.Equal("sql-missing", BranchAgentLocalDecision.Decide(status with { SqlAvailable = false }, status.Server, "0.3.0"));
        Assert.Equal("unpaired", BranchAgentLocalDecision.Decide(status with { Paired = false, AgentId = null }, status.Server, "0.3.0"));
        Assert.Equal("missing", BranchAgentLocalDecision.Decide(null, status.Server, "0.3.0"));
    }

    [Fact]
    public void Installer_rejects_zip_paths_outside_the_install_folder()
    {
        Assert.False(BranchAgentLocalDecision.IsSafeZipEntry(@"..\windows\system32\cmd.exe"));
        Assert.False(BranchAgentLocalDecision.IsSafeZipEntry("/etc/passwd"));
        Assert.True(BranchAgentLocalDecision.IsSafeZipEntry("XtraPOS.Api.exe"));
    }

    [Fact]
    public async Task Local_status_and_transfer_refusal_use_localhost_only()
    {
        var port = 47332;
        var configured = new LocalBranchConfiguration("منشأة الاختبار", null, "الفرع الرئيسي", "001", "XtraPOS_Branch", "localhost", "POS-01", 1, "نقطة 1");
        await using var endpoint = new BranchAgentLocalEndpoint(
            () => new LocalAgentStatus(BranchAgentLocalDecision.Product, "0.3.0", "BRANCH-PC", true, "https://other.example", Guid.NewGuid().ToString(), ["localhost"], true, configured),
            (request, _) => request.ConfirmTransfer
                ? Task.FromResult(new LocalAgentStatus(BranchAgentLocalDecision.Product, "0.3.0", "BRANCH-PC", true, "https://test-pos.sharbelfashion.com", Guid.NewGuid().ToString(), ["localhost"], true, configured))
                : throw new BranchAgentTransferRequiredException("هذه الخدمة مربوطة بموقع آخر."),
            port);
        endpoint.Start();
        using var http = new HttpClient();
        var status = await http.GetFromJsonAsync<LocalAgentStatus>($"http://127.0.0.1:{port}/status");
        Assert.Equal(BranchAgentLocalDecision.Product, status?.Product);
        Assert.Equal("XtraPOS_Branch", status?.ConfiguredBranch?.DatabaseName);
        Assert.Equal("الفرع الرئيسي", status?.ConfiguredBranch?.BranchName);
        var refused = await http.PostAsJsonAsync($"http://127.0.0.1:{port}/pair", new { code = "unused", server = "https://test-pos.sharbelfashion.com", confirmTransfer = false });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.False(BranchAgentLocalEndpoint.IsAllowedOrigin("https://evil.example"));
        Assert.True(BranchAgentLocalEndpoint.IsAllowedOrigin("https://test-pos.sharbelfashion.com"));
    }

    [Fact]
    public void Branch_agent_host_resolves_required_setup_services()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "xtrapos-agent-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var host = BranchAgentHostCommand.BuildHost(
                ["--branch-agent", "--server", "https://test-pos.sharbelfashion.com"],
                "https://test-pos.sharbelfashion.com",
                null,
                tempRoot);
            Assert.NotNull(host.Services.GetRequiredService<BranchAgentWorker>());
            using var scope = host.Services.CreateScope();
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<SetupService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<BranchAgentExecutor>());

            var router = scope.ServiceProvider.GetRequiredService<BranchDestinationRouter>();
            var connections = scope.ServiceProvider.GetRequiredService<XtraPOS.Api.Data.SqlConnectionFactory>();
            var selected = BranchAgentExecutor.LocalSettings(@"localhost\BARTENDER", "XtraPOS_Branch_03");
            router.Bind(selected);
            Assert.Contains(@"localhost\BARTENDER", connections.CreateBranchConnectionString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("XtraPOS_Branch_03", connections.CreateBranchConnectionString(), StringComparison.Ordinal);
            Assert.Contains("master", connections.CreateMasterConnectionString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public void Setup_topology_builder_preserves_central_management_with_local_branch_override()
    {
        var adminId = Guid.NewGuid();
        var request = new CreateBranchRequest
        {
            OperatingMode = "CentralManagement",
            CentralAction = "existing",
            CentralLocation = "Remote",
            CentralServer = "69.30.235.148",
            CentralPort = 10173,
            CentralDatabaseName = "XtraPOS_Central_Test_Staging",
            CentralAuthentication = "Sql",
            CentralUsername = "xtrapos_staging_central",
            CentralPassword = "secret",
            LinkToCentral = "yes",
            CentralAdminUserId = adminId.ToString("D"),
            BranchDatabaseName = "XtraPOS_Branch_03",
        };
        var local = BranchAgentExecutor.LocalSettings(@"localhost\BARTENDER", "XtraPOS_Branch_03");

        var topology = SetupTopologyBuilder.Build(request, local);
        Assert.Equal("CentralManagement", topology.OperatingMode);
        Assert.True(topology.LinkToCentral);
        Assert.False(topology.CreateCentral);
        Assert.Equal(adminId, topology.CentralAdminUserId);
        Assert.Equal("XtraPOS_Central_Test_Staging", topology.Central?.DatabaseName);
        Assert.Equal("BARTENDER", topology.OperationalBranch?.InstanceName);
        Assert.Equal("XtraPOS_Branch_03", topology.OperationalBranch?.DatabaseName);
    }
}
