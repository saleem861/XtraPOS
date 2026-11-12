using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtraPOS.Api.Setup;

public sealed class BranchAgentRelay(BranchAgentRegistry registry)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task StreamAsync(HttpContext http, CreateBranchRequest request, Guid agentId, CancellationToken cancellationToken)
    {
        ClaimedBranchJob job;
        try
        {
            var instance = string.IsNullOrWhiteSpace(request.BranchServer) ? request.BranchInstance : request.BranchServer;
            job = registry.Approve(
                agentId,
                request.BranchDatabaseName,
                string.IsNullOrWhiteSpace(instance) ? "localhost" : instance,
                JsonSerializer.Serialize(request, Json),
                DateTimeOffset.UtcNow);
        }
        catch (BranchAgentRejectedException exception)
        {
            await WriteAsync(http, SetupEvent.Failed(exception.Message), cancellationToken);
            return;
        }

        var seen = 0;
        var deadline = job.ExpiresAt.AddSeconds(2);
        while (DateTimeOffset.UtcNow <= deadline && !cancellationToken.IsCancellationRequested)
        {
            foreach (var progress in registry.ReadEvents(job.Id, seen))
            {
                seen++;
                await WriteAsync(http, ToEvent(progress), cancellationToken);
            }

            if (registry.IsTerminal(job.Id))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        if (!registry.IsTerminal(job.Id))
        {
            await WriteAsync(http, SetupEvent.Failed("جهاز الفرع لم يكمل التجهيز خلال المهلة."), cancellationToken);
        }
    }

    private static SetupEvent ToEvent(BranchAgentProgress progress)
    {
        if (progress.Type == "failed")
        {
            return SetupEvent.Failed(progress.Message ?? "تعذر تجهيز قاعدة الفرع.");
        }

        if (progress.Type == "completed")
        {
            return new SetupEvent
            {
                Type = "completed",
                Message = progress.Message,
                RemoteAgent = true,
            };
        }

        return SetupEvent.Progress(progress.Step ?? "", progress.State ?? "started");
    }

    private static async Task WriteAsync(HttpContext http, SetupEvent setupEvent, CancellationToken cancellationToken)
    {
        await http.Response.WriteAsync(JsonSerializer.Serialize(setupEvent, Json) + "\n", cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
    }
}
