using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace ReviewBot.Persistence.Migrations;

[DbContext(typeof(ReviewBotDbContext))]
partial class ReviewBotDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.8")
            .HasAnnotation("Relational:MaxIdentifierLength", 64);

        modelBuilder.Entity("ReviewBot.Persistence.Entities.DeliveryRecord", entity =>
        {
            entity.Property<string>("DeliveryId")
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            entity.Property<DateTimeOffset>("ProcessedAt")
                .HasColumnType("TEXT");

            entity.HasKey("DeliveryId");

            entity.HasIndex("ProcessedAt");

            entity.ToTable("Deliveries");
        });
    }
}
