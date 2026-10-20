# Dataset Format and Limitations

The current dataset exporter reads current IRS knowledge documents and writes
one JSON object per line as UTF-8 without a byte-order mark:

```json
{"messages":[{"role":"system","content":"Answer only from the supplied authoritative IRS evidence and cite its source URL."},{"role":"user","content":"Provide the IRS source passage titled \"...\"."},{"role":"assistant","content":"...source passage...\n\nSource: https://www.irs.gov/..."}]}
```

There are no additional top-level metadata fields. Long source text is divided
into bounded chunks. Each output line has a citation-bearing assistant answer.
The current exporter supports a complete local-training export and optional
size-based or configured-count upload parts. These parts preserve source order
and balance record counts; they are not train/validation/test splits.

The exporter writes a validation report, a full-export JSON manifest, and
deterministic train/validation/test files under a sibling `*-splits` directory.
The ML split assignment is grouped by source URL so chunks from one document
cannot leak across splits. The split manifest records seed, source-group and
record counts, SHA-256 hashes, and exclusions. The separate upload-size parts
are not used as ML splits.

An `examples.jsonl` sidecar records stable example and source-group hashes,
source URL, chunk ordinal, split, duplicate status, and a synthetic-generation
label. The records transform source passages into prompt/answer pairs; they do
not contain validated scenario ground truth or fabricated tax facts. Duplicate
normalized examples are excluded from split files. A limited email/SSN-pattern
scanner excludes matches from split files, but it is not comprehensive PII
detection or a guarantee of de-identification. The full export is retained
unchanged for review and is not itself PII-filtered.

Review and validate exported records before fine-tuning; do not include
taxpayer records unless a separate approved sanitization process has been
applied. The trainer records post-training loss on held-out splits; review the
test split, loss outputs, and source-age acceptance policy as separate
operator responsibilities.

The export uses current corpus text for training-data generation. The corpus is
not evidence of current law at inference time; verify tax facts against
authoritative sources.
