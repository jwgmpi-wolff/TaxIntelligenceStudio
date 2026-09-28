using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "TaxIntelligenceStudio");
Directory.CreateDirectory(dataDirectory);

builder.Services.AddTaxIntelligenceInfrastructure(
    Path.Combine(dataDirectory, "tax-intelligence.db"));

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
    var result = await ingestionService.CrawlAsync(
        maximumDocuments ?? 100,
        cancellationToken: cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/hardware/self-test", async (
    IAcceleratorSelfTestService selfTestService,
    CancellationToken cancellationToken) =>
{
    var result = await selfTestService.RunAsync("python", cancellationToken);
    return Results.Ok(result);
});

app.Run();
