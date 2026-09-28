using Microsoft.EntityFrameworkCore;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Knowledge;
using TaxIntelligenceStudio.Infrastructure.Persistence;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class DatasetExportServiceTests
{
    [Fact]
    public async Task ExportJsonLinesAsync_EmitsGroundedCitationAndVersion()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jsonl");
        var options = new DbContextOptionsBuilder<TaxIntelligenceDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var context = new TaxIntelligenceDbContext(options);
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        context.KnowledgeDocuments.Add(new KnowledgeDocument
        {
            SourceUrl = "https://www.irs.gov/publications/p17",
            Title = "Publication 17",
            Content = "Verified IRS evidence.",
            ContentHash = new string('B', 64),
            Version = 2,
            IsCurrent = true,
            RetrievedAt = DateTimeOffset.UtcNow,
            LastVerifiedAt = DateTimeOffset.UtcNow,
            ContentType = "text/html"
        });
        await context.SaveChangesAsync();

        try
        {
            var count = await new DatasetExportService(context)
                .ExportJsonLinesAsync(outputPath);
            var text = await File.ReadAllTextAsync(outputPath);

            Assert.Equal(1, count);
            Assert.Contains("Verified IRS evidence.", text);
            Assert.Contains("https://www.irs.gov/publications/p17", text);
            Assert.Contains("\"Version\":2", text);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}
