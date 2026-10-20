# Architecture

```text
Native WPF Desktop (MVVM)       Optional localhost API
              |                          |
              +-------- Application -----+
                            |
             Shared inference request/response
                     /              \
          Azure OpenAI         Local OpenAI-compatible
          HTTPS deployment    loopback endpoint by default
                     \              /
                       Audit metadata
                            |
   IRS corpus / JSONL / local LoRA training / PEFT or Ollama
```

## Projects

| Project | Responsibility |
| --- | --- |
| `TaxIntelligenceStudio.Domain` | Source, evidence, guidance, and audit models |
| `TaxIntelligenceStudio.Application` | Use cases and infrastructure contracts |
| `TaxIntelligenceStudio.Infrastructure` | IRS ingestion, document parsing, tax mapping, reporting, and SQLite |
| `TaxIntelligenceStudio.Service` | Optional localhost process and API endpoints |
| `TaxIntelligenceStudio.Desktop` | Native WPF user experience |
| `TaxIntelligenceStudio.UnitTests` | Compliance and behavior checks |

## Model providers

`IModelInferenceProvider` is the shared request contract. `ModelProviderResolver`
selects exactly one configured provider and reports availability without silently
falling back. Azure OpenAI credentials are read from the configured environment
variable; the local provider uses an OpenAI-compatible `/v1` endpoint and checks
the `/models` health route. Redirects are disabled for both HTTP clients.

The desktop provides a provider selector and the service exposes
`GET /api/models/providers`, `GET /api/models/offline`, and
`POST /api/models/chat`. The shared internal request supports verified source
context and responses carry those citations/usage when supplied by a trusted
application caller. The public chat API deliberately does not accept source
text or client-asserted verification flags. The current repository does not
include a retrieval service that selects corpus passages, nor a configured
Azure deployment.

Offline mode blocks cloud inference, IRS crawling, and live source verification.
It does not implement cache-only guidance retrieval; live verification reports
unavailable and fails closed. Local inference still requires a configured local
runtime. The desktop's existing PEFT/Ollama activation and the provider's
OpenAI-compatible endpoint are separate mechanisms.

## Trust boundary

Only HTTPS content from the exact allowlisted government hosts is accepted.
Automatic redirects are disabled so an allowlisted URL cannot redirect a request
to an untrusted host. A successful response is hashed and timestamped. Failed
network validation produces no guidance. Model-provider HTTP clients also disable
redirects, to avoid forwarding prompts to an unconfigured redirect target.

## No-cache interpretation

The versioned local corpus is intentional product data used for dataset
generation. It is not trusted as current at report time. Each report performs
new no-cache requests for every cited IRS source and fails closed if verification
cannot be completed. Offline mode disables this check; it does not substitute
cached text as current evidence.
