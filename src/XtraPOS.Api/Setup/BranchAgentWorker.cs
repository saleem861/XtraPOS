using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace XtraPOS.Api.Setup;

public sealed class BranchAgentWorker(
    IServiceScopeFactory scopes,
    IHostEnvironment environment,
    IDataProtectionProvider protection,
    LocalPosSessionStore localSessions,
    ILogger<BranchAgentWorker>? logger = null,
    LocalAppHostState? localAppHost = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private volatile LocalBranchConfiguration? _configuredBranch;
    private volatile bool _configuredBranchDetailsLoaded;

    public async Task RunAsync(string server, string? pairingCode, CancellationToken cancellationToken)
    {
        var configuredServer = server.TrimEnd('/');
        using var http = new HttpClient { BaseAddress = new Uri(configuredServer + "/"), Timeout = TimeSpan.FromSeconds(30) };
        var session = new AgentSession(configuredServer);
        LoadSavedSession(session);
        await RefreshConfiguredBranchAsync(cancellationToken);
        await using var local = new BranchAgentLocalEndpoint(
            () => CurrentStatus(session),
            (request, token) => AcceptPairAsync(http, session, request, token),
            BranchAgentLocalEndpoint.DefaultPort);
        local.Start();

        var lastHeartbeat = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            var token = await WaitForTokenAsync(http, session, pairingCode, cancellationToken);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now - lastHeartbeat >= TimeSpan.FromSeconds(15))
                {
                    using var heartbeatResponse = await SendWithResponseAsync(
                        http,
                        HttpMethod.Post,
                        "api/branch-agent/heartbeat",
                        new { instances = LocalInstances() },
                        cancellationToken);
                    if (heartbeatResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        ResetSession(session);
                        continue;
                    }

                    heartbeatResponse.EnsureSuccessStatusCode();
                    if (!_configuredBranchDetailsLoaded && InstallationState.IsConfigured(environment.ContentRootPath))
                    {
                        await RefreshConfiguredBranchAsync(cancellationToken);
                    }

                    lastHeartbeat = now;
                }

                using var response = await http.GetAsync("api/branch-agent/jobs", cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    ResetSession(session);
                    continue;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var job = await response.Content.ReadFromJsonAsync<ClaimedBranchJob>(Json, cancellationToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    continue;
                }

                try
                {
                    await ExecuteAsync(http, job, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var message = exception is BranchAgentRejectedException rejected
                        ? rejected.Message
                        : XtraPOS.Api.Sync.SqlEndpointSettings.DescribeConnectionFailure(exception);
                    try
                    {
                        await PostAsync(http, job, new BranchAgentProgress("failed", null, null, message, false), cancellationToken);
                    }
                    catch (BranchAgentRejectedException)
                    {
                        // The job was already closed by a completed or failed report.
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger?.LogWarning(exception, "Branch agent poll cycle failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task<string> WaitForTokenAsync(HttpClient http, AgentSession session, string? pairingCode, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(session.Token))
        {
            return session.Token;
        }

        if (!string.IsNullOrWhiteSpace(pairingCode))
        {
            await AcceptPairAsync(http, session, new LocalPairRequest(pairingCode, session.Server, false), cancellationToken);
            return session.Token!;
        }

        while (string.IsNullOrWhiteSpace(session.Token) && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return session.Token ?? throw new OperationCanceledException(cancellationToken);
    }

    private async Task<LocalAgentStatus> AcceptPairAsync(HttpClient http, AgentSession session, LocalPairRequest request, CancellationToken cancellationToken)
    {
        var target = BranchAgentLocalDecision.NormalizeServer(string.IsNullOrWhiteSpace(request.Server) ? session.Server : request.Server);
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new BranchAgentRejectedException("موقع الربط غير محدد.");
        }

        if (!string.IsNullOrWhiteSpace(session.Token)
            && !string.Equals(BranchAgentLocalDecision.NormalizeServer(session.Server), target, StringComparison.OrdinalIgnoreCase))
        {
            if (!request.ConfirmTransfer)
            {
                throw new BranchAgentTransferRequiredException("هذه الخدمة مربوطة بموقع آخر. النقل يحتاج تأكيداً صريحاً.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(session.Token))
        {
            return CurrentStatus(session);
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new BranchAgentRejectedException("رمز الربط مطلوب لتسجيل جهاز الفرع.");
        }

        using var pairHttp = new HttpClient { BaseAddress = new Uri(target + "/"), Timeout = TimeSpan.FromSeconds(30) };
        var response = await SendAsync(pairHttp, HttpMethod.Post, "api/branch-agent/pair", new
        {
            code = request.Code.Trim(),
            machineName = Environment.MachineName,
            instances = LocalInstances(),
        }, cancellationToken);
        var paired = await response.Content.ReadFromJsonAsync<PairedResponse>(Json, cancellationToken);
        if (paired is null || string.IsNullOrWhiteSpace(paired.Token) || paired.AgentId == Guid.Empty)
        {
            throw new BranchAgentRejectedException("تعذر تسجيل جهاز الفرع.");
        }

        SaveSession(paired.Token, target, paired.AgentId);
        session.Token = paired.Token;
        session.Server = target;
        session.AgentId = paired.AgentId;
        http.BaseAddress = new Uri(target + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", paired.Token);
        return CurrentStatus(session);
    }

    private LocalAgentStatus CurrentStatus(AgentSession session)
    {
        var instances = LocalInstances();
        var localAppUrl = _configuredBranch is not null
            && !string.IsNullOrWhiteSpace(localAppHost?.BaseUrl)
            && File.Exists(Path.Combine(environment.ContentRootPath, "wwwroot", "index.html"))
            ? localAppHost.BaseUrl
            : null;
        return new LocalAgentStatus(
            BranchAgentLocalDecision.Product,
            "0.3.0",
            Environment.MachineName,
            !string.IsNullOrWhiteSpace(session.Token) && session.AgentId != Guid.Empty,
            session.Server,
            session.AgentId == Guid.Empty ? null : session.AgentId.ToString("D"),
            instances,
            SqlAvailable(instances),
            _configuredBranch,
            localAppUrl);
    }

    public void ForgetConfiguredBranch()
    {
        _configuredBranch = null;
        _configuredBranchDetailsLoaded = false;
    }

    private async Task RefreshConfiguredBranchAsync(CancellationToken cancellationToken)
    {
        if (!InstallationState.IsConfigured(environment.ContentRootPath))
        {
            _configuredBranch = null;
            _configuredBranchDetailsLoaded = false;
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<OperationalBranchConnectionStore>();
            var settings = await store.ReadAsync(cancellationToken);
            if (settings is null)
            {
                return;
            }

            var serverLabel = string.IsNullOrWhiteSpace(settings.InstanceName)
                ? settings.Server
                : $"{settings.Server}\\{settings.InstanceName}";
            _configuredBranch ??= new LocalBranchConfiguration(
                "",
                null,
                settings.DatabaseName,
                null,
                settings.DatabaseName,
                serverLabel,
                null,
                null,
                null);

            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
            await connection.OpenAsync(cancellationToken);
            await using var command = new Microsoft.Data.SqlClient.SqlCommand(
                """
                SELECT TOP (1)
                    b.BusinessName,
                    b.BusinessDisplayName,
                    br.BranchName,
                    br.BranchCode,
                    d.DeviceName,
                    s.StationNumber,
                    s.StationName
                FROM dbo.Devices AS d
                INNER JOIN dbo.Branches AS br ON br.BranchID = d.BranchID
                INNER JOIN dbo.Businesses AS b ON b.BusinessID = br.BusinessID
                LEFT JOIN dbo.POSStations AS s ON s.StationID = d.StationID
                WHERE EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = N'SetupCompletedAt')
                ORDER BY d.RegisteredAt DESC;
                """,
                connection)
            {
                CommandTimeout = 5,
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                _configuredBranch = new LocalBranchConfiguration(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    settings.DatabaseName,
                    serverLabel,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6));
                _configuredBranchDetailsLoaded = true;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogDebug(exception, "Could not refresh configured local branch details.");
        }
    }

    private void LoadSavedSession(AgentSession session)
    {
        var protector = protection.CreateProtector("XtraPOS.BranchAgent");
        var tokenPath = TokenPath();
        var identityPath = IdentityPath();
        if (!File.Exists(tokenPath) || !File.Exists(identityPath))
        {
            return;
        }

        try
        {
            var identity = JsonSerializer.Deserialize<SavedIdentity>(File.ReadAllText(identityPath), Json);
            if (identity is null || identity.AgentId == Guid.Empty || string.IsNullOrWhiteSpace(identity.Server))
            {
                return;
            }

            session.Token = protector.Unprotect(File.ReadAllText(tokenPath));
            session.Server = BranchAgentLocalDecision.NormalizeServer(identity.Server);
            session.AgentId = identity.AgentId;
        }
        catch (Exception)
        {
            session.Token = null;
            session.AgentId = Guid.Empty;
        }
    }

    private void SaveSession(string token, string server, Guid agentId)
    {
        var protector = protection.CreateProtector("XtraPOS.BranchAgent");
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath())!);
        WriteAtomic(TokenPath(), protector.Protect(token));
        WriteAtomic(IdentityPath(), JsonSerializer.Serialize(new SavedIdentity { Server = server, AgentId = agentId }, Json));
    }

    private string TokenPath() => Path.Combine(environment.ContentRootPath, "App_Data", "branch-agent.token");

    private string IdentityPath() => Path.Combine(environment.ContentRootPath, "App_Data", "branch-agent.identity.json");

    private static void WriteAtomic(string path, string contents)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }

    private static bool SqlAvailable(IReadOnlyList<string> instances)
    {
        if (!OperatingSystem.IsWindows() || instances.Count == 0)
        {
            return false;
        }

        try
        {
            using var connection = new Microsoft.Data.SqlClient.SqlConnection(
                "Server=" + instances[0] + ";Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=3");
            connection.Open();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task PostAsync(HttpClient http, ClaimedBranchJob job, BranchAgentProgress progress, CancellationToken cancellationToken)
    {
        await SendAsync(http, HttpMethod.Post, $"api/branch-agent/jobs/{job.Id}/events", new
        {
            nonce = job.Nonce,
            progress.Type,
            progress.Step,
            progress.State,
            progress.Message,
            progress.RemoteAgent,
        }, cancellationToken);
    }

    private void ResetSession(AgentSession session)
    {
        session.Token = null;
        session.AgentId = Guid.Empty;
        try
        {
            var tokenPath = TokenPath();
            if (File.Exists(tokenPath))
            {
                File.Delete(tokenPath);
            }

            var identityPath = IdentityPath();
            if (File.Exists(identityPath))
            {
                File.Delete(identityPath);
            }
        }
        catch (Exception)
        {
            // Ignore cleanup failure; the next successful pair overwrites both files.
        }
    }

    private static async Task<HttpResponseMessage> SendWithResponseAsync(HttpClient http, HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: Json) };
        return await http.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: Json) };
        var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task ExecuteAsync(HttpClient http, ClaimedBranchJob job, CancellationToken cancellationToken)
    {
        if (!string.Equals(job.Operation, BranchAgentRules.ProvisionOperation, StringComparison.Ordinal))
        {
            throw new BranchAgentRejectedException("العملية غير مسموحة.");
        }

        await using var scope = scopes.CreateAsyncScope();
        var request = JsonSerializer.Deserialize<CreateBranchRequest>(string.IsNullOrWhiteSpace(job.SetupJson) ? "{}" : job.SetupJson, Json);
        if (request is not null && BranchSetupValidator.TryValidate(request, out var model, out _))
        {
            var setup = scope.ServiceProvider.GetRequiredService<SetupService>();
            var selected = BranchAgentExecutor.LocalSettings(job.Instance, job.DatabaseName);
            var topology = SetupTopologyBuilder.Build(request, selected);
            await setup.RunAsync(model, async (setupEvent, token) =>
            {
                if (setupEvent.Type == "completed")
                {
                    if (setupEvent.Device is not null)
                    {
                        localSessions.Save(setupEvent.Device);
                    }

                    await RefreshConfiguredBranchAsync(token);
                }

                await PostAsync(http, job, ToProgress(setupEvent), token);
            }, cancellationToken, topology);
            return;
        }

        var executor = scope.ServiceProvider.GetRequiredService<BranchAgentExecutor>();
        await executor.ProvisionAsync(job, (progress, token) => PostAsync(http, job, progress, token), cancellationToken);
        await RefreshConfiguredBranchAsync(cancellationToken);
    }

    private static BranchAgentProgress ToProgress(SetupEvent setupEvent)
    {
        if (setupEvent.Type == "completed")
        {
            return new BranchAgentProgress("completed", null, null, "تم إنشاء قاعدة الفرع على جهاز الفرع.", true);
        }

        if (setupEvent.Type == "failed")
        {
            return new BranchAgentProgress("failed", setupEvent.Step, setupEvent.State, setupEvent.Message, false);
        }

        return new BranchAgentProgress("progress", setupEvent.Step, setupEvent.State, setupEvent.Message, false);
    }

    private static string[] LocalInstances()
    {
        var instances = new List<string> { "localhost" };
        if (!OperatingSystem.IsWindows())
        {
            return instances.ToArray();
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
            if (key is null)
            {
                return instances.ToArray();
            }

            foreach (var name in key.GetValueNames())
            {
                if (!name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                    && BranchAgentRules.TryNormalizeLocalInstance(@"localhost\" + name, out var normalized))
                {
                    instances.Add(normalized);
                }
            }
        }
        catch (Exception)
        {
            return instances.ToArray();
        }

        return instances.ToArray();
    }

    private sealed class PairedResponse
    {
        public Guid AgentId { get; set; }
        public string Token { get; set; } = "";
    }

    private sealed class SavedIdentity
    {
        public string Server { get; set; } = "";
        public Guid AgentId { get; set; }
    }

    private sealed class AgentSession(string server)
    {
        public string Server { get; set; } = server;
        public string? Token { get; set; }
        public Guid AgentId { get; set; }
    }
}
