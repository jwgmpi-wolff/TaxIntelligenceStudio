import argparse
import json
import sys
from pathlib import Path


def parse_args():
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    infer = subparsers.add_parser("infer")
    infer.add_argument("--model", required=True)
    infer.add_argument("--adapter", required=True)
    input_group = infer.add_mutually_exclusive_group(required=True)
    input_group.add_argument("--prompt")
    input_group.add_argument("--request-stdin", action="store_true")
    infer.add_argument("--local-files-only", action="store_true")
    infer.add_argument("--device", choices=("cuda", "directml", "cpu"), required=True)

    merge = subparsers.add_parser("merge")
    merge.add_argument("--model", required=True)
    merge.add_argument("--adapter", required=True)
    merge.add_argument("--output", required=True)
    return parser.parse_args()


def read_chat_request(stream):
    request = json.load(stream)
    messages = request.get("messages")
    if not isinstance(messages, list) or not messages or len(messages) > 257:
        raise ValueError("A bounded list of conversation messages is required.")
    for message in messages:
        if (
            not isinstance(message, dict)
            or message.get("role") not in ("system", "user", "assistant")
            or not isinstance(message.get("content"), str)
            or not message["content"].strip()
        ):
            raise ValueError("Each conversation message needs a supported role and content.")
    return request


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


def directml_probe_loss():
    request = json.loads(sys.stdin.read())
    torch, _, AutoModelForCausalLM, _ = load_dependencies()
    import torch_directml

    device = torch_directml.device()
    model = AutoModelForCausalLM.from_pretrained(
        request["model"], torch_dtype=torch.float32, trust_remote_code=False,
        local_files_only=request.get("local_files_only", False),
    ).to(device).eval()
    ids = torch.tensor([request["ids"]], device=device)
    with torch.no_grad():
        loss = model(input_ids=ids, labels=ids).loss.cpu().item()
    print(f"TIS_PROBE_LOSS|{loss}", flush=True)


def directml_matches_cpu(model_id, ids, cpu_loss, local_files_only=False):
    # Probe in a child process so DirectML's shared-memory allocations are released
    # if inference falls back to CPU.
    import subprocess

    result = subprocess.run(
        [sys.executable, str(Path(__file__).resolve()), "--directml-probe"],
        input=json.dumps({"model": model_id, "ids": ids, "local_files_only": local_files_only}),
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


def infer(args):
    request = read_chat_request(sys.stdin) if args.request_stdin else {
        "messages": [
            {
                "role": "system",
                "content": (
                    "Answer clearly and concisely using authoritative IRS evidence. "
                    "Do not invent tax rules or promise a refund."
                ),
            },
            {"role": "user", "content": args.prompt},
        ],
        "max_tokens": 256,
    }
    print("Loading local Python/PEFT runtime...", flush=True)
    torch, PeftModel, AutoModelForCausalLM, AutoTokenizer = load_dependencies()
    print("Loading tokenizer and base model...", flush=True)
    tokenizer = AutoTokenizer.from_pretrained(
        args.model, trust_remote_code=False, local_files_only=args.local_files_only
    )
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    if args.device == "directml":
        import torch_directml

        device = torch_directml.device()
        dtype = torch.float32
    elif args.device == "cuda":
        if not torch.cuda.is_available():
            raise SystemExit("CUDA was selected but is unavailable.")
        device = torch.device("cuda")
        dtype = torch.float16
    else:
        device = torch.device("cpu")
        dtype = torch.float32

    base_model = AutoModelForCausalLM.from_pretrained(
        args.model, torch_dtype=dtype, trust_remote_code=False,
        local_files_only=args.local_files_only,
    )
    print(f"Applying trained adapter: {args.adapter}", flush=True)
    model = PeftModel.from_pretrained(
        base_model, args.adapter, local_files_only=args.local_files_only
    )
    model.eval()
    messages = request["messages"]
    if hasattr(tokenizer, "apply_chat_template") and tokenizer.chat_template:
        prompt = tokenizer.apply_chat_template(
            messages, tokenize=False, add_generation_prompt=True
        )
    else:
        prompt = "\n".join(
            f"{message['role']}: {message['content']}" for message in messages
        ) + "\nassistant:"
    if args.device == "directml":
        # Short probes can pass on faulty drivers; errors surface around 128+ tokens.
        probe_text = prompt + " " + (
            "The Internal Revenue Service administers and enforces federal tax law. " * 12
        )
        probe = tokenizer(probe_text, return_tensors="pt")["input_ids"][:, -256:]
        with torch.no_grad(), model.disable_adapter():
            cpu_loss = model(input_ids=probe, labels=probe).loss.item()
        matches, dml_loss = directml_matches_cpu(
            args.model, probe[0].tolist(), cpu_loss, args.local_files_only
        )
        if not matches:
            observed = "no result" if dml_loss is None else f"loss {dml_loss:.4f}"
            print(
                f"DirectML returned {observed} but CPU returned {cpu_loss:.4f}; "
                "falling back to CPU inference.",
                flush=True,
            )
            device = torch.device("cpu")
            args.device = "cpu"
    model.to(device)
    inputs = tokenizer(prompt, return_tensors="pt")
    inputs = {key: value.to(device) for key, value in inputs.items()}
    maximum_output_tokens = request.get("max_tokens", 512)
    context_length = getattr(model.config, "max_position_embeddings", None)
    if context_length and inputs["input_ids"].shape[1] + maximum_output_tokens > context_length:
        raise ValueError(
            "Conversation and IRS evidence exceed the model context window. Start a new conversation."
        )
    if request.get("seed") is not None:
        torch.manual_seed(request["seed"])
    temperature = request.get("temperature", 0)
    sampling_options = {"temperature": temperature} if temperature > 0 else {}
    print(f"Generating through {args.device}...", flush=True)
    with torch.no_grad():
        output = model.generate(
            **inputs,
            max_new_tokens=maximum_output_tokens,
            do_sample=temperature > 0,
            **sampling_options,
            repetition_penalty=1.1,
            no_repeat_ngram_size=4,
            pad_token_id=tokenizer.pad_token_id,
            eos_token_id=tokenizer.eos_token_id,
        )
    generated = output[0][inputs["input_ids"].shape[1] :]
    generated_ids = generated.tolist()
    if len(generated_ids) >= 16:
        most_common_count = max(
            generated_ids.count(token_id) for token_id in set(generated_ids)
        )
        if most_common_count / len(generated_ids) >= 0.8:
            raise RuntimeError(
                "The adapter produced a repetitive token loop. Treat this adapter "
                "as collapsed and start a new training run instead of resuming it."
            )
    answer = tokenizer.decode(generated, skip_special_tokens=True)
    if not answer.strip():
        raise RuntimeError("The adapter generated an empty response.")
    visible_characters = [character for character in answer if not character.isspace()]
    letter_count = sum(character.isalpha() for character in visible_characters)
    digit_count = sum(character.isdigit() for character in visible_characters)
    if (
        len(visible_characters) >= 16
        and (letter_count < 8 or digit_count / len(visible_characters) >= 0.8)
    ):
        raise RuntimeError(
            "The adapter generated numeric or non-language output. Its training "
            "weights have diverged; start a new FP32 training run and do not resume "
            "this adapter or its checkpoints."
        )
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
    if len(sys.argv) > 1 and sys.argv[1] == "--directml-probe":
        directml_probe_loss()
        return
    args = parse_args()
    if args.command == "infer":
        infer(args)
    else:
        merge(args)


if __name__ == "__main__":
    main()
