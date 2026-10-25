import unittest

from sonnetdb import (
    DecimalBits,
    FrameFormatError,
    FrameParameter,
    FrameParameterKind,
    decode_sql_execute_frame,
    decode_sql_execute_response_frame,
    encode_sql_execute_frame,
    encode_sql_execute_response_frame,
)


class SqlExecuteFrameTests(unittest.TestCase):
    def test_minimal_request_matches_protocol_bytes(self) -> None:
        frame = encode_sql_execute_frame(42, "d", "S")
        self.assertEqual(
            bytes.fromhex("06000000010302002a000000010164015300"),
            frame,
        )
        request = decode_sql_execute_frame(frame)
        self.assertEqual(("d", "S", ()), (request.database, request.sql, request.parameters))

    def test_all_parameter_kinds_round_trip(self) -> None:
        parameters = (
            FrameParameter("p0", FrameParameterKind.NULL),
            FrameParameter("p1", FrameParameterKind.INT64, -7),
            FrameParameter("p2", FrameParameterKind.FLOAT64, 1.5),
            FrameParameter("p3", FrameParameterKind.BOOLEAN, True),
            FrameParameter("p4", FrameParameterKind.STRING, "你好"),
            FrameParameter("p5", FrameParameterKind.BLOB, b"\x00\xff"),
            FrameParameter("p6", FrameParameterKind.TIMESTAMP, 123456789),
            FrameParameter("p7", FrameParameterKind.DECIMAL, DecimalBits((3, 0, 0, 0))),
            FrameParameter("p8", FrameParameterKind.DATETIME_OFFSET, 638000000000000000),
            FrameParameter("p9", FrameParameterKind.DURATION, -123),
            FrameParameter("p10", FrameParameterKind.TIME, 123456789),
            FrameParameter("p11", FrameParameterKind.VECTOR, (1.0, -2.5)),
            FrameParameter("p12", FrameParameterKind.DATETIME, 638000000000000000),
        )
        request = decode_sql_execute_frame(encode_sql_execute_frame(9, "demo", "INSERT", parameters))
        self.assertEqual("demo", request.database)
        self.assertEqual("INSERT", request.sql)
        self.assertEqual(parameters, request.parameters)

    def test_committed_response_round_trip_and_fixed_shape(self) -> None:
        frame = encode_sql_execute_response_frame(7, 13)
        self.assertEqual(22, len(frame))
        self.assertEqual(13, decode_sql_execute_response_frame(frame).records_affected)

    def test_rejects_mixed_bindings_and_invalid_terminal_state(self) -> None:
        with self.assertRaises(ValueError):
            encode_sql_execute_frame(
                1,
                "db",
                "SELECT 1",
                (FrameParameter("named", FrameParameterKind.INT64, 1), FrameParameter("", FrameParameterKind.INT64, 2)),
            )

        response = bytearray(encode_sql_execute_response_frame(1, 1))
        response[-1] = 0
        with self.assertRaises(FrameFormatError):
            decode_sql_execute_response_frame(response)

    def test_rejects_non_finite_float_and_trailing_bytes(self) -> None:
        with self.assertRaises(TypeError):
            encode_sql_execute_frame(1, "db", "SELECT 1", (FrameParameter("p", FrameParameterKind.FLOAT64, float("nan")),))
        frame = encode_sql_execute_frame(1, "db", "SELECT 1") + b"x"
        with self.assertRaises(FrameFormatError):
            decode_sql_execute_frame(frame)

    def test_rejects_non_ascii_names_decimal_reserved_bits_and_oversize_payload(self) -> None:
        with self.assertRaises(ValueError):
            encode_sql_execute_frame(1, "db", "SELECT 1", (FrameParameter("参数", FrameParameterKind.INT64, 1),))
        with self.assertRaises(ValueError):
            DecimalBits((0, 0, 0, 0x01000000))
        with self.assertRaises(ValueError):
            encode_sql_execute_frame(1, "db", "SELECT 1", (FrameParameter("p", FrameParameterKind.STRING, "x" * (4 * 1024 * 1024)),))

    def test_rejects_non_binary_boolean_payload(self) -> None:
        frame = bytearray(encode_sql_execute_frame(1, "db", "SELECT 1", (FrameParameter("p", FrameParameterKind.BOOLEAN, True),)))
        # Locate the bool value by decoding the fixed prefix and replacing the
        # final byte; the payload has no trailing data in this case.
        frame[-1] = 2
        with self.assertRaises(FrameFormatError):
            decode_sql_execute_frame(frame)


if __name__ == "__main__":
    unittest.main()
