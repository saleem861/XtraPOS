using System.Net.Http.Headers;

namespace XtraPOS.Api.Setup;

public static class BranchAgentChannel
{
    public static HttpClient Open(string server)
    {
        var target = BranchAgentLocalDecision.NormalizeServer(server);
        return new HttpClient
        {
            BaseAddress = new Uri(target + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public static HttpClient Retarget(string server, string token)
    {
        var next = Open(server);
        next.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return next;
    }
}
