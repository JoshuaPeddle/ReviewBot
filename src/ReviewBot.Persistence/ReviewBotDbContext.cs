using Microsoft.EntityFrameworkCore;
using ReviewBot.Persistence.Entities;

namespace ReviewBot.Persistence;

public sealed class ReviewBotDbContext(DbContextOptions<ReviewBotDbContext> options) : DbContext(options)
{
    public DbSet<DeliveryRecord> Deliveries => Set<DeliveryRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeliveryRecord>(entity =>
        {
            entity.HasKey(record => record.DeliveryId);
            entity.Property(record => record.DeliveryId).HasMaxLength(64);
            entity.Property(record => record.ProcessedAt).IsRequired();
            entity.HasIndex(record => record.ProcessedAt);
        });
    }
}
