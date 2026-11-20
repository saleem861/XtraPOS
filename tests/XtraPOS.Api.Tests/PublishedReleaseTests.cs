using Microsoft.AspNetCore.Identity;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;
using XtraPOS.Api.Setup;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class PublishedReleaseTests
{
    [Fact]
    public void Finds_the_branch_agent_asset_and_rejects_other_addresses()
    {
        const string json = """
            {"assets":[{"name":"notes.txt","browser_download_url":"https://github.com/saleem861/XtraPOS/releases/download/v1/notes.txt"},{"name":"XtraPOS-BranchAgent.zip","browser_download_url":"https://github.com/saleem861/XtraPOS/releases/download/branch-agent-1/XtraPOS-BranchAgent.zip"}]}
            """;
        var url = PublishedRelease.FindAssetUrl(json);
        Assert.True(PublishedRelease.IsAllowedDownload(url));
        Assert.True(PublishedRelease.IsAllowedDownload(PublishedRelease.StagingPackageUrl));
        Assert.False(PublishedRelease.IsAllowedDownload("https://example.com/XtraPOS-BranchAgent.zip"));
        Assert.False(PublishedRelease.IsAllowedDownload("http://github.com/saleem861/XtraPOS/releases/download/v1/XtraPOS-BranchAgent.zip"));
    }

    [Fact]
    public void Updater_replaces_the_service_files_and_keeps_local_settings()
    {
        var script = PublishedRelease.BuildUpdaterScript(
            @"C:\Program Files\XtraPOS\BranchAgent",
            "https://github.com/saleem861/XtraPOS/releases/download/branch-agent-1/XtraPOS-BranchAgent.zip");
        Assert.Contains("sc.exe stop XtraPOSBranchAgent", script, StringComparison.Ordinal);
        Assert.Contains("sc.exe start XtraPOSBranchAgent", script, StringComparison.Ordinal);
        Assert.Contains("Copy-Item", script, StringComparison.Ordinal);
        Assert.Contains("App_Data", script, StringComparison.Ordinal);
        Assert.Contains("appsettings*.json", script, StringComparison.Ordinal);
        Assert.Contains(@"C:\Program Files\XtraPOS\BranchAgent", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build", script, StringComparison.Ordinal);
        Assert.DoesNotContain("git pull", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_sale_columns_explain_the_checkout_failure()
    {
        Assert.Contains("جداول البيع", CheckoutFailure.Describe(207), StringComparison.Ordinal);
        Assert.Equal("تعذر إتمام البيع (547).", CheckoutFailure.Describe(547));
        Assert.Contains("مخزون", CheckoutFailure.StockMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Restored_cashier_password_matches_the_requested_password()
    {
        var passwords = new PasswordService();
        var user = new AppUser();
        var hash = passwords.Hash(user, AccountAccessRepair.Password);
        Assert.Equal(PasswordVerificationResult.Success, passwords.Verify(user, hash, AccountAccessRepair.Password));
        Assert.Equal("905017083", AccountIdentity.NormalizeUsername(AccountAccessRepair.Username));
    }
}
