# Local and Azure Model Integration Gap Analysis

## Repository inspected

The solution contains Domain, Application, Infrastructure, Desktop, Service,
and UnitTests projects. The desktop is WPF/MVVM. Infrastructure uses SQLite for
the versioned IRS knowledge corpus, financial import, report generation,
hardware discovery, model training, and local agent-model activation. The
optional ASP.NET Core service exposes health, guidance verification, crawling,
and hardware-test routes. The build uses .NET 9.

## Existing behavior

### Inference and guidance

Before provider integration, the application had no Azure OpenAI client, no
fine-tuned GPT-4.1-mini deployment configuration, and no shared inference
interface. `LiveGuidanceService` verifies supplied HTTPS sources and returns a
verification message; it does not call a generative model. `AgentModelService`
discovers PEFT adapters, persists a selected adapter, runs Transformers/PEFT
inference through a Python subprocess, and can merge an adapter and create an
Ollama model. Those local agent operations are separate from tax guidance.

The new `IModelInferenceProvider` contract and resolver support an explicitly
selected Azure OpenAI deployment and a loopback-first OpenAI-compatible endpoint.
No actual Azure endpoint/deployment is checked into configuration. The local
provider requires an already-running endpoint and configured model alias; it
does not start a runtime or download weights. Neither provider falls back to
the other. A bounded corpus search retrieves ranked excerpts from current
allowlisted HTTPS government-source documents for both providers, including
source URL and verification timestamp. Cached content is labeled and every
response using it warns that the source may be stale. Audit metadata excludes
prompts and generated text.

### Dataset export

`DatasetExportService` reads current `KnowledgeDocuments`, chunks their content,
and writes UTF-8 JSONL chat messages with the IRS source URL in the assistant
answer. Its records are generic source-passage examples, not validated tax
scenario answers. Current export supports BOM-free message-only records and,
in the current worktree implementation, optional upload-file splitting by
maximum bytes or requested file count. It validates JSONL shape and writes a
validation report and manifest with file hashes/sizes. The full export remains
available for local training.

It now produces deterministic train/validation/test files grouped by source
URL, a canonical synthetic-example sidecar, duplicate counts, a limited
email/SSN-pattern scan with exclusion counts, and split file hashes. Upload
parts remain distinct from ML splits. The limited scanner is not comprehensive
PII detection, and generated examples still are not scenario-grounded labels.
The trainer consumes exported splits when present and evaluates validation and
test losses after training. It records run settings, dataset hashes, environment
data, evaluation metrics, and adapter-file hashes. Evaluation against synthetic
source-passage transformations does not establish tax-answer accuracy.

### Training and artifacts

`LocalModelTrainingService` launches the embedded `local_lora_trainer.py` with
`ProcessStartInfo.ArgumentList`, captures progress/stdout/stderr, supports
cancellation and checkpoint resumption, and selects CUDA, DirectML, or
explicitly allowed CPU. The trainer uses Transformers, Datasets, Accelerate,
PEFT/LoRA, and `trust_remote_code=False`. The DirectML path has specialized
compatibility handling. The UI exposes base model, dataset, Python executable,
output folder, epoch, batch, learning-rate defaults, CPU fallback, and
checkpoint selection.

The workflow does not yet provide model-card/license review, baseline-vs-
fine-tuned evaluation, model merge/quantization validation, formal artifact
registration, or full hardware recommendations.

### Catalog and hardware

`ModelCatalogService` currently uses a fixed in-code list of base model IDs,
families, and parameter counts; it has no enabled laptop recommendation. It
does not record license, revision, verified download source, runtime
compatibility, or hashes. Windows hardware discovery reports available CPU/GPU/NPU accelerators
and training/inference flags; it is not a full RAM/VRAM/disk recommendation
engine.

### Security and tests

The source verifier allows exact HTTPS government hosts, disables redirects,
and uses no-cache requests. The database persists source and audit records.
Training/runtime Python model loads use `trust_remote_code=False`. New provider
clients disable redirects, bound request/response sizes, use environment-based
Azure credentials, and default local traffic to loopback. Provider selection
does not silently fall back. Offline mode blocks cloud inference, source
refresh, and live source HTTP verification; inference can retrieve bounded
excerpts from the current local corpus and marks cached evidence as potentially
stale.

The existing unit suite covers financial import, source verification, dataset
export, accelerator selection, checkpointing, and local-model activation. The
provider work adds mocked provider-protocol, selection, loopback, offline, and
prompt-safe audit tests. No tests require live Azure credentials, internet
access, or model downloads.

## Reuse plan and compatibility

- Keep the existing IRS corpus, dataset export, trainer, model catalog,
  hardware detection, PEFT/Ollama operations, report workflow, and SQLite store.
- Use one request/response contract for Azure and local OpenAI-compatible chat.
- Route provider choice through a resolver that checks configured availability
  and never implicitly substitutes cloud for local or vice versa.
- Gate cloud calls and authoritative-source refresh at the service layer in
  offline mode; retrieve only from the local corpus when offline.
- Keep dynamic tax law in retrieved, traceable source context; never present
  an ungrounded model response as verified guidance.
- Defer evaluation,
  licensing/provenance, merging/quantization, managed runtime lifecycle, and
  comprehensive model-management APIs until those behaviors can be tested
  against actual validated data and selected runtimes.

See [local model setup](./LOCAL_MODEL_SETUP.md), [inference](./LOCAL_MODEL_INFERENCE.md),
[dataset format](./DATASET_FORMAT.md), [evaluation](./MODEL_EVALUATION.md), and
[offline mode](./OFFLINE_MODE.md) for current configuration and limitations.
