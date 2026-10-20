using Microsoft.EntityFrameworkCore;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Persistence;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class TrainingDatasetHistoryTests
{
    [Fact]
    public async Task Log_PersistsAllQueuedDatasetsAndActualOutcomeAcrossReopen()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"training-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var database = Path.Combine(folder, "history.db");
        var options = new DbContextOptionsBuilder<TaxIntelligenceDbContext>().UseSqlite($"Data Source={database}").Options;
        try
        {
            await using (var db = new TaxIntelligenceDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }
            var history = new TrainingDatasetHistory(options, TimeProvider.System);
            var request = new ModelTrainingRequest("test-model", Path.Combine(folder, "one.jsonl"),
                folder, "python", 1, 1, 0.00005m, true, null,
                AdditionalDatasetPaths: [Path.Combine(folder, "two.jsonl")]);
            var id = await history.StartAsync(request);
            var submitted = Assert.Single(await history.GetAsync(folder));
            Assert.Contains("Submitted", submitted.Status);
            Assert.Null(submitted.CompletedAt);
            var actualOutput = Path.Combine(folder, "new-run");
            await history.FinishAsync(id, "Completed", actualOutput);
            var reopened = new TrainingDatasetHistory(options, TimeProvider.System);
            var completed = Assert.Single(await reopened.GetAsync(folder));
            Assert.Equal("Completed", completed.Status);
            Assert.Equal(actualOutput, completed.OutputDirectory);
            Assert.NotNull(completed.CompletedAt);
            Assert.Contains("one.jsonl", completed.Details);
            Assert.Contains("two.jsonl", completed.Details);
            Assert.Contains("test-model", completed.Summary);
            var failedId = await reopened.StartAsync(request);
            await reopened.FinishAsync(failedId, "Cancelled");
            Assert.Equal("Cancelled", (await reopened.GetAsync(folder))[0].Status);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task Log_ReadsExistingManifestWithoutClaimingTrainingSucceeded()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"training-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var manifest = Path.Combine(folder, "run_environment.json");
        var database = Path.Combine(folder, "history.db");
        var options = new DbContextOptionsBuilder<TaxIntelligenceDbContext>().UseSqlite($"Data Source={database}").Options;
        try
        {
            await using (var db = new TaxIntelligenceDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }
            await File.WriteAllTextAsync(manifest,
                """{"training_run":{"model":"old-model"},"dataset_files":{"training":[{"path":"old-one.jsonl"},{"path":"old-two.jsonl"}]}}""");
            var history = new TrainingDatasetHistory(options, TimeProvider.System);
            var log = Assert.Single(await history.GetAsync(folder));
            Assert.Contains("outcome not checked", log.Status);
            Assert.Contains("old-one.jsonl", log.DatasetPaths);
            Assert.Contains("old-two.jsonl", log.DatasetPaths);
            Assert.Equal("old-model", log.BaseModel);
            await File.WriteAllTextAsync(manifest, "broken");
            await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => history.GetAsync(folder));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(manifest);
            File.Delete(database);
            Directory.Delete(folder);
        }
    }
}
