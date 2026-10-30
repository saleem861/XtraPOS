using Microsoft.AspNetCore.Http;
using Xunit;
using Microsoft.Extensions.Configuration;
using XtraPOS.Api.Setup;

namespace XtraPOS.Api.Tests;

public class SetupAuthorizationTests
{
    [Fact]
    public void Database_outage_is_not_first_run()
    {
        var unavailable = SetupStatuses.DatabaseUnavailable;
        Assert.NotEqual(SetupStatuses.NotConfigured, unavailable);
    }

    [Fact]
    public void Missing_token_still_allows_first_setup()
    {
        var context = new DefaultHttpContext();
        var configuration = new ConfigurationBuilder().Build();
        var allowed = SetupAuthorization.TryAuthorize(context.Request, configuration, Path.GetTempPath(), alreadyConfigured: false, out var status, out _);
        Assert.True(allowed);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void Valid_token_authorizes_once_before_configuration()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[SetupAuthorization.HeaderName] = "staging-setup-code";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SetupAuthorization.TokenKey] = "staging-setup-code",
            })
            .Build();
        Assert.True(SetupAuthorization.TryAuthorize(context.Request, configuration, Path.GetTempPath(), alreadyConfigured: false, out _, out _));
        Assert.False(SetupAuthorization.TryAuthorize(context.Request, configuration, Path.GetTempPath(), alreadyConfigured: true, out var status, out _));
        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public void Marker_records_configuration_without_a_database()
    {
        var root = Path.Combine(Path.GetTempPath(), "xtrapos-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(InstallationState.IsConfigured(root));
            InstallationState.MarkConfigured(root);
            Assert.True(InstallationState.IsConfigured(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
