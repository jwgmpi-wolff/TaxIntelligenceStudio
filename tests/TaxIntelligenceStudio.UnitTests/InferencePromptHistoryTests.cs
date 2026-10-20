using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Infrastructure;
using TaxIntelligenceStudio.Infrastructure.Persistence;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class InferencePromptHistoryTests
{
    [Fact]
    public async Task History_PersistsExactPromptsAcrossReopenAndSupportsDeletion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"prompt-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<TaxIntelligenceDbContext>().UseSqlite($"Data Source={path}").Options;
        try
        {
            await using (var db = new TaxIntelligenceDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var history = new InferencePromptHistory(db, TimeProvider.System);
                await history.SaveAsync("First question");
                await history.SaveAsync("Follow-up\nwith \"quotes\" and $125");
            }
            await using (var db = new TaxIntelligenceDbContext(options))
            {
                var history = new InferencePromptHistory(db, TimeProvider.System);
                var saved = await history.GetAsync();
                Assert.Equal(2, saved.Count);
                Assert.Equal("Follow-up\nwith \"quotes\" and $125", saved[0].Prompt);
                Assert.DoesNotContain("\n", saved[0].DisplayText);
                Assert.Equal("First question", saved[1].Prompt);
                await history.DeleteAsync(saved[0].Id);
                Assert.Equal("First question", Assert.Single(await history.GetAsync()).Prompt);
                await history.ClearAsync();
                Assert.Empty(await history.GetAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Initialization_AddsHistoryToExistingDatabaseAndIsIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"prompt-history-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE ExistingData (Value TEXT); INSERT INTO ExistingData VALUES ('keep');";
                await command.ExecuteNonQueryAsync();
            }
            var services = new ServiceCollection();
            services.AddTaxIntelligenceInfrastructure(path);
            await using var provider = services.BuildServiceProvider();
            await provider.InitializeTaxIntelligenceDatabaseAsync();
            await provider.InitializeTaxIntelligenceDatabaseAsync();
            await using var scope = provider.CreateAsyncScope();
            var history = scope.ServiceProvider.GetRequiredService<IInferencePromptHistory>();
            await history.SaveAsync("Saved on upgraded database");
            Assert.Single(await history.GetAsync());
            await using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            await verify.OpenAsync();
            using var query = verify.CreateCommand();
            query.CommandText = "SELECT Value FROM ExistingData";
            Assert.Equal("keep", await query.ExecuteScalarAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task History_RejectsBlankInputAndHonorsCancellationWithoutSaving()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaxIntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new TaxIntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var history = new InferencePromptHistory(db, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => history.SaveAsync(" "));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history.SaveAsync("Cancelled", cancelled.Token));
        await history.SaveAsync("Only saved prompt");
        Assert.Equal("Only saved prompt", Assert.Single(await history.GetAsync()).Prompt);
    }
}
