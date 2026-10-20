import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


TRAINER_PATH = (
    Path(__file__).resolve().parents[1]
    / "src"
    / "TaxIntelligenceStudio.Infrastructure"
    / "Training"
    / "local_lora_trainer.py"
)
SPEC = importlib.util.spec_from_file_location("local_lora_trainer", TRAINER_PATH)
TRAINER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRAINER)


class SplitManifestTests(unittest.TestCase):
    def test_manifest_hash_and_count_are_verified(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            payload = b'{"messages":[]}\n'
            files = {
                "training": root / "train.jsonl",
                "validation": root / "validation.jsonl",
                "test": root / "test.jsonl",
            }
            files["training"].write_bytes(payload)
            files["validation"].write_bytes(b"")
            files["test"].write_bytes(b"")
            items = {}
            for name, path in files.items():
                data = path.read_bytes()
                items[name] = {
                    "file": path.name,
                    "recordCount": data.count(b"\n"),
                    "bytes": len(data),
                    "sha256": hashlib.sha256(data).hexdigest().upper(),
                }
            manifest = root / "manifest.json"
            manifest.write_text(
                json.dumps(
                    {
                        "groupingKey": "allowlisted_source_url",
                        **items,
                    }
                ),
                encoding="utf-8",
            )

            result = TRAINER.verify_split_manifest(
                manifest,
                {
                    "training": files["training"],
                    "validation": None,
                    "test": None,
                },
            )

            self.assertEqual(files["training"].resolve(), result["training"])
            self.assertEqual(files["validation"].resolve(), result["validation"])
            self.assertEqual(files["test"].resolve(), result["test"])

    def test_manifest_rejects_tampered_split(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            paths = {
                "training": root / "train.jsonl",
                "validation": root / "validation.jsonl",
                "test": root / "test.jsonl",
            }
            for path in paths.values():
                path.write_bytes(b"")
            manifest_data = {
                "groupingKey": "allowlisted_source_url",
                **{
                    name: {
                        "file": path.name,
                        "recordCount": 0,
                        "bytes": 0,
                        "sha256": hashlib.sha256(b"").hexdigest(),
                    }
                    for name, path in paths.items()
                },
            }
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps(manifest_data), encoding="utf-8")
            paths["training"].write_text("tampered", encoding="utf-8")

            with self.assertRaises(SystemExit):
                TRAINER.verify_split_manifest(
                    manifest,
                    {"training": paths["training"], "validation": None, "test": None},
                )


class MultiDatasetTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def write_dataset(self, name, content, source=None):
        record = {
            "messages": [
                {"role": "user", "content": "Explain this evidence."},
                {
                    "role": "assistant",
                    "content": content + (f"\n\nSource: {source}" if source else ""),
                },
            ]
        }
        path = self.root / name
        path.write_text(json.dumps(record) + "\n", encoding="utf-8")
        return path

    def write_manifest(self, directory, files):
        directory.mkdir(exist_ok=True)
        manifest = directory / "manifest.json"
        manifest.write_text(
            json.dumps({
                "groupingKey": "allowlisted_source_url",
                **{
                    name: {
                        "file": path.name,
                        "recordCount": len(path.read_text(encoding="utf-8").splitlines()),
                        "bytes": path.stat().st_size,
                        "sha256": TRAINER.sha256_file(path),
                    }
                    for name, path in files.items()
                },
            }),
            encoding="utf-8",
        )
        return manifest

    def test_repeated_cli_arguments_preserve_all_selected_files(self):
        with patch("sys.argv", [
            "trainer.py", "--model", "base", "--dataset", "one.jsonl",
            "--dataset", "two.jsonl", "--output", "output", "--device", "cpu",
            "--epochs", "1", "--batch-size", "1", "--learning-rate", "0.00005",
        ]):
            args = TRAINER.parse_args()
        self.assertEqual(["one.jsonl", "two.jsonl"], args.dataset)

    def test_loads_all_files_once_and_excludes_duplicate_examples(self):
        first = self.write_dataset("one.jsonl", "First answer")
        second = self.write_dataset("two.jsonl", "Second answer")
        duplicate = self.write_dataset("duplicate.jsonl", "First answer")

        paths = TRAINER.resolve_dataset_inputs([first, second, first, duplicate])
        records = TRAINER.load_record_sets(paths)

        self.assertEqual([path.resolve() for path in (first, second, duplicate)], paths["training"])
        self.assertEqual(2, len(records["training"]))
        self.assertEqual("First answer", records["training"][0]["messages"][1]["content"])
        self.assertEqual("Second answer", records["training"][1]["messages"][1]["content"])
        self.assertEqual([], records["validation"])

    def test_fingerprint_covers_every_file_and_preserves_single_file_compatibility(self):
        first = self.write_dataset("one.jsonl", "First answer")
        second = self.write_dataset("two.jsonl", "Second answer")
        self.assertEqual(TRAINER.sha256_file(first), TRAINER.dataset_fingerprint([first]))
        original = TRAINER.dataset_fingerprint([first, second])
        self.assertNotEqual(original, TRAINER.dataset_fingerprint([second, first]))
        self.assertNotEqual(original, TRAINER.dataset_fingerprint([first]))
        second.write_text("changed second file", encoding="utf-8")
        self.assertNotEqual(original, TRAINER.dataset_fingerprint([first, second]))
        self.assertIsNone(TRAINER.dataset_fingerprint([]))

    def test_single_file_preserves_repeated_records_and_ignores_blank_lines(self):
        first = self.write_dataset("one.jsonl", "Repeated answer")
        first.write_text(first.read_text(encoding="utf-8") * 2 + "\n", encoding="utf-8")

        records = TRAINER.load_record_sets(TRAINER.resolve_dataset_inputs([first]))

        self.assertEqual(2, len(records["training"]))

    def test_resume_checks_contents_and_order_of_the_entire_queue(self):
        first = self.write_dataset("one.jsonl", "First answer")
        second = self.write_dataset("two.jsonl", "Second answer")
        output = self.root / "output"
        arguments = [
            "trainer.py", "--model", "base", "--dataset", str(first),
            "--dataset", str(second), "--output", str(output), "--device", "cpu",
            "--epochs", "1", "--batch-size", "1", "--learning-rate", "0.00005",
        ]
        with patch("sys.argv", arguments), patch.object(
            TRAINER, "load_record_sets", side_effect=RuntimeError("preflight reached")
        ):
            with self.assertRaisesRegex(RuntimeError, "preflight reached"):
                TRAINER.main()
        checkpoint = output / "checkpoint-1"
        checkpoint.mkdir()
        (checkpoint / "training_run.json").write_bytes(
            (output / "training_run.json").read_bytes()
        )
        resume_arguments = arguments + ["--resume-checkpoint", str(checkpoint)]
        with patch("sys.argv", resume_arguments), patch.object(
            TRAINER, "load_record_sets", side_effect=RuntimeError("resume accepted")
        ):
            with self.assertRaisesRegex(RuntimeError, "resume accepted"):
                TRAINER.main()
        self.write_dataset("two.jsonl", "Changed later file")
        with patch("sys.argv", resume_arguments):
            with self.assertRaisesRegex(SystemExit, "dataset/split SHA-256"):
                TRAINER.main()

    def test_verifies_and_keeps_held_out_files_for_multiple_exports(self):
        manifests = []
        training_files = []
        for index in range(2):
            directory = self.root / str(index)
            directory.mkdir()
            files = {}
            for name in ("training", "validation", "test"):
                files[name] = directory / f"{name}.jsonl"
                files[name].write_text(
                    json.dumps({"messages": [
                        {"role": "assistant", "content": f"{index} {name} evidence"}
                    ]}) + "\n",
                    encoding="utf-8",
                )
            manifests.append(self.write_manifest(directory, files))
            training_files.append(files["training"])

        paths = TRAINER.resolve_dataset_inputs(training_files, manifests=manifests)
        records = TRAINER.load_record_sets(paths)

        for name in ("training", "validation", "test"):
            self.assertEqual(2, len(paths[name]))
            self.assertEqual(2, len(records[name]))
            self.assertTrue(all(
                name in record["messages"][0]["content"] for record in records[name]
            ))
        paths["test"][1].write_text("tampered", encoding="utf-8")
        with self.assertRaisesRegex(SystemExit, "does not match"):
            TRAINER.resolve_dataset_inputs(training_files, manifests=manifests)

    def test_rejects_same_file_assigned_to_training_and_validation(self):
        first = self.write_dataset("one.jsonl", "Answer")
        with self.assertRaisesRegex(SystemExit, "Dataset leakage"):
            TRAINER.resolve_dataset_inputs([first], validation=[first])

    def test_rejects_source_document_leakage_across_different_examples(self):
        first = self.write_dataset("train.jsonl", "First excerpt", "https://www.irs.gov/p17")
        second = self.write_dataset("test.jsonl", "Different excerpt", "https://www.irs.gov/p17")
        paths = TRAINER.resolve_dataset_inputs([first], test=[second])
        with self.assertRaisesRegex(SystemExit, "Dataset leakage: source"):
            TRAINER.load_record_sets(paths)

    def test_rejects_duplicate_examples_across_training_and_test(self):
        first = self.write_dataset("train.jsonl", "Same answer")
        second = self.write_dataset("test.jsonl", "Same answer")
        paths = TRAINER.resolve_dataset_inputs([first], test=[second])
        with self.assertRaisesRegex(SystemExit, "same example"):
            TRAINER.load_record_sets(paths)

    def test_rejects_missing_empty_or_malformed_later_files(self):
        first = self.write_dataset("one.jsonl", "First answer")
        second = self.root / "two.jsonl"
        with self.assertRaisesRegex(SystemExit, "does not exist"):
            TRAINER.resolve_dataset_inputs([first, second])
        for payload, message in [
            ("", "no JSONL records"),
            ("not json", "invalid JSON"),
            ('{"messages":[]}', "invalid chat messages"),
        ]:
            second.write_text(payload, encoding="utf-8")
            with self.assertRaisesRegex(SystemExit, message):
                TRAINER.load_record_sets(TRAINER.resolve_dataset_inputs([first, second]))


if __name__ == "__main__":
    unittest.main()
