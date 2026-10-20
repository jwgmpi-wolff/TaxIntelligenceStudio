# Offline Mode

Set `ModelProviders:OfflineMode` to `true` in the desktop or service
configuration. At the application service boundary this:

- refuses Azure/cloud inference even when explicitly requested;
- refuses IRS crawl/refresh operations; and
- prevents live authoritative-source verification HTTP calls.

Inference can retrieve bounded excerpts from the locally stored corpus in both
online and offline modes. Retrieval is limited to current documents whose
source URLs pass the exact HTTPS government-host allowlist, ranks textual
matches, and sends source URLs and the corpus verification timestamp with each
excerpt. Cached corpus text is never treated as current law: every response
using it carries a staleness warning, and offline answers are explicitly
identified as potentially outdated.

The local model provider is usable only when an already-running local
OpenAI-compatible server responds at the configured loopback endpoint. The
application does not download model files or start a runtime. In offline mode,
only cached authoritative-source context is accepted; inference with no
matching cached material remains possible but is explicitly warned as
ungrounded. Live guidance verification fails closed. Offline mode does not
enforce OS-level network isolation or control what the local runtime itself
does.
