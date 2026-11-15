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
