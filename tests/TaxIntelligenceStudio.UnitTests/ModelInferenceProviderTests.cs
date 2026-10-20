using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Inference;
using TaxIntelligenceStudio.Infrastructure;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class ModelInferenceProviderTests
{
    [Fact]
    public void InfrastructureRegistration_ResolvesInferenceServiceAndBothProviders()
    {
        var services = new ServiceCollection();
        services.AddTaxIntelligenceInfrastructure(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db"));
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IModelInferenceService>());
        Assert.Equal(
            ["Azure", "Local", "Trained"],
            scope.ServiceProvider.GetServices<IModelInferenceProvider>()
                .Select(provider => provider.Id)
                .OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task LocalProvider_SendsOpenAiCompatibleRequestAndReturnsCitationsAndUsage()
    {
        string? sentBody = null;
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(JsonResponse());
        });
        var provider = new LocalModelInferenceProvider(
            new HttpClient(handler),
            new LocalModelProviderOptions
            {
                BaseAddress = "http://127.0.0.1:11434/v1/",
                ModelId = "test-local"
            },
            TimeProvider.System);
        var request = new ModelInferenceRequest(
            [new ModelChatMessage("user", "Explain this source.")],
            TaxYear: 2025,
            SourceContext:
            [
                new RetrievedSourceContext(
                    "IRS publication",
                    "https://www.irs.gov/pub",
                    "Verified passage.",
                    DateTimeOffset.UnixEpoch,
                    IsVerified: true,
                    IsCached: true)
            ]);

        var response = await provider.InferAsync(request);

        Assert.Equal(new Uri("http://127.0.0.1:11434/v1/chat/completions"), handler.LastRequestUri);
        Assert.Equal("test-local", response.ModelId);
        Assert.Equal(
            "Generated answer. See https://www.irs.gov/pub.",
            response.Content);
        Assert.Equal("cached", response.SourceDataMode);
        Assert.Equal(["https://www.irs.gov/pub"], response.Citations);
        Assert.Equal(8, response.Usage!.InputTokens);
        using var json = JsonDocument.Parse(sentBody!);
        Assert.Equal("test-local", json.RootElement.GetProperty("model").GetString());
        Assert.False(json.RootElement.TryGetProperty("temperature", out _));
        Assert.Contains(
            "Verified passage.",
            json.RootElement.GetProperty("messages").GetRawText(),
            StringComparison.Ordinal);
        Assert.Contains(
            "expert U.S. tax research and planning assistant",
            json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalProvider_PreservesAssistantRepliesForFollowUpTurns()
    {
        string? sentBody = null;
        var handler = new RecordingHandler((request, _) =>
        {
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(JsonResponse());
        });
        var provider = new LocalModelInferenceProvider(
            new HttpClient(handler),
            new LocalModelProviderOptions
            {
                BaseAddress = "http://127.0.0.1:11434/v1/",
                ModelId = "test-local"
            },
            TimeProvider.System);
        const string priorAssistantReply = "The earlier answer included this calculation.";
        var request = new ModelInferenceRequest(
            [
                new ModelChatMessage("system", "Give careful tax information."),
                new ModelChatMessage("user", "How is the amount calculated?"),
                new ModelChatMessage("assistant", priorAssistantReply),
                new ModelChatMessage("user", "Can you explain that calculation?")
            ]);

        await provider.InferAsync(request);

        using var json = JsonDocument.Parse(sentBody!);
        var messages = json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[3].GetProperty("role").GetString());
        Assert.Equal(priorAssistantReply, messages[3].GetProperty("content").GetString());
        Assert.Equal("Can you explain that calculation?", messages[4].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Resolver_DoesNotFallBackWhenConfiguredLocalProviderIsUnavailable()
    {
        var local = new StubProvider("Local", isCloud: false, isAvailable: false);
        var azure = new StubProvider("Azure", isCloud: true, isAvailable: true);
        var resolver = new ModelProviderResolver(
            [local, azure],
            new ModelProviderOptions { DefaultProvider = "Local" });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync());

        Assert.Contains("Local model is unavailable", exception.Message);
        Assert.Equal(1, local.AvailabilityChecks);
        Assert.Equal(0, azure.AvailabilityChecks);
    }

    [Fact]
    public async Task Resolver_RejectsAzureExplicitlyWhenOfflineModeIsEnabled()
    {
        var azure = new StubProvider("Azure", isCloud: true, isAvailable: true);
        var resolver = new ModelProviderResolver(
            [azure],
            new ModelProviderOptions { OfflineMode = true });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("Azure"));

        Assert.Contains("blocked", exception.Message);
        Assert.Equal(0, azure.AvailabilityChecks);
    }

    [Fact]
    public async Task InferenceService_RejectsUncachedContextInOfflineMode()
    {
        var local = new StubProvider("Local", isCloud: false, isAvailable: true);
        var resolver = new ModelProviderResolver(
            [local],
            new ModelProviderOptions { OfflineMode = true });
        var audit = new RecordingAuditRepository();
        var service = new ModelInferenceService(
            resolver,
            audit,
            new ModelProviderOptions { OfflineMode = true },
            TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InferAsync(
                RequestWithSource(isCached: false),
                "Local"));

        Assert.Equal(0, local.InferenceCalls);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task InferenceService_AuditsMetadataWithoutPromptOrResponseText()
    {
        const string sensitivePrompt = "Sensitive taxpayer prompt 94-3219876";
        const string privateAnswer = "Private generated answer";
        var local = new StubProvider("Local", isCloud: false, isAvailable: true)
        {
            Response = new ModelInferenceResponse(
                privateAnswer,
                "Local",
                "local-test",
                "stop",
                null,
                TimeSpan.FromMilliseconds(5),
                [],
                false,
                "none",
                [])
        };
        var audit = new RecordingAuditRepository();
        var service = new ModelInferenceService(
            new ModelProviderResolver([local], new ModelProviderOptions()),
            audit,
            new ModelProviderOptions(),
            TimeProvider.System);

        var result = await service.InferAsync(
            new ModelInferenceRequest([new ModelChatMessage("user", sensitivePrompt)]));

        Assert.Equal(privateAnswer, result.Content);
        var entry = Assert.Single(audit.Entries);
        Assert.DoesNotContain(sensitivePrompt, entry.InputJson, StringComparison.Ordinal);
        Assert.DoesNotContain(privateAnswer, entry.OutputJson, StringComparison.Ordinal);
        Assert.Contains("No verified authoritative-source context", result.Warnings[0]);
    }

    [Theory]
    [InlineData("Trained")]
    [InlineData("Local")]
    [InlineData("Azure")]
    public async Task InferenceService_WithholdsCorruptedPropertyTaxesBeforeSuccessfulAudit(string providerId)
    {
        var provider = new StubProvider(providerId, providerId == "Azure", true)
        {
            Response = new ModelInferenceResponse("Property taxes: $10,0 and $8,0.",
                providerId, "test", "stop", null, TimeSpan.Zero, [], false, "none", [])
        };
        var options = new ModelProviderOptions();
        var audit = new RecordingAuditRepository();
        var service = new ModelInferenceService(
            new ModelProviderResolver([provider], options), audit, options, TimeProvider.System);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.InferAsync(
            new ModelInferenceRequest([new ModelChatMessage("user", "Example land tax $10000; condo tax $8000.")]),
            providerId));
        Assert.Contains("malformed currency", error.Message);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task InferenceService_UsesCachedSearchContextInOfflineMode()
    {
        const string sensitivePrompt = "How does the standard deduction work?";
        var context = new RetrievedSourceContext(
            "IRS Publication",
            "https://www.irs.gov/publications/p17",
            "Cached source excerpt.",
            DateTimeOffset.UnixEpoch,
            IsVerified: true,
            IsCached: true);
        var search = new StubKnowledgeSearch([context]);
        var local = new StubProvider("Local", isCloud: false, isAvailable: true);
        var audit = new RecordingAuditRepository();
        var options = new ModelProviderOptions { OfflineMode = true };
        var service = new ModelInferenceService(
            new ModelProviderResolver([local], options),
            audit,
            options,
            TimeProvider.System,
            search);

        var response = await service.InferAsync(
            new ModelInferenceRequest([new ModelChatMessage("user", sensitivePrompt)]));

        Assert.Equal(sensitivePrompt, search.Query);
        Assert.Equal("Cached source excerpt.", Assert.Single(local.LastRequest!.SourceContext!).Content);
        Assert.Equal("offline", response.SourceDataMode);
        Assert.Contains(response.Warnings, warning =>
            warning.Contains("cached authoritative-source", StringComparison.Ordinal));
        Assert.Contains(response.Warnings, warning => warning.Contains("may be outdated", StringComparison.Ordinal));
        Assert.DoesNotContain(sensitivePrompt, Assert.Single(audit.Entries).InputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferenceService_ReportsRetrievalAndModelProgressStages()
    {
        var local = new StubProvider("Local", isCloud: false, isAvailable: true);
        var progress = new RecordingProgress();
        var service = new ModelInferenceService(
            new ModelProviderResolver([local], new ModelProviderOptions()),
            new RecordingAuditRepository(),
            new ModelProviderOptions(),
            TimeProvider.System,
            new StubKnowledgeSearch(
            [
                new RetrievedSourceContext(
                    "IRS Publication",
                    "https://www.irs.gov/publications/p17",
                    "Passage",
                    DateTimeOffset.UnixEpoch,
                    IsVerified: true,
                    IsCached: true)
            ]));

        await service.InferAsync(
            new ModelInferenceRequest([new ModelChatMessage("user", "Question?")]),
            progress: progress);

        Assert.Contains(progress.Messages, message => message.Contains("Searching current documents", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("Found 1 cached IRS source", StringComparison.Ordinal));
        Assert.Contains(progress.Messages, message => message.Contains("Sending request to Local model", StringComparison.Ordinal));
        Assert.Equal("Inference complete.", progress.Messages[^1]);
    }

    [Fact]
    public async Task LocalProvider_RejectsNonLoopbackAddressByDefault()
    {
        var provider = new LocalModelInferenceProvider(
            new HttpClient(new RecordingHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
            new LocalModelProviderOptions
            {
                BaseAddress = "http://192.0.2.10:8080/v1/",
                ModelId = "test-local"
            },
            TimeProvider.System);

        var availability = await provider.CheckAvailabilityAsync();

        Assert.False(availability.IsAvailable);
        Assert.Contains("loopback", availability.Diagnostic);
    }

    [Fact]
    public async Task LocalProvider_RejectsUntrustedSourceHostBeforeSendingPrompt()
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = new LocalModelInferenceProvider(
            new HttpClient(handler),
            new LocalModelProviderOptions
            {
                BaseAddress = "http://127.0.0.1:11434/v1/",
                ModelId = "test-local"
            },
            TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.InferAsync(
            new ModelInferenceRequest(
                [new ModelChatMessage("user", "Prompt.")],
                SourceContext:
                [
                    new RetrievedSourceContext(
                        "Fake source",
                        "https://irs.gov.example.com/fake",
                        "Untrusted passage.",
                        null,
                        IsVerified: true,
                        IsCached: true)
                ])));

        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task AzureProvider_UsesConfiguredDeploymentAndEnvironmentCredential()
    {
        var variable = $"TIS_TEST_AZURE_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "test-secret");
        Uri? requestUri = null;
        string? requestApiKey = null;
        try
        {
            var provider = new AzureModelInferenceProvider(
                new HttpClient(new RecordingHandler((request, _) =>
                {
                    requestUri = request.RequestUri;
                    requestApiKey = request.Headers.GetValues("api-key").Single();
                    return Task.FromResult(JsonResponse());
                })),
                new AzureModelProviderOptions
                {
                    Endpoint = "https://resource.openai.azure.com/",
                    DeploymentName = "fine-tuned",
                    ApiVersion = "2025-01-01",
                    ApiKeyEnvironmentVariable = variable
                },
                TimeProvider.System);

            var response = await provider.InferAsync(
                new ModelInferenceRequest([new ModelChatMessage("user", "Hello.")]));

            Assert.Equal(
                "https://resource.openai.azure.com/openai/deployments/fine-tuned/chat/completions?api-version=2025-01-01",
                requestUri!.AbsoluteUri);
            Assert.Equal("test-secret", requestApiKey);
            Assert.Equal("Azure", response.ProviderId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static ModelInferenceRequest RequestWithSource(bool isCached) =>
        new(
            [new ModelChatMessage("user", "Explain the rule.")],
            SourceContext:
            [
                new RetrievedSourceContext(
                    "IRS publication",
                    "https://www.irs.gov/pub",
                    "Evidence.",
                    DateTimeOffset.UnixEpoch,
                    IsVerified: true,
                    IsCached: isCached)
            ]);

    private static HttpResponseMessage JsonResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"choices":[{"message":{"content":"Generated answer. See https://www.irs.gov/pub."},"finish_reason":"stop"}],
                 "usage":{"prompt_tokens":8,"completion_tokens":4,"total_tokens":12}}
                """,
                Encoding.UTF8,
                "application/json")
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return send(request, cancellationToken);
        }
    }

    private sealed class StubProvider(
        string id,
        bool isCloud,
        bool isAvailable) : IModelInferenceProvider
    {
        public string Id => id;
        public string DisplayName => $"{id} model";
        public bool IsCloud => isCloud;
        public string? ModelId => $"{id}-model";
        public int AvailabilityChecks { get; private set; }
        public int InferenceCalls { get; private set; }
        public ModelInferenceRequest? LastRequest { get; private set; }
        public ModelInferenceResponse Response { get; init; } = new(
            "stub response",
            id,
            $"{id}-model",
            "stop",
            null,
            TimeSpan.Zero,
            [],
            false,
            "none",
            []);

        public Task<ModelProviderAvailability> CheckAvailabilityAsync(
            CancellationToken cancellationToken = default)
        {
            AvailabilityChecks++;
            return Task.FromResult(new ModelProviderAvailability(
                Id,
                DisplayName,
                IsCloud,
                isAvailable,
                ModelId,
                isAvailable ? "ready" : "not ready"));
        }

        public Task<ModelInferenceResponse> InferAsync(
            ModelInferenceRequest request,
            CancellationToken cancellationToken = default,
            IProgress<string>? progress = null)
        {
            InferenceCalls++;
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private sealed class StubKnowledgeSearch(
        IReadOnlyList<RetrievedSourceContext> results) : IKnowledgeContextSearchService
    {
        public string? Query { get; private set; }

        public Task<IReadOnlyList<RetrievedSourceContext>> SearchAsync(
            string query,
            int maximumDocuments,
            int maximumCharacters,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            return Task.FromResult(results);
        }

    }

    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }

    private sealed class RecordingAuditRepository : IAuditRepository
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
