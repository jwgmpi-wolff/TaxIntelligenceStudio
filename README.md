# Tax Intelligence Studio

Image-only PDFs are rendered and OCR'd entirely on-device with Windows OCR. Recognized
W-2, 1099, 1098, SSA-1099, HSA, K-1, and property-tax fields retain their form,
box, and source-page provenance. Unsupported or ambiguous scans are sent to review
instead of being reported as successfully parsed.

Imported financial and tax documents are processed locally and held only in the
running application's memory. Their contents are not copied into the source tree,
published, retained as application training data, or sent to an external OCR
service. Tests use synthetic documents and values only.

Tax Intelligence Studio is a native Windows .NET 9 WPF application for building
a versioned IRS corpus and grounded local-model dataset, importing financial
exports, creating federal tax workpapers, and exporting cited reports.

The blue IRS document application icon is an original, unofficial product icon.
It is not the IRS seal and does not imply endorsement by the Internal Revenue
Service or the United States government.

## Architecture

The solution follows a layered desktop architecture:

- `TaxIntelligenceStudio.Domain` contains immutable financial, tax, source,
  training, and model records.
- `TaxIntelligenceStudio.Application` defines service contracts and application
  boundaries.
- `TaxIntelligenceStudio.Infrastructure` implements SQLite persistence, IRS
  acquisition, financial parsing, tax analysis, report generation, hardware
  discovery, model training, and agent-model deployment.
- `TaxIntelligenceStudio.Desktop` is the native WPF/MVVM interface and hosts the
  local Windows OCR implementation.
- `TaxIntelligenceStudio.Service` is an optional localhost ASP.NET Core service.
- `TaxIntelligenceStudio.UnitTests` contains synthetic workflow and policy
  regression tests.

## End-to-end workflow

1. **Acquire IRS sources** - discover allowed IRS sitemap and linked content,
   retain source metadata and hashes, and version changed content.
2. **Generate grounded datasets** - produce citation-bearing JSONL records from
   acquired authoritative material.
3. **Import financial and tax documents** - select multiple PDF, CSV, Excel, or
   text files; each file receives an explicit success or review diagnostic.
4. **Extract tax evidence** - parse transaction tables, render and OCR scanned
   PDF pages locally, detect standard information returns, and retain form,
   box, year, page/row, confidence, and source-file provenance.
5. **Analyze for federal reporting** - classify records, build Form 1040 and
   schedule mappings, identify evidence-backed review opportunities, and verify
   every cited IRS source live with caching disabled.
6. **Export workpapers** - create PDF or Word reports containing mappings,
   review items, evidence URLs, verification timestamps, and disclaimers.
7. **Train and deploy a local model** - fine-tune a selected Hugging Face model
   with resumable PEFT/LoRA checkpoints, activate a completed adapter, test it
   locally, or merge it for Ollama.

## Implemented

- Native WPF desktop application using MVVM Toolkit, dependency injection, and
  WPF UI (Fluent styling). It is not a website.
- IRS `robots.txt` and sitemap discovery, wildcard allow/disallow handling,
  rate limiting, same-domain link discovery, and prioritization of linked IRS
  PDFs.
- Full-text extraction from IRS HTML and PDF material.
- Versioned IRS content, retrieval timestamps, and SHA-256 fingerprints.
- Citation-grounded chat-format JSONL export for local model training or
  fine-tuning.
- Hardware inventory for Windows NPU, GPU, and CPU devices.
- Local Hugging Face/PEFT trainer that prefers NVIDIA CUDA QLoRA, then real
  DirectML LoRA on AMD, Intel, Qualcomm, or non-CUDA NVIDIA GPUs, and requires
  explicit consent before CPU fallback.
- Multi-file PDF, CSV, XLSX, XLSM, and TXT financial import.
- Per-file parse diagnostics with automatic live IRS-verified analysis after
  successful record extraction; unsupported or ambiguous layouts are reported
  explicitly rather than counted as successful imports.
- On-device Windows OCR for image-only PDFs and PDFs whose embedded text layer
  omits form values. OCR runs page by page and never uses a cloud endpoint.
- Coordinate-aware and reading-order-aware extraction for W-2, 1099-INT,
  1099-DIV, 1099-B, 1099-R, 1099-NEC, 1099-MISC, 1099-K, 1099-SA, 1098,
  1098-E, 1098-T, 1095-A, 5498, 5498-SA, SSA-1099, Schedule K-1, annual
  mortgage/property-tax statements, and Fidelity year-end reports.
- Tax-form provenance columns for form, box, tax year, source page/row, and
  recognition confidence. Informational fields such as withholding,
  tax-deferred income, and retirement contributions remain available for
  reconciliation without being added to income totals.
- Statement-aware normalization removes duplicate overview/detail totals,
  prevents adjacent empty tax boxes from inheriting another box's value, and
  applies correct expense signs to credit-card purchases while excluding card
  payments and credits from tax totals.
- Determinate import and analysis progress meters report the current file,
  extracted-record count, classification/mapping stage, and each live IRS
  source verification.
- Transaction normalization, tax-category classification, and mapping to Form
  1040, Schedules A/B/C/D/E, Form 8949, and Form 8889 as applicable.
- Evidence-linked deduction and loss-review opportunities.
- PDF and Microsoft Word tax-analysis workpaper export.
- Live HTTPS verification restricted to `irs.gov`, `treasury.gov`, and
  `federalregister.gov`.
- Explicit `no-store`, `no-cache`, and zero-age request and response policies.
- Fail-closed behavior when a source is unavailable, redirects, returns empty
  content, or is outside the authoritative allowlist.
- SHA-256 source content fingerprints and timestamped evidence.
- SQLite corpus and audit records for source versions, inputs, outputs, model
  identifiers, and integrity hashes.
- Unit tests for source allowlisting, no-cache headers, refusal behavior,
  financial import, local OCR integration, standard-form field pairing,
  statement signs, Fidelity summaries, tax mappings, grounded datasets, and
  PDF/DOCX output.

## Financial document handling

The importer uses the strongest available local evidence in this order:

1. Structured CSV or Excel headers, including brokerage-specific decorated
   headers.
2. PDF word geometry reconstructed into visual rows.
3. Local Windows PDF rendering and OCR when the text layer is absent or yields
   no supported tax facts.
4. Form- and statement-specific field matching with duplicate suppression.

OCR does not make a document authoritative by itself. Recognition confidence
describes extraction confidence, not tax eligibility. Ambiguous or unmatched
values remain review items and are never invented from a file name, nearby
unrelated number, or model response.

Year-end brokerage reports are treated as supporting estimates, not substitutes
for final Forms 1099 or 5498. Retirement and tax-free income summaries are
informational. Form 1098 interest, property taxes, rents, dividends, interest,
capital activity, HSA activity, and other recognized facts are routed to the
appropriate review mapping with live IRS citations.

Credit-card purchases are represented as expenses. Payments, credits, refunds,
and transfers are retained for reconciliation but excluded from income and
deduction totals unless separately substantiated.

## Build and test

Requirements:

- Windows 10 or later
- .NET 9 SDK

```powershell
dotnet restore TaxIntelligenceStudio.sln
dotnet build TaxIntelligenceStudio.sln
dotnet test TaxIntelligenceStudio.sln --no-build
dotnet run --project src\TaxIntelligenceStudio.Desktop
```

Run the optional local API:

```powershell
dotnet run --project src\TaxIntelligenceStudio.Service
```

## Local model training

The Model Trainer tab launches a local Python PEFT/LoRA workflow. The selected
Python environment must contain:

```powershell
python -m pip install torch transformers datasets accelerate peft
```

For a non-CUDA Windows GPU, install the tested DirectML runtime:

```powershell
python -m pip install torch==2.4.1 torchvision==0.19.1 torch-directml==0.2.5.dev240914
```

Enter a local Hugging Face-compatible model folder or model identifier, select
the exported IRS JSONL dataset, choose an output directory, and start training.
The application uses CUDA when an NVIDIA CUDA runtime is detected, otherwise it
uses the platform GPU through PyTorch DirectML. CPU fallback is disabled unless
explicitly selected.

Training writes a complete checkpoint every 10 optimizer steps and retains the
10 most recent checkpoints. After an interruption, choose the output directory,
click **Refresh**, select a checkpoint from **Resume checkpoint**, and start
training. The trainer restores the model adapter, optimizer, scheduler, random
number generator, and completed-step state. Resume is rejected when the selected
checkpoint's model, dataset SHA-256, epochs, batch size, or learning rate do not
match. Checkpoints without a compatibility manifest are rejected, and selected
checkpoints must resolve inside the output directory. Choose **Start new
training** to ignore existing checkpoints.

## Agent model deployment

The **Agent Model** tab automatically lists active local fine-tuning processes,
completed PEFT/LoRA adapters, and saved checkpoint adapters under the selected
model-output folder. In-progress rows are visible but remain non-activatable
until the trainer writes a usable checkpoint or completed adapter. Select a
ready artifact and one of these deployment runtimes:

- **Transformers / PEFT** validates and persists the base-model plus adapter
  pairing for local use. **Run test prompt** loads that exact pairing through
  the best available CUDA or DirectML GPU, with CPU as the explicit final
  fallback.
- **Ollama** merges the selected adapter into its base model on CPU, writes a
  standalone safe-tensors model and Modelfile, runs `ollama create`, and makes
  the resulting Ollama model active. Ollama must be installed and available on
  `PATH`; merging can require substantial memory and disk space.

The active selection is retained in
`%LOCALAPPDATA%\TaxIntelligenceStudio\active-agent-model.json`. A raw model test
response is not treated as verified tax guidance; tax outputs still require the
application's authoritative-source validation and evidence chain.

Windows ML, QNN, and OpenVINO currently expose NPUs for inference rather than
model-weight training. Tax Intelligence Studio reports NPUs as
inference-capable and does not falsely route fine-tuning to them.

Use **Run real accelerator self-test** to execute two measured workloads:

- a real one-step LoRA optimizer update on the selected Windows GPU through
  PyTorch DirectML;
- an ONNX graph assigned to the Qualcomm QNN HTP execution provider, with
  profiling used to confirm that at least one node actually ran on the Hexagon
  NPU rather than silently falling back to CPU.

## Local data locations

- Application database: `%LOCALAPPDATA%\TaxIntelligenceStudio\tax-intelligence.db`
- Fine-tuned model output: `%LOCALAPPDATA%\TaxIntelligenceStudio\Models`
- Active agent selection:
  `%LOCALAPPDATA%\TaxIntelligenceStudio\active-agent-model.json`
- Hugging Face cache: `%USERPROFILE%\.cache\huggingface\hub`

Imported financial document contents are not persisted by the import workflow.
The SQLite database stores the IRS corpus and audit records described above.
Model-training datasets are generated from the acquired IRS corpus, not from
private financial or tax forms.

## Live-only policy

The local database contains the explicitly requested training corpus and
version history; it is not used as a silent substitute for current guidance.
Every report re-fetches each cited IRS source with HTTP caching disabled. If
current authoritative evidence cannot be fetched, report generation is blocked
with `Current guidance could not be verified.`

## Current scope and next increments

The following capabilities still require additional implementation and
independent validation before production tax filing:

1. taxpayer questionnaire, filing-status/dependent rules, complete tax
   calculations, state returns, and year-specific line-number tables;
2. production RAG/vector database orchestration and full model
   governance;
3. encrypted document vault, RBAC, signed audit chains, malware scanning, and
   backup/recovery;
4. IRS Modernized e-File integration through an authorized IRS e-file provider;
5. installer signing, MSI packaging, accessibility/performance testing, and
   professional tax-domain acceptance.

The current application creates review workpapers; it does not calculate a
guaranteed maximum refund or submit a return. Financial exports alone do not
contain the taxpayer, dependent, basis, withholding, election, and eligibility
data needed to make those claims safely.
