using Microsoft.EntityFrameworkCore;
using ReviewBot.Persistence.Entities;

namespace ReviewBot.Persistence;

public sealed class ReviewBotDbContext(DbContextOptions<ReviewBotDbContext> options) : DbContext(options)
{
    public DbSet<DeliveryRecord> Deliveries => Set<DeliveryRecord>();
    public DbSet<PrReviewStateRecord> PrReviewStates => Set<PrReviewStateRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeliveryRecord>(entity =>
        {
            entity.HasKey(record => record.DeliveryId);
            entity.Property(record => record.DeliveryId).HasMaxLength(64);
            entity.Property(record => record.ProcessedAt).IsRequired();
            entity.HasIndex(record => record.ProcessedAt);
        });

        modelBuilder.Entity<PrReviewStateRecord>(entity =>
        {
            entity.HasKey(r => new { r.InstallationId, r.RepoFullName, r.PullNumber });
            entity.Property(r => r.RepoFullName).HasMaxLength(200);
            entity.Property(r => r.LastSha).HasMaxLength(64);
            entity.Property(r => r.ReviewedAt).IsRequired();
        });
    }
}
