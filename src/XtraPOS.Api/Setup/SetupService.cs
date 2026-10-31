using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Devices;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed class SetupService(
    BranchDbContext db,
    SqlConnectionFactory connections,
    MigrationRunner migrations,
    SetupCoordinator coordinator,
    PasswordService passwords,
    DeviceService devices,
    CentralDatabaseService centralDatabases,
    SharedDataSync sharedData,
    SyncEndpointStore endpoints,
    LocalDeviceRecovery recovery,
    IHostEnvironment environment,
    IConfiguration configuration,
    ILogger<SetupService> logger)
{
    public static Func<CancellationToken, Task>? FailAfterTables { get; set; }
    public async Task<SetupStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (coordinator.IsConfiguring)
        {
            return new SetupStatusResponse { Status = SetupStatuses.Configuring };
        }

        var installationConfigured = InstallationState.IsConfigured(environment.ContentRootPath);
        if (!connections.IsSafeConfiguration(out var problem))
        {
            return new SetupStatusResponse
            {
                Status = installationConfigured
                    ? SetupStatuses.ConfigurationError
                    : SetupStatuses.NotConfigured,
                Message = installationConfigured ? problem : null,
            };
        }

        try
        {
            var state = await connections.GetDatabaseStateAsync(cancellationToken);
            if (state is null)
            {
                return new SetupStatusResponse
                {
                    Status = installationConfigured
                        ? SetupStatuses.DatabaseUnavailable
                        : SetupStatuses.NotConfigured,
                    Message = installationConfigured
                        ? "قاعدة البيانات غير متاحة. لن تُعاد عملية الإعداد الأول."
                        : null,
                };
            }

            if (!string.Equals(state.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                return new SetupStatusResponse
                {
                    Status = SetupStatuses.DatabaseUnavailable,
                    Message = "قاعدة البيانات غير جاهزة. أعد المحاولة.",
                };
            }

            if (!string.Equals(state.Collation, BranchDatabase.Collation, StringComparison.Ordinal))
            {
                return new SetupStatusResponse
                {
                    Status = SetupStatuses.ConfigurationError,
                    Message = "قاعدة البيانات الحالية غير متوافقة مع إعداد إكسترا.",
                };
            }

            var phase = await connections.GetInstallationPhaseAsync(cancellationToken);
            var status = phase switch
            {
                InstallationPhase.Configured => SetupStatuses.Configured,
                InstallationPhase.AdminMissing => SetupStatuses.ConfiguredButAdminMissing,
                _ => SetupStatuses.NotConfigured,
            };
            if (status is SetupStatuses.Configured or SetupStatuses.ConfiguredButAdminMissing)
            {
                try
                {
                    var current = await recovery.GetContextAsync(cancellationToken);
                    return new SetupStatusResponse
                    {
                        Status = status,
                        BusinessId = current.BusinessId,
                        BusinessName = current.BusinessName,
                        BranchId = current.BranchId,
                        BranchName = current.BranchName,
                        BranchCode = current.BranchCode,
                        Stations = current.Stations,
                    };
                }
                catch (SetupRejectedException exception)
                {
                    return new SetupStatusResponse { Status = status, Message = exception.Message };
                }
            }

            return new SetupStatusResponse { Status = status };
        }
        catch (SqlException exception) when (!installationConfigured && exception.Number == 208)
        {
            return new SetupStatusResponse { Status = SetupStatuses.NotConfigured };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Setup status check failed.");
            return new SetupStatusResponse
            {
                Status = SetupStatuses.DatabaseUnavailable,
                Message = "تعذر الاتصال بخادم البيانات. الإعداد الأول لن يبدأ بسبب انقطاع الاتصال.",
            };
        }
    }

    public async Task RunAsync(
        BranchSetup input,
        Func<SetupEvent, CancellationToken, Task> emit,
        CancellationToken cancellationToken,
        TopologyPlan? topology = null,
        bool replaceExisting = false)
    {
        // Enforce the same boundary for non-HTTP callers before any database access.
        if (replaceExisting)
        {
            await emit(SetupEvent.Failed(SetupAuthorization.ReplacementDeniedMessage), cancellationToken);
            return;
        }

        if (!coordinator.TryEnter())
        {
            await emit(SetupEvent.Failed("الإعداد قيد التنفيذ. أعد المحاولة بعد قليل."), cancellationToken);
            return;
        }

        try
        {
            await emit(SetupEvent.Progress(SetupSteps.CheckServer, "started"), cancellationToken);
            await CheckServerAsync(cancellationToken);
            await emit(SetupEvent.Progress(SetupSteps.CheckServer, "succeeded"), cancellationToken);

            if (await HasExistingBranchAsync(cancellationToken))
            {
                var existing = await LoadExistingAsync(cancellationToken);
                await emit(SetupEvent.Completed(ToResponse(existing), alreadyConfigured: true), cancellationToken);
                return;
            }

            await emit(SetupEvent.Progress(SetupSteps.CreateDatabase, "started"), cancellationToken);
            await connections.EnsureBranchDatabaseAsync(cancellationToken);
            await emit(SetupEvent.Progress(SetupSteps.CreateDatabase, "succeeded"), cancellationToken);

            await emit(SetupEvent.Progress(SetupSteps.CreateTables, "started"), cancellationToken);
            await migrations.ApplyPendingAsync(cancellationToken);
            var inspection = await migrations.InspectAsync(cancellationToken);
            if (!inspection.IsValid)
            {
                logger.LogError("Branch schema verification failed. {Diagnostic}", inspection.Diagnostic);
                throw new SetupRejectedException("تعذر تجهيز جداول الفرع.");
            }

            await emit(SetupEvent.Progress(SetupSteps.CreateTables, "succeeded"), cancellationToken);
            if (FailAfterTables is not null)
            {
                await FailAfterTables(cancellationToken);
            }

            CentralBusinessLink? linkedBusiness = null;
            if (topology?.LinkToCentral == true && topology.Central is not null
                && (!topology.CreateCentral || await CentralHasBusinessAsync(topology.Central, cancellationToken)))
            {
                if (!topology.CreateCentral)
                {
                    await centralDatabases.VerifyExistingAsync(topology.Central, cancellationToken);
                }

                linkedBusiness = await ReadCentralBusinessAsync(topology.Central, cancellationToken);
            }

            var created = !await db.Businesses.AnyAsync(cancellationToken);
            InstalledBranch installed;
            if (created)
            {
                var adoptedBranch = linkedBusiness is null || topology?.Central is null || string.IsNullOrWhiteSpace(input.BranchCode)
                    ? null
                    : await FindCentralBranchByCodeAsync(topology.Central, input.BranchCode, cancellationToken);
                installed = await InstallAsync(input, emit, cancellationToken, linkedBusiness, adoptedBranch);
                await emit(SetupEvent.Progress(SetupSteps.Verify, "started"), cancellationToken);
                await VerifyInstallationAsync(installed, cancellationToken, linkedBusiness is null);
                await emit(SetupEvent.Progress(SetupSteps.Verify, "succeeded"), cancellationToken);
            }
            else
            {
                installed = await LoadExistingAsync(cancellationToken);
            }

            if (topology is not null)
            {
                await ApplyTopologyAsync(topology, installed, linkedBusiness is not null, emit, cancellationToken);
            }
            else
            {
                await emit(SetupEvent.Progress(SetupSteps.InitialSync, "started"), cancellationToken);
                await emit(SetupEvent.Progress(SetupSteps.InitialSync, "succeeded"), cancellationToken);
            }

            await MarkCompletedAsync(installed, cancellationToken);
            await emit(SetupEvent.Completed(ToResponse(installed), alreadyConfigured: !created), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SetupRejectedException exception)
        {
            logger.LogWarning("Branch setup was rejected: {Reason}", exception.Message);
            await emit(SetupEvent.Failed(exception.Message), cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Branch setup failed.");
            var branchWasSaved = false;
            try
            {
                branchWasSaved = await db.Businesses.AnyAsync(CancellationToken.None);
            }
            catch (Exception statusException)
            {
                logger.LogDebug(statusException, "Could not check whether branch setup had already saved a business.");
            }

            await emit(
                SetupEvent.Failed(branchWasSaved
                    ? "تعذر إكمال نسخ بيانات الإدارة المركزية. بيانات الفرع محفوظة، ويمكنك إعادة المحاولة."
                    : "تعذر إكمال الإعداد. لم يتم حفظ بيانات الفرع. يمكنك المحاولة مرة أخرى."),
                cancellationToken);
        }
        finally
        {
            coordinator.Exit();
        }
    }

    private async Task ApplyTopologyAsync(
        TopologyPlan topology,
        InstalledBranch installed,
        bool registerInCentral,
        Func<SetupEvent, CancellationToken, Task> emit,
        CancellationToken cancellationToken)
    {
        if (topology.Central is not null && string.Equals(topology.OperatingMode, "CentralManagement", StringComparison.Ordinal))
        {
            if (environment.IsStaging())
            {
                topology = topology with
                {
                    Central = StagingCentral(topology.Central),
                    CreateCentral = false,
                };
            }
            if (topology.CreateCentral)
            {
                await centralDatabases.CreateIfMissingAsync(topology.Central, cancellationToken);
            }
            else
            {
                await centralDatabases.VerifyExistingAsync(topology.Central, cancellationToken);
            }

            var centralRoute = CentralRoute.Describe(topology.Central);
            logger.LogInformation("Central source {CentralRoute}", centralRoute);
            await SaveCentralRouteAsync(centralRoute, cancellationToken);
            var endpoint = await endpoints.SaveAsync(topology.Central, SqlEndpointSettings.Linked, cancellationToken);
            await EnsureCentralCheckpointAsync(endpoint.EndpointId, cancellationToken);
            if (registerInCentral)
            {
                var centralBusiness = await ReadCentralBusinessAsync(topology.Central!, cancellationToken);
                await AdoptCentralBusinessAsync(centralBusiness, installed.BusinessId, cancellationToken);
                installed = installed with { BusinessId = centralBusiness.BusinessId };
                await RegisterBranchInCentralAsync(topology.Central!, installed, cancellationToken);
            }

            if (topology.CentralAdminUserId is Guid selectedAdminId)
            {
                await CopySelectedCentralAdminAsync(topology.Central!, installed.BranchId, selectedAdminId, cancellationToken);
            }

            if (topology.LinkToCentral)
            {
                await emit(SetupEvent.Progress(SetupSteps.InitialSync, "started"), cancellationToken);
                InitialSyncGuard.RefuseOperationalCentral(environment.IsStaging(), topology.Central.DatabaseName);
                var replicaState = await sharedData.ReadReplicaStateAsync(cancellationToken);
                if (InitialSyncGuard.NeedsInitialSync(replicaState))
                {
                    await sharedData.BootstrapAsync(topology.Central, cancellationToken);
                    replicaState = await sharedData.EnsureCustomerReplicaAsync(endpoint.EndpointId, topology.Central, cancellationToken);
                }

                if (InitialSyncGuard.NeedsInitialSync(replicaState))
                {
                    throw new SetupRejectedException("المزامنة الأولية لم تكتمل. النسخة لم تُعلَّم جاهزة، وستستأنف تلقائياً أثناء تشغيل الخدمة.");
                }

                await emit(SetupEvent.Progress(SetupSteps.InitialSync, "succeeded"), cancellationToken);
                if (topology.CentralAdminUserId is null)
                {
                    await EnsureSetupAdminCentralAccessAsync(topology.Central, installed.BranchId, cancellationToken);
                }
            }
        }

        if (topology.BranchRemote is not null)
        {
            await endpoints.SaveAsync(topology.BranchRemote, SqlEndpointSettings.PendingRemoteSync, cancellationToken);
        }

        if (!topology.LinkToCentral)
        {
            await emit(SetupEvent.Progress(SetupSteps.InitialSync, "started"), cancellationToken);
            await emit(SetupEvent.Progress(SetupSteps.InitialSync, "succeeded"), cancellationToken);
        }
    }

    private SqlEndpointSettings StagingCentral(SqlEndpointSettings central)
    {
        InitialSyncGuard.RefuseOperationalCentral(true, InitialSyncGuard.ApprovedExistingCentral);
        var server = configuration["CentralSql:Server"];
        var user = configuration["CentralSql:User"];
        var password = configuration["CentralSql:Password"];
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            throw new SetupRejectedException("اتصال القاعدة المركزية غير مكتمل على الخادم.");
        }

        return central with
        {
            Server = server.Trim(),
            InstanceName = null,
            Port = null,
            DatabaseName = InitialSyncGuard.ApprovedExistingCentral,
            AuthenticationType = SqlEndpointSettings.SqlLogin,
            Username = user,
            Password = password,
            Encrypt = true,
            TrustServerCertificate = true,
        };
    }

    private async Task CheckServerAsync(CancellationToken cancellationToken)
    {
        if (!connections.IsSafeConfiguration(out var problem))
        {
            throw new SetupRejectedException(problem ?? "لم يتم إعداد الاتصال بخادم البيانات.");
        }

        try
        {
            await using var connection = new SqlConnection(connections.CreateMasterConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1;", connection);
            await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (SetupRejectedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "SQL Server check failed.");
            throw new SetupRejectedException("تعذر الاتصال بخادم البيانات. تأكد أن خادم البيانات يعمل ثم أعد المحاولة.");
        }
    }

    private async Task<InstalledBranch> InstallAsync(
        BranchSetup input,
        Func<SetupEvent, CancellationToken, Task> emit,
        CancellationToken cancellationToken,
        CentralBusinessLink? linkedBusiness = null,
        CentralBranchLink? adoptedBranch = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        InstalledBranch installed;
        try
        {
            var lockResult = await AcquireInstallLockAsync(transaction, cancellationToken);
            if (lockResult < 0)
            {
                throw new SetupRejectedException("الإعداد قيد التنفيذ. أعد المحاولة بعد قليل.");
            }

            if (await db.Businesses.AnyAsync(cancellationToken))
            {
                throw new SetupRejectedException("تم إعداد هذا الفرع من قبل.");
            }

            var now = UtcNowSeconds();
            var token = DeviceTokenService.Generate();
            var tokenHash = DeviceTokenService.Hash(token);

            await emit(SetupEvent.Progress(SetupSteps.CreateBusiness, "started"), cancellationToken);
            Guid businessId;
            if (linkedBusiness is null)
            {
                var business = new Business
                {
                    BusinessName = input.BusinessName,
                    BusinessDisplayName = input.BusinessDisplayName,
                    Phone = input.Phone,
                    Address = input.Address,
                    IsActive = true,
                    CreatedAt = now,
                };
                db.Businesses.Add(business);
                await db.SaveChangesAsync(cancellationToken);
                EnsureGenerated(business.BusinessId, "BusinessID");
                businessId = business.BusinessId;
            }
            else
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO dbo.Businesses
                        (BusinessID, BusinessName, BusinessDisplayName, Phone, Description, Address, IsActive, CreatedAt)
                    VALUES
                        ({linkedBusiness.BusinessId}, {linkedBusiness.BusinessName}, {linkedBusiness.ShortName}, {linkedBusiness.Phone}, {linkedBusiness.Description}, NULL, 1, {now});
                    """,
                    cancellationToken);
                businessId = linkedBusiness.BusinessId;
            }

            await emit(SetupEvent.Progress(SetupSteps.CreateBranch, "started"), cancellationToken);
            Guid branchId;
            if (adoptedBranch is null)
            {
                var branch = new Branch
                {
                    BusinessId = businessId,
                    BranchCode = input.BranchCode,
                    BranchName = input.BranchName,
                    BranchType = input.BranchType,
                    IsActive = true,
                    CreatedAt = now,
                };
                db.Branches.Add(branch);
                await db.SaveChangesAsync(cancellationToken);
                EnsureGenerated(branch.BranchId, "BranchID");
                branchId = branch.BranchId;
            }
            else
            {
                branchId = adoptedBranch.BranchId;
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO dbo.Branches
                        (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
                    VALUES
                        ({branchId}, {businessId}, {adoptedBranch.BranchCode}, {adoptedBranch.BranchName}, {adoptedBranch.BranchType}, 1, {now});
                    """,
                    cancellationToken);
            }

            await emit(SetupEvent.Progress(SetupSteps.CreateStation, "started"), cancellationToken);
            var station = new PosStation
            {
                BranchId = branchId,
                StationNumber = input.StationNumber,
                StationName = input.StationName,
                IsActive = true,
                CreatedAt = now,
            };
            db.PosStations.Add(station);
            await db.SaveChangesAsync(cancellationToken);
            EnsureGenerated(station.StationId, "StationID");

            await emit(SetupEvent.Progress(SetupSteps.RegisterDevice, "started"), cancellationToken);
            var device = new Device
            {
                BranchId = branchId,
                StationId = station.StationId,
                DeviceName = input.DeviceName,
                DeviceType = DeviceTokenService.DeviceTypePos,
                DeviceTokenHash = tokenHash,
                IsActive = true,
                RegisteredAt = now,
                CreatedAt = now,
            };
            db.Devices.Add(device);
            await db.SaveChangesAsync(cancellationToken);
            EnsureGenerated(device.DeviceId, "DeviceID");
            await DeviceConfigurationWriter.SaveAsync(db, device.DeviceId, branchId, input.DeviceName, input.StationNumber, cancellationToken);

            await emit(SetupEvent.Progress(SetupSteps.CreateAdmin, "started"), cancellationToken);
            if (linkedBusiness is null)
            {
                await AddAdminAsync(input.Admin, branchId, now, cancellationToken);
            }

            if (!DeviceTokenService.Matches(device.DeviceTokenHash, token))
            {
                throw new InvalidOperationException("Device token hash was not stored.");
            }

            await transaction.CommitAsync(cancellationToken);
            var storedInput = linkedBusiness is null
                ? input
                : input with
                {
                    BusinessName = linkedBusiness.BusinessName,
                    BusinessDisplayName = linkedBusiness.ShortName,
                    Phone = linkedBusiness.Phone,
                    Address = null,
                };
            installed = new InstalledBranch(
                businessId,
                branchId,
                station.StationId,
                device.DeviceId,
                token,
                now,
                storedInput);
        }
        catch
        {
            await SafeRollbackAsync(transaction, cancellationToken);
            throw;
        }

        await emit(SetupEvent.Progress(SetupSteps.CreateBusiness, "succeeded"), cancellationToken);
        await emit(SetupEvent.Progress(SetupSteps.CreateBranch, "succeeded"), cancellationToken);
        await emit(SetupEvent.Progress(SetupSteps.CreateStation, "succeeded"), cancellationToken);
        await emit(SetupEvent.Progress(SetupSteps.RegisterDevice, "succeeded"), cancellationToken);
        await emit(SetupEvent.Progress(SetupSteps.CreateAdmin, "succeeded"), cancellationToken);
        return installed;
    }

    public async Task CreateMissingAdminAsync(
        HttpRequest request,
        AdminAccount account,
        CancellationToken cancellationToken)
    {
        var phase = await connections.GetInstallationPhaseAsync(cancellationToken);
        if (phase == InstallationPhase.NotConfigured)
        {
            throw new SetupRejectedException("أكمل إعداد الفرع أولاً.");
        }

        if (phase == InstallationPhase.Configured)
        {
            throw new SetupRejectedException("تم إنشاء حساب مدير النظام من قبل.");
        }

        await migrations.ApplyPendingAsync(cancellationToken);
        var access = await devices.EvaluateAccessAsync(request, cancellationToken);
        if (access.Problem != DeviceAccessProblem.None || access.Device is null)
        {
            throw new SetupRejectedException("تعذر إنشاء الحساب من هذا الجهاز.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var lockResult = await AcquireInstallLockAsync(transaction, cancellationToken);
            if (lockResult < 0)
            {
                throw new SetupRejectedException("الإعداد قيد التنفيذ. أعد المحاولة بعد قليل.");
            }

            if (await AdminExistsAsync(cancellationToken))
            {
                throw new SetupRejectedException("تم إنشاء حساب مدير النظام من قبل.");
            }

            await AddAdminAsync(account, access.Device.BranchId, UtcNowSeconds(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await SafeRollbackAsync(transaction, cancellationToken);
            throw;
        }

        var central = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (central is not null && string.Equals(central.DatabaseRole, DatabaseRole.Central, StringComparison.Ordinal))
        {
            await EnsureSetupAdminCentralAccessAsync(central, access.Device.BranchId, cancellationToken);
        }
    }

    private async Task AddAdminAsync(
        AdminAccount account,
        Guid branchId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var normalized = AccountIdentity.NormalizeUsername(account.Username);
        if (await db.Users.AnyAsync(item => item.NormalizedUsername == normalized, cancellationToken))
        {
            throw new SetupRejectedException("اسم المستخدم مستخدم بالفعل.");
        }

        var role = await db.Roles.SingleOrDefaultAsync(
            item => item.RoleCode == RoleCodes.Admin && item.IsActive,
            cancellationToken);
        if (role is null)
        {
            throw new SetupRejectedException("تعذر تجهيز دور مدير النظام.");
        }

        var user = new AppUser
        {
            Username = account.Username.Trim(),
            NormalizedUsername = normalized,
            DisplayName = account.DisplayName,
            FullName = account.FullName,
            EmployeeNumber = account.EmployeeNumber,
            IsActive = true,
            MustChangePassword = false,
            FailedLoginCount = 0,
            CreatedAt = now,
        };
        user.PasswordHash = passwords.Hash(user, account.Password);
        if (string.Equals(user.PasswordHash, account.Password, StringComparison.Ordinal)
            || passwords.Verify(user, user.PasswordHash, account.Password) == PasswordVerificationResult.Failed)
        {
            throw new InvalidOperationException("Password hash was not stored.");
        }

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        EnsureGenerated(user.UserId, "UserID");

        db.UserRoles.Add(new UserRole
        {
            UserId = user.UserId,
            RoleId = role.RoleId,
            AssignedAt = now,
        });
        db.UserBranchAccess.Add(new UserBranchAccess
        {
            UserId = user.UserId,
            BranchId = branchId,
            IsActive = true,
            GrantedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> AdminExistsAsync(CancellationToken cancellationToken)
    {
        return await db.UserRoles.AnyAsync(
            item => item.Role.RoleCode == RoleCodes.Admin
                && item.User.BranchAccess.Any(access => access.IsActive),
            cancellationToken);
    }

    private async Task<int> AcquireInstallLockAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            """
            DECLARE @result int;
            EXEC @result = sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 15000;
            SELECT @result;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = "XtraPOS.FirstInstall";
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int code ? code : -1;
    }

    private async Task VerifyInstallationAsync(InstalledBranch installed, CancellationToken cancellationToken, bool requireLocalAdmin)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(
            """
            SELECT
                d.DeviceTokenHash,
                d.DeviceType,
                d.BranchID,
                d.StationID,
                b.BusinessID,
                s.StationNumber,
                (SELECT COUNT(*) FROM dbo.Businesses) AS BusinessCount,
                (SELECT COUNT(*) FROM dbo.Branches) AS BranchCount,
                (SELECT COUNT(*) FROM dbo.POSStations) AS StationCount,
                (SELECT COUNT(*) FROM dbo.Devices) AS DeviceCount
            FROM dbo.Devices AS d
            INNER JOIN dbo.Branches AS b ON b.BranchID = d.BranchID
            INNER JOIN dbo.POSStations AS s ON s.StationID = d.StationID
            WHERE d.DeviceID = @deviceId;
            """,
            connection);
        command.Parameters.Add("@deviceId", SqlDbType.UniqueIdentifier).Value = installed.DeviceId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new SetupRejectedException("تعذر التحقق من سلامة الإعداد.");
        }

        var hash = (byte[])reader.GetValue(reader.GetOrdinal("DeviceTokenHash"));
        var deviceType = reader.GetString(reader.GetOrdinal("DeviceType"));
        var branchId = reader.GetGuid(reader.GetOrdinal("BranchID"));
        var stationId = reader.GetGuid(reader.GetOrdinal("StationID"));
        var businessId = reader.GetGuid(reader.GetOrdinal("BusinessID"));
        var stationNumber = reader.GetInt32(reader.GetOrdinal("StationNumber"));
        var businessCount = reader.GetInt32(reader.GetOrdinal("BusinessCount"));
        var branchCount = reader.GetInt32(reader.GetOrdinal("BranchCount"));
        var stationCount = reader.GetInt32(reader.GetOrdinal("StationCount"));
        var deviceCount = reader.GetInt32(reader.GetOrdinal("DeviceCount"));

        var hashMatches = DeviceTokenService.Matches(hash, installed.Token);
        var tokenStoredAsText = ContainsPlaintext(hash, installed.Token);
        if (!hashMatches
            || tokenStoredAsText
            || deviceType != DeviceTokenService.DeviceTypePos
            || branchId != installed.BranchId
            || stationId != installed.StationId
            || businessId != installed.BusinessId
            || stationNumber != installed.Input.StationNumber
            || businessCount != 1
            || branchCount != 1
            || stationCount != 1
            || deviceCount != 1)
        {
            logger.LogError("Installation verification failed for device {DeviceId}.", installed.DeviceId);
            throw new SetupRejectedException("تعذر التحقق من سلامة الإعداد.");
        }

        await reader.CloseAsync();

        if (requireLocalAdmin)
        {
        await using var adminCommand = new SqlCommand(
            """
            SELECT
                u.PasswordHash,
                (SELECT COUNT(*) FROM dbo.Users) AS UserCount,
                (SELECT COUNT(*) FROM dbo.UserRoles AS ur
                    INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID
                    WHERE ur.UserID = u.UserID AND r.RoleCode = N'Admin') AS AdminRoleCount,
                (SELECT COUNT(*) FROM dbo.UserBranchAccess AS a
                    WHERE a.UserID = u.UserID AND a.BranchID = @branchId AND a.IsActive = 1) AS AccessCount
            FROM dbo.Users AS u
            WHERE u.NormalizedUsername = @normalized;
            """,
            connection);
        adminCommand.Parameters.Add("@branchId", SqlDbType.UniqueIdentifier).Value = installed.BranchId;
        adminCommand.Parameters.Add("@normalized", SqlDbType.NVarChar, 100).Value =
            AccountIdentity.NormalizeUsername(installed.Input.Admin.Username);

        await using var adminReader = await adminCommand.ExecuteReaderAsync(cancellationToken);
        if (!await adminReader.ReadAsync(cancellationToken))
        {
            throw new SetupRejectedException("تعذر التحقق من سلامة الإعداد.");
        }

        var passwordHash = adminReader.GetString(adminReader.GetOrdinal("PasswordHash"));
        var userCount = adminReader.GetInt32(adminReader.GetOrdinal("UserCount"));
        var adminRoleCount = adminReader.GetInt32(adminReader.GetOrdinal("AdminRoleCount"));
        var accessCount = adminReader.GetInt32(adminReader.GetOrdinal("AccessCount"));
        await adminReader.CloseAsync();

        var hashVerified = passwords.Verify(new AppUser(), passwordHash, installed.Input.Admin.Password)
            != PasswordVerificationResult.Failed;
        if (!hashVerified
            || string.Equals(passwordHash, installed.Input.Admin.Password, StringComparison.Ordinal)
            || userCount != 1
            || adminRoleCount != 1
            || accessCount != 1)
        {
            logger.LogError("Admin verification failed for device {DeviceId}.", installed.DeviceId);
            throw new SetupRejectedException("تعذر التحقق من سلامة الإعداد.");
        }
        }

        var inspection = await migrations.InspectAsync(cancellationToken);
        if (!inspection.IsValid)
        {
            logger.LogError("Schema verification failed after installation. {Diagnostic}", inspection.Diagnostic);
            throw new SetupRejectedException("تعذر التحقق من سلامة الإعداد.");
        }
    }

    private static bool ContainsPlaintext(byte[] storedHash, string token)
    {
        var utf8 = System.Text.Encoding.UTF8.GetString(storedHash);
        var unicode = System.Text.Encoding.Unicode.GetString(storedHash);
        return utf8.Contains(token, StringComparison.Ordinal)
            || unicode.Contains(token, StringComparison.Ordinal)
            || Convert.ToHexString(storedHash).Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceRegistrationResponse ToResponse(InstalledBranch installed)
    {
        return new DeviceRegistrationResponse
        {
            DeviceId = installed.DeviceId,
            BusinessId = installed.BusinessId,
            BranchId = installed.BranchId,
            StationId = installed.StationId,
            DeviceName = installed.Input.DeviceName,
            DeviceType = DeviceTokenService.DeviceTypePos,
            DeviceToken = installed.Token,
            RegisteredAt = installed.RegisteredAt,
            BusinessName = installed.Input.BusinessName,
            BusinessDisplayName = installed.Input.BusinessDisplayName,
            BranchName = installed.Input.BranchName,
            BranchCode = installed.Input.BranchCode,
            BranchType = installed.Input.BranchType,
            StationName = installed.Input.StationName,
            StationNumber = installed.Input.StationNumber,
        };
    }

    private static void EnsureGenerated(Guid id, string column)
    {
        if (id == Guid.Empty)
        {
            throw new InvalidOperationException($"{column} was not generated.");
        }
    }

    private static async Task SafeRollbackAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (Exception)
        {
            // The transaction may already be completed.
        }
    }

    private static DateTime UtcNowSeconds()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private async Task<CentralBusinessLink> ReadCentralBusinessAsync(
        SqlEndpointSettings central,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT BusinessID, BusinessName, BusinessDisplayName, Phone
            FROM dbo.Businesses;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new SetupRejectedException("القاعدة المركزية لا تحتوي على هوية منشأة.");
        }

        var link = new CentralBusinessLink(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            null,
            reader.IsDBNull(3) ? null : reader.GetString(3));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new SetupRejectedException("القاعدة المركزية تحتوي على أكثر من منشأة.");
        }

        return link;
    }

    private async Task CopySelectedCentralAdminAsync(
        SqlEndpointSettings central,
        Guid branchId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await remote.OpenAsync(cancellationToken);
        await using var read = new SqlCommand(
            """
            SELECT u.Username, u.NormalizedUsername, u.DisplayName, u.FullName, u.EmployeeNumber,
                   u.PasswordHash, u.MustChangePassword, u.CreatedAt, r.RoleID, r.RoleCode, r.RoleName
            FROM dbo.Users AS u
            INNER JOIN dbo.UserRoles AS ur ON ur.UserID = u.UserID
            INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID AND r.RoleCode = N'Admin'
            WHERE u.UserID = @userId AND u.IsActive = 1;
            """,
            remote);
        read.Parameters.AddWithValue("@userId", userId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new SetupRejectedException("مدير النظام المحدد غير موجود في الإدارة المركزية.");
        }

        var username = reader.GetString(0);
        var normalized = reader.GetString(1);
        var displayName = reader.GetString(2);
        var fullName = reader.IsDBNull(3) ? null : reader.GetString(3);
        var employeeNumber = reader.IsDBNull(4) ? null : reader.GetString(4);
        var hash = reader.GetString(5);
        var mustChange = reader.GetBoolean(6);
        var createdAt = reader.GetDateTime(7);
        var roleId = reader.GetGuid(8);
        var roleCode = reader.GetString(9);
        var roleName = reader.GetString(10);
        await reader.CloseAsync();

        await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        await using var copy = new SqlCommand(
            """
            IF EXISTS (
                SELECT 1 FROM dbo.Users
                WHERE NormalizedUsername = @normalized AND UserID <> @userId)
            BEGIN
                RAISERROR(N'duplicate username', 16, 1);
                RETURN;
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleCode = @roleCode)
                INSERT INTO dbo.Roles (RoleID, RoleCode, RoleName, IsSystemRole, IsActive, CreatedAt)
                VALUES (@roleId, @roleCode, @roleName, 1, 1, SYSUTCDATETIME());

            IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE UserID = @userId)
                INSERT INTO dbo.Users
                    (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, FailedLoginCount, CreatedAt)
                VALUES
                    (@userId, @username, @normalized, @displayName, @fullName, @employeeNumber, @hash, 1, @mustChange, 0, @createdAt);

            INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
            SELECT @userId, localRole.RoleID, SYSUTCDATETIME()
            FROM dbo.Roles AS localRole
            WHERE localRole.RoleCode = @roleCode
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.UserRoles AS existing
                  WHERE existing.UserID = @userId AND existing.RoleID = localRole.RoleID);

            IF EXISTS (SELECT 1 FROM dbo.UserBranchAccess WHERE UserID = @userId AND BranchID = @branchId)
                UPDATE dbo.UserBranchAccess SET IsActive = 1 WHERE UserID = @userId AND BranchID = @branchId;
            ELSE
                INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
                VALUES (@userId, @branchId, 1, SYSUTCDATETIME());
            """,
            branch);
        copy.Parameters.AddWithValue("@userId", userId);
        copy.Parameters.AddWithValue("@username", username);
        copy.Parameters.AddWithValue("@normalized", normalized);
        copy.Parameters.AddWithValue("@displayName", displayName);
        copy.Parameters.AddWithValue("@fullName", (object?)fullName ?? DBNull.Value);
        copy.Parameters.AddWithValue("@employeeNumber", (object?)employeeNumber ?? DBNull.Value);
        copy.Parameters.AddWithValue("@hash", hash);
        copy.Parameters.AddWithValue("@mustChange", mustChange);
        copy.Parameters.AddWithValue("@createdAt", createdAt);
        copy.Parameters.AddWithValue("@roleId", roleId);
        copy.Parameters.AddWithValue("@roleCode", roleCode);
        copy.Parameters.AddWithValue("@roleName", roleName);
        copy.Parameters.AddWithValue("@branchId", branchId);
        try
        {
            await copy.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 50000)
        {
            throw new SetupRejectedException("اسم المستخدم موجود في الفرع بحساب مختلف.");
        }

        await using var grant = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.UserBranchAccess WHERE UserID = @userId AND BranchID = @branchId)
                UPDATE dbo.UserBranchAccess SET IsActive = 1 WHERE UserID = @userId AND BranchID = @branchId;
            ELSE
                INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
                VALUES (@userId, @branchId, 1, SYSUTCDATETIME());
            """,
            remote);
        grant.Parameters.AddWithValue("@userId", userId);
        grant.Parameters.AddWithValue("@branchId", branchId);
        try
        {
            await grant.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (environment.IsStaging() && exception.Number == 229)
        {
            logger.LogWarning("Staging did not grant central branch access because INSERT on UserBranchAccess is not granted.");
        }
    }

    private async Task EnsureSetupAdminCentralAccessAsync(
        SqlEndpointSettings central,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        await using var read = new SqlCommand(
            """
            SELECT u.UserID, u.Username, u.NormalizedUsername, u.DisplayName, u.FullName, u.EmployeeNumber,
                   u.PasswordHash, u.MustChangePassword, u.CreatedAt, r.RoleID, r.RoleCode, r.RoleName
            FROM dbo.Users AS u
            INNER JOIN dbo.UserRoles AS ur ON ur.UserID = u.UserID
            INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID AND r.RoleCode = N'Admin'
            WHERE u.IsActive = 1;
            """,
            branch);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        var admins = new List<(Guid UserId, string Username, string Normalized, string DisplayName, string? FullName, string? EmployeeNumber, string PasswordHash, bool MustChange, DateTime CreatedAt, Guid RoleId, string RoleCode, string RoleName)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            admins.Add((
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetBoolean(7),
                reader.GetDateTime(8),
                reader.GetGuid(9),
                reader.GetString(10),
                reader.GetString(11)));
        }

        await reader.CloseAsync();
        if (admins.Count == 0)
        {
            return;
        }

        await using var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await remote.OpenAsync(cancellationToken);
        foreach (var admin in admins)
        {
            await using var grant = new SqlCommand(
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleCode = @roleCode)
                    INSERT INTO dbo.Roles (RoleID, RoleCode, RoleName, IsSystemRole, IsActive, CreatedAt)
                    VALUES (@roleId, @roleCode, @roleName, 1, 1, SYSUTCDATETIME());

                DECLARE @centralUser uniqueidentifier = (SELECT UserID FROM dbo.Users WHERE UserID = @userId);
                IF @centralUser IS NULL AND EXISTS (SELECT 1 FROM dbo.Users WHERE NormalizedUsername = @normalized)
                BEGIN
                    RAISERROR(N'Central already has this username on a different account.', 16, 1);
                    RETURN;
                END;
                IF @centralUser IS NULL
                BEGIN
                    INSERT INTO dbo.Users
                        (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, CreatedAt)
                    VALUES
                        (@userId, @username, @normalized, @displayName, @fullName, @employeeNumber, @hash, 1, @mustChange, @createdAt);
                    SET @centralUser = @userId;
                    INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
                    SELECT @centralUser, RoleID, SYSUTCDATETIME()
                    FROM dbo.Roles
                    WHERE RoleCode = @roleCode
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.UserRoles AS existing
                          WHERE existing.UserID = @centralUser AND existing.RoleID = dbo.Roles.RoleID);
                END;

                IF NOT EXISTS (SELECT 1 FROM dbo.UserBranchAccess WHERE UserID = @centralUser AND BranchID = @branchId)
                    INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
                    VALUES (@centralUser, @branchId, 1, SYSUTCDATETIME());
                """,
                remote);
            grant.Parameters.AddWithValue("@userId", admin.UserId);
            grant.Parameters.AddWithValue("@username", admin.Username);
            grant.Parameters.AddWithValue("@normalized", admin.Normalized);
            grant.Parameters.AddWithValue("@displayName", admin.DisplayName);
            grant.Parameters.AddWithValue("@fullName", (object?)admin.FullName ?? DBNull.Value);
            grant.Parameters.AddWithValue("@employeeNumber", (object?)admin.EmployeeNumber ?? DBNull.Value);
            grant.Parameters.AddWithValue("@hash", admin.PasswordHash);
            grant.Parameters.AddWithValue("@mustChange", admin.MustChange);
            grant.Parameters.AddWithValue("@createdAt", admin.CreatedAt);
            grant.Parameters.AddWithValue("@roleId", admin.RoleId);
            grant.Parameters.AddWithValue("@roleCode", admin.RoleCode);
            grant.Parameters.AddWithValue("@roleName", admin.RoleName);
            grant.Parameters.AddWithValue("@branchId", branchId);
            try
            {
                await grant.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException exception) when (exception.Number == 50000)
            {
                throw new SetupRejectedException("اسم المستخدم موجود في الإدارة المركزية بحساب مختلف.");
            }

            await using var active = new SqlCommand(
                """
                SELECT CAST(IsActive AS int)
                FROM dbo.UserBranchAccess
                WHERE UserID = @userId AND BranchID = @branchId;
                """,
                remote);
            active.Parameters.AddWithValue("@userId", admin.UserId);
            active.Parameters.AddWithValue("@branchId", branchId);
            if (await active.ExecuteScalarAsync(cancellationToken) is not int isActive || isActive != 1)
            {
                continue;
            }

            await using var mirror = new SqlCommand(
                """
                IF EXISTS (SELECT 1 FROM dbo.UserBranchAccess WHERE UserID = @userId AND BranchID = @branchId)
                    UPDATE dbo.UserBranchAccess SET IsActive = 1 WHERE UserID = @userId AND BranchID = @branchId;
                ELSE
                    INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
                    VALUES (@userId, @branchId, 1, SYSUTCDATETIME());
                """,
                branch);
            mirror.Parameters.AddWithValue("@userId", admin.UserId);
            mirror.Parameters.AddWithValue("@branchId", branchId);
            await mirror.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<bool> CentralHasBusinessAsync(SqlEndpointSettings central, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.Businesses;", connection);
            return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) > 0;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private async Task AdoptCentralBusinessAsync(
        CentralBusinessLink centralBusiness,
        Guid localBusinessId,
        CancellationToken cancellationToken)
    {
        if (centralBusiness.BusinessId == localBusinessId)
        {
            return;
        }

        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Businesses WHERE BusinessID = @centralId)
                INSERT INTO dbo.Businesses
                    (BusinessID, BusinessName, BusinessDisplayName, Phone, Description, Address, IsActive, CreatedAt)
                VALUES
                    (@centralId, @name, @shortName, @phone, NULL, NULL, 1, SYSUTCDATETIME());

            UPDATE dbo.Branches SET BusinessID = @centralId WHERE BusinessID = @localId;

            DELETE FROM dbo.Businesses
            WHERE BusinessID = @localId
              AND NOT EXISTS (SELECT 1 FROM dbo.Branches WHERE BusinessID = @localId);
            """,
            connection);
        command.Parameters.AddWithValue("@centralId", centralBusiness.BusinessId);
        command.Parameters.AddWithValue("@localId", localBusinessId);
        command.Parameters.AddWithValue("@name", centralBusiness.BusinessName);
        command.Parameters.AddWithValue("@shortName", (object?)centralBusiness.ShortName ?? DBNull.Value);
        command.Parameters.AddWithValue("@phone", (object?)centralBusiness.Phone ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RegisterBranchInCentralAsync(
        SqlEndpointSettings central,
        InstalledBranch installed,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Branches WHERE BranchID = @branchId OR BranchCode = @branchCode)
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
                VALUES (@branchId, @businessId, @branchCode, @branchName, @branchType, 1, SYSUTCDATETIME());
            """,
            connection);
        command.Parameters.AddWithValue("@branchId", installed.BranchId);
        command.Parameters.AddWithValue("@businessId", installed.BusinessId);
        command.Parameters.AddWithValue("@branchCode", (object?)installed.Input.BranchCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@branchName", installed.Input.BranchName);
        command.Parameters.AddWithValue("@branchType", installed.Input.BranchType);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (environment.IsStaging() && exception.Number == 229)
        {
            logger.LogWarning("Staging did not register its branch in the existing central database because INSERT on Branches is not granted.");
        }
    }

    private async Task SaveCentralRouteAsync(string route, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = N'CentralSource')
                UPDATE dbo.SystemSettings SET SettingValue = @route WHERE SettingKey = N'CentralSource';
            ELSE
                INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'CentralSource', @route);
            """,
            connection);
        command.Parameters.AddWithValue("@route", route);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureCentralCheckpointAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (
                SELECT 1 FROM dbo.SyncCheckpoints
                WHERE EndpointID = @endpoint AND StreamName = @stream)
                INSERT INTO dbo.SyncCheckpoints (EndpointID, StreamName, LastAppliedVersion)
                VALUES (@endpoint, @stream, 0);
            """,
            connection);
        command.Parameters.AddWithValue("@endpoint", endpointId);
        command.Parameters.AddWithValue("@stream", SharedDataSync.StreamName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkCompletedAsync(InstalledBranch installed, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole')
                INSERT INTO dbo.SystemSettings (SettingKey, SettingValue)
                VALUES (N'DatabaseRole', N'Branch');

            IF NOT EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = N'BusinessID')
                INSERT INTO dbo.SystemSettings (SettingKey, SettingValue)
                VALUES (N'BusinessID', @businessId);

            IF NOT EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = N'BranchID')
                INSERT INTO dbo.SystemSettings (SettingKey, SettingValue)
                VALUES (N'BranchID', @branchId);

            IF NOT EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = N'SetupCompletedAt')
                INSERT INTO dbo.SystemSettings (SettingKey, SettingValue)
                VALUES (N'SetupCompletedAt', CONVERT(nvarchar(40), SYSUTCDATETIME(), 127));
            """,
            connection);
        command.Parameters.AddWithValue("@businessId", installed.BusinessId.ToString("D"));
        command.Parameters.AddWithValue("@branchId", installed.BranchId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        InstallationState.MarkConfigured(environment.ContentRootPath);
        SetupAuthorization.Invalidate(environment.ContentRootPath);
    }

    private async Task<InstalledBranch> LoadExistingAsync(CancellationToken cancellationToken)
    {
        var device = await db.Devices
            .Include(item => item.Branch)
            .ThenInclude(item => item.Business)
            .Include(item => item.Station)
            .SingleAsync(cancellationToken);
        var station = device.Station ?? throw new SetupRejectedException("نقطة البيع غير موجودة.");
        var branch = device.Branch;
        var business = branch.Business;
        var input = new BranchSetup(
            business.BusinessName,
            business.BusinessDisplayName,
            business.Phone,
            business.Address,
            branch.BranchName,
            branch.BranchCode,
            branch.BranchType,
            station.StationNumber,
            station.StationName,
            device.DeviceName,
            new AdminAccount("existing", "existing", null, null, "unused"));
        return new InstalledBranch(
            business.BusinessId,
            branch.BranchId,
            station.StationId,
            device.DeviceId,
            "",
            device.RegisteredAt,
            input);
    }

    private async Task<bool> HasExistingBranchAsync(CancellationToken cancellationToken)
    {
        if (!await connections.DatabaseExistsAsync(cancellationToken))
        {
            return false;
        }

        try
        {
            if (await connections.HasCompletedSetupAsync(cancellationToken))
            {
                return true;
            }

            return await db.Businesses.AnyAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 208)
        {
            return false;
        }
    }

    private sealed record CentralBranchLink(Guid BranchId, string BranchCode, string BranchName, string BranchType);

    private async Task<CentralBranchLink?> FindCentralBranchByCodeAsync(
        SqlEndpointSettings central,
        string branchCode,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) BranchID, BranchCode, BranchName, BranchType
            FROM dbo.Branches
            WHERE BranchCode = @code;
            """,
            connection);
        command.Parameters.AddWithValue("@code", branchCode.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CentralBranchLink(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    private sealed record CentralBusinessLink(
        Guid BusinessId,
        string BusinessName,
        string? ShortName,
        string? Description,
        string? Phone);

    private sealed record InstalledBranch(
        Guid BusinessId,
        Guid BranchId,
        Guid StationId,
        Guid DeviceId,
        string Token,
        DateTime RegisteredAt,
        BranchSetup Input);
}
