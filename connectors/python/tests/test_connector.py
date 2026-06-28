from __future__ import annotations

import sys
import tempfile
import unittest
import shutil
import json
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import sonnetdb


class SonnetDbPythonConnectorTests(unittest.TestCase):
    def make_temp_dir(self) -> str:
        return tempfile.mkdtemp(prefix="sonnetdb-python-test-")

    def best_effort_cleanup(self, path: str) -> None:
        shutil.rmtree(path, ignore_errors=True)

    def test_execute_and_fetch_rows(self) -> None:
        data_dir = self.make_temp_dir()
        self.addCleanup(self.best_effort_cleanup, data_dir)
        with sonnetdb.connect(data_dir) as connection:
            connection.execute_non_query(
                "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT, active FIELD BOOL)"
            )
            inserted = connection.execute_non_query(
                "INSERT INTO cpu (time, host, usage, active) VALUES "
                "(1710000000000, 'edge-1', 0.42, true),"
                "(1710000001000, 'edge-1', 0.73, false)"
            )

            self.assertEqual(2, inserted)

            with connection.execute(
                "SELECT time, host, usage, active FROM cpu WHERE host = 'edge-1' LIMIT 10"
            ) as result:
                self.assertEqual(["time", "host", "usage", "active"], result.columns)
                self.assertEqual(
                    [
                        (1710000000000, "edge-1", 0.42, True),
                        (1710000001000, "edge-1", 0.73, False),
                    ],
                    result.fetchall(),
                )

    def test_cursor_facade(self) -> None:
        data_dir = self.make_temp_dir()
        self.addCleanup(self.best_effort_cleanup, data_dir)
        with sonnetdb.connect(data_dir) as connection:
            with connection.cursor() as cursor:
                cursor.execute("CREATE MEASUREMENT m (v FIELD INT)")
                self.assertEqual(0, cursor.rowcount)

                cursor.execute("INSERT INTO m (time, v) VALUES (1, 7)")
                self.assertEqual(1, cursor.rowcount)

                cursor.execute("SELECT time, v FROM m")
                self.assertEqual(("time", "v"), tuple(col[0] for col in cursor.description or ()))
                self.assertEqual((1, 7), cursor.fetchone())
                self.assertIsNone(cursor.fetchone())

    def test_kv_keyspace_supports_basic_operations(self) -> None:
        data_dir = self.make_temp_dir()
        self.addCleanup(self.best_effort_cleanup, data_dir)
        with sonnetdb.connect(data_dir) as connection:
            with connection.open_kv("cache", "py") as kv:
                version = kv.set("device:edge-1", b"online")
                entry = kv.get("device:edge-1")

                self.assertIsNotNone(entry)
                assert entry is not None
                self.assertEqual("device:edge-1", entry.key)
                self.assertEqual(b"online", entry.value)
                self.assertEqual(version, entry.version)
                self.assertEqual(-1, kv.ttl("device:edge-1").milliseconds)

                counter, counter_version = kv.incr("counter", 3)
                self.assertEqual((3, 2), (counter, counter_version))

                swapped = kv.cas("device:edge-1", version, b"offline")
                self.assertTrue(swapped.swapped)
                self.assertEqual(version, swapped.current_version)
                self.assertGreater(swapped.new_version, version)

                rows = kv.scan_prefix("device:", 10)
                self.assertEqual([("device:edge-1", b"offline")], [(row.key, row.value) for row in rows])

                self.assertTrue(kv.delete("device:edge-1"))
                self.assertIsNone(kv.get("device:edge-1"))
                self.assertEqual(-2, kv.ttl("device:edge-1").milliseconds)

    def test_bulk_and_document_wrappers(self) -> None:
        data_dir = self.make_temp_dir()
        self.addCleanup(self.best_effort_cleanup, data_dir)
        with sonnetdb.connect(data_dir) as connection:
            connection.execute_non_query("CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)")

            rows = connection.execute_bulk(
                "ignored,host=edge-2 usage=0.81 1710000002000\n"
                "ignored,host=edge-2 usage=0.86 1710000003000",
                measurement="cpu",
                on_error="failfast",
                flush="false",
            )
            self.assertEqual(2, rows)

            with connection.open_document_collection("devices") as documents:
                created = json.loads(documents.create_collection('{"ifNotExists":true}'))
                self.assertEqual("devices", created["collection"])

                inserted = json.loads(
                    documents.insert(
                        '{"documents":['
                        '{"id":"dev-1","document":{"site":"north","score":7}},'
                        '{"id":"dev-2","document":{"site":"south","score":3}}'
                        '],"ordered":true}'
                    )
                )
                self.assertEqual(2, inserted["inserted"])

                page = json.loads(
                    documents.find_page(
                        '{"limit":10,"filter":{"path":"$.site","op":"eq","value":"north"}}'
                    )
                )
                self.assertEqual(1, page["count"])

                updated = json.loads(
                    documents.update(
                        '{"id":"dev-1","update":{"set":{"$.status":"ok"},"inc":{"$.score":1}}}'
                    )
                )
                self.assertEqual(1, updated["modified"])

                aggregate = json.loads(
                    documents.aggregate(
                        '[{"$match":{"path":"$.site","op":"eq","value":"north"}},'
                        '{"$group":{"keys":[{"name":"site","path":"$.site"}],'
                        '"accumulators":[{"name":"rows","op":"count"},'
                        '{"name":"total","op":"sum","path":"$.score"}]}}]'
                    )
                )
                self.assertEqual(1, aggregate["count"])

                deleted = json.loads(documents.delete('{"ids":["dev-2"],"ordered":true}'))
                self.assertEqual(1, deleted["deleted"])

    def test_rejects_parameters_until_native_abi_supports_them(self) -> None:
        data_dir = self.make_temp_dir()
        self.addCleanup(self.best_effort_cleanup, data_dir)
        with sonnetdb.connect(data_dir) as connection:
            with connection.cursor() as cursor:
                with self.assertRaises(sonnetdb.NotSupportedError):
                    cursor.execute("SELECT ?", [1])


if __name__ == "__main__":
    unittest.main()
