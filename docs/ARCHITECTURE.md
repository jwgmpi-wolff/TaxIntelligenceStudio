# Architecture

```text
Native WPF Desktop (MVVM)      Optional localhost API
              |                         |
              +------ Application ------+
                        |
      +-----------------+------------------+
      |                 |                  |
 IRS crawler       Financial import     Report export
 robots/sitemap    PDF/CSV/XLSX/TXT     PDF/DOCX
      |                 |                  |
 Versioned SQLite   Tax mapping      Live citation gate
 corpus + audit     + opportunities  (no-cache HTTPS)
      |
 Grounded JSONL dataset
      |
 CUDA LoRA fine-tuning
 (NPU inventory for inference)
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

## Trust boundary

Only HTTPS content from the exact allowlisted government hosts is accepted.
Automatic redirects are disabled so an allowlisted URL cannot redirect a request
to an untrusted host. A successful response is hashed and timestamped. Failed
network validation produces no guidance.

## No-cache interpretation

The versioned local corpus is intentional product data used for dataset
generation. It is not trusted as current at report time. Each report performs
new no-cache requests for every cited IRS source and fails closed if verification
cannot be completed.
