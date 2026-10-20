using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Inference;
using TaxIntelligenceStudio.Infrastructure.Training;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class TrainedModelInferenceProviderTests
{
    [Fact]
    public async Task TrainedProvider_SendsHistoryAndRetrievedTaxEvidenceToActiveAdapter()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "adapter_config.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(directory, "adapter_model.safetensors"), "test");
            var agent = new RecordingAgent(new ActiveAgentModel(
                "project-base", directory, AgentModelService.TransformersRuntime, null,
                DateTimeOffset.UnixEpoch));
            var options = new ModelProviderOptions { OfflineMode = true };
            var provider = new TrainedModelInferenceProvider(agent, options, TimeProvider.System);
            var service = new ModelInferenceService(
                new ModelProviderResolver([provider], options), new Audit(), options,
                TimeProvider.System, new Search());
            ModelChatMessage[] messages =
            [
                new("user", "How do rental expenses work?"),
                new("assistant", "Some rental expenses may be deductible."),
                new("user", "What records support those expenses?")
            ];

            var response = await service.InferAsync(new ModelInferenceRequest(messages), "Trained");

            Assert.Equal(messages, agent.Request!.Messages);
            Assert.Equal("IRS rental evidence", Assert.Single(agent.Request.SourceContext!).Content);
            Assert.Contains(directory, response.ModelId, StringComparison.Ordinal);
            Assert.Equal(["https://www.irs.gov/publications/p527"], response.Citations);
            Assert.Equal("offline", response.SourceDataMode);
            Assert.Equal("Trained", response.ProviderId);
        }
        finally
        {
            File.Delete(Path.Combine(directory, "adapter_config.json"));
            File.Delete(Path.Combine(directory, "adapter_model.safetensors"));
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task TrainedProvider_MissingActivationFailsWithoutInvokingRuntime()
    {
        var agent = new RecordingAgent(null);
        var provider = new TrainedModelInferenceProvider(
            agent, new ModelProviderOptions(), TimeProvider.System);

        Assert.False((await provider.CheckAvailabilityAsync()).IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InferAsync(
            new ModelInferenceRequest([new ModelChatMessage("user", "Question")])));
        Assert.Null(agent.Request);
    }

    private sealed class RecordingAgent(ActiveAgentModel? model) : IAgentModelService
    {
        public ModelInferenceRequest? Request { get; private set; }
        public ActiveAgentModel? GetActiveModel() => model;
        public Task<string> InferChatAsync(
            ActiveAgentModel activeModel, ModelInferenceRequest request, string configuredPython,
            CancellationToken cancellationToken = default, IProgress<string>? progress = null)
        {
            Assert.Same(model, activeModel);
            Request = request;
            return Task.FromResult("See https://www.irs.gov/publications/p527");
        }
        public IReadOnlyCollection<AgentModelCandidate> DiscoverModels(string rootDirectory) =>
            throw new NotSupportedException();
        public Task<ActiveAgentModel> ActivateAsync(
            AgentModelCandidate candidate, string runtime, string? runtimeModelName = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> TestPromptAsync(
            ActiveAgentModel activeModel, string prompt, string configuredPython,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ActiveAgentModel> CreateOllamaModelAsync(
            AgentModelCandidate candidate, string modelName, string configuredPython,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Search : IKnowledgeContextSearchService
    {
        public Task<IReadOnlyList<RetrievedSourceContext>> SearchAsync(
            string query, int maximumDocuments, int maximumCharacters,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RetrievedSourceContext>>(
            [
                new("IRS rental publication", "https://www.irs.gov/publications/p527",
                    "IRS rental evidence", DateTimeOffset.UnixEpoch, true, true)
            ]);
    }

    private sealed class Audit : IAuditRepository
    {
        public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
