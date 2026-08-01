using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace ReviewBot.Persistence.Migrations;

[DbContext(typeof(ReviewBotDbContext))]
[Migration("20260730000000_InitialCreate")]
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PrReviewStates",
            columns: table => new
            {
                InstallationId = table.Column<long>(type: "INTEGER", nullable: false),
                RepoFullName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                PullNumber = table.Column<int>(type: "INTEGER", nullable: false),
                LastSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ReviewedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
                table.PrimaryKey(
                    "PK_PrReviewStates",
                    row => new { row.InstallationId, row.RepoFullName, row.PullNumber }));

        migrationBuilder.CreateTable(
            name: "ReviewJobs",
            columns: table => new
            {
                DeliveryId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                InstallationId = table.Column<long>(type: "INTEGER", nullable: false),
                Owner = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Repo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                PrNumber = table.Column<int>(type: "INTEGER", nullable: false),
                HeadSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                Reason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                AvailableAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LeaseExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LeaseToken = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                LastError = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_ReviewJobs", row => row.DeliveryId));

        migrationBuilder.CreateIndex(
            name: "IX_ReviewJobs_InstallationId_Owner_Repo_PrNumber_HeadSha",
            table: "ReviewJobs",
            columns: ["InstallationId", "Owner", "Repo", "PrNumber", "HeadSha"]);

        migrationBuilder.CreateIndex(
            name: "IX_ReviewJobs_LeaseToken",
            table: "ReviewJobs",
            column: "LeaseToken",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ReviewJobs_Status_AvailableAt_CreatedAt",
            table: "ReviewJobs",
            columns: ["Status", "AvailableAt", "CreatedAt"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PrReviewStates");
        migrationBuilder.DropTable(name: "ReviewJobs");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "10.0.8");

        modelBuilder.Entity("ReviewBot.Persistence.Entities.PrReviewStateRecord", builder =>
        {
            builder.Property<long>("InstallationId").HasColumnType("INTEGER");
            builder.Property<string>("RepoFullName").HasMaxLength(200).HasColumnType("TEXT");
            builder.Property<int>("PullNumber").HasColumnType("INTEGER");
            builder.Property<string>("LastSha").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            builder.Property<DateTimeOffset>("ReviewedAt").HasColumnType("TEXT");
            builder.HasKey("InstallationId", "RepoFullName", "PullNumber");
            builder.ToTable("PrReviewStates");
        });

        modelBuilder.Entity("ReviewBot.Persistence.Entities.ReviewJobRecord", builder =>
        {
            builder.Property<string>("DeliveryId").HasMaxLength(64).HasColumnType("TEXT");
            builder.Property<int>("AttemptCount").HasColumnType("INTEGER");
            builder.Property<DateTimeOffset>("AvailableAt").HasColumnType("TEXT");
            builder.Property<DateTimeOffset?>("CompletedAt").HasColumnType("TEXT");
            builder.Property<DateTimeOffset>("CreatedAt").HasColumnType("TEXT");
            builder.Property<string>("HeadSha").HasMaxLength(64).HasColumnType("TEXT");
            builder.Property<long>("InstallationId").HasColumnType("INTEGER");
            builder.Property<string>("LastError").HasMaxLength(4000).HasColumnType("TEXT");
            builder.Property<DateTimeOffset?>("LeaseExpiresAt").HasColumnType("TEXT");
            builder.Property<string>("LeaseToken").HasMaxLength(32).HasColumnType("TEXT");
            builder.Property<string>("Owner").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            builder.Property<int>("PrNumber").HasColumnType("INTEGER");
            builder.Property<string>("Reason").IsRequired().HasMaxLength(32).HasColumnType("TEXT");
            builder.Property<string>("Repo").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            builder.Property<DateTimeOffset?>("StartedAt").HasColumnType("TEXT");
            builder.Property<string>("Status").IsRequired().HasMaxLength(24).HasColumnType("TEXT");
                builder.HasKey("DeliveryId");
            builder.HasIndex("LeaseToken").IsUnique();
            builder.HasIndex("Status", "AvailableAt", "CreatedAt");
            builder.HasIndex("InstallationId", "Owner", "Repo", "PrNumber", "HeadSha");
            builder.ToTable("ReviewJobs");
        });
#pragma warning restore 612, 618
    }
}
