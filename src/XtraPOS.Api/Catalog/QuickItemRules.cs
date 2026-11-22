namespace XtraPOS.Api.Catalog;

public sealed record QuickItemLine(int SortOrder, string ItemNumber, string? ItemName, string? Classification);

public sealed record QuickItemSet(
    Guid SetId,
    Guid? BranchId,
    string? BranchName,
    DateTime CreatedAt,
    string? CreatedByName,
    IReadOnlyList<QuickItemLine> Lines);

public sealed class QuickItemException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class QuickItemRules
{
    /// <summary>أقصى عدد في القائمة؛ الشريط صف واحد يُمرَّر أفقياً ويظهر منه 6 إلى 10.</summary>
    public const int MaxItems = 30;

    /// <summary>الأرقام كما أدخلها المستخدم: بلا فراغات ولا تكرار، بالترتيب نفسه.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? numbers)
    {
        var result = new List<string>();
        foreach (var raw in numbers ?? [])
        {
            var number = raw?.Trim();
            if (string.IsNullOrEmpty(number)) continue;
            if (number.Length > 100) throw new QuickItemException("رقم الصنف أطول من المسموح.");
            if (!result.Contains(number, StringComparer.OrdinalIgnoreCase)) result.Add(number);
        }

        if (result.Count > MaxItems)
        {
            throw new QuickItemException($"الحد الأقصى {MaxItems} صنفاً في القائمة.");
        }

        return result;
    }

    /// <summary>
    /// القائمة المعمول بها في فرع: أحدث قائمة خاصة به (بالمعرّف أو بالاسم) إن كانت غير فارغة،
    /// وإلا أحدث قائمة «كل الفروع». قائمة خاصة فارغة تعني الرجوع إلى «كل الفروع».
    /// </summary>
    public static QuickItemSet? Effective(IEnumerable<QuickItemSet> sets, Guid branchId, string? branchName)
    {
        var ordered = sets.OrderByDescending(set => set.CreatedAt).ThenByDescending(set => set.SetId).ToArray();
        var own = ordered.FirstOrDefault(set => IsBranch(set, branchId, branchName));
        if (own is { Lines.Count: > 0 }) return own;
        return ordered.FirstOrDefault(set => set.BranchId is null && string.IsNullOrWhiteSpace(set.BranchName));
    }

    public static bool IsBranch(QuickItemSet set, Guid branchId, string? branchName) =>
        set.BranchId == branchId
        || (!string.IsNullOrWhiteSpace(branchName) && !string.IsNullOrWhiteSpace(set.BranchName)
            && string.Equals(set.BranchName.Trim(), branchName.Trim(), StringComparison.CurrentCultureIgnoreCase));
}
