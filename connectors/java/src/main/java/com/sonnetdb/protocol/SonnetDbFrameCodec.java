package com.sonnetdb.protocol;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.CharBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CharsetDecoder;
import java.nio.charset.CharsetEncoder;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/**
 * Java 8 compatible SQL Execute Frame v1 codec.
 *
 * <p>The codec only handles the binary Frame contract. It does not provide a
 * REST or HTTP/1 fallback, and it does not perform network I/O.</p>
 */
public final class SonnetDbFrameCodec {
    /** Fixed frame header size in bytes. */
    public static final int FRAME_HEADER_SIZE = 12;
    /** Current generic Frame envelope version. */
    public static final int FRAME_VERSION = 1;
    /** SQL service identifier. */
    public static final int SQL_SERVICE = 3;
    /** SQL Execute operation identifier. */
    public static final int SQL_EXECUTE_OPERATION = 2;
    /** Response flag in the generic Frame envelope. */
    public static final int RESPONSE_FLAG = 1;
    /** SQL Execute payload version. */
    public static final int CURRENT_VERSION = 1;
    /** Maximum request payload in bytes. */
    public static final int MAX_REQUEST_PAYLOAD_BYTES = 4 * 1024 * 1024;
    /** Maximum parameter count. */
    public static final int MAX_PARAMETER_COUNT = 256;
    /** Maximum database and parameter name size in UTF-8 bytes. */
    public static final int MAX_NAME_BYTES = 512;
    /** Maximum SQL text size in UTF-8 bytes. */
    public static final int MAX_SQL_BYTES = 1024 * 1024;
    /** Maximum string or blob parameter size in bytes. */
    public static final int MAX_VALUE_BYTES = MAX_REQUEST_PAYLOAD_BYTES;
    /** Maximum vector dimension. */
    public static final int MAX_VECTOR_DIMENSION = 1_000_000;
    /** Maximum generic frame payload size. */
    public static final long MAX_FRAME_PAYLOAD_BYTES = 132L * 1024L * 1024L;

    private static final int COMMITTED_OUTCOME = 1;
    private static final long DATETIME_MAX_TICKS = 3155378975999999999L;
    private static final long TICKS_PER_DAY = 86400L * 10_000_000L;

    private SonnetDbFrameCodec() {
    }

    /** Exact parameter type codes in the SQL Execute payload. */
    public enum ParameterKind {
        NULL(0), INT64(1), FLOAT64(2), BOOLEAN(3), STRING(4), BLOB(5),
        TIMESTAMP(6), DECIMAL(7), DATETIME_OFFSET(8), DURATION(9), TIME(10),
        VECTOR(11), DATETIME(12);

        private final int code;

        ParameterKind(int code) {
            this.code = code;
        }

        /** Returns the wire type code. */
        public int code() {
            return code;
        }

        private static ParameterKind fromCode(int code) {
            for (ParameterKind kind : values()) {
                if (kind.code == code) {
                    return kind;
                }
            }
            throw new FrameFormatException("unknown SQL Execute parameter type");
        }
    }

    /** One named or positional SQL Execute parameter. */
    public static final class Parameter {
        /** Parameter name without an @ or : prefix; empty means positional. */
        public final String name;
        /** Exact wire type. */
        public final ParameterKind kind;
        /** Value matching {@link #kind}; null represents a typed NULL. */
        public final Object value;

        /** Creates a parameter value. */
        public Parameter(String name, ParameterKind kind, Object value) {
            if (name == null) {
                throw new NullPointerException("name");
            }
            if (kind == null) {
                throw new NullPointerException("kind");
            }
            this.name = name;
            this.kind = kind;
            this.value = value;
        }
    }

    /** Decoded SQL Execute request. */
    public static final class Request {
        /** Database name. */
        public final String database;
        /** SQL text. */
        public final String sql;
        /** Immutable parameter list. */
        public final List<Parameter> parameters;
        /** Stream id echoed by the generic Frame envelope. */
        public final long streamId;

        private Request(String database, String sql, List<Parameter> parameters, long streamId) {
            this.database = database;
            this.sql = sql;
            this.parameters = Collections.unmodifiableList(parameters);
            this.streamId = streamId;
        }
    }

    /** Decoded committed SQL Execute response. */
    public static final class Response {
        /** Affected row count. */
        public final long recordsAffected;
        /** Stream id echoed by the generic Frame envelope. */
        public final long streamId;

        private Response(long recordsAffected, long streamId) {
            this.recordsAffected = recordsAffected;
            this.streamId = streamId;
        }
    }

    /** Exception raised for malformed or semantically invalid Frame bytes. */
    public static final class FrameFormatException extends IllegalArgumentException {
        private static final long serialVersionUID = 1L;

        /** Creates a format exception with a stable diagnostic. */
        public FrameFormatException(String message) {
            super(message);
        }

        /** Creates a format exception with a stable diagnostic and cause. */
        public FrameFormatException(String message, Throwable cause) {
            super(message, cause);
        }
    }

    /** Encodes a complete SQL Execute request frame. */
    public static byte[] encodeRequest(long streamId, String database, String sql,
                                       List<Parameter> parameters) {
        validateStreamId(streamId);
        byte[] databaseBytes = utf8(database, MAX_NAME_BYTES, "database");
        byte[] sqlBytes = utf8(sql, MAX_SQL_BYTES, "sql");
        if (sqlBytes.length == 0) {
            throw new IllegalArgumentException("sql must not be empty");
        }
        List<Parameter> values = parameters == null
            ? Collections.<Parameter>emptyList() : parameters;
        if (values.size() > MAX_PARAMETER_COUNT) {
            throw new IllegalArgumentException("parameter count exceeds the protocol limit");
        }
        validateBindings(values, false);
        int payloadSize = 1 + varUIntSize(databaseBytes.length) + databaseBytes.length
            + varUIntSize(sqlBytes.length) + sqlBytes.length
            + varUIntSize(values.size());
        for (Parameter parameter : values) {
            validateParameter(parameter, false);
            byte[] name = utf8(parameter.name, MAX_NAME_BYTES, "parameter name");
            payloadSize = checkedAdd(payloadSize, varUIntSize(name.length) + name.length + 2
                + measureValue(parameter));
        }
        if (payloadSize > MAX_REQUEST_PAYLOAD_BYTES) {
            throw new IllegalArgumentException("SQL Execute payload exceeds the protocol limit");
        }

        ByteArrayOutputStream payload = new ByteArrayOutputStream(payloadSize);
        payload.write(CURRENT_VERSION);
        writeString(payload, databaseBytes);
        writeString(payload, sqlBytes);
        writeVarUInt(payload, values.size());
        for (Parameter parameter : values) {
            writeString(payload, utf8(parameter.name, MAX_NAME_BYTES, "parameter name"));
            payload.write(parameter.kind.code());
            payload.write(parameter.value == null ? 0 : 1);
            if (parameter.value != null) {
                writeValue(payload, parameter.kind, parameter.value);
            }
        }
        return frame(payload.toByteArray(), SQL_SERVICE, SQL_EXECUTE_OPERATION, 0, streamId);
    }

    /** Decodes and validates a complete SQL Execute request frame. */
    public static Request decodeRequest(byte[] frame) {
        Header header = readHeader(frame, SQL_SERVICE, SQL_EXECUTE_OPERATION, 0);
        Reader reader = new Reader(header.payload);
        try {
            if (reader.readByte() != CURRENT_VERSION) {
                throw new FrameFormatException("unsupported SQL Execute payload version");
            }
            String database = reader.readString(MAX_NAME_BYTES, "database");
            String sql = reader.readString(MAX_SQL_BYTES, "sql");
            if (sql.length() == 0) {
                throw new FrameFormatException("sql must not be empty");
            }
            long count = reader.readVarUInt();
            if (count > MAX_PARAMETER_COUNT) {
                throw new FrameFormatException("parameter count exceeds the protocol limit");
            }
            List<Parameter> values = new ArrayList<Parameter>((int) count);
            for (int i = 0; i < count; i++) {
                String name = reader.readString(MAX_NAME_BYTES, "parameter name");
                ParameterKind kind = ParameterKind.fromCode(reader.readByte());
                boolean hasValue = reader.readBoolean();
                if (kind == ParameterKind.NULL && hasValue) {
                    throw new FrameFormatException("NULL parameter must not contain a value");
                }
                values.add(new Parameter(name, kind, hasValue ? readValue(reader, kind) : null));
            }
            for (Parameter parameter : values) {
                validateParameter(parameter, true);
            }
            validateBindings(values, true);
            if (reader.remaining() != 0) {
                throw new FrameFormatException("SQL Execute payload contains trailing bytes");
            }
            return new Request(database, sql, values, header.streamId);
        } catch (FrameFormatException exception) {
            throw exception;
        } catch (RuntimeException exception) {
            throw new FrameFormatException("invalid SQL Execute payload", exception);
        }
    }

    /** Encodes a complete committed SQL Execute response frame. */
    public static byte[] encodeResponse(long streamId, long recordsAffected) {
        validateStreamId(streamId);
        if (recordsAffected < 0) {
            throw new IllegalArgumentException("recordsAffected must be non-negative");
        }
        ByteArrayOutputStream payload = new ByteArrayOutputStream(10);
        payload.write(CURRENT_VERSION);
        writeLong(payload, recordsAffected);
        payload.write(COMMITTED_OUTCOME);
        return frame(payload.toByteArray(), SQL_SERVICE, SQL_EXECUTE_OPERATION, RESPONSE_FLAG, streamId);
    }

    /** Decodes and validates a complete committed SQL Execute response frame. */
    public static Response decodeResponse(byte[] frame) {
        Header header = readHeader(frame, SQL_SERVICE, SQL_EXECUTE_OPERATION, RESPONSE_FLAG);
        if (header.payload.length != 10) {
            throw new FrameFormatException("invalid SQL Execute response payload");
        }
        Reader reader = new Reader(header.payload);
        if (reader.readByte() != CURRENT_VERSION) {
            throw new FrameFormatException("unsupported SQL Execute response version");
        }
        long recordsAffected = reader.readLong();
        if (recordsAffected < 0 || reader.readByte() != COMMITTED_OUTCOME || reader.remaining() != 0) {
            throw new FrameFormatException("SQL Execute response is not a committed terminal state");
        }
        return new Response(recordsAffected, header.streamId);
    }

    private static int checkedAdd(int left, int right) {
        long value = (long) left + right;
        if (value > Integer.MAX_VALUE) {
            throw new IllegalArgumentException("payload length overflows Int32");
        }
        return (int) value;
    }

    private static byte[] frame(byte[] payload, int service, int operation, int flags, long streamId) {
        if (payload.length > MAX_FRAME_PAYLOAD_BYTES) {
            throw new IllegalArgumentException("frame payload exceeds the protocol limit");
        }
        byte[] result = new byte[FRAME_HEADER_SIZE + payload.length];
        putInt(result, 0, payload.length);
        result[4] = (byte) FRAME_VERSION;
        result[5] = (byte) service;
        result[6] = (byte) operation;
        result[7] = (byte) flags;
        putInt(result, 8, streamId);
        System.arraycopy(payload, 0, result, FRAME_HEADER_SIZE, payload.length);
        return result;
    }

    private static Header readHeader(byte[] frame, int service, int operation, int flags) {
        if (frame == null || frame.length < FRAME_HEADER_SIZE) {
            throw new FrameFormatException("frame is shorter than its header");
        }
        long length = getInt(frame, 0);
        if (frame[4] != (byte) FRAME_VERSION || (frame[5] & 0xff) != service
            || (frame[6] & 0xff) != operation || (frame[7] & 0xff) != flags
            || length > MAX_FRAME_PAYLOAD_BYTES || length != frame.length - FRAME_HEADER_SIZE) {
            throw new FrameFormatException("frame identity or payload length is invalid");
        }
        byte[] payload = new byte[(int) length];
        System.arraycopy(frame, FRAME_HEADER_SIZE, payload, 0, payload.length);
        return new Header(payload, getInt(frame, 8));
    }

    private static final class Header {
        private final byte[] payload;
        private final long streamId;

        private Header(byte[] payload, long streamId) {
            this.payload = payload;
            this.streamId = streamId;
        }
    }

    private static void validateStreamId(long streamId) {
        if (streamId < 0 || streamId > 0xffffffffL) {
            throw new IllegalArgumentException("streamId must be an unsigned 32-bit integer");
        }
    }

    private static void validateBindings(List<Parameter> values, boolean decoding) {
        Boolean named = null;
        Set<String> names = new HashSet<String>();
        for (Parameter parameter : values) {
            if (parameter == null) {
                fail(decoding, "parameter has an invalid shape");
            }
            boolean isNamed = !parameter.name.isEmpty();
            if (named != null && named.booleanValue() != isNamed) {
                fail(decoding, "named and positional SQL parameters cannot be mixed");
            }
            named = Boolean.valueOf(isNamed);
            if (isNamed) {
                if (parameter.name.charAt(0) == '@' || parameter.name.charAt(0) == ':'
                    || parameter.name.trim().isEmpty()
                    || !names.add(parameter.name.toLowerCase(Locale.ROOT))) {
                    fail(decoding, "parameter names must be unique and omit @/: prefixes");
                }
            }
        }
    }

    private static void validateParameter(Parameter parameter, boolean decoding) {
        if (parameter == null) {
            fail(decoding, "parameter has an invalid shape");
        }
        utf8(parameter.name, MAX_NAME_BYTES, "parameter name");
        Object value = parameter.value;
        if (value == null) {
            return;
        }
        switch (parameter.kind) {
            case NULL:
                fail(decoding, "NULL parameter must not contain a value");
                break;
            case INT64:
            case TIMESTAMP:
                requireType(value, Long.class, decoding, "Int64 parameter requires Long");
                break;
            case FLOAT64:
                requireType(value, Double.class, decoding, "Float64 parameter requires Double");
                if (!Double.isFinite(((Double) value).doubleValue())) {
                    fail(decoding, "Float64 parameter must be finite");
                }
                break;
            case BOOLEAN:
                requireType(value, Boolean.class, decoding, "Boolean parameter requires Boolean");
                break;
            case STRING:
                requireType(value, String.class, decoding, "String parameter requires String");
                utf8((String) value, MAX_VALUE_BYTES, "string parameter");
                break;
            case BLOB:
                requireType(value, byte[].class, decoding, "Blob parameter requires byte[]");
                if (((byte[]) value).length > MAX_VALUE_BYTES) {
                    fail(decoding, "Blob parameter exceeds its byte limit");
                }
                break;
            case DECIMAL:
                requireType(value, int[].class, decoding, "Decimal parameter requires four Int32 values");
                if (((int[]) value).length != 4 || !validDecimalBits((int[]) value)) {
                    fail(decoding, "Decimal parameter requires four Int32 values");
                }
                break;
            case DATETIME_OFFSET:
            case DATETIME:
                requireType(value, Long.class, decoding, "Date parameter requires Long ticks");
                if ((Long) value < 0 || (Long) value > DATETIME_MAX_TICKS) {
                    fail(decoding, "Date ticks are out of range");
                }
                break;
            case DURATION:
                requireType(value, Long.class, decoding, "Duration parameter requires Long ticks");
                break;
            case TIME:
                requireType(value, Long.class, decoding, "Time parameter requires Long ticks");
                if ((Long) value < 0 || (Long) value >= TICKS_PER_DAY) {
                    fail(decoding, "Time ticks are out of range");
                }
                break;
            case VECTOR:
                requireType(value, float[].class, decoding, "Vector parameter requires float[]");
                float[] vector = (float[]) value;
                if (vector.length == 0 || vector.length > MAX_VECTOR_DIMENSION) {
                    fail(decoding, "Vector dimension is invalid");
                }
                for (float item : vector) {
                    if (!Float.isFinite(item)) {
                        fail(decoding, "Vector values must be finite");
                    }
                }
                break;
            default:
                fail(decoding, "unknown SQL Execute parameter type");
        }
    }

    private static void requireType(Object value, Class<?> type, boolean decoding, String message) {
        if (!type.isInstance(value)) {
            fail(decoding, message);
        }
    }

    private static boolean validDecimalBits(int[] bits) {
        int flags = bits[3];
        return (flags & 0x7f00ffff) == 0 && ((flags >>> 16) & 0xff) <= 28;
    }

    private static void fail(boolean decoding, String message) {
        if (decoding) {
            throw new FrameFormatException(message);
        }
        throw new IllegalArgumentException(message);
    }

    private static int measureValue(Parameter parameter) {
        if (parameter.value == null) {
            return 0;
        }
        switch (parameter.kind) {
            case INT64:
            case TIMESTAMP:
            case DATETIME_OFFSET:
            case DURATION:
            case TIME:
            case DATETIME:
                return 8;
            case FLOAT64:
                return 8;
            case BOOLEAN:
                return 1;
            case DECIMAL:
                return 16;
            case STRING:
                byte[] text = utf8((String) parameter.value, MAX_VALUE_BYTES, "string parameter");
                return varUIntSize(text.length) + text.length;
            case BLOB:
                byte[] blob = (byte[]) parameter.value;
                return varUIntSize(blob.length) + blob.length;
            case VECTOR:
                float[] vector = (float[]) parameter.value;
                return varUIntSize(vector.length) + vector.length * 4;
            case NULL:
                return 0;
            default:
                throw new IllegalArgumentException("unknown SQL Execute parameter type");
        }
    }

    private static void writeValue(ByteArrayOutputStream output, ParameterKind kind, Object value) {
        switch (kind) {
            case INT64:
            case TIMESTAMP:
            case DATETIME_OFFSET:
            case DURATION:
            case TIME:
            case DATETIME:
                writeLong(output, ((Long) value).longValue());
                break;
            case FLOAT64:
                writeLong(output, Double.doubleToRawLongBits(((Double) value).doubleValue()));
                break;
            case BOOLEAN:
                output.write(((Boolean) value).booleanValue() ? 1 : 0);
                break;
            case STRING:
                writeString(output, utf8((String) value, MAX_VALUE_BYTES, "string parameter"));
                break;
            case BLOB:
                byte[] blob = (byte[]) value;
                writeVarUInt(output, blob.length);
                output.write(blob, 0, blob.length);
                break;
            case DECIMAL:
                for (int bits : (int[]) value) {
                    writeInt(output, bits & 0xffffffffL);
                }
                break;
            case VECTOR:
                float[] vector = (float[]) value;
                writeVarUInt(output, vector.length);
                for (float item : vector) {
                    writeInt(output, Float.floatToRawIntBits(item) & 0xffffffffL);
                }
                break;
            case NULL:
                break;
            default:
                throw new IllegalArgumentException("unknown SQL Execute parameter type");
        }
    }

    private static Object readValue(Reader reader, ParameterKind kind) {
        switch (kind) {
            case INT64:
            case TIMESTAMP:
            case DATETIME_OFFSET:
            case DURATION:
            case TIME:
            case DATETIME:
                return Long.valueOf(reader.readLong());
            case FLOAT64:
                double value = Double.longBitsToDouble(reader.readLong());
                if (!Double.isFinite(value)) {
                    throw new FrameFormatException("Float64 parameter must be finite");
                }
                return Double.valueOf(value);
            case BOOLEAN:
                return Boolean.valueOf(reader.readBoolean());
            case STRING:
                return reader.readString(MAX_VALUE_BYTES, "string parameter");
            case BLOB:
                int length = boundedLength(reader, MAX_VALUE_BYTES, "Blob parameter");
                return reader.readBytes(length);
            case DECIMAL:
                int[] bits = new int[4];
                for (int i = 0; i < bits.length; i++) {
                    bits[i] = (int) reader.readInt();
                }
                return bits;
            case VECTOR:
                long dimension = reader.readVarUInt();
                if (dimension == 0 || dimension > MAX_VECTOR_DIMENSION || dimension * 4L > reader.remaining()) {
                    throw new FrameFormatException("Vector dimension is invalid");
                }
                float[] vector = new float[(int) dimension];
                for (int i = 0; i < vector.length; i++) {
                    float item = Float.intBitsToFloat((int) reader.readInt());
                    if (!Float.isFinite(item)) {
                        throw new FrameFormatException("Vector values must be finite");
                    }
                    vector[i] = item;
                }
                return vector;
            case NULL:
                return null;
            default:
                throw new FrameFormatException("unknown SQL Execute parameter type");
        }
    }

    private static int boundedLength(Reader reader, int maximum, String label) {
        long length = reader.readVarUInt();
        if (length > maximum || length > reader.remaining()) {
            throw new FrameFormatException(label + " length is invalid");
        }
        return (int) length;
    }

    private static byte[] utf8(String value, int maximum, String label) {
        if (value == null) {
            throw new NullPointerException(label);
        }
        CharsetEncoder encoder = StandardCharsets.UTF_8.newEncoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT);
        try {
            ByteBuffer buffer = encoder.encode(CharBuffer.wrap(value));
            if (buffer.remaining() > maximum) {
                throw new IllegalArgumentException(label + " exceeds its UTF-8 byte limit");
            }
            byte[] bytes = new byte[buffer.remaining()];
            buffer.get(bytes);
            return bytes;
        } catch (CharacterCodingException exception) {
            throw new IllegalArgumentException(label + " is not valid UTF-16/UTF-8", exception);
        }
    }

    private static String decodeUtf8(byte[] bytes, String label) {
        CharsetDecoder decoder = StandardCharsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT);
        try {
            return decoder.decode(ByteBuffer.wrap(bytes)).toString();
        } catch (CharacterCodingException exception) {
            throw new FrameFormatException(label + " is not valid UTF-8", exception);
        }
    }

    private static void writeString(ByteArrayOutputStream output, byte[] bytes) {
        writeVarUInt(output, bytes.length);
        output.write(bytes, 0, bytes.length);
    }

    private static void writeVarUInt(ByteArrayOutputStream output, long value) {
        if (value < 0 || value > 0xffffffffL) {
            throw new IllegalArgumentException("varuint32 value out of range");
        }
        do {
            int next = (int) (value & 0x7f);
            value >>>= 7;
            output.write(value == 0 ? next : next | 0x80);
        } while (value != 0);
    }

    private static int varUIntSize(long value) {
        int size = 1;
        while (value >= 0x80) {
            value >>>= 7;
            size++;
        }
        return size;
    }

    private static void writeInt(ByteArrayOutputStream output, long value) {
        for (int i = 0; i < 4; i++) {
            output.write((int) (value >>> (8 * i)) & 0xff);
        }
    }

    private static void writeLong(ByteArrayOutputStream output, long value) {
        for (int i = 0; i < 8; i++) {
            output.write((int) (value >>> (8 * i)) & 0xff);
        }
    }

    private static void putInt(byte[] target, int offset, long value) {
        for (int i = 0; i < 4; i++) {
            target[offset + i] = (byte) (value >>> (8 * i));
        }
    }

    private static long getInt(byte[] source, int offset) {
        long value = 0;
        for (int i = 0; i < 4; i++) {
            value |= (long) (source[offset + i] & 0xff) << (8 * i);
        }
        return value;
    }

    private static final class Reader {
        private final byte[] data;
        private int offset;

        private Reader(byte[] data) {
            this.data = data;
        }

        private int remaining() {
            return data.length - offset;
        }

        private int readByte() {
            if (remaining() < 1) {
                throw new FrameFormatException("payload ended before the value was complete");
            }
            return data[offset++] & 0xff;
        }

        private boolean readBoolean() {
            int value = readByte();
            if (value > 1) {
                throw new FrameFormatException("boolean values must be 0 or 1");
            }
            return value == 1;
        }

        private long readVarUInt() {
            long value = 0;
            for (int i = 0; i < 5; i++) {
                int next = readByte();
                value |= (long) (next & 0x7f) << (7 * i);
                if ((next & 0x80) == 0) {
                    if (value > 0xffffffffL) {
                        throw new FrameFormatException("varuint32 value out of range");
                    }
                    return value;
                }
            }
            throw new FrameFormatException("varuint32 uses more than five bytes");
        }

        private String readString(int maximum, String label) {
            int length = boundedLength(this, maximum, label);
            return decodeUtf8(readBytes(length), label);
        }

        private long readLong() {
            if (remaining() < 8) {
                throw new FrameFormatException("payload ended before the Int64 value was complete");
            }
            long value = 0;
            for (int i = 0; i < 8; i++) {
                value |= (long) (data[offset++] & 0xff) << (8 * i);
            }
            return value;
        }

        private long readInt() {
            if (remaining() < 4) {
                throw new FrameFormatException("payload ended before the Int32 value was complete");
            }
            long value = 0;
            for (int i = 0; i < 4; i++) {
                value |= (long) (data[offset++] & 0xff) << (8 * i);
            }
            return value;
        }

        private byte[] readBytes(int length) {
            if (length < 0 || remaining() < length) {
                throw new FrameFormatException("payload ended before the value was complete");
            }
            byte[] value = new byte[length];
            System.arraycopy(data, offset, value, 0, length);
            offset += length;
            return value;
        }
    }
}
