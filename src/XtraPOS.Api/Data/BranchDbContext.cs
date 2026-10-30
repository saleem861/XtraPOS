using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace XtraPOS.Api.Data;

public sealed partial class BranchDbContext(DbContextOptions<BranchDbContext> options) : DbContext(options)
{
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<PosStation> PosStations => Set<PosStation>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<SchemaMigrationRecord> SchemaHistory => Set<SchemaMigrationRecord>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserBranchAccess> UserBranchAccess => Set<UserBranchAccess>();
    public DbSet<UserLoginAudit> UserLoginAudit => Set<UserLoginAudit>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ItemUnit> ItemUnits => Set<ItemUnit>();
    public DbSet<PriceType> PriceTypes => Set<PriceType>();
    public DbSet<ItemPrice> ItemPrices => Set<ItemPrice>();
    public DbSet<ItemPriceHistory> ItemPriceHistory => Set<ItemPriceHistory>();
    public DbSet<ItemBranchInventory> ItemBranchInventory => Set<ItemBranchInventory>();
    public DbSet<BranchStockBalance> BranchStockBalances => Set<BranchStockBalance>();
    public DbSet<StockMovementType> StockMovementTypes => Set<StockMovementType>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var utc = new ValueConverter<DateTime, DateTime>(
            value => value,
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            value => value,
            value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            if (property.ClrType == typeof(DateTime))
            {
                property.SetValueConverter(utc);
                property.SetColumnType("datetime2(0)");
            }
            else if (property.ClrType == typeof(DateTime?))
            {
                property.SetValueConverter(utcNullable);
                property.SetColumnType("datetime2(0)");
            }
        }

        modelBuilder.Entity<Business>(entity =>
        {
            entity.ToTable("Businesses");
            entity.HasKey(e => e.BusinessId);
            entity.Property(e => e.BusinessId).HasColumnName("BusinessID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.BusinessName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.BusinessDisplayName).HasMaxLength(200);
            entity.Ignore(e => e.Description);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<Branch>(entity =>
        {
            entity.ToTable("Branches");
            entity.HasKey(e => e.BranchId);
            entity.Property(e => e.BranchId).HasColumnName("BranchID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.BusinessId).HasColumnName("BusinessID");
            entity.Property(e => e.BranchCode).HasMaxLength(50);
            entity.Property(e => e.BranchName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.BranchType).HasMaxLength(30).IsRequired();
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.HasOne(e => e.Business)
                .WithMany(e => e.Branches)
                .HasForeignKey(e => e.BusinessId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.BusinessId, e.BranchCode })
                .IsUnique()
                .HasFilter("[BranchCode] IS NOT NULL")
                .HasDatabaseName("UX_Branches_BusinessID_BranchCode");
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_Branches_BranchType",
                "[BranchType] IN (N'Retail', N'Warehouse', N'RetailAndWarehouse')"));
        });

        modelBuilder.Entity<PosStation>(entity =>
        {
            entity.ToTable("POSStations");
            entity.HasKey(e => e.StationId);
            entity.Property(e => e.StationId).HasColumnName("StationID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.BranchId).HasColumnName("BranchID");
            entity.Property(e => e.StationName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.HasOne(e => e.Branch)
                .WithMany(e => e.Stations)
                .HasForeignKey(e => e.BranchId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.BranchId, e.StationNumber })
                .IsUnique()
                .HasDatabaseName("UQ_POSStations_BranchID_StationNumber");
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_POSStations_StationNumber",
                "[StationNumber] > 0"));
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("Devices");
            entity.HasKey(e => e.DeviceId);
            entity.Property(e => e.DeviceId).HasColumnName("DeviceID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.BranchId).HasColumnName("BranchID");
            entity.Property(e => e.StationId).HasColumnName("StationID");
            entity.Property(e => e.DeviceName).HasMaxLength(150).IsRequired();
            entity.Property(e => e.DeviceType).HasMaxLength(30).IsRequired();
            entity.Property(e => e.DeviceTokenHash).HasColumnType("varbinary(64)");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.HasOne(e => e.Branch)
                .WithMany(e => e.Devices)
                .HasForeignKey(e => e.BranchId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Station)
                .WithMany(e => e.Devices)
                .HasForeignKey(e => e.StationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_Devices_DeviceType",
                "[DeviceType] IN (N'POS', N'Management', N'CustomerDisplay', N'Warehouse')"));
        });

        modelBuilder.Entity<SchemaMigrationRecord>(entity =>
        {
            entity.ToTable("DatabaseSchemaHistory");
            entity.HasKey(e => e.MigrationId);
            entity.Property(e => e.MigrationId).HasColumnName("MigrationID").HasMaxLength(100);
            entity.Property(e => e.MigrationName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ApplicationVersion).HasMaxLength(50);
        });

        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.ToTable("SystemSettings");
            entity.HasKey(e => e.SettingKey);
            entity.Property(e => e.SettingKey).HasMaxLength(100);
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.UserId).HasColumnName("UserID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.Username).HasMaxLength(100).IsRequired();
            entity.Property(e => e.NormalizedUsername).HasMaxLength(100).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(150).IsRequired();
            entity.Property(e => e.FullName).HasMaxLength(300);
            entity.Property(e => e.EmployeeNumber).HasMaxLength(50);
            entity.Property(e => e.PasswordHash).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.HasIndex(e => e.NormalizedUsername).IsUnique().HasDatabaseName("UQ_Users_NormalizedUsername");
            entity.HasIndex(e => e.EmployeeNumber)
                .IsUnique()
                .HasFilter("[EmployeeNumber] IS NOT NULL")
                .HasDatabaseName("UX_Users_EmployeeNumber");
            entity.ToTable(table => table.HasCheckConstraint("CK_Users_FailedLoginCount", "[FailedLoginCount] >= 0"));
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("Roles");
            entity.HasKey(e => e.RoleId);
            entity.Property(e => e.RoleId).HasColumnName("RoleID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.RoleCode).HasMaxLength(50).IsRequired();
            entity.Property(e => e.RoleName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.HasIndex(e => e.RoleCode).IsUnique().HasDatabaseName("UQ_Roles_RoleCode");
        });

        modelBuilder.Entity<Permission>(entity =>
        {
            entity.ToTable("Permissions");
            entity.HasKey(e => e.PermissionId);
            entity.Property(e => e.PermissionId).HasColumnName("PermissionID").HasDefaultValueSql("NEWSEQUENTIALID()").ValueGeneratedOnAdd();
            entity.Property(e => e.PermissionCode).HasMaxLength(100).IsRequired();
            entity.Property(e => e.PermissionName).HasMaxLength(150).IsRequired();
            entity.Property(e => e.Module).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.HasIndex(e => e.PermissionCode).IsUnique().HasDatabaseName("UQ_Permissions_PermissionCode");
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.ToTable("UserRoles");
            entity.HasKey(e => new { e.UserId, e.RoleId });
            entity.Property(e => e.UserId).HasColumnName("UserID");
            entity.Property(e => e.RoleId).HasColumnName("RoleID");
            entity.HasOne(e => e.User).WithMany(e => e.UserRoles).HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Role).WithMany(e => e.UserRoles).HasForeignKey(e => e.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.ToTable("RolePermissions");
            entity.HasKey(e => new { e.RoleId, e.PermissionId });
            entity.Property(e => e.RoleId).HasColumnName("RoleID");
            entity.Property(e => e.PermissionId).HasColumnName("PermissionID");
            entity.HasOne(e => e.Role).WithMany(e => e.RolePermissions).HasForeignKey(e => e.RoleId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Permission).WithMany(e => e.RolePermissions).HasForeignKey(e => e.PermissionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserBranchAccess>(entity =>
        {
            entity.ToTable("UserBranchAccess");
            entity.HasKey(e => new { e.UserId, e.BranchId });
            entity.Property(e => e.UserId).HasColumnName("UserID");
            entity.Property(e => e.BranchId).HasColumnName("BranchID");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.HasOne(e => e.User).WithMany(e => e.BranchAccess).HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Branch).WithMany().HasForeignKey(e => e.BranchId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserLoginAudit>(entity =>
        {
            entity.ToTable("UserLoginAudit");
            entity.HasKey(e => e.LoginAuditId);
            entity.Property(e => e.LoginAuditId).HasColumnName("LoginAuditID").ValueGeneratedOnAdd();
            entity.Property(e => e.UserId).HasColumnName("UserID");
            entity.Property(e => e.DeviceId).HasColumnName("DeviceID");
            entity.Property(e => e.UsernameAttempted).HasMaxLength(100).IsRequired();
            entity.Property(e => e.FailureReason).HasMaxLength(100);
            entity.Property(e => e.ClientInfo).HasMaxLength(300);
            entity.HasIndex(e => e.OccurredAt).HasDatabaseName("IX_UserLoginAudit_OccurredAt");
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_UserLoginAudit_UserID");
            entity.HasIndex(e => e.DeviceId).HasDatabaseName("IX_UserLoginAudit_DeviceID");
        });

        ConfigureCatalog(modelBuilder);
    }
}
