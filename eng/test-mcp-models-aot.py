"""启动本次构建的 NativeAOT Server，验证实际 MCP 读写与只读拒绝；仅使用临时合成数据。"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    server = args.server.resolve(strict=True)
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    report = {"status": "RUNNING", "server": str(server), "serverSha256": hashlib.sha256(server.read_bytes()).hexdigest(), "checks": []}
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    process = None
    owned_data = None

    def require(condition, message):
        if not condition:
            raise RuntimeError(message)

    def request(path, payload=None, token=None, extra=None, expected=200):
        require(time.monotonic() - started < 180, "smoke wall-clock deadline exceeded")
        headers = {"Content-Type": "application/json"}
        if token:
            headers["Authorization"] = "Bearer " + token
        headers.update(extra or {})
        data = None if payload is None else json.dumps(payload).encode("utf-8")
        req = urllib.request.Request(base + path, data, headers)
        try:
            response = opener.open(req, timeout=5)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw = response.read(1024 * 1024 + 1)
            require(response.code == expected, "unexpected HTTP status " + str(response.code))
            require(len(raw) <= 1024 * 1024, "response budget exceeded")
            return raw, response.headers

    def mcp(token):
        state = {"id": 0, "Accept": "application/json, text/event-stream"}

        def rpc(method, parameters=None, notification=False):
            state["id"] += 1
            envelope = {"jsonrpc": "2.0", "method": method}
            if not notification:
                envelope["id"] = state["id"]
            if parameters is not None:
                envelope["params"] = parameters
            headers = {key: value for key, value in state.items() if key != "id"}
            raw, response_headers = request("/mcp/" + database, envelope, token, headers, 202 if notification else 200)
            if response_headers.get("Mcp-Session-Id"):
                state["Mcp-Session-Id"] = response_headers["Mcp-Session-Id"]
            if notification:
                return None
            if "text/event-stream" in response_headers.get("Content-Type", ""):
                messages = [json.loads(line[5:].strip()) for line in raw.decode().splitlines() if line.startswith("data:")]
                messages = [item for item in messages if item.get("id") == state["id"]]
                require(len(messages) == 1, "MCP SSE response identity")
                message = messages[0]
            else:
                message = json.loads(raw)
            require(message.get("id") == state["id"] and "error" not in message, "MCP protocol error")
            return message["result"]

        initialized = rpc("initialize", {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "sonnetdb-aot-model-smoke", "version": "1.0"}})
        state["MCP-Protocol-Version"] = initialized["protocolVersion"]
        rpc("notifications/initialized", notification=True)
        return rpc

    def call(rpc, name, arguments=None):
        result = rpc("tools/call", {"name": name, "arguments": arguments or {}})
        require(not result.get("isError"), "tool failed: " + name)
        structured = result.get("structuredContent", {})
        require(structured.get("contractVersion") == "1.0", "typed contract missing: " + name)
        report["checks"].append(name)
        return structured

    try:
        with socket.socket() as reserved:
            reserved.bind(("127.0.0.1", 0))
            port = reserved.getsockname()[1]
        base = "http://127.0.0.1:" + str(port)
        database = "aot_models"
        password = secrets.token_urlsafe(24)
        reader_password = secrets.token_urlsafe(24)
        with tempfile.TemporaryDirectory(prefix="sonnetdb-mcp-aot-") as directory:
            owned_data = Path(directory)
            environment = os.environ.copy()
            environment.update({
                "SONNETDB_USER": "aot_admin", "SONNETDB_PASSWORD": password, "SONNETDB_DB": database,
                "SONNETDB_SonnetDBServer__DataRoot": directory,
                "SONNETDB_SonnetDBServer__Mqtt__Enabled": "false",
                "SONNETDB_SonnetDBServer__AllowAnonymousProbes": "true",
                "SONNETDB_Kestrel__Endpoints__Http__Url": base,
                "SONNETDB_Kestrel__Endpoints__FrameH2__Url": "http://127.0.0.1:0",
                "DOTNET_PROCESSOR_COUNT": "4",
            })
            argv = [str(server)]
            with output.with_suffix(".server.log").open("wb") as log:
                process = subprocess.Popen(argv, cwd=server.parent, env=environment, stdout=log, stderr=subprocess.STDOUT,
                                           creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                report["process"] = {"pid": process.pid, "parentPid": os.getpid(), "startedUnix": time.time(), "argv": argv}
                try:
                    ready_deadline = time.monotonic() + 60
                    ready = False
                    for _ in range(60):
                        require(process.poll() is None, "NativeAOT Server exited before readiness")
                        if time.monotonic() >= ready_deadline:
                            break
                        try:
                            request("/healthz/live")
                            ready = True
                            break
                        except (urllib.error.URLError, OSError, RuntimeError):
                            time.sleep(0.25)
                    else:
                        raise RuntimeError("NativeAOT readiness attempts exhausted")
                    require(ready, "NativeAOT readiness wall-clock deadline exceeded")
                    raw, _ = request("/v1/auth/login", {"username": "aot_admin", "password": password})
                    token = json.loads(raw)["token"]
                    admin = mcp(token)
                    tools = admin("tools/list")["tools"]
                    names = {tool["name"] for tool in tools}
                    require(len(names) >= 43 and {"get_kv", "list_buckets", "read_mq", "execute_sql", "upsert_graph_vertex"} <= names, "model tools missing")
                    report["toolCount"] = len(names)
                    call(admin, "put_kv", {"keyspace": "state", "key": "x", "valueBase64": "AP8="})
                    require(call(admin, "get_kv", {"keyspace": "state", "key": "x"})["valueBase64"] == "AP8=", "KV binary mismatch")
                    call(admin, "create_bucket", {"bucket": "samples"})
                    call(admin, "put_object", {"bucket": "samples", "key": "a", "valueBase64": "YWJj"})
                    require(call(admin, "get_object", {"bucket": "samples", "key": "a", "maxBytes": 1})["truncated"], "object range missing")
                    call(admin, "publish_mq", {"topic": "events", "payloadBase64": "AQI="})
                    require(call(admin, "read_mq", {"topic": "events"})["storageScope"] == "instance", "MQ scope missing")
                    call(admin, "execute_sql", {"sql": "CREATE TABLE Devices (Id INT, PRIMARY KEY (Id))"})
                    call(admin, "execute_sql", {"sql": "INSERT INTO Devices (Id) VALUES (1)"})
                    require(call(admin, "query_sql", {"sql": "SELECT Id FROM Devices"})["returnedRows"] == 1, "relational read failed")
                    call(admin, "execute_sql", {"sql": "CREATE MEASUREMENT telemetry (host TAG, value FIELD FLOAT)"})
                    call(admin, "execute_sql", {"sql": "INSERT INTO telemetry (time, host, value) VALUES (1, 'probe', 0.5)"})
                    require(call(admin, "sample_rows", {"measurement": "telemetry"})["returnedRows"] == 1, "timeseries read failed")
                    call(admin, "execute_sql", {"sql": "CREATE DOCUMENT COLLECTION notes"})
                    call(admin, "execute_sql", {"sql": "CREATE FULLTEXT INDEX notes_text ON notes ('$.body') USING unicode"})
                    call(admin, "execute_sql", {"sql": "CREATE VECTOR INDEX notes_vector ON notes ('$.embedding') WITH (dimensions=3, metric='l2')"})
                    require(len(call(admin, "describe_collection", {"collection": "notes"})["indexes"]) == 2, "search index discovery failed")
                    call(admin, "put_document", {"collection": "notes", "id": "a", "json": '{"body":"pump alarm","embedding":[0,0,0]}'})
                    require(len(call(admin, "get_document", {"collection": "notes", "id": "a"})["documents"]) == 1, "document read failed")
                    require(call(admin, "search_fulltext", {"collection": "notes", "index": "notes_text", "path": "$.body", "query": "pump"})["returnedRows"] == 1, "fulltext read failed")
                    require(call(admin, "search_vector", {"collection": "notes", "path": "$.embedding", "vector": [0, 0, 0], "k": 1, "metric": "l2"})["returnedRows"] == 1, "vector read failed")
                    call(admin, "execute_sql", {"sql": "CREATE GRAPH topology"})
                    call(admin, "upsert_graph_vertex", {"graph": "topology", "request": {"id": 1, "labels": [10], "requestId": str(uuid.uuid4())}})
                    require(call(admin, "get_graph_vertex", {"graph": "topology", "vertexId": 1})["found"], "graph read failed")
                    full_binary = base64.b64encode(bytes([255]) * 65536).decode("ascii")
                    call(admin, "put_kv", {"keyspace": "binary_limit", "key": "exact", "valueBase64": full_binary})
                    require(call(admin, "get_kv", {"keyspace": "binary_limit", "key": "exact"})["valueBase64"] == full_binary, "64 KiB binary boundary failed")
                    page_value = base64.b64encode(bytes(32768)).decode("ascii")
                    for key in ("a", "b", "c"):
                        call(admin, "put_kv", {"keyspace": "budget_page", "key": key, "valueBase64": page_value})
                    first_page = call(admin, "scan_kv", {"keyspace": "budget_page", "maxRows": 1000})
                    require(first_page["truncated"] and len(first_page["entries"]) <= 2, "KV byte page limit failed")
                    next_page = call(admin, "scan_kv", {"keyspace": "budget_page", "maxRows": 1000, "afterKeyBase64": first_page["nextCursor"]})
                    require(next_page["entries"][0]["key"] != first_page["entries"][0]["key"], "KV continuation repeated data")
                    raw, _ = request("/v1/db/" + database + "/kv/state/set-many", {"entries": [{"key": "oversized", "value": base64.b64encode(bytes(2 * 1024 * 1024)).decode("ascii")}]}, token)
                    oversized = admin("tools/call", {"name": "get_kv", "arguments": {"keyspace": "state", "key": "oversized"}})
                    require(oversized.get("isError") and json.loads(oversized["content"][1]["text"])["code"] == "limit_exceeded", "stored value budget failed")
                    report["checks"].append("oversized_stored_value_rejected")
                    graph_limit = admin("tools/call", {"name": "upsert_graph_vertex", "arguments": {"graph": "topology", "request": {"id": 2, "labels": list(range(1, 130)), "requestId": str(uuid.uuid4())}}})
                    require(graph_limit.get("isError") and json.loads(graph_limit["content"][1]["text"])["code"] == "limit_exceeded", "graph input preflight failed")
                    require(not call(admin, "get_graph_vertex", {"graph": "topology", "vertexId": 2})["found"], "rejected graph write mutated data")
                    report["checks"].append("oversized_graph_input_rejected_without_mutation")
                    for sql in ["CREATE USER aot_reader WITH PASSWORD '" + reader_password + "'", "GRANT READ ON DATABASE aot_models TO aot_reader"]:
                        raw, _ = request("/v1/db/" + database + "/sql", {"sql": sql}, token)
                        require(not any(isinstance(frame, dict) and frame.get("type") == "error" for frame in [json.loads(line) for line in raw.splitlines() if line.strip()]), "reader setup SQL failed")
                    raw, _ = request("/v1/auth/login", {"username": "aot_reader", "password": reader_password})
                    reader = mcp(json.loads(raw)["token"])
                    denied = reader("tools/call", {"name": "put_kv", "arguments": {"keyspace": "state", "key": "x", "valueBase64": "AQ=="}})
                    require(denied.get("isError") and json.loads(denied["content"][1]["text"])["code"] == "forbidden", "read-only write was not rejected")
                    require(call(reader, "get_kv", {"keyspace": "state", "key": "x"})["valueBase64"] == "AP8=", "denied write changed data")
                    report["checks"].append("read_only_write_rejected_and_value_unchanged")
                    report["status"] = "PASS"
                finally:
                    if process.poll() is None:
                        process.terminate()
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
                    report["serverExited"] = process.poll() is not None
    except Exception as error:
        report["status"] = "FAIL"
        report["error"] = str(error)
    finally:
        report["ownedDataRemoved"] = owned_data is not None and not owned_data.exists()
        report["elapsedSeconds"] = round(time.monotonic() - started, 3)
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))
    return 0 if report["status"] == "PASS" and report.get("serverExited") and report["ownedDataRemoved"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
