//go:build cgo && (windows || linux)

package sonnetdb

import (
	"math"
	"testing"
)

func TestMinimalSQLExecuteFrameMatchesProtocolBytes(t *testing.T) {
	frame, err := EncodeSQLExecuteFrame(42, "d", "S", nil)
	if err != nil {
		t.Fatal(err)
	}
	want := []byte{0x06, 0, 0, 0, 1, 3, 2, 0, 0x2a, 0, 0, 0, 1, 1, 'd', 1, 'S', 0}
	if string(frame) != string(want) {
		t.Fatalf("frame bytes differ: got %x want %x", frame, want)
	}
	request, err := DecodeSQLExecuteFrame(frame)
	if err != nil {
		t.Fatal(err)
	}
	if request.Database != "d" || request.SQL != "S" || len(request.Parameters) != 0 {
		t.Fatalf("unexpected request: %#v", request)
	}
}

func TestAllSQLExecuteParameterKindsRoundTrip(t *testing.T) {
	parameters := []FrameParameter{
		{Name: "p0", Kind: FrameNull},
		{Name: "p1", Kind: FrameInt64, Value: int64(-7)},
		{Name: "p2", Kind: FrameFloat64, Value: float64(1.5)},
		{Name: "p3", Kind: FrameBoolean, Value: true},
		{Name: "p4", Kind: FrameString, Value: "你好"},
		{Name: "p5", Kind: FrameBlob, Value: []byte{0, 255}},
		{Name: "p6", Kind: FrameTimestamp, Value: int64(123456789)},
		{Name: "p7", Kind: FrameDecimal, Value: [4]int32{3, 0, 0, 0}},
		{Name: "p8", Kind: FrameDateTimeOffset, Value: int64(638000000000000000)},
		{Name: "p9", Kind: FrameDuration, Value: int64(-123)},
		{Name: "p10", Kind: FrameTime, Value: int64(123456789)},
		{Name: "p11", Kind: FrameVector, Value: []float32{1, -2.5}},
		{Name: "p12", Kind: FrameDateTime, Value: int64(638000000000000000)},
	}
	frame, err := EncodeSQLExecuteFrame(9, "demo", "INSERT", parameters)
	if err != nil {
		t.Fatal(err)
	}
	request, err := DecodeSQLExecuteFrame(frame)
	if err != nil {
		t.Fatal(err)
	}
	if request.Database != "demo" || request.SQL != "INSERT" {
		t.Fatalf("unexpected request identity: %#v", request)
	}
	if len(request.Parameters) != len(parameters) {
		t.Fatalf("parameter count: got %d want %d", len(request.Parameters), len(parameters))
	}
	for index := range parameters {
		if request.Parameters[index].Name != parameters[index].Name || request.Parameters[index].Kind != parameters[index].Kind {
			t.Fatalf("parameter %d identity differs: got %#v want %#v", index, request.Parameters[index], parameters[index])
		}
		if !equalFrameValue(request.Parameters[index].Value, parameters[index].Value) {
			t.Fatalf("parameter %d value differs: got %#v want %#v", index, request.Parameters[index].Value, parameters[index].Value)
		}
	}
}

func TestSQLExecuteCommittedResponseRejectsUnknownOutcome(t *testing.T) {
	frame, err := EncodeSQLExecuteResponseFrame(7, 13)
	if err != nil {
		t.Fatal(err)
	}
	response, err := DecodeSQLExecuteResponseFrame(frame)
	if err != nil || response.RecordsAffected != 13 {
		t.Fatalf("response: %#v %v", response, err)
	}
	frame[len(frame)-1] = 0
	if _, err := DecodeSQLExecuteResponseFrame(frame); err == nil {
		t.Fatal("expected non-committed outcome to be rejected")
	}
}

func TestSQLExecuteFrameRejectsMalformedParameterValues(t *testing.T) {
	if _, err := EncodeSQLExecuteFrame(1, "db", "SELECT 1", []FrameParameter{{Name: "参数", Kind: FrameInt64, Value: int64(1)}}); err == nil {
		t.Fatal("expected non-ASCII parameter name to be rejected")
	}
	if _, err := EncodeSQLExecuteFrame(1, "db", "SELECT 1", []FrameParameter{{Name: "p", Kind: FrameDecimal, Value: [4]int32{0, 0, 0, 0x01000000}}}); err == nil {
		t.Fatal("expected decimal reserved bits to be rejected")
	}
	if _, err := EncodeSQLExecuteFrame(1, "db", "SELECT 1", []FrameParameter{{Name: "p", Kind: FrameFloat64, Value: math.NaN()}}); err == nil {
		t.Fatal("expected non-finite float to be rejected")
	}
	if _, err := EncodeSQLExecuteFrame(1, "db", "SELECT 1", []FrameParameter{{Name: "p", Kind: FrameString, Value: string(make([]byte, maxRequestPayloadBytes))}}); err == nil {
		t.Fatal("expected oversized payload to be rejected")
	}
}

func equalFrameValue(left, right any) bool {
	switch leftValue := left.(type) {
	case []byte:
		rightValue, ok := right.([]byte)
		if !ok || len(leftValue) != len(rightValue) {
			return false
		}
		for index := range leftValue {
			if leftValue[index] != rightValue[index] {
				return false
			}
		}
		return true
	case []float32:
		rightValue, ok := right.([]float32)
		if !ok || len(leftValue) != len(rightValue) {
			return false
		}
		for index := range leftValue {
			if leftValue[index] != rightValue[index] {
				return false
			}
		}
		return true
	default:
		return left == right
	}
}
