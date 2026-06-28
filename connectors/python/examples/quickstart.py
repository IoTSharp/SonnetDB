from __future__ import annotations

import sys
import tempfile
import json
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import sonnetdb


def main() -> None:
    data_dir = tempfile.mkdtemp(prefix="sonnetdb-python-quickstart-")

    print("SonnetDB native version:", sonnetdb.version())

    with sonnetdb.connect(data_dir) as connection:
        connection.execute_non_query("CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)")

        inserted = connection.execute_non_query(
            "INSERT INTO cpu (time, host, usage) VALUES "
            "(1710000000000, 'edge-1', 0.42),"
            "(1710000001000, 'edge-1', 0.73)"
        )
        print("inserted rows:", inserted)

        bulk_rows = connection.execute_bulk(
            "ignored,host=edge-2 usage=0.81 1710000002000\n"
            "ignored,host=edge-2 usage=0.86 1710000003000",
            measurement="cpu",
            on_error="failfast",
            flush="false",
        )
        print("bulk rows:", bulk_rows)

        with connection.open_document_collection("devices") as documents:
            print_json("doc create", documents.create_collection('{"ifNotExists":true}'))
            print_json(
                "doc insert",
                documents.insert(
                    '{"documents":['
                    '{"id":"dev-1","document":{"site":"north","kind":"pump","score":7}},'
                    '{"id":"dev-2","document":{"site":"south","kind":"fan","score":3}}'
                    '],"ordered":true}'
                ),
            )
            print_json(
                "doc find",
                documents.find_page(
                    '{"limit":10,"filter":{"path":"$.site","op":"eq","value":"north"}}'
                ),
            )
            print_json(
                "doc update",
                documents.update(
                    '{"id":"dev-1","update":{"set":{"$.status":"ok"},"inc":{"$.score":1}}}'
                ),
            )
            print_json(
                "doc aggregate",
                documents.aggregate(
                    '[{"$match":{"path":"$.site","op":"eq","value":"north"}},'
                    '{"$group":{"keys":[{"name":"site","path":"$.site"}],'
                    '"accumulators":[{"name":"rows","op":"count"},'
                    '{"name":"total","op":"sum","path":"$.score"}]}}]'
                ),
            )
            print_json("doc delete", documents.delete('{"ids":["dev-2"],"ordered":true}'))

        with connection.open_kv("app-cache", "quickstart") as kv:
            version = kv.set("device:edge-1", b"online")
            entry = kv.get("device:edge-1")
            if entry is not None:
                print(f"kv {entry.key} = {entry.value.decode()} (version {entry.version})")

            counter, counter_version = kv.incr("counter", 3)
            print(f"kv counter: {counter} (version {counter_version})")

            cas = kv.cas("device:edge-1", version, b"offline")
            print(
                "kv cas swapped: "
                f"{cas.swapped} (current {cas.current_version}, new {cas.new_version})"
            )

            for row in kv.scan_prefix("device:", 10):
                print(f"kv scan {row.key} = {row.value.decode()}")

        with connection.execute(
            "SELECT time, host, usage FROM cpu WHERE host = 'edge-1' LIMIT 10"
        ) as result:
            print("\t".join(result.columns))
            for timestamp, host, usage in result:
                print(f"{timestamp}\t{host}\t{usage:.3f}")

    print("data directory:", data_dir)


def print_json(label: str, payload: str) -> None:
    print(f"{label}: {json.dumps(json.loads(payload), separators=(',', ':'))}")


if __name__ == "__main__":
    main()
