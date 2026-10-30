using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class BranchApiFixture : IAsyncLifetime
{
    private const string Password = "Admin1234";

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public Guid DeviceId { get; private set; }
    public string DeviceToken { get; private set; } = "";
    public string Username { get; } = "admin";

    public async Task InitializeAsync()
    {
        await DropAsync();
        Factory = new XtraPosWebFactory();
        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/setup/create-branch", new
        {
            businessName = "منشأة التجربة",
            branchName = "فرع التجربة",
            branchType = "Retail",
            stationNumber = 1,
            stationName = "كاونتر 1",
            deviceName = "صندوق 1",
            username = Username,
            displayName = "مدير التجربة",
            password = Password,
            confirmPassword = Password,
        });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Setup failed ({(int)response.StatusCode}): {body}");
        }

        string? token = null;
        string? deviceId = null;
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("type", out var type)
                && type.GetString() == "completed"
                && document.RootElement.TryGetProperty("device", out var device))
            {
                token = device.GetProperty("deviceToken").GetString();
                deviceId = device.GetProperty("deviceId").GetString();
            }

            if (document.RootElement.TryGetProperty("type", out var failedType)
                && failedType.GetString() == "failed")
            {
                var message = document.RootElement.TryGetProperty("message", out var messageValue)
                    ? messageValue.GetString()
                    : body;
                throw new InvalidOperationException($"Setup failed: {message}");
            }
        }

        if (string.IsNullOrWhiteSpace(token) || !Guid.TryParse(deviceId, out var parsed))
        {
            throw new InvalidOperationException($"Setup did not return a device token. Body: {body}");
        }

        DeviceToken = token;
        DeviceId = parsed;
    }

    public Task DisposeAsync()
    {
        Factory.Dispose();
        return DropAsync();
    }

    public HttpClient CreateClient()
    {
        return Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false,
        });
    }

    private static Task DropAsync()
    {
        return TestDatabase.DropAsync();
    }
}

[CollectionDefinition("Branch", DisableParallelization = true)]
public sealed class BranchCollection : ICollectionFixture<BranchApiFixture>;
