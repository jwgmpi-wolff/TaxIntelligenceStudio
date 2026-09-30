import argparse
import hashlib
import json
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


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--dataset", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--device", choices=("cuda", "directml", "cpu"), required=True)
    parser.add_argument("--epochs", type=int, required=True)
    parser.add_argument("--batch-size", type=int, required=True)
    parser.add_argument("--learning-rate", type=float, required=True)
    parser.add_argument("--resume-checkpoint")
    return parser.parse_args()


def main():
    args = parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    max_sequence_length = 512 if args.device == "directml" else 2048
    run_config_path = output / "training_run.json"
    run_config = {
        "trainer_version": 2,
        "model": args.model,
        "dataset_sha256": hashlib.sha256(
            Path(args.dataset).read_bytes()
        ).hexdigest(),
        "epochs": args.epochs,
        "batch_size": args.batch_size,
        "learning_rate": args.learning_rate,
        "max_sequence_length": max_sequence_length,
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
        if saved_config != run_config:
            raise SystemExit(
                "The selected checkpoint was created with a different base model, "
                "dataset SHA-256, epochs, batch size, learning rate, sequence length, "
                "or trainer version. Restore the original settings or start new training."
            )
    run_config_path.write_text(
        json.dumps(run_config, indent=2), encoding="utf-8"
    )

    report_progress("environment", 1, "Checking training dependencies and environment")
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

    records = []
    report_progress("dataset", 3, "Loading JSONL training records")
    with open(args.dataset, "r", encoding="utf-8") as stream:
        for line in stream:
            if line.strip():
                records.append(json.loads(line))
    if not records:
        raise SystemExit("The dataset contains no JSONL records.")

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
    dtype = torch.float16 if args.device in ("cuda", "directml") else torch.float32
    report_progress("model", 16, f"Loading base model on {args.device}")
    model = AutoModelForCausalLM.from_pretrained(
        args.model, torch_dtype=dtype, trust_remote_code=False
    )
    model.config.use_cache = False
    model = get_peft_model(
        model,
        LoraConfig(
            r=16,
            lora_alpha=32,
            lora_dropout=0.05,
            task_type="CAUSAL_LM",
        ),
    )
    if args.device == "directml":
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
    report_progress("saving", 97, "Saving LoRA adapter")
    model.save_pretrained(
        output / "adapter",
        state_dict=copy_to_cpu(model.state_dict()),
    )
    tokenizer.save_pretrained(output / "adapter")
    report_progress("complete", 100, "Training complete")


if __name__ == "__main__":
    try:
        main()
    except BaseException as exc:
        if not isinstance(exc, KeyboardInterrupt):
            report_progress("failed", current_percent, f"Training failed: {exc}")
            traceback.print_exc(file=sys.stderr)
        raise
