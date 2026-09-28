import argparse
import json
from pathlib import Path


def parse_args():
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    infer = subparsers.add_parser("infer")
    infer.add_argument("--model", required=True)
    infer.add_argument("--adapter", required=True)
    infer.add_argument("--prompt", required=True)
    infer.add_argument("--device", choices=("cuda", "directml", "cpu"), required=True)

    merge = subparsers.add_parser("merge")
    merge.add_argument("--model", required=True)
    merge.add_argument("--adapter", required=True)
    merge.add_argument("--output", required=True)
    return parser.parse_args()


def load_dependencies():
    try:
        import torch
        from peft import PeftModel
        from transformers import AutoModelForCausalLM, AutoTokenizer
    except ImportError as exc:
        raise SystemExit(
            "Missing agent runtime packages. Install torch, transformers, and "
            f"peft in the selected Python environment: {exc}"
        )
    return torch, PeftModel, AutoModelForCausalLM, AutoTokenizer


def infer(args):
    torch, PeftModel, AutoModelForCausalLM, AutoTokenizer = load_dependencies()
    print("Loading tokenizer and base model...", flush=True)
    tokenizer = AutoTokenizer.from_pretrained(args.model, trust_remote_code=False)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    if args.device == "directml":
        import torch_directml

        device = torch_directml.device()
        dtype = torch.float16
    elif args.device == "cuda":
        if not torch.cuda.is_available():
            raise SystemExit("CUDA was selected but is unavailable.")
        device = torch.device("cuda")
        dtype = torch.float16
    else:
        device = torch.device("cpu")
        dtype = torch.float32

    base_model = AutoModelForCausalLM.from_pretrained(
        args.model, torch_dtype=dtype, trust_remote_code=False
    )
    model = PeftModel.from_pretrained(base_model, args.adapter)
    model.to(device)
    model.eval()
    inputs = tokenizer(args.prompt, return_tensors="pt")
    inputs = {key: value.to(device) for key, value in inputs.items()}
    print(f"Generating through {args.device}...", flush=True)
    with torch.no_grad():
        output = model.generate(
            **inputs,
            max_new_tokens=256,
            do_sample=False,
            pad_token_id=tokenizer.pad_token_id,
        )
    generated = output[0][inputs["input_ids"].shape[1] :]
    answer = tokenizer.decode(generated, skip_special_tokens=True)
    print("TIS_AGENT_RESULT|" + json.dumps(answer), flush=True)


def merge(args):
    torch, PeftModel, AutoModelForCausalLM, AutoTokenizer = load_dependencies()
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    print("Loading base model on CPU for merge...", flush=True)
    tokenizer = AutoTokenizer.from_pretrained(args.model, trust_remote_code=False)
    base_model = AutoModelForCausalLM.from_pretrained(
        args.model, torch_dtype=torch.float32, trust_remote_code=False
    )
    print("Applying and merging LoRA adapter...", flush=True)
    merged_model = PeftModel.from_pretrained(base_model, args.adapter).merge_and_unload()
    merged_model.save_pretrained(output, safe_serialization=True)
    tokenizer.save_pretrained(output)
    print(f"Merged model saved to {output}", flush=True)


def main():
    args = parse_args()
    if args.command == "infer":
        infer(args)
    else:
        merge(args)


if __name__ == "__main__":
    main()
