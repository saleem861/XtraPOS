using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XtraPOS.Api.Setup;

public class BranchAgentRejectedException(string message) : Exception(message);

public sealed class BranchAgentTransferRequiredException(string message) : BranchAgentRejectedException(message);

public static class BranchAgentRules
{
    public const string ProvisionOperation = "provision-branch";
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan JobLifetime = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(45);

    public static bool TryNormalizeLocalInstance(string? value, out string normalized)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0
            || text.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || text == "."
            || text.Equals("(local)", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "localhost";
            return true;
        }

        var separator = text.IndexOf('\\');
        if (separator <= 0 || text.AsSpan(separator + 1).Contains('\\') || text.Contains(',') || text.Contains(';') || text.Contains('/'))
        {
            normalized = "";
            return false;
        }

        var host = text[..separator].Trim();
        var instance = text[(separator + 1)..].Trim();
        if (!IsLocalHost(host) || !IsInstanceName(instance))
        {
            normalized = "";
            return false;
        }

        normalized = "localhost\\" + instance;
        return true;
    }

    public static string SanitizeSetup(string? json, string databaseName, string instance)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }

        if (JsonNode.Parse(json) is not JsonObject node)
        {
            throw new BranchAgentRejectedException("طلب التجهيز غير صالح.");
        }

        foreach (var banned in new[] { "sql", "command", "script", "query", "statement" })
        {
            node.Remove(banned);
        }

        node["branchDatabaseName"] = databaseName;
        node["branchLocation"] = "Local";
        node["branchAuthentication"] = "Integrated";
        node["branchUsername"] = null;
        node["branchPassword"] = null;
        node["branchServer"] = instance == "localhost" ? "" : instance;
        node["branchInstance"] = null;
        node["branchPort"] = null;
        node["branchAgentId"] = null;
        return node.ToJsonString();
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "."
        || host.Equals("(local)", StringComparison.OrdinalIgnoreCase);

    private static bool IsInstanceName(string instance) =>
        instance.Length is > 0 and <= 16
        && instance.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}

public sealed record BranchComputerRecord(Guid Id, string Name, bool Online, IReadOnlyList<string> Instances);

public sealed record ClaimedBranchJob(
    Guid Id,
    Guid AgentId,
    string Operation,
    string DatabaseName,
    string Instance,
    string Nonce,
    string SetupJson,
    DateTimeOffset ExpiresAt);

public sealed record BranchAgentProgress(string Type, string? Step, string? State, string? Message, bool RemoteAgent);

public sealed class BranchAgentRegistry
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<PairingCode> _codes = [];
    private readonly List<AgentRecord> _agents = [];
    private readonly List<JobRecord> _jobs = [];
    private readonly HashSet<string> _spentCodes = new(StringComparer.Ordinal);

    public BranchAgentRegistry(IHostEnvironment environment)
        : this(Path.Combine(environment.ContentRootPath, "App_Data"))
    {
    }

    public BranchAgentRegistry(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "branch-agents.json");
        Load();
    }

    public (string Code, DateTimeOffset ExpiresAt) IssuePairingCode(DateTimeOffset now)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        lock (_gate)
        {
            _codes.Add(new PairingCode(Hash(code), now.Add(BranchAgentRules.PairingLifetime)));
        }

        return (code, now.Add(BranchAgentRules.PairingLifetime));
    }

    public (Guid AgentId, string Token) Pair(string? code, string? machineName, IReadOnlyList<string>? instances, DateTimeOffset now)
    {
        var name = (machineName ?? "").Trim();
        if (name.Length is < 1 or > 80)
        {
            throw new BranchAgentRejectedException("اسم جهاز الفرع غير صالح.");
        }

        var localInstances = NormalizeReported(instances);
        var hash = Hash((code ?? "").Trim());
        lock (_gate)
        {
            if (_spentCodes.Contains(hash))
            {
                throw new BranchAgentRejectedException("رمز الربط غير صالح أو انتهت صلاحيته.");
            }

            var pairing = _codes.FirstOrDefault(item => item.Hash == hash && item.ExpiresAt >= now);
            if (pairing is null)
            {
                throw new BranchAgentRejectedException("رمز الربط غير صالح أو انتهت صلاحيته.");
            }

            _codes.Remove(pairing);
            _spentCodes.Add(hash);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var agent = new AgentRecord(Guid.NewGuid(), name, Hash(token), localInstances, now);
            _agents.Add(agent);
            Save();
            Audit("paired " + agent.Id + " " + name);
            return (agent.Id, token);
        }
    }

    public void Heartbeat(string? token, IReadOnlyList<string>? instances, DateTimeOffset now)
    {
        var localInstances = NormalizeReported(instances);
        lock (_gate)
        {
            var agent = RequireAgent(token);
            agent.Instances = localInstances;
            agent.LastSeen = now;
        }
    }

    public IReadOnlyList<BranchComputerRecord> List(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _agents.Select(agent => new BranchComputerRecord(
                agent.Id,
                agent.Name,
                now - agent.LastSeen <= BranchAgentRules.OnlineWindow,
                agent.Instances.ToArray())).ToArray();
        }
    }

    public ClaimedBranchJob Approve(Guid agentId, string? databaseName, string? instance, string? setupJson, DateTimeOffset now)
    {
        if (!OperationalDatabaseGuard.IsProductionCatalog(databaseName))
        {
            throw new BranchAgentRejectedException("اسم قاعدة البيانات غير صالح.");
        }

        if (!BranchAgentRules.TryNormalizeLocalInstance(instance, out var normalized))
        {
            throw new BranchAgentRejectedException("نسخة SQL المحلية غير مسموحة.");
        }

        var setup = BranchAgentRules.SanitizeSetup(setupJson, databaseName!.Trim(), normalized);
        lock (_gate)
        {
            var agent = _agents.FirstOrDefault(item => item.Id == agentId);
            if (agent is null || now - agent.LastSeen > BranchAgentRules.OnlineWindow)
            {
                throw new BranchAgentRejectedException("جهاز الفرع غير متصل.");
            }

            if (!agent.Instances.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                throw new BranchAgentRejectedException("نسخة SQL غير مسجلة على جهاز الفرع.");
            }

            ExpireJobs(now);
            if (_jobs.Any(job => job.AgentId == agentId && !job.Terminal && job.ExpiresAt >= now))
            {
                throw new BranchAgentRejectedException("طلب تجهيز سابق ما زال قيد التنفيذ.");
            }

            var job = new JobRecord(
                Guid.NewGuid(),
                agentId,
                BranchAgentRules.ProvisionOperation,
                databaseName.Trim(),
                normalized,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                setup,
                now.Add(BranchAgentRules.JobLifetime));
            _jobs.Add(job);
            Audit("approved " + job.Id + " " + job.DatabaseName + " " + job.Instance);
            return ToClaim(job);
        }
    }

    public ClaimedBranchJob? Claim(string? token, DateTimeOffset now)
    {
        lock (_gate)
        {
            var agent = RequireAgent(token);
            agent.LastSeen = now;
            ExpireJobs(now);
            var job = _jobs.FirstOrDefault(item => item.AgentId == agent.Id && !item.Claimed && !item.Terminal && item.ExpiresAt >= now);
            if (job is null)
            {
                return null;
            }

            job.Claimed = true;
            Audit("claimed " + job.Id);
            return ToClaim(job);
        }
    }

    public void Report(string? token, Guid jobId, string? nonce, BranchAgentProgress progress, DateTimeOffset now)
    {
        if (progress.Type is not ("progress" or "failed" or "completed"))
        {
            throw new BranchAgentRejectedException("حالة التجهيز غير مسموحة.");
        }

        lock (_gate)
        {
            var agent = RequireAgent(token);
            var job = _jobs.FirstOrDefault(item => item.Id == jobId && item.AgentId == agent.Id);
            if (job is null || !job.Claimed || !string.Equals(job.Nonce, nonce, StringComparison.Ordinal))
            {
                throw new BranchAgentRejectedException("طلب التجهيز غير صالح أو أُعيد استخدامه.");
            }

            if (job.Terminal || job.ExpiresAt < now)
            {
                throw new BranchAgentRejectedException("طلب التجهيز انتهت صلاحيته أو اكتمل.");
            }

            job.Events.Add(progress);
            if (progress.Type is "failed" or "completed")
            {
                job.Terminal = true;
            }

            Audit(progress.Type + " " + job.Id + " " + progress.Step + " " + progress.State);
        }
    }

    public IReadOnlyList<BranchAgentProgress> ReadEvents(Guid jobId, int skip)
    {
        lock (_gate)
        {
            var job = _jobs.FirstOrDefault(item => item.Id == jobId);
            if (job is null || skip >= job.Events.Count)
            {
                return [];
            }

            return job.Events.Skip(skip).ToArray();
        }
    }

    public bool IsTerminal(Guid jobId)
    {
        lock (_gate)
        {
            return _jobs.FirstOrDefault(item => item.Id == jobId)?.Terminal == true;
        }
    }

    private AgentRecord RequireAgent(string? token)
    {
        var agent = _agents.FirstOrDefault(item => item.TokenHash == Hash((token ?? "").Trim()));
        if (agent is null)
        {
            throw new BranchAgentRejectedException("اعتماد جهاز الفرع غير صالح.");
        }

        return agent;
    }

    private void ExpireJobs(DateTimeOffset now)
    {
        foreach (var job in _jobs.Where(item => !item.Terminal && item.ExpiresAt < now))
        {
            job.Terminal = true;
        }
    }

    private static ClaimedBranchJob ToClaim(JobRecord job) =>
        new(job.Id, job.AgentId, job.Operation, job.DatabaseName, job.Instance, job.Nonce, job.SetupJson, job.ExpiresAt);

    private static List<string> NormalizeReported(IReadOnlyList<string>? instances)
    {
        var source = instances is null || instances.Count == 0 ? ["localhost"] : instances;
        var local = new List<string>();
        foreach (var instance in source)
        {
            if (!BranchAgentRules.TryNormalizeLocalInstance(instance, out var normalized) || local.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                if (!BranchAgentRules.TryNormalizeLocalInstance(instance, out _))
                {
                    throw new BranchAgentRejectedException("جهاز الفرع يقبل نسخ SQL المحلية فقط.");
                }

                continue;
            }

            local.Add(normalized);
        }

        return local;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        var stored = JsonSerializer.Deserialize<StoredAgents>(File.ReadAllText(_path), Json);
        if (stored is null)
        {
            return;
        }

        _agents.AddRange((stored.Agents ?? []).Select(agent => new AgentRecord(
            agent.Id,
            agent.Name,
            agent.TokenHash,
            (agent.Instances ?? []).ToList(),
            DateTimeOffset.MinValue)));
        foreach (var hash in stored.SpentCodes ?? [])
        {
            _spentCodes.Add(hash);
        }
    }

    private void Save()
    {
        var stored = new StoredAgents
        {
            Agents = _agents.Select(agent => new StoredAgent
            {
                Id = agent.Id,
                Name = agent.Name,
                TokenHash = agent.TokenHash,
                Instances = agent.Instances.ToArray(),
            }).ToArray(),
            SpentCodes = _spentCodes.ToArray(),
        };
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(stored, Json));
        File.Move(temporary, _path, overwrite: true);
    }

    private void Audit(string line)
    {
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(_path)!, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "branch-agent.log"), $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // The audit file must not block provisioning.
        }
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private sealed class PairingCode(string hash, DateTimeOffset expiresAt)
    {
        public string Hash { get; } = hash;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
    }

    private sealed class AgentRecord(Guid id, string name, string tokenHash, List<string> instances, DateTimeOffset lastSeen)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string TokenHash { get; } = tokenHash;
        public List<string> Instances { get; set; } = instances;
        public DateTimeOffset LastSeen { get; set; } = lastSeen;
    }

    private sealed class JobRecord(
        Guid id,
        Guid agentId,
        string operation,
        string databaseName,
        string instance,
        string nonce,
        string setupJson,
        DateTimeOffset expiresAt)
    {
        public Guid Id { get; } = id;
        public Guid AgentId { get; } = agentId;
        public string Operation { get; } = operation;
        public string DatabaseName { get; } = databaseName;
        public string Instance { get; } = instance;
        public string Nonce { get; } = nonce;
        public string SetupJson { get; } = setupJson;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool Claimed { get; set; }
        public bool Terminal { get; set; }
        public List<BranchAgentProgress> Events { get; } = [];
    }

    private sealed class StoredAgents
    {
        public StoredAgent[] Agents { get; set; } = [];
        public string[] SpentCodes { get; set; } = [];
    }

    private sealed class StoredAgent
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string TokenHash { get; set; } = "";
        public string[] Instances { get; set; } = [];
    }
}
