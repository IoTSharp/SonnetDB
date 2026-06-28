from __future__ import annotations

import sys
import tempfile
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


if __name__ == "__main__":
    main()
