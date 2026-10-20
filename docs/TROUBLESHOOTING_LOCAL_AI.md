# Troubleshooting Local AI

## Local provider is unavailable

Check that the local server is running, implements `GET /v1/models` and
`POST /v1/chat/completions`, and is listening at the configured
`ModelProviders:Local:BaseAddress`. `ModelId` must match a model alias accepted
by that server. The default address is loopback; non-loopback use is rejected
unless explicitly enabled and secured with HTTPS and a bearer token.

## Azure provider is unavailable

Check the HTTPS endpoint, deployment name, API version, and the environment
variable named by `ApiKeyEnvironmentVariable`. The application does not print
the key or use an implicit fallback. Azure cannot be selected while
`OfflineMode` is enabled.

## No answer or missing citations

The provider service retrieves bounded matching documents from the local
corpus. If no passages match, check that documents are current, their source
URLs use an allowlisted HTTPS government host, and the question contains
distinctive topic terms. Responses without supplied context carry a warning
and are not verified tax guidance.

## Offline mode blocks a request

Confirm `ModelProviders:OfflineMode` in the configuration loaded by the
desktop/service. Offline mode blocks cloud inference, IRS crawling, and live
source verification. It can retrieve from the local cache; cached passages may
be stale, and the local model endpoint must already be running.

## Training fails

Use the Model Trainer progress/log text, confirm the selected Python
environment and accelerator, select a compatible model and JSONL dataset, and
allow CPU fallback only if intended. GPU memory, model licensing, model
availability, and model downloads remain the operator's responsibility.
