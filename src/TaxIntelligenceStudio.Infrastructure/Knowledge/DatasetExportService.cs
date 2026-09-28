using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Infrastructure.Persistence;

namespace TaxIntelligenceStudio.Infrastructure.Knowledge;

public sealed class DatasetExportService(TaxIntelligenceDbContext dbContext)
    : IDatasetExportService
{
    private const int ChunkSize = 3_500;

    public async Task<int> ExportJsonLinesAsync(
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        Directory.CreateDirectory(directory!);

        var count = 0;
        await using var stream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        await foreach (var document in dbContext.KnowledgeDocuments
                           .AsNoTracking()
                           .Where(item => item.IsCurrent)
                           .OrderBy(item => item.SourceUrl)
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken))
        {
            foreach (var chunk in Chunk(document.Content, ChunkSize))
            {
                var record = new
                {
                    messages = new object[]
                    {
                        new
                        {
                            role = "system",
                            content = "Answer only from the supplied authoritative IRS evidence and cite its source URL."
                        },
                        new
                        {
                            role = "user",
                            content = $"Provide the IRS source passage titled \"{document.Title}\"."
                        },
                        new
                        {
                            role = "assistant",
                            content = $"{chunk}\n\nSource: {document.SourceUrl}"
                        }
                    },
                    source = new
                    {
                        url = document.SourceUrl,
                        document.Title,
                        document.Version,
                        document.ContentHash,
                        document.RetrievedAt,
                        document.LastVerifiedAt
                    }
                };

                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(record).AsMemory(),
                    cancellationToken);
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> Chunk(string text, int maximumLength)
    {
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(maximumLength, text.Length - offset);
            if (offset + length < text.Length)
            {
                var boundary = text.LastIndexOf(' ', offset + length - 1, length);
                if (boundary > offset)
                {
                    length = boundary - offset;
                }
            }

            var chunk = text.Substring(offset, length).Trim();
            if (chunk.Length > 0)
            {
                yield return chunk;
            }

            offset += Math.Max(length, 1);
        }
    }
}
