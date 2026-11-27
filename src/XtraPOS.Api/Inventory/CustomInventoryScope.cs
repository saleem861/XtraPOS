namespace XtraPOS.Api.Inventory;

/// <summary>
/// جرد مخصص يُرى لكل فرع نشط يحمل نفس الاسم. على المركز أكثر من صف باسم Labtop،
/// وتعيين «جرد تيربو» لأحدها كان يخفيه عن جهاز الصف الآخر.
/// </summary>
public static class CustomInventoryScope
{
    public const string AssignedToBranch = """
        EXISTS (
            SELECT 1
            FROM dbo.CustomInventoryBranches AS scopeAssigned
            INNER JOIN dbo.Branches AS scopeAssignedBranch
                ON scopeAssignedBranch.BranchID = scopeAssigned.BranchID AND scopeAssignedBranch.IsActive = 1
            INNER JOIN dbo.Branches AS scopeMine
                ON scopeMine.BranchID = @branch AND scopeMine.BusinessID = scopeAssignedBranch.BusinessID
            WHERE scopeAssigned.CustomInventoryID = inventory.CustomInventoryID
              AND LTRIM(RTRIM(scopeAssignedBranch.BranchName)) = LTRIM(RTRIM(scopeMine.BranchName)))
        """;

    /// <summary>اختيار فرع واحد بالاسم يُوسَّع إلى كل الفروع النشطة التي تحمل الاسم نفسه.</summary>
    public static IReadOnlyList<Guid> SameNameBranches(
        IEnumerable<(Guid Id, string Name)> branches,
        IReadOnlyCollection<Guid> selected)
    {
        var wanted = selected as ISet<Guid> ?? selected.ToHashSet();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = branches.ToList();
        foreach (var branch in rows)
        {
            if (wanted.Contains(branch.Id) && branch.Name.Trim().Length > 0) names.Add(branch.Name.Trim());
        }

        return rows.Where(branch => names.Contains(branch.Name.Trim())).Select(branch => branch.Id).Distinct().ToList();
    }
}
