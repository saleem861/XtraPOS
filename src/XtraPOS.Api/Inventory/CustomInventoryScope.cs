namespace XtraPOS.Api.Inventory;

/// <summary>
/// جرد مخصص يُرى لكل فرع نشط يحمل نفس الاسم. على المركز أكثر من صف باسم Labtop،
/// وتعيين «جرد تيربو» لأحدها كان يخفيه عن جهاز الصف الآخر.
/// </summary>
public static class CustomInventoryScope
{
    /// <summary>فروع نشطة بنفس الاسم داخل النشاط. المعامل صيغة معرّف الفرع، مثل <c>@branch</c>.</summary>
    public static string SameNameBranches(string branchParameter) =>
        $"""
        SELECT scopeSibling.BranchID
        FROM dbo.Branches AS scopeMine
        INNER JOIN dbo.Branches AS scopeSibling
            ON scopeSibling.IsActive = 1
           AND scopeSibling.BusinessID = scopeMine.BusinessID
           AND LTRIM(RTRIM(scopeSibling.BranchName)) = LTRIM(RTRIM(scopeMine.BranchName))
        WHERE scopeMine.BranchID = {branchParameter}
        """;

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

    /// <summary>حالة كل فرع معيّن لجرد مخصص يخص فرع الجهاز (بالاسم). الاستعلام مُركَّب حتى لا يُرسل القالب حرفياً.</summary>
    public static readonly string BranchStatusSql = $"""
        SELECT assigned.CustomInventoryID, branch.BranchID, branch.BranchName,
               CASE WHEN EXISTS (SELECT 1 FROM dbo.InventorySessions AS countSession
                                 WHERE countSession.CustomInventoryID = assigned.CustomInventoryID
                                   AND countSession.BranchID = assigned.BranchID
                                   AND countSession.Status = N'Completed'
                                   AND countSession.CompletedAt >= inventory.UpdatedAt) THEN 1 ELSE 0 END
        FROM dbo.CustomInventoryBranches AS assigned
        INNER JOIN dbo.CustomInventories AS inventory ON inventory.CustomInventoryID = assigned.CustomInventoryID
        INNER JOIN dbo.Branches AS branch ON branch.BranchID = assigned.BranchID AND branch.IsActive = 1
        WHERE inventory.IsActive = 1
          AND {AssignedToBranch}
        ORDER BY branch.BranchName;
        """;

    /// <summary>الجرد المخصص الذي يسحبه الفرع من المركز. الاستعلام مُركَّب حتى لا يُرسل القالب حرفياً.</summary>
    public static readonly string AssignedPullSql = $"""
        SELECT inventory.CustomInventoryID, inventory.InventoryName, inventory.IsActive, inventory.UpdatedAt
        FROM dbo.CustomInventories AS inventory
        WHERE {AssignedToBranch};
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
