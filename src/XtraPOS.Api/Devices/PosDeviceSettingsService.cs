using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Operations;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Devices;

/// <summary>نقطة بيع في أي فرع، كما تظهر في قائمة «القيم الأولية».</summary>
public sealed record PosPointInfo(Guid DeviceId, Guid BranchId, string? BranchName, int? PosNumber, string? DeviceName);

public sealed record PosPointSettings(
    Guid DeviceId,
    Guid BranchId,
    string? BranchName,
    int? PosNumber,
    string? DeviceName,
    bool IsCurrent,
    IReadOnlyList<PosDeviceSettingValue> Settings,
    long CentralVersion,
    DateTime? SyncedAtUtc,
    string? UpdatedSource);

public sealed record PosPointsList(IReadOnlyList<PosPointSettings> Points, bool CanManageAll, string? Notice);

public sealed record DeviceSettingsSaveRequest(
    string? Scope,
    Guid? DeviceId,
    PosPointInfo? Point,
    Dictionary<string, JsonElement>? Values);

public sealed record DeviceSettingsSaveResult(Guid OperationId, int Points, string Message);

/// <summary>سطر من سجل الترحيل الموجود (BranchOperationLog) يخص إعدادات النقاط.</summary>
public sealed record DeviceSettingsLogRow(
    Guid OperationId,
    string? ReferenceNumber,
    string Description,
    string Status,
    string? ResultMessage,
    int AttemptCount,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? ActorName,
    bool AllPoints);

/// <summary>محتوى عملية DeviceSettings في سجل الترحيل.</summary>
public sealed record DeviceSettingsOperation(
    string Scope,
    IReadOnlyList<PosPointInfo> Points,
    SortedDictionary<string, PosDeviceSettingEntry> Settings,
    Guid ActorUserId,
    string? ActorName);

/// <summary>
/// القيم الأولية لنقاط البيع.
///
/// الجدول: dbo.PosDeviceSettings على كل فرع، سطر لكل نقطة في كل الفروع، نسخة من CentralPosDeviceSettings على المركز.
///
/// من الفرع إلى المركز: الحفظ يكتب الجدول المحلي ويُدرج عملية DeviceSettings في سجل الترحيل الموجود
/// (BranchOperationLog) داخل المعاملة نفسها. CreditOutboxWorker ينفذها بالترتيب مع باقي العمليات عبر RelayLoggedAsync.
/// المركز يسجّل استلام كل عملية مرة واحدة في PosDeviceSettingsReceipts، ويكتب كل نقطة تغيّرت في SyncChangeLog.
///
/// من المركز إلى كل الفروع: PosDeviceSettingsSyncWorker يسحب PosDeviceSettings من SyncChangeLog كل دقيقة
/// ويطبّق كل النقاط، لا نقاط فرعه فقط، فيتطابق الجدول.
///
/// التعارض: الأحدث لكل إعداد على حدة (PosDeviceSettingsDocument.Merge).
/// </summary>
public sealed class PosDeviceSettingsService(
    SqlConnectionFactory connections,
    SyncEndpointStore endpoints,
    ILogger<PosDeviceSettingsService> logger)
{
    public const string EntityType = "PosDeviceSettings";
    public const string StreamName = "PosDeviceSettings";
    public const string OperationType = "DeviceSettings";
    public const string ScopeDevice = "Device";
    public const string ScopeAll = "All";

    // ------------------------------------------------------------------
    // القراءة على الفرع
    // ------------------------------------------------------------------

    public async Task<PosPointSettings> GetCurrentAsync(PosDeviceContext actor, CancellationToken cancellationToken)
    {
        await using var branch = await OpenBranchAsync(cancellationToken);
        var current = await CurrentPointAsync(branch, null, actor, cancellationToken);
        var row = await ReadRowAsync(branch, null, actor.DeviceId, cancellationToken);
        return ToView(current, row, isCurrent: true);
    }

    /// <summary>مهلة قراءة المركز عند فتح الشاشة؛ بعدها نعرض نسخة الفرع حتى لا يتوقف العمل.</summary>
    internal static readonly TimeSpan CentralReadTimeout = TimeSpan.FromSeconds(6);

    internal const string CentralUnreachableNotice =
        "تعذر الوصول إلى الإدارة المركزية. تظهر القيم المحفوظة على هذا الفرع، والحفظ يعمل ويُرحَّل تلقائيًا عند عودة الاتصال.";

    internal const string CentralNotLinkedNotice =
        "هذا الفرع غير مربوط بالإدارة المركزية بعد. تظهر القيم المحفوظة على هذا الفرع، والحفظ يعمل ويُرحَّل بعد الربط.";

    public async Task<PosPointsList> ListPointsAsync(PosDeviceContext actor, bool canManageAll, CancellationToken cancellationToken)
    {
        await using var branch = await OpenBranchAsync(cancellationToken);
        var current = await CurrentPointAsync(branch, null, actor, cancellationToken);

        // المركز أولًا: نقرأ نقاطه وقيمه ونحدّث نسخة الفرع، وعند الفشل نكمل بنسخة الفرع.
        var snapshot = await TryReadCentralAsync(canManageAll ? null : current.DeviceId, canManageAll, cancellationToken);
        if (snapshot.Settings.Count > 0)
        {
            await RefreshLocalFromCentralAsync(branch, current, snapshot.Settings, cancellationToken);
        }

        var rows = await ReadAllRowsAsync(branch, cancellationToken);
        if (!canManageAll)
        {
            rows.TryGetValue(actor.DeviceId, out var own);
            return new PosPointsList([ToView(current, own, isCurrent: true)], false, snapshot.Notice);
        }

        var points = new Dictionary<Guid, PosPointInfo>
        {
            [current.DeviceId] = current,
        };
        foreach (var row in rows.Values)
        {
            points.TryAdd(row.Point.DeviceId, row.Point);
        }

        foreach (var registered in snapshot.Points)
        {
            points[registered.DeviceId] = registered.DeviceId == current.DeviceId
                ? current with { BranchName = current.BranchName ?? registered.BranchName }
                : registered;
        }

        var list = points.Values
            .Select(point => ToView(point, rows.GetValueOrDefault(point.DeviceId), point.DeviceId == current.DeviceId))
            .OrderByDescending(point => point.IsCurrent)
            .ThenBy(point => point.BranchName, StringComparer.CurrentCulture)
            .ThenBy(point => point.PosNumber ?? int.MaxValue)
            .ThenBy(point => point.DeviceName, StringComparer.CurrentCulture)
            .ToArray();
        return new PosPointsList(list, true, snapshot.Notice);
    }

    private sealed record CentralSnapshot(
        IReadOnlyList<PosPointInfo> Points,
        IReadOnlyList<CentralPayload> Settings,
        string? Notice);

    /// <summary>
    /// يقرأ من المركز بمهلة قصيرة: قائمة النقاط (للمدير) وصفوف CentralPosDeviceSettings
    /// (كلها للمدير، أو صف هذا الجهاز فقط). لا يرمي عند انقطاع الاتصال بل يعيد ملاحظة.
    /// </summary>
    private async Task<CentralSnapshot> TryReadCentralAsync(Guid? onlyDevice, bool includePoints, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CentralReadTimeout);
        try
        {
            var central = await endpoints.ReadSettingsAsync(DatabaseRole.Central, timeout.Token);
            if (central is null)
            {
                return new CentralSnapshot([], [], CentralNotLinkedNotice);
            }

            var builder = new SqlConnectionStringBuilder(central.BuildConnectionString(central.DatabaseName))
            {
                ConnectTimeout = (int)CentralReadTimeout.TotalSeconds,
            };
            await using var remote = new SqlConnection(builder.ConnectionString);
            await remote.OpenAsync(timeout.Token);

            var points = includePoints
                ? await CentralPointListAsync(remote, null, timeout.Token)
                : (IReadOnlyList<PosPointInfo>)[];
            var settings = await CentralSettingsRowsAsync(remote, onlyDevice, timeout.Token);
            return new CentralSnapshot(points, settings, null);
        }
        catch (Exception exception) when (
            exception is SqlException or InvalidOperationException or TimeoutException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogInformation(exception, "Central initial values were not reachable; using the branch copy.");
            return new CentralSnapshot([], [], CentralUnreachableNotice);
        }
    }

    private static async Task<IReadOnlyList<CentralPayload>> CentralSettingsRowsAsync(SqlConnection remote, Guid? onlyDevice, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(remote, null, "dbo.CentralPosDeviceSettings", cancellationToken))
        {
            return [];
        }

        var hasBranches = await TableExistsAsync(remote, null, "dbo.Branches", cancellationToken);
        await using var command = new SqlCommand(
            $"""
            SELECT c.DeviceID, c.BranchID, {(hasBranches ? "b.BranchName" : "CAST(NULL AS nvarchar(200))")} AS BranchName,
                   c.PosNumber, c.DeviceName, c.SettingsJson, c.SettingsVersion
            FROM dbo.CentralPosDeviceSettings AS c
            {(hasBranches ? "LEFT JOIN dbo.Branches AS b ON b.BranchID = c.BranchID" : "")}
            WHERE @device IS NULL OR c.DeviceID = @device;
            """,
            remote);
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = (object?)onlyDevice ?? DBNull.Value;
        var result = new List<CentralPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var point = new PosPointInfo(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
            result.Add(new CentralPayload(point, reader.GetInt64(6), PosDeviceSettingsDocument.Parse(reader.IsDBNull(5) ? null : reader.GetString(5))));
        }

        return result;
    }

    /// <summary>
    /// يدمج قيم المركز في جدول الفرع (الأحدث لكل مفتاح يفوز)، فلا يضيع تعديل محلي
    /// لم يُرحَّل بعد؛ يبقى الصف «غير مُزامَن» حتى يصل تعديله إلى المركز.
    /// </summary>
    private async Task RefreshLocalFromCentralAsync(
        SqlConnection branch,
        PosPointInfo current,
        IReadOnlyList<CentralPayload> payloads,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var payload in payloads)
            {
                await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
                var row = await ReadRowAsync(branch, transaction, payload.Point.DeviceId, cancellationToken, lockRow: true);
                var local = row?.Settings ?? new SortedDictionary<string, PosDeviceSettingEntry>(StringComparer.Ordinal);
                var merged = PosDeviceSettingsDocument.Merge(local, payload.Settings);
                var version = Math.Max(row?.CentralVersion ?? 0, payload.Version);
                var matchesCentral = PosDeviceSettingsDocument.SameContent(merged, payload.Settings);
                var unchanged = row is not null
                    && PosDeviceSettingsDocument.SameContent(merged, local)
                    && row.CentralVersion == version
                    && (!matchesCentral || row.SyncedAtUtc is not null);
                if (!unchanged)
                {
                    var point = payload.Point.DeviceId == current.DeviceId
                        ? current with { BranchName = current.BranchName ?? payload.Point.BranchName }
                        : payload.Point with { BranchName = payload.Point.BranchName ?? row?.Point.BranchName };
                    await UpsertRowAsync(
                        branch,
                        transaction,
                        point,
                        merged,
                        version,
                        matchesCentral ? PosDeviceSettingSources.Central : PosDeviceSettingSources.Branch,
                        synced: matchesCentral,
                        cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (SqlException exception)
        {
            // فشل تحديث النسخة المحلية لا يمنع عرض الشاشة.
            logger.LogWarning(exception, "Could not refresh the branch copy of initial values from central.");
        }
    }

    public async Task<IReadOnlyList<DeviceSettingsLogRow>> HistoryAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using var branch = await OpenBranchAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT TOP (50) l.OperationID, l.ReferenceNumber, l.Description, l.Status, l.ResultMessage, l.AttemptCount,
                   l.CreatedAt, l.CompletedAt, u.DisplayName, l.SourceEntityID
            FROM dbo.BranchOperationLog AS l
            LEFT JOIN dbo.Users AS u ON u.UserID = l.ActorUserID
            WHERE l.OperationType = @type AND l.SourceEntityID IN (@device, @all)
            ORDER BY l.Serial DESC;
            """,
            branch);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 40).Value = OperationType;
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
        command.Parameters.Add("@all", SqlDbType.UniqueIdentifier).Value = Guid.Empty;
        var result = new List<DeviceSettingsLogRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DeviceSettingsLogRow(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetGuid(9) == Guid.Empty));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // الحفظ على الفرع + سجل الترحيل الموجود
    // ------------------------------------------------------------------

    public async Task<DeviceSettingsSaveResult> SaveAsync(
        PosDeviceContext actor,
        DeviceSettingsSaveRequest request,
        bool canManageAll,
        CancellationToken cancellationToken)
    {
        var changes = PosDeviceSettingsDocument.BuildChanges(
            request.Values,
            DateTime.UtcNow,
            actor.UserId,
            actor.DisplayName,
            PosDeviceSettingSources.Branch);
        var allPoints = string.Equals(request.Scope, ScopeAll, StringComparison.OrdinalIgnoreCase);
        var targetDevice = request.DeviceId ?? actor.DeviceId;
        if ((allPoints || targetDevice != actor.DeviceId) && !canManageAll)
        {
            throw new PosDeviceSettingsException("تعديل نقطة أخرى أو جميع النقاط متاح للمدير فقط.", StatusCodes.Status403Forbidden);
        }

        IReadOnlyList<PosPointInfo> targets;
        if (allPoints)
        {
            targets = (await ListPointsAsync(actor, canManageAll: true, cancellationToken)).Points
                .Select(point => new PosPointInfo(point.DeviceId, point.BranchId, point.BranchName, point.PosNumber, point.DeviceName))
                .ToArray();
        }
        else
        {
            targets = [];
        }

        var operationId = Guid.NewGuid();
        await using var branch = await OpenBranchAsync(cancellationToken);
        await using (var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken))
        {
            if (!allPoints)
            {
                PosPointInfo? known;
                if (targetDevice == actor.DeviceId)
                {
                    known = await CurrentPointAsync(branch, transaction, actor, cancellationToken);
                }
                else
                {
                    known = (await ReadRowAsync(branch, transaction, targetDevice, cancellationToken))?.Point;
                    if (known is null && request.Point is { } given && given.DeviceId == targetDevice && given.BranchId != Guid.Empty)
                    {
                        known = given;
                    }
                }

                if (known is null)
                {
                    throw new PosDeviceSettingsException("هذه النقطة غير معروفة. أعد فتح القائمة واخترها مرة أخرى.", StatusCodes.Status404NotFound);
                }

                targets = new[] { known };
            }

            foreach (var point in targets)
            {
                var row = await ReadRowAsync(branch, transaction, point.DeviceId, cancellationToken, lockRow: true);
                var merged = PosDeviceSettingsDocument.Merge(row?.Settings ?? new SortedDictionary<string, PosDeviceSettingEntry>(StringComparer.Ordinal), changes);
                await UpsertRowAsync(branch, transaction, point, merged, row?.CentralVersion ?? 0, PosDeviceSettingSources.Branch, synced: false, cancellationToken);
            }

            var operation = new DeviceSettingsOperation(allPoints ? ScopeAll : ScopeDevice, targets, changes, actor.UserId, actor.DisplayName);
            await BranchOperationLog.EnqueueAsync(
                branch,
                transaction,
                new BranchOperationDraft(
                    operationId,
                    OperationType,
                    SerializeOperation(operation),
                    Describe(operation),
                    EntityType,
                    allPoints ? Guid.Empty : targets[0].DeviceId,
                    actor.BranchId,
                    actor.UserId,
                    actor.StationId),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "POS initial values saved by {UserId} for {Count} point(s), scope {Scope}. Operation {OperationId}.",
            actor.UserId,
            targets.Count,
            allPoints ? ScopeAll : ScopeDevice,
            operationId);
        return new DeviceSettingsSaveResult(
            operationId,
            targets.Count,
            allPoints
                ? $"تم حفظ القيم الأولية لجميع النقاط ({targets.Count}) على هذا الفرع، وأُضيفت إلى سجل الترحيل."
                : "تم حفظ القيم الأولية لهذه النقطة، وأُضيفت إلى سجل الترحيل.");
    }

    public static string Describe(DeviceSettingsOperation operation)
    {
        var target = operation.Scope == ScopeAll
            ? "جميع النقاط"
            : operation.Points.Count == 1
                ? PointLabel(operation.Points[0])
                : $"{operation.Points.Count} نقاط";
        var values = string.Join("، ", operation.Settings.Select(pair =>
        {
            var label = PosDeviceSettingsCatalog.Find(pair.Key)?.Label ?? pair.Key;
            var text = pair.Value.Value is JsonValue value && value.TryGetValue<bool>(out var flag)
                ? flag ? "مفعّل" : "غير مفعّل"
                : pair.Value.Value?.ToJsonString() ?? "";
            return $"{label}: {text}";
        }));
        var description = $"القيم الأولية ({target}): {values}";
        return description.Length <= 400 ? description : description[..400];
    }

    /// <summary>«كاونتر 1 - Labtop»: اسم النقطة ثم الفرع.</summary>
    public static string PointLabel(PosPointInfo point)
    {
        var name = string.IsNullOrWhiteSpace(point.DeviceName)
            ? point.PosNumber is int pos ? $"نقطة {pos}" : "نقطة"
            : point.DeviceName.Trim();
        return string.IsNullOrWhiteSpace(point.BranchName) ? name : $"{name} - {point.BranchName.Trim()}";
    }

    public static string SerializeOperation(DeviceSettingsOperation operation)
    {
        var points = new JsonArray();
        foreach (var point in operation.Points)
        {
            points.Add(new JsonObject
            {
                ["deviceId"] = point.DeviceId.ToString("D"),
                ["branchId"] = point.BranchId.ToString("D"),
                ["branchName"] = point.BranchName,
                ["posNumber"] = point.PosNumber,
                ["deviceName"] = point.DeviceName,
            });
        }

        return new JsonObject
        {
            ["scope"] = operation.Scope,
            ["points"] = points,
            ["settings"] = PosDeviceSettingsDocument.ToObject(operation.Settings),
            ["actorUserId"] = operation.ActorUserId.ToString("D"),
            ["actorName"] = operation.ActorName,
        }.ToJsonString();
    }

    public static DeviceSettingsOperation? ParseOperation(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload) is JsonObject root ? ParseOperationObject(root) : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static DeviceSettingsOperation? ParseOperationObject(JsonObject root)
    {
        var scope = root["scope"]?.GetValue<string>() == ScopeAll ? ScopeAll : ScopeDevice;
        var points = new List<PosPointInfo>();
        if (root["points"] is JsonArray array)
        {
            foreach (var node in array.OfType<JsonObject>())
            {
                if (!Guid.TryParse(node["deviceId"]?.GetValue<string>(), out var deviceId)
                    || !Guid.TryParse(node["branchId"]?.GetValue<string>(), out var branchId))
                {
                    continue;
                }

                points.Add(new PosPointInfo(
                    deviceId,
                    branchId,
                    node["branchName"]?.GetValue<string>(),
                    node["posNumber"] is JsonValue pos && pos.TryGetValue<int>(out var number) ? number : null,
                    node["deviceName"]?.GetValue<string>()));
            }
        }

        var settings = PosDeviceSettingsDocument.FromObject(root["settings"] as JsonObject);
        if (settings.Count == 0 || (scope == ScopeDevice && points.Count == 0))
        {
            return null;
        }

        Guid.TryParse(root["actorUserId"]?.GetValue<string>(), out var actor);
        return new DeviceSettingsOperation(scope, points, settings, actor, root["actorName"]?.GetValue<string>());
    }

    // ------------------------------------------------------------------
    // تنفيذ رأس سجل الترحيل على المركز (يستدعيه CreditOutboxWorker)
    // ------------------------------------------------------------------

    public static async Task<string?> RelayLoggedAsync(SqlConnection central, SqlConnection branch, CancellationToken cancellationToken)
    {
        Guid operationId;
        Guid originBranch;
        string? payload;
        await using (var read = new SqlCommand(
            """
            SELECT TOP (1) OperationID, OriginBranchID, Payload
            FROM dbo.BranchOperationLog
            WHERE Status NOT IN (N'Completed', N'Cancelled')
              AND OperationType = @type
            ORDER BY Serial;
            """,
            branch))
        {
            read.Parameters.Add("@type", SqlDbType.NVarChar, 40).Value = OperationType;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            operationId = reader.GetGuid(0);
            originBranch = reader.GetGuid(1);
            payload = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        var operation = ParseOperation(payload);
        if (operation is null)
        {
            await BranchOperationLog.MarkAsync(branch, null, operationId, "Blocked", "محتوى عملية الإعدادات غير صالح.", cancellationToken);
            return "Blocked";
        }

        try
        {
            var affected = await ApplyOperationToCentralAsync(central, operationId, originBranch, operation, cancellationToken);
            await BranchOperationLog.MarkAsync(
                branch,
                null,
                operationId,
                "Completed",
                affected < 0 ? "وصلت هذه العملية إلى المركز سابقاً." : $"تم ترحيل القيم الأولية إلى المركز ({affected} نقطة).",
                cancellationToken);
            return "Completed";
        }
        catch (SqlException exception) when (exception.Number == 208)
        {
            await BranchOperationLog.MarkAsync(
                branch,
                null,
                operationId,
                "Blocked",
                "جدول القيم الأولية غير موجود على المركز بعد (الترحيل 035).",
                cancellationToken);
            return "Blocked";
        }
        catch (SqlException exception)
        {
            await using var note = new SqlCommand(
                """
                UPDATE dbo.BranchOperationLog
                SET ResultMessage = @message, LastAttemptAt = SYSUTCDATETIME()
                WHERE OperationID = @operation AND Status NOT IN (N'Completed', N'Cancelled');
                """,
                branch);
            note.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = SqlEndpointSettings.DescribeConnectionFailure(exception);
            note.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
            await note.ExecuteNonQueryAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>يعيد عدد النقاط التي كُتبت، أو -1 إذا وصلت العملية سابقاً.</summary>
    private static async Task<int> ApplyOperationToCentralAsync(
        SqlConnection central,
        Guid operationId,
        Guid originBranch,
        DeviceSettingsOperation operation,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
        await using (var seen = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.PosDeviceSettingsReceipts WITH (UPDLOCK, HOLDLOCK) WHERE OperationID = @operation;",
            central,
            transaction))
        {
            seen.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
            if (Convert.ToInt32(await seen.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return -1;
            }
        }

        var targets = operation.Points.ToDictionary(point => point.DeviceId);
        if (operation.Scope == ScopeAll)
        {
            foreach (var point in await CentralPointListAsync(central, transaction, cancellationToken))
            {
                targets.TryAdd(point.DeviceId, point);
            }
        }

        var written = 0;
        foreach (var point in targets.Values)
        {
            await MergeIntoCentralAsync(central, transaction, point, operation.Settings, PosDeviceSettingSources.Branch, operation.ActorUserId, operation.ActorName, operationId, cancellationToken);
            written++;
        }

        await using (var receipt = new SqlCommand(
            """
            INSERT INTO dbo.PosDeviceSettingsReceipts (OperationID, DeviceID, BranchID, AffectedDevices)
            VALUES (@operation, @device, @branch, @count);
            """,
            central,
            transaction))
        {
            receipt.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
            receipt.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = operation.Scope == ScopeAll || operation.Points.Count == 0
                ? Guid.Empty
                : operation.Points[0].DeviceId;
            receipt.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = originBranch;
            receipt.Parameters.Add("@count", SqlDbType.Int).Value = written;
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return written;
    }

    private static async Task MergeIntoCentralAsync(
        SqlConnection remote,
        SqlTransaction transaction,
        PosPointInfo point,
        IReadOnlyDictionary<string, PosDeviceSettingEntry> incoming,
        string source,
        Guid? actorUserId,
        string? actorName,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var exists = false;
        string? existingJson = null;
        long existingVersion = 0;
        await using (var read = new SqlCommand(
            "SELECT SettingsJson, SettingsVersion FROM dbo.CentralPosDeviceSettings WITH (UPDLOCK, HOLDLOCK) WHERE DeviceID = @device;",
            remote,
            transaction))
        {
            read.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = point.DeviceId;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                exists = true;
                existingJson = reader.GetString(0);
                existingVersion = reader.GetInt64(1);
            }
        }

        var current = PosDeviceSettingsDocument.Parse(existingJson);
        var merged = PosDeviceSettingsDocument.Merge(current, incoming);
        if (exists && PosDeviceSettingsDocument.SameContent(merged, current))
        {
            return;
        }

        var version = existingVersion + 1;
        await using (var write = new SqlCommand(
            exists
                ? """
                  UPDATE dbo.CentralPosDeviceSettings
                  SET BranchID = @branch, PosNumber = COALESCE(@pos, PosNumber), DeviceName = COALESCE(@name, DeviceName),
                      SettingsJson = @json, SettingsVersion = @version, UpdatedAtUtc = SYSUTCDATETIME(),
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
            write.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = point.DeviceId;
            write.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = point.BranchId;
            write.Parameters.Add("@pos", SqlDbType.Int).Value = (object?)point.PosNumber ?? DBNull.Value;
            write.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = (object?)Limit(point.DeviceName, 200) ?? DBNull.Value;
            write.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = PosDeviceSettingsDocument.Serialize(merged);
            write.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
            write.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = actorUserId is Guid user && user != Guid.Empty ? (object)user : DBNull.Value;
            write.Parameters.Add("@source", SqlDbType.NVarChar, 20).Value = source;
            await write.ExecuteNonQueryAsync(cancellationToken);
        }

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
                    (AuditID, DeviceID, BranchID, SettingKey, BeforeValue, AfterValue, Source, ActorUserID, ActorName, OperationID, SettingsVersion)
                VALUES (NEWID(), @device, @branch, @key, @before, @after, @source, @user, @name, @operation, @version);
                """,
                remote,
                transaction);
            audit.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = point.DeviceId;
            audit.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = point.BranchId;
            audit.Parameters.Add("@key", SqlDbType.NVarChar, 100).Value = key;
            audit.Parameters.Add("@before", SqlDbType.NVarChar, 400).Value = (object?)Limit(beforeText, 400) ?? DBNull.Value;
            audit.Parameters.Add("@after", SqlDbType.NVarChar, 400).Value = (object?)Limit(afterText, 400) ?? DBNull.Value;
            audit.Parameters.Add("@source", SqlDbType.NVarChar, 20).Value = source;
            audit.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = actorUserId is Guid user && user != Guid.Empty ? (object)user : DBNull.Value;
            audit.Parameters.Add("@name", SqlDbType.NVarChar, 150).Value = (object?)Limit(actorName, 150) ?? DBNull.Value;
            audit.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
            audit.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        var branchName = point.BranchName;
        await using (var name = new SqlCommand("SELECT BranchName FROM dbo.Branches WHERE BranchID = @branch;", remote, transaction))
        {
            name.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = point.BranchId;
            branchName = await name.ExecuteScalarAsync(cancellationToken) as string ?? branchName;
        }

        var payload = new JsonObject
        {
            ["deviceId"] = point.DeviceId.ToString("D"),
            ["branchId"] = point.BranchId.ToString("D"),
            ["branchName"] = branchName,
            ["posNumber"] = point.PosNumber,
            ["deviceName"] = point.DeviceName,
            ["version"] = version,
            ["settings"] = PosDeviceSettingsDocument.ToObject(merged),
        };
        await using var log = new SqlCommand(
            "INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload) VALUES (@entity, @device, N'Upsert', @payload);",
            remote,
            transaction);
        log.Parameters.Add("@entity", SqlDbType.NVarChar, 64).Value = EntityType;
        log.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = point.DeviceId;
        log.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = payload.ToJsonString();
        await log.ExecuteNonQueryAsync(cancellationToken);
    }

    // ------------------------------------------------------------------
    // من المركز إلى كل الفروع (يستدعيه PosDeviceSettingsSyncWorker)
    // ------------------------------------------------------------------

    public async Task<int> PullAsync(CancellationToken cancellationToken)
    {
        var branchConnection = connections.TryCreateBranchConnectionString();
        if (branchConnection is null)
        {
            return 0;
        }

        var central = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        var view = central is null ? null : await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
        if (central is null || view is null)
        {
            return 0;
        }

        await using var branch = new SqlConnection(branchConnection);
        await branch.OpenAsync(cancellationToken);
        if (!await TableExistsAsync(branch, null, "dbo.PosDeviceSettings", cancellationToken))
        {
            return 0;
        }

        var checkpoint = await ReadCheckpointAsync(branch, view.EndpointId, cancellationToken);
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
            if (ReadCentralPayload(change.Payload) is { } payload)
            {
                await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
                var row = await ReadRowAsync(branch, transaction, payload.Point.DeviceId, cancellationToken, lockRow: true);
                var local = row?.Settings ?? new SortedDictionary<string, PosDeviceSettingEntry>(StringComparer.Ordinal);
                var merged = PosDeviceSettingsDocument.Merge(local, payload.Settings);
                var version = Math.Max(row?.CentralVersion ?? 0, payload.Version);
                await UpsertRowAsync(branch, transaction, payload.Point, merged, version, PosDeviceSettingSources.Central, synced: true, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                applied++;
            }

            checkpoint = change.Sequence;
        }

        if (changes.Count > 0)
        {
            await SaveCheckpointAsync(branch, view.EndpointId, checkpoint, cancellationToken);
        }

        return applied;
    }

    private sealed record CentralPayload(PosPointInfo Point, long Version, SortedDictionary<string, PosDeviceSettingEntry> Settings);

    private static CentralPayload? ReadCentralPayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root
                || !Guid.TryParse(root["deviceId"]?.GetValue<string>(), out var deviceId)
                || !Guid.TryParse(root["branchId"]?.GetValue<string>(), out var branchId))
            {
                return null;
            }

            var version = root["version"] is JsonValue v && v.TryGetValue<long>(out var parsed) ? parsed : 0;
            var pos = root["posNumber"] is JsonValue p && p.TryGetValue<int>(out var number) ? number : (int?)null;
            return new CentralPayload(
                new PosPointInfo(deviceId, branchId, root["branchName"]?.GetValue<string>(), pos, root["deviceName"]?.GetValue<string>()),
                version,
                PosDeviceSettingsDocument.FromObject(root["settings"] as JsonObject));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // قائمة النقاط في المركز
    // ------------------------------------------------------------------

    /// <summary>
    /// نقاط البيع في كل الفروع كما يعرفها المركز.
    /// المصدر الأول CentralPosRegistry (سجل حجز الأرقام): صفوف Active المربوطة بجهاز، ورقمها PosNumber.
    /// السجل قد يكون فارغاً لنقاط أُعدّت قبله، فتُضاف أجهزة dbo.Devices من نوع POS الفعّالة وغير الملغاة
    /// (تُنسخ إلى المركز مع ترحيل البيعات) ورقمها من POSStations، إلا جهازاً ألغي في السجل (Revoked).
    /// </summary>
    private static async Task<IReadOnlyList<PosPointInfo>> CentralPointListAsync(SqlConnection remote, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        var hasRegistry = await TableExistsAsync(remote, transaction, "dbo.CentralPosRegistry", cancellationToken);
        if (hasRegistry)
        {
            parts.Add("SELECT DeviceID, BranchID, PosNumber, PosName AS DeviceName FROM dbo.CentralPosRegistry WHERE DeviceID IS NOT NULL AND Status = N'Active'");
        }

        if (await TableExistsAsync(remote, transaction, "dbo.Devices", cancellationToken))
        {
            var hasStations = await TableExistsAsync(remote, transaction, "dbo.POSStations", cancellationToken);
            parts.Add($"""
                SELECT d.DeviceID, d.BranchID, {(hasStations ? "s.StationNumber" : "CAST(NULL AS int)")} AS PosNumber,
                       {(hasStations ? "COALESCE(NULLIF(LTRIM(RTRIM(s.StationName)), N''), d.DeviceName)" : "d.DeviceName")} AS DeviceName
                FROM dbo.Devices AS d
                {(hasStations ? "LEFT JOIN dbo.POSStations AS s ON s.StationID = d.StationID" : "")}
                WHERE d.DeviceType = N'POS' AND d.IsActive = 1 AND d.RevokedAt IS NULL
                {(hasRegistry ? "AND NOT EXISTS (SELECT 1 FROM dbo.CentralPosRegistry AS r WHERE r.DeviceID = d.DeviceID AND r.Status <> N'Active')" : "")}
                """);
        }

        if (parts.Count == 0)
        {
            return [];
        }

        await using var command = new SqlCommand(
            $"""
            SELECT p.DeviceID, MIN(CAST(p.BranchID AS nvarchar(36))) AS BranchID, MIN(b.BranchName) AS BranchName,
                   MIN(p.PosNumber) AS PosNumber, MIN(p.DeviceName) AS DeviceName
            FROM ({string.Join(" UNION ALL ", parts)}) AS p
            LEFT JOIN dbo.Branches AS b ON b.BranchID = p.BranchID
            GROUP BY p.DeviceID;
            """,
            remote,
            transaction);
        var result = new List<PosPointInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PosPointInfo(
                reader.GetGuid(0),
                Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // جدول الفرع dbo.PosDeviceSettings
    // ------------------------------------------------------------------

    private sealed record LocalRow(PosPointInfo Point, SortedDictionary<string, PosDeviceSettingEntry> Settings, long CentralVersion, DateTime? SyncedAtUtc, string UpdatedSource);

    private async Task<SqlConnection> OpenBranchAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        if (!await TableExistsAsync(connection, null, "dbo.PosDeviceSettings", cancellationToken))
        {
            await connection.DisposeAsync();
            throw new PosDeviceSettingsException("قاعدة هذا الفرع لم تُحدَّث بعد للقيم الأولية (الترحيل 050).", StatusCodes.Status503ServiceUnavailable);
        }

        return connection;
    }

    private static async Task<PosPointInfo> CurrentPointAsync(SqlConnection branch, SqlTransaction? transaction, PosDeviceContext actor, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT b.BranchName, st.StationNumber, COALESCE(NULLIF(LTRIM(RTRIM(st.StationName)), N''), d.DeviceName)
            FROM dbo.Devices AS d
            LEFT JOIN dbo.Branches AS b ON b.BranchID = d.BranchID
            LEFT JOIN dbo.POSStations AS st ON st.StationID = d.StationID
            WHERE d.DeviceID = @device;
            """,
            branch,
            transaction);
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = actor.DeviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PosPointInfo(actor.DeviceId, actor.BranchId, null, null, null);
        }

        return new PosPointInfo(
            actor.DeviceId,
            actor.BranchId,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task<LocalRow?> ReadRowAsync(
        SqlConnection branch,
        SqlTransaction? transaction,
        Guid deviceId,
        CancellationToken cancellationToken,
        bool lockRow = false)
    {
        await using var command = new SqlCommand(
            $"""
            SELECT DeviceID, BranchID, BranchName, PosNumber, DeviceName, SettingsJson, CentralVersion, SyncedAtUtc, UpdatedSource
            FROM dbo.PosDeviceSettings{(lockRow ? " WITH (UPDLOCK, HOLDLOCK)" : "")}
            WHERE DeviceID = @device;
            """,
            branch,
            transaction);
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRow(reader) : null;
    }

    private static async Task<Dictionary<Guid, LocalRow>> ReadAllRowsAsync(SqlConnection branch, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT DeviceID, BranchID, BranchName, PosNumber, DeviceName, SettingsJson, CentralVersion, SyncedAtUtc, UpdatedSource FROM dbo.PosDeviceSettings;",
            branch);
        var result = new Dictionary<Guid, LocalRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = ReadRow(reader);
            result[row.Point.DeviceId] = row;
        }

        return result;
    }

    private static LocalRow ReadRow(SqlDataReader reader) =>
        new(
            new PosPointInfo(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)),
            PosDeviceSettingsDocument.Parse(reader.GetString(5)),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            reader.GetString(8));

    private static async Task UpsertRowAsync(
        SqlConnection branch,
        SqlTransaction transaction,
        PosPointInfo point,
        IReadOnlyDictionary<string, PosDeviceSettingEntry> settings,
        long centralVersion,
        string source,
        bool synced,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.PosDeviceSettings WHERE DeviceID = @device)
                UPDATE dbo.PosDeviceSettings
                SET BranchID = @branch,
                    BranchName = COALESCE(@branchName, BranchName),
                    PosNumber = COALESCE(@pos, PosNumber),
                    DeviceName = COALESCE(@name, DeviceName),
                    SettingsJson = @json,
                    CentralVersion = @version,
                    UpdatedAtUtc = SYSUTCDATETIME(),
                    UpdatedSource = @source,
                    SyncedAtUtc = CASE WHEN @synced = 1 THEN SYSUTCDATETIME() ELSE SyncedAtUtc END
                WHERE DeviceID = @device;
            ELSE
                INSERT INTO dbo.PosDeviceSettings
                    (DeviceID, BranchID, BranchName, PosNumber, DeviceName, SettingsJson, CentralVersion, UpdatedSource, SyncedAtUtc)
                VALUES (@device, @branch, @branchName, @pos, @name, @json, @version, @source,
                        CASE WHEN @synced = 1 THEN SYSUTCDATETIME() ELSE NULL END);
            """,
            branch,
            transaction);
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = point.DeviceId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = point.BranchId;
        command.Parameters.Add("@branchName", SqlDbType.NVarChar, 200).Value = (object?)Limit(point.BranchName, 200) ?? DBNull.Value;
        command.Parameters.Add("@pos", SqlDbType.Int).Value = (object?)point.PosNumber ?? DBNull.Value;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = (object?)Limit(point.DeviceName, 200) ?? DBNull.Value;
        command.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = PosDeviceSettingsDocument.Serialize(settings);
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = centralVersion;
        command.Parameters.Add("@source", SqlDbType.NVarChar, 20).Value = source;
        command.Parameters.Add("@synced", SqlDbType.Bit).Value = synced;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PosPointSettings ToView(PosPointInfo point, LocalRow? row, bool isCurrent) =>
        new(
            point.DeviceId,
            point.BranchId,
            point.BranchName ?? row?.Point.BranchName,
            point.PosNumber ?? row?.Point.PosNumber,
            point.DeviceName ?? row?.Point.DeviceName,
            isCurrent,
            PosDeviceSettingsDocument.Effective(row?.Settings ?? new SortedDictionary<string, PosDeviceSettingEntry>(StringComparer.Ordinal)),
            row?.CentralVersion ?? 0,
            row?.SyncedAtUtc,
            row?.UpdatedSource);

    // ------------------------------------------------------------------

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

    private static async Task<bool> TableExistsAsync(SqlConnection connection, SqlTransaction? transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(@name, N'U');", connection, transaction);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 256).Value = name;
        return await command.ExecuteScalarAsync(cancellationToken) is not (null or DBNull);
    }

    private static string? Limit(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
