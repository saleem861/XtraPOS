using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed class BranchDatabaseProvisioner(IConfiguration configuration, IHostEnvironment environment, SqlConnectionFactory connections)
{
    public async Task EnsureAsync(string databaseName, CancellationToken cancellationToken)
    {
        var rejected = BranchProvisionPolicy.RejectDatabaseName(databaseName, connections.ConfiguredDatabase);
        if (rejected is not null)
        {
            throw new SetupRejectedException(rejected);
        }

        var problem = BranchProvisionPolicy.ConfigurationProblem(configuration);
        if (problem is not null)
        {
            SetupLog.Write(environment.ContentRootPath, environment.EnvironmentName, "provision-database", problem);
            throw new SetupRejectedException(problem);
        }

        var settings = Settings();
        var runtimeUser = configuration["SqlServer:User"] ?? "";
        if (!BranchDatabase.IsSafeIdentifier(runtimeUser))
        {
            throw new SetupRejectedException("حساب التشغيل غير صالح لربطه بقاعدة الفرع الجديدة.");
        }

        await using var master = new SqlConnection(settings.BuildConnectionString("master"));
        await master.OpenAsync(cancellationToken);
        await using var exists = new SqlCommand(
            """
            SELECT SUSER_SNAME(owner_sid)
            FROM sys.databases
            WHERE name = @name;
            """,
            master);
        exists.Parameters.AddWithValue("@name", databaseName);
        var owner = await exists.ExecuteScalarAsync(cancellationToken) as string;
        if (owner is not null)
        {
            await using var current = new SqlCommand("SELECT SUSER_SNAME();", master);
            var login = await current.ExecuteScalarAsync(cancellationToken) as string;
            if (!string.Equals(owner, login, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupRejectedException("قاعدة بهذا الاسم موجودة وليست ملك حساب التجهيز. لم يتم تعديلها.");
            }

            SetupLog.Write(environment.ContentRootPath, environment.EnvironmentName, "provision-database", "already-owned " + databaseName);
        }
        else
        {
            try
            {
                await using var create = new SqlCommand($"CREATE DATABASE [{databaseName}] COLLATE {BranchDatabase.Collation};", master);
                create.CommandTimeout = 60;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException exception) when (exception.Number is 262 or 15247)
            {
                var id = SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "provision-database", exception);
                throw new SetupRejectedException($"{BranchProvisionPolicy.MissingProvisionerMessage} {id}");
            }
            catch (SqlException exception) when (exception.Number == 1801)
            {
                var id = SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "provision-database", exception);
                throw new SetupRejectedException($"قاعدة بهذا الاسم موجودة ولم تُنشأ في هذه العملية. لم يتم تعديلها. {id}");
            }
        }

        await using var map = new SqlConnection(settings.BuildConnectionString(databaseName));
        await map.OpenAsync(cancellationToken);
        await using var user = new SqlCommand(
            $"""
            IF DATABASE_PRINCIPAL_ID(@runtime) IS NULL
                CREATE USER [{runtimeUser}] FOR LOGIN [{runtimeUser}];
            ALTER ROLE db_owner ADD MEMBER [{runtimeUser}];
            """,
            map);
        user.Parameters.AddWithValue("@runtime", runtimeUser);
        await user.ExecuteNonQueryAsync(cancellationToken);
        SetupLog.Write(environment.ContentRootPath, environment.EnvironmentName, "provision-database", "created " + databaseName);
    }

    public async Task RegisterCentralBranchAsync(
        Guid branchId,
        Guid businessId,
        string branchCode,
        string branchName,
        Guid centralAdminUserId,
        CancellationToken cancellationToken)
    {
        var problem = BranchProvisionPolicy.ConfigurationProblem(configuration);
        if (problem is not null)
        {
            throw new SetupRejectedException(BranchProvisionPolicy.MissingRegistrarMessage);
        }

        var settings = Settings() with { DatabaseName = InitialSyncGuard.ApprovedExistingCentral, DatabaseRole = DatabaseRole.Central };
        try
        {
            await using var connection = new SqlConnection(settings.BuildConnectionString(InitialSyncGuard.ApprovedExistingCentral));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                """
                EXEC dbo.RegisterProvisionedBranch
                    @id, @business, @code, @name, @admin, @result OUTPUT;
                """,
                connection);
            command.Parameters.AddWithValue("@id", branchId);
            command.Parameters.AddWithValue("@business", businessId);
            command.Parameters.AddWithValue("@code", branchCode);
            command.Parameters.AddWithValue("@name", branchName);
            command.Parameters.AddWithValue("@admin", centralAdminUserId);
            var result = command.Parameters.Add("@result", System.Data.SqlDbType.Int);
            result.Direction = System.Data.ParameterDirection.Output;
            await command.ExecuteNonQueryAsync(cancellationToken);
            var code = result.Value is int value ? value : 0;
            if (!CentralRegistrationResult.IsVerifiedSuccess(code))
            {
                throw new SetupRejectedException("تسجيل الفرع مركزياً لم يُؤكد. لم يُعلَّم الربط ناجحاً.");
            }

            SetupLog.Write(environment.ContentRootPath, environment.EnvironmentName, "register-central-branch", "verified " + code + " " + branchCode);
        }
        catch (SqlException exception) when (exception.Number is 50002 or 50003 or 50004 or 50005)
        {
            var id = SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "register-central-branch", exception);
            throw new SetupRejectedException($"رفض تسجيل الفرع مركزياً لأن الهوية أو المدير أو المنشأة غير مطابقة. لم يُعلَّم الربط ناجحاً. {id}");
        }
        catch (SqlException exception) when (exception.Number is 229 or 2812 or 916)
        {
            var id = SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "register-central-branch", exception);
            throw new SetupRejectedException($"{BranchProvisionPolicy.MissingRegistrarMessage} {id}");
        }
    }

    private SqlEndpointSettings Settings()
    {
        return new SqlEndpointSettings(
            DatabaseRole.Branch,
            SqlEndpointSettings.Remote,
            configuration[$"{BranchProvisionPolicy.SectionName}:Server"]!.Trim(),
            null,
            null,
            "master",
            SqlEndpointSettings.SqlLogin,
            configuration[$"{BranchProvisionPolicy.SectionName}:User"],
            configuration[$"{BranchProvisionPolicy.SectionName}:Password"],
            true,
            true);
    }
}
