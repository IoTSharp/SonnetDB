//go:build cgo && (windows || linux)

package sonnetdb

import (
	"encoding/binary"
	"fmt"
	"math"
	"unicode/utf8"
)

const (
	frameHeaderSize          = 12
	frameVersion             = byte(1)
	sqlFrameService          = byte(3)
	sqlExecuteOperation      = byte(2)
	frameResponseFlag        = byte(1)
	currentExecuteVersion   = byte(1)
	maxFramePayloadBytes     = 132 * 1024 * 1024
	maxRequestPayloadBytes   = 4 * 1024 * 1024
	maxParameterCount        = 256
	maxNameBytes             = 512
	maxSqlBytes              = 1024 * 1024
	maxValueBytes            = maxRequestPayloadBytes
	maxVectorDimension       = 1_000_000
	committedOutcome         = byte(1)
	maxDateTimeTicks         = int64(3_155_378_975_999_999_999)
	ticksPerDay              = int64(864_000_000_000)
)

// FrameParameterKind is the exact SQL Execute v1 parameter type code.
type FrameParameterKind byte

const (
	FrameNull FrameParameterKind = iota
	FrameInt64
	FrameFloat64
	FrameBoolean
	FrameString
	FrameBlob
	FrameTimestamp
	FrameDecimal
	FrameDateTimeOffset
	FrameDuration
	FrameTime
	FrameVector
	FrameDateTime
)

// FrameParameter carries one named or positional SQL Execute parameter. A nil
// Value preserves the declared type while encoding a SQL NULL value.
type FrameParameter struct {
	Name  string
	Kind  FrameParameterKind
	Value any
}

// SqlExecuteFrameRequest is the decoded SQL Execute request.
type SqlExecuteFrameRequest struct {
	Database   string
	SQL        string
	Parameters []FrameParameter
}

// SqlExecuteFrameResponse is a committed SQL Execute response.
type SqlExecuteFrameResponse struct {
	RecordsAffected int64
}

// FrameFormatError reports a malformed SQL Execute frame or payload.
type FrameFormatError struct{ Message string }

func (e *FrameFormatError) Error() string { return e.Message }

// EncodeSQLExecuteFrame encodes a complete SQL Execute v1 request frame. The
// returned bytes are transport-neutral and require an HTTP/2 Frame transport;
// no HTTP/1 or REST fallback is performed here.
func EncodeSQLExecuteFrame(streamID uint32, database, sql string, parameters []FrameParameter) ([]byte, error) {
	if err := validateString(database, maxNameBytes, "database"); err != nil {
		return nil, err
	}
	if err := validateString(sql, maxSqlBytes, "sql"); err != nil {
		return nil, err
	}
	if sql == "" {
		return nil, fmt.Errorf("sql must not be empty")
	}
	if len(parameters) > maxParameterCount {
		return nil, fmt.Errorf("parameter count exceeds the protocol limit")
	}
	if err := validateBindings(parameters, false); err != nil {
		return nil, err
	}
	for _, parameter := range parameters {
		if err := validateParameter(parameter); err != nil {
			return nil, err
		}
	}

	payloadLength := 1 + measureString(database) + measureString(sql) + measureVarUint(len(parameters))
	for _, parameter := range parameters {
		payloadLength += measureString(parameter.Name) + 2 + measureValue(parameter)
	}
	if payloadLength > maxRequestPayloadBytes {
		return nil, fmt.Errorf("SQL Execute payload exceeds the protocol limit")
	}
	payload := make([]byte, 0, payloadLength)
	payload = append(payload, currentExecuteVersion)
	var err error
	payload, err = writeString(payload, database)
	if err != nil {
		return nil, err
	}
	payload, err = writeString(payload, sql)
	if err != nil {
		return nil, err
	}
	payload = writeVarUint(payload, uint32(len(parameters)))
	for _, parameter := range parameters {
		payload, err = writeString(payload, parameter.Name)
		if err != nil {
			return nil, err
		}
		payload = append(payload, byte(parameter.Kind))
		if parameter.Value == nil {
			payload = append(payload, 0)
			continue
		}
		payload = append(payload, 1)
		payload, err = writeValue(payload, parameter.Kind, parameter.Value)
		if err != nil {
			return nil, err
		}
	}
	header, err := encodeFrameHeader(uint32(len(payload)), sqlFrameService, sqlExecuteOperation, 0, streamID)
	if err != nil {
		return nil, err
	}
	return append(header, payload...), nil
}

// DecodeSQLExecuteFrame validates and decodes a complete request frame.
func DecodeSQLExecuteFrame(frame []byte) (SqlExecuteFrameRequest, error) {
	payload, err := readFrame(frame, sqlFrameService, sqlExecuteOperation, 0)
	if err != nil {
		return SqlExecuteFrameRequest{}, err
	}
	return DecodeSQLExecutePayload(payload)
}

// DecodeSQLExecutePayload decodes a request payload without its 12-byte header.
func DecodeSQLExecutePayload(payload []byte) (SqlExecuteFrameRequest, error) {
	if len(payload) > maxRequestPayloadBytes {
		return SqlExecuteFrameRequest{}, frameError("SQL Execute payload exceeds the protocol limit")
	}
	reader := frameReader{data: payload}
	version, err := reader.byte()
	if err != nil {
		return SqlExecuteFrameRequest{}, err
	}
	if version != currentExecuteVersion {
		return SqlExecuteFrameRequest{}, frameError("unsupported SQL Execute payload version")
	}
	database, err := reader.string(maxNameBytes, "database")
	if err != nil {
		return SqlExecuteFrameRequest{}, err
	}
	sql, err := reader.string(maxSqlBytes, "sql")
	if err != nil {
		return SqlExecuteFrameRequest{}, err
	}
	if sql == "" {
		return SqlExecuteFrameRequest{}, frameError("sql must not be empty")
	}
	count, err := reader.varUint()
	if err != nil {
		return SqlExecuteFrameRequest{}, err
	}
	if count > maxParameterCount {
		return SqlExecuteFrameRequest{}, frameError("parameter count exceeds the protocol limit")
	}
	parameters := make([]FrameParameter, 0, count)
	for index := 0; index < count; index++ {
		name, err := reader.string(maxNameBytes, "parameter name")
		if err != nil {
			return SqlExecuteFrameRequest{}, err
		}
		kindValue, err := reader.byte()
		if err != nil {
			return SqlExecuteFrameRequest{}, err
		}
		if kindValue > byte(FrameDateTime) {
			return SqlExecuteFrameRequest{}, frameError("unknown SQL Execute parameter type")
		}
		kind := FrameParameterKind(kindValue)
		hasValue, err := reader.boolean()
		if err != nil {
			return SqlExecuteFrameRequest{}, err
		}
		if kind == FrameNull && hasValue {
			return SqlExecuteFrameRequest{}, frameError("NULL parameter must not contain a value")
		}
		var value any
		if hasValue {
			value, err = reader.value(kind)
			if err != nil {
				return SqlExecuteFrameRequest{}, err
			}
		}
		parameters = append(parameters, FrameParameter{Name: name, Kind: kind, Value: value})
	}
	if reader.remaining() != 0 {
		return SqlExecuteFrameRequest{}, frameError("SQL Execute payload contains trailing bytes")
	}
	if err := validateBindings(parameters, true); err != nil {
		return SqlExecuteFrameRequest{}, err
	}
	return SqlExecuteFrameRequest{Database: database, SQL: sql, Parameters: parameters}, nil
}

// EncodeSQLExecuteResponseFrame encodes a complete committed response frame.
func EncodeSQLExecuteResponseFrame(streamID uint32, recordsAffected int64) ([]byte, error) {
	if recordsAffected < 0 {
		return nil, fmt.Errorf("records affected must be non-negative")
	}
	payload := []byte{currentExecuteVersion}
	var bytes [8]byte
	binary.LittleEndian.PutUint64(bytes[:], uint64(recordsAffected))
	payload = append(payload, bytes[:]...)
	payload = append(payload, committedOutcome)
	header, err := encodeFrameHeader(uint32(len(payload)), sqlFrameService, sqlExecuteOperation, frameResponseFlag, streamID)
	if err != nil {
		return nil, err
	}
	return append(header, payload...), nil
}

// DecodeSQLExecuteResponseFrame validates and decodes a committed response frame.
func DecodeSQLExecuteResponseFrame(frame []byte) (SqlExecuteFrameResponse, error) {
	payload, err := readFrame(frame, sqlFrameService, sqlExecuteOperation, frameResponseFlag)
	if err != nil {
		return SqlExecuteFrameResponse{}, err
	}
	return DecodeSQLExecuteResponsePayload(payload)
}

// DecodeSQLExecuteResponsePayload decodes a committed response payload.
func DecodeSQLExecuteResponsePayload(payload []byte) (SqlExecuteFrameResponse, error) {
	if len(payload) != 10 || payload[0] != currentExecuteVersion {
		return SqlExecuteFrameResponse{}, frameError("invalid SQL Execute response payload")
	}
	records := int64(binary.LittleEndian.Uint64(payload[1:9]))
	if records < 0 || payload[9] != committedOutcome {
		return SqlExecuteFrameResponse{}, frameError("SQL Execute response is not a committed terminal state")
	}
	return SqlExecuteFrameResponse{RecordsAffected: records}, nil
}

func frameError(message string) error { return &FrameFormatError{Message: message} }

func encodeFrameHeader(payloadLength uint32, service, operation, flags byte, streamID uint32) ([]byte, error) {
	if payloadLength > maxFramePayloadBytes {
		return nil, frameError("frame payload exceeds the protocol limit")
	}
	header := make([]byte, frameHeaderSize)
	binary.LittleEndian.PutUint32(header[0:4], payloadLength)
	header[4], header[5], header[6], header[7] = frameVersion, service, operation, flags
	binary.LittleEndian.PutUint32(header[8:12], streamID)
	return header, nil
}

func readFrame(frame []byte, service, operation, flags byte) ([]byte, error) {
	if len(frame) < frameHeaderSize {
		return nil, frameError("frame is shorter than its header")
	}
	payloadLength := int(binary.LittleEndian.Uint32(frame[0:4]))
	if frame[4] != frameVersion || frame[5] != service || frame[6] != operation || frame[7] != flags {
		return nil, frameError("frame identity does not match SQL Execute")
	}
	if payloadLength > maxFramePayloadBytes || len(frame) != frameHeaderSize+payloadLength {
		return nil, frameError("frame payload length is invalid")
	}
	return frame[frameHeaderSize:], nil
}

func validateString(value string, maximum int, label string) error {
	if !utf8.ValidString(value) || len(value) > maximum {
		return fmt.Errorf("%s exceeds its UTF-8 byte limit or is invalid UTF-8", label)
	}
	return nil
}

func writeVarUint(target []byte, value uint32) []byte {
	for value >= 0x80 {
		target = append(target, byte(value&0x7f)|0x80)
		value >>= 7
	}
	return append(target, byte(value))
}

func writeString(target []byte, value string) ([]byte, error) {
	if len(value) > int(^uint32(0)) || !utf8.ValidString(value) {
		return nil, fmt.Errorf("string is not valid UTF-8 or exceeds varuint32 range")
	}
	target = writeVarUint(target, uint32(len(value)))
	return append(target, value...), nil
}

func validateBindings(parameters []FrameParameter, decoding bool) error {
	var named *bool
	for index, parameter := range parameters {
		isNamed := parameter.Name != ""
		if named != nil && *named != isNamed {
			return frameError("named and positional SQL parameters cannot be mixed")
		}
		if named == nil {
			copyValue := isNamed
			named = &copyValue
		}
		if isNamed {
			if err := validateString(parameter.Name, maxNameBytes, "parameter name"); err != nil {
				return err
			}
			for index := range parameter.Name {
				if parameter.Name[index] >= 0x80 {
					return frameError("parameter names must contain ASCII characters")
				}
			}
			if parameter.Name[0] == '@' || parameter.Name[0] == ':' || parameter.Name != stringTrimSpace(parameter.Name) {
				return frameError("parameter names must omit @/: prefixes and whitespace")
			}
			for _, previous := range parameters[:index] {
				if equalFold(previous.Name, parameter.Name) {
					return frameError("parameter names must be unique")
				}
			}
		}
	}
	if decoding {
		for _, parameter := range parameters {
			if err := validateParameter(parameter); err != nil {
				return err
			}
		}
	}
	return nil
}

func validateParameter(parameter FrameParameter) error {
	if err := validateString(parameter.Name, maxNameBytes, "parameter name"); err != nil {
		return err
	}
	if parameter.Kind > FrameDateTime {
		return frameError("unknown SQL Execute parameter type")
	}
	if parameter.Value == nil {
		return nil
	}
	valid := false
	switch parameter.Kind {
	case FrameInt64:
		_, valid = parameter.Value.(int64)
	case FrameFloat64:
		value, ok := parameter.Value.(float64)
		valid = ok && !math.IsNaN(value) && !math.IsInf(value, 0)
	case FrameBoolean:
		_, valid = parameter.Value.(bool)
	case FrameString:
		value, ok := parameter.Value.(string)
		valid = ok && validateString(value, maxValueBytes, "string parameter") == nil
	case FrameBlob:
		value, ok := parameter.Value.([]byte)
		valid = ok && len(value) <= maxValueBytes
	case FrameTimestamp:
		_, valid = parameter.Value.(int64)
	case FrameDecimal:
		value, ok := parameter.Value.([4]int32)
		valid = ok && validDecimalBits(value)
	case FrameDateTimeOffset:
		value, ok := parameter.Value.(int64)
		valid = ok && value >= 0 && value <= maxDateTimeTicks
	case FrameDuration:
		_, valid = parameter.Value.(int64)
	case FrameTime:
		value, ok := parameter.Value.(int64)
		valid = ok && value >= 0 && value < ticksPerDay
	case FrameVector:
		value, ok := parameter.Value.([]float32)
		valid = ok && len(value) > 0 && len(value) <= maxVectorDimension
		if valid {
			for _, item := range value {
				if math.IsNaN(float64(item)) || math.IsInf(float64(item), 0) {
					valid = false
					break
				}
			}
		}
	case FrameDateTime:
		value, ok := parameter.Value.(int64)
		valid = ok && value >= 0 && value <= maxDateTimeTicks
	case FrameNull:
		valid = false
	}
	if !valid {
		return frameError("parameter value does not match its protocol type")
	}
	return nil
}

func validDecimalBits(bits [4]int32) bool {
	flags := uint32(bits[3])
	return flags&0x7f00ffff == 0 && ((flags>>16)&0xff) <= 28
}

func measureVarUint(value int) int {
	if value < 0x80 {
		return 1
	}
	if value < 0x4000 {
		return 2
	}
	if value < 0x200000 {
		return 3
	}
	if value < 0x10000000 {
		return 4
	}
	return 5
}

func measureString(value string) int {
	return measureVarUint(len(value)) + len(value)
}

func measureValue(parameter FrameParameter) int {
	if parameter.Value == nil {
		return 0
	}
	switch parameter.Kind {
	case FrameInt64, FrameFloat64, FrameTimestamp, FrameDateTimeOffset, FrameDuration, FrameTime, FrameDateTime:
		return 8
	case FrameBoolean:
		return 1
	case FrameString:
		return measureString(parameter.Value.(string))
	case FrameBlob:
		value := parameter.Value.([]byte)
		return measureVarUint(len(value)) + len(value)
	case FrameDecimal:
		return 16
	case FrameVector:
		value := parameter.Value.([]float32)
		return measureVarUint(len(value)) + len(value)*4
	default:
		return 0
	}
}

func writeValue(target []byte, kind FrameParameterKind, value any) ([]byte, error) {
	var bytes [8]byte
	switch kind {
	case FrameInt64:
		binary.LittleEndian.PutUint64(bytes[:], uint64(value.(int64)))
		target = append(target, bytes[:]...)
	case FrameFloat64:
		binary.LittleEndian.PutUint64(bytes[:], math.Float64bits(value.(float64)))
		target = append(target, bytes[:]...)
	case FrameBoolean:
		if value.(bool) {
			target = append(target, 1)
		} else {
			target = append(target, 0)
		}
	case FrameString:
		return writeString(target, value.(string))
	case FrameBlob:
		raw := value.([]byte)
		target = writeVarUint(target, uint32(len(raw)))
		target = append(target, raw...)
	case FrameTimestamp, FrameDateTimeOffset, FrameDuration, FrameTime, FrameDateTime:
		binary.LittleEndian.PutUint64(bytes[:], uint64(value.(int64)))
		target = append(target, bytes[:]...)
	case FrameDecimal:
		for _, bit := range value.([4]int32) {
			var raw [4]byte
			binary.LittleEndian.PutUint32(raw[:], uint32(bit))
			target = append(target, raw[:]...)
		}
	case FrameVector:
		values := value.([]float32)
		target = writeVarUint(target, uint32(len(values)))
		for _, item := range values {
			var raw [4]byte
			binary.LittleEndian.PutUint32(raw[:], math.Float32bits(item))
			target = append(target, raw[:]...)
		}
	default:
		return nil, frameError("unsupported SQL Execute parameter type")
	}
	return target, nil
}

type frameReader struct {
	data []byte
	offset int
}

func (r *frameReader) remaining() int { return len(r.data) - r.offset }

func (r *frameReader) take(length int) ([]byte, error) {
	if length < 0 || r.remaining() < length {
		return nil, frameError("payload ended before the value was complete")
	}
	value := r.data[r.offset : r.offset+length]
	r.offset += length
	return value, nil
}

func (r *frameReader) byte() (byte, error) {
	value, err := r.take(1)
	if err != nil {
		return 0, err
	}
	return value[0], nil
}

func (r *frameReader) boolean() (bool, error) {
	value, err := r.byte()
	if err != nil {
		return false, err
	}
	if value > 1 {
		return false, frameError("boolean values must be 0 or 1")
	}
	return value == 1, nil
}

func (r *frameReader) varUint() (int, error) {
	var value uint32
	for index := 0; index < 5; index++ {
		part, err := r.byte()
		if err != nil {
			return 0, err
		}
		if index == 4 && part&0xf0 != 0 {
			return 0, frameError("varuint32 value out of range")
		}
		value |= uint32(part&0x7f) << (7 * index)
		if part&0x80 == 0 {
			return int(value), nil
		}
	}
	return 0, frameError("varuint32 uses more than five bytes")
}

func (r *frameReader) string(maximum int, label string) (string, error) {
	length, err := r.varUint()
	if err != nil {
		return "", err
	}
	if length > maximum {
		return "", frameError(fmt.Sprintf("%s exceeds its byte limit", label))
	}
	value, err := r.take(length)
	if err != nil {
		return "", err
	}
	if !utf8.Valid(value) {
		return "", frameError(fmt.Sprintf("%s is not valid UTF-8", label))
	}
	return string(value), nil
}

func (r *frameReader) value(kind FrameParameterKind) (any, error) {
	switch kind {
	case FrameInt64, FrameTimestamp, FrameDateTimeOffset, FrameDuration, FrameTime, FrameDateTime:
		bytes, err := r.take(8)
		if err != nil {
			return nil, err
		}
		value := int64(binary.LittleEndian.Uint64(bytes))
		if kind == FrameDateTimeOffset || kind == FrameDateTime {
			if value < 0 || value > maxDateTimeTicks {
				return nil, frameError("date ticks are out of range")
			}
		}
		if kind == FrameTime && (value < 0 || value >= ticksPerDay) {
			return nil, frameError("time ticks are out of range")
		}
		return value, nil
	case FrameFloat64:
		bytes, err := r.take(8)
		if err != nil {
			return nil, err
		}
		value := math.Float64frombits(binary.LittleEndian.Uint64(bytes))
		if math.IsNaN(value) || math.IsInf(value, 0) {
			return nil, frameError("Float64 values must be finite")
		}
		return value, nil
	case FrameBoolean:
		return r.boolean()
	case FrameString:
		return r.string(maxValueBytes, "string parameter")
	case FrameBlob:
		length, err := r.varUint()
		if err != nil {
			return nil, err
		}
		if length > maxValueBytes {
			return nil, frameError("Blob exceeds its byte limit")
		}
		value, err := r.take(length)
		if err != nil {
			return nil, err
		}
		return append([]byte(nil), value...), nil
	case FrameDecimal:
		var value [4]int32
		for index := range value {
			bytes, err := r.take(4)
			if err != nil {
				return nil, err
			}
			value[index] = int32(binary.LittleEndian.Uint32(bytes))
		}
		if !validDecimalBits(value) {
			return nil, frameError("decimal flags use a reserved bit or scale above 28")
		}
		return value, nil
	case FrameVector:
		dimension, err := r.varUint()
		if err != nil {
			return nil, err
		}
		if dimension <= 0 || dimension > maxVectorDimension || dimension > r.remaining()/4 {
			return nil, frameError("vector dimension is invalid")
		}
		values := make([]float32, dimension)
		for index := range values {
			bytes, err := r.take(4)
			if err != nil {
				return nil, err
			}
			values[index] = math.Float32frombits(binary.LittleEndian.Uint32(bytes))
			if math.IsNaN(float64(values[index])) || math.IsInf(float64(values[index]), 0) {
				return nil, frameError("vector values must be finite")
			}
		}
		return values, nil
	default:
		return nil, frameError("unknown SQL Execute parameter type")
	}
}

func equalFold(left, right string) bool {
	if len(left) != len(right) {
		return false
	}
	for index := range left {
		if foldAscii(left[index]) != foldAscii(right[index]) {
			return false
		}
	}
	return true
}

func foldAscii(value byte) byte {
	if value >= 'A' && value <= 'Z' {
		return value + ('a' - 'A')
	}
	return value
}

func stringTrimSpace(value string) string {
	start, end := 0, len(value)
	for start < end && value[start] <= ' ' {
		start++
	}
	for end > start && value[end-1] <= ' ' {
		end--
	}
	return value[start:end]
}
