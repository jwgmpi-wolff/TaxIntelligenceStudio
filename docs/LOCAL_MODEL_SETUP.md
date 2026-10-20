# Local and Azure Provider Setup

The desktop and service read `ModelProviders` from their application settings
and environment variables. Checked-in settings contain placeholders only.
Changing settings selects configuration; it does not download a model.

## Local endpoint

Run a local server that implements OpenAI-compatible `GET /v1/models` and
`POST /v1/chat/completions`, then set the local model alias in the applicable
`appsettings.json`:

```json
{
  "ModelProviders": {
    "DefaultProvider": "Local",
    "OfflineMode": false,
    "Local": {
      "Enabled": true,
      "BaseAddress": "http://127.0.0.1:11434/v1/",
      "ModelId": "your-installed-model-alias",
      "AllowNonLoopback": false,
      "RequestTimeoutSeconds": 120
    }
  }
}
```

The endpoint must be loopback by default. If non-loopback access is
intentionally configured, the endpoint must use HTTPS and the named
`ApiKeyEnvironmentVariable` must contain a bearer token. The application does
not start or stop the server.

The existing Agent Model tab runs PEFT inference through Python or creates an
Ollama model. It is not automatically registered as an OpenAI-compatible
server model; configure an endpoint and model alias the server actually serves.

## Azure OpenAI

Configure `Azure:Endpoint` (an HTTPS Azure OpenAI resource endpoint ending in
`.openai.azure.com`, `.openai.azure.us`, or `.openai.azure.cn`),
`DeploymentName`, and
`ApiVersion`. Set the API key in the current user's environment under the
configured `ApiKeyEnvironmentVariable` name (default:
`TAXINTELLIGENCE_AZURE_OPENAI_API_KEY`). Do not put the key in JSON, source
control, command-line arguments, or logs. The application cannot confirm model
quality or deployment identity beyond the configured deployment.

Selecting Azure sends prompts and supplied source context to that deployment.
The UI identifies this cloud behavior, and there is no automatic cloud/local
fallback.

## Offline mode

Set `ModelProviders:OfflineMode` to `true` in desktop/service configuration.
This blocks Azure inference, IRS crawling, and live source verification. Local
inference still needs an already-running local endpoint. The application
searches the local corpus and supplies bounded excerpts from current documents
on allowlisted government hosts. Cached passages can be stale; responses
clearly warn that they are not verified as current law.

## Installation and dependencies

The Windows installer prepares the repository's existing private Python
training environment. Provider inference itself uses .NET HTTP and does not
install Python packages. No model acquisition or offline wheelhouse workflow
is provided by the current application.
