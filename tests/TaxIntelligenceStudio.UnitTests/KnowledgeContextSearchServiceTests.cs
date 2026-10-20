using Microsoft.EntityFrameworkCore;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Knowledge;
using TaxIntelligenceStudio.Infrastructure.Persistence;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class KnowledgeContextSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_RanksMatchesAndReturnsOnlyAllowlistedCachedSources()
    {
        var (context, cleanup) = await CreateContextAsync();
        await using (context)
        {
            try
            {
                var date = DateTimeOffset.UtcNow.AddDays(-20);
                context.KnowledgeDocuments.AddRange(
                    Document(
                        "https://www.irs.gov/publications/p17",
                        "Standard Deduction",
                        "The standard deduction amount is described in this cached IRS document. " +
                        new string('x', 300),
                        date,
                        new DateOnly(2025, 1, 1)),
                    Document(
                        "https://www.irs.gov/taxtopics/tc551",
                        "Capital Gains",
                        "Information about the standard deduction and investment income.",
                        date.AddDays(-2)),
                    Document(
                        "https://www.irs.gov.example.com/fake",
                        "Standard Deduction",
                        "Untrusted lookalike host discussing the standard deduction.",
                        date.AddDays(1)),
                    Document(
                        "https://www.irs.gov:8443/publications/p17",
                        "Standard Deduction",
                        "Non-default source port discussing the standard deduction.",
                        date.AddDays(2)));
                await context.SaveChangesAsync();

                var results = await new KnowledgeContextSearchService(context)
                    .SearchAsync("What is the standard deduction?", 3, 100);

                Assert.Equal(2, results.Count);
                Assert.Equal("Standard Deduction", results[0].Title);
                Assert.Contains(results, result => result.SourceUrl == "https://www.irs.gov/taxtopics/tc551");
                Assert.DoesNotContain(results, result => result.SourceUrl.Contains("example.com", StringComparison.Ordinal));
                Assert.All(results, result =>
                {
                    Assert.True(result.IsCached);
                    Assert.True(result.IsVerified);
                    Assert.NotNull(result.RetrievedAt);
                    Assert.True(result.Content.Length <= 100);
                });
                Assert.Equal(date, results[0].RetrievedAt);
                Assert.Equal(new DateOnly(2025, 1, 1), results[0].PublishedDate);
            }
            finally
            {
                await cleanup();
            }
        }
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyWhenNoCurrentSourceMatches()
    {
        var (context, cleanup) = await CreateContextAsync();
        await using (context)
        {
            try
            {
                context.KnowledgeDocuments.Add(Document(
                    "https://www.irs.gov/pub",
                    "Retirement",
                    "This document discusses retirement accounts.",
                    DateTimeOffset.UtcNow));
                await context.SaveChangesAsync();

                var results = await new KnowledgeContextSearchService(context)
                    .SearchAsync("foreign earned income exclusion", 3, 500);

                Assert.Empty(results);
            }
            finally
            {
                await cleanup();
            }
        }
    }

    private static KnowledgeDocument Document(
        string sourceUrl,
        string title,
        string content,
        DateTimeOffset verifiedAt,
        DateOnly? publishedDate = null) =>
        new()
        {
            SourceUrl = sourceUrl,
            Title = title,
            Content = content,
            ContentHash = new string('A', 64),
            Version = 1,
            IsCurrent = true,
            RetrievedAt = verifiedAt,
            LastVerifiedAt = verifiedAt,
            PublishedDate = publishedDate
        };

    private static async Task<(TaxIntelligenceDbContext Context, Func<Task> Cleanup)> CreateContextAsync()
    {
        var options = new DbContextOptionsBuilder<TaxIntelligenceDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new TaxIntelligenceDbContext(options);
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        return (context, () => context.Database.CloseConnectionAsync());
    }
}
