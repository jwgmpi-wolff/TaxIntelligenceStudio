import json
import tempfile
from pathlib import Path


def test_gpu():
    import platform

    try:
        import torch
        import torch_directml
    except ImportError as exc:
        machine = platform.machine().lower()
        if machine not in ("amd64", "x86_64"):
            return False, (
                f"This Python is {platform.machine()} and torch-directml "
                "publishes x64 wheels only; select an x64 Python interpreter, "
                "which runs under emulation on Windows on Arm"
            )
        raise

    if not torch_directml.is_available():
        return False, "DirectML unavailable"
    index = torch_directml.default_device()
    device = torch_directml.device(index)
    base_weight = torch.randn((32, 32), device=device)
    lora_a = torch.nn.Parameter(torch.randn((32, 4), device=device) * 0.01)
    lora_b = torch.nn.Parameter(torch.zeros((4, 32), device=device))
    optimizer = torch.optim.SGD([lora_a, lora_b], lr=1e-2)
    inputs = torch.randn((8, 32), device=device)
    targets = torch.randn((8, 32), device=device)
    predictions = inputs @ (base_weight + lora_a @ lora_b)
    loss = ((predictions - targets) ** 2).mean()
    loss.backward()
    optimizer.step()
    train_loss = float(loss.detach().cpu())
    if not train_loss > 0:
        raise RuntimeError(f"DirectML LoRA training produced invalid loss {train_loss}.")
    device_name = torch_directml.device_name(index).replace("\x00", "")
    return True, f"{device_name}; one-step LoRA train_loss={train_loss:.6f}"


def test_npu():
    import numpy as np
    import onnx
    import onnxruntime as ort
    import onnxruntime_qnn
    from onnx import TensorProto, helper

    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [1, 4])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [1, 4])
    z = helper.make_tensor_value_info("z", TensorProto.FLOAT, [1, 4])
    graph = helper.make_graph(
        [helper.make_node("Add", ["x", "y"], ["z"])],
        "tax-intelligence-npu-probe",
        [x, y],
        [z],
    )
    model = helper.make_model(
        graph, opset_imports=[helper.make_opsetid("", 19)]
    )
    model.ir_version = 9
    model_path = Path(tempfile.gettempdir()) / "tax-intelligence-npu-probe.onnx"
    onnx.save(model, model_path)

    ort.register_execution_provider_library(
        "QNNExecutionProvider", onnxruntime_qnn.get_library_path()
    )
    devices = ort.get_ep_devices()
    npu_devices = [
        device
        for device in devices
        if device.ep_name == "QNNExecutionProvider"
        and "NPU" in str(device.device.type)
    ]
    if not npu_devices:
        return False, "QNN NPU device unavailable"

    options = ort.SessionOptions()
    options.add_provider_for_devices(npu_devices, {"backend_type": "htp"})
    options.enable_profiling = True
    session = ort.InferenceSession(str(model_path), sess_options=options)
    output = session.run(
        None,
        {
            "x": np.ones((1, 4), dtype=np.float32),
            "y": np.ones((1, 4), dtype=np.float32),
        },
    )[0]
    profile_path = Path(session.end_profiling())
    events = json.loads(profile_path.read_text(encoding="utf-8"))
    profile_path.unlink(missing_ok=True)
    model_path.unlink(missing_ok=True)
    npu_nodes = sum(
        1
        for event in events
        if event.get("cat") == "Node"
        and event.get("args", {}).get("provider") == "QNNExecutionProvider"
    )
    verified = bool(np.allclose(output, np.full((1, 4), 2.0))) and npu_nodes > 0
    return verified, f"QNNExecutionProvider ({npu_nodes} node on Hexagon NPU)"


def main():
    gpu_verified = False
    npu_verified = False
    gpu_device = "Unavailable"
    npu_provider = "Unavailable"
    details = []

    try:
        gpu_verified, gpu_device = test_gpu()
        details.append(
            "A real one-step LoRA update completed through PyTorch DirectML."
            if gpu_verified
            else f"GPU test did not verify: {gpu_device}."
        )
    except Exception as exc:
        details.append(f"GPU test failed: {exc}")

    try:
        npu_verified, npu_provider = test_npu()
        details.append(
            "ONNX graph executed through the Qualcomm QNN HTP provider."
            if npu_verified
            else f"NPU test did not verify: {npu_provider}."
        )
    except Exception as exc:
        details.append(f"NPU test failed: {exc}")

    print(
        json.dumps(
            {
                "gpuVerified": gpu_verified,
                "npuVerified": npu_verified,
                "gpuDevice": gpu_device,
                "npuProvider": npu_provider,
                "details": " ".join(details),
            }
        )
    )
    raise SystemExit(0 if gpu_verified or npu_verified else 1)


if __name__ == "__main__":
    main()
