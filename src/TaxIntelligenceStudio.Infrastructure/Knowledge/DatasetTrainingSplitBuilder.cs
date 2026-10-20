using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Infrastructure.Sources;

namespace TaxIntelligenceStudio.Infrastructure.Knowledge;

internal static partial class DatasetTrainingSplitBuilder
{
    private const int DefaultSeed = 42;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<DatasetTrainingSplitResult> BuildAsync(
        string datasetPath,
        CancellationToken cancellationToken)
    {
        var datasetGroups = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var line in File.ReadLinesAsync(datasetPath, cancellationToken))
        {
            if (TryReadRecord(line, out var messages, out var sourceUrl) &&
                IsAllowedSource(sourceUrl))
            {
                datasetGroups.Add(sourceUrl);
            }
        }

        if (datasetGroups.Count == 0)
        {
            throw new InvalidDataException(
                "No records with allowlisted authoritative-source URLs are available for training splits.");
        }

        var groupAssignments = AssignGroups(datasetGroups, DefaultSeed);
        var directory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(datasetPath))!,
            $"{Path.GetFileNameWithoutExtension(datasetPath)}-splits");
        Directory.CreateDirectory(directory);
        var trainingPath = Path.Combine(directory, "train.jsonl");
        var validationPath = Path.Combine(directory, "validation.jsonl");
        var testPath = Path.Combine(directory, "test.jsonl");
        var metadataPath = Path.Combine(directory, "examples.jsonl");
        var manifestPath = Path.Combine(directory, "manifest.json");

        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["train"] = 0,
            ["validation"] = 0,
            ["test"] = 0
        };
        var excludedDuplicates = 0;
        var excludedPii = 0;
        var excludedSources = 0;
        var metadataCount = 0;
        var duplicateKeys = new HashSet<string>(StringComparer.Ordinal);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var splitWriters = new Dictionary<string, StreamWriter>(StringComparer.Ordinal);
        try
        {
            splitWriters["train"] = CreateWriter(trainingPath);
            splitWriters["validation"] = CreateWriter(validationPath);
            splitWriters["test"] = CreateWriter(testPath);
            await using var metadataWriter = CreateWriter(metadataPath);
            await foreach (var line in File.ReadLinesAsync(datasetPath, cancellationToken))
            {
                if (!TryReadRecord(line, out var messages, out var sourceUrl) ||
                    !IsAllowedSource(sourceUrl))
                {
                    excludedSources++;
                    await metadataWriter.WriteLineAsync(
                        JsonSerializer.Serialize(new
                        {
                            exampleId = Hash(line),
                            sourceDocumentId = (string?)null,
                            sourceUrl = (string?)null,
                            chunkOrdinal = (int?)null,
                            split = (string?)null,
                            generationType = "synthetic_source_passage_transformation",
                            duplicateKey = (string?)null,
                            piiScan = new
                            {
                                scanner = "limited-email-ssn-regex-v1",
                                status = "not_scanned_invalid_source_record",
                                findings = Array.Empty<string>()
                            },
                            exclusionReason = "invalid_or_untrusted_source_url"
                        }).AsMemory(),
                        cancellationToken);
                    metadataCount++;
                    continue;
                }

                var ordinal = ordinals.GetValueOrDefault(sourceUrl);
                ordinals[sourceUrl] = ordinal + 1;
                var split = groupAssignments[sourceUrl];
                var piiFindings = ScanForPii(messages);
                var normalizedText = string.Join(
                    "\n",
                    messages.Select(message => Normalize(message.Content)));
                var duplicateKey = Hash(normalizedText);
                var isDuplicate = !duplicateKeys.Add(duplicateKey);
                if (isDuplicate)
                {
                    excludedDuplicates++;
                }
                if (piiFindings.Count > 0)
                {
                    excludedPii++;
                }
                var exclusionReasons = new List<string>();
                if (isDuplicate)
                {
                    exclusionReasons.Add("duplicate");
                }
                if (piiFindings.Count > 0)
                {
                    exclusionReasons.Add("limited_pii_scan_match");
                }
                if (exclusionReasons.Count == 0)
                {
                    await splitWriters[split].WriteLineAsync(line.AsMemory(), cancellationToken);
                    counts[split]++;
                }

                var metadata = new
                {
                    exampleId = Hash($"{sourceUrl}\n{ordinal}\n{duplicateKey}"),
                    sourceDocumentId = Hash(sourceUrl),
                    sourceUrl,
                    chunkOrdinal = ordinal,
                    split,
                    generationType = "synthetic_source_passage_transformation",
                    duplicateKey,
                    piiScan = new
                    {
                        scanner = "limited-email-ssn-regex-v1",
                        status = piiFindings.Count == 0 ? "no_match" : "excluded_match",
                        findings = piiFindings
                    },
                    exclusionReason = exclusionReasons.Count == 0
                        ? null
                        : string.Join(",", exclusionReasons)
                };
                await metadataWriter.WriteLineAsync(
                    JsonSerializer.Serialize(metadata).AsMemory(),
                    cancellationToken);
                metadataCount++;
            }
        }
        finally
        {
            foreach (var writer in splitWriters.Values)
            {
                await writer.DisposeAsync();
            }
        }

        var manifest = new
        {
            schemaVersion = 1,
            seed = DefaultSeed,
            groupingKey = "allowlisted_source_url",
            splitMethod = "deterministic_group_hash_order",
            intendedRatios = new { train = 0.8, validation = 0.1, test = 0.1 },
            groupCounts = groupAssignments
                .GroupBy(item => item.Value)
                .ToDictionary(group => group.Key, group => group.Count()),
            recordCounts = counts,
            duplicateExcludedCount = excludedDuplicates,
            piiExcludedCount = excludedPii,
            untrustedSourceExcludedCount = excludedSources,
            piiScanner = "limited-email-ssn-regex-v1",
            piiScannerLimitation = "Regex screening only; not a guarantee of de-identification.",
            generationType = "synthetic_source_passage_transformation",
            training = await DescribeFileAsync(trainingPath, counts["train"], cancellationToken),
            validation = await DescribeFileAsync(validationPath, counts["validation"], cancellationToken),
            test = await DescribeFileAsync(testPath, counts["test"], cancellationToken),
            exampleMetadata = await DescribeFileAsync(
                metadataPath,
                metadataCount,
                cancellationToken)
        };
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);

        return new DatasetTrainingSplitResult(
            trainingPath,
            validationPath,
            testPath,
            metadataPath,
            manifestPath,
            counts["train"],
            counts["validation"],
            counts["test"],
            excludedDuplicates,
            excludedPii,
            excludedSources,
            DefaultSeed);
    }

    private static Dictionary<string, string> AssignGroups(IEnumerable<string> groups, int seed)
    {
        var ordered = groups
            .Select(group => new { Group = group, Order = Hash($"{seed}\n{group}") })
            .OrderBy(item => item.Order, StringComparer.Ordinal)
            .ToArray();
        var groupCount = ordered.Length;
        var validationCount = groupCount >= 3 ? Math.Max(1, (int)Math.Round(groupCount * 0.1)) :
            groupCount == 2 ? 1 : 0;
        var testCount = groupCount >= 3 ? Math.Max(1, (int)Math.Round(groupCount * 0.1)) : 0;
        while (validationCount + testCount >= groupCount && groupCount > 1)
        {
            if (testCount > 0)
            {
                testCount--;
            }
            else
            {
                validationCount--;
            }
        }

        var trainingCount = groupCount - validationCount - testCount;
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Length; index++)
        {
            var split = index < trainingCount
                ? "train"
                : index < trainingCount + validationCount
                    ? "validation"
                    : "test";
            assignments.Add(ordered[index].Group, split);
        }

        return assignments;
    }

    private static bool TryReadRecord(
        string line,
        out IReadOnlyList<ChatMessage> messages,
        out string sourceUrl)
    {
        messages = [];
        sourceUrl = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("messages", out var messageArray) ||
                messageArray.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsedMessages = new List<ChatMessage>();
            foreach (var item in messageArray.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                parsedMessages.Add(new ChatMessage(content.GetString() ?? string.Empty));
            }

            if (parsedMessages.Count != 3)
            {
                return false;
            }

            var assistantText = parsedMessages[2].Content;
            const string marker = "\n\nSource: ";
            var markerIndex = assistantText.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return false;
            }

            sourceUrl = assistantText[(markerIndex + marker.Length)..].Trim();
            messages = parsedMessages;
            return sourceUrl.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsAllowedSource(string sourceUrl) =>
        Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) &&
        AuthoritativeSourcePolicy.IsAllowedHttps(uri);

    private static IReadOnlyList<string> ScanForPii(IReadOnlyList<ChatMessage> messages)
    {
        var text = string.Join("\n", messages.Select(message => message.Content));
        var findings = new List<string>();
        if (EmailPattern().IsMatch(text))
        {
            findings.Add("email_address");
        }
        if (SsnPattern().IsMatch(text))
        {
            findings.Add("ssn_like_number");
        }

        return findings;
    }

    private static string Normalize(string text) =>
        WhitespacePattern().Replace(text.Trim().ToLowerInvariant(), " ");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static StreamWriter CreateWriter(string path)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
        return writer;
    }

    private static async Task<object> DescribeFileAsync(
        string path,
        int recordCount,
        CancellationToken cancellationToken) =>
        new
        {
            file = Path.GetFileName(path),
            recordCount,
            bytes = new FileInfo(path).Length,
            sha256 = await ComputeSha256Async(path, cancellationToken)
        };

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private sealed record ChatMessage(string Content);

    [GeneratedRegex(@"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b(?!000|666|9\d{2})\d{3}-(?!00)\d{2}-(?!0000)\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SsnPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
