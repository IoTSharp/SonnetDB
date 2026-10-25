"""SonnetDB SQL Execute frame codec.

The codec is transport neutral: callers may send the returned bytes through a
native HTTP/2 Frame transport, while this module keeps the payload contract
identical to ``SonnetDB.Protocol.SqlExecuteFrameCodec``.  It deliberately does
not implement HTTP/1 or REST fallback.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import IntEnum
import math
import struct
from typing import Any, Iterable, Sequence


FRAME_HEADER_SIZE = 12
FRAME_VERSION = 1
SQL_SERVICE = 3
SQL_EXECUTE_OPERATION = 2
RESPONSE_FLAG = 1
MAX_FRAME_PAYLOAD_BYTES = 132 * 1024 * 1024
CURRENT_VERSION = 1
MAX_REQUEST_PAYLOAD_BYTES = 4 * 1024 * 1024
MAX_PARAMETER_COUNT = 256
MAX_NAME_BYTES = 512
MAX_SQL_BYTES = 1024 * 1024
MAX_VALUE_BYTES = MAX_REQUEST_PAYLOAD_BYTES
MAX_VECTOR_DIMENSION = 1_000_000
_COMMITTED_OUTCOME = 1
_DATETIME_MAX_TICKS = 3155378975999999999
_TICKS_PER_DAY = 86400 * 10_000_000


class FrameFormatError(ValueError):
    """Raised when a frame or SQL Execute payload violates its contract."""


class FrameParameterKind(IntEnum):
    """Exact SQL Execute parameter type codes."""

    NULL = 0
    INT64 = 1
    FLOAT64 = 2
    BOOLEAN = 3
    STRING = 4
    BLOB = 5
    TIMESTAMP = 6
    DECIMAL = 7
    DATETIME_OFFSET = 8
    DURATION = 9
    TIME = 10
    VECTOR = 11
    DATETIME = 12


@dataclass(frozen=True)
class DecimalBits:
    """A decimal represented by the four values returned by ``decimal.GetBits``."""

    bits: tuple[int, int, int, int]

    def __post_init__(self) -> None:
        if len(self.bits) != 4 or any(value < -2**31 or value > 2**31 - 1 for value in self.bits):
            raise ValueError("DecimalBits must contain four Int32 values.")
        flags = self.bits[3] & 0xFFFFFFFF
        if flags & 0x7F00FFFF or ((flags >> 16) & 0xFF) > 28:
            raise ValueError("DecimalBits flags must use a scale from 0 to 28 and no reserved bits.")


@dataclass(frozen=True)
class FrameParameter:
    """One named or positional SQL Execute parameter."""

    name: str
    kind: FrameParameterKind
    value: Any = None


@dataclass(frozen=True)
class SqlExecuteRequest:
    """Decoded SQL Execute payload."""

    database: str
    sql: str
    parameters: tuple[FrameParameter, ...]


@dataclass(frozen=True)
class SqlExecuteResponse:
    """Decoded committed SQL Execute response."""

    records_affected: int


def encode_sql_execute_frame(
    stream_id: int,
    database: str,
    sql: str,
    parameters: Sequence[FrameParameter] | None = None,
) -> bytes:
    """Encode a complete v1 SQL Execute request frame."""

    if not isinstance(stream_id, int) or not 0 <= stream_id <= 0xFFFFFFFF:
        raise ValueError("stream_id must be an unsigned 32-bit integer")
    parameter_values = tuple(parameters or ())
    _validate_utf8(database, MAX_NAME_BYTES, "database")
    _validate_utf8(sql, MAX_SQL_BYTES, "sql")
    if not sql:
        raise ValueError("sql must not be empty")
    if len(parameter_values) > MAX_PARAMETER_COUNT:
        raise ValueError("parameter count exceeds the protocol limit")
    _validate_bindings(parameter_values)

    for parameter in parameter_values:
        _validate_parameter(parameter)
    payload_length = 1 + _measure_string(database) + _measure_string(sql) + _measure_varuint(len(parameter_values))
    for parameter in parameter_values:
        payload_length += _measure_string(parameter.name) + 2 + _measure_value(parameter)
    if payload_length > MAX_REQUEST_PAYLOAD_BYTES:
        raise ValueError("SQL Execute payload exceeds the protocol limit")

    payload = bytearray()
    payload.append(CURRENT_VERSION)
    _write_string(payload, database)
    _write_string(payload, sql)
    _write_varuint(payload, len(parameter_values))
    for parameter in parameter_values:
        _write_string(payload, parameter.name)
        payload.append(int(parameter.kind))
        has_value = parameter.value is not None
        payload.append(1 if has_value else 0)
        if has_value:
            _write_value(payload, parameter.kind, parameter.value)

    return _encode_header(len(payload), SQL_SERVICE, SQL_EXECUTE_OPERATION, 0, stream_id) + bytes(payload)


def decode_sql_execute_payload(payload: bytes | bytearray | memoryview) -> SqlExecuteRequest:
    """Decode a SQL Execute payload without its 12-byte frame header."""

    reader = _Reader(bytes(payload))
    if reader.remaining > MAX_REQUEST_PAYLOAD_BYTES:
        raise FrameFormatError("SQL Execute payload exceeds the protocol limit")
    if reader.read_byte() != CURRENT_VERSION:
        raise FrameFormatError("unsupported SQL Execute payload version")
    database = reader.read_string(MAX_NAME_BYTES, "database")
    sql = reader.read_string(MAX_SQL_BYTES, "sql")
    if not sql:
        raise FrameFormatError("sql must not be empty")
    count = reader.read_varuint()
    if count > MAX_PARAMETER_COUNT:
        raise FrameFormatError("parameter count exceeds the protocol limit")
    parameters: list[FrameParameter] = []
    for _ in range(count):
        name = reader.read_string(MAX_NAME_BYTES, "parameter name")
        try:
            kind = FrameParameterKind(reader.read_byte())
        except ValueError as error:
            raise FrameFormatError("unknown SQL Execute parameter type") from error
        has_value = reader.read_bool()
        if kind is FrameParameterKind.NULL and has_value:
            raise FrameFormatError("NULL parameter must not contain a value")
        value = _read_value(reader, kind) if has_value else None
        parameters.append(FrameParameter(name, kind, value))
    if reader.remaining:
        raise FrameFormatError("SQL Execute payload contains trailing bytes")
    _validate_bindings(tuple(parameters), decode=True)
    return SqlExecuteRequest(database, sql, tuple(parameters))


def decode_sql_execute_frame(frame: bytes | bytearray | memoryview) -> SqlExecuteRequest:
    """Validate a complete request frame and decode its SQL Execute payload."""

    payload = _read_frame(frame, service=SQL_SERVICE, operation=SQL_EXECUTE_OPERATION, flags=0)
    return decode_sql_execute_payload(payload)


def encode_sql_execute_response_frame(stream_id: int, records_affected: int) -> bytes:
    """Encode a complete committed SQL Execute response frame."""

    if not isinstance(records_affected, int) or records_affected < 0 or records_affected > 2**63 - 1:
        raise ValueError("records_affected must be a non-negative Int64")
    payload = bytes((CURRENT_VERSION,)) + struct.pack("<qB", records_affected, _COMMITTED_OUTCOME)
    return _encode_header(len(payload), SQL_SERVICE, SQL_EXECUTE_OPERATION, RESPONSE_FLAG, stream_id) + payload


def decode_sql_execute_response_payload(payload: bytes | bytearray | memoryview) -> SqlExecuteResponse:
    """Decode and validate a committed SQL Execute response payload."""

    data = bytes(payload)
    if len(data) != 10 or data[0] != CURRENT_VERSION:
        raise FrameFormatError("invalid SQL Execute response payload")
    records_affected, outcome = struct.unpack_from("<qB", data, 1)
    if records_affected < 0 or outcome != _COMMITTED_OUTCOME:
        raise FrameFormatError("SQL Execute response is not a committed terminal state")
    return SqlExecuteResponse(records_affected)


def decode_sql_execute_response_frame(frame: bytes | bytearray | memoryview) -> SqlExecuteResponse:
    """Validate and decode a complete committed response frame."""

    payload = _read_frame(frame, service=SQL_SERVICE, operation=SQL_EXECUTE_OPERATION, flags=RESPONSE_FLAG)
    return decode_sql_execute_response_payload(payload)


def _encode_header(payload_length: int, service: int, operation: int, flags: int, stream_id: int) -> bytes:
    return struct.pack("<IBBBBI", payload_length, FRAME_VERSION, service, operation, flags, stream_id)


def _read_frame(frame: bytes | bytearray | memoryview, service: int, operation: int, flags: int) -> bytes:
    data = bytes(frame)
    if len(data) < FRAME_HEADER_SIZE:
        raise FrameFormatError("frame is shorter than its header")
    payload_length, version, actual_service, actual_operation, actual_flags, _ = struct.unpack_from("<IBBBBI", data)
    if version != FRAME_VERSION or actual_service != service or actual_operation != operation or actual_flags != flags:
        raise FrameFormatError("frame identity does not match SQL Execute")
    if payload_length > MAX_FRAME_PAYLOAD_BYTES or len(data) != FRAME_HEADER_SIZE + payload_length:
        raise FrameFormatError("frame payload length is invalid")
    return data[FRAME_HEADER_SIZE:]


def _validate_utf8(value: str, maximum: int, label: str) -> bytes:
    if not isinstance(value, str):
        raise TypeError(f"{label} must be str")
    encoded = value.encode("utf-8", "strict")
    if len(encoded) > maximum:
        raise ValueError(f"{label} exceeds its UTF-8 byte limit")
    return encoded


def _write_varuint(target: bytearray, value: int) -> None:
    if not 0 <= value <= 0xFFFFFFFF:
        raise ValueError("varuint32 value out of range")
    while value >= 0x80:
        target.append((value & 0x7F) | 0x80)
        value >>= 7
    target.append(value)


def _write_string(target: bytearray, value: str) -> None:
    encoded = _validate_utf8(value, 0xFFFFFFFF, "string")
    _write_varuint(target, len(encoded))
    target.extend(encoded)


class _Reader:
    def __init__(self, data: bytes) -> None:
        self._data = data
        self._offset = 0

    @property
    def remaining(self) -> int:
        return len(self._data) - self._offset

    def _take(self, length: int) -> bytes:
        if length < 0 or self.remaining < length:
            raise FrameFormatError("payload ended before the value was complete")
        value = self._data[self._offset:self._offset + length]
        self._offset += length
        return value

    def read_byte(self) -> int:
        return self._take(1)[0]

    def read_bool(self) -> bool:
        value = self.read_byte()
        if value not in (0, 1):
            raise FrameFormatError("boolean values must be 0 or 1")
        return bool(value)

    def read_varuint(self) -> int:
        value = 0
        for index in range(5):
            byte = self.read_byte()
            value |= (byte & 0x7F) << (7 * index)
            if not byte & 0x80:
                if value > 0xFFFFFFFF:
                    raise FrameFormatError("varuint32 value out of range")
                return value
        raise FrameFormatError("varuint32 uses more than five bytes")

    def read_string(self, maximum: int, label: str) -> str:
        length = self.read_varuint()
        if length > maximum:
            raise FrameFormatError(f"{label} exceeds its byte limit")
        try:
            return self._take(length).decode("utf-8", "strict")
        except UnicodeDecodeError as error:
            raise FrameFormatError(f"{label} is not valid UTF-8") from error

    def read_i64(self) -> int:
        return struct.unpack("<q", self._take(8))[0]


def _validate_bindings(parameters: Sequence[FrameParameter], decode: bool = False) -> None:
    named: bool | None = None
    names: set[str] = set()
    for parameter in parameters:
        if not isinstance(parameter, FrameParameter):
            raise FrameFormatError("parameter has an invalid shape") if decode else TypeError("parameters must be FrameParameter")
        is_named = bool(parameter.name)
        if named is not None and named != is_named:
            raise FrameFormatError("named and positional SQL parameters cannot be mixed") if decode else ValueError("named and positional SQL parameters cannot be mixed")
        named = is_named
        if is_named:
            _validate_utf8(parameter.name, MAX_NAME_BYTES, "parameter name")
            if not parameter.name.isascii():
                error = "parameter names must contain ASCII characters"
                raise FrameFormatError(error) if decode else ValueError(error)
            folded = parameter.name.casefold()
            if parameter.name[0] in "@:" or not parameter.name.strip() or folded in names:
                raise FrameFormatError("parameter names must be unique and omit @/: prefixes") if decode else ValueError("parameter names must be unique and omit @/: prefixes")
            names.add(folded)


def _validate_parameter(parameter: FrameParameter) -> None:
    if not isinstance(parameter, FrameParameter):
        raise TypeError("parameters must be FrameParameter")
    _validate_utf8(parameter.name, MAX_NAME_BYTES, "parameter name")
    if parameter.name and not parameter.name.isascii():
        raise ValueError("parameter names must contain ASCII characters")
    try:
        kind = FrameParameterKind(parameter.kind)
    except ValueError as error:
        raise ValueError("unknown SQL Execute parameter type") from error
    value = parameter.value
    if value is None:
        return
    if kind is FrameParameterKind.NULL:
        raise ValueError("NULL parameter must not contain a value")
    if kind in (FrameParameterKind.INT64, FrameParameterKind.TIMESTAMP) and (type(value) is not int or not -2**63 <= value <= 2**63 - 1):
        raise TypeError("Int64 and timestamp parameters require an Int64")
    if kind is FrameParameterKind.FLOAT64 and (type(value) is not float or not math.isfinite(value)):
        raise TypeError("Float64 parameters require finite float values")
    if kind is FrameParameterKind.BOOLEAN and type(value) is not bool:
        raise TypeError("Boolean parameters require bool values")
    if kind is FrameParameterKind.STRING:
        _validate_utf8(value, MAX_VALUE_BYTES, "string parameter")
    elif kind is FrameParameterKind.BLOB:
        if not isinstance(value, (bytes, bytearray, memoryview)) or len(value) > MAX_VALUE_BYTES:
            raise TypeError("Blob parameters require a bounded bytes value")
    elif kind is FrameParameterKind.DECIMAL and not isinstance(value, DecimalBits):
        raise TypeError("Decimal parameters require DecimalBits")
    elif kind in (FrameParameterKind.DATETIME_OFFSET, FrameParameterKind.DURATION,
                  FrameParameterKind.TIME, FrameParameterKind.DATETIME):
        if type(value) is not int:
            raise TypeError("date, time, and duration parameters require Int64 ticks")
        if kind is FrameParameterKind.DATETIME_OFFSET or kind is FrameParameterKind.DATETIME:
            if not 0 <= value <= _DATETIME_MAX_TICKS:
                raise ValueError("date ticks are out of range")
        elif kind is FrameParameterKind.TIME and not 0 <= value < _TICKS_PER_DAY:
            raise ValueError("time ticks are out of range")
    elif kind is FrameParameterKind.VECTOR:
        if not isinstance(value, Sequence) or not value or len(value) > MAX_VECTOR_DIMENSION or any(type(item) not in (float, int) or not math.isfinite(float(item)) for item in value):
            raise TypeError("Vector parameters require a non-empty finite float sequence")


def _write_value(target: bytearray, kind: FrameParameterKind, value: Any) -> None:
    if kind in (FrameParameterKind.INT64, FrameParameterKind.TIMESTAMP):
        target.extend(struct.pack("<q", value))
    elif kind is FrameParameterKind.FLOAT64:
        target.extend(struct.pack("<d", value))
    elif kind is FrameParameterKind.BOOLEAN:
        target.append(1 if value else 0)
    elif kind is FrameParameterKind.STRING:
        _write_string(target, value)
    elif kind is FrameParameterKind.BLOB:
        raw = bytes(value)
        _write_varuint(target, len(raw))
        target.extend(raw)
    elif kind is FrameParameterKind.DECIMAL:
        target.extend(struct.pack("<iiii", *value.bits))
    elif kind in (FrameParameterKind.DATETIME_OFFSET, FrameParameterKind.DURATION,
                  FrameParameterKind.TIME, FrameParameterKind.DATETIME):
        target.extend(struct.pack("<q", value))
    elif kind is FrameParameterKind.VECTOR:
        _write_varuint(target, len(value))
        target.extend(struct.pack(f"<{len(value)}f", *(float(item) for item in value)))
    elif kind is not FrameParameterKind.NULL:
        raise ValueError("unsupported SQL Execute parameter type")


def _measure_varuint(value: int) -> int:
    return 1 if value < 0x80 else 2 if value < 0x4000 else 3 if value < 0x200000 else 4 if value < 0x10000000 else 5


def _measure_string(value: str) -> int:
    return _measure_varuint(len(value.encode("utf-8", "strict"))) + len(value.encode("utf-8", "strict"))


def _measure_value(parameter: FrameParameter) -> int:
    if parameter.value is None:
        return 0
    kind = parameter.kind
    value = parameter.value
    if kind in (FrameParameterKind.INT64, FrameParameterKind.FLOAT64, FrameParameterKind.TIMESTAMP,
                FrameParameterKind.DATETIME_OFFSET, FrameParameterKind.DURATION,
                FrameParameterKind.TIME, FrameParameterKind.DATETIME):
        return 8
    if kind is FrameParameterKind.BOOLEAN:
        return 1
    if kind is FrameParameterKind.STRING:
        return _measure_string(value)
    if kind is FrameParameterKind.BLOB:
        return _measure_varuint(len(value)) + len(value)
    if kind is FrameParameterKind.DECIMAL:
        return 16
    if kind is FrameParameterKind.VECTOR:
        return _measure_varuint(len(value)) + 4 * len(value)
    raise ValueError("unsupported SQL Execute parameter type")


def _read_value(reader: _Reader, kind: FrameParameterKind) -> Any:
    if kind in (FrameParameterKind.INT64, FrameParameterKind.TIMESTAMP):
        return reader.read_i64()
    if kind is FrameParameterKind.FLOAT64:
        value = struct.unpack("<d", reader._take(8))[0]
        if not math.isfinite(value):
            raise FrameFormatError("Float64 parameters must be finite")
        return value
    if kind is FrameParameterKind.BOOLEAN:
        return reader.read_bool()
    if kind is FrameParameterKind.STRING:
        return reader.read_string(MAX_VALUE_BYTES, "string parameter")
    if kind is FrameParameterKind.BLOB:
        length = reader.read_varuint()
        if length > MAX_VALUE_BYTES:
            raise FrameFormatError("Blob parameter exceeds its byte limit")
        return reader._take(length)
    if kind is FrameParameterKind.DECIMAL:
        try:
            return DecimalBits(struct.unpack("<iiii", reader._take(16)))
        except ValueError as error:
            raise FrameFormatError(str(error)) from error
    if kind is FrameParameterKind.DATETIME_OFFSET:
        ticks = reader.read_i64()
        if not 0 <= ticks <= _DATETIME_MAX_TICKS:
            raise FrameFormatError("date ticks are out of range")
        return ticks
    if kind is FrameParameterKind.DURATION:
        return reader.read_i64()
    if kind is FrameParameterKind.TIME:
        ticks = reader.read_i64()
        if not 0 <= ticks < _TICKS_PER_DAY:
            raise FrameFormatError("time ticks out of range")
        return ticks
    if kind is FrameParameterKind.VECTOR:
        dimension = reader.read_varuint()
        if not 0 < dimension <= MAX_VECTOR_DIMENSION or dimension * 4 > reader.remaining:
            raise FrameFormatError("vector dimension is invalid")
        values = struct.unpack(f"<{dimension}f", reader._take(dimension * 4))
        if any(not math.isfinite(value) for value in values):
            raise FrameFormatError("vector values must be finite")
        return tuple(values)
    if kind is FrameParameterKind.DATETIME:
        ticks = reader.read_i64()
        if not 0 <= ticks <= _DATETIME_MAX_TICKS:
            raise FrameFormatError("date ticks are out of range")
        return ticks
    if kind is FrameParameterKind.NULL:
        return None
    raise FrameFormatError("unknown SQL Execute parameter type")
