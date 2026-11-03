#!/usr/bin/env python3
"""只做本地静态检查；可按项目白名单生成 ZIP，不连接服务或读取环境变量。"""

import argparse
import json
import re
import sys
import time
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parent
PACKAGE_FILES = (
    "connector-meta.json",
    "mcp.json",
    "token-schema.json",
    "icon.svg",
    "skills/sonnetdb-skill/SKILL.md",
)
LOCAL_ONLY = {"README.md", "validate-package.py", "docker-compose.verify.yml"}
KEBAB = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*\Z")
VARIABLE = re.compile(r"\$\{([A-Z][A-Z0-9_]*)\}")
LEGACY_TOOLS = (
    "list_databases",
    "list_measurements",
    "describe_measurement",
    "sample_rows",
    "explain_sql",
    "query_sql",
    "docs_search",
    "skill_search",
    "skill_load",
)
READ_TOOLS = (
    "list_databases",
    "list_measurements",
    "describe_measurement",
    "sample_rows",
    "explain_sql",
    "query_sql",
    "docs_search",
    "skill_search",
    "skill_load",
    "list_keyspaces",
    "get_kv",
    "scan_kv",
    "list_buckets",
    "list_objects",
    "get_object",
    "list_collections",
    "get_document",
    "scan_documents",
    "list_mq_topics",
    "read_mq",
    "get_mq_stats",
    "list_tables",
    "list_graphs",
    "get_graph_vertex",
    "expand_graph",
    "search_fulltext",
    "search_vector",
    "describe_collection",
)
WRITE_TOOLS = (
    "put_kv",
    "delete_kv",
    "create_bucket",
    "delete_bucket",
    "put_object",
    "delete_object",
    "put_document",
    "delete_document",
    "publish_mq",
    "ack_mq",
    "upsert_graph_vertex",
    "delete_graph_vertex",
    "upsert_graph_edge",
    "delete_graph_edge",
    "execute_sql",
)
TOOLS = READ_TOOLS + WRITE_TOOLS


def require(condition, message):
    if not condition:
        raise ValueError(message)


def nonempty(value):
    return isinstance(value, str) and bool(value.strip())


def version(value):
    require(isinstance(value, str) and re.fullmatch(r"\d+\.\d+\.\d+", value), "版本必须为三段数字")
    return tuple(map(int, value.split(".")))


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "JSON 含重复键")
        result[key] = value
    return result


def load_json(name):
    return json.loads((ROOT / name).read_text(encoding="utf-8"), object_pairs_hook=unique_object)


def english(value):
    return nonempty(value) and bool(re.search(r"[A-Za-z]", value)) and value.isascii()


def check_files():
    for name in PACKAGE_FILES:
        require((ROOT / name).is_file(), "缺少正式包文件：" + name)
    deadline = time.monotonic() + 5
    for index, path in enumerate(ROOT.rglob("*")):
        require(index < 64 and time.monotonic() < deadline, "连接器文件检查超过数量或时间预算")
        require(not path.is_symlink(), "连接器目录不得包含符号链接")
        require(not getattr(path, "is_junction", lambda: False)(), "连接器目录不得包含目录联接")
        if path.is_file():
            require(path.stat().st_size <= 262144, "连接器文件超过静态检查字节预算")
            name = path.relative_to(ROOT).as_posix()
            # 不读取环境文件；发现即拒绝，避免把本地凭证混入连接器目录。
            require(not path.name.lower().startswith(".env"), "发现环境文件，未读取其内容")
            require(name in PACKAGE_FILES or name in LOCAL_ONLY or name == "sonnetdb-review.zip",
                    "发现非白名单文件，请移出连接器目录后重试")


def check_meta():
    meta = load_json("connector-meta.json")
    for key in ("name", "name_zh", "description", "description_zh", "source"):
        require(nonempty(meta.get(key)), "元信息缺少字段：" + key)
    require(KEBAB.fullmatch(meta["source"]), "source 必须是 kebab-case")
    for key in ("name_en", "description_en"):
        require(english(meta.get(key)), "缺少有效英文元信息：" + key)
    for key in ("examples_zh", "examples_en"):
        values = meta.get(key)
        require(isinstance(values, list) and 2 <= len(values) <= 5 and all(nonempty(v) for v in values),
                "中英文示例各需 2 至 5 条（项目策略）")
    require(all(english(v) for v in meta["examples_en"]), "英文示例必须为英文")
    require(meta.get("type") == "mcp" and meta.get("auth_mode") == "token", "本包必须使用 MCP token 模式")
    require(version(meta.get("version")) >= (0, 2, 0), "当前连接器合同至少为0.2.0")
    require(all(marker in meta["description_zh"] for marker in ("5.0", "43", "tools/list", "Read", "Write", "授权")),
            "元信息必须区分5.0工具发现、Read默认和已授权Write")
    require(all(marker in meta["description_en"] for marker in ("5.0", "43", "Read", "Write", "authorization")),
            "英文元信息缺少当前能力或授权边界")
    require(version(meta.get("minWorkbuddyVersion")) >= (4, 23, 0), "Token 模式最低版本为 4.23.0")
    require("server_auth" not in meta, "Token 模式不应包含 OAuth 密钥配置")


def check_mcp_and_fields():
    meta = load_json("connector-meta.json")
    mcp = load_json("mcp.json")
    servers = mcp.get("mcpServers")
    require(isinstance(servers, dict) and len(servers) == 1, "必须且只能有一个 MCP Server")
    key, server = next(iter(servers.items()))
    require(key == meta["source"] and KEBAB.fullmatch(key), "本包 Server key 必须与 source 一致")
    require(server.get("type") == "streamableHttp", "本包使用 streamableHttp")
    template = server.get("url", "")
    require(template.startswith("https://"), "正式 MCP URL 模板自身必须带 HTTPS 协议头")
    authority = template[len("https://"):].split("/", 1)[0]
    require(bool(authority) and "@" not in authority, "MCP URL authority 不得含 userinfo")
    require(template == "https://${SONNETDB_HOST}/mcp/${SONNETDB_DATABASE}", "MCP 主机和数据库必须由表单分别注入")
    require(server.get("headers") == {"Authorization": "Bearer ${SONNETDB_TOKEN}"}, "Bearer 必须由密码字段注入")
    require(type(server.get("timeout")) is int and server["timeout"] > 0, "timeout 必须为正整数毫秒")
    require(not ({"staticHeaders", "staticEnv", "env", "command", "preAuth"} & set(server)), "本包不应存在额外凭证或启动入口")
    schema = load_json("token-schema.json")
    for name in ("title", "description", "docLabel"):
        require(nonempty(schema.get(name)), "凭证表单缺少：" + name)
    for name in ("title_en", "description_en"):
        require(english(schema.get(name)), "凭证表单缺少英文：" + name)
    doc = urlsplit(schema.get("docUrl", ""))
    require(doc.scheme == "https" and doc.hostname and not doc.username and not doc.password,
            "docUrl 必须为无内嵌凭证的 HTTPS 文档地址")
    fields = schema.get("fields")
    require(isinstance(fields, list) and len(fields) > 0, "表单字段不能为空")
    keys = []
    for field in fields:
        field_key = field.get("key")
        require(isinstance(field_key, str) and re.fullmatch(r"[A-Z][A-Z0-9_]*", field_key), "字段 key 不合法")
        keys.append(field_key)
        require(nonempty(field.get("label")) and english(field.get("label_en")), "表单字段缺少中英文标签")
        require(field.get("type") in {"text", "password"} and field.get("required") is True, "本包字段必须为必填 text/password")
        require(field.get("defaultValue", "") == "", "不得预填生产地址或凭证")
    require(len(keys) == len(set(keys)), "表单字段 key 重复")
    references = set(VARIABLE.findall(json.dumps(mcp)))
    require(references == set(keys) == {"SONNETDB_HOST", "SONNETDB_DATABASE", "SONNETDB_TOKEN"}, "变量引用与字段 key 必须逐一匹配且无闲置项")
    by_key = {field["key"]: field for field in fields}
    require(by_key["SONNETDB_TOKEN"]["type"] == "password", "Token 必须为 password")
    require(by_key["SONNETDB_HOST"]["type"] == "text", "主机必须为 text")
    require(by_key["SONNETDB_DATABASE"]["type"] == "text", "数据库必须为 text")
    require("HTTPS" in by_key["SONNETDB_HOST"].get("description", ""), "主机指引必须要求 HTTPS")
    require(all(marker in schema["description"] for marker in ("43", "tools/list", "Read", "Write", "明确授权")),
            "表单必须区分工具发现、默认Read和明确授权Write")


def check_skill():
    text = (ROOT / "skills/sonnetdb-skill/SKILL.md").read_text(encoding="utf-8")
    require(text.startswith("---\n"), "SKILL.md 缺少 YAML frontmatter")
    parts = text.split("---\n", 2)
    require(len(parts) == 3, "SKILL.md frontmatter 未闭合")
    front = {}
    # 本包仅接受简单标量 YAML 子集，不用第三方 YAML 依赖。
    for line in parts[1].splitlines():
        key, separator, value = line.partition(":")
        require(separator and key not in front and nonempty(value), "frontmatter 必须为唯一的非空标量字段")
        front[key] = value.strip()
    require(KEBAB.fullmatch(front.get("name", "")), "Skill name 必须为 kebab-case")
    require(front["name"] == "sonnetdb-skill" and nonempty(front.get("description")), "Skill 名称或描述不完整")
    require(front.get("version") == load_json("connector-meta.json")["version"], "Skill 和连接器版本应一致")
    body = parts[2]
    for tool in LEGACY_TOOLS:
        require("### " + tool + "\n" in body, "Skill 缺少独立工具说明：" + tool)
    for tool in TOOLS:
        require(re.search(r"\b" + re.escape(tool) + r"\b", body), "Skill 缺少当前模型工具：" + tool)
    for marker in ("43工具", "用户已明确授权", "Write grant", "query_sql", "仍只读", "write_outcome_unknown",
                   "禁止盲目重试写入", "实例", "单库", "另有授权", "日志或提示词", "401", "revoked", "轮换", "最小", "不可信数据", "tools/list", "inputSchema", "outputSchema"):
        require(marker in body, "Skill 缺少权限、能力边界或错误处理说明")
    for stale in ("九个只读 MCP 工具不等于九模型 typed API", "当前 MCP contract 是只读入口",
                  "不保证每个模型可由 MCP 直接访问", "不提供写入、DDL"):
        require(stale not in body, "Skill 仍使用过期九工具只读合同")
    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    require(all(marker in readme for marker in ("43", "Read", "Write grant", "tools/list", "仍只读", "write_outcome_unknown", "另有授权")),
            "README缺少当前工具/权限/未知写入边界")


def check_svg():
    text = (ROOT / "icon.svg").read_text(encoding="utf-8")
    require("<!DOCTYPE" not in text.upper() and "<!ENTITY" not in text.upper(), "SVG 不允许 DTD 或实体")
    svg = ET.fromstring(text)
    require(svg.tag == "{http://www.w3.org/2000/svg}svg", "图标根节点必须为 SVG")
    require(svg.get("viewBox") == "0 0 64 64", "图标 viewBox 与本包设计不一致")
    for element in svg.iter():
        require(element.tag.rsplit("}", 1)[-1].lower() not in {"script", "foreignobject"}, "SVG 不允许主动内容")
        for key, value in element.attrib.items():
            local = key.rsplit("}", 1)[-1].lower()
            require(not local.startswith("on"), "SVG 不允许事件处理器")
            require(local not in {"href", "src"} or value.startswith("#"), "SVG 不允许外部资源")
            require("url(" not in value.lower(), "本包 SVG 不应包含资源引用")


def check_references_and_secrets():
    # 启发式扫描不是秘密不存在的数学证明；错误只输出路径，不打印匹配值。
    patterns = (
        re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
        re.compile(r"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b"),
        re.compile(r"\b(?:ghp_|github_pat_|sk_live_|AKIA)[A-Za-z0-9_]{16,}\b"),
        re.compile(r"Bearer\s+(?!\$\{)[A-Za-z0-9_+./=-]{20,}", re.IGNORECASE),
        re.compile(r'''["']?(?:api[_-]?key|access[_-]?token|client[_-]?secret|password|SONNETDB_TOKEN)["']?\s*[:=]\s*["'](?!\$\{)[A-Za-z0-9_+./=-]{20,}["']''', re.IGNORECASE),
    )
    for name in (*PACKAGE_FILES, *sorted(LOCAL_ONLY)):
        path = ROOT / name
        require(path.is_file(), "缺少本地说明或验证器")
        text = path.read_text(encoding="utf-8")
        require(not any(pattern.search(text) for pattern in patterns), "疑似真实凭证，已隐藏匹配内容：" + name)
        for url in re.findall(r"https?://[^\s\"'<>`)]+", text):
            parts = urlsplit(url)
            require(not parts.username and not parts.password, "URL 中疑似嵌入凭证：" + name)
            require(not re.search(r"[?&](?:token|key|secret|password)=", url, re.IGNORECASE), "URL 不应传递凭证：" + name)
        if path.suffix == ".md":
            for target in re.findall(r"\[[^\]]*\]\(([^\s)]+)\)", text):
                if target.startswith(("https://", "http://", "#")):
                    continue
                resolved = (path.parent / target.split("#", 1)[0]).resolve()
                require(resolved.is_relative_to(ROOT) and resolved.exists(), "本地文档引用缺失或越界：" + name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--zip", action="store_true", help="全部检查通过后，只打包五个正式文件到 outputs 目录")
    parser.add_argument("--output", type=Path, help="审核 ZIP 输出路径；默认 outputs/sonnetdb-workbuddy-{version}-review.zip，version 取自 connector-meta.json；相对路径基于当前工作目录")
    args = parser.parse_args()
    if args.output and not args.zip:
        parser.error("--output 必须与 --zip 一起使用")
    checks = (
        ("文件布局与白名单", check_files), ("元信息与英文文案", check_meta),
        ("MCP 配置与变量字段", check_mcp_and_fields), ("Skill frontmatter 与能力边界", check_skill),
        ("SVG XML 与主动内容", check_svg), ("本地引用与凭证启发式扫描", check_references_and_secrets),
    )
    failed = False
    for label, check in checks:
        try:
            check()
            print("PASS " + label)
        except (ValueError, TypeError, KeyError, AttributeError, OSError, ET.ParseError) as error:
            failed = True
            # 不输出异常原文中的凭证值；JSON 解码错误也可能携带输入片段。
            if isinstance(error, json.JSONDecodeError):
                detail = "JSON 解析失败（行 %d，列 %d）" % (error.lineno, error.colno)
            elif type(error) is ValueError:
                detail = str(error)
            else:
                detail = type(error).__name__
            print("FAIL " + label + "：" + detail)
    if failed:
        print("FAIL 本地静态验证未通过；未生成 ZIP")
        return 1
    print("PASS 本地静态验证；未连接真实服务、未验证运行时凭证、未上传或送审")
    if args.zip:
        package_version = load_json("connector-meta.json")["version"]
        target = (args.output or ROOT.parents[1] / "outputs" /
                  ("sonnetdb-workbuddy-" + package_version + "-review.zip")).resolve()
        if target.is_relative_to(ROOT) or target.suffix.lower() != ".zip":
            print("FAIL ZIP 必须输出到连接器源码目录之外，且使用 .zip 扩展名")
            return 1
        if not target.parent.is_dir():
            print("FAIL 输出目录不存在，请先确认并创建该目录")
            return 1
        if target.exists():
            print("FAIL 审核 ZIP 已存在，拒绝覆盖；请先人工处理旧包")
            return 1
        with zipfile.ZipFile(target, "x", compression=zipfile.ZIP_DEFLATED) as archive:
            for name in PACKAGE_FILES:
                archive.write(ROOT / name, arcname=name)
        print("PASS 仅已打包五个白名单文件；README、验证脚本、测试数据均未入包")
        print("ZIP " + str(target))
    return 0


if __name__ == "__main__":
    sys.exit(main())
