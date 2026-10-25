package com.sonnetdb.protocol;

import java.util.Arrays;
import java.util.Collections;

/** Small Java 8 compatible executable contract test for the Frame codec. */
public final class SonnetDbFrameCodecTest {
    private SonnetDbFrameCodecTest() {
    }

    public static void main(String[] args) {
        roundTripAllValueKinds();
        responseRoundTripAndExactEnvelope();
        malformedFramesAreRejected();
        System.out.println("SonnetDbFrameCodecTest: 3/3 PASS");
    }

    private static void roundTripAllValueKinds() {
        SonnetDbFrameCodec.Parameter[] values = new SonnetDbFrameCodec.Parameter[] {
            new SonnetDbFrameCodec.Parameter("i", SonnetDbFrameCodec.ParameterKind.INT64, Long.valueOf(-7)),
            new SonnetDbFrameCodec.Parameter("f", SonnetDbFrameCodec.ParameterKind.FLOAT64, Double.valueOf(1.25)),
            new SonnetDbFrameCodec.Parameter("b", SonnetDbFrameCodec.ParameterKind.BOOLEAN, Boolean.TRUE),
            new SonnetDbFrameCodec.Parameter("s", SonnetDbFrameCodec.ParameterKind.STRING, "你好"),
            new SonnetDbFrameCodec.Parameter("blob", SonnetDbFrameCodec.ParameterKind.BLOB, new byte[] { 1, 2, 3 }),
            new SonnetDbFrameCodec.Parameter("ts", SonnetDbFrameCodec.ParameterKind.TIMESTAMP, Long.valueOf(1700000000000L)),
            new SonnetDbFrameCodec.Parameter("decimal", SonnetDbFrameCodec.ParameterKind.DECIMAL, new int[] { 1, 2, 3, 0 }),
            new SonnetDbFrameCodec.Parameter("offset", SonnetDbFrameCodec.ParameterKind.DATETIME_OFFSET, Long.valueOf(638000000000000000L)),
            new SonnetDbFrameCodec.Parameter("duration", SonnetDbFrameCodec.ParameterKind.DURATION, Long.valueOf(-1234L)),
            new SonnetDbFrameCodec.Parameter("time", SonnetDbFrameCodec.ParameterKind.TIME, Long.valueOf(123456L)),
            new SonnetDbFrameCodec.Parameter("vector", SonnetDbFrameCodec.ParameterKind.VECTOR, new float[] { 1.0f, -2.5f }),
            new SonnetDbFrameCodec.Parameter("date", SonnetDbFrameCodec.ParameterKind.DATETIME, Long.valueOf(638000000000000000L)),
            new SonnetDbFrameCodec.Parameter("nullable", SonnetDbFrameCodec.ParameterKind.STRING, null),
        };
        byte[] frame = SonnetDbFrameCodec.encodeRequest(0xffffffffL, "demo", "SELECT ?", Arrays.asList(values));
        SonnetDbFrameCodec.Request request = SonnetDbFrameCodec.decodeRequest(frame);
        require(request.streamId == 0xffffffffL, "stream id did not round-trip");
        require("demo".equals(request.database), "database did not round-trip");
        require("SELECT ?".equals(request.sql), "SQL did not round-trip");
        require(request.parameters.size() == values.length, "parameter count did not round-trip");
        require(((Long) request.parameters.get(0).value).longValue() == -7L, "Int64 did not round-trip");
        require(((Double) request.parameters.get(1).value).doubleValue() == 1.25, "Float64 did not round-trip");
        require("你好".equals(request.parameters.get(3).value), "UTF-8 text did not round-trip");
        require(Arrays.equals((byte[]) request.parameters.get(4).value, new byte[] { 1, 2, 3 }), "Blob did not round-trip");
        require(Arrays.equals((int[]) request.parameters.get(6).value, new int[] { 1, 2, 3, 0 }), "Decimal bits did not round-trip");
        require(Arrays.equals((float[]) request.parameters.get(10).value, new float[] { 1.0f, -2.5f }), "Vector did not round-trip");
        require(request.parameters.get(12).value == null, "typed NULL did not round-trip");
    }

    private static void responseRoundTripAndExactEnvelope() {
        byte[] response = SonnetDbFrameCodec.encodeResponse(9L, 17L);
        byte[] expected = new byte[] {
            10, 0, 0, 0, 1, 3, 2, 1, 9, 0, 0, 0,
            1, 17, 0, 0, 0, 0, 0, 0, 0, 1,
        };
        require(Arrays.equals(response, expected), "response envelope bytes changed");
        SonnetDbFrameCodec.Response decoded = SonnetDbFrameCodec.decodeResponse(response);
        require(decoded.streamId == 9L && decoded.recordsAffected == 17L, "response did not round-trip");
    }

    private static void malformedFramesAreRejected() {
        expectFailure(new Runnable() {
            @Override public void run() {
                SonnetDbFrameCodec.encodeRequest(1L, "demo", "SELECT 1", Arrays.asList(
                    new SonnetDbFrameCodec.Parameter("x", SonnetDbFrameCodec.ParameterKind.INT64, Long.valueOf(1)),
                    new SonnetDbFrameCodec.Parameter("X", SonnetDbFrameCodec.ParameterKind.INT64, Long.valueOf(2))));
            }
        });
        expectFailure(new Runnable() {
            @Override public void run() {
                SonnetDbFrameCodec.decodeResponse(new byte[] {
                    10, 0, 0, 0, 1, 3, 2, 0, 9, 0, 0, 0,
                    1, 17, 0, 0, 0, 0, 0, 0, 0, 1,
                });
            }
        });
        expectFailure(new Runnable() {
            @Override public void run() {
                SonnetDbFrameCodec.encodeRequest(1L, "demo", "SELECT 1", Arrays.asList(
                    new SonnetDbFrameCodec.Parameter("decimal", SonnetDbFrameCodec.ParameterKind.DECIMAL, new int[] { 0, 0, 0, 4 })));
            }
        });
        byte[] request = SonnetDbFrameCodec.encodeRequest(1L, "demo", "SELECT 1", Collections.<SonnetDbFrameCodec.Parameter>emptyList());
        byte[] trailing = Arrays.copyOf(request, request.length + 1);
        trailing[trailing.length - 1] = 1;
        expectFailure(new Runnable() {
            @Override public void run() {
                SonnetDbFrameCodec.decodeRequest(trailing);
            }
        });
    }

    private static void expectFailure(Runnable action) {
        try {
            action.run();
        } catch (IllegalArgumentException expected) {
            return;
        }
        throw new AssertionError("expected an invalid Frame to be rejected");
    }

    private static void require(boolean condition, String message) {
        if (!condition) {
            throw new AssertionError(message);
        }
    }
}
