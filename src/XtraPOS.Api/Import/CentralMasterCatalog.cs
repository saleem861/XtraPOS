using XtraPOS.Api.Security;

namespace XtraPOS.Api.Import;

public sealed record PlannedCentralItem(
    string ItemNumber,
    string ItemName,
    string? ItemType,
    string? Manufacturer,
    string? Color,
    string? Taste,
    string? Size,
    string? Packing,
    string? Classification,
    string? CountryOfOrigin,
    string? ProductType,
    string? ParentItemNumber,
    bool IsProhibited,
    bool IsActive,
    string DescriptiveSource);

public sealed record PlannedCentralUnit(
    string ItemNumber,
    decimal UnitsPerPackage,
    string InternalUnitItemNumber,
    string? InternalUnitItemName);

public sealed record PlannedCentralPrice(string ItemNumber, string PriceCode, decimal Price);

public sealed record PlannedCentralUser(
    string EmployeeNumber,
    string Username,
    string DisplayName,
    string? FullName,
    bool IsActive,
    string RoleCode);

public sealed record DuplicateItemChoice(string ItemNumber, string Reason);

public sealed record CentralMasterPlan(
    IReadOnlyList<PlannedCentralItem> Items,
    IReadOnlyList<PlannedCentralUnit> Units,
    IReadOnlyList<PlannedCentralPrice> Prices,
    IReadOnlyList<PlannedCentralUser> Users,
    IReadOnlyList<DuplicateItemChoice> DuplicateChoices,
    IReadOnlyList<string> Anomalies,
    int BlankItemRows,
    int TechnicalRowsExcluded,
    int TechnicalIdentitiesExcluded,
    int SelfLinks,
    int OrphanLinksSkipped,
    int MultiHopLinks,
    int CycleLinksSkipped,
    int ZeroPrices,
    int NegativeMasterPricesExcluded,
    int ShopPricesIgnored,
    int ActiveItems,
    int InactiveItems,
    int ActiveUsers,
    int InactiveUsers);

public static class CentralMasterCatalog
{
    public const string TechnicalItemNumber = "XTRAPOS_DELETE_TEST_20260917115506";

    public static readonly string[] SourceOrder =
    [
        LegacyCatalogs.Central,
        LegacyCatalogs.Shop1,
        LegacyCatalogs.Shop2,
        LegacyCatalogs.Swstore,
    ];

    public static readonly (string Code, string Name, int SortOrder)[] PriceTypes =
    [
        ("Dealer", "سعر الجملة", 20),
        ("MajorCustomer", "سعر مستهلك كبير", 30),
        ("SpecialCustomer", "زبون مميز", 40),
        ("Price0", "Price0", 50),
        ("Price1", "Price1", 60),
        ("Price2", "Price2", 70),
        ("Price3", "Price3", 80),
    ];

    public static readonly (string Code, string Name)[] Roles =
    [
        (RoleCodes.Admin, "مدير النظام"),
        (RoleCodes.Supervisor, "مشرف"),
        (RoleCodes.User, "مستخدم"),
    ];

    public static readonly (string Code, string Name, string Module)[] Permissions =
    [
        (PermissionCodes.SystemAdminister, "إدارة النظام", "System"),
        (PermissionCodes.UsersView, "عرض المستخدمين", "Users"),
        (PermissionCodes.UsersManage, "إدارة المستخدمين", "Users"),
        (PermissionCodes.UsersResetPassword, "إعادة تعيين كلمة المرور", "Users"),
        (PermissionCodes.RolesView, "عرض الأدوار", "Roles"),
        (PermissionCodes.RolesManage, "إدارة الأدوار", "Roles"),
        (PermissionCodes.DeviceView, "عرض الأجهزة", "Device"),
        (PermissionCodes.SetupView, "عرض الإعداد", "Setup"),
        (PermissionCodes.ItemsView, "عرض الأصناف", "Items"),
        (PermissionCodes.ItemsEditMaster, "تعديل البيانات الأساسية للصنف", "Items"),
        (PermissionCodes.ItemsEditBranchState, "تعديل بيانات الفرع للصنف", "Items"),
        (PermissionCodes.ItemsEditBranchPrice, "تعديل سعر الفرع", "Items"),
        (PermissionCodes.CustomersView, "عرض الزبائن", "Customers"),
        (PermissionCodes.CustomersCreate, "إضافة زبون", "Customers"),
        (PermissionCodes.CustomersManage, "إدارة الزبائن", "Customers"),
    ];

    private static readonly Dictionary<string, string> RoleByEmployeeNumber = new(StringComparer.Ordinal)
    {
        ["905017083"] = RoleCodes.Admin,
        ["402120067"] = RoleCodes.Supervisor,
        ["421600008"] = RoleCodes.Supervisor,
    };

    private static readonly (string Field, Func<LegacyItemObservation, decimal?> Read)[] PriceFields =
    [
        ("Dealer", row => row.DealerPrice),
        ("MajorCustomer", row => row.MajorCustomerPrice),
        ("SpecialCustomer", row => row.SpecialCustomerPrice),
        ("Price0", row => row.Price0),
        ("Price1", row => row.Price1),
        ("Price2", row => row.Price2),
        ("Price3", row => row.Price3),
    ];

    public static CentralMasterPlan Plan(
        IReadOnlyList<LegacyItemObservation> observations,
        IReadOnlyList<LegacyEmployeeRecord> employees)
    {
        var anomalies = new List<string>();
        var choices = new List<DuplicateItemChoice>();
        var blankRows = observations.Count(row => string.IsNullOrWhiteSpace(row.ItemNumber));
        var technical = observations
            .Where(row => string.Equals(Text(row.ItemNumber), TechnicalItemNumber, StringComparison.Ordinal))
            .ToList();
        var accepted = observations
            .Where(row => SourceOrder.Contains(row.Source, StringComparer.Ordinal))
            .Select(Trim)
            .Where(row => row.ItemNumber.Length > 0 && !string.Equals(row.ItemNumber, TechnicalItemNumber, StringComparison.Ordinal))
            .ToList();
        var chosenByNumber = new Dictionary<string, Dictionary<string, LegacyItemObservation>>(StringComparer.Ordinal);
        foreach (var numberGroup in accepted.GroupBy(row => row.ItemNumber, StringComparer.Ordinal))
        {
            var bySource = new Dictionary<string, LegacyItemObservation>(StringComparer.Ordinal);
            foreach (var sourceGroup in numberGroup.GroupBy(row => row.Source, StringComparer.Ordinal))
            {
                var rows = sourceGroup.ToList();
                if (rows.Count == 1)
                {
                    bySource[sourceGroup.Key] = rows[0];
                    continue;
                }

                var ranked = rows
                    .Select(row => (Row: row, Score: Completeness(row), Signature: Signature(row)))
                    .OrderByDescending(row => row.Score)
                    .ThenBy(row => row.Signature, StringComparer.Ordinal)
                    .ToList();
                var kept = ranked[0];
                var same = ranked.All(row => row.Score == kept.Score && string.Equals(row.Signature, kept.Signature, StringComparison.Ordinal));
                choices.Add(new DuplicateItemChoice(
                    numberGroup.Key,
                    same
                        ? $"{sourceGroup.Key} has {rows.Count} identical rows. One row is kept."
                        : $"{sourceGroup.Key} has {rows.Count} rows. The more complete row is kept (score {kept.Score})."));
                bySource[sourceGroup.Key] = kept.Row;
            }

            chosenByNumber[numberGroup.Key] = bySource;
        }

        var numbers = chosenByNumber.Keys.ToHashSet(StringComparer.Ordinal);
        var items = new List<PlannedCentralItem>();
        var units = new List<PlannedCentralUnit>();
        var prices = new List<PlannedCentralPrice>();
        var self = 0;
        var orphans = 0;
        var cycles = 0;
        var zeroPrices = 0;
        var negativeMaster = 0;
        var shopPrices = 0;
        var edges = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var number in numbers.OrderBy(value => value, StringComparer.Ordinal))
        {
            var sources = chosenByNumber[number];
            var descriptive = FirstSource(sources);
            var item = new PlannedCentralItem(
                number,
                FirstText(sources, row => row.ItemName) ?? number,
                FirstText(sources, row => row.ItemType),
                FirstText(sources, row => row.Manufacturer),
                FirstText(sources, row => row.Color),
                FirstText(sources, row => row.Taste),
                FirstText(sources, row => row.Size),
                FirstText(sources, row => row.Packing),
                FirstText(sources, row => row.Classification),
                FirstText(sources, row => row.CountryOfOrigin),
                FirstText(sources, row => row.ProductType),
                Parent(number, sources, numbers, anomalies),
                FirstBool(sources, row => row.IsProhibited),
                sources.Keys.Any(LegacyCatalogs.IsRealBranch),
                descriptive);
            if (string.IsNullOrWhiteSpace(FirstText(sources, row => row.ItemName)))
            {
                anomalies.Add($"{number}: ItemName is missing. ItemNumber is used as the name.");
            }

            items.Add(item);
            var unitRow = FirstRow(sources, row => !string.IsNullOrWhiteSpace(row.InternalUnitItemNumber));
            if (unitRow is not null)
            {
                var target = Text(unitRow.InternalUnitItemNumber);
                var factor = unitRow.UnitsPerPackage ?? 0;
                if (string.Equals(target, number, StringComparison.Ordinal))
                {
                    self++;
                    if (factor != 1)
                    {
                        anomalies.Add($"{number}: self-link factor is {factor}. It stays a base unit.");
                    }
                }
                else if (factor <= 0)
                {
                    orphans++;
                    anomalies.Add($"{number}: unit factor {factor} for {target} was skipped.");
                }
                else if (!numbers.Contains(target))
                {
                    orphans++;
                    anomalies.Add($"{number}: orphan unit target {target} was skipped.");
                }
                else if (CreatesCycle(edges, number, target))
                {
                    cycles++;
                    anomalies.Add($"{number}: unit link to {target} was skipped because it cycles.");
                }
                else
                {
                    edges[number] = target;
                    units.Add(new PlannedCentralUnit(number, factor, target, BlankToNull(unitRow.InternalUnitItemName)));
                }
            }

            if (sources.TryGetValue(LegacyCatalogs.Central, out var master))
            {
                foreach (var price in PriceFields)
                {
                    if (price.Read(master) is not { } value)
                    {
                        continue;
                    }

                    if (value < 0)
                    {
                        negativeMaster++;
                        anomalies.Add($"{number}: negative master {price.Field} {value} was excluded.");
                        continue;
                    }

                    if (value == 0)
                    {
                        zeroPrices++;
                    }

                    prices.Add(new PlannedCentralPrice(number, price.Field, decimal.Round(value, 4)));
                }
            }

            foreach (var source in sources.Values.Where(row => LegacyCatalogs.IsRealBranch(row.Source)))
            {
                shopPrices += PriceFields.Count(price => price.Read(source) is not null);
            }
        }

        var multiHop = edges.Count(edge => edges.ContainsKey(edge.Value));
        var users = PlanUsers(employees, anomalies);
        return new CentralMasterPlan(
            items,
            units,
            prices,
            users,
            choices,
            anomalies,
            blankRows,
            technical.Count,
            technical.Select(row => row.ItemNumber.Trim()).Distinct(StringComparer.Ordinal).Count(),
            self,
            orphans,
            multiHop,
            cycles,
            zeroPrices,
            negativeMaster,
            shopPrices,
            items.Count(item => item.IsActive),
            items.Count(item => !item.IsActive),
            users.Count(user => user.IsActive),
            users.Count(user => !user.IsActive));
    }

    public static string PrecedenceText => string.Join(" then ", SourceOrder);

    private static List<PlannedCentralUser> PlanUsers(IReadOnlyList<LegacyEmployeeRecord> employees, List<string> anomalies)
    {
        var chosen = new List<LegacyEmployeeRecord>();
        foreach (var group in employees.GroupBy(row => Text(row.IdNumber), StringComparer.Ordinal))
        {
            if (group.Key.Length == 0)
            {
                anomalies.Add("An employee row with a blank EmployeeNumber was skipped.");
                continue;
            }

            var rows = group.ToList();
            if (rows.Count > 1)
            {
                anomalies.Add($"EmployeeNumber {group.Key} is duplicated. One row is kept.");
            }

            chosen.Add(rows
                .OrderByDescending(row => row.EndWork is null)
                .ThenByDescending(row => Text(row.UserShortName).Length > 0)
                .ThenBy(row => Text(row.UserFullName), StringComparer.Ordinal)
                .First());
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var users = new List<PlannedCentralUser>();
        foreach (var employee in chosen.OrderBy(row => Text(row.IdNumber), StringComparer.Ordinal))
        {
            var employeeNumber = Text(employee.IdNumber);
            if (employeeNumber.Length > 50)
            {
                anomalies.Add($"EmployeeNumber {employeeNumber} is longer than 50 characters and was skipped.");
                continue;
            }

            var username = Username(employee, employeeNumber, usedNames, anomalies);
            if (username is null)
            {
                continue;
            }

            var shortName = Text(employee.UserShortName);
            var fullName = Text(employee.UserFullName);
            var display = shortName.Length > 0 ? shortName : fullName.Length > 0 ? fullName : employeeNumber;
            if (display.Length > 150)
            {
                display = display[..150];
            }

            users.Add(new PlannedCentralUser(
                employeeNumber,
                username,
                display,
                fullName.Length == 0 ? null : fullName.Length <= 300 ? fullName : fullName[..300],
                employee.EndWork is null,
                RoleByEmployeeNumber.TryGetValue(employeeNumber, out var role) ? role : RoleCodes.User));
        }

        foreach (var (employeeNumber, role) in RoleByEmployeeNumber)
        {
            if (users.All(user => !string.Equals(user.EmployeeNumber, employeeNumber, StringComparison.Ordinal)))
            {
                anomalies.Add($"Role mapping {employeeNumber} -> {role} was not found in UsersTable.");
            }
        }

        return users;
    }

    private static string? Username(
        LegacyEmployeeRecord employee,
        string employeeNumber,
        HashSet<string> usedNames,
        List<string> anomalies)
    {
        foreach (var candidate in new[] { Text(employee.UserShortName), employeeNumber })
        {
            if (AccountIdentity.IsAcceptableUsername(candidate) && usedNames.Add(candidate))
            {
                return candidate;
            }
        }

        var sanitized = new string(employeeNumber.Where(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-').ToArray());
        if (sanitized.Length < AccountIdentity.UsernameMinLength)
        {
            sanitized = "emp" + sanitized;
        }

        if (sanitized.Length > AccountIdentity.UsernameMaxLength)
        {
            sanitized = sanitized[..AccountIdentity.UsernameMaxLength];
        }

        var unique = sanitized;
        var suffix = 2;
        while (!usedNames.Add(unique))
        {
            var tail = suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            unique = sanitized.Length + tail.Length > AccountIdentity.UsernameMaxLength
                ? sanitized[..(AccountIdentity.UsernameMaxLength - tail.Length)] + tail
                : sanitized + tail;
            suffix++;
        }

        anomalies.Add($"EmployeeNumber {employeeNumber} uses username {unique}.");
        return unique;
    }

    private static string? Parent(
        string itemNumber,
        IReadOnlyDictionary<string, LegacyItemObservation> sources,
        HashSet<string> numbers,
        List<string> anomalies)
    {
        var parent = FirstText(sources, row => row.ParentItemNumber);
        if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, itemNumber, StringComparison.Ordinal))
        {
            return null;
        }

        if (!numbers.Contains(parent))
        {
            anomalies.Add($"{itemNumber}: parent {parent} is not in the catalog and was left empty.");
            return null;
        }

        return parent;
    }

    private static bool CreatesCycle(IReadOnlyDictionary<string, string> edges, string from, string to)
    {
        var current = to;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(current))
        {
            if (string.Equals(current, from, StringComparison.Ordinal) || !seen.Add(current))
            {
                return true;
            }

            if (!edges.TryGetValue(current, out var next))
            {
                return false;
            }

            current = next;
        }

        return false;
    }

    private static LegacyItemObservation Trim(LegacyItemObservation row) => row with
    {
        ItemNumber = Text(row.ItemNumber),
        ItemName = BlankToNull(row.ItemName),
        InternalUnitItemNumber = BlankToNull(row.InternalUnitItemNumber),
        InternalUnitItemName = BlankToNull(row.InternalUnitItemName),
        ParentItemNumber = BlankToNull(row.ParentItemNumber),
    };

    private static string FirstSource(IReadOnlyDictionary<string, LegacyItemObservation> sources)
    {
        foreach (var source in SourceOrder)
        {
            if (sources.ContainsKey(source))
            {
                return source;
            }
        }

        return sources.Keys.First();
    }

    private static string? FirstText(
        IReadOnlyDictionary<string, LegacyItemObservation> sources,
        Func<LegacyItemObservation, string?> read)
    {
        foreach (var source in SourceOrder)
        {
            if (sources.TryGetValue(source, out var row) && !string.IsNullOrWhiteSpace(read(row)))
            {
                return Text(read(row));
            }
        }

        return null;
    }

    private static LegacyItemObservation? FirstRow(
        IReadOnlyDictionary<string, LegacyItemObservation> sources,
        Func<LegacyItemObservation, bool> match)
    {
        foreach (var source in SourceOrder)
        {
            if (sources.TryGetValue(source, out var row) && match(row))
            {
                return row;
            }
        }

        return null;
    }

    private static bool FirstBool(
        IReadOnlyDictionary<string, LegacyItemObservation> sources,
        Func<LegacyItemObservation, bool?> read)
    {
        foreach (var source in SourceOrder)
        {
            if (sources.TryGetValue(source, out var row) && read(row) is { } value)
            {
                return value;
            }
        }

        return false;
    }

    private static int Completeness(LegacyItemObservation row)
    {
        var score = string.IsNullOrWhiteSpace(row.ItemName) ? 0 : 32;
        foreach (var value in new[]
        {
            row.ItemType, row.Manufacturer, row.Color, row.Taste, row.Size, row.Packing,
            row.Classification, row.CountryOfOrigin, row.ProductType, row.ParentItemNumber,
            row.InternalUnitItemNumber, row.InternalUnitItemName,
        })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                score++;
            }
        }

        if (row.UnitsPerPackage is > 0)
        {
            score += 2;
        }

        score += PriceFields.Count(price => price.Read(row) is not null);
        if (row.IsProhibited is not null)
        {
            score++;
        }

        return score;
    }

    private static string Signature(LegacyItemObservation row)
    {
        return string.Join(
            "|",
            Text(row.ItemName),
            Text(row.ItemType),
            Text(row.Manufacturer),
            Text(row.Color),
            Text(row.Taste),
            Text(row.Size),
            Text(row.Packing),
            Text(row.Classification),
            Text(row.CountryOfOrigin),
            Text(row.ProductType),
            Text(row.ParentItemNumber),
            Text(row.InternalUnitItemNumber),
            Text(row.InternalUnitItemName),
            row.UnitsPerPackage?.ToString() ?? "",
            string.Join(",", PriceFields.Select(price => price.Read(row)?.ToString() ?? "")),
            row.IsProhibited?.ToString() ?? "");
    }

    private static string Text(string? value) => value?.Trim() ?? "";

    private static string? BlankToNull(string? value)
    {
        var text = Text(value);
        return text.Length == 0 ? null : text;
    }
}
