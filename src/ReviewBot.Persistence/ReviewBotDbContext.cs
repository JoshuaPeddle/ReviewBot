using Microsoft.EntityFrameworkCore;
using ReviewBot.Persistence.Entities;

namespace ReviewBot.Persistence;

public sealed class ReviewBotDbContext(DbContextOptions<ReviewBotDbContext> options) : DbContext(options)
{
    public DbSet<PrReviewStateRecord> PrReviewStates => Set<PrReviewStateRecord>();
    public DbSet<ReviewJobRecord> ReviewJobs => Set<ReviewJobRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PrReviewStateRecord>(entity =>
        {
            entity.HasKey(r => new { r.InstallationId, r.RepoFullName, r.PullNumber });
            entity.Property(r => r.RepoFullName).HasMaxLength(200);
            entity.Property(r => r.LastSha).HasMaxLength(64);
            entity.Property(r => r.ReviewedAt).IsRequired();
        });

        modelBuilder.Entity<ReviewJobRecord>(entity =>
        {
            entity.HasKey(record => record.DeliveryId);
            entity.Property(record => record.DeliveryId).HasMaxLength(64);
            entity.Property(record => record.Owner).HasMaxLength(100);
            entity.Property(record => record.Repo).HasMaxLength(100);
            entity.Property(record => record.HeadSha).HasMaxLength(64);
            entity.Property(record => record.Reason).HasMaxLength(32);
            entity.Property(record => record.Status).HasMaxLength(24);
            entity.Property(record => record.LeaseToken).HasMaxLength(32);
            entity.Property(record => record.LastError).HasMaxLength(4000);
            entity.HasIndex(record => new { record.Status, record.AvailableAt, record.CreatedAt });
            entity.HasIndex(record => record.LeaseToken).IsUnique();
            entity.HasIndex(record => new
            {
                record.InstallationId,
                record.Owner,
                record.Repo,
                record.PrNumber,
                record.HeadSha
            });
        });
    }
}
