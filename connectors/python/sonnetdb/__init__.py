"""Python connector for SonnetDB over the native C ABI.

The connector is intentionally small and dependency-free. It loads the
``SonnetDB.Native`` library with :mod:`ctypes`, then exposes a Pythonic
connection/result API plus a light DB-API-style cursor facade.
"""

from __future__ import annotations

import ctypes
import os
import platform
from enum import IntEnum
from pathlib import Path
from dataclasses import dataclass
from typing import Any, Iterable, Iterator, Sequence

apilevel = "2.0"
threadsafety = 1
paramstyle = "qmark"

_NATIVE_STRING_BUFFER_SIZE = 4096
_LIBRARIES: dict[str, "_NativeLibrary"] = {}
_DLL_DIRECTORY_HANDLES: list[Any] = []


class Error(Exception):
    """Base exception for the SonnetDB Python connector."""


class InterfaceError(Error):
    """Raised when the connector cannot use a native handle or library."""


class DatabaseError(Error):
    """Raised when the native SonnetDB engine reports an execution error."""


class NotSupportedError(DatabaseError):
    """Raised for DB-API features not exposed by the current native ABI."""


class ValueType(IntEnum):
    """Value type codes exposed by the SonnetDB native C ABI."""

    NULL = 0
    INT64 = 1
    DOUBLE = 2
    BOOL = 3
    TEXT = 4


@dataclass(frozen=True)
class KvEntry:
    """Materialized KV entry."""

    key: str
    value: bytes
    version: int
    expires_at_unix_ms: int


@dataclass(frozen=True)
class KvTtl:
    """Redis-style KV TTL result."""

    milliseconds: int
    expires_at_unix_ms: int


@dataclass(frozen=True)
class KvCasResult:
    """KV compare-and-set result."""

    swapped: bool
    current_version: int
    new_version: int


@dataclass(frozen=True)
class BulkOptions:
    """Options for one synchronous bulk ingest operation."""

    measurement: str = ""
    on_error: str = ""
    flush: str = ""


class _NativeLibrary:
    def __init__(self, library_path: str | os.PathLike[str] | None = None) -> None:
        path = _resolve_library_path(library_path)
        _add_dll_directory(path.parent)
        self.path = path
        self._dll = ctypes.CDLL(str(path))
        self._bind()

    def _bind(self) -> None:
        dll = self._dll

        dll.sonnetdb_open.argtypes = [ctypes.c_char_p]
        dll.sonnetdb_open.restype = ctypes.c_void_p

        dll.sonnetdb_close.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_close.restype = None

        dll.sonnetdb_execute.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_execute.restype = ctypes.c_void_p

        dll.sonnetdb_result_free.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_result_free.restype = None

        dll.sonnetdb_bulk_create.argtypes = [ctypes.c_char_p]
        dll.sonnetdb_bulk_create.restype = ctypes.c_void_p

        dll.sonnetdb_bulk_set_measurement.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_bulk_set_measurement.restype = ctypes.c_int32

        dll.sonnetdb_bulk_set_onerror.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_bulk_set_onerror.restype = ctypes.c_int32

        dll.sonnetdb_bulk_set_flush.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_bulk_set_flush.restype = ctypes.c_int32

        dll.sonnetdb_bulk_execute.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        dll.sonnetdb_bulk_execute.restype = ctypes.c_void_p

        dll.sonnetdb_bulk_free.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_bulk_free.restype = None

        dll.sonnetdb_doc_open.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_doc_open.restype = ctypes.c_void_p

        dll.sonnetdb_doc_close.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_doc_close.restype = None

        dll.sonnetdb_doc_create_collection.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_doc_create_collection.restype = ctypes.c_void_p

        dll.sonnetdb_doc_drop_collection.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_doc_drop_collection.restype = ctypes.c_int32

        dll.sonnetdb_doc_insert.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_doc_insert.restype = ctypes.c_void_p

        dll.sonnetdb_doc_update.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_doc_update.restype = ctypes.c_void_p

        dll.sonnetdb_doc_delete.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_doc_delete.restype = ctypes.c_void_p

        dll.sonnetdb_doc_find_page.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_doc_find_page.restype = ctypes.c_void_p

        dll.sonnetdb_doc_aggregate.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_doc_aggregate.restype = ctypes.c_void_p

        dll.sonnetdb_doc_result_free.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_doc_result_free.restype = None

        dll.sonnetdb_doc_result_json_length.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_doc_result_json_length.restype = ctypes.c_int32

        dll.sonnetdb_doc_result_copy_json.argtypes = [
            ctypes.c_void_p,
            ctypes.c_void_p,
            ctypes.c_int32,
        ]
        dll.sonnetdb_doc_result_copy_json.restype = ctypes.c_int32

        dll.sonnetdb_result_records_affected.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_result_records_affected.restype = ctypes.c_int32

        dll.sonnetdb_result_column_count.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_result_column_count.restype = ctypes.c_int32

        dll.sonnetdb_result_column_name.argtypes = [ctypes.c_void_p, ctypes.c_int32]
        dll.sonnetdb_result_column_name.restype = ctypes.c_void_p

        dll.sonnetdb_result_next.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_result_next.restype = ctypes.c_int32

        dll.sonnetdb_result_value_type.argtypes = [ctypes.c_void_p, ctypes.c_int32]
        dll.sonnetdb_result_value_type.restype = ctypes.c_int32

        dll.sonnetdb_result_value_int64.argtypes = [ctypes.c_void_p, ctypes.c_int32]
        dll.sonnetdb_result_value_int64.restype = ctypes.c_int64

        dll.sonnetdb_result_value_double.argtypes = [ctypes.c_void_p, ctypes.c_int32]
        dll.sonnetdb_result_value_double.restype = ctypes.c_double

        dll.sonnetdb_result_value_bool.argtypes = [ctypes.c_void_p, ctypes.c_int32]
        dll.sonnetdb_result_value_bool.restype = ctypes.c_int32

        dll.sonnetdb_result_value_text.argtypes = [ctypes.c_void_p, ctypes.c_int32]
        dll.sonnetdb_result_value_text.restype = ctypes.c_void_p

        dll.sonnetdb_kv_open.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p]
        dll.sonnetdb_kv_open.restype = ctypes.c_void_p

        dll.sonnetdb_kv_close.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_close.restype = None

        dll.sonnetdb_kv_get.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_kv_get.restype = ctypes.c_void_p

        dll.sonnetdb_kv_set.argtypes = [
            ctypes.c_void_p,
            ctypes.c_char_p,
            ctypes.c_void_p,
            ctypes.c_int32,
            ctypes.c_int64,
        ]
        dll.sonnetdb_kv_set.restype = ctypes.c_int64

        dll.sonnetdb_kv_delete.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_kv_delete.restype = ctypes.c_int32

        dll.sonnetdb_kv_scan_prefix.argtypes = [
            ctypes.c_void_p,
            ctypes.c_char_p,
            ctypes.c_int32,
        ]
        dll.sonnetdb_kv_scan_prefix.restype = ctypes.c_void_p

        dll.sonnetdb_kv_ttl.argtypes = [
            ctypes.c_void_p,
            ctypes.c_char_p,
            ctypes.POINTER(ctypes.c_int64),
        ]
        dll.sonnetdb_kv_ttl.restype = ctypes.c_int64

        dll.sonnetdb_kv_expire_at.argtypes = [
            ctypes.c_void_p,
            ctypes.c_char_p,
            ctypes.c_int64,
        ]
        dll.sonnetdb_kv_expire_at.restype = ctypes.c_int32

        dll.sonnetdb_kv_persist.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        dll.sonnetdb_kv_persist.restype = ctypes.c_int32

        dll.sonnetdb_kv_incr.argtypes = [
            ctypes.c_void_p,
            ctypes.c_char_p,
            ctypes.c_int64,
            ctypes.POINTER(ctypes.c_int64),
            ctypes.POINTER(ctypes.c_int64),
        ]
        dll.sonnetdb_kv_incr.restype = ctypes.c_int32

        dll.sonnetdb_kv_cas.argtypes = [
            ctypes.c_void_p,
            ctypes.c_char_p,
            ctypes.c_int64,
            ctypes.c_void_p,
            ctypes.c_int32,
            ctypes.c_int64,
            ctypes.POINTER(ctypes.c_int64),
            ctypes.POINTER(ctypes.c_int64),
        ]
        dll.sonnetdb_kv_cas.restype = ctypes.c_int32

        dll.sonnetdb_kv_entry_free.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_entry_free.restype = None

        dll.sonnetdb_kv_entry_key.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_entry_key.restype = ctypes.c_void_p

        dll.sonnetdb_kv_entry_value_length.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_entry_value_length.restype = ctypes.c_int64

        dll.sonnetdb_kv_entry_copy_value.argtypes = [
            ctypes.c_void_p,
            ctypes.c_void_p,
            ctypes.c_int32,
        ]
        dll.sonnetdb_kv_entry_copy_value.restype = ctypes.c_int32

        dll.sonnetdb_kv_entry_version.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_entry_version.restype = ctypes.c_int64

        dll.sonnetdb_kv_entry_expires_at_unix_ms.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_entry_expires_at_unix_ms.restype = ctypes.c_int64

        dll.sonnetdb_kv_scan_next.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_scan_next.restype = ctypes.c_int32

        dll.sonnetdb_kv_scan_key.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_scan_key.restype = ctypes.c_void_p

        dll.sonnetdb_kv_scan_value_length.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_scan_value_length.restype = ctypes.c_int64

        dll.sonnetdb_kv_scan_copy_value.argtypes = [
            ctypes.c_void_p,
            ctypes.c_void_p,
            ctypes.c_int32,
        ]
        dll.sonnetdb_kv_scan_copy_value.restype = ctypes.c_int32

        dll.sonnetdb_kv_scan_version.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_scan_version.restype = ctypes.c_int64

        dll.sonnetdb_kv_scan_expires_at_unix_ms.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_scan_expires_at_unix_ms.restype = ctypes.c_int64

        dll.sonnetdb_kv_scan_free.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_kv_scan_free.restype = None

        dll.sonnetdb_flush.argtypes = [ctypes.c_void_p]
        dll.sonnetdb_flush.restype = ctypes.c_int32

        dll.sonnetdb_version.argtypes = [ctypes.c_void_p, ctypes.c_int32]
        dll.sonnetdb_version.restype = ctypes.c_int32

        dll.sonnetdb_last_error.argtypes = [ctypes.c_void_p, ctypes.c_int32]
        dll.sonnetdb_last_error.restype = ctypes.c_int32

    def version(self) -> str:
        return self._copy_native_string(self._dll.sonnetdb_version)

    def last_error(self) -> str:
        try:
            return self._copy_native_string(self._dll.sonnetdb_last_error)
        except Error:
            return ""

    def _copy_native_string(self, func: Any) -> str:
        buffer = ctypes.create_string_buffer(_NATIVE_STRING_BUFFER_SIZE)
        required = int(func(buffer, len(buffer)))
        if required < 0:
            raise DatabaseError(self.last_error() or "SonnetDB native string copy failed.")
        if required >= len(buffer):
            buffer = ctypes.create_string_buffer(required + 1)
            required = int(func(buffer, len(buffer)))
            if required < 0:
                raise DatabaseError(self.last_error() or "SonnetDB native string copy failed.")
        return buffer.value.decode("utf-8")


class Connection:
    """Embedded SonnetDB connection backed by the native C ABI."""

    def __init__(
        self,
        data_source: str | os.PathLike[str],
        *,
        library_path: str | os.PathLike[str] | None = None,
    ) -> None:
        text = os.fspath(data_source)
        if not text:
            raise InterfaceError("data_source must not be empty")

        self._native = _load_native_library(library_path)
        self._handle: int | None = self._native._dll.sonnetdb_open(_encode_utf8(text))
        if not self._handle:
            raise DatabaseError(self._native.last_error() or "sonnetdb_open failed.")

    def execute(self, sql: str) -> "Result":
        """Execute one SQL statement and return a forward-only result."""

        handle = self._require_handle()
        if not sql:
            raise InterfaceError("sql must not be empty")

        result = self._native._dll.sonnetdb_execute(handle, _encode_utf8(sql))
        if not result:
            raise DatabaseError(self._native.last_error() or "sonnetdb_execute failed.")
        return Result(self._native, result)

    def execute_non_query(self, sql: str) -> int:
        """Execute SQL and return the affected row count."""

        with self.execute(sql) as result:
            return result.records_affected

    def execute_bulk(
        self,
        payload: str,
        *,
        measurement: str = "",
        on_error: str = "",
        flush: str = "",
    ) -> int:
        """Synchronously ingest a bulk payload and return the affected row count."""

        handle = self._require_handle()
        if not payload:
            raise InterfaceError("bulk payload must not be empty")

        bulk = self._native._dll.sonnetdb_bulk_create(_encode_utf8(payload))
        if not bulk:
            raise DatabaseError(self._native.last_error() or "sonnetdb_bulk_create failed.")
        try:
            self._set_bulk_option(
                bulk,
                "sonnetdb_bulk_set_measurement",
                measurement,
            )
            self._set_bulk_option(bulk, "sonnetdb_bulk_set_onerror", on_error)
            self._set_bulk_option(bulk, "sonnetdb_bulk_set_flush", flush)

            result = self._native._dll.sonnetdb_bulk_execute(handle, bulk)
            if not result:
                raise DatabaseError(self._native.last_error() or "sonnetdb_bulk_execute failed.")
            with Result(self._native, result) as cursor:
                return cursor.records_affected
        finally:
            self._native._dll.sonnetdb_bulk_free(bulk)

    def query(self, sql: str) -> list[tuple[Any, ...]]:
        """Execute SQL and return all rows as tuples."""

        with self.execute(sql) as result:
            return result.fetchall()

    def flush(self) -> None:
        """Force pending data to durable storage."""

        handle = self._require_handle()
        if self._native._dll.sonnetdb_flush(handle) != 0:
            raise DatabaseError(self._native.last_error() or "sonnetdb_flush failed.")

    def cursor(self) -> "Cursor":
        """Create a light DB-API-style cursor."""

        return Cursor(self)

    def open_kv(self, keyspace: str, namespace: str = "") -> "KeyValueStore":
        """Open a KV keyspace/namespace handle."""

        handle = self._require_handle()
        if not keyspace:
            raise InterfaceError("keyspace must not be empty")
        native_handle = self._native._dll.sonnetdb_kv_open(
            handle,
            _encode_utf8(keyspace),
            _encode_utf8(namespace) if namespace else None,
        )
        if not native_handle:
            raise DatabaseError(self._native.last_error() or "sonnetdb_kv_open failed.")
        return KeyValueStore(self._native, native_handle)

    def open_document_collection(self, collection: str) -> "DocumentCollection":
        """Open a document collection handle."""

        handle = self._require_handle()
        if not collection:
            raise InterfaceError("collection must not be empty")
        native_handle = self._native._dll.sonnetdb_doc_open(handle, _encode_utf8(collection))
        if not native_handle:
            raise DatabaseError(self._native.last_error() or "sonnetdb_doc_open failed.")
        return DocumentCollection(self._native, native_handle)

    def commit(self) -> None:
        """DB-API compatibility method mapped to ``flush``."""

        self.flush()

    def rollback(self) -> None:
        """SonnetDB does not expose transactions through the native ABI."""

        raise NotSupportedError("transactions are not supported by the SonnetDB native ABI")

    def close(self) -> None:
        """Release the native connection handle. Calling close twice is safe."""

        if self._handle is None:
            return

        handle = self._handle
        self._handle = None
        self._native._dll.sonnetdb_close(handle)

    @property
    def closed(self) -> bool:
        """Whether this connection has been closed."""

        return self._handle is None

    def _require_handle(self) -> int:
        if self._handle is None:
            raise InterfaceError("SonnetDB connection is closed")
        return self._handle

    def _set_bulk_option(self, bulk: int, name: str, value: str) -> None:
        if not value:
            return
        func = getattr(self._native._dll, name)
        if int(func(bulk, _encode_utf8(value))) != 0:
            raise DatabaseError(self._native.last_error() or f"{name} failed.")

    def __enter__(self) -> "Connection":
        self._require_handle()
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:
            pass


class Result(Iterator[tuple[Any, ...]]):
    """Forward-only cursor over one SQL execution result."""

    def __init__(self, native: _NativeLibrary, handle: int) -> None:
        self._native = native
        self._handle: int | None = handle
        self._columns: list[str] | None = None

    @property
    def records_affected(self) -> int:
        """INSERT/DELETE affected rows. SELECT results return ``-1``."""

        handle = self._require_handle()
        value = int(self._native._dll.sonnetdb_result_records_affected(handle))
        if value < 0:
            message = self._native.last_error()
            if message:
                raise DatabaseError(message)
        return value

    @property
    def column_count(self) -> int:
        """Number of result columns."""

        handle = self._require_handle()
        value = int(self._native._dll.sonnetdb_result_column_count(handle))
        if value < 0:
            raise DatabaseError(
                self._native.last_error() or "sonnetdb_result_column_count failed."
            )
        return value

    @property
    def columns(self) -> list[str]:
        """Result column names."""

        if self._columns is None:
            self._columns = [self.column_name(i) for i in range(self.column_count)]
        return list(self._columns)

    def column_name(self, ordinal: int) -> str:
        """Return a result column name by zero-based ordinal."""

        ordinal = _checked_ordinal(ordinal)
        handle = self._require_handle()
        pointer = self._native._dll.sonnetdb_result_column_name(handle, ordinal)
        if not pointer:
            raise DatabaseError(
                self._native.last_error() or "sonnetdb_result_column_name failed."
            )
        return _decode_pointer(pointer)

    def next(self) -> bool:
        """Advance to the next row."""

        handle = self._require_handle()
        value = int(self._native._dll.sonnetdb_result_next(handle))
        if value < 0:
            raise DatabaseError(self._native.last_error() or "sonnetdb_result_next failed.")
        return value == 1

    def value_type(self, ordinal: int) -> ValueType:
        """Return the native value type for the current row and column."""

        ordinal = _checked_ordinal(ordinal)
        handle = self._require_handle()
        code = int(self._native._dll.sonnetdb_result_value_type(handle, ordinal))
        if code < 0:
            raise DatabaseError(
                self._native.last_error() or "sonnetdb_result_value_type failed."
            )
        try:
            return ValueType(code)
        except ValueError as ex:
            raise DatabaseError(f"unknown SonnetDB value type code: {code}") from ex

    def get_int(self, ordinal: int) -> int:
        """Read the current row value as ``int``."""

        value_type = self.value_type(ordinal)
        if value_type != ValueType.INT64:
            raise DatabaseError(f"column {ordinal} is {value_type.name}, not INT64")
        handle = self._require_handle()
        return int(self._native._dll.sonnetdb_result_value_int64(handle, ordinal))

    def get_float(self, ordinal: int) -> float:
        """Read the current row value as ``float``."""

        value_type = self.value_type(ordinal)
        if value_type not in (ValueType.DOUBLE, ValueType.INT64):
            raise DatabaseError(f"column {ordinal} is {value_type.name}, not DOUBLE")
        handle = self._require_handle()
        return float(self._native._dll.sonnetdb_result_value_double(handle, ordinal))

    def get_bool(self, ordinal: int) -> bool:
        """Read the current row value as ``bool``."""

        value_type = self.value_type(ordinal)
        if value_type != ValueType.BOOL:
            raise DatabaseError(f"column {ordinal} is {value_type.name}, not BOOL")
        handle = self._require_handle()
        value = int(self._native._dll.sonnetdb_result_value_bool(handle, ordinal))
        if value < 0:
            raise DatabaseError(
                self._native.last_error() or "sonnetdb_result_value_bool failed."
            )
        return value != 0

    def get_text(self, ordinal: int) -> str | None:
        """Read the current row value as UTF-8 text. NULL returns ``None``."""

        value_type = self.value_type(ordinal)
        if value_type == ValueType.NULL:
            return None
        handle = self._require_handle()
        pointer = self._native._dll.sonnetdb_result_value_text(
            handle, _checked_ordinal(ordinal)
        )
        if not pointer:
            raise DatabaseError(
                self._native.last_error() or "sonnetdb_result_value_text failed."
            )
        return _decode_pointer(pointer)

    def get_value(self, ordinal: int) -> Any:
        """Read the current row value using a natural Python type."""

        value_type = self.value_type(ordinal)
        if value_type == ValueType.NULL:
            return None
        if value_type == ValueType.INT64:
            return self.get_int(ordinal)
        if value_type == ValueType.DOUBLE:
            return self.get_float(ordinal)
        if value_type == ValueType.BOOL:
            return self.get_bool(ordinal)
        if value_type == ValueType.TEXT:
            return self.get_text(ordinal)
        raise DatabaseError(f"unsupported SonnetDB value type: {value_type!r}")

    def row(self) -> tuple[Any, ...]:
        """Read the current row as a tuple."""

        return tuple(self.get_value(i) for i in range(self.column_count))

    def fetchone(self) -> tuple[Any, ...] | None:
        """Fetch one row, or ``None`` when the cursor is exhausted."""

        if not self.next():
            return None
        return self.row()

    def fetchmany(self, size: int = 1) -> list[tuple[Any, ...]]:
        """Fetch up to ``size`` rows."""

        if size < 0:
            raise InterfaceError("size must be non-negative")
        rows: list[tuple[Any, ...]] = []
        for _ in range(size):
            row = self.fetchone()
            if row is None:
                break
            rows.append(row)
        return rows

    def fetchall(self) -> list[tuple[Any, ...]]:
        """Fetch all remaining rows."""

        rows: list[tuple[Any, ...]] = []
        while True:
            row = self.fetchone()
            if row is None:
                return rows
            rows.append(row)

    def close(self) -> None:
        """Release the native result handle. Calling close twice is safe."""

        if self._handle is None:
            return

        handle = self._handle
        self._handle = None
        self._native._dll.sonnetdb_result_free(handle)
        message = self._native.last_error()
        if message:
            raise DatabaseError(message)

    @property
    def closed(self) -> bool:
        """Whether this result has been closed."""

        return self._handle is None

    def _require_handle(self) -> int:
        if self._handle is None:
            raise InterfaceError("SonnetDB result is closed")
        return self._handle

    def __iter__(self) -> "Result":
        return self

    def __next__(self) -> tuple[Any, ...]:
        row = self.fetchone()
        if row is None:
            raise StopIteration
        return row

    def __enter__(self) -> "Result":
        self._require_handle()
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:
            pass


class KeyValueStore:
    """KV keyspace/namespace handle backed by the native C ABI."""

    def __init__(self, native: _NativeLibrary, handle: int) -> None:
        self._native = native
        self._handle: int | None = handle

    def get(self, key: str) -> KvEntry | None:
        """Read a KV entry, or ``None`` when the key is missing."""

        handle = self._require_handle()
        if not key:
            raise InterfaceError("key must not be empty")
        entry = self._native._dll.sonnetdb_kv_get(handle, _encode_utf8(key))
        if not entry:
            message = self._native.last_error()
            if message:
                raise DatabaseError(message)
            return None
        try:
            return self._entry_from_handle(entry)
        finally:
            self._native._dll.sonnetdb_kv_entry_free(entry)

    def set(
        self,
        key: str,
        value: bytes | bytearray | memoryview,
        expires_at_unix_ms: int = -1,
    ) -> int:
        """Write a binary value and return the written version."""

        handle = self._require_handle()
        if not key:
            raise InterfaceError("key must not be empty")
        buffer = bytes(value)
        native_buffer = ctypes.create_string_buffer(buffer, len(buffer)) if buffer else None
        ptr = ctypes.cast(native_buffer, ctypes.c_void_p) if native_buffer is not None else None
        version = int(
            self._native._dll.sonnetdb_kv_set(
                handle,
                _encode_utf8(key),
                ptr,
                _checked_byte_length(buffer),
                int(expires_at_unix_ms),
            )
        )
        if version < 0:
            raise DatabaseError(self._native.last_error() or "sonnetdb_kv_set failed.")
        return version

    def delete(self, key: str) -> bool:
        """Delete a key and return whether a value was removed."""

        code = self._bool_call("sonnetdb_kv_delete", key)
        return code

    def scan_prefix(self, prefix: str = "", limit: int = 0) -> list[KvEntry]:
        """Scan a prefix into a materialized list. ``limit <= 0`` uses the default."""

        handle = self._require_handle()
        scan = self._native._dll.sonnetdb_kv_scan_prefix(
            handle,
            _encode_utf8(prefix),
            _checked_int32(limit, "limit"),
        )
        if not scan:
            raise DatabaseError(self._native.last_error() or "sonnetdb_kv_scan_prefix failed.")
        try:
            entries: list[KvEntry] = []
            while True:
                next_row = int(self._native._dll.sonnetdb_kv_scan_next(scan))
                if next_row < 0:
                    raise DatabaseError(self._native.last_error() or "sonnetdb_kv_scan_next failed.")
                if next_row == 0:
                    return entries
                entries.append(self._entry_from_scan(scan))
        finally:
            self._native._dll.sonnetdb_kv_scan_free(scan)

    def ttl(self, key: str) -> KvTtl:
        """Return remaining TTL in milliseconds."""

        handle = self._require_handle()
        if not key:
            raise InterfaceError("key must not be empty")
        expires = ctypes.c_int64(-1)
        milliseconds = int(
            self._native._dll.sonnetdb_kv_ttl(handle, _encode_utf8(key), ctypes.byref(expires))
        )
        if milliseconds < -2:
            raise DatabaseError(self._native.last_error() or "sonnetdb_kv_ttl failed.")
        return KvTtl(milliseconds, int(expires.value))

    def expire_at(self, key: str, expires_at_unix_ms: int) -> bool:
        """Set an absolute UTC expiration time in Unix milliseconds."""

        return self._bool_call("sonnetdb_kv_expire_at", key, int(expires_at_unix_ms))

    def persist(self, key: str) -> bool:
        """Remove a key expiration."""

        return self._bool_call("sonnetdb_kv_persist", key)

    def incr(self, key: str, delta: int = 1) -> tuple[int, int]:
        """Atomically increment a UTF-8 integer value and return value/version."""

        handle = self._require_handle()
        if not key:
            raise InterfaceError("key must not be empty")
        value = ctypes.c_int64(0)
        version = ctypes.c_int64(0)
        code = int(
            self._native._dll.sonnetdb_kv_incr(
                handle,
                _encode_utf8(key),
                int(delta),
                ctypes.byref(value),
                ctypes.byref(version),
            )
        )
        if code != 0:
            raise DatabaseError(self._native.last_error() or "sonnetdb_kv_incr failed.")
        return int(value.value), int(version.value)

    def cas(
        self,
        key: str,
        expected_version: int,
        value: bytes | bytearray | memoryview,
        expires_at_unix_ms: int = -1,
    ) -> KvCasResult:
        """Compare a key version and swap in a new value on match."""

        handle = self._require_handle()
        if not key:
            raise InterfaceError("key must not be empty")
        buffer = bytes(value)
        native_buffer = ctypes.create_string_buffer(buffer, len(buffer)) if buffer else None
        ptr = ctypes.cast(native_buffer, ctypes.c_void_p) if native_buffer is not None else None
        current = ctypes.c_int64(0)
        new = ctypes.c_int64(-1)
        code = int(
            self._native._dll.sonnetdb_kv_cas(
                handle,
                _encode_utf8(key),
                int(expected_version),
                ptr,
                _checked_byte_length(buffer),
                int(expires_at_unix_ms),
                ctypes.byref(current),
                ctypes.byref(new),
            )
        )
        if code < 0:
            raise DatabaseError(self._native.last_error() or "sonnetdb_kv_cas failed.")
        return KvCasResult(code == 1, int(current.value), int(new.value))

    def close(self) -> None:
        """Release the native KV handle. Calling close twice is safe."""

        if self._handle is None:
            return
        handle = self._handle
        self._handle = None
        self._native._dll.sonnetdb_kv_close(handle)
        message = self._native.last_error()
        if message:
            raise DatabaseError(message)

    @property
    def closed(self) -> bool:
        """Whether this KV handle has been closed."""

        return self._handle is None

    def _bool_call(self, name: str, key: str, *extra: int) -> bool:
        handle = self._require_handle()
        if not key:
            raise InterfaceError("key must not be empty")
        func = getattr(self._native._dll, name)
        code = int(func(handle, _encode_utf8(key), *extra))
        if code < 0:
            raise DatabaseError(self._native.last_error() or f"{name} failed.")
        return code == 1

    def _entry_from_handle(self, entry: int) -> KvEntry:
        key = self._native._dll.sonnetdb_kv_entry_key(entry)
        if not key:
            raise DatabaseError(self._native.last_error() or "sonnetdb_kv_entry_key failed.")
        return KvEntry(
            _decode_pointer(key),
            self._copy_entry_value(entry),
            int(self._native._dll.sonnetdb_kv_entry_version(entry)),
            int(self._native._dll.sonnetdb_kv_entry_expires_at_unix_ms(entry)),
        )

    def _entry_from_scan(self, scan: int) -> KvEntry:
        key = self._native._dll.sonnetdb_kv_scan_key(scan)
        if not key:
            raise DatabaseError(self._native.last_error() or "sonnetdb_kv_scan_key failed.")
        return KvEntry(
            _decode_pointer(key),
            self._copy_scan_value(scan),
            int(self._native._dll.sonnetdb_kv_scan_version(scan)),
            int(self._native._dll.sonnetdb_kv_scan_expires_at_unix_ms(scan)),
        )

    def _copy_entry_value(self, entry: int) -> bytes:
        length = int(self._native._dll.sonnetdb_kv_entry_value_length(entry))
        if length < 0:
            raise DatabaseError(
                self._native.last_error() or "sonnetdb_kv_entry_value_length failed."
            )
        if length == 0:
            return b""
        buffer = ctypes.create_string_buffer(length)
        copied = int(self._native._dll.sonnetdb_kv_entry_copy_value(entry, buffer, length))
        if copied < 0:
            raise DatabaseError(
                self._native.last_error() or "sonnetdb_kv_entry_copy_value failed."
            )
        return bytes(buffer.raw)

    def _copy_scan_value(self, scan: int) -> bytes:
        length = int(self._native._dll.sonnetdb_kv_scan_value_length(scan))
        if length < 0:
            raise DatabaseError(
                self._native.last_error() or "sonnetdb_kv_scan_value_length failed."
            )
        if length == 0:
            return b""
        buffer = ctypes.create_string_buffer(length)
        copied = int(self._native._dll.sonnetdb_kv_scan_copy_value(scan, buffer, length))
        if copied < 0:
            raise DatabaseError(
                self._native.last_error() or "sonnetdb_kv_scan_copy_value failed."
            )
        return bytes(buffer.raw)

    def _require_handle(self) -> int:
        if self._handle is None:
            raise InterfaceError("SonnetDB KV handle is closed")
        return self._handle

    def __enter__(self) -> "KeyValueStore":
        self._require_handle()
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:
            pass


class DocumentCollection:
    """Document collection handle backed by the native C ABI."""

    def __init__(self, native: _NativeLibrary, handle: int) -> None:
        self._native = native
        self._handle: int | None = handle

    def create_collection(self, options_json: str = "") -> str:
        """Create the collection and return the native JSON response."""

        return self._execute_json(
            "sonnetdb_doc_create_collection",
            options_json,
            required=False,
        )

    def drop_collection(self) -> bool:
        """Drop the collection and return whether it existed."""

        handle = self._require_handle()
        code = int(self._native._dll.sonnetdb_doc_drop_collection(handle))
        if code < 0:
            raise DatabaseError(self._native.last_error() or "sonnetdb_doc_drop_collection failed.")
        return code == 1

    def insert(self, payload_json: str) -> str:
        """Insert one or more documents from a JSON request."""

        return self._execute_json("sonnetdb_doc_insert", payload_json)

    def update(self, payload_json: str) -> str:
        """Update documents from a JSON request."""

        return self._execute_json("sonnetdb_doc_update", payload_json)

    def delete(self, payload_json: str) -> str:
        """Delete documents from a JSON request."""

        return self._execute_json("sonnetdb_doc_delete", payload_json)

    def find_page(self, payload_json: str = "") -> str:
        """Find a page of documents and return the native JSON response."""

        return self._execute_json("sonnetdb_doc_find_page", payload_json, required=False)

    def aggregate(self, payload_json: str) -> str:
        """Run a document aggregation pipeline and return the native JSON response."""

        return self._execute_json("sonnetdb_doc_aggregate", payload_json)

    def close(self) -> None:
        """Release the native document handle. Calling close twice is safe."""

        if self._handle is None:
            return
        handle = self._handle
        self._handle = None
        self._native._dll.sonnetdb_doc_close(handle)
        message = self._native.last_error()
        if message:
            raise DatabaseError(message)

    @property
    def closed(self) -> bool:
        """Whether this document collection handle has been closed."""

        return self._handle is None

    def _execute_json(self, name: str, payload_json: str, *, required: bool = True) -> str:
        handle = self._require_handle()
        if required and not payload_json:
            raise InterfaceError("document JSON payload must not be empty")

        func = getattr(self._native._dll, name)
        result = func(handle, _encode_utf8(payload_json) if payload_json else None)
        if not result:
            raise DatabaseError(self._native.last_error() or f"{name} failed.")
        try:
            return self._copy_result_json(result, name)
        finally:
            self._native._dll.sonnetdb_doc_result_free(result)

    def _copy_result_json(self, result: int, name: str) -> str:
        length = int(self._native._dll.sonnetdb_doc_result_json_length(result))
        if length < 0:
            raise DatabaseError(self._native.last_error() or f"{name} result length failed.")
        buffer = ctypes.create_string_buffer(length + 1)
        copied = int(self._native._dll.sonnetdb_doc_result_copy_json(result, buffer, length + 1))
        if copied < 0:
            raise DatabaseError(self._native.last_error() or f"{name} result copy failed.")
        return buffer.value.decode("utf-8")

    def _require_handle(self) -> int:
        if self._handle is None:
            raise InterfaceError("SonnetDB document collection is closed")
        return self._handle

    def __enter__(self) -> "DocumentCollection":
        self._require_handle()
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:
            pass


class Cursor:
    """Small DB-API-style cursor wrapper over ``Connection.execute``."""

    arraysize = 1

    def __init__(self, connection: Connection) -> None:
        self.connection = connection
        self._result: Result | None = None
        self.description: tuple[tuple[Any, ...], ...] | None = None
        self.rowcount = -1

    def execute(self, sql: str, parameters: Sequence[Any] | dict[str, Any] | None = None) -> "Cursor":
        """Execute SQL.

        The current native ABI accepts a single SQL string, so non-empty
        ``parameters`` are rejected instead of interpolated.
        """

        self._ensure_parameters_empty(parameters)
        self.close_result()
        self._result = self.connection.execute(sql)
        self.rowcount = self._result.records_affected
        columns = self._result.columns
        self.description = tuple((name, None, None, None, None, None, None) for name in columns)
        return self

    def fetchone(self) -> tuple[Any, ...] | None:
        result = self._require_result()
        return result.fetchone()

    def fetchmany(self, size: int | None = None) -> list[tuple[Any, ...]]:
        result = self._require_result()
        return result.fetchmany(self.arraysize if size is None else size)

    def fetchall(self) -> list[tuple[Any, ...]]:
        result = self._require_result()
        return result.fetchall()

    def close_result(self) -> None:
        if self._result is not None:
            self._result.close()
            self._result = None

    def close(self) -> None:
        self.close_result()

    def _require_result(self) -> Result:
        if self._result is None:
            raise InterfaceError("cursor has no active result")
        return self._result

    @staticmethod
    def _ensure_parameters_empty(
        parameters: Sequence[Any] | dict[str, Any] | None,
    ) -> None:
        if parameters is None:
            return
        if isinstance(parameters, dict):
            has_parameters = len(parameters) > 0
        else:
            has_parameters = len(parameters) > 0
        if has_parameters:
            raise NotSupportedError("SQL parameters are not supported by the native ABI")

    def __enter__(self) -> "Cursor":
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        self.close()


def connect(
    data_source: str | os.PathLike[str],
    *,
    library_path: str | os.PathLike[str] | None = None,
) -> Connection:
    """Open an embedded SonnetDB database directory."""

    return Connection(data_source, library_path=library_path)


open = connect


def version(*, library_path: str | os.PathLike[str] | None = None) -> str:
    """Return the loaded SonnetDB native library version."""

    return _load_native_library(library_path).version()


def last_error(*, library_path: str | os.PathLike[str] | None = None) -> str:
    """Return the last native error for the current thread."""

    return _load_native_library(library_path).last_error()


def _load_native_library(
    library_path: str | os.PathLike[str] | None = None,
) -> _NativeLibrary:
    path = str(_resolve_library_path(library_path))
    existing = _LIBRARIES.get(path)
    if existing is not None:
        return existing

    library = _NativeLibrary(path)
    _LIBRARIES[path] = library
    return library


def _resolve_library_path(library_path: str | os.PathLike[str] | None) -> Path:
    explicit = library_path or os.environ.get("SONNETDB_NATIVE_LIBRARY")
    if explicit:
        path = Path(explicit).expanduser().resolve()
        if path.is_dir():
            path = path / _native_library_name()
        if path.exists():
            return path
        raise InterfaceError(f"SonnetDB native library not found: {path}")

    library_dir = os.environ.get("SONNETDB_NATIVE_LIB_DIR")
    candidates: list[Path] = []
    if library_dir:
        candidates.append(Path(library_dir).expanduser() / _native_library_name())

    candidates.extend(_default_library_candidates())
    for candidate in candidates:
        resolved = candidate.resolve()
        if resolved.exists():
            return resolved

    searched = "\n".join(f"  - {candidate}" for candidate in candidates)
    raise InterfaceError(
        "SonnetDB native library was not found. Build connectors/c first or set "
        "SONNETDB_NATIVE_LIBRARY / SONNETDB_NATIVE_LIB_DIR.\nSearched:\n" + searched
    )


def _default_library_candidates() -> list[Path]:
    name = _native_library_name()
    rid = _runtime_identifier()
    package_root = Path(__file__).resolve().parents[1]
    repo_root = Path(__file__).resolve().parents[3]
    cwd = Path.cwd()

    return [
        package_root / name,
        cwd / name,
        repo_root / "artifacts" / "connectors" / "c" / rid / name,
        repo_root / "artifacts" / "connectors" / "c" / rid / "Release" / name,
        repo_root / "artifacts" / "connectors" / "c" / rid / "native" / rid / "publish" / name,
        repo_root / "artifacts" / "connectors" / "c" / "dotnet-publish-win-x64" / name,
        repo_root / "connectors" / "c" / "native" / "SonnetDB.Native" / "bin"
        / "Release" / "net10.0" / rid / "native" / name,
    ]


def _native_library_name() -> str:
    system = platform.system().lower()
    if system == "windows":
        return "SonnetDB.Native.dll"
    if system == "linux":
        return "SonnetDB.Native.so"
    raise InterfaceError(f"unsupported platform for SonnetDB native library: {platform.system()}")


def _runtime_identifier() -> str:
    system = platform.system().lower()
    machine = platform.machine().lower()
    if machine in ("amd64", "x86_64"):
        arch = "x64"
    elif machine in ("arm64", "aarch64"):
        arch = "arm64"
    elif machine in ("x86", "i386", "i686"):
        arch = "x86"
    else:
        arch = machine

    if system == "windows":
        return f"win-{arch}"
    if system == "linux":
        return f"linux-{arch}"
    return f"{system}-{arch}"


def _add_dll_directory(directory: Path) -> None:
    if hasattr(os, "add_dll_directory") and platform.system().lower() == "windows":
        _DLL_DIRECTORY_HANDLES.append(os.add_dll_directory(str(directory)))


def _encode_utf8(value: str) -> bytes:
    if "\x00" in value:
        raise InterfaceError("strings passed to the native ABI must not contain NUL bytes")
    return value.encode("utf-8")


def _decode_pointer(pointer: int) -> str:
    return ctypes.cast(pointer, ctypes.c_char_p).value.decode("utf-8")


def _checked_ordinal(ordinal: int) -> int:
    if ordinal < 0 or ordinal > 2_147_483_647:
        raise InterfaceError(f"column ordinal {ordinal} is out of range")
    return int(ordinal)


def _checked_int32(value: int, name: str) -> int:
    if value < -2_147_483_648 or value > 2_147_483_647:
        raise InterfaceError(f"{name} {value} is out of range")
    return int(value)


def _checked_byte_length(value: bytes) -> int:
    return _checked_int32(len(value), "byte length")


__all__ = [
    "BulkOptions",
    "Connection",
    "Cursor",
    "DatabaseError",
    "DocumentCollection",
    "Error",
    "InterfaceError",
    "KeyValueStore",
    "KvCasResult",
    "KvEntry",
    "KvTtl",
    "NotSupportedError",
    "Result",
    "ValueType",
    "apilevel",
    "connect",
    "last_error",
    "open",
    "paramstyle",
    "threadsafety",
    "version",
]
