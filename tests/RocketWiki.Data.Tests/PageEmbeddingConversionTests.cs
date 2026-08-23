using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// On SQLite, Embedding is stored via the hand-rolled float[]-to-bytes conversion
/// (RocketWikiDbContext's provider-conditional mapping; data-model.md: "On SQLite
/// (tests) this table maps Embedding to a blob"). SQL Server graduated to the native
/// vector(1536) column — its round-trip twin lives in the SqlServer tier's
/// VectorSearchTests. This proves the blob conversion round-trips exactly through a
/// real provider rather than trusting the Buffer.BlockCopy math by eye.
/// </summary>
public class PageEmbeddingConversionTests : SqliteTestBase
{
    [Fact]
    public void Embedding_RoundTrips_ExactFloatValues()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var vector = Enumerable.Range(0, 1536).Select(i => (float)Math.Sin(i * 0.01)).ToArray();
        var embedding = new PageEmbedding
        {
            PageId = page.Id,
            ChunkIndex = 0,
            HeadingPath = "Introduction",
            ChunkHash = new byte[32],
            Embedding = vector,
            Model = "test-embedding-model",
            UpdatedAtUtc = DateTime.UtcNow,
        };

        using (var writeContext = CreateContext())
        {
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.PageEmbeddings.Add(embedding);
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();
        var reloaded = readContext.PageEmbeddings.Single(e => e.Id == embedding.Id);

        Assert.Equal(vector.Length, reloaded.Embedding.Length);
        Assert.Equal(vector, reloaded.Embedding);
    }

    [Fact]
    public void Embedding_EmptyArray_RoundTrips()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var embedding = new PageEmbedding
        {
            PageId = page.Id,
            ChunkIndex = 0,
            HeadingPath = "Empty",
            ChunkHash = new byte[32],
            Embedding = Array.Empty<float>(),
            Model = "test-embedding-model",
            UpdatedAtUtc = DateTime.UtcNow,
        };

        using (var writeContext = CreateContext())
        {
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.PageEmbeddings.Add(embedding);
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();
        var reloaded = readContext.PageEmbeddings.Single(e => e.Id == embedding.Id);

        Assert.Empty(reloaded.Embedding);
    }

    [Fact]
    public void Embedding_UniquePerPageAndChunkIndex_RejectsDuplicate()
    {
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageEmbeddings.Add(new PageEmbedding
        {
            PageId = page.Id,
            ChunkIndex = 0,
            HeadingPath = "First",
            ChunkHash = new byte[32],
            Embedding = new float[] { 1f, 2f, 3f },
            Model = "m",
            UpdatedAtUtc = DateTime.UtcNow,
        });
        context.SaveChanges();

        context.PageEmbeddings.Add(new PageEmbedding
        {
            PageId = page.Id,
            ChunkIndex = 0, // duplicate (PageId, ChunkIndex)
            HeadingPath = "Duplicate",
            ChunkHash = new byte[32],
            Embedding = new float[] { 4f, 5f, 6f },
            Model = "m",
            UpdatedAtUtc = DateTime.UtcNow,
        });

        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }
}
