using Microsoft.EntityFrameworkCore;

namespace XtraPOS.Api.Data;

public sealed partial class BranchDbContext
{
    private static void ConfigureCatalog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Item>(entity =>
        {
            entity.ToTable("Items");
            entity.HasKey(e => e.ItemId);
            entity.Property(e => e.ItemId).HasColumnName("ItemID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.ItemNumber).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ItemName).HasMaxLength(500).IsRequired();
            entity.Property(e => e.ItemNameSearchStrict).HasMaxLength(500);
            entity.Property(e => e.ItemNameSearchLoose).HasMaxLength(500);
            entity.Property(e => e.ItemType).HasMaxLength(100);
            entity.Property(e => e.Manufacturer).HasMaxLength(100);
            entity.Property(e => e.Color).HasMaxLength(100);
            entity.Property(e => e.Taste).HasMaxLength(100);
            entity.Property(e => e.Size).HasMaxLength(100);
            entity.Property(e => e.Packing).HasMaxLength(100);
            entity.Property(e => e.Classification).HasMaxLength(200);
            entity.Property(e => e.CountryOfOrigin).HasMaxLength(100);
            entity.Property(e => e.ProductType).HasMaxLength(50);
            entity.Property(e => e.ParentItemId).HasColumnName("ParentItemID");
            entity.Property(e => e.RowVersion).IsRowVersion();
            entity.HasIndex(e => e.ItemNumber).IsUnique().HasDatabaseName("UQ_Items_ItemNumber");
            entity.HasIndex(e => e.ItemName).HasDatabaseName("IX_Items_ItemName");
            entity.HasIndex(e => e.ItemType).HasDatabaseName("IX_Items_ItemType");
            entity.HasIndex(e => e.Classification).HasDatabaseName("IX_Items_Classification");
            entity.HasOne(e => e.ParentItem)
                .WithMany()
                .HasForeignKey(e => e.ParentItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ItemUnit>(entity =>
        {
            entity.ToTable("ItemUnits");
            entity.HasKey(e => e.ItemUnitId);
            entity.Property(e => e.ItemUnitId).HasColumnName("ItemUnitID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.ItemId).HasColumnName("ItemID");
            entity.Property(e => e.UnitsPerPackage).HasPrecision(18, 4);
            entity.Property(e => e.InternalUnitItemId).HasColumnName("InternalUnitItemID");
            entity.Property(e => e.InternalUnitItemNumber).HasMaxLength(100);
            entity.Property(e => e.InternalUnitItemName).HasMaxLength(500);
            entity.HasIndex(e => e.ItemId).HasDatabaseName("IX_ItemUnits_ItemID");
            entity.HasIndex(e => e.InternalUnitItemId).HasDatabaseName("IX_ItemUnits_InternalUnitItemID");
            entity.HasIndex(e => e.InternalUnitItemNumber).HasDatabaseName("IX_ItemUnits_InternalUnitItemNumber");
            entity.HasOne(e => e.Item).WithMany().HasForeignKey(e => e.ItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.InternalUnitItem).WithMany().HasForeignKey(e => e.InternalUnitItemId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("CK_ItemUnits_UnitsPerPackage", "[UnitsPerPackage] > 0"));
        });

        modelBuilder.Entity<PriceType>(entity =>
        {
            entity.ToTable("PriceTypes");
            entity.HasKey(e => e.PriceTypeId);
            entity.Property(e => e.PriceTypeId).HasColumnName("PriceTypeID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.PriceCode).HasMaxLength(50).IsRequired();
            entity.Property(e => e.PriceName).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.PriceCode).IsUnique().HasDatabaseName("UQ_PriceTypes_PriceCode");
        });

        modelBuilder.Entity<ItemPrice>(entity =>
        {
            entity.ToTable("ItemPrices");
            entity.HasKey(e => e.ItemPriceId);
            entity.Property(e => e.ItemPriceId).HasColumnName("ItemPriceID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.ItemId).HasColumnName("ItemID");
            entity.Property(e => e.PriceTypeId).HasColumnName("PriceTypeID");
            entity.Property(e => e.BranchId).HasColumnName("BranchID");
            entity.Property(e => e.Price).HasPrecision(19, 4);
            entity.Property(e => e.RowVersion).IsRowVersion();
            entity.HasIndex(e => new { e.ItemId, e.PriceTypeId })
                .IsUnique()
                .HasFilter("[BranchID] IS NULL")
                .HasDatabaseName("UX_ItemPrices_Default");
            entity.HasIndex(e => new { e.ItemId, e.PriceTypeId, e.BranchId })
                .IsUnique()
                .HasFilter("[BranchID] IS NOT NULL")
                .HasDatabaseName("UX_ItemPrices_Branch");
            entity.HasIndex(e => e.BranchId).HasDatabaseName("IX_ItemPrices_BranchID");
            entity.HasOne(e => e.Item).WithMany().HasForeignKey(e => e.ItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.PriceType).WithMany().HasForeignKey(e => e.PriceTypeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Branch).WithMany().HasForeignKey(e => e.BranchId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ItemPriceHistory>(entity =>
        {
            entity.ToTable("ItemPriceHistory");
            entity.HasKey(e => e.ItemPriceHistoryId);
            entity.Property(e => e.ItemPriceHistoryId).HasColumnName("ItemPriceHistoryID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.ItemId).HasColumnName("ItemID");
            entity.Property(e => e.PriceTypeId).HasColumnName("PriceTypeID");
            entity.Property(e => e.BranchId).HasColumnName("BranchID");
            entity.Property(e => e.OldPrice).HasPrecision(19, 4);
            entity.Property(e => e.NewPrice).HasPrecision(19, 4);
            entity.Property(e => e.ChangedByUserId).HasColumnName("ChangedByUserID");
            entity.Property(e => e.ChangeSource).HasMaxLength(50);
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.HasIndex(e => new { e.ItemId, e.ChangedAt }).HasDatabaseName("IX_ItemPriceHistory_ItemID_ChangedAt");
        });

        modelBuilder.Entity<ItemBranchInventory>(entity =>
        {
            entity.ToTable("ItemBranchInventory");
            entity.HasKey(e => e.ItemBranchInventoryId);
            entity.Property(e => e.ItemBranchInventoryId).HasColumnName("ItemBranchInventoryID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.BranchId).HasColumnName("BranchID");
            entity.Property(e => e.ItemId).HasColumnName("ItemID");
            entity.Property(e => e.UnitCost).HasPrecision(19, 4);
            entity.Property(e => e.DailySalesRate).HasPrecision(18, 6).IsRequired(false);
            entity.Property(e => e.AvailabilityDays).HasPrecision(18, 4);
            entity.Property(e => e.IsAvailable).HasDefaultValue(true);
            entity.Property(e => e.RowVersion).IsRowVersion();
            entity.HasIndex(e => new { e.BranchId, e.ItemId }).IsUnique().HasDatabaseName("UQ_ItemBranchInventory_BranchID_ItemID");
            entity.HasIndex(e => new { e.BranchId, e.DailySalesRate }).HasDatabaseName("IX_ItemBranchInventory_BranchID_DailySalesRate");
            entity.HasIndex(e => new { e.BranchId, e.LastSaleAt }).HasDatabaseName("IX_ItemBranchInventory_BranchID_LastSaleAt");
            entity.HasOne(e => e.Branch).WithMany().HasForeignKey(e => e.BranchId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Item).WithMany().HasForeignKey(e => e.ItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BranchStockBalance>(entity =>
        {
            entity.ToTable("BranchStockBalances");
            entity.HasKey(e => e.BranchStockBalanceId);
            entity.Property(e => e.BranchStockBalanceId).HasColumnName("BranchStockBalanceID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.BranchId).HasColumnName("BranchID");
            entity.Property(e => e.ItemId).HasColumnName("ItemID");
            entity.Property(e => e.OnHandQuantity).HasPrecision(18, 4);
            entity.Property(e => e.UnitCost).HasPrecision(19, 4);
            entity.Property(e => e.RowVersion).IsRowVersion();
            entity.HasIndex(e => new { e.BranchId, e.ItemId }).IsUnique().HasDatabaseName("UQ_BranchStockBalances_BranchID_ItemID");
            entity.HasOne(e => e.Branch).WithMany().HasForeignKey(e => e.BranchId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Item).WithMany().HasForeignKey(e => e.ItemId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_BranchStockBalances_Initialization",
                "([IsQuantityInitialized] = 0 AND [OnHandQuantity] IS NULL) OR ([IsQuantityInitialized] = 1 AND [OnHandQuantity] IS NOT NULL)"));
        });

        modelBuilder.Entity<StockMovementType>(entity =>
        {
            entity.ToTable("StockMovementTypes");
            entity.HasKey(e => e.MovementTypeId);
            entity.Property(e => e.MovementTypeId).HasColumnName("MovementTypeID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.MovementCode).HasMaxLength(50).IsRequired();
            entity.Property(e => e.MovementName).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.MovementCode).IsUnique().HasDatabaseName("UQ_StockMovementTypes_MovementCode");
        });

        modelBuilder.Entity<StockMovement>(entity =>
        {
            entity.ToTable("StockMovements");
            entity.HasKey(e => e.StockMovementId);
            entity.Property(e => e.StockMovementId).HasColumnName("StockMovementID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.BranchId).HasColumnName("BranchID");
            entity.Property(e => e.ItemId).HasColumnName("ItemID");
            entity.Property(e => e.MovementTypeId).HasColumnName("MovementTypeID");
            entity.Property(e => e.QuantityDelta).HasPrecision(18, 4);
            entity.Property(e => e.UnitCost).HasPrecision(19, 4);
            entity.Property(e => e.CreatedByUserId).HasColumnName("CreatedByUserID");
            entity.Property(e => e.ReferenceType).HasMaxLength(50);
            entity.Property(e => e.ReferenceId).HasColumnName("ReferenceID");
            entity.Property(e => e.MovementGroupId).HasColumnName("MovementGroupID");
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.SourceType).HasMaxLength(40);
            entity.Property(e => e.SourceDocumentId).HasColumnName("SourceDocumentID");
            entity.Property(e => e.SourceLineId).HasColumnName("SourceLineID");
            entity.Property(e => e.PostingMode).HasMaxLength(20);
            entity.Property(e => e.PreviousQuantity).HasPrecision(18, 4);
            entity.Property(e => e.ObservedQuantity).HasPrecision(18, 4);
            entity.Property(e => e.ResultingQuantity).HasPrecision(18, 4);
            entity.Property(e => e.ResultingUnitCost).HasPrecision(19, 4);
            entity.HasIndex(e => new { e.BranchId, e.SourceType, e.SourceLineId })
                .IsUnique()
                .HasFilter("[SourceLineID] IS NOT NULL")
                .HasDatabaseName("UX_StockMovements_Branch_SourceLine");
            entity.HasIndex(e => new { e.BranchId, e.ItemId, e.OccurredAt }).HasDatabaseName("IX_StockMovements_BranchID_ItemID_OccurredAt");
            entity.HasIndex(e => new { e.ItemId, e.OccurredAt }).HasDatabaseName("IX_StockMovements_ItemID_OccurredAt");
            entity.HasIndex(e => new { e.ReferenceType, e.ReferenceId }).HasDatabaseName("IX_StockMovements_ReferenceType_ReferenceID");
            entity.HasIndex(e => e.MovementGroupId).HasDatabaseName("IX_StockMovements_MovementGroupID");
            entity.HasOne(e => e.MovementType).WithMany().HasForeignKey(e => e.MovementTypeId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
