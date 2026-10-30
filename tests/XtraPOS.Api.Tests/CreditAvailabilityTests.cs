using Xunit;
using XtraPOS.Api.Credit;

namespace XtraPOS.Api.Tests;

public class CreditAvailabilityTests
{
    [Fact]
    public void Available_subtracts_debt_and_reservations()
    {
        Assert.Equal(400m, CreditAvailability.Available(1000m, 600m, 0m));
        Assert.Equal(100m, CreditAvailability.Available(1000m, 600m, 300m));
        Assert.Equal(0m, CreditAvailability.Available(1000m, 900m, 300m));
    }

    [Fact]
    public void Second_concurrent_request_is_rejected_after_the_first_reservation()
    {
        var first = CreditAvailability.Decide(true, "Active", 1000m, 600m, 0m, 300m);
        var reserved = 300m;
        var second = CreditAvailability.Decide(true, "Active", 1000m, 600m, reserved, 300m);
        Assert.Equal(CreditDecision.Approved, first);
        Assert.Equal(CreditDecision.LimitExceeded, second);
    }

    [Fact]
    public void Covers_limit_edges_and_ignores_expired_holds()
    {
        Assert.Equal(1000m, CreditAvailability.Available(1000m, 0m, 0m));
        Assert.Equal(400m, CreditAvailability.Available(1000m, 600m, 0m));
        Assert.Equal(0m, CreditAvailability.Available(1000m, 1000m, 0m));
        Assert.Equal(0m, CreditAvailability.Available(500m, 800m, 0m));
        Assert.Equal(CreditDecision.LimitExceeded, CreditAvailability.Decide(true, "Active", 500m, 800m, 0m, 1m));
        Assert.Equal(CreditDecision.CreditDisabled, CreditAvailability.Decide(true, "Active", 0m, 0m, 0m, 1m));
        var now = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);
        Assert.False(CreditAvailability.HoldsCredit("Authorized", now.AddMinutes(-1), now));
        Assert.True(CreditAvailability.HoldsCredit("Authorized", now.AddMinutes(5), now));
        Assert.False(CreditAvailability.HoldsCredit("Rejected", now.AddMinutes(5), now));
        Assert.False(CreditAvailability.HoldsCredit("Expired", now.AddMinutes(5), now));
    }

    [Fact]
    public void Defaults_block_credit()
    {
        Assert.Equal(CreditDecision.CreditDisabled, CreditAvailability.Decide(false, "Inactive", 0m, 0m, 0m, 10m));
        Assert.Equal(CreditDecision.AccountInactive, CreditAvailability.Decide(true, "Suspended", 500m, 0m, 0m, 10m));
        Assert.Equal(CreditDecision.InvalidAmount, CreditAvailability.Decide(true, "Active", 500m, 0m, 0m, 0m));
    }
}
