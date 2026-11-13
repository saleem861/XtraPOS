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

    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(10);

    public async Task StreamAsync(HttpContext http, CreateBranchRequest request, Guid agentId, CancellationToken cancellationToken)
    {
        http.Response.Headers["X-Accel-Buffering"] = "no";
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

        await WriteAsync(http, SetupEvent.Progress(SetupSteps.CheckServer, "started"), cancellationToken);

        var seen = 0;
        var claimDeadline = DateTimeOffset.UtcNow.Add(ClaimTimeout);
        var lastWrite = DateTimeOffset.UtcNow;
        var deadline = job.ExpiresAt.AddSeconds(2);
        try
        {
            while (DateTimeOffset.UtcNow <= deadline && !cancellationToken.IsCancellationRequested)
            {
                foreach (var progress in registry.ReadEvents(job.Id, seen))
                {
                    seen++;
                    await WriteAsync(http, ToEvent(progress), cancellationToken);
                    lastWrite = DateTimeOffset.UtcNow;
                }

                if (registry.IsTerminal(job.Id))
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                if (!registry.IsClaimed(job.Id) && now >= claimDeadline)
                {
                    registry.CancelUnclaimed(job.Id, "خدمة XtraPOS على جهاز الفرع لم تستجب للطلب. تأكد أن الخدمة تعمل ثم أعد المحاولة.");
                    foreach (var progress in registry.ReadEvents(job.Id, seen))
                    {
                        seen++;
                        await WriteAsync(http, ToEvent(progress), cancellationToken);
                    }
                    return;
                }

                if (now - lastWrite >= KeepaliveInterval)
                {
                    await WriteAsync(http, new SetupEvent { Type = "keepalive" }, cancellationToken);
                    lastWrite = DateTimeOffset.UtcNow;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            }

            if (!registry.IsTerminal(job.Id) && !cancellationToken.IsCancellationRequested)
            {
                await WriteAsync(http, SetupEvent.Failed("جهاز الفرع لم يكمل التجهيز خلال المهلة."), cancellationToken);
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                registry.CancelUnclaimed(job.Id, "انقطع الاتصال قبل استلام الطلب.");
            }
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
