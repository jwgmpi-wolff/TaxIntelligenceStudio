using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Knowledge;
using TaxIntelligenceStudio.Infrastructure.Persistence;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class DatasetExportServiceTests
{
    [Theory]
    [InlineData(1, 450_000_000, 0)]
    [InlineData(500, 450_000_000, 0)]
    [InlineData(500, 25_000, 0)]
    [InlineData(500, 450_000_000, 3)]
    [InlineData(1, 450_000_000, 1)]
    public async Task ExportJsonLinesAsync_EmitsStrictJsonLinesWithoutBom(
        int repetitions, long maximumBytes, int datasetFileCount)
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
            Content = string.Concat(Enumerable.Repeat("Verified IRS evidence. \"Quoted\"\n\u00e9vidence. ", repetitions)),
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
            var service = new DatasetExportService(context);
            var result = datasetFileCount > 0
                ? await service.ExportJsonLinesAsync(outputPath, datasetFileCount)
                : await service.ExportJsonLinesAsync(outputPath, maximumBytes);
            var count = result.RecordCount;
            var bytes = await File.ReadAllBytesAsync(outputPath);
            Assert.NotNull(result.ManifestPath);
            Assert.True(File.Exists(result.ManifestPath));
            var splits = Assert.IsType<TaxIntelligenceStudio.Application.DatasetTrainingSplitResult>(
                result.TrainingSplits);
            Assert.True(File.Exists(splits.TrainingFile));
            Assert.True(File.Exists(splits.ValidationFile));
            Assert.True(File.Exists(splits.TestFile));
            Assert.True(File.Exists(splits.ExampleMetadataFile));
            Assert.True(File.Exists(splits.ManifestPath));
            Assert.True(splits.TrainingRecordCount > 0);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)),
                result.Sha256);
            using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath)))
            {
                Assert.Equal(count, manifest.RootElement.GetProperty("recordCount").GetInt32());
                Assert.Equal(
                    result.Sha256,
                    manifest.RootElement.GetProperty("fullExport").GetProperty("sha256").GetString());
            }
            var validationPath = Path.ChangeExtension(outputPath, ".validation-report.json");
            using (var validation = JsonDocument.Parse(await File.ReadAllTextAsync(validationPath)))
            {
                Assert.True(validation.RootElement.GetProperty("isValid").GetBoolean());
                Assert.Equal(count, validation.RootElement.GetProperty("recordCount").GetInt32());
            }
            if (datasetFileCount == 0 && bytes.Length <= maximumBytes)
            {
                Assert.Equal(new[] { outputPath }, result.UploadFiles);
            }
            else
            {
                if (datasetFileCount > 0)
                {
                    Assert.Equal(datasetFileCount, result.UploadFiles.Count);
                }
                else
                {
                    Assert.True(result.UploadFiles.Count > 1);
                }
                var uploadLines = new List<string>();
                var partCounts = new List<int>();
                foreach (var file in result.UploadFiles)
                {
                    Assert.InRange(new FileInfo(file).Length, 1, maximumBytes);
                    var partLines = await File.ReadAllLinesAsync(file);
                    Assert.NotEmpty(partLines);
                    partCounts.Add(partLines.Length);
                    uploadLines.AddRange(partLines);
                }
                if (datasetFileCount > 0)
                {
                    Assert.InRange(partCounts.Max() - partCounts.Min(), 0, 1);
                }
                Assert.Equal(await File.ReadAllLinesAsync(outputPath), uploadLines);
            }
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
            Assert.Equal((byte)'{', bytes[0]);
            Assert.Equal(count, lines.Length);
            if (repetitions == 1)
            {
                Assert.Equal(1, count);
            }
            else
            {
                Assert.True(count > 1);
            }
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                var record = document.RootElement;
                var messages = record.GetProperty("messages");
                Assert.Equal(
                    new[] { "messages" },
                    record.EnumerateObject().Select(property => property.Name));
                Assert.Equal(3, messages.GetArrayLength());
                Assert.Equal("system", messages[0].GetProperty("role").GetString());
                Assert.Equal("user", messages[1].GetProperty("role").GetString());
                Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
                foreach (var message in messages.EnumerateArray())
                {
                    Assert.Equal(
                        new[] { "role", "content" },
                        message.EnumerateObject().Select(property => property.Name));
                    Assert.False(string.IsNullOrWhiteSpace(message.GetProperty("content").GetString()));
                }
                Assert.Contains(
                    "https://www.irs.gov/publications/p17",
                    messages[2].GetProperty("content").GetString());
            }
            Assert.Contains("Verified IRS evidence.", text);
        }
        finally
        {
            File.Delete(outputPath);
            File.Delete(Path.ChangeExtension(outputPath, ".manifest.json"));
            File.Delete(Path.ChangeExtension(outputPath, ".validation-report.json"));
            var parent = Path.GetDirectoryName(outputPath)!;
            var splitDirectory = Path.Combine(
                parent,
                $"{Path.GetFileNameWithoutExtension(outputPath)}-splits");
            if (Directory.Exists(splitDirectory))
            {
                Directory.Delete(splitDirectory, recursive: true);
            }
            foreach (var directory in Directory.EnumerateDirectories(
                         parent, $"{Path.GetFileNameWithoutExtension(outputPath)}-foundry-*"))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportJsonLinesAsync_CreatesSourceGroupedSplitsAndLimitedPiiExclusions()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jsonl");
        var options = new DbContextOptionsBuilder<TaxIntelligenceDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var context = new TaxIntelligenceDbContext(options);
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var captured = DateTimeOffset.UtcNow;
        for (var index = 0; index < 4; index++)
        {
            context.KnowledgeDocuments.Add(new KnowledgeDocument
            {
                SourceUrl = $"https://www.irs.gov/publications/p{index}",
                Title = $"Distinct topic {index}",
                Content = index == 0
                    ? "Distinct content person@example.com should be excluded from training. " +
                      string.Concat(Enumerable.Repeat("public passage text. ", 250))
                    : $"Distinct official source passage about tax rules, unique category {index}.",
                ContentHash = new string((char)('A' + index), 64),
                Version = 1,
                IsCurrent = true,
                RetrievedAt = captured,
                LastVerifiedAt = captured
            });
        }

        await context.SaveChangesAsync();
        try
        {
            var result = await new DatasetExportService(context).ExportJsonLinesAsync(outputPath);
            var splits = Assert.IsType<TaxIntelligenceStudio.Application.DatasetTrainingSplitResult>(
                result.TrainingSplits);
            Assert.Equal(42, splits.Seed);
            Assert.True(splits.PiiExcludedCount > 0);
            Assert.True(splits.ValidationRecordCount > 0);
            Assert.True(splits.TestRecordCount > 0);

            var metadata = (await File.ReadAllLinesAsync(splits.ExampleMetadataFile))
                .Select(line => JsonDocument.Parse(line))
                .Select(document => document.RootElement.Clone())
                .ToArray();
            var assignments = metadata
                .GroupBy(item => item.GetProperty("sourceUrl").GetString()!)
                .Select(group => group.Select(item => item.GetProperty("split").GetString()).Distinct().Count())
                .ToArray();
            Assert.All(assignments, assignmentCount => Assert.Equal(1, assignmentCount));
            Assert.Contains(metadata, item =>
                item.GetProperty("piiScan").GetProperty("status").GetString() == "excluded_match");
            Assert.All(metadata, item =>
                Assert.Equal(
                    "synthetic_source_passage_transformation",
                    item.GetProperty("generationType").GetString()));
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(splits.ManifestPath));
            Assert.Equal(
                "allowlisted_source_url",
                manifest.RootElement.GetProperty("groupingKey").GetString());
        }
        finally
        {
            File.Delete(outputPath);
            File.Delete(Path.ChangeExtension(outputPath, ".manifest.json"));
            File.Delete(Path.ChangeExtension(outputPath, ".validation-report.json"));
            var splitDirectory = Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                $"{Path.GetFileNameWithoutExtension(outputPath)}-splits");
            if (Directory.Exists(splitDirectory))
            {
                Directory.Delete(splitDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(0, 400, 0)]
    [InlineData(1, 4_000, 1)]
    [InlineData(3, 4_000, 3)]
    [InlineData(10, 4_000, 10)]
    public async Task SplitUploadFilesAsync_EnforcesByteLimitAndPreservesEveryRecord(
        int fileCount, long maximumBytes, int expectedCount)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inputPath = Path.Combine(root, "training.jsonl");
        var lines = Enumerable.Range(0, 10).Select(index => JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                    new { role = "user", content = $"Question {index}" },
                    new { role = "assistant", content = "\u00e9vidence " + new string('x', 50) }
                }
        })).ToArray();
        await File.WriteAllTextAsync(inputPath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        try
        {
            var parts = await DatasetExportService.SplitUploadFilesAsync(
                inputPath, maximumBytes, datasetFileCount: fileCount);
            if (expectedCount > 0)
            {
                Assert.Equal(expectedCount, parts.Length);
            }
            else
            {
                Assert.True(parts.Length > 1);
            }
            var reconstructed = new List<string>();
            var counts = new List<int>();
            foreach (var part in parts)
            {
                var bytes = await File.ReadAllBytesAsync(part);
                Assert.InRange(bytes.LongLength, 1, maximumBytes);
                Assert.Equal((byte)'{', bytes[0]);
                var partLines = await File.ReadAllLinesAsync(part);
                Assert.NotEmpty(partLines);
                counts.Add(partLines.Length);
                foreach (var line in partLines)
                {
                    using var document = JsonDocument.Parse(line);
                    Assert.Equal(2, document.RootElement.GetProperty("messages").GetArrayLength());
                }
                reconstructed.AddRange(partLines);
            }
            Assert.Equal(lines, reconstructed);
            if (fileCount > 0)
            {
                Assert.InRange(counts.Max() - counts.Min(), 0, 1);
            }
            Assert.Equal(lines, await File.ReadAllLinesAsync(inputPath));
            Assert.True(DatasetExportService.MaximumUploadBytes < 512_000_000);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SplitUploadFilesAsync_RejectsNegativeCountBeforeReadingInput()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            DatasetExportService.SplitUploadFilesAsync(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.jsonl"),
                10_000,
                datasetFileCount: -1));
    }

    [Theory]
    [InlineData(3, 10_000)]
    [InlineData(1, 10)]
    public async Task SplitUploadFilesAsync_RejectsImpossibleCountWithoutCreatingParts(
        int fileCount, long maximumBytes)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inputPath = Path.Combine(root, "training.jsonl");
        const string record = "{\"messages\":[{\"role\":\"user\",\"content\":\"Question\"},{\"role\":\"assistant\",\"content\":\"Answer\"}]}";
        await File.WriteAllTextAsync(inputPath, record + "\n" + record + "\n", new UTF8Encoding(false));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatasetExportService.SplitUploadFilesAsync(
                    inputPath, maximumBytes, datasetFileCount: fileCount));
            Assert.Empty(Directory.EnumerateDirectories(root));
            Assert.Equal(2, (await File.ReadAllLinesAsync(inputPath)).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
