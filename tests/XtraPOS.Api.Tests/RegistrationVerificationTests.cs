using Xunit;
using XtraPOS.Api.Customers;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Tests;

public class RegistrationVerificationTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Save_without_verification_is_rejected_for_a_new_customer()
    {
        Assert.True(RegistrationVerification.SaveRequiresChallenge("972"));
        Assert.False(RegistrationVerification.SaveRequiresChallenge(null));
    }

    [Fact]
    public void Verified_unused_challenge_authorizes_only_the_same_destination()
    {
        Assert.Null(RegistrationVerification.Rejection("Verified", null, "+972592594707", "+972592594707", Now, Now.AddMinutes(1)));
        Assert.Equal("رقم الهاتف لا يطابق التحقق.", RegistrationVerification.Rejection("Verified", null, "+972592594707", "+970592594707", Now, Now.AddMinutes(1)));
    }

    [Fact]
    public void Pending_or_consumed_challenge_cannot_create_a_customer()
    {
        Assert.Equal("يجب التحقق من رقم الهاتف قبل حفظ الزبون.", RegistrationVerification.Rejection("Pending", null, "+972592594707", "+972592594707", null, Now));
        Assert.Equal("طلب التحقق مستخدم بالفعل.", RegistrationVerification.Rejection("Verified", Guid.NewGuid(), "+972592594707", "+972592594707", Now, Now));
    }

    [Theory]
    [InlineData("1234", null)]
    [InlineData("123", "رمز التحقق يجب أن يكون 4 أرقام.")]
    [InlineData("12345", "رمز التحقق يجب أن يكون 4 أرقام.")]
    [InlineData("123456", "رمز التحقق يجب أن يكون 4 أرقام.")]
    public void Otp_must_be_exactly_four_digits(string code, string? error)
    {
        Assert.Equal(4, PhoneVerificationCode.Length);
        Assert.Equal(error, PhoneVerificationCode.Rejection(code));
        Assert.Equal(4, OtpHasher.CreateCode().Length);
    }

    [Fact]
    public void Branch_conflict_keeps_any_dependent_customer()
    {
        Assert.False(BranchCustomerReplacement.CanReplace(new Dictionary<string, int> { ["Sales"] = 0, ["SaleItems"] = 0, ["CustomerDebts"] = 1 }));
        Assert.False(BranchCustomerReplacement.CanReplace(new Dictionary<string, int> { ["Sales"] = 1, ["CustomerAdministrationAudit"] = 1 }));
        Assert.True(BranchCustomerReplacement.CanReplace(new Dictionary<string, int> { ["Sales"] = 0, ["SaleItems"] = 0, ["CustomerAdministrationAudit"] = 1, ["CustomerPhoneVerificationChallenges"] = 1 }));
    }
}
