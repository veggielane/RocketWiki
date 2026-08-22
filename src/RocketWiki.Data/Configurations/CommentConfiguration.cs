using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RocketWiki.Core.Entities;

namespace RocketWiki.Data.Configurations;

public class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.ToTable("Comments");
        builder.HasKey(c => c.Id);

        // No explicit HasColumnType: an unbounded string with no HasMaxLength already
        // defaults to nvarchar(max) on SQL Server; an explicit "nvarchar(max)" string is
        // SQL-Server-only syntax and fails schema creation on SQLite (its type-name
        // grammar accepts numeric length args but not the keyword MAX).
        builder.Property(c => c.Body).IsRequired();
        builder.Property(c => c.CreatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(c => c.EditedAtUtc).HasColumnType("datetime2(3)");

        builder.HasIndex(c => new { c.PageId, c.CreatedAtUtc });

        builder.HasOne(c => c.Page)
            .WithMany(p => p.Comments)
            .HasForeignKey(c => c.PageId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(c => c.ParentComment)
            .WithMany(c => c.Replies)
            .HasForeignKey(c => c.ParentCommentId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(c => c.Author)
            .WithMany()
            .HasForeignKey(c => c.AuthorUserId)
            .OnDelete(DeleteBehavior.NoAction);

        // Deliberately no global query filter: IsDeleted is a tombstone that keeps
        // thread shape (data-model.md) — the application blanks the body, it does not
        // hide the row.
    }
}
