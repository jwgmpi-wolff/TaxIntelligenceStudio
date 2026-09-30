# Tax Intelligence Studio

## One-click Windows install

[![Download Tax Intelligence Studio](https://img.shields.io/badge/Download-Windows%20installer-0078D4?style=for-the-badge&logo=windows)](https://github.com/jwgmpi-wolff/TaxIntelligenceStudio/archive/refs/heads/main.zip)

1. [Download the complete repository ZIP](https://github.com/jwgmpi-wolff/TaxIntelligenceStudio/archive/refs/heads/main.zip).
2. Extract the ZIP.
3. Double-click **[`Install-Local.cmd`](./Install-Local.cmd)** in the extracted top-level
   folder.

The installer builds and installs the application for the current Windows user,
creates Start Menu and Desktop shortcuts, installs the complete private
Python/DirectML model-training environment, and launches the application.
Administrator rights are not required.

> **Important:** Download and extract the complete ZIP. `Install-Local.cmd` uses the
> accompanying `scripts\Install.ps1` file and cannot install correctly if it is
> downloaded by itself.

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
- `scripts` contains the Windows deployment script described in
  [Deployment](#deployment).

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
- Hardware inventory for Windows NPU, GPU, and CPU devices, on x64 and Arm64.
- Local Hugging Face/PEFT trainer that prefers NVIDIA CUDA QLoRA, then real
  DirectML LoRA on AMD, Intel, Qualcomm, or non-CUDA NVIDIA GPUs, and requires
  explicit consent before CPU fallback. The trainer detects the Python
  interpreter's architecture and reports an unusable interpreter by name.
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
- Scripted Windows deployment to a remote destination over PowerShell
  Remoting, with destination architecture detection, staged rollback-safe
  publishing, Start Menu/Desktop shortcut creation, and uninstall.

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

All commands below are run **from the root of a cloned repository**. Clone it
first if you have not already:

```powershell
git clone https://github.com/jwgmpi-wolff/TaxIntelligenceStudio.git
cd TaxIntelligenceStudio
```

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

## Install on a Windows machine

### Quick start: double-click

1. Get a copy of this repository: clone it, or on GitHub choose
   **Code > Download ZIP** and extract it.
2. Double-click **`Install-Local.cmd`** in the top folder.

That's all. There is nothing to edit, type, or install first. The installer
builds the app from that copy, installs it for the current user, adds Start
Menu and Desktop shortcuts, and starts it. No GitHub token and no
administrator rights are needed. Local model training additionally requires
the Python environment described in [Local model training](#local-model-training);
both installation scripts provision it automatically, and the application
repairs it if a later model operation detects a problem.

To remove it, double-click **`Uninstall.cmd`**. Your data in
`%LOCALAPPDATA%\TaxIntelligenceStudio` is kept.

If Windows shows a SmartScreen prompt for a downloaded ZIP, choose
**More info > Run anyway**.

The installer detects these differences between machines when it runs and
adjusts for them, so the same files work everywhere:

| Detected | Adjustment |
| --- | --- |
| CPU architecture (x64, x86, Arm64), including x64 emulation on Arm64 | Builds for, and installs an SDK matching, the native architecture |
| No suitable .NET 9 SDK | Installs a private per-user SDK; nothing machine-wide changes |
| Windows PowerShell 5.1 or PowerShell 7 | Handles each shell's differences in native-command error handling |
| Windows long paths disabled | Moves the build to a shorter folder automatically |
| Not running as administrator | Uses per-user settings only |
| System drive other than `C:` | Places the results folder on the system drive |
| App already running | Asks it to close before replacing its files |

### Install without a copy of the repository

`scripts\Install.ps1` can also install on a machine that has **nothing
set up in advance** — no clone of this repository, no Git, no .NET SDK or
runtime, and no build tooling. The script downloads everything it needs,
installs to a per-user location, and creates shortcuts. Administrator rights
are not required.

Because this repository is **private**, downloading requires authenticating
to GitHub. See [Credentials](#credentials) below.

Run this on the machine you want the application installed on:

```powershell
$env:GITHUB_TOKEN = '<personal access token>'
irm -Headers @{ Authorization = "Bearer $env:GITHUB_TOKEN"; Accept = 'application/vnd.github.raw' } `
    'https://api.github.com/repos/jwgmpi-wolff/TaxIntelligenceStudio/contents/scripts/Install.ps1?ref=main' |
    Out-File $env:TEMP\Install.ps1
& $env:TEMP\Install.ps1
```

The installer:

1. Uses the local repository copy it is running from, if there is one. No
   download or credential is needed in that case.
2. Otherwise resolves a GitHub credential and downloads a prebuilt release
   asset for the machine's architecture if one exists. This requires no
   compilation.
3. Otherwise downloads the source archive and builds it.
4. When building, installs a private .NET 9 SDK under
   `%LOCALAPPDATA%\TaxIntelligenceStudio\dotnet-sdk` if the machine has no
   suitable SDK. The private SDK is never added to `PATH` and does not affect
   other tooling on the machine.
5. Verifies or installs private x64 Python 3.11 and the complete pinned
   DirectML/LoRA stack under
   `%LOCALAPPDATA%\TaxIntelligenceStudio\.venv-training`. This also works under
   x64 emulation on Windows on Arm.
6. Installs to `%LOCALAPPDATA%\Programs\TaxIntelligenceStudio` and creates a
   Start Menu shortcut.

The target architecture (`win-x64` or `win-arm64`) is detected automatically.

### Credentials

The installer resolves a token in this order, using the first one available:

1. The `-Token` parameter.
2. The `GITHUB_TOKEN` environment variable.
3. `gh auth token`, if the GitHub CLI is installed and signed in.

A fine-grained or classic personal access token with **read access to this
repository's contents** is sufficient.

### Uninstall

Double-click `Uninstall.cmd`, or run:

```powershell
.\scripts\Install.ps1 -Uninstall
```

This removes the application and its shortcuts. Local data in
`%LOCALAPPDATA%\TaxIntelligenceStudio` is preserved.

### Install parameters

| Parameter | Description |
| --- | --- |
| `-Token` | GitHub token. Overrides all other credential sources. |
| `-InstallPath` | Install directory. Defaults to `%LOCALAPPDATA%\Programs\TaxIntelligenceStudio`. |
| `-WorkPath` | Temporary build directory. Defaults to a short folder inside the collector container. Use a short path such as `C:\tis` if the default exceeds the Windows 260 character path limit. |
| `-ResultsPath` | Collector container for the install log and install result. Defaults to `C:\temp\TaxIntelligenceStudio`. |
| `-Runtime` | `win-x64` or `win-arm64`. Detected automatically when omitted. |
| `-Reference` | Branch, tag, or commit to install from. Defaults to `main`. |
| `-FromSource` | Always build from source, ignoring prebuilt releases. |
| `-FromGitHub` | Download from GitHub even when run from a local repository copy. |
| `-DesktopShortcut` | Also create a Desktop shortcut. |
| `-SkipTrainingEnvironment` | Skip verification and installation of the private Python DirectML/LoRA environment. |
| `-Uninstall` | Remove the application. |

### Publishing prebuilt releases

Building from source takes several minutes. To make installs fast, publish a
release so the installer can download a prebuilt payload instead:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The `.github/workflows/release.yml` workflow runs the tests, publishes
self-contained builds for `win-x64` and `win-arm64`, and attaches them to a
GitHub release. It can also be started manually, which requires the tag to
publish as an input:

```powershell
gh workflow run release.yml -f tag=v1.0.0
```

### If the build path is too long

Windows limits paths to 260 characters unless long path support is enabled.
The installer detects this and automatically moves the build to a shorter
folder. If no short writable folder can be found, pass one explicitly:

```powershell
.\Install.ps1 -WorkPath C:\tis
```

Or enable long path support once, as an administrator:

```powershell
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' `
    -Name LongPathsEnabled -Value 1
```

## Deployment

`scripts\Deploy-Windows.ps1` builds the desktop application **on this machine**
and pushes it to a Windows destination over PowerShell Remoting. Use it when
you have a clone and want to install onto one or more other machines without
touching them directly. If the destination has no clone and you want it to
install itself, use [`scripts\Install.ps1`](#install-on-a-windows-machine)
instead.

**Remote destinations use PowerShell Remoting.** When a remote computer name
is given, the script publishes a self-contained payload on the build machine
and copies it to the destination, so the destination needs no .NET SDK, no
.NET runtime, no source tree, and no build tooling. With no computer name, it
deploys to this machine.

The script resolves the repository root from its own location, so it can be
invoked from any working directory.

### Prerequisites

On the **build machine** (where you run the script):

- Windows 10 or later
- .NET 9 SDK
- A clone of this repository

On the **destination machine**:

- Windows 10 or later
- Internet access during deployment to download the private Python 3.11
  runtime and pinned DirectML/LoRA packages. Pass
  `-SkipTrainingEnvironment` only when local model training is not needed.
- PowerShell Remoting enabled:

  ```powershell
  Enable-PSRemoting -Force
  ```

If the two machines are not in the same domain, add the destination to the
build machine's trusted hosts, or use `-UseSSL`:

```powershell
Set-Item WSMan:\localhost\Client\TrustedHosts -Value 'TAX-WS01' -Concatenate
```

### Deploy to a remote machine

```powershell
.\scripts\Deploy-Windows.ps1 -ComputerName TAX-WS01 -Credential (Get-Credential)
```

Run the tests first and add a Desktop shortcut:

```powershell
.\scripts\Deploy-Windows.ps1 -ComputerName TAX-WS01 -RunTests -DesktopShortcut
```

Deploy over HTTPS (WinRM 5986):

```powershell
.\scripts\Deploy-Windows.ps1 -ComputerName TAX-WS01 -UseSSL -Credential (Get-Credential)
```

The script reports the destination host, signed-in user, OS version and
architecture, install path, training-environment path, and payload size before
and after installing.

### Deploy to this machine

Run the script with no computer name. It detects this machine's name
automatically and installs locally, so nothing needs to be edited and WinRM is
not required:

```powershell
.\scripts\Deploy-Windows.ps1
```

Passing this machine's own name, `localhost`, or `.` as `-ComputerName`, or
using `-Local`, does the same.

### Uninstall

```powershell
.\scripts\Deploy-Windows.ps1 -Uninstall
.\scripts\Deploy-Windows.ps1 -ComputerName TAX-WS01 -Uninstall
```

Uninstall removes the installed application and its shortcuts. Local data under
`%LOCALAPPDATA%\TaxIntelligenceStudio` is deliberately preserved.

### Parameters

| Parameter | Description |
| --- | --- |
| `-ComputerName` | Destination computer. Required unless `-Local` is used. |
| `-Credential` | Credential used to connect to the destination. |
| `-Local` | Deploy to this machine instead of a remote destination. |
| `-UseSSL` | Connect over HTTPS (WinRM 5986) instead of HTTP. |
| `-Port` | Override the WinRM port. |
| `-Authentication` | WinRM authentication mechanism (for example `Negotiate`, `Credssp`). |
| `-Runtime` | `win-x64`, `win-arm64`, or `win-x86`. Auto-detected from the destination when omitted. |
| `-Configuration` | `Release` (default) or `Debug`. |
| `-InstallPath` | Install directory on the destination. Defaults to `%LOCALAPPDATA%\Programs\TaxIntelligenceStudio`. |
| `-FrameworkDependent` | Publish a framework-dependent build. The destination must already have the .NET 9 Desktop Runtime. |
| `-NoShortcut` | Skip creating the Start Menu shortcut. |
| `-DesktopShortcut` | Also create a Desktop shortcut. |
| `-RunTests` | Run the unit tests before publishing and abort on failure. |
| `-SkipTrainingEnvironment` | Skip the private x64 Python 3.11 and DirectML/LoRA package installation when model training is not needed. |
| `-ResultsPath` | Collector container for the deployment log and deployment result. Defaults to `C:\temp\TaxIntelligenceStudio` on both machines. |
| `-Uninstall` | Remove the application and shortcuts from the destination. |

Full help is available with `Get-Help .\scripts\Deploy-Windows.ps1 -Full`.

### Behavior and limitations

- The target runtime is detected from the **destination's** architecture, so an
  ARM64 destination receives a `win-arm64` build automatically. Override with
  `-Runtime` only when cross-publishing deliberately.
- The payload is published to a temporary staging folder first. A failed build
  never leaves a partially overwritten installation on the destination.
- Before replacing the application, deployment creates
  `%LOCALAPPDATA%\TaxIntelligenceStudio\.venv-training` with private x64 Python
  3.11 and the tested DirectML/LoRA dependency set. The trainer discovers this
  path automatically. On Windows on Arm, x64 Python runs under emulation
  because PyTorch does not publish native Windows Arm64 wheels.
- The training environment is user-scoped, requires no administrator rights,
  and is retained with the rest of the local application data during uninstall.
- Deployment is refused while the application is running on the destination,
  so a live installation is never corrupted mid-copy.
- A self-contained payload is roughly 200 MB.
- The application runs on the destination under the account you connect as, and
  writes its data to that account's `%LOCALAPPDATA%\TaxIntelligenceStudio`, per
  [Local data locations](#local-data-locations).
- A WinRM session has no interactive desktop, so the script does not launch the
  GUI on a remote destination. Sign in to the destination and use the Start
  Menu shortcut.
- No signed MSI installer is produced yet; see
  [Current scope and next increments](#current-scope-and-next-increments).

### Manual publish

To produce a payload without the script, run from the repository root:

```powershell
dotnet publish src\TaxIntelligenceStudio.Desktop `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  --output dist\TaxIntelligenceStudio
```

Use `--runtime win-arm64` for an ARM64 destination. Copy the output folder to
the destination and run `TaxIntelligenceStudio.Desktop.exe`.

If this fails with `MSBUILD : error MSB1009: Project file does not exist`, the
current directory is not the repository root. `cd` into the clone first, or use
`scripts\Deploy-Windows.ps1`, which is directory independent.

## Local model training

`scripts\Install.ps1` and `scripts\Deploy-Windows.ps1` install a private x64
Python 3.11 environment at
`%LOCALAPPDATA%\TaxIntelligenceStudio\.venv-training` by default. They use a
compatible x64 Python 3.10-3.12 bootstrap when available or download private
x64 Python 3.11.9 from python.org, then install and verify every pinned training
component before completing installation. The Model Trainer discovers and
validates that environment automatically. If it later becomes missing or
incompatible, the application repairs it before training, accelerator
self-test, or local agent-model activation.

The private environment includes all tested DirectML/LoRA components:

- Python 3.11.9 x64
- PyTorch 2.4.1
- TorchVision 0.19.1
- PyTorch DirectML 0.2.5.dev240914
- Transformers 4.46.3
- Datasets 3.1.0
- Accelerate 1.1.1
- PEFT 0.13.2

To repair or manually prepare the environment for a non-CUDA Windows GPU, run
the following command with an x64 Python interpreter:

```powershell
python -m pip install torch==2.4.1 torchvision==0.19.1 torch-directml==0.2.5.dev240914 transformers==4.46.3 datasets==3.1.0 accelerate==1.1.1 peft==0.13.2
```

For a separately managed CUDA or CPU environment, the selected Python must
contain:

```powershell
python -m pip install torch transformers datasets accelerate peft
```

The DirectML versions above must be pinned together: `torch-directml` requires
torch 2.4.1, and current `transformers` releases require torch 2.5 or newer and
fail to import against 2.4.1.

### Trainer platform requirements

Training capability is determined by the architecture of the **Python
interpreter**, not by the architecture of the machine:

| Interpreter | Trains | Notes |
| --- | --- | --- |
| x64 | Yes | PyTorch and `torch-directml` publish Windows x64 wheels. |
| Arm64 | No | Neither package has ever published a `win_arm64` wheel, so no wheel resolves. |
| x86 (32-bit) | No | No wheels are published. |

Windows on Arm machines **can** train. Windows runs x64 processes under
emulation, so an x64 Python installs and runs the x64 wheels normally. This was
verified on a Snapdragon X Elite (X1E80100): a one-step LoRA optimizer update
completed through PyTorch DirectML on the Qualcomm Adreno X1-85 GPU, a CPU LoRA
step also completed, and the trainer's environment check accepted the
interpreter.

The practical failure on an Arm64 machine is selecting the Arm64-native Python,
which cannot resolve any wheel no matter how often the install command is run.
Install the **x64** build of Python from python.org and select it as the trainer
interpreter. The application detects the interpreter's architecture and names
this problem explicitly instead of failing part-way through a run.

Accelerator selection order is CUDA GPU, then DirectML GPU, then CPU when CPU
fallback is explicitly enabled. NPUs are never selected for training.

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

## Results collector container

Every result this repository produces is collected into one labelled local
folder, the **collector container**:

```
C:\temp\TaxIntelligenceStudio
```

Nothing is scattered across per-user temp folders any more. What lands there:

| File | Written by |
| --- | --- |
| `accelerator-self-test-*.json` | The application's accelerator self-test |
| `accelerator-probe-*.json` | The Python accelerator probe (raw result) |
| `install-*.log`, `install-result-*.json` | `scripts\Install.ps1` |
| `uninstall-*.log`, `uninstall-result-*.json` | `scripts\Install.ps1 -Uninstall` |
| `deploy-*.log`, `deploy-result-*.json` | `scripts\Deploy-Windows.ps1` |
| `azure-retail-prices-*.log`, `azure-retail-prices-result-*.json` | `scripts\Get-AzureRetailPrices.ps1` |

Redirect the container with the `TAXINTELLIGENCESTUDIO_RESULTS` environment
variable, or with `-ResultsPath` on any of the scripts:

```powershell
$env:TAXINTELLIGENCESTUDIO_RESULTS = 'D:\collected'
.\scripts\Install.ps1 -ResultsPath 'D:\collected'
```

`Install.ps1` and `Deploy-Windows.ps1` also set `TAXINTELLIGENCESTUDIO_RESULTS`
on the target machine, so the installed application collects its own results to
the same container without further configuration.

Collecting a result never fails the operation that produced it. If the container
cannot be created — a locked-down machine, or a non-admin user with no access to
`C:\temp` — the work still completes and results fall back to the temp folder.

Deployments collect on both sides: the build machine keeps the deployment log
and result, and the destination gets its own container. Retrieve a remote one
with:

```powershell
$session = New-PSSession -ComputerName TAX-WS01
Copy-Item -FromSession $session -Path 'C:\temp\TaxIntelligenceStudio\*' `
  -Destination 'C:\temp\TaxIntelligenceStudio\TAX-WS01' -Recurse
```

### Azure retail price export

`scripts\Get-AzureRetailPrices.ps1` queries the public Azure Retail Prices API —
no credential and no Azure subscription required — and writes a CSV to
`C:\temp\azure_retail_prices.csv`:

```powershell
.\scripts\Get-AzureRetailPrices.ps1 -ServiceName 'Virtual Machines' -ArmRegionName 'eastus'
```

The run log and a run result recording the filter, record count, part number
match counts, and CSV path are collected to the container. Narrow broad queries
with `-Filter`, `-ServiceName`, `-ArmRegionName`, `-PriceType`, or
`-MaxRecords`; an unfiltered export retrieves every meter Azure publishes and
takes a long time.

#### Part numbers

The retail prices API does not publish part numbers. A part number is the
identifier that appears on your invoice and in the price sheet, so it has to
come from one of those, joined to retail prices on `meterId`.

Supply a price sheet or invoice export — CSV, or a `.zip` containing one:

```powershell
.\scripts\Get-AzureRetailPrices.ps1 -ServiceName 'Virtual Machines' `
  -PriceSheetPath C:\temp\pricesheet.csv
```

Header names are matched on their letters alone, so `MeterID`, `Meter ID`, and
`meterId` all work, as do `PartNumber`, `Part Number`, and `Part Num`.

Or have the script download the price sheet:

```powershell
az billing account list                      # find the billing account id
az billing profile list --account-name <id>  # find the billing profile id

.\scripts\Get-AzureRetailPrices.ps1 -ServiceName 'Virtual Machines' `
  -BillingAccountId <id> -BillingProfileId <profile> -InvoiceId <invoice>
```

Downloading requires a billing role such as Billing Profile Reader. A
subscription owner does not have it by default — the script reports that
explicitly and tells you to request the role or use `-PriceSheetPath`.

`partNumber` is always the first column, even when no price sheet is supplied
(left empty), so the CSV schema never changes. The price sheet is resolved
before the export begins, so a bad path or a missing role fails immediately
rather than after a long download.

| Parameter | Description |
| --- | --- |
| `-Filter` | Raw OData filter, for example `"serviceName eq 'Azure OpenAI'"`. Takes precedence over the convenience parameters. |
| `-ServiceName` | Restrict to one service. |
| `-ArmRegionName` | Restrict to one region, for example `eastus`. |
| `-PriceType` | `Consumption`, `Reservation`, or `DevTestConsumption`. |
| `-CurrencyCode` | Currency for returned prices. Defaults to `USD`. |
| `-MaxRecords` | Stop after this many rows. `0` (default) retrieves everything matched. |
| `-PriceSheetPath` | Price sheet or invoice CSV (or `.zip`) supplying part numbers. |
| `-BillingAccountId` | Download the price sheet instead of supplying a file. |
| `-BillingProfileId` | Billing profile for the download. Required on a Microsoft Customer Agreement. |
| `-InvoiceId` | Download the price sheet for one specific invoice. |
| `-OutputPath` | CSV file to write. Defaults to `C:\temp\azure_retail_prices.csv`. |
| `-ResultsPath` | Collector container. Defaults to `C:\temp\TaxIntelligenceStudio`. |

## Local data locations

- Application database: `%LOCALAPPDATA%\TaxIntelligenceStudio\tax-intelligence.db`
- Fine-tuned model output: `%LOCALAPPDATA%\TaxIntelligenceStudio\Models`
- Active agent selection:
  `%LOCALAPPDATA%\TaxIntelligenceStudio\active-agent-model.json`
- Hugging Face cache: `%USERPROFILE%\.cache\huggingface\hub`
- Results collector container: `C:\temp\TaxIntelligenceStudio`

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
