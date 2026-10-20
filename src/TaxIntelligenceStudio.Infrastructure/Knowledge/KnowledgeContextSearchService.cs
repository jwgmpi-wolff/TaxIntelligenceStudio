using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Persistence;
using TaxIntelligenceStudio.Infrastructure.Sources;

namespace TaxIntelligenceStudio.Infrastructure.Knowledge;

public sealed partial class KnowledgeContextSearchService(
    TaxIntelligenceDbContext dbContext) : IKnowledgeContextSearchService
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "also", "and", "are", "can", "could", "does", "from", "have",
        "into", "irs", "its", "more", "please", "should", "that", "the", "their", "then",
        "this", "through", "what", "when", "where", "which", "with", "would", "your"
    };

    public async Task<IReadOnlyList<RetrievedSourceContext>> SearchAsync(
        string query,
        int maximumDocuments,
        int maximumCharacters,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDocuments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var terms = WordPattern().Matches(query)
            .Select(match => match.Value)
            .Where(term => term.Length >= 3 && !StopWords.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        if (terms.Length == 0)
        {
            return [];
        }

        var parameter = Expression.Parameter(typeof(KnowledgeDocument), "document");
        var content = Expression.Property(parameter, nameof(KnowledgeDocument.Content));
        var title = Expression.Property(parameter, nameof(KnowledgeDocument.Title));
        var functions = Expression.Property(null, typeof(EF).GetProperty(nameof(EF.Functions))!);
        var likeMethod = typeof(DbFunctionsExtensions).GetMethod(
            nameof(DbFunctionsExtensions.Like),
            [typeof(DbFunctions), typeof(string), typeof(string)])!;
        Expression? predicate = null;
        foreach (var term in terms)
        {
            var contentMatch = Expression.Call(
                likeMethod,
                functions,
                content,
                Expression.Constant($"%{term}%"));
            var titleMatch = Expression.Call(
                likeMethod,
                functions,
                title,
                Expression.Constant($"%{term}%"));
            var match = Expression.OrElse(contentMatch, titleMatch);
            predicate = predicate is null ? match : Expression.OrElse(predicate, match);
        }

        var filter = Expression.Lambda<Func<KnowledgeDocument, bool>>(predicate!, parameter);
        var candidates = await dbContext.KnowledgeDocuments
            .AsNoTracking()
            .Where(document => document.IsCurrent)
            .Where(filter)
            .Take(500)
            .ToListAsync(cancellationToken);

        var ranked = candidates
            .Where(document =>
                Uri.TryCreate(document.SourceUrl, UriKind.Absolute, out var sourceUri) &&
                AuthoritativeSourcePolicy.IsAllowedHttps(sourceUri))
            .Select(document => new
            {
                Document = document,
                Score = terms.Sum(term =>
                    (document.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ? 4 : 0) +
                    (document.Content.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Document.LastVerifiedAt)
            .Take(maximumDocuments);

        var results = new List<RetrievedSourceContext>();
        var charactersLeft = maximumCharacters;
        foreach (var candidate in ranked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (charactersLeft <= 0)
            {
                break;
            }

            var excerpt = CreateExcerpt(
                candidate.Document.Content,
                terms,
                Math.Min(charactersLeft, Math.Max(1, maximumCharacters / maximumDocuments)));
            if (string.IsNullOrWhiteSpace(excerpt))
            {
                continue;
            }

            results.Add(new RetrievedSourceContext(
                candidate.Document.Title,
                candidate.Document.SourceUrl,
                excerpt,
                candidate.Document.RetrievedAt,
                IsVerified: true,
                IsCached: true,
                LastVerifiedAt: candidate.Document.LastVerifiedAt,
                PublishedDate: candidate.Document.PublishedDate,
                EffectiveDate: candidate.Document.EffectiveDate));
            charactersLeft -= excerpt.Length;
        }

        return results;
    }

    private static string CreateExcerpt(string content, IReadOnlyList<string> terms, int maximumLength)
    {
        var match = terms
            .Select(term => content.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Max(0, match - Math.Min(500, maximumLength / 4));
        if (start > 0)
        {
            var boundary = content.IndexOf(' ', start);
            if (boundary >= 0 && boundary - start < 300)
            {
                start = boundary + 1;
            }
        }

        var length = Math.Min(content.Length - start, maximumLength);
        return content.Substring(start, length).Trim();
    }

    [GeneratedRegex("[A-Za-z0-9]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
