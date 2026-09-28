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
- Perform threat modeling, dependency scanning, penetration testing, code
  signing, and installer signing.

Do not represent the current foundation as production-certified or compliant
until these controls are implemented and independently assessed.
