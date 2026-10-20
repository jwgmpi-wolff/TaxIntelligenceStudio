# Security Baseline

## Implemented controls

- Exact-host HTTPS allowlist for authoritative sources.
- Automatic redirects disabled to prevent allowlist bypass.
- Explicit HTTP no-cache/no-store directives.
- Thirty-second outbound request timeout.
- SHA-256 evidence and audit record fingerprints.
- Fail-closed guidance workflow.
- No API keys or credentials in source control.
- SQLite files excluded from source control.
- Local model endpoint defaults to loopback; non-loopback use requires explicit
  enablement, HTTPS, and a configured bearer token.
- Azure OpenAI keys are read from an environment variable, not JSON settings.
- Model-provider HTTP clients disable redirects and reject oversized requests
  and responses.
- Model inference and source-verification audit rows contain metadata only, not
  prompt, response, or taxpayer content.
- Offline mode blocks cloud inference, IRS refresh, and live source HTTP checks.
- Inference retrieves bounded cached passages only from exact allowlisted
  government HTTPS hosts and warns that cached content may be stale.

## Model and taxpayer-data risks

Taxpayer content is sent only to the provider explicitly selected in the UI or
API request. Selecting Azure transmits the request to the configured Azure
deployment; the application does not fall back between providers. The local
provider accepts only loopback endpoints by default. If network exposure is
deliberately enabled, the endpoint must use HTTPS and bearer authentication.

Prompts and model responses are not written to the audit log. Audit rows record
provider/model identifiers, counts, usage, elapsed time, and source mode only.
The application cannot prevent a separately operated local runtime from
logging requests or making its own outbound network calls; configure that
runtime for local-only operation and disable its prompt logging.

Model weights, adapters, caches, exported datasets, and training outputs must
remain outside source control. Training hashes its dataset splits and adapter
outputs, but the application does not verify base-model supply-chain
provenance, licenses, merge/quantization outputs, or register artifacts.
Transformers loading currently uses `trust_remote_code=False`. Avoid
pickle-based model artifacts.

Offline mode is an application-service policy. It blocks Azure selection,
corpus refresh, and live source verification, but can retrieve from the local
corpus. It does not enforce OS-level network isolation or stop other software
from accessing the network. Responses using cached sources are warned as
potentially outdated; no complete offline tax-advice assurance is claimed.

## Required before production

- Encrypt SQLite using an approved SQLCipher provider and manage its key with
  Windows DPAPI or Windows Credential Manager.
- Encrypt imported files with per-user keys and authenticated encryption.
- Add authenticated local IPC and role-based authorization.
- Add chained or externally anchored audit signatures; a standalone hash is not
  sufficient tamper protection.
- Add malware scanning, archive limits, parser sandboxing, and content-size
  limits for document ingestion.
- Pin supported AI endpoints, redact sensitive data, and require affirmative
  consent before external transmission.
- Add authenticated API/local IPC, explicit secure consent workflow, and
  application-managed local runtime lifecycle before exposing the service to
  other processes or users.
- Add reviewed freshness/coverage policy and validated tax scenarios before
  claiming complete offline tax guidance.
- Add artifact hash/provenance validation, license review, and model-supply-chain
  verification before enabling model downloads or registration.
- Perform threat modeling, dependency scanning, penetration testing, code
  signing, and installer signing.

Do not represent the current foundation as production-certified or compliant
until these controls are implemented and independently assessed.
