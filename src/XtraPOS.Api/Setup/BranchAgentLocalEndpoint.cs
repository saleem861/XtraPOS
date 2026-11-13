using System.Net;
using System.Text.Json;

namespace XtraPOS.Api.Setup;

public sealed record LocalBranchConfiguration(
    string BusinessName,
    string? BusinessDisplayName,
    string BranchName,
    string? BranchCode,
    string DatabaseName,
    string Server,
    string? DeviceName,
    int? StationNumber,
    string? StationName);

public sealed record LocalAgentStatus(
    string Product,
    string Version,
    string MachineName,
    bool Paired,
    string Server,
    string? AgentId,
    IReadOnlyList<string> Instances,
    bool SqlAvailable,
    LocalBranchConfiguration? ConfiguredBranch = null,
    string? LocalAppUrl = null);

public sealed record LocalPairRequest(string? Code, string? Server, bool ConfirmTransfer);

public static class BranchAgentLocalDecision
{
    public const string Product = "XtraPOS.BranchAgent";

    public static string Decide(LocalAgentStatus? status, string expectedServer, string expectedVersion)
    {
        if (status is null || !string.Equals(status.Product, Product, StringComparison.Ordinal))
        {
            return "missing";
        }

        if (CompareVersions(status.Version, expectedVersion) < 0)
        {
            return "outdated";
        }

        if (!status.SqlAvailable)
        {
            return "sql-missing";
        }

        if (!status.Paired || string.IsNullOrWhiteSpace(status.AgentId))
        {
            return "unpaired";
        }

        if (!string.Equals(NormalizeServer(status.Server), NormalizeServer(expectedServer), StringComparison.OrdinalIgnoreCase))
        {
            return "other-environment";
        }

        return "ready";
    }

    public static int CompareVersions(string? actual, string? expected)
    {
        if (!Version.TryParse(expected, out var wanted))
        {
            return 0;
        }

        Version.TryParse(actual, out var found);
        found ??= new Version(0, 0);
        return found.CompareTo(wanted);
    }

    public static bool IsSafeZipEntry(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains(':'))
        {
            return false;
        }

        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(part => part != "." && part != "..");
    }

    public static string NormalizeServer(string? server)
    {
        var value = (server ?? "").Trim().TrimEnd('/');
        return value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? value
            : value;
    }
}

public sealed class BranchAgentLocalEndpoint : IAsyncDisposable
{
    public const int DefaultPort = 47331;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly int _port;
    private readonly Func<LocalAgentStatus> _status;
    private readonly Func<LocalPairRequest, CancellationToken, Task<LocalAgentStatus>> _pair;
    private readonly CancellationTokenSource _stop = new();
    private HttpListener? _listener;
    private Task? _loop;

    public BranchAgentLocalEndpoint(
        Func<LocalAgentStatus> status,
        Func<LocalPairRequest, CancellationToken, Task<LocalAgentStatus>> pair,
        int port = DefaultPort)
    {
        _status = status;
        _pair = pair;
        _port = port;
    }

    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _loop = Task.Run(() => ListenAsync(_stop.Token));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || _listener is null || !_listener.IsListening)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var origin = context.Request.Headers["Origin"];
            ApplyCors(context.Response, origin);
            if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && path is "/" or "/status")
            {
                await WriteAsync(context.Response, 200, _status());
                return;
            }

            if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) && path == "/pair")
            {
                var request = await JsonSerializer.DeserializeAsync<LocalPairRequest>(context.Request.InputStream, Json, cancellationToken);
                var status = await _pair(request ?? new LocalPairRequest(null, null, false), cancellationToken);
                await WriteAsync(context.Response, 200, status);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (BranchAgentTransferRequiredException exception)
        {
            await WriteAsync(context.Response, 409, new { message = exception.Message });
        }
        catch (BranchAgentRejectedException exception)
        {
            await WriteAsync(context.Response, 400, new { message = exception.Message });
        }
        catch (Exception)
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch (Exception)
            {
                // The browser probe treats a closed socket as not installed.
            }
        }
    }

    private static void ApplyCors(HttpListenerResponse response, string? origin)
    {
        if (IsAllowedOrigin(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin!;
            response.Headers["Vary"] = "Origin";
        }

        response.Headers["Access-Control-Allow-Private-Network"] = "true";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Cache-Control"] = "no-store";
    }

    public static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Host.Equals("test-pos.sharbelfashion.com", StringComparison.OrdinalIgnoreCase) && uri.Scheme == "https")
        {
            return true;
        }

        return uri.Host is "127.0.0.1" or "localhost"
            && uri.Port is 5173 or 5080 or BranchAgentHostCommand.LocalAppPort;
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, object body)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener?.Stop();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // The listener is closing.
            }
        }

        _listener?.Close();
        _stop.Dispose();
    }
}
