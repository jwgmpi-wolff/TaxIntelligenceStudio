# Model Evaluation

The dataset exporter creates source-document-grouped train, validation, and
test splits. When a split manifest is present, local training verifies each
split's hash and record count, then records post-training validation/test loss
metrics in `evaluation.json`. These are operational checks against
synthetically transformed source passages, not validated held-out tax
scenarios.

The standard unit tests use synthetic fixtures and mocks; they do not measure
tax accuracy, unsupported-claim rate, citation quality, latency under load, or
Azure-versus-local quality. No benchmark results are claimed by this
repository.

Do not treat an Azure output or a low test loss as tax ground truth. A separate
scenario evaluation set should be reviewed and approved, kept separate from
training and validation data, and grounded in deterministic calculations and
authoritative IRS publications. Protect taxpayer data in evaluation inputs and
reports.
