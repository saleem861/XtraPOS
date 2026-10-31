using Microsoft.AspNetCore.Http;
using Xunit;
using Microsoft.Extensions.Configuration;
using XtraPOS.Api.Setup;

namespace XtraPOS.Api.Tests;

public class SetupAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replacement_is_denied_even_with_a_setup_token(bool configured)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[SetupAuthorization.HeaderName] = "valid-setup-token";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [SetupAuthorization.TokenKey] = "valid-setup-token" }).Build();

        Assert.False(SetupAuthorization.TryAuthorize(context.Request, configuration, Path.GetTempPath(),
            configured, out var status, out var message, replaceExisting: true));
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal(SetupAuthorization.ReplacementDeniedMessage, message);
    }

    [Fact]
    public async Task Direct_service_replacement_is_rejected_before_accessing_dependencies()
    {
        // Null dependencies deliberately fail if replacement reaches any database/setup work.
        var service = new SetupService(null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!);
        var events = new List<SetupEvent>();

        await service.RunAsync(null!, (item, _) =>
        {
            events.Add(item);
            return Task.CompletedTask;
        }, CancellationToken.None, replaceExisting: true);

        var failure = Assert.Single(events);
        Assert.Equal(SetupAuthorization.ReplacementDeniedMessage, failure.Message);
    }

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
