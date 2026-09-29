using System.Text.Json;
using TaxIntelligenceStudio.Infrastructure.Training;

namespace TaxIntelligenceStudio.UnitTests;

/// <summary>
/// Tests run against a temporary collector container so they never write into
/// the real one at C:\temp\TaxIntelligenceStudio.
/// </summary>
public sealed class ResultsCollectorTests : IDisposable
{
    private readonly string? _originalOverride;
    private readonly string _container;

    public ResultsCollectorTests()
    {
        _originalOverride = Environment.GetEnvironmentVariable(ResultsCollector.OverrideVariable);
        _container = Path.Combine(
            Path.GetTempPath(),
            "tis-collector-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(ResultsCollector.OverrideVariable, _container);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ResultsCollector.OverrideVariable, _originalOverride);

        if (Directory.Exists(_container))
        {
            Directory.Delete(_container, recursive: true);
        }
    }

    [Fact]
    public void DefaultContainerIsUnderLocalTemp()
    {
        Assert.Equal(@"C:\temp\TaxIntelligenceStudio", ResultsCollector.DefaultContainer);
    }

    [Fact]
    public void ContainerFallsBackToDefaultWhenOverrideIsBlank()
    {
        Environment.SetEnvironmentVariable(ResultsCollector.OverrideVariable, "   ");

        Assert.Equal(ResultsCollector.DefaultContainer, ResultsCollector.Container);
    }

    [Fact]
    public void ContainerHonorsTheOverrideVariable()
    {
        Assert.Equal(_container, ResultsCollector.Container);
    }

    [Fact]
    public void ContainerExpandsEnvironmentVariablesInTheOverride()
    {
        Environment.SetEnvironmentVariable(ResultsCollector.OverrideVariable, @"%SystemDrive%\temp\collected");

        Assert.Equal(
            Environment.ExpandEnvironmentVariables(@"%SystemDrive%\temp\collected"),
            ResultsCollector.Container);
    }

    [Fact]
    public void EnsureContainerCreatesTheFolder()
    {
        Assert.False(Directory.Exists(_container));

        var created = ResultsCollector.EnsureContainer();

        Assert.Equal(_container, created);
        Assert.True(Directory.Exists(created));
    }

    [Fact]
    public void ResolvePathCreatesRequestedSubfolder()
    {
        var path = ResultsCollector.ResolvePath("probe.json", "accelerators");

        Assert.Equal(Path.Combine(_container, "accelerators", "probe.json"), path);
        Assert.True(Directory.Exists(Path.Combine(_container, "accelerators")));
    }

    [Fact]
    public void TimestampedNameIsSortableAndKeepsTheExtension()
    {
        var name = ResultsCollector.TimestampedName("accelerator-self-test", ".json");

        Assert.StartsWith("accelerator-self-test-", name);
        Assert.EndsWith(".json", name);
        Assert.Matches(@"^accelerator-self-test-\d{8}-\d{6}-\d{3}\.json$", name);
    }

    [Fact]
    public async Task WriteJsonAsyncCollectsValidJsonIntoTheContainer()
    {
        var path = await ResultsCollector.WriteJsonAsync(
            new { Machine = "WOLFFSURFACEPC", Verified = true },
            "accelerator-self-test");

        Assert.NotNull(path);
        Assert.Equal(_container, Path.GetDirectoryName(path));
        Assert.True(File.Exists(path));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path!));
        Assert.Equal("WOLFFSURFACEPC", document.RootElement.GetProperty("Machine").GetString());
        Assert.True(document.RootElement.GetProperty("Verified").GetBoolean());
    }

    [Fact]
    public async Task WriteJsonAsyncReturnsNullWhenTheContainerCannotBeWritten()
    {
        // A file where the container should be makes every write fail.
        var blocker = Path.Combine(Path.GetTempPath(), "tis-collector-blocked-" + Guid.NewGuid().ToString("N")[..8]);
        await File.WriteAllTextAsync(blocker, "not a directory");

        try
        {
            Environment.SetEnvironmentVariable(ResultsCollector.OverrideVariable, blocker);

            Assert.Null(await ResultsCollector.WriteJsonAsync(new { Value = 1 }, "blocked"));
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
