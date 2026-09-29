using System.Text.Json;

namespace TaxIntelligenceStudio.Infrastructure.Training;

/// <summary>
/// The collector container: the single local folder that every result produced
/// by this application is written to.
/// </summary>
/// <remarks>
/// Results were previously scattered across the per-user temp folder, the
/// install directory, and stdout, which made them hard to find and impossible
/// to collect from a remote machine in one step. Everything now lands in one
/// labelled, predictable location:
///
/// <code>C:\temp\TaxIntelligenceStudio</code>
///
/// The location can be redirected with the <c>TAXINTELLIGENCESTUDIO_RESULTS</c>
/// environment variable, which the deployment and install scripts set when they
/// are told to collect somewhere else.
/// </remarks>
public static class ResultsCollector
{
    /// <summary>Environment variable that overrides the collector container.</summary>
    public const string OverrideVariable = "TAXINTELLIGENCESTUDIO_RESULTS";

    /// <summary>The default collector container, on the local system drive.</summary>
    public const string DefaultContainer = @"C:\temp\TaxIntelligenceStudio";

    /// <summary>
    /// The collector container for this process. All results are written here.
    /// </summary>
    public static string Container
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(OverrideVariable);
            return string.IsNullOrWhiteSpace(configured)
                ? DefaultContainer
                : Environment.ExpandEnvironmentVariables(configured.Trim());
        }
    }

    /// <summary>
    /// Ensures the collector container exists and returns it.
    /// </summary>
    public static string EnsureContainer()
    {
        var container = Container;
        Directory.CreateDirectory(container);
        return container;
    }

    /// <summary>
    /// Resolves a path inside the collector container, creating the container
    /// and any requested subfolder.
    /// </summary>
    public static string ResolvePath(string fileName, string? subfolder = null)
    {
        var container = EnsureContainer();
        if (!string.IsNullOrWhiteSpace(subfolder))
        {
            container = Path.Combine(container, subfolder);
            Directory.CreateDirectory(container);
        }

        return Path.Combine(container, fileName);
    }

    /// <summary>
    /// Builds a sortable, collision-free file name stamped with the local time.
    /// </summary>
    public static string TimestampedName(string prefix, string extension) =>
        $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}{extension}";

    /// <summary>
    /// Writes a result as indented JSON into the collector container and returns
    /// the file that was written.
    /// </summary>
    /// <remarks>
    /// Collecting a result must never fail the operation that produced it, so
    /// write errors are reported as null rather than thrown.
    /// </remarks>
    public static async Task<string?> WriteJsonAsync<T>(
        T result,
        string prefix,
        string? subfolder = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = ResolvePath(TimestampedName(prefix, ".json"), subfolder);
            var json = JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, cancellationToken);
            return path;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
