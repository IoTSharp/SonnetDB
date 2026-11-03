#!/usr/bin/env python3
"""仅用于隔离的本机 SonnetDB 验收；会创建用户、数据库与小体积合成数据。

首次运行随机密码仅存内存，后续运行从 WBVERIFY_ADMIN_USERNAME 和
WBVERIFY_ADMIN_PASSWORD 环境变量登录。不会保存凭据、原始响应或请求日志。
--restart-container 是显式选择的 Docker restart；须先核对匹配的回环端口
及 SonnetDB 镜像，不删除容器或数据卷。
原生 HTTP 九模型成功不代表九模型 MCP 均可用，报告分别记录。
"""

import argparse
import base64
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


MODELS = ("timeseries", "relational", "kv", "document", "fulltext",
          "vector", "object", "mq", "graph_beta")
KNOWN_CODES = {"invalid_argument", "invalid_sql", "read_only_violation",
               "measurement_not_found", "operation_failed", "forbidden",
               "unauthorized", "bad_request"}
MAX_BODY = 2 * 1024 * 1024


class CheckFailure(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


def require(condition, code):
    if not condition:
        raise CheckFailure(code)


def json_value(raw):
    try:
        return json.loads(raw)
    except (ValueError, UnicodeError):
        raise CheckFailure("invalid_json") from None


def ndjson(raw):
    """逐行检查：HTTP 200 并不等于 SQL 成功，必须有且只有一个末尾 end。"""
    frames = [json_value(line) for line in raw.splitlines() if line.strip()]
    require(bool(frames), "ndjson_empty")
    objects = [frame for frame in frames if isinstance(frame, dict)]
    require(not any(frame.get("type") == "error" for frame in objects),
            "ndjson_error_frame")
    require(all(isinstance(frame, (dict, list)) for frame in frames), "ndjson_frame_shape")
    require(all(frame.get("type") in ("meta", "end") for frame in objects),
            "ndjson_unexpected_frame")
    ends = [frame for frame in objects if frame.get("type") == "end"]
    require(len(ends) == 1 and frames[-1] == ends[0], "ndjson_missing_terminal_end")
    metas = [frame for frame in objects if frame.get("type") == "meta"]
    require(len(metas) <= 1, "ndjson_meta_count")
    columns = metas[0].get("columns", []) if metas else []
    rows = [frame for frame in frames if isinstance(frame, list)]
    require(not rows or len(metas) == 1, "ndjson_rows_without_meta")
    require(all(len(row) == len(columns) for row in rows), "ndjson_row_width")
    require(ends[0].get("rowCount") == len(rows), "ndjson_row_count")
    return {"columns": columns, "rows": rows, "end": ends[0]}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # 不把 Authorization 跟随重定向发送到其他目标。
        return None


class Http:
    def __init__(self, base, timeout):
        self.base = base
        self.timeout = timeout
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def request(self, method, path, data=None, token=None, expected=200,
                headers=None, raw=False):
        require(path.startswith("/") and not path.startswith("//"), "invalid_request_path")
        combined = {"Accept": "application/json"}
        if token:
            combined["Authorization"] = "Bearer " + token
        if isinstance(data, bytes):
            body = data
            combined["Content-Type"] = "application/octet-stream"
        elif data is not None:
            body = json.dumps(data, separators=(",", ":")).encode("utf-8")
            combined["Content-Type"] = "application/json"
        else:
            body = None
        combined.update(headers or {})
        req = urllib.request.Request(self.base + path, body, combined, method=method)
        try:
            response = self.opener.open(req, timeout=self.timeout)
        except urllib.error.HTTPError as exc:
            response = exc
        except (urllib.error.URLError, OSError, TimeoutError):
            raise CheckFailure("http_unreachable_or_timeout") from None
        try:
            with response:
                status = response.code
                response_headers = response.headers
                payload = response.read(MAX_BODY + 1)
        except (OSError, TimeoutError):
            raise CheckFailure("http_body_read_failed") from None
        require(len(payload) <= MAX_BODY, "http_response_budget_exceeded")
        require(status == expected, "http_status_" + str(status))
        if raw:
            return payload, response_headers
        if not payload:
            return None
        return json_value(payload)

    def sql(self, path, sql, token):
        raw, _ = self.request("POST", path, {"sql": sql}, token, raw=True)
        return ndjson(raw)


class Mcp:
    def __init__(self, http, db, token):
        self.http = http
        self.path = "/mcp/" + db
        self.token = token
        self.counter = 0
        self.headers = {"Accept": "application/json, text/event-stream"}

    def rpc(self, method, params=None, expected=200, notification=False):
        body = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            body["params"] = params
        if not notification:
            self.counter += 1
            body["id"] = self.counter
        raw, headers = self.http.request("POST", self.path, body, self.token,
                                         expected, self.headers, raw=True)
        session = headers.get("Mcp-Session-Id")
        if session:
            self.headers["Mcp-Session-Id"] = session
        if notification or expected != 200:
            return None
        content_type = headers.get("Content-Type", "").lower()
        if "text/event-stream" in content_type:
            messages = []
            data_lines = []
            for line in raw.decode("utf-8").replace("\r\n", "\n").split("\n") + [""]:
                if line.startswith("data:"):
                    data_lines.append(line[5:].lstrip())
                elif not line and data_lines:
                    messages.append(json_value("\n".join(data_lines)))
                    data_lines = []
            matching = [item for item in messages if isinstance(item, dict)
                        and item.get("id") == body["id"]]
            require(len(matching) == 1, "mcp_sse_response_id")
            message = matching[0]
        else:
            message = json_value(raw)
        require(isinstance(message, dict) and message.get("id") == body["id"],
                "mcp_response_id")
        if "error" in message:
            error = message["error"]
            if isinstance(error, dict) and error.get("code") == -32602:
                raise CheckFailure("mcp_invalid_params")
            raise CheckFailure("mcp_rpc_error")
        require(isinstance(message.get("result"), dict), "mcp_result_missing")
        return message["result"]

    def initialize(self):
        result = self.rpc("initialize", {"protocolVersion": "2025-03-26",
                          "capabilities": {}, "clientInfo": {
                              "name": "workbuddy-sonnetdb-verify", "version": "1.0"}})
        version = result.get("protocolVersion")
        require(isinstance(version, str) and bool(version), "mcp_protocol_missing")
        self.headers["MCP-Protocol-Version"] = version
        self.rpc("notifications/initialized", notification=True, expected=202)
        return result

    def tool(self, name, arguments):
        result = self.rpc("tools/call", {"name": name, "arguments": arguments})
        if result.get("isError"):
            code = "mcp_tool_error"
            for content in result.get("content", []):
                text = content.get("text", "") if isinstance(content, dict) else ""
                try:
                    parsed = json.loads(text)
                except (ValueError, TypeError):
                    continue
                if isinstance(parsed, dict) and parsed.get("code") in KNOWN_CODES:
                    code = parsed["code"]
            raise CheckFailure(code)
        structured = result.get("structuredContent")
        require(isinstance(structured, dict), "mcp_structured_content_missing")
        require(structured.get("contractVersion") == "1.0", "mcp_contract_version")
        return structured

    @staticmethod
    def scalar(value):
        # Contracts/Dtos.cs: ScalarKind Null=0, String=1, Integer=2, Double=3, Boolean=4。
        require(isinstance(value, dict), "mcp_scalar_wrapper_missing")
        kind = value.get("kind")
        fields = {0: None, 1: "stringValue", 2: "integerValue", 3: "doubleValue", 4: "booleanValue"}
        require(type(kind) is int and kind in fields, "mcp_scalar_kind_unknown")
        if kind == 0:
            return None
        field = fields[kind]
        require(field in value and value[field] is not None, "mcp_scalar_value_missing")
        decoded = value[field]
        valid = ((kind == 1 and isinstance(decoded, str))
                 or (kind == 2 and type(decoded) is int)
                 or (kind == 3 and type(decoded) in (int, float))
                 or (kind == 4 and type(decoded) is bool))
        require(valid, "mcp_scalar_value_type")
        return decoded

    def query(self, sql, max_rows=10):
        result = self.tool("query_sql", {"sql": sql, "maxRows": max_rows})
        require(isinstance(result.get("rows"), list), "mcp_rows_missing")
        require(result.get("returnedRows") == len(result["rows"]), "mcp_row_count")
        require(len(result["rows"]) <= max_rows, "mcp_row_budget")
        require(isinstance(result.get("columns"), list), "mcp_columns_missing")
        require(all(isinstance(row, list) and len(row) == len(result["columns"])
                    for row in result["rows"]), "mcp_row_width")
        return {**result, "rows": [[self.scalar(value) for value in row] for row in result["rows"]]}


class Verification:
    def __init__(self, args):
        self.args = args
        self.http = Http(args.base_url, args.timeout)
        self.suffix = secrets.token_hex(5)
        self.db = "wbverify"
        self.hidden = "wbverify_hidden_" + self.suffix
        self.reader_name = "wbverify_reader_" + self.suffix
        self.admin = None
        self.reader = None
        self.mcp = None
        self.readbacks = []
        self.report = {"schemaVersion": "1.0", "startedUnix": time.time(),
                       "baseUrl": args.base_url, "database": self.db,
                       "runId": self.suffix, "credentialsSaved": False,
                       "scope": "隔离本机小样本；原生 API 与 MCP 分开验收；非质量、规模或生产认证",
                       "checks": [], "nativeModels": [], "mcpModels": [],
                       "persistence": {"status": "not_available", "code": "NOT_RUN_restart_not_requested"}}

    def run_check(self, model, transport, assertion, function, target=None):
        entry = {"model": model, "transport": transport,
                 "assertions": [assertion], "status": "fail"}
        (target if target is not None else self.report["checks"]).append(entry)
        try:
            observation = function()
            entry["status"] = "pass"
            if observation is not None:
                # 所有调用者仅返回白名单构造的统计值，禁止传入原始 HTTP 响应。
                entry["observation"] = observation
            print(model + " " + transport + " " + assertion + " pass", flush=True)
            return True
        except CheckFailure as exc:
            entry["code"] = exc.code
        except Exception:
            # 不输出异常字符串、traceback 或响应体，以防包含密码/Token。
            entry["code"] = "unexpected_probe_exception"
        print(model + " " + transport + " " + assertion + " fail " + entry["code"], flush=True)
        return False

    def control(self, sql):
        return self.http.sql("/v1/sql", sql, self.admin["token"])

    def sql(self, sql, token=None):
        return self.http.sql("/v1/db/" + self.db + "/sql", sql,
                             token or self.admin["token"])

    def api(self, method, path, data=None, expected=200, reader=False):
        return self.http.request(method, "/v1/db/" + self.db + path, data,
                                 (self.reader if reader else self.admin)["token"], expected)

    def setup(self):
        status = self.http.request("GET", "/v1/setup/status")
        if status.get("needsSetup"):
            configured_username = os.environ.get("WBVERIFY_ADMIN_USERNAME")
            configured_password = os.environ.get("WBVERIFY_ADMIN_PASSWORD")
            require(bool(configured_username) == bool(configured_password), "admin_login_env_pair_required")
            password = configured_password or "WbVerify!" + secrets.token_hex(24)
            self.admin = self.http.request("POST", "/v1/setup/initialize", {
                "serverId": status["suggestedServerId"], "organization": "WorkBuddy isolated verification",
                "username": configured_username or "wbverify_admin_" + self.suffix, "password": password,
                "bearerToken": "wbverify_" + secrets.token_hex(32)}, expected=201)
            self.report["initializedFreshServer"] = True
        else:
            username = os.environ.get("WBVERIFY_ADMIN_USERNAME")
            password = os.environ.get("WBVERIFY_ADMIN_PASSWORD")
            require(bool(username and password), "NOT_RUN_initialized_server_requires_admin_login_env")
            self.admin = self.http.request("POST", "/v1/auth/login", {
                "username": username, "password": password})
            self.report["initializedFreshServer"] = False
        require(self.admin.get("isSuperuser") is True and bool(self.admin.get("token")),
                "administrator_identity_required")
        databases = self.http.request("GET", "/v1/db", token=self.admin["token"])
        if self.db not in databases.get("databases", []):
            self.http.request("POST", "/v1/db", {"name": self.db}, self.admin["token"], 201)
        self.http.request("POST", "/v1/db", {"name": self.hidden}, self.admin["token"], 201)
        reader_password = "WbReader!" + secrets.token_hex(24)
        self.control("CREATE USER " + self.reader_name + " WITH PASSWORD '" + reader_password + "'")
        self.control("GRANT READ ON DATABASE " + self.db + " TO " + self.reader_name)
        self.reader = self.http.request("POST", "/v1/auth/login", {
            "username": self.reader_name, "password": reader_password})
        require(self.reader.get("isSuperuser") is False and bool(self.reader.get("tokenId")),
                "reader_identity_required")
        return {"fresh": self.report["initializedFreshServer"], "readerSuperuser": False}

    def model_probe(self, model, seed, read):
        def probe():
            seed()
            self.readbacks.append((model, read))
            return read()
        self.run_check(model, "native_http", "write_then_read_synthetic_sample", probe,
                       self.report["nativeModels"])

    def native_models(self):
        s = self.suffix
        p = "wbv_" + s
        self.names = {"measurement": p + "_time", "table": p + "_table",
                      "keyspace": p + "_kv", "documents": p + "_docs",
                      "fulltext": p + "_ftdocs", "fulltextIndex": p + "_ftidx",
                      "vectors": p + "_vectors", "vectorDocs": p + "_vdocs",
                      "graph": p + "_graph", "bucket": "wbv-" + s,
                      "topic": p + "_topic"}
        n = self.names
        self.report["syntheticResources"] = n
        self.timestamp = int(time.time() * 1000)
        ts = self.timestamp
        measurement_sql = "SELECT time, device, value FROM " + n["measurement"] + " ORDER BY time"
        self.measurement_query = measurement_sql
        expected_time = [[ts + i, "probe", i + 0.5] for i in range(3)]

        def seed_time():
            self.sql("CREATE MEASUREMENT " + n["measurement"] + " (device TAG, value FIELD FLOAT)")
            values = ",".join("(%d, 'probe', %.1f)" % tuple((row[0], row[2])) for row in expected_time)
            result = self.sql("INSERT INTO " + n["measurement"] + " (time, device, value) VALUES " + values)
            require(result["end"].get("recordsAffected") == 3, "timeseries_insert_count")
        self.model_probe("timeseries", seed_time,
                         lambda: self.assert_rows(self.sql(measurement_sql, self.reader["token"])["rows"], expected_time))

        table_sql = "SELECT id, name FROM " + n["table"] + " ORDER BY id"
        self.table_query = table_sql
        def seed_table():
            self.sql("CREATE TABLE " + n["table"] + " (id INT, name STRING, PRIMARY KEY (id))")
            self.sql("INSERT INTO " + n["table"] + " (id, name) VALUES (1, 'synthetic'), (2, 'sample'), (3, 'bounded')")
        self.model_probe("relational", seed_table, lambda: self.assert_rows(
            self.sql(table_sql, self.reader["token"])["rows"], [[1, "synthetic"], [2, "sample"], [3, "bounded"]]))

        encoded = base64.b64encode(b"wbverify synthetic kv").decode("ascii")
        kv_path = "/kv/" + n["keyspace"]
        def seed_kv():
            response = self.api("POST", kv_path + "/set-many", {
                "entries": [{"key": "sample", "value": encoded}], "expiresAtUtc": None})
            require(response.get("versions", {}).get("sample", 0) > 0, "kv_version")
        def read_kv():
            response = self.api("POST", kv_path + "/get", {"key": "sample"}, reader=True)
            require(response.get("found") is True and response.get("value") == encoded, "kv_roundtrip")
            return {"items": 1}
        self.model_probe("kv", seed_kv, read_kv)

        document_path = "/documents/" + n["documents"]
        document = {"label": "synthetic", "count": 7}
        def seed_docs():
            self.api("POST", document_path, {}, 201)
            self.insert_documents(document_path, [{"id": "sample", "document": document}])
        def read_docs():
            response = self.api("POST", document_path + "/find", {"ids": ["sample"], "limit": 1}, reader=True)
            docs = response.get("documents", [])
            require(len(docs) == 1 and docs[0].get("id") == "sample"
                    and docs[0].get("document") == document, "document_roundtrip")
            return {"documents": 1}
        self.model_probe("document", seed_docs, read_docs)

        ft_path = "/documents/" + n["fulltext"]
        def seed_fulltext():
            self.api("POST", ft_path, {}, 201)
            self.insert_documents(ft_path, [{"id": "sample", "document": {"body": "pump alarm synthetic sample"}}])
            self.sql("CREATE FULLTEXT INDEX " + n["fulltextIndex"] + " ON " + n["fulltext"] + " ('$.body') USING unicode")
        def read_fulltext():
            result = self.api("POST", "/fulltext/search-preview", {
                "collection": n["fulltext"], "index": n["fulltextIndex"], "field": "*",
                "query": "pump alarm", "topK": 3, "mode": "exact", "queryKind": "all"}, reader=True)
            hits = result.get("hits", [])
            require(len(hits) == 1 and hits[0].get("documentId") == "sample", "fulltext_sample_hit")
            return {"hits": 1}
        self.model_probe("fulltext", seed_fulltext, read_fulltext)

        def seed_vector():
            self.sql("CREATE MEASUREMENT " + n["vectors"] +
                     " (device TAG, embedding FIELD VECTOR(3) WITH INDEX hnsw(m=16, ef=200, metric='l2'))")
            self.sql("INSERT INTO " + n["vectors"] + " (time, device, embedding) VALUES (" +
                     str(ts) + ", 'probe', [1, 0, 0])")
            path = "/documents/" + n["vectorDocs"]
            self.api("POST", path, {}, 201)
            self.insert_documents(path, [{"id": "sample", "document": {"embedding": [1, 0, 0]}}])
        def read_vector():
            result = self.api("POST", "/vector/search-preview", {
                "measurement": n["vectors"], "column": "embedding", "query": [0, 0, 0],
                "topK": 1, "metric": "l2", "filter": "device = 'probe'"}, reader=True)
            hits = result.get("hits", [])
            require(len(hits) == 1 and hits[0].get("timestampUtc") == ts
                    and hits[0].get("distance") == 1, "vector_exact_sample")
            return {"hits": 1, "syntheticDimension": 3, "qualityEvidence": False}
        self.model_probe("vector", seed_vector, read_vector)

        object_path = "/s3/" + n["bucket"]
        object_bytes = b"wbverify synthetic object\n"
        def seed_object():
            self.api("PUT", object_path, {"purpose": "synthetic verification"})
            self.http.request("PUT", "/v1/db/" + self.db + object_path + "/sample.txt",
                              object_bytes, self.admin["token"], headers={"Content-Type": "text/plain"})
        def read_object():
            raw, _ = self.http.request("GET", "/v1/db/" + self.db + object_path + "/sample.txt",
                                       token=self.reader["token"], raw=True)
            require(raw == object_bytes, "object_byte_roundtrip")
            return {"bytes": len(raw)}
        self.model_probe("object", seed_object, read_object)

        mq_path = "/mq/" + n["topic"]
        mq_payload = base64.b64encode(b"wbverify synthetic mq").decode("ascii")
        def seed_mq():
            result = self.api("POST", mq_path + "/publish-batch", {
                "messages": [{"payload": mq_payload, "headers": {"source": "wbverify"}}]}, 201)
            require(result.get("offsets") == [0], "mq_initial_offset")
        def read_mq():
            result = self.api("POST", mq_path + "/browse", {"fromOffset": 0, "maxCount": 2}, reader=True)
            messages = result.get("messages", [])
            require(len(messages) == 1 and messages[0].get("payload") == mq_payload
                    and messages[0].get("offset") == 0, "mq_payload_roundtrip")
            return {"messages": 1, "offset": 0}
        self.model_probe("mq", seed_mq, read_mq)

        graph_path = "/graphs/" + n["graph"]
        def seed_graph():
            self.api("POST", "/graphs", {"name": n["graph"]}, 201)
            vertices = [{"id": i, "expectedElementVersion": 0, "labels": [10],
                         "properties": [{"propertyId": 20, "value": {"kind": 4, "string": "synthetic"}}],
                         "uniquePropertyIds": []} for i in (1, 2)]
            edges = [{"id": 1001, "expectedElementVersion": 0, "sourceId": 1, "targetId": 2,
                      "labelId": 30, "properties": [], "uniquePropertyIds": []}]
            result = self.api("POST", graph_path + "/import", {
                "requestId": str(uuid.uuid4()), "vertices": vertices, "edges": edges})
            require(result.get("vertexCount") == 2 and result.get("edgeCount") == 1, "graph_import_count")
        def read_graph():
            result = self.api("GET", graph_path + "/operations/export?maxElements=10", reader=True)
            require(result.get("truncated") is False and result.get("elementCount") == 3, "graph_export_complete")
            vertices = result.get("vertices", [])
            require([item.get("id") for item in vertices] == [1, 2], "graph_vertex_ids")
            for vertex in vertices:
                properties = vertex.get("properties", [])
                require(vertex.get("labels") == [10] and len(properties) == 1
                        and properties[0].get("propertyId") == 20
                        and properties[0].get("value", {}).get("string") == "synthetic",
                        "graph_property_roundtrip")
            edges = result.get("edges", [])
            require(len(edges) == 1 and edges[0].get("sourceId") == 1 and edges[0].get("targetId") == 2
                    and edges[0].get("labelId") == 30, "graph_edge_roundtrip")
            return {"vertices": 2, "edges": 1, "beta": True}
        self.model_probe("graph_beta", seed_graph, read_graph)

    @staticmethod
    def assert_rows(actual, expected):
        require(actual == expected, "synthetic_rows_mismatch")
        return {"rows": len(actual)}

    def insert_documents(self, path, documents):
        result = self.api("POST", path + "/insert-many", {"documents": documents, "ordered": True})
        require(result.get("inserted") == len(documents) and not result.get("errors"), "document_insert_count")

    def mcp_initialize(self):
        self.mcp = Mcp(self.http, self.db, self.reader["token"])
        self.mcp.initialize()
        tools = self.mcp.rpc("tools/list").get("tools", [])
        required = {"list_databases", "list_measurements", "describe_measurement", "query_sql",
                    "sample_rows", "explain_sql", "docs_search", "skill_search", "skill_load"}
        names = {item.get("name") for item in tools}
        require(required <= names, "mcp_established_tools_missing")
        require(all(item.get("annotations", {}).get("readOnlyHint") is True for item in tools
                    if item.get("name") in required), "mcp_read_only_annotation")
        self.report["mcpTools"] = sorted(name for name in names if isinstance(name, str) and name in required)
        safe_keys = {"name", "title", "description", "inputSchema", "outputSchema", "annotations"}
        safe_tools = [{key: value for key, value in tool.items() if key in safe_keys}
                      for tool in tools if tool.get("name") in required]
        output = Path(self.args.output)
        schema_name = output.name.replace("verification", "tools", 1)
        schema_path = output.with_name(schema_name if schema_name != output.name else output.stem + "-tools.json")
        schema_path.parent.mkdir(parents=True, exist_ok=True)
        schema_path.write_text(json.dumps({"transport": "stdlib_http_mcp_client",
            "hostInstallationVerified": False, "credentialsSaved": False,
            "capturedUnix": time.time(), "tools": safe_tools}, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8")
        self.report["mcpToolsSchemaFile"] = str(schema_path)
        return {"establishedTools": len(required), "hostInstallationVerified": False}

    def expect_tool_error(self, sql, max_rows, allowed):
        try:
            self.mcp.query(sql, max_rows)
        except CheckFailure as exc:
            require(exc.code in allowed, "mcp_wrong_rejection_code")
            return {"rejectionCode": exc.code}
        raise CheckFailure("mcp_unexpected_success")

    def mcp_checks(self):
        if not self.run_check("mcp", "mcp_http", "initialize_and_tools_list", self.mcp_initialize):
            for model in MODELS:
                self.report["mcpModels"].append({"model": model, "transport": "mcp_http",
                    "assertions": ["read_synthetic_sample"], "status": "not_available",
                    "code": "NOT_RUN_mcp_initialization_failed"})
            return
        def resources():
            result = self.mcp.rpc("resources/list")
            items = result.get("resources")
            require(isinstance(items, list), "mcp_resources_missing")
            require(all(isinstance(item, dict) and isinstance(item.get("uri"), str)
                        for item in items), "mcp_resources_shape")
            return {"resources": len(items)}
        self.run_check("mcp", "mcp_http", "resources_list", resources)
        measurement = self.names["measurement"]
        def list_measurements():
            result = self.mcp.tool("list_measurements", {"maxRows": 1000})
            require(measurement in result.get("measurements", []), "mcp_measurement_not_listed")
            require(isinstance(result.get("schemaRevision"), str), "mcp_schema_revision_missing")
            return {"syntheticMeasurementListed": True}
        self.run_check("timeseries", "mcp_http", "list_measurements", list_measurements)
        def describe():
            result = self.mcp.tool("describe_measurement", {"name": measurement})
            require(result.get("measurement") == measurement, "mcp_describe_wrong_measurement")
            columns = {column.get("name") for column in result.get("columns", [])}
            require({"device", "value"} <= columns, "mcp_describe_columns_missing")
            return {"syntheticColumnsDescribed": True}
        self.run_check("timeseries", "mcp_http", "describe_measurement", describe)
        def sample():
            result = self.mcp.tool("sample_rows", {"measurement": measurement, "n": 2})
            require(result.get("returnedRows") == 2 and len(result.get("rows", [])) == 2,
                    "mcp_sample_row_count")
            require(result.get("truncated") is True, "mcp_sample_truncation")
            require(set(result.get("columns", [])) == {"time", "device", "value"},
                    "mcp_sample_columns")
            return {"returnedRows": 2, "truncated": True}
        self.run_check("timeseries", "mcp_http", "sample_rows", sample)
        def explain():
            result = self.mcp.tool("explain_sql", {"sql": self.measurement_query})
            require(result.get("database") == self.db and result.get("statementType") == "select",
                    "mcp_explain_statement")
            require(result.get("measurement") == measurement, "mcp_explain_measurement")
            return {"statementType": "select", "syntheticMeasurementExplained": True}
        self.run_check("timeseries", "mcp_http", "explain_sql", explain)
        def visible():
            result = self.mcp.tool("list_databases", {})
            require(self.db in result.get("databases", []) and self.hidden not in result["databases"],
                    "mcp_database_visibility")
            return {"authorizedDatabaseVisible": True, "hiddenDatabaseVisible": False}
        self.run_check("authorization", "mcp_http", "database_visibility", visible)
        self.run_check("authorization", "mcp_http", "missing_bearer_401", lambda:
                       Mcp(self.http, self.db, None).rpc("tools/list", expected=401))
        self.run_check("authorization", "mcp_http", "hidden_database_403", lambda:
                       Mcp(self.http, self.hidden, self.reader["token"]).rpc("tools/list", expected=403))
        self.run_check("authorization", "mcp_http", "write_rejected", lambda:
                       self.expect_tool_error("DELETE FROM " + self.names["measurement"] + " WHERE time = " +
                                              str(self.timestamp), 10, {"read_only_violation"}))
        for max_rows in (0, -1, 1001):
            self.run_check("mcp", "mcp_http", "invalid_maxRows_" + str(max_rows), lambda value=max_rows:
                           self.expect_tool_error(self.measurement_query, value,
                                                  {"invalid_argument", "mcp_invalid_params"}))
        for suffix, expected_count, truncated in (("", 2, True), (" LIMIT 3", 2, True), (" LIMIT 1", 1, False)):
            def truncation(query_suffix=suffix, count=expected_count, cut=truncated):
                result = self.mcp.query(self.measurement_query + query_suffix, 2)
                require(len(result["rows"]) == count and result.get("truncated") is cut, "mcp_truncation_contract")
                return {"returnedRows": count, "truncated": cut}
            self.run_check("timeseries", "mcp_http", "truncation_" + (suffix.strip().replace(" ", "_") or "without_LIMIT"), truncation)
        n = self.names
        queries = {
            "timeseries": (self.measurement_query, [[self.timestamp + i, "probe", i + 0.5] for i in range(3)]),
            "relational": (self.table_query, [[1, "synthetic"], [2, "sample"], [3, "bounded"]]),
            "document": ("SELECT id, json_value(document, '$.label') AS label FROM " + n["documents"], [["sample", "synthetic"]]),
            "fulltext": ("SELECT id FROM " + n["fulltext"] + " WHERE match(" + n["fulltextIndex"] +
                         ", '$.body', 'pump alarm', 3)", [["sample"]]),
            "vector": ("SELECT id FROM vector_search(source => " + n["vectorDocs"] +
                       ", vector_field => '$.embedding', vector => [0, 0, 0], k => 1, metric => 'l2')", [["sample"]]),
            "graph_beta": ("SELECT target_id FROM GRAPH_TABLE (" + n["graph"] +
                           " MATCH (a IS 10)-[e IS 30]->(b IS 10) COLUMNS (b.id AS target_id))", [[2]])}
        for model in MODELS:
            if model not in queries:
                self.report["mcpModels"].append({"model": model, "transport": "mcp_http",
                    "assertions": ["read_synthetic_sample"], "status": "not_available",
                    "code": "NOT_RUN_no_published_read_only_tool_or_confirmed_SELECT_contract",
                    "reason": "tools/list 无专用只读入口；未将原生 API 能力冒称为 MCP 能力"})
                continue
            query, expected = queries[model]
            def mcp_read(sql=query, rows=expected):
                result = self.mcp.query(sql)
                require(result.get("truncated") is False, "mcp_unexpected_truncation")
                return self.assert_rows(result["rows"], rows)
            self.run_check(model, "mcp_http", "read_synthetic_sample", mcp_read, self.report["mcpModels"])
        self.run_check("authorization", "native_http+mcp_http", "issue_revoke_rotation", self.rotation)

    def rotation(self):
        issued = self.control("ISSUE TOKEN FOR " + self.reader_name)
        require(len(issued["rows"]) == 1 and len(issued["rows"][0]) == 2, "issued_token_shape")
        token_id, token = issued["rows"][0]
        require(isinstance(token, str) and bool(token), "issued_token_missing")
        replacement = Mcp(self.http, self.db, token)
        replacement.initialize()
        replacement.query(self.measurement_query)
        old_token = self.reader["token"]
        self.control("REVOKE TOKEN '" + self.reader["tokenId"].replace("'", "''") + "'")
        Mcp(self.http, self.db, old_token).rpc("tools/list", expected=401)
        replacement.query(self.measurement_query)
        self.reader = {"token": token, "tokenId": token_id, "isSuperuser": False}
        self.mcp = replacement
        return {"oldTokenUnauthorized": True, "replacementReadSucceeded": True}

    def persistence(self):
        if not self.args.restart_container:
            return
        def restart():
            try:
                inspected = subprocess.run(["docker", "inspect", "--type", "container", self.args.restart_container],
                                           stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=15,
                                           check=False)
            except (OSError, subprocess.TimeoutExpired):
                raise CheckFailure("docker_inspect_failed") from None
            require(inspected.returncode == 0, "docker_inspect_failed")
            records = json_value(inspected.stdout)
            require(isinstance(records, list) and len(records) == 1
                    and isinstance(records[0], dict), "docker_inspect_shape")
            container = records[0]
            config = container.get("Config")
            require(isinstance(config, dict), "docker_inspect_config_missing")
            image = config.get("Image")
            require(isinstance(image, str) and image.split("@", 1)[0].rsplit("/", 1)[-1].split(":", 1)[0] == "sonnetdb",
                    "docker_restart_image_not_sonnetdb")
            network = container.get("NetworkSettings")
            ports = network.get("Ports") if isinstance(network, dict) else None
            expected_port = str(urllib.parse.urlsplit(self.args.base_url).port)
            matching_port = isinstance(ports, dict) and any(
                isinstance(bindings, list) and any(
                    isinstance(binding, dict) and binding.get("HostIp") == "127.0.0.1"
                    and binding.get("HostPort") == expected_port for binding in bindings)
                for port, bindings in ports.items() if isinstance(port, str) and port.endswith("/tcp"))
            # 隔离标签只说明用途，不能证明该容器提供本次 base-url 的服务。
            require(matching_port, "docker_restart_target_not_verified")
            container_id = container.get("Id")
            require(isinstance(container_id, str) and len(container_id) == 64
                    and all(c in "0123456789abcdef" for c in container_id), "docker_inspect_id_invalid")
            try:
                # 固定 inspect 返回的 ID，避免名称在核验后被替换而误重启其他容器。
                result = subprocess.run(["docker", "restart", "--time", "15", container_id],
                                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60,
                                        check=False)
            except (OSError, subprocess.TimeoutExpired):
                raise CheckFailure("docker_restart_failed") from None
            require(result.returncode == 0, "docker_restart_failed")
            deadline = time.monotonic() + 60
            for _ in range(60):
                if time.monotonic() >= deadline:
                    break
                try:
                    self.http.request("GET", "/healthz/live")
                    return {"restartRequested": True}
                except CheckFailure:
                    time.sleep(1)
            raise CheckFailure("restart_readiness_timeout")
        succeeded = self.run_check("persistence", "docker", "explicit_restart", restart)
        self.report["persistence"] = {"status": "pass" if succeeded else "fail", "checks": []}
        if not succeeded:
            return
        for model, read in self.readbacks:
            self.run_check(model, "native_http_after_restart", "persisted_synthetic_readback", read,
                           self.report["persistence"]["checks"])
        if any(item["status"] != "pass" for item in self.report["persistence"]["checks"]):
            self.report["persistence"]["status"] = "fail"

    def run(self):
        self.report["provenance"] = {
            "transportClient": "Python stdlib HTTP MCP protocol client",
            "workbuddyHostInstallationVerified": False,
            "imageVersion": getattr(self.args, "image_version", None),
            "imageRevision": getattr(self.args, "image_revision", None),
            "imageDigest": getattr(self.args, "image_digest", None),
            "imageEvidenceSource": "operator_supplied_not_inferred_from_llms_txt"}
        if self.run_check("setup", "native_http", "initialize_or_login_and_reader_read_grant", self.setup):
            self.native_models()
            self.mcp_checks()
            self.persistence()
        else:
            for model in MODELS:
                for key, transport in (("nativeModels", "native_http"), ("mcpModels", "mcp_http")):
                    self.report[key].append({"model": model, "transport": transport,
                        "assertions": ["write_then_read_synthetic_sample" if key == "nativeModels" else "read_synthetic_sample"],
                        "status": "not_available", "code": "NOT_RUN_setup_failed"})
        all_entries = self.report["checks"] + self.report["nativeModels"] + self.report["mcpModels"]
        all_entries += self.report["persistence"].get("checks", [])
        self.report["finishedUnix"] = time.time()
        self.report["counts"] = {state: sum(entry["status"] == state for entry in all_entries)
                                 for state in ("pass", "fail", "not_available")}
        self.report["status"] = "fail" if self.report["counts"]["fail"] else "pass"
        output = Path(self.args.output)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(self.report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print("验证结束 " + self.report["status"] + "；报告 " + str(output), flush=True)
        return 1 if self.report["status"] == "fail" else 0


def main():
    parser = argparse.ArgumentParser(description="SonnetDB 隔离本机原生 HTTP / 只读 MCP 真联调；不保存凭据")
    parser.add_argument("--base-url", required=True, help="隔离本机源地址，如 http://127.0.0.1:18580")
    parser.add_argument("--output", required=True, help="脱敏 JSON 验证报告路径")
    parser.add_argument("--timeout", type=float, default=15, help="单次 HTTP 超时秒数")
    parser.add_argument("--restart-container", help="显式允许重启指定隔离容器并复读；先 inspect 核验 SonnetDB 镜像及匹配 base-url 的回环端口，隔离标签不能替代端口匹配；默认不执行 Docker 命令")
    parser.add_argument("--image-version", help="操作者已核实的镜像版本，不从 llms.txt 推断")
    parser.add_argument("--image-revision", help="操作者已核实的镜像源提交")
    parser.add_argument("--image-digest", help="操作者已核实的镜像 digest")
    args = parser.parse_args()
    parsed = urllib.parse.urlsplit(args.base_url)
    if (parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or not parsed.port
            or parsed.path not in ("", "/") or parsed.query or parsed.fragment
            or parsed.username or parsed.password):
        parser.error("--base-url 必须为隔离的 http://127.0.0.1:port 源地址")
    if not 0 < args.timeout <= 60:
        parser.error("--timeout 必须在 0..60 秒内")
    if args.restart_container and (args.restart_container.startswith("-") or
                                  not all(c.isalnum() or c in "_.-" for c in args.restart_container)):
        parser.error("容器名称格式无效")
    args.base_url = args.base_url.rstrip("/")
    return Verification(args).run()


if __name__ == "__main__":
    sys.exit(main())
