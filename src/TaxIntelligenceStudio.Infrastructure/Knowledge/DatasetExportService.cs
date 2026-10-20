using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Infrastructure.Persistence;

namespace TaxIntelligenceStudio.Infrastructure.Knowledge;

public sealed class DatasetExportService(TaxIntelligenceDbContext dbContext)
    : IDatasetExportService
{
    private const int ChunkSize = 3_500;
    internal const long MaximumUploadBytes = 450_000_000;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task<DatasetExportResult> ExportJsonLinesAsync(
        string outputPath,
        CancellationToken cancellationToken = default) =>
        ExportJsonLinesAsync(outputPath, MaximumUploadBytes, cancellationToken);

    public Task<DatasetExportResult> ExportJsonLinesAsync(
        string outputPath,
        int datasetFileCount,
        CancellationToken cancellationToken = default) =>
        ExportJsonLinesAsync(outputPath, MaximumUploadBytes, cancellationToken, datasetFileCount);

    internal async Task<DatasetExportResult> ExportJsonLinesAsync(
        string outputPath,
        long maximumUploadBytes,
        CancellationToken cancellationToken = default,
        int datasetFileCount = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumUploadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(datasetFileCount);
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
                    }
                };

                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(record).AsMemory(),
                    cancellationToken);
                count++;
            }
        }

        await writer.FlushAsync(cancellationToken);
        if (count == 0)
        {
            throw new InvalidOperationException(
                "No current IRS source passages are available. Ingest IRS documents before exporting.");
        }
        var length = stream.Length;
        await writer.DisposeAsync();
        var uploadFiles = datasetFileCount == 0 && length <= maximumUploadBytes
            ? new[] { Path.GetFullPath(outputPath) }
            : await SplitUploadFilesAsync(
                outputPath, maximumUploadBytes, cancellationToken, datasetFileCount);
        var validationPath = Path.ChangeExtension(outputPath, ".validation-report.json");
        var validatedRecordCount = await ValidateJsonLinesAsync(outputPath, cancellationToken);
        if (validatedRecordCount != count)
        {
            throw new InvalidDataException(
                "The exported dataset record count did not match the validated record count.");
        }
        var trainingSplits = await DatasetTrainingSplitBuilder.BuildAsync(
            outputPath,
            cancellationToken);

        await File.WriteAllTextAsync(
            validationPath,
            JsonSerializer.Serialize(new
            {
                format = "openai-chat-completions-jsonl",
                isValid = true,
                recordCount = validatedRecordCount,
                errors = Array.Empty<string>()
            }, ManifestJsonOptions),
            new UTF8Encoding(false),
            cancellationToken);

        var exportHash = await ComputeSha256Async(outputPath, cancellationToken);
        var partMetadata = new List<DatasetPartMetadata>();
        foreach (var uploadFile in uploadFiles)
        {
            partMetadata.Add(new DatasetPartMetadata(
                Path.GetFileName(uploadFile),
                new FileInfo(uploadFile).Length,
                await ComputeSha256Async(uploadFile, cancellationToken)));
        }

        var manifestPath = Path.ChangeExtension(outputPath, ".manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                format = "openai-chat-completions-jsonl",
                createdAt = DateTimeOffset.UtcNow,
                recordCount = count,
                fullExport = new DatasetPartMetadata(
                    Path.GetFileName(outputPath),
                    length,
                    exportHash),
                uploadFiles = partMetadata,
                trainingSplits = new
                {
                    seed = trainingSplits.Seed,
                    trainingFile = Path.GetRelativePath(directory!, trainingSplits.TrainingFile),
                    validationFile = Path.GetRelativePath(directory!, trainingSplits.ValidationFile),
                    testFile = Path.GetRelativePath(directory!, trainingSplits.TestFile),
                    exampleMetadataFile = Path.GetRelativePath(directory!, trainingSplits.ExampleMetadataFile),
                    manifestPath = Path.GetRelativePath(directory!, trainingSplits.ManifestPath),
                    trainingSplits.TrainingRecordCount,
                    trainingSplits.ValidationRecordCount,
                    trainingSplits.TestRecordCount,
                    trainingSplits.DuplicateExcludedCount,
                    trainingSplits.PiiExcludedCount,
                    trainingSplits.UntrustedSourceExcludedCount
                },
                validationReport = Path.GetFileName(validationPath)
            }, ManifestJsonOptions),
            new UTF8Encoding(false),
            cancellationToken);
        return new DatasetExportResult(
            count,
            uploadFiles,
            manifestPath,
            exportHash,
            trainingSplits);
    }

    private static async Task<int> ValidateJsonLinesAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var recordCount = 0;
        await foreach (var line in File.ReadLinesAsync(filePath, cancellationToken))
        {
            recordCount++;
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException(
                    $"The exported JSONL contains an empty record at line {recordCount}.");
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    root.EnumerateObject().Count() != 1 ||
                    !root.TryGetProperty("messages", out var messages) ||
                    messages.ValueKind != JsonValueKind.Array ||
                    !HasRequiredMessages(messages))
                {
                    throw new InvalidDataException(
                        $"The exported JSONL record at line {recordCount} has an invalid chat-message shape.");
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"The exported JSONL record at line {recordCount} is not valid JSON.",
                    exception);
            }
        }

        return recordCount;
    }

    private static bool HasRequiredMessages(JsonElement messages)
    {
        var expectedRoles = new[] { "system", "user", "assistant" };
        var items = messages.EnumerateArray().ToArray();
        if (items.Length != expectedRoles.Length)
        {
            return false;
        }

        for (var index = 0; index < expectedRoles.Length; index++)
        {
            var message = items[index];
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("role", out var role) ||
                role.ValueKind != JsonValueKind.String ||
                role.GetString() != expectedRoles[index] ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(content.GetString()))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private sealed record DatasetPartMetadata(string FileName, long Bytes, string Sha256);

    internal static async Task<string[]> SplitUploadFilesAsync(
        string inputPath,
        long maximumBytes,
        CancellationToken cancellationToken = default,
        int datasetFileCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(datasetFileCount);
        var totalRecords = 0;
        if (datasetFileCount > 0)
        {
            await foreach (var line in File.ReadLinesAsync(inputPath, cancellationToken))
            {
                totalRecords++;
            }
            if (datasetFileCount > totalRecords)
            {
                throw new InvalidOperationException(
                    $"Cannot create {datasetFileCount:N0} non-empty dataset files from " +
                    $"{totalRecords:N0} records. Choose a smaller file count.");
            }

            var partIndex = 0;
            var recordsInPart = 0;
            long bytesInPart = 0;
            await foreach (var line in File.ReadLinesAsync(inputPath, cancellationToken))
            {
                bytesInPart += Encoding.UTF8.GetByteCount(line) + 1L;
                if (bytesInPart > maximumBytes)
                {
                    throw new InvalidOperationException(
                        $"The selected {datasetFileCount:N0} dataset files would exceed the " +
                        $"{maximumBytes:N0}-byte upload limit. Choose more files or 0 for automatic splitting. " +
                        "The full local-training export has been retained.");
                }
                if (++recordsInPart == RecordsPerPart(totalRecords, datasetFileCount, partIndex))
                {
                    partIndex++;
                    recordsInPart = 0;
                    bytesInPart = 0;
                }
            }
        }
        var directory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(inputPath))!,
            $"{Path.GetFileNameWithoutExtension(inputPath)}-foundry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var files = new List<string>();
        await using var input = new FileStream(
            inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, useAsync: true);
        using var reader = new StreamReader(input, new UTF8Encoding(false, true));
        StreamWriter? writer = null;
        long partBytes = 0;
        var partRecords = 0;
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                var lineBytes = Encoding.UTF8.GetByteCount(line) + 1L;
                if (string.IsNullOrWhiteSpace(line) || lineBytes > maximumBytes)
                {
                    throw new InvalidOperationException(
                        "The training dataset contains an empty or oversized record and cannot be split for upload.");
                }
                if (writer is null ||
                    (datasetFileCount == 0
                        ? partBytes + lineBytes > maximumBytes
                        : partRecords == RecordsPerPart(totalRecords, datasetFileCount, files.Count - 1)))
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync();
                    }
                    var part = Path.Combine(directory,
                        $"part-{(files.Count + 1).ToString("D3", CultureInfo.InvariantCulture)}.jsonl");
                    writer = new StreamWriter(
                        new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            64 * 1024, useAsync: true),
                        new UTF8Encoding(false));
                    writer.NewLine = "\n";
                    files.Add(part);
                    partBytes = 0;
                    partRecords = 0;
                }
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                partBytes += lineBytes;
                partRecords++;
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
        }
        return files.ToArray();
    }

    private static int RecordsPerPart(int totalRecords, int fileCount, int partIndex) =>
        totalRecords / fileCount + (partIndex < totalRecords % fileCount ? 1 : 0);

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
