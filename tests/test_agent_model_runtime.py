import importlib.util
import io
import json
import unittest
from pathlib import Path


RUNTIME_PATH = (
    Path(__file__).resolve().parents[1]
    / "src"
    / "TaxIntelligenceStudio.Infrastructure"
    / "Training"
    / "agent_model_runtime.py"
)
SPEC = importlib.util.spec_from_file_location("agent_model_runtime", RUNTIME_PATH)
RUNTIME = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNTIME)


class ChatRequestTests(unittest.TestCase):
    def test_preserves_evidence_assistant_response_and_follow_up(self):
        request = {
            "messages": [
                {"role": "system", "content": "IRS evidence: rental expenses"},
                {"role": "user", "content": "Explain rental expenses"},
                {"role": "assistant", "content": "The prior trained response"},
                {"role": "user", "content": "Explain that answer"},
            ],
            "max_tokens": 64,
            "temperature": 0,
        }
        self.assertEqual(
            request, RUNTIME.read_chat_request(io.StringIO(json.dumps(request)))
        )

    def test_rejects_invalid_conversation_roles(self):
        with self.assertRaisesRegex(ValueError, "supported role"):
            RUNTIME.read_chat_request(
                io.StringIO('{"messages":[{"role":"tool","content":"invalid"}]}')
            )

    def test_rejects_empty_conversation(self):
        with self.assertRaisesRegex(ValueError, "conversation messages"):
            RUNTIME.read_chat_request(io.StringIO('{"messages":[]}'))


if __name__ == "__main__":
    unittest.main()
