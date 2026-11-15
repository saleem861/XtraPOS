using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;

namespace XtraPOS.Api.Membership;

public sealed record MembershipRelayAttempt(Guid AttemptId, string Nonce, string Verifier);

public sealed class MembershipRelay
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly object _gate = new();
    private readonly string _path;
    private RelayDocument _document;

    public MembershipRelay(IHostEnvironment environment)
        : this(Path.Combine(environment.ContentRootPath, "App_Data"))
    {
    }

    public MembershipRelay(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "membership-phone-relay.json");
        _document = Load();
    }

    public void Publish(SharedEnrollmentPublish publish, DateTime utcNow)
    {
        if (!MembershipLinks.IsEnrollmentToken(publish.Token)) throw new MembershipException("MEMBERSHIP_REQUEST_NOT_FOUND");
        var expires = DateTime.SpecifyKind(publish.ExpiresAtUtc, DateTimeKind.Utc);
        if (expires <= utcNow || expires > utcNow.AddMinutes(15)) throw new MembershipException("MEMBERSHIP_QR_EXPIRED");
        lock (_gate)
        {
            Purge(utcNow);
            var hash = Key(publish.Token!);
            var entry = _document.Entries.FirstOrDefault(item => item.TokenHash == hash);
            if (entry is null)
            {
                entry = new RelayEntry { TokenHash = hash };
                _document.Entries.Add(entry);
            }

            entry.EnrollmentId = publish.EnrollmentId;
            entry.BusinessId = publish.BusinessId;
            entry.CustomerId = publish.CustomerId is Guid customer && customer != Guid.Empty ? customer : null;
            entry.CashierUserId = publish.CashierUserId;
            entry.ExpiresAtUtc = expires;
            Save();
        }
    }

    public bool KnowsEnrollment(string? token, DateTime utcNow)
    {
        if (!MembershipLinks.IsEnrollmentToken(token)) return false;
        lock (_gate)
        {
            var entry = Find(token!);
            return entry is not null && entry.ExpiresAtUtc > utcNow;
        }
    }

    public SharedEnrollmentView? Read(string token, DateTime utcNow)
    {
        if (!MembershipLinks.IsEnrollmentToken(token)) return null;
        lock (_gate)
        {
            var entry = Find(token);
            if (entry is null) return null;
            var status = entry.ExpiresAtUtc <= utcNow ? "Expired" : "Pending";
            Guid? attemptId = null;
            DateTime? attemptExpires = null;
            if (status != "Expired"
                && entry.AttemptStatus == "Authenticated"
                && entry.AttemptId is Guid id
                && entry.AttemptExpiresAtUtc is DateTime expires
                && expires > utcNow
                && !string.IsNullOrWhiteSpace(entry.Subject))
            {
                attemptId = id;
                attemptExpires = expires;
            }

            return new SharedEnrollmentView(status, entry.Provider, entry.Display, entry.Subject, entry.Email, attemptId, attemptExpires);
        }
    }

    public void StartAttempt(string enrollmentToken, Guid attemptId, string provider, string state, string nonce, string verifier, DateTime utcNow)
    {
        lock (_gate)
        {
            var entry = Find(enrollmentToken) ?? throw new MembershipException("MEMBERSHIP_REQUEST_NOT_FOUND");
            if (entry.ExpiresAtUtc <= utcNow) throw new MembershipException("MEMBERSHIP_QR_EXPIRED");
            entry.Provider = provider;
            entry.AttemptId = attemptId;
            entry.AttemptStatus = "Pending";
            entry.AttemptExpiresAtUtc = utcNow.AddMinutes(10);
            entry.StateHash = Key(state);
            entry.Nonce = nonce;
            entry.Verifier = verifier;
            entry.Subject = null;
            entry.Email = null;
            entry.Display = null;
            Save();
        }
    }

    public void TouchCounter(string token, DateTime utcNow)
    {
        if (!MembershipLinks.IsEnrollmentToken(token)) throw new MembershipException("MEMBERSHIP_REQUEST_NOT_FOUND");
        lock (_gate)
        {
            Purge(utcNow);
            var entry = FindCounter(token);
            if (entry is null)
            {
                entry = new CounterEntry { TokenHash = Key(token) };
                _document.Counters.Add(entry);
            }

            entry.SeenAtUtc = utcNow;
            Save();
        }
    }

    public bool KnowsFreshCounter(string? token, DateTime utcNow)
    {
        if (!MembershipLinks.IsEnrollmentToken(token)) return false;
        lock (_gate)
        {
            var entry = FindCounter(token!);
            return entry is not null && Fresh(entry, utcNow);
        }
    }

    public void StartCounterAttempt(string token, Guid attemptId, string provider, string state, string nonce, string verifier, DateTime utcNow)
    {
        lock (_gate)
        {
            var entry = FindCounter(token) ?? throw new MembershipException("MEMBERSHIP_REQUEST_NOT_FOUND");
            if (!Fresh(entry, utcNow)) throw new MembershipException("MEMBERSHIP_COUNTER_IDLE");
            entry.Provider = provider;
            entry.AttemptId = attemptId;
            entry.AttemptStatus = "Pending";
            entry.AttemptExpiresAtUtc = utcNow.AddMinutes(10);
            entry.StateHash = Key(state);
            entry.Nonce = nonce;
            entry.Verifier = verifier;
            entry.Subject = null;
            entry.Email = null;
            entry.Display = null;
            entry.ResultStatus = null;
            Save();
        }
    }

    public SharedCounterView? ReadCounter(string token, DateTime utcNow)
    {
        if (!MembershipLinks.IsEnrollmentToken(token)) return null;
        lock (_gate)
        {
            var entry = FindCounter(token);
            if (entry is null) return null;
            if (entry.ResultStatus is "Verified" or "Unlinked" && !string.IsNullOrWhiteSpace(entry.Subject))
            {
                return new SharedCounterView(entry.ResultStatus, entry.Provider, entry.Subject, entry.Email, entry.Display);
            }

            return Fresh(entry, utcNow)
                ? new SharedCounterView("Pending", null, null, null, null)
                : new SharedCounterView("Expired", null, null, null, null);
        }
    }

    public void ClearCounterResult(string token, DateTime utcNow)
    {
        if (!MembershipLinks.IsEnrollmentToken(token)) return;
        lock (_gate)
        {
            var entry = FindCounter(token);
            if (entry is null) return;
            entry.ResultStatus = null;
            entry.Subject = null;
            entry.Email = null;
            entry.Display = null;
            entry.Provider = null;
            entry.AttemptId = null;
            entry.AttemptStatus = null;
            entry.AttemptExpiresAtUtc = null;
            entry.StateHash = null;
            entry.Nonce = null;
            entry.Verifier = null;
            entry.SeenAtUtc = utcNow;
            Save();
        }
    }

    public MembershipRelayAttempt? FindPending(string provider, string state, DateTime utcNow)
    {
        lock (_gate)
        {
            var hash = Key(state);
            var entry = _document.Entries.FirstOrDefault(item => Pending(item.Provider, item.AttemptStatus, item.AttemptId, item.AttemptExpiresAtUtc, item.StateHash, item.Nonce, item.Verifier, provider, hash, utcNow));
            if (entry is not null) return new MembershipRelayAttempt(entry.AttemptId!.Value, entry.Nonce!, entry.Verifier!);
            var counter = _document.Counters.FirstOrDefault(item => Pending(item.Provider, item.AttemptStatus, item.AttemptId, item.AttemptExpiresAtUtc, item.StateHash, item.Nonce, item.Verifier, provider, hash, utcNow));
            return counter is null ? null : new MembershipRelayAttempt(counter.AttemptId!.Value, counter.Nonce!, counter.Verifier!);
        }
    }

    public void Complete(Guid attemptId, string provider, string subject, string? email, string? display, DateTime utcNow)
    {
        lock (_gate)
        {
            var entry = _document.Entries.FirstOrDefault(item => item.AttemptId == attemptId && item.AttemptStatus == "Pending");
            if (entry is not null)
            {
                if (entry.ExpiresAtUtc <= utcNow) throw new MembershipException("MEMBERSHIP_PROVIDER_STATE");
                entry.Provider = provider;
                entry.Subject = subject;
                entry.Email = string.IsNullOrWhiteSpace(email) ? null : email;
                entry.Display = string.IsNullOrWhiteSpace(display) ? subject : display;
                entry.AttemptStatus = "Authenticated";
                entry.AttemptExpiresAtUtc = utcNow.AddMinutes(15);
                entry.Nonce = null;
                entry.Verifier = null;
                Save();
                return;
            }

            var counter = _document.Counters.FirstOrDefault(item => item.AttemptId == attemptId && item.AttemptStatus == "Pending");
            if (counter is null || counter.AttemptExpiresAtUtc <= utcNow) throw new MembershipException("MEMBERSHIP_PROVIDER_STATE");
            counter.Provider = provider;
            counter.Subject = subject;
            counter.Email = string.IsNullOrWhiteSpace(email) ? null : email;
            counter.Display = string.IsNullOrWhiteSpace(display) ? subject : display;
            counter.ResultStatus = "Verified";
            counter.AttemptStatus = "Authenticated";
            counter.AttemptExpiresAtUtc = utcNow.AddMinutes(15);
            counter.Nonce = null;
            counter.Verifier = null;
            Save();
        }
    }

    private static bool Pending(string? provider, string? status, Guid? attemptId, DateTime? expiresAt, string? stateHash, string? nonce, string? verifier, string expectedProvider, string expectedState, DateTime utcNow) =>
        stateHash == expectedState
        && string.Equals(provider, expectedProvider, StringComparison.Ordinal)
        && status == "Pending"
        && attemptId is not null
        && expiresAt > utcNow
        && !string.IsNullOrWhiteSpace(nonce)
        && !string.IsNullOrWhiteSpace(verifier);

    private static bool Fresh(CounterEntry entry, DateTime utcNow) => entry.SeenAtUtc > utcNow.AddMinutes(-2);

    private CounterEntry? FindCounter(string token) =>
        _document.Counters.FirstOrDefault(item => item.TokenHash == Key(token));

    private RelayEntry? Find(string token) =>
        _document.Entries.FirstOrDefault(item => item.TokenHash == Key(token));

    private static string Key(string value) => Convert.ToHexString(MembershipTokens.Hash(value));

    private void Purge(DateTime utcNow)
    {
        var cutoff = utcNow.AddHours(-1);
        _document.Entries.RemoveAll(item => item.ExpiresAtUtc < cutoff);
        var counterCutoff = utcNow.AddDays(-1);
        _document.Counters.RemoveAll(item => item.SeenAtUtc < counterCutoff && item.AttemptStatus != "Pending");
    }

    private RelayDocument Load()
    {
        try
        {
            if (!File.Exists(_path)) return new RelayDocument();
            var document = JsonSerializer.Deserialize<RelayDocument>(File.ReadAllText(_path), Json) ?? new RelayDocument();
            document.Entries ??= [];
            document.Counters ??= [];
            return document;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new RelayDocument();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_document, Json);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, true);
    }

    private sealed class RelayDocument
    {
        public List<RelayEntry> Entries { get; set; } = [];
        public List<CounterEntry> Counters { get; set; } = [];
    }

    private sealed class CounterEntry
    {
        public string TokenHash { get; set; } = "";
        public DateTime SeenAtUtc { get; set; }
        public string? ResultStatus { get; set; }
        public string? Provider { get; set; }
        public string? Subject { get; set; }
        public string? Email { get; set; }
        public string? Display { get; set; }
        public Guid? AttemptId { get; set; }
        public string? AttemptStatus { get; set; }
        public DateTime? AttemptExpiresAtUtc { get; set; }
        public string? StateHash { get; set; }
        public string? Nonce { get; set; }
        public string? Verifier { get; set; }
    }

    private sealed class RelayEntry
    {
        public Guid EnrollmentId { get; set; }
        public Guid BusinessId { get; set; }
        public Guid? CustomerId { get; set; }
        public Guid CashierUserId { get; set; }
        public string TokenHash { get; set; } = "";
        public DateTime ExpiresAtUtc { get; set; }
        public string? Provider { get; set; }
        public string? Subject { get; set; }
        public string? Email { get; set; }
        public string? Display { get; set; }
        public Guid? AttemptId { get; set; }
        public string? AttemptStatus { get; set; }
        public DateTime? AttemptExpiresAtUtc { get; set; }
        public string? StateHash { get; set; }
        public string? Nonce { get; set; }
        public string? Verifier { get; set; }
    }
}
