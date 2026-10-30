namespace XtraPOS.Api.Setup;

public static class CentralRegistrationResult
{
    public const int Created = 1;
    public const int ExactRetry = 2;

    public static bool IsVerifiedSuccess(int? code) => code is Created or ExactRetry;
}
