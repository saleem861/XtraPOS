using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Inventory;

/// <summary>
/// قرار المالك (27 نوفمبر 2026): الجرود المحددة تزول تلقائياً بعد 20 يوماً من إنشائها مهما كانت حالتها.
/// تزول بنفس قاعدة «حذف»: تُفك فروعها، وغير المستعمل يُحذف، والمستعمل (جلسة جرد أو معالجة نواقص) يُوقف ويختفي ويبقى سجله.
/// جرد مخصص عليه جلسة مفتوحة لا يزول حتى تُثبَّت أو تُلغى، حتى لا يضيع عدّ في منتصفه.
/// </summary>
public static class CustomInventoryExpiry
{
    public const int LifetimeDays = 20;

    public static DateTime ExpiresAt(DateTime createdAtUtc) => createdAtUtc.AddDays(LifetimeDays);

    /// <summary>الأيام المتبقية قبل الزوال (يوم ناقص يُحسب يوماً)، وصفر إن انتهت المدة.</summary>
    public static int DaysLeft(DateTime createdAtUtc, DateTime nowUtc)
    {
        var left = ExpiresAt(createdAtUtc) - nowUtc;
        return left <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(left.TotalDays);
    }

    public static bool IsExpired(DateTime createdAtUtc, DateTime nowUtc) => nowUtc >= ExpiresAt(createdAtUtc);

    /// <summary>يُزيل الجرود المنتهية على المركز، ويرجع عددها. آمن للتكرار ومن أكثر من جهاز.</summary>
    public static async Task<int> ExpireAsync(SqlConnection central, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @expired TABLE (CustomInventoryID uniqueidentifier PRIMARY KEY, Used bit NOT NULL);
            INSERT INTO @expired (CustomInventoryID, Used)
            SELECT inventory.CustomInventoryID,
                   CASE WHEN EXISTS (SELECT 1 FROM dbo.InventorySessions AS used WHERE used.CustomInventoryID = inventory.CustomInventoryID)
                          OR EXISTS (SELECT 1 FROM dbo.ShortageRuns AS run WHERE run.CustomInventoryID = inventory.CustomInventoryID)
                        THEN 1 ELSE 0 END
            FROM dbo.CustomInventories AS inventory WITH (UPDLOCK, HOLDLOCK)
            WHERE inventory.CreatedAt <= DATEADD(DAY, -@days, SYSUTCDATETIME())
              AND (inventory.IsActive = 1
                   OR EXISTS (SELECT 1 FROM dbo.CustomInventoryBranches AS target WHERE target.CustomInventoryID = inventory.CustomInventoryID))
              AND NOT EXISTS (SELECT 1 FROM dbo.InventorySessions AS running
                              WHERE running.CustomInventoryID = inventory.CustomInventoryID AND running.Status = N'Open');

            DELETE target FROM dbo.CustomInventoryBranches AS target
            INNER JOIN @expired AS gone ON gone.CustomInventoryID = target.CustomInventoryID;

            UPDATE inventory SET IsActive = 0, UpdatedAt = SYSUTCDATETIME()
            FROM dbo.CustomInventories AS inventory
            INNER JOIN @expired AS gone ON gone.CustomInventoryID = inventory.CustomInventoryID AND gone.Used = 1;

            DELETE line FROM dbo.CustomInventoryItems AS line
            INNER JOIN @expired AS gone ON gone.CustomInventoryID = line.CustomInventoryID AND gone.Used = 0;

            DELETE inventory FROM dbo.CustomInventories AS inventory
            INNER JOIN @expired AS gone ON gone.CustomInventoryID = inventory.CustomInventoryID AND gone.Used = 0;

            COMMIT TRANSACTION;
            SELECT COUNT(*) FROM @expired;
            """,
            central);
        command.Parameters.Add("@days", System.Data.SqlDbType.Int).Value = LifetimeDays;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int count ? count : 0;
    }
}
