using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");

        // Clustered PK (TimestampUtc, Id) per data-model.md. Id stays store-generated
        // (bigint identity) even though it's part of a composite key.
        builder.HasKey(e => new { e.TimestampUtc, e.Id });
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.TimestampUtc).HasColumnType("datetime2(3)");
        builder.Property(e => e.Action).HasColumnType("varchar(64)").IsRequired();
        builder.Property(e => e.SpaceKey).HasMaxLength(32);
        builder.Property(e => e.RequestId).HasColumnType("varchar(64)").IsRequired();
        builder.Property(e => e.ClientIp).HasColumnType("varchar(45)").IsRequired();
        builder.Property(e => e.McpClient).HasMaxLength(128);
        // See CommentConfiguration for why there's no explicit "nvarchar(max)" here.
        builder.Property(e => e.DetailsJson);

        builder.HasIndex(e => new { e.UserId, e.TimestampUtc });
        builder.HasIndex(e => new { e.SubjectId, e.TimestampUtc });
        builder.HasIndex(e => new { e.Action, e.TimestampUtc });

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        // TODO(sql-server): monthly partition function/scheme on TimestampUtc, and
        // restricting the app's SQL login to INSERT/SELECT only on this table
        // (data-model.md, design.md §7). Both are server/security-principal-level DDL
        // outside the EF relational model — add as raw SQL in a follow-up migration
        // once the partition boundary scheme and login name are decided. This
        // migration creates an ordinary clustered index on (TimestampUtc, Id) so
        // insert/query behavior is correct ahead of partitioning.
    }
}
