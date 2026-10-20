using System.Reflection;
using System.IO;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Desktop.ViewModels;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Inference;
using TaxIntelligenceStudio.Infrastructure.Training;

namespace TaxIntelligenceStudio.Desktop.Tests;

public sealed class ConcurrentJobsTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Jobs_CanStartInEitherOrderAndCompleteIndependently(bool trainingFirst, bool finishTrainingFirst)
    {
        var fixture = new Fixture();
        var vm = fixture.ViewModel;
        Task training;
        Task inference;
        if (trainingFirst)
        {
            training = vm.TrainModelCommand.ExecuteAsync(null);
            Assert.True(vm.IsTraining);
            Assert.False(vm.IsBusy);
            Assert.True(vm.RunModelInferenceCommand.CanExecute(null));
            inference = vm.RunModelInferenceCommand.ExecuteAsync(null);
        }
        else
        {
            inference = vm.RunModelInferenceCommand.ExecuteAsync(null);
            Assert.True(vm.IsInferenceRunning);
            Assert.True(vm.TrainModelCommand.CanExecute(null));
            Assert.True(vm.ChooseTrainingDatasetCommand.CanExecute(null));
            training = vm.TrainModelCommand.ExecuteAsync(null);
        }
        Assert.True(vm.IsTraining);
        Assert.True(vm.IsInferenceRunning);
        Assert.False(vm.TrainModelCommand.CanExecute(null));
        Assert.False(vm.RunModelInferenceCommand.CanExecute(null));
        Assert.False(vm.CanEditTraining);
        Assert.True(vm.TrainModelCancelCommand.CanExecute(null));
        Assert.True(vm.RunModelInferenceCancelCommand.CanExecute(null));
        if (finishTrainingFirst)
        {
            fixture.CompleteTraining();
            await training;
            Assert.True(vm.IsInferenceRunning);
            Assert.True(vm.IsBusy);
            Assert.True(vm.CanEditTraining);
            fixture.CompleteInference();
            await inference;
        }
        else
        {
            fixture.CompleteInference();
            await inference;
            Assert.True(vm.IsTraining);
            Assert.False(vm.IsBusy);
            Assert.False(vm.CanEditTraining);
            vm.ModelInferencePrompt = "Next question while training continues";
            Assert.True(vm.RunModelInferenceCommand.CanExecute(null));
            fixture.CompleteTraining();
            await training;
        }
        Assert.False(vm.IsTraining);
        Assert.False(vm.IsInferenceRunning);
        Assert.True(vm.CanEditTraining);
        Assert.Contains("Test answer", vm.ModelInferenceResponse);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_StopsOnlyTheSelectedJob(bool cancelTraining)
    {
        var fixture = new Fixture();
        var vm = fixture.ViewModel;
        var training = vm.TrainModelCommand.ExecuteAsync(null);
        var inference = vm.RunModelInferenceCommand.ExecuteAsync(null);
        if (cancelTraining)
        {
            vm.TrainModelCancelCommand.Execute(null);
            await training;
            Assert.False(vm.IsTraining);
            Assert.True(vm.IsInferenceRunning);
            fixture.CompleteInference();
            await inference;
            Assert.Contains("cancelled", vm.TrainingStatus);
        }
        else
        {
            vm.RunModelInferenceCancelCommand.Execute(null);
            await inference;
            Assert.True(vm.IsTraining);
            Assert.False(vm.IsInferenceRunning);
            fixture.CompleteTraining();
            await training;
            Assert.Contains("cancelled", vm.ModelProviderStatus);
        }
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsTraining);
    }

    [Fact]
    public void UnrelatedBusyOperation_StillBlocksStartingTraining()
    {
        var vm = new Fixture().ViewModel;
        vm.IsBusy = true;
        Assert.False(vm.CanEditTraining);
        Assert.False(vm.TrainModelCommand.CanExecute(null));
        Assert.False(vm.ChooseTrainingOutputCommand.CanExecute(null));
    }

    [Fact]
    public async Task RejectedInference_DoesNotEnterConversationOrEnableExport()
    {
        var fixture = new Fixture();
        var vm = fixture.ViewModel;
        var task = vm.RunModelInferenceCommand.ExecuteAsync(null);
        fixture.FailInferenceIntegrity();
        await task;
        Assert.Equal(string.Empty, vm.ModelInferenceResponse);
        Assert.Equal("Test question", vm.ModelInferencePrompt);
        Assert.False(vm.ExportInferencePdfCommand.CanExecute(null));
        Assert.False(vm.ExportInferenceWordCommand.CanExecute(null));
        Assert.Contains("Response withheld", vm.ModelProviderStatus);
        var retry = vm.RunModelInferenceCommand.ExecuteAsync(null);
        fixture.CompleteInference();
        await retry;
        Assert.Single(fixture.LastInferenceRequest!.Messages);
        Assert.Contains("Test answer", vm.ModelInferenceResponse);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failure_LeavesTheOtherJobRunning(bool trainingFails)
    {
        var fixture = new Fixture();
        var vm = fixture.ViewModel;
        var training = vm.TrainModelCommand.ExecuteAsync(null);
        var inference = vm.RunModelInferenceCommand.ExecuteAsync(null);
        fixture.Fail(trainingFails);
        if (trainingFails)
        {
            await training;
            Assert.True(vm.IsInferenceRunning);
            Assert.True(vm.IsBusy);
            Assert.Contains("Test failure", vm.TrainingStatus);
            fixture.CompleteInference();
            await inference;
        }
        else
        {
            await inference;
            Assert.True(vm.IsTraining);
            Assert.False(vm.IsBusy);
            Assert.Contains("Test failure", vm.ModelProviderStatus);
            fixture.CompleteTraining();
            await training;
        }
        Assert.False(vm.IsTraining);
        Assert.False(vm.IsInferenceRunning);
    }

    private sealed class Fixture
    {
        private readonly TaskCompletionSource<ModelTrainingResult> _training = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<ModelInferenceResponse> _inference = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MainViewModel ViewModel { get; }
        public ModelInferenceRequest? LastInferenceRequest { get; private set; }

        public Fixture()
        {
            var trainer = Stub<IModelTrainingService>((method, args) => method.Name switch
            {
                nameof(IModelTrainingService.TrainAsync) => _training.Task.WaitAsync((CancellationToken)args![2]!),
                nameof(IModelTrainingService.GetCheckpoints) => new ModelTrainingCheckpoint[] { new("New run", null, null, null) },
                _ => throw new NotSupportedException(method.Name)
            });
            var inference = Stub<IModelInferenceService>((_, args) =>
            {
                LastInferenceRequest = (ModelInferenceRequest)args![0]!;
                return _inference.Task.WaitAsync((CancellationToken)args[2]!);
            });
            var agent = Stub<IAgentModelService>((method, _) => method.Name switch
            {
                nameof(IAgentModelService.GetActiveModel) => null,
                nameof(IAgentModelService.DiscoverModels) => Array.Empty<AgentModelCandidate>(),
                _ => throw new NotSupportedException(method.Name)
            });
            var history = Stub<IInferencePromptHistory>((method, _) => method.Name switch
            {
                nameof(IInferencePromptHistory.SaveAsync) => Task.CompletedTask,
                nameof(IInferencePromptHistory.GetAsync) => Task.FromResult<IReadOnlyList<SavedInferencePrompt>>([]),
                _ => throw new NotSupportedException(method.Name)
            });
            ViewModel = new MainViewModel(
                null!, null!, null!, null!, null!, null!, trainer,
                new ModelCatalogService(), null!, agent, inference, null!, null!, null!,
                history, new ModelProviderOptions());
            ViewModel.TrainingDatasetPaths.Add("test.jsonl");
            ViewModel.ModelInferencePrompt = "Test question";
        }

        public void CompleteTraining() =>
            _training.SetResult(new ModelTrainingResult(
                true, "CPU", "test-output", "Test run finished", DateTimeOffset.UtcNow));

        public void CompleteInference() =>
            _inference.TrySetResult(new ModelInferenceResponse("Test answer", "Trained", "test-adapter",
                "stop", null, TimeSpan.Zero, [], true, "cached", []));

        public void FailInferenceIntegrity()
        {
            _inference.SetException(new InvalidDataException("Response withheld: malformed currency amount."));
            _inference = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Fail(bool training)
        {
            if (training)
            {
                _training.SetException(new InvalidOperationException("Test failure"));
            }
            else
            {
                _inference.SetException(new InvalidOperationException("Test failure"));
            }
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ServiceStub>();
        ((ServiceStub)(object)proxy).Handler = handler;
        return proxy;
    }

    public class ServiceStub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } =
            (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args);
    }
}
