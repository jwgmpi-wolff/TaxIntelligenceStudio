import argparse
from datetime import datetime, timezone
import hashlib
import json
import platform
import shutil
import sys
import traceback
from collections.abc import Mapping
from pathlib import Path


current_percent = 0.0


def report_progress(stage, percent, message):
    global current_percent
    current_percent = percent
    event = {"stage": stage, "percent": round(percent, 1), "message": message}
    print(f"TIS_EVENT|{json.dumps(event, separators=(',', ':'))}", flush=True)


# DirectML (torch-directml 0.2.5) silently corrupts gathers whose indices exceed
# 16 bits (embedding, index_select, cross_entropy) and tensors larger than ~2^26
# elements. Qwen's 151,936-token vocabulary hits both, so the embedding lookup is
# split into 32K-row blocks and the lm_head + loss run as a fused, chunked kernel
# built from ops verified to match CPU.
DIRECTML_EMBEDDING_BLOCK = 32768
DIRECTML_LOSS_ROWS = 64


def install_directml_safe_embedding(model):
    import torch.nn.functional as F

    embedding = model.get_input_embeddings()

    def forward(input_ids):
        weight = embedding.weight
        output = None
        for start in range(0, weight.size(0), DIRECTML_EMBEDDING_BLOCK):
            rows = min(DIRECTML_EMBEDDING_BLOCK, weight.size(0) - start)
            local = input_ids - start
            inside = ((local >= 0) & (local < rows)).unsqueeze(-1).to(weight.dtype)
            part = F.embedding(local.clamp(0, rows - 1), weight[start:start + rows]) * inside
            output = part if output is None else output + part
        return output

    embedding.forward = forward


def make_chunked_linear_cross_entropy():
    import torch

    class ChunkedLinearCrossEntropy(torch.autograd.Function):
        @staticmethod
        def forward(ctx, hidden, weight, labels):
            vocab = torch.arange(weight.size(0), device=hidden.device)
            valid = (labels != -100).to(hidden.dtype)
            count = valid.sum().clamp_min(1)
            total = torch.zeros((), device=hidden.device, dtype=hidden.dtype)
            gradients = []
            for start in range(0, hidden.size(0), DIRECTML_LOSS_ROWS):
                rows = hidden[start:start + DIRECTML_LOSS_ROWS]
                targets = labels[start:start + DIRECTML_LOSS_ROWS]
                weights = valid[start:start + DIRECTML_LOSS_ROWS]
                logits = rows @ weight.t()
                hit = (vocab.unsqueeze(0) == targets.unsqueeze(1)).to(logits.dtype)
                token_loss = torch.logsumexp(logits, -1) - (logits * hit).sum(-1)
                total = total + (token_loss * weights).sum()
                scale = (weights / count).unsqueeze(1)
                gradients.append(((torch.softmax(logits, -1) - hit) * scale) @ weight)
            ctx.save_for_backward(torch.cat(gradients, 0))
            return total / count

        @staticmethod
        def backward(ctx, grad_output):
            (grad_hidden,) = ctx.saved_tensors
            return grad_hidden * grad_output, None, None

    return ChunkedLinearCrossEntropy


def directml_causal_lm_loss(model, loss_function, input_ids, attention_mask, labels):
    causal_lm = model.get_base_model() if hasattr(model, "get_base_model") else model
    hidden = causal_lm.model(
        input_ids=input_ids, attention_mask=attention_mask, use_cache=False
    ).last_hidden_state
    hidden = hidden[:, :-1, :].reshape(-1, hidden.size(-1))
    targets = labels[:, 1:].reshape(-1)
    return loss_function.apply(hidden, causal_lm.lm_head.weight.detach(), targets)


def directml_probe_loss():
    request = json.loads(sys.stdin.read())
    import torch
    import torch_directml
    from transformers import AutoModelForCausalLM

    device = torch_directml.device()
    model = AutoModelForCausalLM.from_pretrained(
        request["model"], torch_dtype=torch.float32, trust_remote_code=False
    ).to(device).eval()
    install_directml_safe_embedding(model)
    ids = torch.tensor([request["ids"]], device=device)
    with torch.no_grad():
        loss = directml_causal_lm_loss(
            model,
            make_chunked_linear_cross_entropy(),
            ids,
            torch.ones_like(ids),
            ids,
        ).cpu().item()
    print(f"TIS_PROBE_LOSS|{loss}", flush=True)


def directml_matches_cpu(model_id, ids, cpu_loss):
    # Probe in a child process so DirectML's shared-memory allocations are released
    # if the run falls back to CPU.
    import subprocess

    result = subprocess.run(
        [sys.executable, str(Path(__file__).resolve()), "--directml-probe"],
        input=json.dumps({"model": model_id, "ids": ids}),
        capture_output=True,
        text=True,
    )
    line = next(
        (item for item in result.stdout.splitlines() if item.startswith("TIS_PROBE_LOSS|")),
        None,
    )
    if line is None:
        return False, None
    dml_loss = float(line.split("|", 1)[1])
    return abs(dml_loss - cpu_loss) <= 0.05 * max(cpu_loss, 1.0), dml_loss


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--dataset", required=True, action="append")
    parser.add_argument("--output", required=True)
    parser.add_argument("--device", choices=("cuda", "directml", "cpu"), required=True)
    parser.add_argument("--epochs", type=int, required=True)
    parser.add_argument("--batch-size", type=int, required=True)
    parser.add_argument("--learning-rate", type=float, required=True)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--lora-rank", type=int, default=16)
    parser.add_argument("--lora-alpha", type=int, default=32)
    parser.add_argument("--lora-dropout", type=float, default=0.05)
    parser.add_argument("--max-sequence-length", type=int)
    parser.add_argument("--validation-dataset", action="append")
    parser.add_argument("--test-dataset", action="append")
    parser.add_argument("--split-manifest", action="append")
    parser.add_argument("--resume-checkpoint")
    return parser.parse_args()


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify_split_manifest(manifest_path, configured_paths):
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise SystemExit(f"The dataset split manifest is unreadable: {exc}") from exc
    if manifest.get("groupingKey") != "allowlisted_source_url":
        raise SystemExit("The split manifest does not declare source-document grouping.")

    verified_paths = {}
    for key, configured_path in configured_paths.items():
        item = manifest.get(key)
        if not isinstance(item, dict) or not isinstance(item.get("file"), str):
            raise SystemExit(f"The split manifest is missing its {key} file metadata.")
        file_name = item["file"]
        if Path(file_name).name != file_name:
            raise SystemExit(f"The split manifest contains an unsafe {key} file name.")
        expected_path = (manifest_path.parent / file_name).resolve()
        if configured_path is not None and configured_path.resolve() != expected_path:
            raise SystemExit(f"The configured {key} dataset does not match the split manifest.")
        if not expected_path.is_file():
            raise SystemExit(f"The split manifest {key} dataset is missing: {expected_path}")
        if expected_path.stat().st_size != item.get("bytes"):
            raise SystemExit(f"The split manifest {key} dataset byte count does not match.")
        expected_hash = item.get("sha256")
        if not isinstance(expected_hash, str) or sha256_file(expected_path).casefold() != expected_hash.casefold():
            raise SystemExit(f"The split manifest {key} dataset SHA-256 does not match.")
        with expected_path.open(encoding="utf-8-sig") as stream:
            record_count = sum(1 for line in stream if line.strip())
        if record_count != item.get("recordCount"):
            raise SystemExit(f"The split manifest {key} record count does not match.")
        verified_paths[key] = expected_path
    return verified_paths


def read_records(path, description):
    records = []
    with open(path, "r", encoding="utf-8-sig") as stream:
        for line_number, line in enumerate(stream, 1):
            if not line.strip():
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError as exc:
                raise SystemExit(
                    f"{description} contains invalid JSON on line {line_number}: {exc}"
                ) from exc
            if not isinstance(record, dict) or not isinstance(record.get("messages"), list):
                raise SystemExit(
                    f"{description} contains a record without a chat messages array "
                    f"on line {line_number}."
                )
            messages = record["messages"]
            if not messages or any(
                not isinstance(message, dict)
                or message.get("role") not in ("system", "user", "assistant")
                or not isinstance(message.get("content"), str)
                or not message["content"].strip()
                for message in messages
            ):
                raise SystemExit(
                    f"{description} has invalid chat messages on line {line_number}."
                )
            records.append(record)
    return records


def resolve_dataset_inputs(training, validation=None, test=None, manifests=None):
    def unique_paths(values):
        return list(dict.fromkeys(Path(value).resolve() for value in values or []))

    paths = {
        "training": unique_paths(training),
        "validation": unique_paths(validation),
        "test": unique_paths(test),
        "manifests": unique_paths(manifests),
    }
    if not paths["training"]:
        raise SystemExit("At least one training dataset is required.")
    for manifest_path in paths["manifests"]:
        verified = verify_split_manifest(
            manifest_path, {"training": None, "validation": None, "test": None}
        )
        if verified["training"] not in paths["training"]:
            raise SystemExit(
                f"The split manifest training file was not selected: {manifest_path}"
            )
        for name in ("validation", "test"):
            if verified[name] not in paths[name]:
                paths[name].append(verified[name])
    roles = {}
    for name, files in paths.items():
        for path in files:
            if not path.is_file():
                raise SystemExit(f"The configured {name} file does not exist: {path}")
            if name != "manifests":
                if path in roles and roles[path] != name:
                    raise SystemExit(
                        f"Dataset leakage: {path} was selected for both {roles[path]} and {name}."
                    )
                roles[path] = name
    return paths


def dataset_fingerprint(paths):
    hashes = [sha256_file(path) for path in paths]
    if not hashes:
        return None
    if len(hashes) == 1:
        return hashes[0]
    return hashlib.sha256(
        json.dumps(hashes, separators=(",", ":")).encode("utf-8")
    ).hexdigest()


def load_record_sets(paths):
    result = {"training": [], "validation": [], "test": []}
    record_roles = {}
    record_files = {}
    source_roles = {}
    for name in result:
        for path in paths[name]:
            records = read_records(path, f"{name.title()} dataset '{path}'")
            if name == "training" and not records:
                raise SystemExit(f"The training dataset contains no JSONL records: {path}")
            duplicate_count = 0
            for record in records:
                messages = record["messages"]
                key = hashlib.sha256(
                    json.dumps(messages, sort_keys=True, ensure_ascii=False).encode("utf-8")
                ).hexdigest()
                prior_role = record_roles.get(key)
                if prior_role is not None and prior_role != name:
                    raise SystemExit(
                        f"Dataset leakage: the same example appears in {prior_role} and {name}: {path}"
                    )
                for message in messages:
                    if message["role"] == "assistant" and "\n\nSource: " in message["content"]:
                        source = message["content"].rsplit("\n\nSource: ", 1)[1].strip()
                        prior_source_role = source_roles.get(source)
                        if prior_source_role is not None and prior_source_role != name:
                            raise SystemExit(
                                f"Dataset leakage: source {source} appears in {prior_source_role} and {name}. "
                                "Use one consistent source-grouped split; do not mix raw exports with their held-out files."
                            )
                        source_roles[source] = name
                if prior_role is None or record_files[key] == path:
                    result[name].append(record)
                    record_roles[key] = name
                    record_files[key] = path
                else:
                    duplicate_count += 1
            report_progress(
                "dataset", 3,
                f"Loaded {name} file {path.name}: {len(records)} records; "
                f"{duplicate_count} repeated examples excluded.",
            )
    return result


def main():
    args = parse_args()
    requested_device = args.device
    if args.seed < 0:
        raise SystemExit("The random seed must be non-negative.")
    if args.lora_rank <= 0 or args.lora_alpha <= 0 or not 0 <= args.lora_dropout < 1:
        raise SystemExit("LoRA rank/alpha must be positive and dropout must be in [0, 1).")
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    max_sequence_length = args.max_sequence_length
    if max_sequence_length is None:
        max_sequence_length = 512 if args.device == "directml" else 2048
    if max_sequence_length <= 0 or max_sequence_length > 32768:
        raise SystemExit("Maximum sequence length must be between 1 and 32768.")
    dataset_inputs = resolve_dataset_inputs(
        args.dataset, args.validation_dataset, args.test_dataset, args.split_manifest
    )
    run_config_path = output / "training_run.json"
    run_config = {
        "trainer_version": 4,
        "model": args.model,
        "dataset_sha256": dataset_fingerprint(dataset_inputs["training"]),
        "validation_dataset_sha256": dataset_fingerprint(dataset_inputs["validation"]),
        "test_dataset_sha256": dataset_fingerprint(dataset_inputs["test"]),
        "split_manifest_sha256": dataset_fingerprint(dataset_inputs["manifests"]),
        "epochs": args.epochs,
        "batch_size": args.batch_size,
        "learning_rate": args.learning_rate,
        "max_sequence_length": max_sequence_length,
        "seed": args.seed,
        "lora": {
            "rank": args.lora_rank,
            "alpha": args.lora_alpha,
            "dropout": args.lora_dropout,
        },
    }
    if args.resume_checkpoint:
        checkpoint = Path(args.resume_checkpoint).resolve()
        try:
            checkpoint.relative_to(output)
        except ValueError as exc:
            raise SystemExit(
                "The resume checkpoint must be inside the selected output directory."
            ) from exc
        checkpoint_config_path = checkpoint / "training_run.json"
        if not checkpoint_config_path.is_file():
            raise SystemExit(
                "The selected checkpoint has no compatibility manifest and cannot "
                "be resumed safely. Start new training instead."
            )
        saved_config = json.loads(checkpoint_config_path.read_text(encoding="utf-8"))
        legacy_run_config = {
            "trainer_version": 3,
            "model": args.model,
            "dataset_sha256": run_config["dataset_sha256"],
            "epochs": args.epochs,
            "batch_size": args.batch_size,
            "learning_rate": args.learning_rate,
            "max_sequence_length": max_sequence_length,
        }
        can_resume_legacy = (
            saved_config == legacy_run_config
            and len(dataset_inputs["training"]) == 1
            and not dataset_inputs["validation"]
            and not dataset_inputs["test"]
            and not dataset_inputs["manifests"]
            and args.seed == 42
            and args.lora_rank == 16
            and args.lora_alpha == 32
            and args.lora_dropout == 0.05
        )
        if saved_config != run_config and not can_resume_legacy:
            raise SystemExit(
                "The selected checkpoint was created with a different base model, "
                "dataset/split SHA-256, training settings, sequence length, "
                "or trainer version. Restore the original settings or start new training."
            )
    run_config_path.write_text(
        json.dumps(run_config, indent=2), encoding="utf-8"
    )

    report_progress("dataset", 3, f"Loading {len(dataset_inputs['training'])} queued JSONL training file(s)")
    record_sets = load_record_sets(dataset_inputs)
    records = record_sets["training"]
    validation_records = record_sets["validation"]
    test_records = record_sets["test"]

    report_progress("environment", 4, "Checking training dependencies and environment")
    try:
        import torch
        from datasets import Dataset
        from peft import LoraConfig, get_peft_model
        from transformers import (
            AutoModelForCausalLM,
            AutoTokenizer,
            DataCollatorForLanguageModeling,
            Trainer,
            TrainerCallback,
            TrainingArguments,
        )
    except ImportError as exc:
        raise SystemExit(
            "Missing trainer packages. Install torch, transformers, datasets, "
            f"accelerate, and peft in the selected Python environment: {exc}"
        )

    if args.device == "cuda" and not torch.cuda.is_available():
        raise SystemExit("CUDA was selected but PyTorch cannot access a CUDA GPU.")
    if args.device == "directml":
        try:
            import torch_directml
        except ImportError as exc:
            raise SystemExit(
                "DirectML GPU training was selected, but torch-directml is not "
                "installed. Install the tested Windows packages with: "
                "pip install torch==2.4.1 torchvision==0.19.1 "
                f"torch-directml==0.2.5.dev240914. Details: {exc}"
            )
        if not torch_directml.is_available():
            raise SystemExit(
                "torch-directml is installed but cannot access a DirectML GPU."
            )

    report_progress("dataset", 5, f"Loading tokenizer for {args.model}")
    tokenizer = AutoTokenizer.from_pretrained(args.model, trust_remote_code=False)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    def format_record(record):
        messages = record["messages"]
        if hasattr(tokenizer, "apply_chat_template") and tokenizer.chat_template:
            text = tokenizer.apply_chat_template(
                messages, tokenize=False, add_generation_prompt=False
            )
        else:
            text = "\n".join(
                f"{message['role']}: {message['content']}" for message in messages
            )
        return {"text": text}

    report_progress("dataset", 8, f"Formatting {len(records)} training records")
    dataset = Dataset.from_list(records).map(format_record)

    def tokenize(batch):
        return tokenizer(
            batch["text"],
            truncation=True,
            max_length=max_sequence_length,
            padding=False,
        )

    report_progress(
        "tokenization",
        12,
        f"Tokenizing training records (maximum {max_sequence_length} tokens)",
    )
    tokenized = dataset.map(tokenize, batched=True, remove_columns=dataset.column_names)

    def tokenize_evaluation(records, description):
        if not records:
            return None
        report_progress("dataset", 10, f"Formatting {len(records)} {description} records")
        evaluation_dataset = Dataset.from_list(records).map(format_record)
        return evaluation_dataset.map(
            tokenize,
            batched=True,
            remove_columns=evaluation_dataset.column_names,
        )

    validation_tokenized = tokenize_evaluation(validation_records, "validation")
    test_tokenized = tokenize_evaluation(test_records, "test")
    torch.manual_seed(args.seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(args.seed)
    dtype = torch.float16 if args.device == "cuda" else torch.float32
    report_progress("model", 16, f"Loading base model on {args.device}")
    model = AutoModelForCausalLM.from_pretrained(
        args.model, torch_dtype=dtype, trust_remote_code=False
    )
    model.config.use_cache = False
    if args.device == "directml":
        report_progress("model", 17, "Verifying DirectML numerical accuracy against CPU")
        sample_ids = list(tokenized[0]["input_ids"][:256])
        sample = torch.tensor([sample_ids])
        model.eval()
        with torch.no_grad():
            cpu_loss = model(input_ids=sample, labels=sample).loss.item()
        model.train()
        matches, dml_loss = directml_matches_cpu(args.model, sample_ids, cpu_loss)
        if not matches:
            observed = "no result" if dml_loss is None else f"loss {dml_loss:.4f}"
            report_progress(
                "model",
                17,
                f"DirectML returned {observed} but CPU returned {cpu_loss:.4f}; "
                "this GPU driver computes incorrect results for this model. "
                "Falling back to CPU training.",
            )
            args.device = "cpu"
    (output / "run_environment.json").write_text(
        json.dumps(
            {
                "python": sys.version,
                "python_executable": sys.executable,
                "platform": platform.platform(),
                "machine": platform.machine(),
                "torch": torch.__version__,
                "requested_device": requested_device,
                "selected_device": args.device,
                "cuda_available": torch.cuda.is_available(),
                "cuda_devices": (
                    [torch.cuda.get_device_name(index) for index in range(torch.cuda.device_count())]
                    if torch.cuda.is_available()
                    else []
                ),
                "directml_device": (
                    str(torch_directml.device())
                    if args.device == "directml"
                    else None
                ),
                "training_run": run_config,
                "dataset_files": {
                    name: [
                        {"path": str(path), "sha256": sha256_file(path)}
                        for path in files
                    ]
                    for name, files in dataset_inputs.items()
                },
                "dataset_record_counts": {
                    name: len(items) for name, items in record_sets.items()
                },
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    model = get_peft_model(
        model,
        LoraConfig(
            r=args.lora_rank,
            lora_alpha=args.lora_alpha,
            lora_dropout=args.lora_dropout,
            task_type="CAUSAL_LM",
        ),
    )
    directml_loss_function = make_chunked_linear_cross_entropy()
    if args.device == "directml":
        install_directml_safe_embedding(model)
        runtime_device = torch_directml.device()
    else:
        runtime_device = torch.device(args.device)
    model.to(runtime_device)

    def copy_to_cpu(value):
        if isinstance(value, torch.Tensor):
            return value.detach().cpu()
        if isinstance(value, Mapping):
            return type(value)(
                {key: copy_to_cpu(item) for key, item in value.items()}
            )
        if isinstance(value, (tuple, list)):
            return type(value)(copy_to_cpu(item) for item in value)
        return value

    # PyTorch AdamW uses lerp, which DirectML sends through a CPU fallback that
    # can remove the GPU device during sustained training.
    class DirectMLAdamW(torch.optim.Optimizer):
        def __init__(self, parameter_groups):
            super().__init__(parameter_groups, {})

        @torch.no_grad()
        def step(self, closure=None):
            loss = None
            if closure is not None:
                with torch.enable_grad():
                    loss = closure()

            for group in self.param_groups:
                beta1, beta2 = group["betas"]
                for parameter in group["params"]:
                    if parameter.grad is None:
                        continue
                    gradient = parameter.grad
                    if gradient.is_sparse:
                        raise RuntimeError("DirectML AdamW does not support sparse gradients.")
                    if group.get("maximize", False):
                        gradient = -gradient

                    state = self.state[parameter]
                    if not state:
                        state["step"] = 0
                        state["exp_avg"] = torch.zeros_like(parameter)
                        state["exp_avg_sq"] = torch.zeros_like(parameter)

                    saved_step = state["step"]
                    if isinstance(saved_step, torch.Tensor):
                        saved_step = int(saved_step.item())
                    step = saved_step + 1
                    state["step"] = step

                    if group["weight_decay"] != 0:
                        parameter.mul_(1 - group["lr"] * group["weight_decay"])

                    exp_avg = state["exp_avg"]
                    exp_avg_sq = state["exp_avg_sq"]
                    exp_avg.mul_(beta1).add_(gradient, alpha=1 - beta1)
                    exp_avg_sq.mul_(beta2).addcmul_(
                        gradient, gradient, value=1 - beta2
                    )

                    bias_correction1 = 1 - beta1**step
                    bias_correction2 = 1 - beta2**step
                    denominator = (
                        exp_avg_sq.sqrt()
                        .div_(bias_correction2**0.5)
                        .add_(group["eps"])
                    )
                    parameter.addcdiv_(
                        exp_avg,
                        denominator,
                        value=-(group["lr"] / bias_correction1),
                    )

            return loss

    class DirectMLTrainer(Trainer):
        def create_accelerator_and_postprocess(self):
            super().create_accelerator_and_postprocess()
            self.accelerator.device_placement = False

        def create_optimizer(self):
            if self.optimizer is None:
                default_optimizer = super().create_optimizer()
                self.optimizer = DirectMLAdamW(default_optimizer.param_groups)
            return self.optimizer

        def compute_loss(self, model, inputs, return_outputs=False, num_items_in_batch=None):
            loss = directml_causal_lm_loss(
                model,
                directml_loss_function,
                inputs["input_ids"],
                inputs.get("attention_mask"),
                inputs["labels"],
            )
            return (loss, None) if return_outputs else loss

        def _maybe_log_save_evaluate(self, tr_loss, *args, **kwargs):
            # DirectML computes the in-place self-subtraction `tr_loss -= tr_loss`
            # used by Trainer to reset the logged loss as 2x instead of 0, which makes
            # the reported loss double at every logging step.
            cpu_loss = tr_loss.detach().cpu()
            result = super()._maybe_log_save_evaluate(cpu_loss, *args, **kwargs)
            if cpu_loss.item() == 0.0:
                tr_loss.zero_()
            return result

        def _move_model_to_device(self, model, device):
            return

        def _prepare_input(self, data):
            if isinstance(data, Mapping):
                return type(data)(
                    {key: self._prepare_input(value) for key, value in data.items()}
                )
            if isinstance(data, (tuple, list)):
                return type(data)(self._prepare_input(value) for value in data)
            if isinstance(data, torch.Tensor):
                return data.to(runtime_device)
            return data

        def _save(self, output_dir=None, state_dict=None):
            cpu_state = copy_to_cpu(
                self.model.state_dict() if state_dict is None else state_dict
            )
            super()._save(output_dir, state_dict=cpu_state)

        def _save_optimizer_and_scheduler(self, output_dir):
            if self.args.should_save:
                torch.save(
                    copy_to_cpu(self.optimizer.state_dict()),
                    Path(output_dir) / "optimizer.pt",
                )
                torch.save(
                    copy_to_cpu(self.lr_scheduler.state_dict()),
                    Path(output_dir) / "scheduler.pt",
                )

    class ProgressCallback(TrainerCallback):
        def on_log(self, args, state, control, logs=None, **kwargs):
            loss = None if logs is None else logs.get("loss")
            if loss is not None and (
                not torch.isfinite(torch.tensor(loss)) or loss < 0 or loss > 100
            ):
                raise RuntimeError(
                    f"Training loss diverged to {loss}. The adapter was not saved. "
                    "Start a new run with stable settings instead of resuming this run."
                )

        def on_train_begin(self, args, state, control, **kwargs):
            if runtime_device.type == "privateuseone":
                model_device = next(kwargs["model"].parameters()).device
                if model_device.type != "privateuseone":
                    raise RuntimeError(
                        "DirectML model placement was lost; refusing silent CPU fallback."
                    )
            total = max(state.max_steps, 1)
            percent = 20 + (75 * state.global_step / total)
            report_progress(
                "training",
                percent,
                f"Training started: {state.global_step}/{state.max_steps} steps",
            )

        def on_step_end(self, args, state, control, **kwargs):
            total = max(state.max_steps, 1)
            percent = 20 + (75 * state.global_step / total)
            report_progress(
                "training",
                percent,
                f"Training step {state.global_step}/{state.max_steps}",
            )

        def on_save(self, args, state, control, **kwargs):
            checkpoint_config = output / f"checkpoint-{state.global_step}" / "training_run.json"
            if not checkpoint_config.parent.is_dir():
                raise RuntimeError(
                    f"Checkpoint {checkpoint_config.parent} was removed right after saving. "
                    "Use an empty output folder for new training runs."
                )
            shutil.copyfile(run_config_path, checkpoint_config)
            report_progress(
                "checkpoint",
                20 + (75 * state.global_step / max(state.max_steps, 1)),
                f"Saved checkpoint at step {state.global_step}",
            )

        def on_train_end(self, args, state, control, **kwargs):
            report_progress("training", 95, "Optimizer steps complete")

    training_arguments = TrainingArguments(
        output_dir=str(output),
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        learning_rate=args.learning_rate,
        seed=args.seed,
        data_seed=args.seed,
        prediction_loss_only=True,
        fp16=args.device == "cuda",
        gradient_checkpointing=args.device == "directml",
        gradient_checkpointing_kwargs={"use_reentrant": False},
        report_to=[],
        save_strategy="steps",
        save_steps=10,
        save_total_limit=10,
        logging_steps=10,
    )
    if args.device == "directml":
        training_arguments.__dict__["__cached__setup_devices"] = runtime_device

    trainer_class = DirectMLTrainer if args.device == "directml" else Trainer
    trainer = trainer_class(
        model=model,
        train_dataset=tokenized,
        data_collator=DataCollatorForLanguageModeling(tokenizer, mlm=False),
        args=training_arguments,
        callbacks=[ProgressCallback()],
    )
    if args.resume_checkpoint:
        report_progress("resume", 18, f"Restoring checkpoint {args.resume_checkpoint}")
    trainer.train(resume_from_checkpoint=args.resume_checkpoint)
    evaluation = {}
    if validation_tokenized is not None:
        report_progress("evaluation", 96, "Evaluating held-out validation split")
        evaluation["validation"] = trainer.evaluate(
            eval_dataset=validation_tokenized,
            metric_key_prefix="validation",
        )
    if test_tokenized is not None:
        report_progress("evaluation", 96, "Evaluating held-out test split")
        evaluation["test"] = trainer.evaluate(
            eval_dataset=test_tokenized,
            metric_key_prefix="test",
        )
    (output / "evaluation.json").write_text(
        json.dumps(
            {
                "status": "completed" if evaluation else "not_run_no_held_out_data",
                "metrics": evaluation,
                "validation_dataset_sha256": run_config["validation_dataset_sha256"],
                "test_dataset_sha256": run_config["test_dataset_sha256"],
                "split_manifest_sha256": run_config["split_manifest_sha256"],
                "labels_are_synthetic_source_passage_transformations": True,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    report_progress("saving", 97, "Saving LoRA adapter")
    model.save_pretrained(
        output / "adapter",
        state_dict=copy_to_cpu(model.state_dict()),
    )
    tokenizer.save_pretrained(output / "adapter")
    adapter_files = []
    for path in sorted((output / "adapter").rglob("*")):
        if path.is_file():
            adapter_files.append(
                {
                    "file": path.relative_to(output / "adapter").as_posix(),
                    "bytes": path.stat().st_size,
                    "sha256": sha256_file(path),
                }
            )
    (output / "artifact_manifest.json").write_text(
        json.dumps(
            {
                "created_at": datetime.now(timezone.utc).isoformat(),
                "base_model": args.model,
                "base_model_revision": getattr(model.config, "_commit_hash", None),
                "dataset_sha256": run_config["dataset_sha256"],
                "split_manifest_sha256": run_config["split_manifest_sha256"],
                "evaluation_sha256": sha256_file(output / "evaluation.json"),
                "adapter_files": adapter_files,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    report_progress("complete", 100, "Training complete")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--directml-probe":
        directml_probe_loss()
        sys.exit(0)
    try:
        main()
    except BaseException as exc:
        if not isinstance(exc, KeyboardInterrupt):
            report_progress("failed", current_percent, f"Training failed: {exc}")
            traceback.print_exc(file=sys.stderr)
        raise
