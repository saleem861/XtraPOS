using XtraPOS.Api.Membership;
using Xunit;

namespace XtraPOS.Api.Tests;

public class MembershipRelayTests
{
    [Fact]
    public void PhoneHandoffStaysOutOfTheBusinessDatabase()
    {
        using var directory = new TempDirectory();
        var relay = new MembershipRelay(directory.Path);
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var token = MembershipTokens.CreateOpaqueToken();
        var publish = new SharedEnrollmentPublish
        {
            EnrollmentId = Guid.NewGuid(),
            BusinessId = Guid.NewGuid(),
            CashierUserId = Guid.NewGuid(),
            Token = token,
            ExpiresAtUtc = now.AddMinutes(2),
        };

        relay.Publish(publish, now);

        var pending = relay.Read(token, now);
        Assert.NotNull(pending);
        Assert.Equal("Pending", pending.Status);
        Assert.Null(pending.AttemptId);
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(directory.Path, "membership-phone-relay.json")), StringComparison.Ordinal);

        var state = token + "." + MembershipTokens.CreateOpaqueToken();
        var attemptId = Guid.NewGuid();
        relay.StartAttempt(token, attemptId, "Google", state, "nonce", "verifier", now);
        var found = relay.FindPending("Google", state, now);
        Assert.Equal(attemptId, found?.AttemptId);

        relay.Complete(attemptId, "Google", "subject-1", "person@example.com", "person@example.com", now);
        var ready = relay.Read(token, now);
        Assert.Equal("subject-1", ready?.Subject);
        Assert.Equal(attemptId, ready?.AttemptId);
        Assert.Null(relay.FindPending("Google", state, now));
        Assert.DoesNotContain("verifier", File.ReadAllText(Path.Combine(directory.Path, "membership-phone-relay.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiredHandoffIsRejected()
    {
        using var directory = new TempDirectory();
        var relay = new MembershipRelay(directory.Path);
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var publish = new SharedEnrollmentPublish
        {
            EnrollmentId = Guid.NewGuid(),
            BusinessId = Guid.NewGuid(),
            CashierUserId = Guid.NewGuid(),
            Token = MembershipTokens.CreateOpaqueToken(),
            ExpiresAtUtc = now.AddMinutes(-1),
        };

        var error = Assert.Throws<MembershipException>(() => relay.Publish(publish, now));
        Assert.Equal("MEMBERSHIP_QR_EXPIRED", error.Code);
        Assert.Null(relay.Read(publish.Token!, now));
    }

    [Fact]
    public void CounterHeartbeatReachesThePhoneWithoutTheBusinessDatabase()
    {
        using var directory = new TempDirectory();
        var relay = new MembershipRelay(directory.Path);
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var token = MembershipTokens.CreateOpaqueToken();

        relay.TouchCounter(token, now);

        var pending = relay.ReadCounter(token, now);
        Assert.Equal("Pending", pending?.Status);
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(directory.Path, "membership-phone-relay.json")), StringComparison.Ordinal);
        Assert.Equal("Expired", relay.ReadCounter(token, now.AddMinutes(3))?.Status);

        var state = MembershipTokens.CreateOpaqueToken() + "|/membership/counter/" + token;
        var attemptId = Guid.NewGuid();
        relay.StartCounterAttempt(token, attemptId, "Google", state, "nonce", "verifier", now);
        Assert.Equal(attemptId, relay.FindPending("Google", state, now)?.AttemptId);

        relay.Complete(attemptId, "Google", "subject-1", "person@example.com", "person@example.com", now);
        var recognized = relay.ReadCounter(token, now);
        Assert.Equal("Verified", recognized?.Status);
        Assert.Equal("subject-1", recognized?.Subject);
        Assert.Null(relay.FindPending("Google", state, now));
        Assert.DoesNotContain("verifier", File.ReadAllText(Path.Combine(directory.Path, "membership-phone-relay.json")), StringComparison.Ordinal);

        relay.ClearCounterResult(token, now);
        Assert.Equal("Pending", relay.ReadCounter(token, now)?.Status);
        Assert.Null(relay.ReadCounter(token, now)?.Subject);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xtrapos-relay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
