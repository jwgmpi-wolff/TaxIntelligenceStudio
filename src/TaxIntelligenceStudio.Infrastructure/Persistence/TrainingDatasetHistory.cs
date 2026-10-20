using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Persistence;

public sealed class TrainingDatasetHistory(
    DbContextOptions<TaxIntelligenceDbContext> options,
    TimeProvider timeProvider) : ITrainingDatasetHistory
{
    public async Task<Guid> StartAsync(ModelTrainingRequest request, CancellationToken cancellationToken = default)
    {
        var entry = new TrainingDatasetLog
        {
            Id = Guid.NewGuid(),
            StartedAt = timeProvider.GetUtcNow(),
            BaseModel = request.BaseModel,
            DatasetPaths = string.Join(Environment.NewLine, request.DatasetPaths.Select(Path.GetFullPath)),
            OutputDirectory = Path.GetFullPath(request.OutputDirectory),
            Status = "Submitted (preflight/training)"
        };
        await using var db = new TaxIntelligenceDbContext(options);
        db.TrainingDatasetLogs.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return entry.Id;
    }

    public async Task FinishAsync(Guid id, string status, string? outputDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        await using var db = new TaxIntelligenceDbContext(options);
        var entry = await db.TrainingDatasetLogs.SingleAsync(item => item.Id == id, cancellationToken);
        entry.Status = status;
        entry.CompletedAt = timeProvider.GetUtcNow();
        if (outputDirectory is not null)
        {
            entry.OutputDirectory = Path.GetFullPath(outputDirectory);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TrainingDatasetLog>> GetAsync(string outputRoot, CancellationToken cancellationToken = default)
    {
        await using var db = new TaxIntelligenceDbContext(options);
        var entries = await db.TrainingDatasetLogs.AsNoTracking().ToListAsync(cancellationToken);
        if (Directory.Exists(outputRoot))
        {
            var folders = new[] { outputRoot }.Concat(Directory.EnumerateDirectories(outputRoot));
            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manifestPath = Path.Combine(folder, "run_environment.json");
                if (!File.Exists(manifestPath) || entries.Any(entry =>
                        string.Equals(Path.GetFullPath(entry.OutputDirectory), Path.GetFullPath(folder),
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
                var root = manifest.RootElement;
                if (!root.TryGetProperty("dataset_files", out var files) ||
                    !files.TryGetProperty("training", out var trainingFiles))
                {
                    continue;
                }
                var paths = trainingFiles.EnumerateArray().Select(file =>
                    file.GetProperty("path").GetString() ??
                    throw new InvalidDataException($"Missing dataset path in {manifestPath}")).ToArray();
                entries.Add(new TrainingDatasetLog
                {
                    StartedAt = File.GetLastWriteTimeUtc(manifestPath),
                    BaseModel = root.GetProperty("training_run").GetProperty("model").GetString() ?? "Not recorded",
                    DatasetPaths = string.Join(Environment.NewLine, paths),
                    OutputDirectory = Path.GetFullPath(folder),
                    Status = "Existing manifest (outcome not checked)"
                });
            }
        }
        return entries.OrderByDescending(entry => entry.StartedAt).ToArray();
    }
}
