using Microsoft.EntityFrameworkCore;
using PharmaOS.Platform.Host.MasterData;

namespace PharmaOS.Platform.Host;

/// <summary>
/// EF Core mapping onto the generated schema (database/schema). The schema is owned by the catalog,
/// not by EF migrations: this context never creates or alters tables.
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<Uom> Uoms => Set<Uom>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Uom>(e =>
        {
            e.ToTable("uom", "mdm");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(50);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(255);
            e.Property(x => x.Dimension).HasColumnName("dimension").HasMaxLength(30);
            e.Property(x => x.Ext).HasColumnName("ext").HasColumnType("jsonb");
            // row_version: the WHERE clause checks the client's If-Match value; the trigger increments it
            // and EF reads the new value back with RETURNING.
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        });
    }
}
