namespace XtraPOS.Api.Data.Central;

public static class CentralOwnedEntities
{
    public static readonly string[] Implemented =
    [
        "Businesses",
        "Branches",
        "Items",
        "ItemUnits",
        "PriceTypes",
        "ItemPrices",
        "Users",
        "Roles",
        "Permissions",
        "RolePermissions",
        "UserRoles",
        "UserBranchAccess",
        "CustomerClassifications",
        "Customers",
    ];

    public static bool IsImplemented(string entityType) =>
        Implemented.Contains(entityType, StringComparer.Ordinal);

    public static bool IsRegistered(string entityType) =>
        IsImplemented(entityType);
}
