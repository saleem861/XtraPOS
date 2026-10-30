using Microsoft.Extensions.Configuration;
using XtraPOS.Api.Setup;
using Xunit;

namespace XtraPOS.Api.Tests;

public class BranchProvisionPolicyTests
{
    [Fact]
    public void Rejects_the_configured_branch_and_central_names()
    {
        Assert.NotNull(BranchProvisionPolicy.RejectDatabaseName("XtraPOS_Branch_Test_Staging", "XtraPOS_Branch_Test_Staging"));
        Assert.NotNull(BranchProvisionPolicy.RejectDatabaseName("XtraPOS_Central", "XtraPOS_Branch_Test_Staging"));
        Assert.NotNull(BranchProvisionPolicy.RejectDatabaseName("XtraPOS_Branch", "XtraPOS_Branch_Test_Staging"));
        Assert.Null(BranchProvisionPolicy.RejectDatabaseName("XtraPOS_Branch_Test_S29", "XtraPOS_Branch_Test_Staging"));
    }

    [Fact]
    public void Missing_provisioner_is_not_the_runtime_login()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.Equal(BranchProvisionPolicy.MissingProvisionerMessage, BranchProvisionPolicy.ConfigurationProblem(empty));

        var runtime = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SqlProvisioning:Server"] = "sql.example",
            ["SqlProvisioning:User"] = "xtrapos_staging",
            ["SqlProvisioning:Password"] = "secret",
            ["SqlServer:User"] = "xtrapos_staging",
        }).Build();
        Assert.Contains("مختلفاً", BranchProvisionPolicy.ConfigurationProblem(runtime));
        Assert.Contains("لا يُمنح dbcreator", BranchProvisionPolicy.MissingProvisionerMessage);
        Assert.Contains("CREATE ANY DATABASE", BranchProvisionPolicy.MissingProvisionerMessage);
        Assert.Contains("RegisterProvisionedBranch", BranchProvisionPolicy.MissingRegistrarMessage);
        Assert.Contains("EXECUTE", BranchProvisionPolicy.MissingRegistrarMessage);
    }

    [Fact]
    public void Registration_succeeds_only_for_a_verified_result()
    {
        Assert.False(CentralRegistrationResult.IsVerifiedSuccess(null));
        Assert.False(CentralRegistrationResult.IsVerifiedSuccess(0));
        Assert.True(CentralRegistrationResult.IsVerifiedSuccess(CentralRegistrationResult.Created));
        Assert.True(CentralRegistrationResult.IsVerifiedSuccess(CentralRegistrationResult.ExactRetry));
    }

    [Fact]
    public void Independent_onboarding_is_single_use_and_rate_limited()
    {
        IndependentOnboarding.ResetAttempts();
        var root = Path.Combine(Path.GetTempPath(), "xtrapos-onboarding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "App_Data"));
        var token = new string('a', 32);
        try
        {
            File.WriteAllText(IndependentOnboarding.TokenPath(root), token);
            Assert.False(IndependentOnboarding.TryConsume(root, "wrong-token-value-not-the-real-one", "client-a", out _));
            Assert.True(File.Exists(IndependentOnboarding.TokenPath(root)));
            Assert.True(IndependentOnboarding.TryConsume(root, token, "client-a", out _));
            Assert.False(File.Exists(IndependentOnboarding.TokenPath(root)));
            Assert.False(IndependentOnboarding.TryConsume(root, token, "client-b", out _));

            IndependentOnboarding.ResetAttempts();
            File.WriteAllText(IndependentOnboarding.TokenPath(root), token);
            var gate = new Barrier(2);
            var results = new int[2];
            var tasks = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            {
                gate.SignalAndWait();
                results[index] = IndependentOnboarding.TryConsume(root, token, "client-c", out _) ? 1 : 0;
            })).ToArray();
            Task.WaitAll(tasks);
            Assert.Equal(1, results.Sum());

            IndependentOnboarding.ResetAttempts();
            for (var attempt = 0; attempt < IndependentOnboarding.AttemptLimit; attempt++)
            {
                IndependentOnboarding.TryConsume(root, "", "client-d", out _);
            }

            Assert.False(IndependentOnboarding.TryConsume(root, token, "client-d", out var limited));
            Assert.Contains("إيقاف", limited);
        }
        finally
        {
            IndependentOnboarding.ResetAttempts();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
