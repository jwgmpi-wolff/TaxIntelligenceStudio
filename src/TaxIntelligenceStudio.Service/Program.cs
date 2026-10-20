using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure;
using TaxIntelligenceStudio.Infrastructure.Inference;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "TaxIntelligenceStudio");
Directory.CreateDirectory(dataDirectory);

builder.Services.AddTaxIntelligenceInfrastructure(
    Path.Combine(dataDirectory, "tax-intelligence.db"),
    builder.Configuration);

var app = builder.Build();
await app.Services.InitializeTaxIntelligenceDatabaseAsync();

app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.Expires = "0";
    await next();
});

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "healthy",
    cache = "disabled",
    timestamp = DateTimeOffset.UtcNow
}));

app.MapGet("/api/models/providers", async (
    IModelProviderResolver providerResolver,
    CancellationToken cancellationToken) =>
    Results.Ok(await providerResolver.GetAvailabilityAsync(cancellationToken)));

app.MapGet("/api/models/offline", (ModelProviderOptions options) =>
    Results.Ok(new { offlineMode = options.OfflineMode }));

app.MapPost("/api/models/chat", async (
    ModelChatApiRequest request,
    string? providerId,
    IModelInferenceService inferenceService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var inferenceRequest = new ModelInferenceRequest(
            request.Messages,
            request.TaxYear,
            request.ScenarioIdentifier,
            MaximumOutputTokens: request.MaximumOutputTokens,
            Temperature: request.Temperature,
            Seed: request.Seed);
        return Results.Ok(await inferenceService.InferAsync(
            inferenceRequest,
            providerId,
            cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(
            new { error = exception.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException exception)
    {
        return Results.Json(
            new { error = exception.Message },
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Json(
            new { error = "The model provider request timed out." },
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (InvalidDataException)
    {
        return Results.Json(
            new { error = "The model provider returned an invalid response." },
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/guidance/verify", async (
    GuidanceRequest request,
    IGuidanceService guidanceService,
    CancellationToken cancellationToken) =>
{
    var response = await guidanceService.VerifyGuidanceAsync(request, cancellationToken);
    return response.IsVerified
        ? Results.Ok(response)
        : Results.Json(response, statusCode: StatusCodes.Status424FailedDependency);
});

app.MapPost("/api/knowledge/crawl", async (
    int? maximumDocuments,
    IIrsKnowledgeIngestionService ingestionService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await ingestionService.CrawlAsync(
            maximumDocuments ?? 100,
            cancellationToken: cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(
            new { error = exception.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/hardware/self-test", async (
    IAcceleratorSelfTestService selfTestService,
    CancellationToken cancellationToken) =>
{
    var result = await selfTestService.RunAsync("python", cancellationToken);
    return Results.Ok(result);
});

app.Run();
