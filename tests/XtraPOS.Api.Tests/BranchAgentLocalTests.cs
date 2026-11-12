using System.Net;
using System.Net.Http.Json;
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
        await using var endpoint = new BranchAgentLocalEndpoint(
            () => new LocalAgentStatus(BranchAgentLocalDecision.Product, "0.3.0", "BRANCH-PC", true, "https://other.example", Guid.NewGuid().ToString(), ["localhost"], true),
            (request, _) => request.ConfirmTransfer
                ? Task.FromResult(new LocalAgentStatus(BranchAgentLocalDecision.Product, "0.3.0", "BRANCH-PC", true, "https://test-pos.sharbelfashion.com", Guid.NewGuid().ToString(), ["localhost"], true))
                : throw new BranchAgentTransferRequiredException("هذه الخدمة مربوطة بموقع آخر."),
            port);
        endpoint.Start();
        using var http = new HttpClient();
        var status = await http.GetFromJsonAsync<LocalAgentStatus>($"http://127.0.0.1:{port}/status");
        Assert.Equal(BranchAgentLocalDecision.Product, status?.Product);
        var refused = await http.PostAsJsonAsync($"http://127.0.0.1:{port}/pair", new { code = "unused", server = "https://test-pos.sharbelfashion.com", confirmTransfer = false });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.False(BranchAgentLocalEndpoint.IsAllowedOrigin("https://evil.example"));
        Assert.True(BranchAgentLocalEndpoint.IsAllowedOrigin("https://test-pos.sharbelfashion.com"));
    }
}
