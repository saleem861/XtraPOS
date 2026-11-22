using System.Text.Json;
using XtraPOS.Api.Devices;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class PosDeviceSettingsDocumentTests
{
    private static readonly Guid BranchUser = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CentralUser = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime T0 = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

    private static SortedDictionary<string, PosDeviceSettingEntry> Change(bool enabled, DateTime at, string source)
    {
        var values = new Dictionary<string, JsonElement>
        {
            [PosDeviceSettingsCatalog.CustomerDisplayEnabled] = JsonSerializer.SerializeToElement(enabled),
        };
        return PosDeviceSettingsDocument.BuildChanges(
            values,
            at,
            source == PosDeviceSettingSources.Central ? CentralUser : BranchUser,
            source == PosDeviceSettingSources.Central ? "موظف المركز" : "مشرف الفرع",
            source);
    }

    private static bool Enabled(IReadOnlyDictionary<string, PosDeviceSettingEntry> settings) =>
        PosDeviceSettingsDocument.Effective(settings)
            .Single(value => value.Key == PosDeviceSettingsCatalog.CustomerDisplayEnabled)
            .Value.GetValue<bool>();

    [Fact]
    public void Defaults_CustomerDisplayIsOff()
    {
        var effective = PosDeviceSettingsDocument.Effective(PosDeviceSettingsDocument.Parse(null));
        var display = Assert.Single(effective, value => value.Key == PosDeviceSettingsCatalog.CustomerDisplayEnabled);
        Assert.False(display.Value.GetValue<bool>());
        Assert.True(display.IsDefault);
    }

    [Fact]
    public void Merge_NewerWins_InBothDirections()
    {
        var branch = Change(true, T0, PosDeviceSettingSources.Branch);
        var central = Change(false, T0.AddMinutes(5), PosDeviceSettingSources.Central);

        Assert.False(Enabled(PosDeviceSettingsDocument.Merge(branch, central)));
        Assert.False(Enabled(PosDeviceSettingsDocument.Merge(central, branch)));

        var laterBranch = Change(true, T0.AddMinutes(10), PosDeviceSettingSources.Branch);
        Assert.True(Enabled(PosDeviceSettingsDocument.Merge(central, laterBranch)));
        Assert.True(Enabled(PosDeviceSettingsDocument.Merge(laterBranch, central)));
    }

    [Fact]
    public void Merge_SameInstant_CentralWins_AndIsCommutative()
    {
        var branch = Change(true, T0, PosDeviceSettingSources.Branch);
        var central = Change(false, T0, PosDeviceSettingSources.Central);

        var a = PosDeviceSettingsDocument.Merge(branch, central);
        var b = PosDeviceSettingsDocument.Merge(central, branch);
        Assert.False(Enabled(a));
        Assert.True(PosDeviceSettingsDocument.SameContent(a, b));
    }

    [Fact]
    public void Merge_IsIdempotent()
    {
        var central = Change(true, T0, PosDeviceSettingSources.Central);
        var once = PosDeviceSettingsDocument.Merge(central, central);
        var twice = PosDeviceSettingsDocument.Merge(once, central);
        Assert.True(PosDeviceSettingsDocument.SameContent(central, twice));
    }

    [Fact]
    public void RoundTrip_ThroughConfigurationJson_KeepsOtherDeviceFields()
    {
        const string existing = """{"schemaVersion":1,"configVersion":4,"device":{"posNumber":3}}""";
        var settings = Change(true, T0, PosDeviceSettingSources.Branch);

        var written = PosDeviceSettingsDocument.WriteIntoConfiguration(existing, settings);
        var read = PosDeviceSettingsDocument.Parse(PosDeviceSettingsDocument.ReadFromConfiguration(written));

        Assert.True(PosDeviceSettingsDocument.SameContent(settings, read));
        using var document = JsonDocument.Parse(written);
        Assert.Equal(3, document.RootElement.GetProperty("device").GetProperty("posNumber").GetInt32());
        Assert.Equal(4, document.RootElement.GetProperty("configVersion").GetInt32());
        var entry = read[PosDeviceSettingsCatalog.CustomerDisplayEnabled];
        Assert.Equal(BranchUser, entry.UpdatedByUserId);
        Assert.Equal(PosDeviceSettingSources.Branch, entry.Source);
        Assert.Equal(T0, entry.UpdatedAtUtc);
    }

    [Fact]
    public void BuildChanges_RejectsUnknownKeysAndWrongTypes()
    {
        Assert.Throws<PosDeviceSettingsException>(() => PosDeviceSettingsDocument.BuildChanges(
            new Dictionary<string, JsonElement> { ["unknown.key"] = JsonSerializer.SerializeToElement(true) },
            T0,
            BranchUser,
            null,
            PosDeviceSettingSources.Branch));
        Assert.Throws<PosDeviceSettingsException>(() => PosDeviceSettingsDocument.BuildChanges(
            new Dictionary<string, JsonElement> { [PosDeviceSettingsCatalog.CustomerDisplayEnabled] = JsonSerializer.SerializeToElement("yes") },
            T0,
            BranchUser,
            null,
            PosDeviceSettingSources.Branch));
        Assert.Throws<PosDeviceSettingsException>(() => PosDeviceSettingsDocument.BuildChanges(
            new Dictionary<string, JsonElement>(),
            T0,
            BranchUser,
            null,
            PosDeviceSettingSources.Branch));
    }

    [Fact]
    public void Parse_IgnoresCorruptJson()
    {
        Assert.Empty(PosDeviceSettingsDocument.Parse("{not json"));
        Assert.Null(PosDeviceSettingsDocument.ReadFromConfiguration("{not json"));
        var written = PosDeviceSettingsDocument.WriteIntoConfiguration("{not json", Change(true, T0, PosDeviceSettingSources.Branch));
        Assert.True(Enabled(PosDeviceSettingsDocument.Parse(PosDeviceSettingsDocument.ReadFromConfiguration(written))));
    }

    [Fact]
    public void LoggedOperation_RoundTripsThroughTheExistingRelayLog()
    {
        var point = new PosPointInfo(Guid.NewGuid(), Guid.NewGuid(), "Labtop", 2, "كاونتر 2");
        var operation = new DeviceSettingsOperation(PosDeviceSettingsService.ScopeDevice, [point], Change(true, T0, PosDeviceSettingSources.Branch), BranchUser, "سليم");
        var parsed = PosDeviceSettingsService.ParseOperation(PosDeviceSettingsService.SerializeOperation(operation));

        Assert.NotNull(parsed);
        Assert.Equal(PosDeviceSettingsService.ScopeDevice, parsed!.Scope);
        Assert.Equal(point, Assert.Single(parsed.Points));
        Assert.True(Enabled(parsed.Settings));
        Assert.Equal(BranchUser, parsed.ActorUserId);
        Assert.Equal("القيم الأولية (كاونتر 2 - Labtop): تفعيل شاشة الزبون: مفعّل", PosDeviceSettingsService.Describe(parsed));
        Assert.Equal("نقطة 1 - Asus", PosDeviceSettingsService.PointLabel(new PosPointInfo(Guid.NewGuid(), Guid.NewGuid(), "Asus", 1, " ")));
    }

    [Fact]
    public void LoggedOperation_ForAllPointsNeedsNoPointList()
    {
        var operation = new DeviceSettingsOperation(PosDeviceSettingsService.ScopeAll, [], Change(false, T0, PosDeviceSettingSources.Branch), BranchUser, null);
        var parsed = PosDeviceSettingsService.ParseOperation(PosDeviceSettingsService.SerializeOperation(operation));

        Assert.NotNull(parsed);
        Assert.Equal(PosDeviceSettingsService.ScopeAll, parsed!.Scope);
        Assert.Empty(parsed.Points);
        Assert.False(Enabled(parsed.Settings));
        Assert.StartsWith("القيم الأولية (جميع النقاط)", PosDeviceSettingsService.Describe(parsed));
    }

    [Fact]
    public void LoggedOperation_RejectsBrokenPayloads()
    {
        Assert.Null(PosDeviceSettingsService.ParseOperation(null));
        Assert.Null(PosDeviceSettingsService.ParseOperation("{not json"));
        Assert.Null(PosDeviceSettingsService.ParseOperation("""{"scope":"Device","points":[],"settings":{}}"""));
        Assert.Null(PosDeviceSettingsService.ParseOperation("""{"scope":5}"""));
    }

    [Fact]
    public void Point_list_keeps_one_entry_per_counter_in_each_branch()
    {
        var labtop = Guid.NewGuid();
        var sharbel = Guid.NewGuid();
        var current = new PosPointInfo(Guid.NewGuid(), labtop, "Labtop", 1, "كاونتر 1");
        var oldSame = new PosPointInfo(Guid.NewGuid(), labtop, "Labtop", 1, "كاونتر 1");
        var older = new PosPointInfo(Guid.NewGuid(), labtop, "Labtop", 1, " كاونتر 1 ");
        var sharbelOld = new PosPointInfo(Guid.NewGuid(), sharbel, "Sharbel", 1, "كاونتر 1");
        var sharbelNew = new PosPointInfo(Guid.NewGuid(), sharbel, "Sharbel", 1, "كاونتر 1");
        var sharbelTwo = new PosPointInfo(Guid.NewGuid(), sharbel, "Sharbel", 2, "كاونتر 2");

        var list = PosDeviceSettingsService.DistinctPoints(
            [oldSame, older, current, sharbelOld, sharbelNew, sharbelTwo],
            current.DeviceId,
            [sharbelNew, oldSame, sharbelTwo, sharbelOld, older]);

        Assert.Equal(3, list.Count);
        Assert.Contains(list, point => point.DeviceId == current.DeviceId);
        Assert.Contains(list, point => point.DeviceId == sharbelNew.DeviceId);
        Assert.Contains(list, point => point.DeviceId == sharbelTwo.DeviceId);
    }
}
