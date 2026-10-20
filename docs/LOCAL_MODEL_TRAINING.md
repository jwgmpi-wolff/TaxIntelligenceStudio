# Local Model Training

## Recent fine-tuning datasets

In **Model Trainer**, expand **Recent fine-tuning datasets** beneath the training
status. Select an entry to see all queued dataset paths, the base model, submitted
time, recorded outcome, completion time and output folder. The log is saved locally
in the app's SQLite database and loaded again on startup. **Refresh dataset log**
reads the latest entries and compatible `run_environment.json` files in the selected
output folder and its immediate run subfolders. No dataset contents are copied.

Submitted entries record intent, including preflight failures; they do not prove
the files were consumed. If the app exits mid-run, the outcome may remain
unrecorded. Existing manifests show **outcome not checked**, not assumed success.
Older manifests without per-file dataset provenance cannot reconstruct the file
list. The log remains viewable while training or inference is active.

The Model Trainer tab uses the existing embedded Python PEFT/LoRA trainer. It
accepts a local Hugging Face-compatible model folder or model ID, one or more
JSONL datasets, an output directory, Python interpreter, epoch/batch/
learning-rate settings, optional CPU fallback, and an optional compatible
checkpoint. The application invokes Python with separate process arguments,
reports progress, captures output, and supports cancellation.

## Queueing dataset files

In **4 Model Trainer**, select **Add datasets...**. Use Ctrl-click or Shift-click
in the file picker to choose several JSONL files, then select **Open**. You can
use **Add datasets...** again to add files from another folder. The list shows
the queued paths and file count; selecting the same path again does not add
another copy. Use **Remove selected** or **Clear queue** before starting.

**Start LoRA fine-tuning** trains one adapter on all queued files in a single
run, not separate adapters or independent training jobs. This is suitable for
the `irs-training-part-*.jsonl` files. File order is retained; the trainer
combines their records before its normal training shuffle. Exact repeated
chat examples across files are excluded with counts in the progress log.
The queue is kept in memory until cleared or the app closes and cannot be
edited while training is running.

Missing, empty, or malformed input files stop the run explicitly. All selected
files contribute to the dataset fingerprint. Resume requires the same file
contents and ordering as the original run; adding/removing/reordering a
dataset requires a new run. Original single-file fingerprints remain compatible.
Per-file paths/hashes and effective record counts are recorded in
`run_environment.json`.

The trainer loads model code with `trust_remote_code=False`; CUDA is preferred,
then DirectML, then CPU only when explicitly allowed. The Windows installer
prepares a private training environment; see the main README for tested
versions and hardware constraints.

When a queued dataset has a companion `*-splits/manifest.json`, training
uses its non-empty `train.jsonl`, validation data is evaluated after training,
and the test split is independently evaluated after training. It does not
silently fall back to the unfiltered full export if a split manifest exists but
the training split is empty. This applies independently to every queued export.
Manifest hashes and held-out files are verified before training. Selecting a
raw export alongside overlapping held-out data, or combining exports whose
source-document assignments conflict across training/validation/test, fails
with a dataset-leakage error. Use one consistent source-grouped export/split
in that case. Do not queue validation/test files as training datasets.

Hand-selected JSONL parts without split metadata are combined as training
data only; this does not create held-out evaluation automatically. Prefer
selecting the full export with its companion split directory when held-out
evaluation is required, rather than its upload-size parts.

Run configuration contains base-model ID, training/validation/test and split
manifest hashes, seed, LoRA values, sequence length, and optimization settings.
The run writes environment/hardware data, evaluation metrics, and adapter
artifacts; the application verifies expected run and adapter files before
reporting success. Checkpoint resumption remains tied to identical data and
settings, including the supported legacy configuration.

Training produces PEFT adapters and resumable checkpoints. It may require
network access when a model is not already present locally. No model is
downloaded automatically by provider selection. Evaluation metrics on
synthetically transformed source passages do not establish tax-answer
accuracy. Use only a model and dataset you are authorized to use, and keep run
artifacts in a fresh private folder outside Git.
