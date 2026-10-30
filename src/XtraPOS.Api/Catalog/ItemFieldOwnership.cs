namespace XtraPOS.Api.Catalog;

public static class ItemEditing
{
    public const string Standalone = "Standalone";
    public const string CentralLinked = "CentralLinked";
    public const string Central = "Central";

    public const string CentralOfflineMessage =
        "تعذر الاتصال بالإدارة المركزية.\nيمكن عرض بيانات الصنف، لكن تعديل البيانات الأساسية يحتاج اتصالًا بالمركز.";

    public const string ItemNumberImmutableMessage =
        "رقم الصنف ثابت بعد الإنشاء ولا يمكن تغييره.";

    public const string ConflictMessage =
        "تم تعديل الصنف من مستخدم آخر. أعد تحميل البيانات ثم حاول مرة أخرى.";

    public const string UnitChangeRejectedMessage =
        "لا يمكن تغيير علاقة الوحدة لأن هذا الفرع يملك حركات مخزون أو كمية مجرودة. لن يتم تعديل سجل المخزون.";

    public const string CentralDeniedMessage =
        "الإدارة المركزية رفضت تعديل البيانات الأساسية.";
}

/// <summary>
/// Central owns the master catalog. Branch operational stock and branch prices stay local.
/// ItemNumber is immutable after creation.
/// </summary>
public static class ItemFieldOwnership
{
    public static readonly string[] CentralItemColumns =
    [
        "ItemName",
        "ItemType",
        "Manufacturer",
        "Color",
        "Taste",
        "Size",
        "Packing",
        "Classification",
        "CountryOfOrigin",
        "ProductType",
        "ParentItemID",
        "IsActive",
        "IsProhibited",
        "IsPublishedOnline",
    ];

    public static readonly string[] CentralUnitColumns =
    [
        "UnitsPerPackage",
        "InternalUnitItemID",
        "InternalUnitItemNumber",
        "InternalUnitItemName",
        "IsBaseUnit",
        "IsActive",
    ];

    public static readonly string[] BranchInventoryColumns =
    [
        "UnitCost",
        "DailySalesRate",
        "IsAvailable",
        "LastSaleAt",
        "LastRestockAt",
        "IsActive",
    ];
}
