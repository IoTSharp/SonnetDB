"""连接器0.2.0离线合同：不访问服务、不读取凭证、不操作Docker。"""
import base64
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

REPOSITORY = Path(__file__).resolve().parents[1]


def load(name, relative):
    spec = importlib.util.spec_from_file_location(name, REPOSITORY / relative)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PACKAGE = load("connector_package", "connectors/workbuddy-sonnetdb/validate-package.py")
VERIFY = load("connector_verify", "eng/workbuddy-sonnetdb-verify.py")


def tools():
    return [{"name": name,
             "inputSchema": {"type": "object", "properties": {}},
             "outputSchema": {"type": "object", "properties": {"contractVersion": {"type": "string"}}},
             "annotations": {"readOnlyHint": name in VERIFY.READ_TOOLS,
                             "destructiveHint": name in VERIFY.WRITE_TOOLS and name not in {"create_bucket", "publish_mq"},
                             "idempotentHint": name in VERIFY.READ_TOOLS}}
            for name in sorted(VERIFY.READ_TOOLS | VERIFY.WRITE_TOOLS)]


class WorkBuddyContractTests(unittest.TestCase):
    def test_inventory_and_version_match_current_contract(self):
        self.assertEqual(set(PACKAGE.TOOLS), VERIFY.READ_TOOLS | VERIFY.WRITE_TOOLS)
        self.assertEqual(28, len(VERIFY.READ_TOOLS))
        self.assertEqual(15, len(VERIFY.WRITE_TOOLS))
        self.assertEqual("0.2.0", PACKAGE.load_json("connector-meta.json")["version"])
        self.assertEqual(43, len(VERIFY.validate_discovery(tools())))

    def test_legacy_nine_tool_service_is_rejected(self):
        legacy = [tool for tool in tools() if tool["name"] in PACKAGE.LEGACY_TOOLS]
        with self.assertRaises(VERIFY.CheckFailure) as error:
            VERIFY.validate_discovery(legacy)
        self.assertEqual("mcp_5_0_tools_missing", error.exception.code)

    def test_wrong_write_annotation_and_missing_schema_are_rejected(self):
        for violation in ("annotation", "schema"):
            with self.subTest(violation=violation):
                inventory = tools()
                target = next(tool for tool in inventory if tool["name"] == "put_kv")
                if violation == "annotation":
                    target["annotations"]["readOnlyHint"] = True
                else:
                    del target["outputSchema"]
                with self.assertRaises(VERIFY.CheckFailure):
                    VERIFY.validate_discovery(inventory)

    def test_additive_future_tool_is_accepted_without_authorization_inference(self):
        inventory = tools() + [{"name": "future_tool"}]
        self.assertEqual(43, len(VERIFY.validate_discovery(inventory)))
        self.assertIn("query_sql", VERIFY.READ_TOOLS)
        self.assertNotIn("query_sql", VERIFY.WRITE_TOOLS)

    def test_discovery_receipt_has_43_tools_and_refuses_existing_schema_file(self):
        with tempfile.TemporaryDirectory(prefix="wb-contract-discovery-") as directory:
            verifier = VERIFY.Verification.__new__(VERIFY.Verification)
            verifier.http = Mock()
            verifier.db = "synthetic"
            verifier.reader = {"token": "placeholder"}
            verifier.args = SimpleNamespace(output=str(Path(directory) / "verification-020.json"))
            verifier.report = {}
            client = Mock()
            client.rpc.return_value = {"tools": tools()}
            with patch.object(VERIFY, "Mcp", return_value=client):
                result = verifier.mcp_initialize()
                self.assertEqual(43, result["requiredTools"])
                self.assertFalse(result["writeAuthorizationInferred"])
                schema_file = Path(verifier.report["mcpToolsSchemaFile"])
                original = schema_file.read_bytes()
                self.assertNotIn(b"placeholder", original)
                self.assertEqual(43, len(json.loads(original)["tools"]))
                with self.assertRaises(VERIFY.CheckFailure):
                    verifier.mcp_initialize()
                self.assertEqual(original, schema_file.read_bytes())

    def test_nine_model_reader_plan_uses_only_read_tools(self):
        verifier = VERIFY.Verification.__new__(VERIFY.Verification)
        verifier.names = {name: name for name in ("keyspace", "documents", "fulltext", "fulltextIndex", "vectorDocs", "bucket", "topic", "graph")}
        verifier.timestamp = 1000
        verifier.measurement_query = "time"
        verifier.table_query = "table"
        calls = []

        def call(name, arguments):
            calls.append(name)
            result = {
                "get_kv": {"found": True, "valueBase64": base64.b64encode(b"wbverify synthetic kv").decode()},
                "get_document": {"documents": [{"json": json.dumps({"label": "synthetic", "count": 7})}]},
                "search_fulltext": {"rows": [[{"kind": 1, "stringValue": "sample"}]]},
                "search_vector": {"rows": [[{"kind": 1, "stringValue": "sample"}]]},
                "get_object": {"found": True, "truncated": False, "valueBase64": base64.b64encode(b"wbverify synthetic object\n").decode()},
                "read_mq": {"storageScope": "instance", "messages": [{"payloadBase64": base64.b64encode(b"wbverify synthetic mq").decode()}]},
                "get_graph_vertex": {"found": True},
                "expand_graph": {"edges": [{"neighborId": 2}]},
            }
            self.assertIn(name, VERIFY.READ_TOOLS)
            return result[name]

        verifier.mcp = SimpleNamespace(tool=call, query=lambda sql: {"truncated": False, "rows":
            [[1000 + i, "probe", i + 0.5] for i in range(3)] if sql == "time" else [[1, "synthetic"], [2, "sample"], [3, "bounded"]]})
        readers = verifier.mcp_model_readers()
        self.assertEqual(set(VERIFY.MODELS), set(readers))
        for model in VERIFY.MODELS:
            readers[model]()
        self.assertFalse(set(calls) & VERIFY.WRITE_TOOLS)

    def test_static_validator_rejects_reintroduced_readonly_claim(self):
        with tempfile.TemporaryDirectory(prefix="wb-contract-static-") as directory:
            target = Path(directory)
            for relative in (*PACKAGE.PACKAGE_FILES, *PACKAGE.LOCAL_ONLY):
                output = target / relative
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_bytes((PACKAGE.ROOT / relative).read_bytes())
            skill = target / "skills/sonnetdb-skill/SKILL.md"
            with skill.open("a", encoding="utf-8") as stream:
                stream.write("\n当前 MCP contract 是只读入口\n")
            with patch.object(PACKAGE, "ROOT", target):
                with self.assertRaises(ValueError):
                    PACKAGE.check_skill()


if __name__ == "__main__":
    unittest.main()
