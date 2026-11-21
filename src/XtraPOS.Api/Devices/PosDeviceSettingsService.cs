using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Devices;

public sealed record PosDeviceSettingsView(
    Guid DeviceId,
    Guid BranchId,
    string? BranchName,
    int? PosNumber,
    string? DeviceName,
    IReadOnlyList<PosDeviceSettingValue> Settings,
    string SyncState,
    int PendingTransfers,
    string? LastError,
    string? DiagnosticId,
    long? CentralVersion,
    DateTime? SyncedAtUtc);

public sealed record CentralPosDeviceSummary(
    Guid DeviceId,
    Guid BranchId,
    string? BranchName,
    int? PosNumber,
    string? DeviceName,
    IReadOnlyList<PosDeviceSettingValue> Settings,
    long SettingsVersion,
    DateTime? UpdatedAtUtc,
    string? UpdatedSource);

/// <summary>سطر من سجل الترحيلات (من الفرع إلى المركز).</summary>
public sealed record DeviceSettingsTransfer(
    Guid OutboxEventId,
    IReadOnlyList<string> ChangedKeys,
    string? CreatedByName,
    string Status,
    int AttemptCount,
    DateTime CreatedAt,
    DateTime? LastAttemptAt,
    DateTime? NextRetryAt,
    DateTime? CompletedAt,
    long? CentralVersion,
    string? LastError,
    string? DiagnosticId);

/// <summary>سطر مما وصل من المركز وطُبِّق على هذه النقطة.</summary>
public sealed record DeviceSettingsReceived(long ChangeSequence, long CentralVersion, string Outcome, DateTime AppliedAt);

public sealed record DeviceSettingsHistory(
    IReadOnlyList<DeviceSettingsTransfer> Transfers,
    IReadOnlyList<DeviceSettingsReceived> Received);

/// <summary>سطر من سجل التدقيق المركزي: قيمة قبل وبعد لكل تعديل فعلي.</summary>
public sealed record DeviceSettingsAuditEntry(
    string SettingKey,
    string? Label,
    string? BeforeValue,
    string? AfterValue,
    string Source,
    string? ActorName,
    Guid? OutboxEventId,
    long SettingsVersion,
    DateTime OccurredAt);

public sealed record PosDeviceSettingsSyncResult(int Pushed, int Pulled, string? Error);

/// <summary>
/// القيم الأولية لنقطة البيع ومزامنتها مع الإدارة المركزية.
///
/// من الفرع إلى المركز (نفس نمط BranchFinancialOutbox):
///   كل حفظ = تحديث الإعداد محلياً + سطر في BranchDeviceSettingsOutbox ضمن Transaction واحدة.
///   العامل (أو الحفظ نفسه فوراً) يحجز السطر (Lease)، يرحّله، ويسجّل الحالة وعدد المحاولات وآخر خطأ ورمز التشخيص.
///   المركز يسجّل استلام كل ترحيل في PosDeviceSettingsReceipts (فلا يُطبَّق مرتين)،
///   وكل قيمة تغيّرت فعلاً في PosDeviceSettingsAudit (قبل/بعد)، وكل نسخة جديدة في SyncChangeLog.
///
/// من المركز إلى الفرع:
///   تعديل موظف المركز يُكتب في CentralPosDeviceSettings + PosDeviceSettingsAudit + SyncChangeLog.
///   الفرع يسحب أسطر PosDeviceSettings من SyncChangeLog، يطبّق ما يخص أجهزته، ويسجّل ذلك في DeviceSettingsInbox.
///
/// الدمج في كل الاتجاهات بقاعدة الأحدث لكل مفتاح (PosDeviceSettingsDocument.Merge)، فلا يضيع تعديل.
/// </summary>
public sealed class PosDeviceSettingsService(
    SqlConnectionFactory connections,
    SyncEndpointStore endpoints,
    SyncStatusStore status,
    IHostEnvironment environment,
    ILogger<PosDeviceSettingsService> logger)
{
    public const string EntityType = "PosDeviceSettings";
    public const string StreamName = "PosDeviceSettings";
    public const string SyncLocalOnly = "LocalOnly";
    public const string SyncPending = "PendingCentral";
    public const string SyncReconciliation = "ReconciliationRequired";
    public const string SyncCurrent = "Synced";
    public const string OutboxPending = "Pending";
    public const string OutboxCompleted = "Completed";
    public const string OutboxReconciliation = "ReconciliationRequired";
    private const string StatusOperation = "pos-settings";
    private const int MaxDeliveriesPerRun = 50;

    // ------------------------------------------------------------------
    // النقطة الحالية (الفرع)
    // ------------------------------------------------------------------

    public async Task<PosDeviceSettingsView> GetCurrentAsync(PosDeviceContext device, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await EnsureBranchTablesAsync(connection, cancellationToken);
        var link = await ResolveLinkedCentralAsync(connection, cancellationToken);
        return await ReadLocalViewAsync(connection, device.DeviceId, device.BranchId, link.Linked, cancellationToken);
    }

    public async Task<PosDeviceSettingsView> SaveCurrentAsync(
        PosDeviceContext device,
        IReadOnlyDictionary<string, JsonElement>? values,
        CancellationToken cancellationToken)
    {
        var changes = PosDeviceSettingsDocument.BuildChanges(
            values,
            DateTime.UtcNow,
            device.UserId,
            device.DisplayName,
            PosDeviceSettingSources.Branch);

        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await EnsureBranchTablesAsync(connection, cancellationToken);
        var link = await ResolveLinkedCentralAsync(connection, cancellationToken);

        var eventId = Guid.NewGuid();
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            var row = await ReadLocalRowAsync(connection, transaction, device.DeviceId, lockRow: true, cancellationToken);
            var current = PosDeviceSettingsDocument.Parse(PosDeviceSettingsDocument.ReadFromConfiguration(row?.ConfigurationJson));
            var merged = PosDeviceSettingsDocument.Merge(current, changes);
            await UpsertLocalAsync(
                connection,
                transaction,
                device.DeviceId,
                device.BranchId,
                device.BusinessId,
                row,
                merged,
                centralVersion: null,
                updatedBy: device.UserId.ToString("D"),
                cancellationToken);
            await InsertOutboxAsync(connection, transaction, eventId, device, changes, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "POS device settings saved for {DeviceId} by {UserId}: {Keys}. Outbox {OutboxEventId}.",
            device.DeviceId,
            device.UserId,
            string.Join(",", changes.Keys),
            eventId);

        // الترحيل الفوري. إن فشل يبقى السطر Pending مع الخطأ ويعيده العامل تلقائياً.
        if (link.Settings is not null)
        {
            try
            {
                await DeliverNextAsync(connection, link.Settings, eventId, cancellationToken);
            }
            catch (SqlException exception)
            {
                // الحفظ المحلي وسطر الـ Outbox تمّا. السطر محجوز 45 ثانية ثم يعيده العامل؛
                // PosDeviceSettingsReceipts يمنع تكراره في المركز إن كان قد وصل.
                logger.LogWarning(exception, "Immediate POS settings transfer {OutboxEventId} interrupted.", eventId);
            }
        }

        return await ReadLocalViewAsync(connection, device.DeviceId, device.BranchId, link.Linked, cancellationToken);
    }

    public async Task<DeviceSettingsHistory> GetCurrentHistoryAsync(PosDeviceContext device, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await EnsureBranchTablesAsync(connection, cancellationToken);

        var transfers = new List<DeviceSettingsTransfer>();
        await using (var command = new SqlCommand(
            """
            SELECT TOP (50) OutboxEventID, ChangedKeys, CreatedByName, Status, AttemptCount, CreatedAt,
                   LastAttemptAt, NextRetryAt, CompletedAt, CentralVersion, LastError, DiagnosticID
            FROM dbo.BranchDeviceSettingsOutbox
            WHERE DeviceID = @device
            ORDER BY CreatedAt DESC;
            """,
            connection))
        {
            command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = device.DeviceId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var state = reader.GetString(3);
                transfers.Add(new DeviceSettingsTransfer(
                    reader.GetGuid(0),
                    reader.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    state,
                    reader.GetInt32(4),
                    reader.GetDateTime(5),
                    reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                    state == OutboxPending ? reader.GetDateTime(7) : null,
                    reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11)));
            }
        }

        var received = new List<DeviceSettingsReceived>();
        await using (var command = new SqlCommand(
            """
            SELECT TOP (20) ChangeSequence, CentralVersion, Outcome, AppliedAt
            FROM dbo.DeviceSettingsInbox
            WHERE DeviceID = @device
            ORDER BY AppliedAt DESC, ChangeSequence DESC;
            """,
            connection))
        {
            command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = device.DeviceId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                received.Add(new DeviceSettingsReceived(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetDateTime(3)));
            }
        }

        return new DeviceSettingsHistory(transfers, received);
    }

    // ------------------------------------------------------------------
    // المزامنة (تُستدعى من العامل في الخلفية)
    // ------------------------------------------------------------------

    public async Task<PosDeviceSettingsSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        if (!await BranchTablesExistAsync(connection, cancellationToken))
        {
            return new PosDeviceSettingsSyncResult(0, 0, null);
        }

        var link = await ResolveLinkedCentralAsync(connection, cancellationToken);
        var central = link.Settings;
        var view = central is null ? null : await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
        if (central is null || view is null)
        {
            return new PosDeviceSettingsSyncResult(0, 0, null);
        }

        var pushed = 0;
        string? error = null;
        for (var i = 0; i < MaxDeliveriesPerRun; i++)
        {
            var outcome = await DeliverNextAsync(connection, central, null, cancellationToken);
            if (outcome is null)
            {
                break;
            }

            if (outcome.Delivered)
            {
                pushed++;
                continue;
            }

            // المركز غير متاح أو غير محدّث: لا فائدة من محاولة باقي السطور الآن.
            error = outcome.Error;
            break;
        }

        var pulled = 0;
        if (error is null)
        {
            try
            {
                pulled = await PullAsync(connection, central, view.EndpointId, cancellationToken);
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                error = SqlEndpointSettings.DescribeConnectionFailure(exception);
                var diagnostic = SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "pos-settings-pull", exception);
                status.Report("Error", StatusOperation, "تعذر سحب القيم الأولية من الإدارة المركزية.", DiagnosticOf(diagnostic));
                logger.LogWarning(exception, "POS device settings pull failed.");
            }
        }

        if (error is null && (pushed > 0 || pulled > 0))
        {
            status.Report("Success", StatusOperation, null, null);
        }

        return new PosDeviceSettingsSyncResult(pushed, pulled, error);
    }

    private sealed record DeliveryOutcome(bool Delivered, string? Error);

    private sealed record ClaimedEvent(
        Guid OutboxEventId,
        Guid DeviceId,
        Guid BranchId,
        string Payload,
        Guid? CreatedByUserId,
        string? CreatedByName,
        int AttemptCount);

    /// <summary>
    /// يحجز سطراً واحداً من الـ Outbox (أو السطر المحدد) ويرحّله. null إن لم يوجد سطر جاهز.
    /// لا يرمي استثناءات اتصال: يسجّلها في السطر نفسه.
    /// </summary>
    private async Task<DeliveryOutcome?> DeliverNextAsync(
        SqlConnection branch,
        SqlEndpointSettings central,
        Guid? onlyEventId,
        CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(branch, onlyEventId, cancellationToken);
        if (claimed is null)
        {
            return null;
        }

        SortedDictionary<string, PosDeviceSettingEntry> changes;
        try
        {
            changes = PosDeviceSettingsDocument.Parse(claimed.Payload);
            if (changes.Count == 0)
            {
                throw new FormatException("Outbox payload has no settings.");
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            var id = SetupLog.Write(environment.ContentRootPath, environment.EnvironmentName, "pos-settings-push", "InvalidPayload", detail: claimed.OutboxEventId.ToString("D"));
            await ParkAsync(branch, claimed, OutboxReconciliation, "محتوى الترحيل غير صالح.", id, TimeSpan.Zero, cancellationToken);
            return new DeliveryOutcome(false, "محتوى الترحيل غير صالح.");
        }

        CentralWriteResult written;
        try
        {
            var deviceName = await LocalDeviceNameAsync(branch, claimed.DeviceId, cancellationToken);
            await using var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
            await remote.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await remote.BeginTransactionAsync(cancellationToken);
            await EnsureCentralTablesAsync(remote, transaction, cancellationToken);
            written = await ReceiptAsync(remote, transaction, claimed, cancellationToken)
                ?? await MergeIntoCentralAsync(
                    remote,
                    transaction,
                    claimed.DeviceId,
                    claimed.BranchId,
                    deviceName,
                    changes,
                    PosDeviceSettingSources.Branch,
                    claimed.CreatedByUserId,
                    claimed.CreatedByName,
                    claimed.OutboxEventId,
                    requireKnownDevice: false,
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            var message = exception is PosDeviceSettingsException known
                ? known.Message
                : SqlEndpointSettings.DescribeConnectionFailure(exception);
            var diagnostic = DiagnosticOf(SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "pos-settings-push", exception));
            await ParkAsync(branch, claimed, OutboxPending, message, diagnostic, Backoff(claimed.AttemptCount + 1), cancellationToken);
            status.Report("Error", StatusOperation, "تعذر ترحيل القيم الأولية إلى الإدارة المركزية.", diagnostic);
            logger.LogWarning(exception, "POS device settings transfer {OutboxEventId} failed. Diagnostic {DiagnosticId}.", claimed.OutboxEventId, diagnostic);
            return new DeliveryOutcome(false, message);
        }

        await CompleteAsync(branch, claimed, written, cancellationToken);
        logger.LogInformation(
            "POS device settings transfer {OutboxEventId} completed for {DeviceId}. Central version {Version}.",
            claimed.OutboxEventId,
            claimed.DeviceId,
            written.Version);
        return new DeliveryOutcome(true, null);
    }

    private static async Task<ClaimedEvent?> ClaimAsync(SqlConnection branch, Guid? onlyEventId, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            ;WITH nextRow AS (
                SELECT TOP (1) OutboxEventID
                FROM dbo.BranchDeviceSettingsOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = N'Pending'
                  AND (@event IS NOT NULL OR NextRetryAt <= DATEADD(second, 1, SYSUTCDATETIME()))
                  AND (LeaseUntil IS NULL OR LeaseUntil < SYSUTCDATETIME())
                  AND (@event IS NULL OR OutboxEventID = @event)
                ORDER BY CreatedAt
            )
            UPDATE box
            SET LeaseOwner = @owner, LeaseUntil = DATEADD(second, 45, SYSUTCDATETIME())
            OUTPUT inserted.OutboxEventID, inserted.DeviceID, inserted.BranchID, inserted.Payload,
                   inserted.CreatedByUserID, inserted.CreatedByName, inserted.AttemptCount
            FROM dbo.BranchDeviceSettingsOutbox AS box
            INNER JOIN nextRow ON nextRow.OutboxEventID = box.OutboxEventID;
            """,
            branch,
            transaction);
        command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = (object?)onlyEventId ?? DBNull.Value;
        command.Parameters.Add("@owner", SqlDbType.NVarChar, 80).Value = Limit(Environment.MachineName, 80);
        ClaimedEvent? claimed = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                claimed = new ClaimedEvent(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt32(6));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    private static async Task ParkAsync(
        SqlConnection branch,
        ClaimedEvent claimed,
        string state,
        string error,
        string? diagnosticId,
        TimeSpan retryAfter,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.BranchDeviceSettingsOutbox
            SET Status = @status,
                AttemptCount = AttemptCount + 1,
                LastAttemptAt = SYSUTCDATETIME(),
                NextRetryAt = DATEADD(second, @retry, SYSUTCDATETIME()),
                LastError = @error,
                DiagnosticID = @diagnostic,
                LeaseOwner = NULL,
                LeaseUntil = NULL
            WHERE OutboxEventID = @event AND Status <> N'Completed';
            """,
            branch);
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = state;
        command.Parameters.Add("@retry", SqlDbType.Int).Value = (int)retryAfter.TotalSeconds;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 400).Value = Limit(error, 400);
        command.Parameters.Add("@diagnostic", SqlDbType.NVarChar, 40).Value = (object?)Limit(diagnosticId, 40) ?? DBNull.Value;
        command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = claimed.OutboxEventId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>يعلّم الترحيل مكتملاً، ويدمج نسخة المركز في إعداد الجهاز محلياً، في Transaction واحدة.</summary>
    private static async Task CompleteAsync(
        SqlConnection branch,
        ClaimedEvent claimed,
        CentralWriteResult written,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
        await using (var command = new SqlCommand(
            """
            UPDATE dbo.BranchDeviceSettingsOutbox
            SET Status = N'Completed',
                AttemptCount = AttemptCount + 1,
                LastAttemptAt = SYSUTCDATETIME(),
                CompletedAt = SYSUTCDATETIME(),
                CentralVersion = @version,
                LastError = NULL,
                DiagnosticID = NULL,
                LeaseOwner = NULL,
                LeaseUntil = NULL
            WHERE OutboxEventID = @event;
            """,
            branch,
            transaction))
        {
            command.Parameters.Add("@version", SqlDbType.BigInt).Value = written.Version;
            command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = claimed.OutboxEventId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // قد يحمل المركز مفاتيح أحدث (من موظف المركز) لم تصل بعد؛ الدمج يحفظ الأحدث لكل مفتاح.
        var row = await ReadLocalRowAsync(branch, transaction, claimed.DeviceId, lockRow: true, cancellationToken);
        if (row is not null)
        {
            var local = PosDeviceSettingsDocument.Parse(PosDeviceSettingsDocument.ReadFromConfiguration(row.ConfigurationJson));
            var merged = PosDeviceSettingsDocument.Merge(local, written.Settings);
            await using var update = new SqlCommand(
                """
                UPDATE dbo.POSDeviceConfigurations
                SET ConfigurationJson = @json,
                    ConfigVersion = CASE WHEN ConfigurationJson = @json THEN ConfigVersion ELSE ConfigVersion + 1 END,
                    SettingsCentralVersion = CASE WHEN SettingsCentralVersion > @version THEN SettingsCentralVersion ELSE @version END,
                    SettingsSyncedAtUtc = SYSUTCDATETIME()
                WHERE DeviceID = @device;
                """,
                branch,
                transaction);
            update.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value =
                PosDeviceSettingsDocument.WriteIntoConfiguration(row.ConfigurationJson, merged);
            update.Parameters.Add("@version", SqlDbType.BigInt).Value = written.Version;
            update.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = claimed.DeviceId;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertOutboxAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid eventId,
        PosDeviceContext device,
        IReadOnlyDictionary<string, PosDeviceSettingEntry> changes,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.BranchDeviceSettingsOutbox
                (OutboxEventID, DeviceID, BranchID, ChangedKeys, Payload, CreatedByUserID, CreatedByName, Status)
            VALUES (@event, @device, @branch, @keys, @payload, @user, @name, N'Pending');
            """,
            connection,
            transaction);
        command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = eventId;
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = device.DeviceId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = device.BranchId;
        command.Parameters.Add("@keys", SqlDbType.NVarChar, 400).Value = Limit(string.Join(",", changes.Keys), 400);
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = PosDeviceSettingsDocument.Serialize(changes);
        command.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = device.UserId;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 150).Value = (object?)Limit(device.DisplayName, 150) ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> PullAsync(
        SqlConnection branch,
        SqlEndpointSettings central,
        Guid endpointId,
        CancellationToken cancellationToken)
    {
        var checkpoint = await ReadCheckpointAsync(branch, endpointId, cancellationToken);
        var changes = new List<(long Sequence, string? Payload)>();
        await using (var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName)))
        {
            await remote.OpenAsync(cancellationToken);
            await using var read = new SqlCommand(
                """
                SELECT TOP (500) ChangeSequence, Payload
                FROM dbo.SyncChangeLog
                WHERE EntityType = @entity AND ChangeSequence > @checkpoint
                ORDER BY ChangeSequence;
                """,
                remote);
            read.Parameters.Add("@entity", SqlDbType.NVarChar, 64).Value = EntityType;
            read.Parameters.Add("@checkpoint", SqlDbType.BigInt).Value = checkpoint;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                changes.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        var applied = 0;
        foreach (var change in changes)
        {
            var payload = ReadPayload(change.Payload);
            if (payload is not null
                && await ApplyCentralPayloadAsync(branch, payload, (endpointId, change.Sequence), cancellationToken) == ApplyOutcome.Applied)
            {
                applied++;
            }

            checkpoint = change.Sequence;
        }

        if (changes.Count > 0)
        {
            await SaveCheckpointAsync(branch, endpointId, checkpoint, cancellationToken);
        }

        return applied;
    }

    private enum ApplyOutcome
    {
        NotThisBranch,
        NoChange,
        Applied,
    }

    /// <summary>
    /// يطبّق نسخة المركز على هذا الفرع إن كان الجهاز من أجهزته، ويسجّل ذلك في DeviceSettingsInbox.
    /// التطبيق المتكرر آمن.
    /// </summary>
    private static async Task<ApplyOutcome> ApplyCentralPayloadAsync(
        SqlConnection branch,
        CentralSettingsPayload payload,
        (Guid EndpointId, long Sequence)? source,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
        var known = await LocalDeviceAsync(branch, transaction, payload.DeviceId, cancellationToken);
        if (known is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApplyOutcome.NotThisBranch;
        }

        var row = await ReadLocalRowAsync(branch, transaction, payload.DeviceId, lockRow: true, cancellationToken);
        var local = PosDeviceSettingsDocument.Parse(PosDeviceSettingsDocument.ReadFromConfiguration(row?.ConfigurationJson));
        var merged = PosDeviceSettingsDocument.Merge(local, payload.Settings);
        var version = Math.Max(row?.CentralVersion ?? 0, payload.Version);
        var outcome = row is not null
            && PosDeviceSettingsDocument.SameContent(merged, local)
            && row.CentralVersion == version
                ? ApplyOutcome.NoChange
                : ApplyOutcome.Applied;

        if (outcome == ApplyOutcome.Applied)
        {
            await UpsertLocalAsync(
                branch,
                transaction,
                payload.DeviceId,
                known.Value.BranchId,
                known.Value.BusinessId,
                row,
                merged,
                version,
                updatedBy: row?.UpdatedBy,
                cancellationToken);
        }

        if (source is { } from)
        {
            await using var inbox = new SqlCommand(
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.DeviceSettingsInbox WHERE EndpointID = @endpoint AND ChangeSequence = @sequence)
                    INSERT INTO dbo.DeviceSettingsInbox (EndpointID, ChangeSequence, DeviceID, CentralVersion, Outcome)
                    VALUES (@endpoint, @sequence, @device, @version, @outcome);
                """,
                branch,
                transaction);
            inbox.Parameters.Add("@endpoint", SqlDbType.UniqueIdentifier).Value = from.EndpointId;
            inbox.Parameters.Add("@sequence", SqlDbType.BigInt).Value = from.Sequence;
            inbox.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = payload.DeviceId;
            inbox.Parameters.Add("@version", SqlDbType.BigInt).Value = payload.Version;
            inbox.Parameters.Add("@outcome", SqlDbType.NVarChar, 20).Value = outcome == ApplyOutcome.Applied ? "Applied" : "NoChange";
            await inbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    // ------------------------------------------------------------------
    // الإدارة المركزية: أي نقطة في أي فرع
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<CentralPosDeviceSummary>> ListCentralAsync(CancellationToken cancellationToken)
    {
        var centralConnection = await RequireCentralConnectionStringAsync(cancellationToken);
        await using var remote = new SqlConnection(centralConnection);
        await remote.OpenAsync(cancellationToken);
        await EnsureCentralTablesAsync(remote, null, cancellationToken);
        var hasRegistry = await TableExistsAsync(remote, null, "dbo.CentralPosRegistry", cancellationToken);

        // نقاط لها إعدادات محفوظة + نقاط مسجّلة مركزياً بلا إعدادات بعد (إن وُجد جدول التسجيل).
        var sql = hasRegistry
            ? """
              SELECT COALESCE(s.DeviceID, r.DeviceID) AS DeviceID,
                     COALESCE(s.BranchID, r.BranchID) AS BranchID,
                     b.BranchName,
                     COALESCE(r.PosNumber, s.PosNumber) AS PosNumber,
                     COALESCE(s.DeviceName, r.PosName) AS DeviceName,
                     s.SettingsJson, ISNULL(s.SettingsVersion, 0) AS SettingsVersion, s.UpdatedAtUtc, s.UpdatedSource
              FROM dbo.CentralPosDeviceSettings AS s
              FULL OUTER JOIN (SELECT DeviceID, BranchID, PosNumber, PosName FROM dbo.CentralPosRegistry
                               WHERE DeviceID IS NOT NULL AND Status = N'Active') AS r
                  ON r.DeviceID = s.DeviceID
              LEFT JOIN dbo.Branches AS b ON b.BranchID = COALESCE(s.BranchID, r.BranchID)
              ORDER BY 3, 4, 5;
              """
            : """
              SELECT s.DeviceID, s.BranchID, b.BranchName, s.PosNumber, s.DeviceName,
                     s.SettingsJson, s.SettingsVersion, s.UpdatedAtUtc, s.UpdatedSource
              FROM dbo.CentralPosDeviceSettings AS s
              LEFT JOIN dbo.Branches AS b ON b.BranchID = s.BranchID
              ORDER BY 3, 4, 5;
              """;
        await using var command = new SqlCommand(sql, remote);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CentralPosDeviceSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var settings = PosDeviceSettingsDocument.Parse(reader.IsDBNull(5) ? null : reader.GetString(5));
            result.Add(new CentralPosDeviceSummary(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                PosDeviceSettingsDocument.Effective(settings),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return result;
    }

    public async Task<CentralPosDeviceSummary> SaveCentralAsync(
        Guid actorUserId,
        string? actorName,
        Guid deviceId,
        IReadOnlyDictionary<string, JsonElement>? values,
        CancellationToken cancellationToken)
    {
        var changes = PosDeviceSettingsDocument.BuildChanges(
            values,
            DateTime.UtcNow,
            actorUserId,
            actorName,
            PosDeviceSettingSources.Central);
        var centralConnection = await RequireCentralConnectionStringAsync(cancellationToken);

        CentralWriteResult written;
        await using (var remote = new SqlConnection(centralConnection))
        {
            await remote.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await remote.BeginTransactionAsync(cancellationToken);
            await EnsureCentralTablesAsync(remote, transaction, cancellationToken);
            written = await MergeIntoCentralAsync(
                remote,
                transaction,
                deviceId,
                branchId: null,
                deviceName: null,
                changes,
                PosDeviceSettingSources.Central,
                actorUserId,
                actorName,
                outboxEventId: null,
                requireKnownDevice: true,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "POS device settings changed centrally for {DeviceId} by {UserId}. Version {Version}.",
            deviceId,
            actorUserId,
            written.Version);

        // إن كانت النقطة على هذا الفرع نفسه نطبّق فوراً، وإلا يسحبها فرعها خلال دقيقة.
        try
        {
            await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
            await branch.OpenAsync(cancellationToken);
            if (await BranchTablesExistAsync(branch, cancellationToken))
            {
                await ApplyCentralPayloadAsync(
                    branch,
                    new CentralSettingsPayload(deviceId, written.BranchId, written.Version, written.Settings),
                    source: null,
                    cancellationToken);
            }
        }
        catch (SqlException exception)
        {
            logger.LogWarning(exception, "Immediate local apply of central POS settings failed for {DeviceId}.", deviceId);
        }

        var summaries = await ListCentralAsync(cancellationToken);
        return summaries.FirstOrDefault(summary => summary.DeviceId == deviceId)
            ?? new CentralPosDeviceSummary(
                deviceId,
                written.BranchId,
                null,
                written.PosNumber,
                written.DeviceName,
                PosDeviceSettingsDocument.Effective(written.Settings),
                written.Version,
                DateTime.UtcNow,
                PosDeviceSettingSources.Central);
    }

    public async Task<IReadOnlyList<DeviceSettingsAuditEntry>> GetCentralHistoryAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var centralConnection = await RequireCentralConnectionStringAsync(cancellationToken);
        await using var remote = new SqlConnection(centralConnection);
        await remote.OpenAsync(cancellationToken);
        await EnsureCentralTablesAsync(remote, null, cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT TOP (100) SettingKey, BeforeValue, AfterValue, Source, ActorName, OutboxEventID, SettingsVersion, OccurredAt
            FROM dbo.PosDeviceSettingsAudit
            WHERE DeviceID = @device
            ORDER BY OccurredAt DESC, SettingsVersion DESC;
            """,
            remote);
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
        var result = new List<DeviceSettingsAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            result.Add(new DeviceSettingsAuditEntry(
                key,
                PosDeviceSettingsCatalog.Find(key)?.Label,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.GetInt64(6),
                reader.GetDateTime(7)));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // كتابة المركز (مشتركة بين ترحيل الفرع وتعديل موظف المركز)
    // ------------------------------------------------------------------

    private sealed record CentralWriteResult(
        Guid BranchId,
        int? PosNumber,
        string? DeviceName,
        long Version,
        SortedDictionary<string, PosDeviceSettingEntry> Settings);

    /// <summary>إن كان هذا الترحيل قد وصل سابقاً (إعادة محاولة بعد انقطاع)، نعيد الحالة الحالية دون تكرار.</summary>
    private static async Task<CentralWriteResult?> ReceiptAsync(
        SqlConnection remote,
        SqlTransaction transaction,
        ClaimedEvent claimed,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT s.BranchID, s.PosNumber, s.DeviceName, s.SettingsJson, s.SettingsVersion
            FROM dbo.PosDeviceSettingsReceipts AS r
            INNER JOIN dbo.CentralPosDeviceSettings AS s ON s.DeviceID = r.DeviceID
            WHERE r.OutboxEventID = @event;
            """,
            remote,
            transaction);
        command.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = claimed.OutboxEventId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CentralWriteResult(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(4),
            PosDeviceSettingsDocument.Parse(reader.GetString(3)));
    }

    private static async Task<CentralWriteResult> MergeIntoCentralAsync(
        SqlConnection remote,
        SqlTransaction transaction,
        Guid deviceId,
        Guid? branchId,
        string? deviceName,
        IReadOnlyDictionary<string, PosDeviceSettingEntry> incoming,
        string source,
        Guid? actorUserId,
        string? actorName,
        Guid? outboxEventId,
        bool requireKnownDevice,
        CancellationToken cancellationToken)
    {
        Guid? existingBranch = null;
        int? existingPos = null;
        string? existingName = null;
        string? existingJson = null;
        long existingVersion = 0;
        var exists = false;
        await using (var read = new SqlCommand(
            """
            SELECT BranchID, PosNumber, DeviceName, SettingsJson, SettingsVersion
            FROM dbo.CentralPosDeviceSettings WITH (UPDLOCK, HOLDLOCK)
            WHERE DeviceID = @device;
            """,
            remote,
            transaction))
        {
            read.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                exists = true;
                existingBranch = reader.GetGuid(0);
                existingPos = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                existingName = reader.IsDBNull(2) ? null : reader.GetString(2);
                existingJson = reader.GetString(3);
                existingVersion = reader.GetInt64(4);
            }
        }

        var registry = await RegistryEntryAsync(remote, transaction, deviceId, cancellationToken);
        var resolvedBranch = branchId ?? existingBranch ?? registry?.BranchId;
        if (resolvedBranch is null)
        {
            if (requireKnownDevice)
            {
                throw new PosDeviceSettingsException("هذه النقطة غير معروفة في الإدارة المركزية.", StatusCodes.Status404NotFound);
            }

            throw new InvalidOperationException("Branch of the POS device is unknown.");
        }

        var current = PosDeviceSettingsDocument.Parse(existingJson);
        var merged = PosDeviceSettingsDocument.Merge(current, incoming);
        var posNumber = registry?.PosNumber ?? existingPos;
        var name = deviceName ?? existingName ?? registry?.PosName;
        var unchanged = exists
            && PosDeviceSettingsDocument.SameContent(merged, current)
            && existingBranch == resolvedBranch
            && existingPos == posNumber
            && string.Equals(existingName, name, StringComparison.Ordinal);
        var version = unchanged ? existingVersion : existingVersion + 1;

        if (!unchanged)
        {
            await using (var write = new SqlCommand(
                exists
                    ? """
                      UPDATE dbo.CentralPosDeviceSettings
                      SET BranchID = @branch, PosNumber = @pos, DeviceName = @name, SettingsJson = @json,
                          SettingsVersion = @version, UpdatedAtUtc = SYSUTCDATETIME(),
                          UpdatedByUserID = @user, UpdatedSource = @source
                      WHERE DeviceID = @device;
                      """
                    : """
                      INSERT INTO dbo.CentralPosDeviceSettings
                          (DeviceID, BranchID, PosNumber, DeviceName, SettingsJson, SettingsVersion, UpdatedAtUtc, UpdatedByUserID, UpdatedSource)
                      VALUES (@device, @branch, @pos, @name, @json, @version, SYSUTCDATETIME(), @user, @source);
                      """,
                remote,
                transaction))
            {
                write.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
                write.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = resolvedBranch.Value;
                write.Parameters.Add("@pos", SqlDbType.Int).Value = (object?)posNumber ?? DBNull.Value;
                write.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = (object?)Limit(name, 200) ?? DBNull.Value;
                write.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = PosDeviceSettingsDocument.Serialize(merged);
                write.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
                write.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = (object?)actorUserId ?? DBNull.Value;
                write.Parameters.Add("@source", SqlDbType.NVarChar, 20).Value = source;
                await write.ExecuteNonQueryAsync(cancellationToken);
            }

            // تدقيق: فقط الإعدادات التي تغيّرت قيمتها الفعلية.
            var before = PosDeviceSettingsDocument.Effective(current).ToDictionary(value => value.Key, StringComparer.Ordinal);
            var after = PosDeviceSettingsDocument.Effective(merged).ToDictionary(value => value.Key, StringComparer.Ordinal);
            foreach (var key in incoming.Keys)
            {
                var beforeText = before.TryGetValue(key, out var b) ? b.Value.ToJsonString() : null;
                var afterText = after.TryGetValue(key, out var a) ? a.Value.ToJsonString() : null;
                if (string.Equals(beforeText, afterText, StringComparison.Ordinal))
                {
                    continue;
                }

                await using var audit = new SqlCommand(
                    """
                    INSERT INTO dbo.PosDeviceSettingsAudit
                        (AuditID, DeviceID, BranchID, SettingKey, BeforeValue, AfterValue, Source, ActorUserID, ActorName, OutboxEventID, SettingsVersion)
                    VALUES (NEWID(), @device, @branch, @key, @before, @after, @source, @user, @name, @event, @version);
                    """,
                    remote,
                    transaction);
                audit.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
                audit.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = resolvedBranch.Value;
                audit.Parameters.Add("@key", SqlDbType.NVarChar, 100).Value = key;
                audit.Parameters.Add("@before", SqlDbType.NVarChar, 400).Value = (object?)Limit(beforeText, 400) ?? DBNull.Value;
                audit.Parameters.Add("@after", SqlDbType.NVarChar, 400).Value = (object?)Limit(afterText, 400) ?? DBNull.Value;
                audit.Parameters.Add("@source", SqlDbType.NVarChar, 20).Value = source;
                audit.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = (object?)actorUserId ?? DBNull.Value;
                audit.Parameters.Add("@name", SqlDbType.NVarChar, 150).Value = (object?)Limit(actorName, 150) ?? DBNull.Value;
                audit.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = (object?)outboxEventId ?? DBNull.Value;
                audit.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
                await audit.ExecuteNonQueryAsync(cancellationToken);
            }

            var payload = new JsonObject
            {
                ["deviceId"] = deviceId.ToString("D"),
                ["branchId"] = resolvedBranch.Value.ToString("D"),
                ["posNumber"] = posNumber,
                ["deviceName"] = name,
                ["version"] = version,
                ["settings"] = PosDeviceSettingsDocument.ToObject(merged),
            };
            await using var log = new SqlCommand(
                """
                INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload)
                VALUES (@entity, @device, N'Upsert', @payload);
                """,
                remote,
                transaction);
            log.Parameters.Add("@entity", SqlDbType.NVarChar, 64).Value = EntityType;
            log.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
            log.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = payload.ToJsonString();
            await log.ExecuteNonQueryAsync(cancellationToken);
        }

        if (outboxEventId is Guid eventId)
        {
            await using var receipt = new SqlCommand(
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.PosDeviceSettingsReceipts WHERE OutboxEventID = @event)
                    INSERT INTO dbo.PosDeviceSettingsReceipts (OutboxEventID, DeviceID, BranchID, SettingsVersion)
                    VALUES (@event, @device, @branch, @version);
                """,
                remote,
                transaction);
            receipt.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = eventId;
            receipt.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
            receipt.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = resolvedBranch.Value;
            receipt.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }

        return new CentralWriteResult(resolvedBranch.Value, posNumber, name, version, merged);
    }

    private static async Task<(Guid BranchId, int PosNumber, string PosName)?> RegistryEntryAsync(
        SqlConnection remote,
        SqlTransaction transaction,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(remote, transaction, "dbo.CentralPosRegistry", cancellationToken))
        {
            return null;
        }

        await using var command = new SqlCommand(
            """
            SELECT TOP (1) BranchID, PosNumber, PosName
            FROM dbo.CentralPosRegistry
            WHERE DeviceID = @device AND Status = N'Active'
            ORDER BY PosNumber;
            """,
            remote,
            transaction);
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2));
    }

    // ------------------------------------------------------------------
    // قاعدة الفرع
    // ------------------------------------------------------------------

    private sealed record LocalRow(
        Guid BranchId,
        int? PosNumber,
        string? DeviceName,
        string ConfigurationJson,
        string? UpdatedBy,
        long? CentralVersion,
        DateTime? SyncedAtUtc);

    private static async Task<LocalRow?> ReadLocalRowAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid deviceId,
        bool lockRow,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            $"""
            SELECT BranchID, POSNumber, DeviceName, ConfigurationJson, UpdatedBy,
                   SettingsCentralVersion, SettingsSyncedAtUtc
            FROM dbo.POSDeviceConfigurations {(lockRow ? "WITH (UPDLOCK, HOLDLOCK)" : "")}
            WHERE DeviceID = @device;
            """,
            connection,
            transaction);
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LocalRow(
            reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6));
    }

    private static async Task UpsertLocalAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid deviceId,
        Guid branchId,
        Guid? businessId,
        LocalRow? row,
        IReadOnlyDictionary<string, PosDeviceSettingEntry> settings,
        long? centralVersion,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        var json = PosDeviceSettingsDocument.WriteIntoConfiguration(row?.ConfigurationJson, settings);
        await using var command = new SqlCommand(
            row is not null
                ? """
                  UPDATE dbo.POSDeviceConfigurations
                  SET ConfigurationJson = @json,
                      BranchID = ISNULL(BranchID, @branch),
                      ConfigVersion = ConfigVersion + 1,
                      UpdatedAtUtc = SYSUTCDATETIME(),
                      UpdatedBy = @updatedBy,
                      SettingsCentralVersion = ISNULL(@centralVersion, SettingsCentralVersion),
                      SettingsSyncedAtUtc = CASE WHEN @centralVersion IS NULL THEN SettingsSyncedAtUtc ELSE SYSUTCDATETIME() END
                  WHERE DeviceID = @device;
                  """
                : """
                  INSERT INTO dbo.POSDeviceConfigurations
                      (DeviceID, BranchID, OrganizationID, DeviceName, ConfigurationJson, SchemaVersion, ConfigVersion,
                       IsSetupCompleted, UpdatedAtUtc, UpdatedBy, SettingsCentralVersion, SettingsSyncedAtUtc)
                  SELECT @device, @branch, @business, d.DeviceName, @json, @schema, 1,
                         1, SYSUTCDATETIME(), @updatedBy, @centralVersion,
                         CASE WHEN @centralVersion IS NULL THEN NULL ELSE SYSUTCDATETIME() END
                  FROM dbo.Devices AS d
                  WHERE d.DeviceID = @device;
                  """,
            connection,
            transaction);
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = (object?)businessId ?? DBNull.Value;
        command.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = json;
        command.Parameters.Add("@schema", SqlDbType.Int).Value = DeviceConfigDocument.SchemaVersion;
        command.Parameters.Add("@updatedBy", SqlDbType.NVarChar, 100).Value = (object?)Limit(updatedBy, 100) ?? DBNull.Value;
        command.Parameters.Add("@centralVersion", SqlDbType.BigInt).Value = (object?)centralVersion ?? DBNull.Value;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PosDeviceSettingsException("هذا الجهاز غير مسجّل في قاعدة الفرع.", StatusCodes.Status404NotFound);
        }
    }

    private async Task<PosDeviceSettingsView> ReadLocalViewAsync(
        SqlConnection connection,
        Guid deviceId,
        Guid branchId,
        bool linked,
        CancellationToken cancellationToken)
    {
        var row = await ReadLocalRowAsync(connection, null, deviceId, lockRow: false, cancellationToken);
        var settings = PosDeviceSettingsDocument.Parse(PosDeviceSettingsDocument.ReadFromConfiguration(row?.ConfigurationJson));

        var pending = 0;
        var reconciliation = 0;
        string? lastError = null;
        string? diagnostic = null;
        await using (var command = new SqlCommand(
            """
            SELECT
                SUM(CASE WHEN Status = N'Pending' THEN 1 ELSE 0 END),
                SUM(CASE WHEN Status = N'ReconciliationRequired' THEN 1 ELSE 0 END),
                (SELECT TOP (1) LastError FROM dbo.BranchDeviceSettingsOutbox
                 WHERE DeviceID = @device AND Status <> N'Completed' AND LastError IS NOT NULL ORDER BY LastAttemptAt DESC),
                (SELECT TOP (1) DiagnosticID FROM dbo.BranchDeviceSettingsOutbox
                 WHERE DeviceID = @device AND Status <> N'Completed' AND DiagnosticID IS NOT NULL ORDER BY LastAttemptAt DESC)
            FROM dbo.BranchDeviceSettingsOutbox
            WHERE DeviceID = @device AND Status <> N'Completed';
            """,
            connection))
        {
            command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                pending = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                reconciliation = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                lastError = reader.IsDBNull(2) ? null : reader.GetString(2);
                diagnostic = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
        }

        var state = !linked
            ? SyncLocalOnly
            : reconciliation > 0
                ? SyncReconciliation
                : pending > 0
                    ? SyncPending
                    : SyncCurrent;
        var branchName = await ScalarStringAsync(
            connection,
            "SELECT BranchName FROM dbo.Branches WHERE BranchID = @id;",
            branchId,
            cancellationToken);
        var deviceName = row?.DeviceName ?? await LocalDeviceNameAsync(connection, deviceId, cancellationToken);
        return new PosDeviceSettingsView(
            deviceId,
            branchId,
            branchName,
            row?.PosNumber,
            deviceName,
            PosDeviceSettingsDocument.Effective(settings),
            state,
            pending + reconciliation,
            linked ? lastError : null,
            linked ? diagnostic : null,
            row?.CentralVersion,
            row?.SyncedAtUtc);
    }

    private static async Task<(Guid BranchId, Guid? BusinessId)?> LocalDeviceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT d.BranchID, b.BusinessID
            FROM dbo.Devices AS d
            LEFT JOIN dbo.Branches AS b ON b.BranchID = d.BranchID
            WHERE d.DeviceID = @device;
            """,
            connection,
            transaction);
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    private static Task<string?> LocalDeviceNameAsync(SqlConnection connection, Guid deviceId, CancellationToken cancellationToken) =>
        ScalarStringAsync(connection, "SELECT DeviceName FROM dbo.Devices WHERE DeviceID = @id;", deviceId, cancellationToken);

    private static async Task<string?> ScalarStringAsync(SqlConnection connection, string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<long> ReadCheckpointAsync(SqlConnection connection, Guid endpointId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT LastAppliedVersion FROM dbo.SyncCheckpoints WHERE EndpointID = @endpoint AND StreamName = @stream;",
            connection);
        command.Parameters.Add("@endpoint", SqlDbType.UniqueIdentifier).Value = endpointId;
        command.Parameters.Add("@stream", SqlDbType.NVarChar, 64).Value = StreamName;
        return await command.ExecuteScalarAsync(cancellationToken) is long value ? value : 0;
    }

    private static async Task SaveCheckpointAsync(SqlConnection connection, Guid endpointId, long version, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            MERGE dbo.SyncCheckpoints AS target
            USING (SELECT @endpoint AS EndpointID, @stream AS StreamName) AS source
            ON target.EndpointID = source.EndpointID AND target.StreamName = source.StreamName
            WHEN MATCHED THEN UPDATE SET LastAppliedVersion = @version
            WHEN NOT MATCHED THEN INSERT (EndpointID, StreamName, LastAppliedVersion) VALUES (@endpoint, @stream, @version);
            """,
            connection);
        command.Parameters.Add("@endpoint", SqlDbType.UniqueIdentifier).Value = endpointId;
        command.Parameters.Add("@stream", SqlDbType.NVarChar, 64).Value = StreamName;
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ------------------------------------------------------------------
    // الاتصال بالمركز
    // ------------------------------------------------------------------

    private sealed record CentralLink(bool Linked, SqlEndpointSettings? Settings);

    /// <summary>
    /// هل الفرع مرتبط بالمركز؟ وإن كان، ما إعدادات الاتصال؟
    /// Linked = true مع Settings = null يعني أن الربط موجود لكن تعذر فك تشفير الاتصال؛
    /// الحفظ المحلي يستمر وتبقى الترحيلات Pending حتى يعود الاتصال.
    /// لا يُعتبر مرتبطاً إذا كانت هذه القاعدة هي المركز نفسه.
    /// </summary>
    private async Task<CentralLink> ResolveLinkedCentralAsync(SqlConnection branch, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(branch, null, "dbo.SyncEndpoints", cancellationToken)
            || string.Equals(await LocalRoleAsync(branch, cancellationToken), DatabaseRole.Central, StringComparison.Ordinal))
        {
            return new CentralLink(false, null);
        }

        var view = await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
        if (view is null || !string.Equals(view.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal))
        {
            return new CentralLink(false, null);
        }

        try
        {
            var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
            return settings is not null && string.Equals(settings.DatabaseRole, DatabaseRole.Central, StringComparison.Ordinal)
                ? new CentralLink(true, settings)
                : new CentralLink(true, null);
        }
        catch (CryptographicException exception)
        {
            logger.LogWarning(exception, "Central connection could not be decrypted for POS device settings.");
            return new CentralLink(true, null);
        }
    }

    /// <summary>للشاشة المركزية: قاعدة المركز سواء كان هذا الخادم هو المركز أو فرعاً مرتبطاً به.</summary>
    private async Task<string> RequireCentralConnectionStringAsync(CancellationToken cancellationToken)
    {
        await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        if (string.Equals(await LocalRoleAsync(branch, cancellationToken), DatabaseRole.Central, StringComparison.Ordinal))
        {
            return connections.CreateBranchConnectionString();
        }

        var link = await ResolveLinkedCentralAsync(branch, cancellationToken);
        if (!link.Linked)
        {
            throw new PosDeviceSettingsException("لا توجد إدارة مركزية مرتبطة بهذا الفرع.", StatusCodes.Status503ServiceUnavailable);
        }

        return link.Settings?.BuildConnectionString(link.Settings.DatabaseName)
            ?? throw new PosDeviceSettingsException("تعذر قراءة اتصال القاعدة المركزية.", StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<string?> LocalRoleAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';",
            connection);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task EnsureBranchTablesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await BranchTablesExistAsync(connection, cancellationToken))
        {
            throw new PosDeviceSettingsException(
                "قاعدة هذا الفرع لم تُحدَّث بعد لإعدادات نقاط البيع (Migration 033).",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static Task<bool> BranchTablesExistAsync(SqlConnection connection, CancellationToken cancellationToken) =>
        TableExistsAsync(connection, null, "dbo.BranchDeviceSettingsOutbox", cancellationToken);

    private static async Task EnsureCentralTablesAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "dbo.PosDeviceSettingsReceipts", cancellationToken))
        {
            throw new PosDeviceSettingsException(
                "القاعدة المركزية لم تُحدَّث بعد لإعدادات نقاط البيع (Migration 016).",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, SqlTransaction? transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(@name, N'U');", connection, transaction);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 256).Value = name;
        return await command.ExecuteScalarAsync(cancellationToken) is not (null or DBNull);
    }

    // ------------------------------------------------------------------

    private sealed record CentralSettingsPayload(
        Guid DeviceId,
        Guid BranchId,
        long Version,
        SortedDictionary<string, PosDeviceSettingEntry> Settings);

    private static CentralSettingsPayload? ReadPayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root
                || !Guid.TryParse(root["deviceId"]?.ToString(), out var deviceId)
                || !Guid.TryParse(root["branchId"]?.ToString(), out var branchId))
            {
                return null;
            }

            var version = root["version"] is JsonValue value && value.TryGetValue<long>(out var parsed) ? parsed : 0;
            return new CentralSettingsPayload(
                deviceId,
                branchId,
                version,
                PosDeviceSettingsDocument.FromObject(root["settings"] as JsonObject));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>أخطاء مؤقتة تُعاد محاولتها: الاتصال، SQL، المركز غير محدّث، تعذر فك تشفير الاتصال.</summary>
    private static bool IsTransient(Exception exception) =>
        exception is SqlException
            or InvalidOperationException
            or PosDeviceSettingsException
            or CryptographicException
            or TimeoutException
            or IOException;

    /// <summary>15ث، 30ث، 1د، 2د، 4د ... بحد أقصى 10 دقائق.</summary>
    public static TimeSpan Backoff(int attempt)
    {
        var seconds = 15 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, 600));
    }

    /// <summary>SetupLog.Fail يعيد «الرسالة رمز التشخيص: SETUP-XXXX»؛ نستخرج الرمز فقط.</summary>
    private static string? DiagnosticOf(string message)
    {
        var index = message.LastIndexOf("SETUP-", StringComparison.Ordinal);
        return index < 0 ? null : message[index..].Trim();
    }

    private static string? Limit(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
