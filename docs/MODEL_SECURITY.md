# Model Security and Privacy

- Keep local endpoints loopback-bound. Non-loopback configuration requires
  HTTPS, explicit enablement, and a bearer key provided via environment.
- Azure inference requires explicit provider selection and sends prompt/context
  to the configured cloud deployment. There is no implicit fallback.
- Azure API keys are loaded from the configured environment-variable name;
  never put secrets in appsettings files or command-line arguments.
- Inference and source-verification audit records contain metadata, not
  prompts, generated responses, or source text.
- Provider HTTP clients disable redirects and limit request/response sizes.
- Offline mode blocks cloud inference and application-controlled IRS network
  refresh/verification, but does not sandbox the local runtime or provide
  system-wide network isolation.
- Transformers loads with `trust_remote_code=False`. Treat model weights and
  downloaded metadata as untrusted and keep model artifacts outside Git.
- The current application does not implement model-download hash verification,
  license verification, artifact provenance manifests, merge/quantization
  validation, or model-registration integrity checks.

The optional service has no authentication or role-based authorization in the
current implementation. Do not expose it to an untrusted network. Use least
privilege and protect app settings, environment variables, SQLite, training
outputs, and exported data.
