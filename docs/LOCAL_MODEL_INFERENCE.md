# Model Inference

The shared application request is a list of `system`, `user`, and `assistant`
messages plus optional tax year, source context, output-token limit,
temperature, and seed. Source context must be verified HTTPS evidence. The
response carries provider/model IDs, finish reason, usage when supplied,
elapsed time, cited source URLs, source-data mode, and warnings.

## Tax-assistance behavior

The model is prompted to act as an expert U.S. tax research and planning
assistant: apply the facts supplied, explain relevant options and tradeoffs,
show calculations when useful, and ask focused follow-up questions when
important facts are missing. It should distinguish assumptions and
calculations from tax-law conclusions, cite exact supplied authoritative URLs,
and say when current law cannot be verified. It must not claim to be a licensed
tax professional or IRS representative. Responses are not a substitute for
professional review, especially for complex or high-stakes decisions.

## Retrieving earlier prompts

Expand **Saved prompts (local history)** beneath the message area. Typed prompts
are saved automatically when sent, including when model generation fails, and
loaded again on app startup. Select an entry and click **Reuse selected prompt**
to place it in the composer for review/editing; it is not sent automatically.
Reusing a prompt does not restore earlier replies, attachments or conversation
context. Start **New conversation** first if you want an independent question.

Use **Delete selected** or **Clear history** (with confirmation) to remove saved
entries. **Refresh history** includes changes made by another app window.
New conversation does not delete saved prompts. History is local SQLite storage
in `%LOCALAPPDATA%\TaxIntelligenceStudio\tax-intelligence.db`, not a cloud
service. It contains the text you typed, which may include sensitive taxpayer
information; it excludes extracted attachment content and model replies.
Prompts sent before this feature was installed cannot be recovered from history.

## Exporting inference results

After a successful response, use **Export response PDF** or **Export response
Word** in the conversation area and select a save location. Word exports are
editable `.docx` files. By default the export includes the latest response and
its question; select **Include full conversation** to export every completed
question/answer in order, including follow-ups. Unsent prompts and pending
attachments are excluded. **New conversation** clears the exportable history.

Both formats include headings, paragraph/list layout, model/provider identity,
source mode, citation URLs, recorded warnings, and a review disclaimer. PDF text
wraps and paginates; Word uses editable paragraphs and heading outline levels.
This is a local document export: it does not make another model request or
reverify IRS sources, and is separate from the verified tax-analysis workpaper.
Exports may include private taxpayer details and extracted attachment text from
sent messages. Store and share them securely. Conversation history remains
in memory only, so export before closing the app.

## Providers

Training and inference can run concurrently in the same desktop window.
Start either job first, then use **Model Trainer** or **Model Inference** for
the other. Training settings are locked until that run finishes. **Cancel
training** and **Cancel request** cancel only their respective jobs. CPU/GPU
and memory are shared, so running both may be slower or fail for insufficient
memory; the application does not promise resource isolation. Trained inference
continues to use the activated adapter. A newly trained adapter must be
activated separately after training completes.

- **Trained** loads the active **Transformers / PEFT** base model and LoRA
  adapter selected in **5 Agent Model**, in a local Python subprocess. It
  receives the same IRS-cache excerpts, tax-assistant instructions, and
  conversation messages as the other providers. It does not use Ollama or
  HTTP. Missing adapters or unsupported active runtimes fail explicitly;
  there is no fallback to an untrained base model.
- **Local** posts to the configured OpenAI-compatible `/chat/completions`
  route and checks `/models`. It is loopback-only unless non-loopback exposure
  is explicitly enabled with HTTPS and bearer authentication.
- **Azure** posts to the configured Azure OpenAI deployment route using an
  API key read from an environment variable.

Configure a provider in desktop `appsettings.json` or service
`appsettings.json`. The desktop **Model Inference** tab and the service use
the provider explicitly selected by the caller. The service endpoints are:

- `GET /api/models/providers`
- `GET /api/models/offline`
- `POST /api/models/chat?providerId=Local` (or `Azure`)
- `POST /api/models/chat?providerId=Trained`

The desktop defaults to **Trained**. Activate a complete adapter with its
matching base model using **Transformers / PEFT** in **5 Agent Model**, then
choose **Trained** in **7 Model Inference** and check availability. The status
identifies both the base model and adapter path. The availability check
confirms adapter files; generation also verifies the installed Python
environment and cached base weights.

`ModelProviders:TrainedPythonExecutable` defaults to `python`, which searches
the existing private training environment first. Set it to the full Python
executable path to select another installed training environment. Trained
chat does not install dependencies or download weights: the matching base
model must already exist in the Hugging Face cache. It reloads the base and
adapter per request and may take time on CPU. Its default output limit is
512 tokens; use **Cancel request** to stop a running request without losing
previous conversation turns. The HTTP provider's 120-second timeout does
not apply to this subprocess.

Training JSONL files are used during training, not attached wholesale to
each conversation. **Trained** applies the saved adapter learned from your
training run; retrieval separately searches your locally cached IRS documents.
Connecting an adapter does not retrain it or certify its tax accuracy. Existing
checks still reject repetitive or numeric/non-language adapter output.

The public chat body follows `ModelChatApiRequest`: `messages` is required;
optional fields are `taxYear`, `scenarioIdentifier`, `maximumOutputTokens`,
`temperature`, and `seed` (which must be explicitly enabled by the provider's
`SupportsSeed` setting). The public API intentionally does not accept
client-supplied source text or verification flags. The application searches the current local corpus for bounded matching
excerpts from allowlisted HTTPS government sources, and supplies them to either
provider with the source URL and verification timestamp. Internal trusted
callers may still supply `ModelInferenceRequest.SourceContext`.

Cached excerpts are not revalidated live for each prompt and may be stale;
responses using them include this warning. When no relevant passage matches,
the response says no verified source context was supplied and is not verified
tax guidance.

In the desktop **Model Inference** tab, send another message after a response
to ask a follow-up. The app includes earlier user and assistant messages in
the next request and shows the conversation in the response area. Use **New
conversation** to clear that in-memory history and start over. Conversation
history is not persisted when the app closes. The **Agent Model** test prompt
is still a single-prompt diagnostic; use **Trained** in **Model Inference**
for retrieved evidence and continuing conversation.

## Images and document attachments (desktop)

In **7 Model Inference**, choose image options first, then select
**Add images/documents...** and select one or more files. The application
processes files locally and lists the results. Expand **Review selected
attachment extraction** to check OCR and observations before sending your
question. **Remove attachment** and **Clear attachments** affect pending
attachments; **Cancel extraction** cancels work in progress. Already processed
files are retained if a later file fails or extraction is cancelled.

Supported attachments:

- PNG, JPEG, BMP, TIFF: Windows OCR, optional face detection, and optional
  local object/scene descriptions. Only the first frame of multi-frame
  images is analyzed, with an explicit note. Images are limited to 40
  megapixels and resized to a maximum 2,000-pixel edge for analysis.
- PDF: embedded text extraction with Windows OCR for pages without text.
  Image recognition options do not apply to PDF pages.
- DOCX: document-body text, including tables; embedded images are not analyzed.
- XLSX: used worksheet cells; embedded images are not analyzed.
- TXT and CSV: text extraction. Legacy DOC/XLS and password-protected documents
  are not supported.

Face analysis detects/counts possible faces and reports bounding boxes on the
resized image. It does **not identify people**, compare identities, or infer
sensitive traits. Detection and OCR can be inaccurate; check the original
document. A Windows OCR language pack is required.

Object recognition is an optional separate local vision step, not a capability
of the trained Qwen text model. Enable **Describe objects/scenes using local
vision model** before adding an image. The desktop configuration uses the
locally installed `gemma4:26b` by default:

```json
"LocalVisionBaseAddress": "http://127.0.0.1:11434/",
"LocalVisionModelId": "gemma4:26b"
```

These settings live under `ModelProviders`. The endpoint must be loopback
and the model must already be installed in Ollama and declare vision support.
There is no automatic download or fallback. Vision uses local HTTP between
programs on the same laptop and can take several minutes, especially with this
large model during fine-tuning. Leave object recognition off for OCR-only work.
Failures appear in the attachment status rather than silently dropping the
requested visual analysis.

Limits: eight attachments per message, 20 MB per file, 30 PDF pages,
20,000 extracted characters per file and 40,000 characters of combined
attachment context. Oversized inputs fail explicitly rather than silently
truncating them. The model's own conversation/context-window limits still
apply. Office documents also have expanded-archive safety limits.

Sending includes extracted text and machine observations as **untrusted
user-supplied data**, not verified IRS evidence, in the conversation sent to
your selected tax model. Raw file bytes are not sent to that tax model.
If you explicitly select **Azure** for chat, the extracted content is sent to
Azure; choose **Trained** or a loopback **Local** provider to keep the complete
workflow on the laptop. Attachment excerpts and responses remain available
to follow-up turns. Attachments are not added to training datasets or the IRS
cache. Pending attachments clear only after a successful send or an explicit
clear/new-conversation action; failures retain them for retry.
