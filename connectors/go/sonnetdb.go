//go:build cgo && (windows || linux)

package sonnetdb

/*
#cgo CFLAGS: -I${SRCDIR}/../c/include
#cgo windows LDFLAGS: -lSonnetDB.Native
#cgo linux LDFLAGS: -l:SonnetDB.Native.so

#include <stdint.h>
#include <stdlib.h>
#include "sonnetdb.h"

static char* sonnetdb_go_alloc(int32_t length)
{
    return (char*)malloc((size_t)length);
}

static void sonnetdb_go_free(char* value)
{
    free(value);
}
*/
import "C"

import (
	"errors"
	"fmt"
	"runtime"
	"unsafe"
)

const nativeStringBufferSize = 4096
const maxInt32 = 1<<31 - 1

// ErrClosed is returned when an operation is attempted on a closed native handle.
var ErrClosed = errors.New("sonnetdb: native handle is closed")

// Error represents an error returned by the SonnetDB native ABI.
type Error struct {
	Message string
}

// Error returns the native error message.
func (e *Error) Error() string {
	return e.Message
}

// ValueType describes a value kind exposed by the SonnetDB C ABI.
type ValueType int

const (
	// ValueNull represents a NULL value.
	ValueNull ValueType = 0

	// ValueInt64 represents a signed 64-bit integer.
	ValueInt64 ValueType = 1

	// ValueDouble represents a 64-bit floating-point value.
	ValueDouble ValueType = 2

	// ValueBool represents a boolean value.
	ValueBool ValueType = 3

	// ValueText represents UTF-8 text.
	ValueText ValueType = 4
)

// String returns a stable display name for the value type.
func (t ValueType) String() string {
	switch t {
	case ValueNull:
		return "NULL"
	case ValueInt64:
		return "INT64"
	case ValueDouble:
		return "DOUBLE"
	case ValueBool:
		return "BOOL"
	case ValueText:
		return "TEXT"
	default:
		return fmt.Sprintf("UNKNOWN(%d)", int(t))
	}
}

// Value is the natural Go representation of one SonnetDB cell.
type Value any

// Connection is an embedded SonnetDB connection backed by the native C ABI.
type Connection struct {
	handle *C.sonnetdb_connection
}

// BulkOptions configures one bulk ingest operation.
type BulkOptions struct {
	Measurement string
	OnError     string
	Flush       string
}

// KV is a SonnetDB keyspace/namespace handle backed by the native C ABI.
type KV struct {
	handle *C.sonnetdb_kv
}

// DocumentCollection is a SonnetDB document collection handle backed by the native C ABI.
type DocumentCollection struct {
	handle *C.sonnetdb_doc
}

// KVEntry is a materialized key/value entry.
type KVEntry struct {
	Key             string
	Value           []byte
	Version         int64
	ExpiresAtUnixMs int64
}

// KVTTL is a Redis-style TTL result. Milliseconds is -2 for missing keys and -1
// for keys without expiration.
type KVTTL struct {
	Milliseconds    int64
	ExpiresAtUnixMs int64
}

// KVCASResult describes a compare-and-set operation.
type KVCASResult struct {
	Swapped        bool
	CurrentVersion int64
	NewVersion     int64
}

// Result is a forward-only cursor over one SQL execution result.
type Result struct {
	handle *C.sonnetdb_result
}

type nativeStringKind int

const (
	nativeStringVersion nativeStringKind = iota
	nativeStringLastError
)

// Open opens an embedded SonnetDB database directory.
func Open(dataSource string) (*Connection, error) {
	if dataSource == "" {
		return nil, errors.New("sonnetdb: data source must not be empty")
	}

	cDataSource := C.CString(dataSource)
	defer C.sonnetdb_go_free(cDataSource)

	handle := C.sonnetdb_open(cDataSource)
	if handle == nil {
		return nil, lastError("sonnetdb_open failed")
	}

	connection := &Connection{handle: handle}
	runtime.SetFinalizer(connection, func(value *Connection) {
		_ = value.Close()
	})
	return connection, nil
}

// Version returns the loaded SonnetDB native library version.
func Version() (string, error) {
	return copyNativeString(nativeStringVersion)
}

// LastError returns the last native error message for the current native thread.
func LastError() string {
	value, err := copyNativeString(nativeStringLastError)
	if err != nil {
		return ""
	}
	return value
}

// Close releases the native connection handle. Calling Close more than once is safe.
func (c *Connection) Close() error {
	if c == nil || c.handle == nil {
		return nil
	}

	handle := c.handle
	c.handle = nil
	runtime.SetFinalizer(c, nil)
	C.sonnetdb_close(handle)
	if message := LastError(); message != "" {
		return &Error{Message: message}
	}
	return nil
}

// Execute executes one SQL statement and returns a cursor or non-query result.
func (c *Connection) Execute(sql string) (*Result, error) {
	handle, err := c.ensureOpen()
	if err != nil {
		return nil, err
	}
	if sql == "" {
		return nil, errors.New("sonnetdb: SQL must not be empty")
	}

	cSQL := C.CString(sql)
	defer C.sonnetdb_go_free(cSQL)

	result := C.sonnetdb_execute(handle, cSQL)
	if result == nil {
		return nil, lastError("sonnetdb_execute failed")
	}

	cursor := &Result{handle: result}
	runtime.SetFinalizer(cursor, func(value *Result) {
		_ = value.Close()
	})
	return cursor, nil
}

// ExecuteNonQuery executes SQL and returns the affected row count.
func (c *Connection) ExecuteNonQuery(sql string) (int, error) {
	result, err := c.Execute(sql)
	if err != nil {
		return 0, err
	}
	defer result.Close()

	return result.RecordsAffected()
}

// ExecuteBulk ingests a bulk payload synchronously and returns the affected row count.
func (c *Connection) ExecuteBulk(payload string, options ...BulkOptions) (int, error) {
	handle, err := c.ensureOpen()
	if err != nil {
		return 0, err
	}
	if payload == "" {
		return 0, errors.New("sonnetdb: bulk payload must not be empty")
	}

	cPayload := C.CString(payload)
	defer C.sonnetdb_go_free(cPayload)

	bulk := C.sonnetdb_bulk_create(cPayload)
	if bulk == nil {
		return 0, lastError("sonnetdb_bulk_create failed")
	}
	defer C.sonnetdb_bulk_free(bulk)

	if len(options) > 0 {
		if err := configureBulk(bulk, options[0]); err != nil {
			return 0, err
		}
	}

	result := C.sonnetdb_bulk_execute(handle, bulk)
	if result == nil {
		return 0, lastError("sonnetdb_bulk_execute failed")
	}

	cursor := &Result{handle: result}
	affected, err := cursor.RecordsAffected()
	if closeErr := cursor.Close(); err == nil && closeErr != nil {
		err = closeErr
	}
	return affected, err
}

// Flush forces pending data to durable storage through the native engine.
func (c *Connection) Flush() error {
	handle, err := c.ensureOpen()
	if err != nil {
		return err
	}
	if C.sonnetdb_flush(handle) != 0 {
		return lastError("sonnetdb_flush failed")
	}
	return nil
}

// OpenDocumentCollection opens a document collection handle.
func (c *Connection) OpenDocumentCollection(collection string) (*DocumentCollection, error) {
	handle, err := c.ensureOpen()
	if err != nil {
		return nil, err
	}
	if collection == "" {
		return nil, errors.New("sonnetdb: document collection must not be empty")
	}

	cCollection := C.CString(collection)
	defer C.sonnetdb_go_free(cCollection)

	docHandle := C.sonnetdb_doc_open(handle, cCollection)
	if docHandle == nil {
		return nil, lastError("sonnetdb_doc_open failed")
	}

	documents := &DocumentCollection{handle: docHandle}
	runtime.SetFinalizer(documents, func(value *DocumentCollection) {
		_ = value.Close()
	})
	return documents, nil
}

// OpenKV opens a KV keyspace handle. The optional namespace scopes keys with a
// logical prefix; omit it or pass an empty string for the root namespace.
func (c *Connection) OpenKV(keyspace string, namespace ...string) (*KV, error) {
	handle, err := c.ensureOpen()
	if err != nil {
		return nil, err
	}
	if keyspace == "" {
		return nil, errors.New("sonnetdb: keyspace must not be empty")
	}

	ns := ""
	if len(namespace) > 0 {
		ns = namespace[0]
	}

	cKeyspace := C.CString(keyspace)
	defer C.sonnetdb_go_free(cKeyspace)

	var cNamespace *C.char
	if ns != "" {
		cNamespace = C.CString(ns)
		defer C.sonnetdb_go_free(cNamespace)
	}

	kvHandle := C.sonnetdb_kv_open(handle, cKeyspace, cNamespace)
	if kvHandle == nil {
		return nil, lastError("sonnetdb_kv_open failed")
	}

	kv := &KV{handle: kvHandle}
	runtime.SetFinalizer(kv, func(value *KV) {
		_ = value.Close()
	})
	return kv, nil
}

// Close releases the native KV handle. Calling Close more than once is safe.
func (kv *KV) Close() error {
	if kv == nil || kv.handle == nil {
		return nil
	}

	handle := kv.handle
	kv.handle = nil
	runtime.SetFinalizer(kv, nil)
	C.sonnetdb_kv_close(handle)
	if message := LastError(); message != "" {
		return &Error{Message: message}
	}
	return nil
}

// Get returns a materialized KV entry, or nil when the key is missing.
func (kv *KV) Get(key string) (*KVEntry, error) {
	handle, err := kv.ensureOpen()
	if err != nil {
		return nil, err
	}
	if key == "" {
		return nil, errors.New("sonnetdb: key must not be empty")
	}

	cKey := C.CString(key)
	defer C.sonnetdb_go_free(cKey)

	entry := C.sonnetdb_kv_get(handle, cKey)
	if entry == nil {
		if message := LastError(); message != "" {
			return nil, &Error{Message: message}
		}
		return nil, nil
	}
	defer C.sonnetdb_kv_entry_free(entry)

	return materializeKVEntry(entry)
}

// Set writes a binary value and returns the written version. expiresAtUnixMs is
// optional; omit it or pass a negative value for no expiration.
func (kv *KV) Set(key string, value []byte, expiresAtUnixMs ...int64) (int64, error) {
	handle, err := kv.ensureOpen()
	if err != nil {
		return 0, err
	}
	if key == "" {
		return 0, errors.New("sonnetdb: key must not be empty")
	}
	length, err := checkedByteLength(value)
	if err != nil {
		return 0, err
	}

	expires := int64(-1)
	if len(expiresAtUnixMs) > 0 {
		expires = expiresAtUnixMs[0]
	}

	cKey := C.CString(key)
	defer C.sonnetdb_go_free(cKey)

	var ptr unsafe.Pointer
	if len(value) > 0 {
		ptr = unsafe.Pointer(&value[0])
	}

	version := int64(C.sonnetdb_kv_set(handle, cKey, ptr, C.int32_t(length), C.int64_t(expires)))
	if version < 0 {
		return 0, lastError("sonnetdb_kv_set failed")
	}
	return version, nil
}

// Delete removes a key and returns whether an existing key was removed.
func (kv *KV) Delete(key string) (bool, error) {
	handle, err := kv.ensureOpen()
	if err != nil {
		return false, err
	}
	if key == "" {
		return false, errors.New("sonnetdb: key must not be empty")
	}

	cKey := C.CString(key)
	defer C.sonnetdb_go_free(cKey)

	code := int(C.sonnetdb_kv_delete(handle, cKey))
	if code < 0 {
		return false, lastError("sonnetdb_kv_delete failed")
	}
	return code == 1, nil
}

// ScanPrefix returns a materialized snapshot of entries matching prefix. Pass
// limit <= 0 to use the keyspace default.
func (kv *KV) ScanPrefix(prefix string, limit int) ([]KVEntry, error) {
	handle, err := kv.ensureOpen()
	if err != nil {
		return nil, err
	}
	if limit > maxInt32 {
		return nil, errors.New("sonnetdb: scan limit is out of range")
	}

	cPrefix := C.CString(prefix)
	defer C.sonnetdb_go_free(cPrefix)

	scan := C.sonnetdb_kv_scan_prefix(handle, cPrefix, C.int32_t(limit))
	if scan == nil {
		return nil, lastError("sonnetdb_kv_scan_prefix failed")
	}
	defer C.sonnetdb_kv_scan_free(scan)

	var entries []KVEntry
	for {
		next := int(C.sonnetdb_kv_scan_next(scan))
		if next < 0 {
			return nil, lastError("sonnetdb_kv_scan_next failed")
		}
		if next == 0 {
			return entries, nil
		}

		entry, err := materializeKVScanEntry(scan)
		if err != nil {
			return nil, err
		}
		entries = append(entries, *entry)
	}
}

// TTL returns the remaining key TTL in milliseconds.
func (kv *KV) TTL(key string) (KVTTL, error) {
	handle, err := kv.ensureOpen()
	if err != nil {
		return KVTTL{}, err
	}
	if key == "" {
		return KVTTL{}, errors.New("sonnetdb: key must not be empty")
	}

	cKey := C.CString(key)
	defer C.sonnetdb_go_free(cKey)

	var expires C.int64_t
	ms := int64(C.sonnetdb_kv_ttl(handle, cKey, &expires))
	if ms < -2 {
		return KVTTL{}, lastError("sonnetdb_kv_ttl failed")
	}
	return KVTTL{Milliseconds: ms, ExpiresAtUnixMs: int64(expires)}, nil
}

// ExpireAt sets an absolute UTC expiration time in Unix milliseconds.
func (kv *KV) ExpireAt(key string, expiresAtUnixMs int64) (bool, error) {
	handle, err := kv.ensureOpen()
	if err != nil {
		return false, err
	}
	if key == "" {
		return false, errors.New("sonnetdb: key must not be empty")
	}

	cKey := C.CString(key)
	defer C.sonnetdb_go_free(cKey)

	code := int(C.sonnetdb_kv_expire_at(handle, cKey, C.int64_t(expiresAtUnixMs)))
	if code < 0 {
		return false, lastError("sonnetdb_kv_expire_at failed")
	}
	return code == 1, nil
}

// Persist removes a key expiration.
func (kv *KV) Persist(key string) (bool, error) {
	handle, err := kv.ensureOpen()
	if err != nil {
		return false, err
	}
	if key == "" {
		return false, errors.New("sonnetdb: key must not be empty")
	}

	cKey := C.CString(key)
	defer C.sonnetdb_go_free(cKey)

	code := int(C.sonnetdb_kv_persist(handle, cKey))
	if code < 0 {
		return false, lastError("sonnetdb_kv_persist failed")
	}
	return code == 1, nil
}

// Incr atomically increments a UTF-8 integer value.
func (kv *KV) Incr(key string, delta int64) (value int64, version int64, err error) {
	handle, err := kv.ensureOpen()
	if err != nil {
		return 0, 0, err
	}
	if key == "" {
		return 0, 0, errors.New("sonnetdb: key must not be empty")
	}

	cKey := C.CString(key)
	defer C.sonnetdb_go_free(cKey)

	var nativeValue C.int64_t
	var nativeVersion C.int64_t
	if C.sonnetdb_kv_incr(handle, cKey, C.int64_t(delta), &nativeValue, &nativeVersion) != 0 {
		return 0, 0, lastError("sonnetdb_kv_incr failed")
	}
	return int64(nativeValue), int64(nativeVersion), nil
}

// CAS writes value only when the current version matches expectedVersion.
func (kv *KV) CAS(key string, expectedVersion int64, value []byte, expiresAtUnixMs ...int64) (KVCASResult, error) {
	handle, err := kv.ensureOpen()
	if err != nil {
		return KVCASResult{}, err
	}
	if key == "" {
		return KVCASResult{}, errors.New("sonnetdb: key must not be empty")
	}
	length, err := checkedByteLength(value)
	if err != nil {
		return KVCASResult{}, err
	}

	expires := int64(-1)
	if len(expiresAtUnixMs) > 0 {
		expires = expiresAtUnixMs[0]
	}

	cKey := C.CString(key)
	defer C.sonnetdb_go_free(cKey)

	var ptr unsafe.Pointer
	if len(value) > 0 {
		ptr = unsafe.Pointer(&value[0])
	}
	var current C.int64_t
	var next C.int64_t
	code := int(C.sonnetdb_kv_cas(
		handle,
		cKey,
		C.int64_t(expectedVersion),
		ptr,
		C.int32_t(length),
		C.int64_t(expires),
		&current,
		&next))
	if code < 0 {
		return KVCASResult{}, lastError("sonnetdb_kv_cas failed")
	}
	return KVCASResult{
		Swapped:        code == 1,
		CurrentVersion: int64(current),
		NewVersion:     int64(next),
	}, nil
}

// Close releases the native document collection handle. Calling Close more than once is safe.
func (d *DocumentCollection) Close() error {
	if d == nil || d.handle == nil {
		return nil
	}

	handle := d.handle
	d.handle = nil
	runtime.SetFinalizer(d, nil)
	C.sonnetdb_doc_close(handle)
	if message := LastError(); message != "" {
		return &Error{Message: message}
	}
	return nil
}

// CreateCollection creates the collection and returns the native JSON response.
func (d *DocumentCollection) CreateCollection(optionsJSON ...string) (string, error) {
	payload := ""
	if len(optionsJSON) > 0 {
		payload = optionsJSON[0]
	}
	return d.executeDocumentJSON(documentCreateCollection, payload, false, "sonnetdb_doc_create_collection failed")
}

// DropCollection drops the collection and returns whether it existed.
func (d *DocumentCollection) DropCollection() (bool, error) {
	handle, err := d.ensureOpen()
	if err != nil {
		return false, err
	}

	code := int(C.sonnetdb_doc_drop_collection(handle))
	if code < 0 {
		return false, lastError("sonnetdb_doc_drop_collection failed")
	}
	return code == 1, nil
}

// Insert inserts one or more documents from a JSON request and returns the native JSON response.
func (d *DocumentCollection) Insert(payloadJSON string) (string, error) {
	return d.executeDocumentJSON(documentInsert, payloadJSON, true, "sonnetdb_doc_insert failed")
}

// Update updates documents from a JSON request and returns the native JSON response.
func (d *DocumentCollection) Update(payloadJSON string) (string, error) {
	return d.executeDocumentJSON(documentUpdate, payloadJSON, true, "sonnetdb_doc_update failed")
}

// Delete deletes documents from a JSON request and returns the native JSON response.
func (d *DocumentCollection) Delete(payloadJSON string) (string, error) {
	return d.executeDocumentJSON(documentDelete, payloadJSON, true, "sonnetdb_doc_delete failed")
}

// FindPage finds a page of documents and returns the native JSON response.
func (d *DocumentCollection) FindPage(payloadJSON ...string) (string, error) {
	payload := ""
	if len(payloadJSON) > 0 {
		payload = payloadJSON[0]
	}
	return d.executeDocumentJSON(documentFindPage, payload, false, "sonnetdb_doc_find_page failed")
}

// Aggregate runs a document aggregation pipeline and returns the native JSON response.
func (d *DocumentCollection) Aggregate(payloadJSON string) (string, error) {
	return d.executeDocumentJSON(documentAggregate, payloadJSON, true, "sonnetdb_doc_aggregate failed")
}

// Close releases the native result handle. Calling Close more than once is safe.
func (r *Result) Close() error {
	if r == nil || r.handle == nil {
		return nil
	}

	handle := r.handle
	r.handle = nil
	runtime.SetFinalizer(r, nil)
	C.sonnetdb_result_free(handle)
	if message := LastError(); message != "" {
		return &Error{Message: message}
	}
	return nil
}

// RecordsAffected returns INSERT/DELETE affected rows. SELECT results return -1.
func (r *Result) RecordsAffected() (int, error) {
	handle, err := r.ensureOpen()
	if err != nil {
		return 0, err
	}

	value := int(C.sonnetdb_result_records_affected(handle))
	if value < 0 {
		if message := LastError(); message != "" {
			return 0, &Error{Message: message}
		}
	}
	return value, nil
}

// ColumnCount returns the number of columns in the result.
func (r *Result) ColumnCount() (int, error) {
	handle, err := r.ensureOpen()
	if err != nil {
		return 0, err
	}

	value := int(C.sonnetdb_result_column_count(handle))
	if value < 0 {
		return 0, lastError("sonnetdb_result_column_count failed")
	}
	return value, nil
}

// ColumnName returns a result column name by zero-based ordinal.
func (r *Result) ColumnName(ordinal int) (string, error) {
	handle, err := r.ensureOpen()
	if err != nil {
		return "", err
	}
	if err := validateOrdinal(ordinal); err != nil {
		return "", err
	}

	value := C.sonnetdb_result_column_name(handle, C.int32_t(ordinal))
	if value == nil {
		return "", lastError("sonnetdb_result_column_name failed")
	}
	return C.GoString(value), nil
}

// Columns returns all result column names.
func (r *Result) Columns() ([]string, error) {
	count, err := r.ColumnCount()
	if err != nil {
		return nil, err
	}

	columns := make([]string, count)
	for i := 0; i < count; i++ {
		columns[i], err = r.ColumnName(i)
		if err != nil {
			return nil, err
		}
	}
	return columns, nil
}

// Next advances the cursor to the next row.
func (r *Result) Next() (bool, error) {
	handle, err := r.ensureOpen()
	if err != nil {
		return false, err
	}

	value := int(C.sonnetdb_result_next(handle))
	if value < 0 {
		return false, lastError("sonnetdb_result_next failed")
	}
	return value == 1, nil
}

// ValueType returns the native type of the current row value.
func (r *Result) ValueType(ordinal int) (ValueType, error) {
	handle, err := r.ensureOpen()
	if err != nil {
		return ValueNull, err
	}
	if err := validateOrdinal(ordinal); err != nil {
		return ValueNull, err
	}

	code := int(C.sonnetdb_result_value_type(handle, C.int32_t(ordinal)))
	if code < 0 {
		return ValueNull, lastError("sonnetdb_result_value_type failed")
	}
	return valueTypeFromCode(code)
}

// Int64 reads the current row value as int64.
func (r *Result) Int64(ordinal int) (int64, error) {
	valueType, err := r.ValueType(ordinal)
	if err != nil {
		return 0, err
	}
	if valueType != ValueInt64 {
		return 0, fmt.Errorf("sonnetdb: column %d is %s, not INT64", ordinal, valueType)
	}

	handle, err := r.ensureOpen()
	if err != nil {
		return 0, err
	}
	return int64(C.sonnetdb_result_value_int64(handle, C.int32_t(ordinal))), nil
}

// Double reads the current row value as float64.
func (r *Result) Double(ordinal int) (float64, error) {
	valueType, err := r.ValueType(ordinal)
	if err != nil {
		return 0, err
	}
	if valueType != ValueDouble && valueType != ValueInt64 {
		return 0, fmt.Errorf("sonnetdb: column %d is %s, not DOUBLE", ordinal, valueType)
	}

	handle, err := r.ensureOpen()
	if err != nil {
		return 0, err
	}
	return float64(C.sonnetdb_result_value_double(handle, C.int32_t(ordinal))), nil
}

// Bool reads the current row value as bool.
func (r *Result) Bool(ordinal int) (bool, error) {
	valueType, err := r.ValueType(ordinal)
	if err != nil {
		return false, err
	}
	if valueType != ValueBool {
		return false, fmt.Errorf("sonnetdb: column %d is %s, not BOOL", ordinal, valueType)
	}

	handle, err := r.ensureOpen()
	if err != nil {
		return false, err
	}
	value := int(C.sonnetdb_result_value_bool(handle, C.int32_t(ordinal)))
	if value < 0 {
		return false, lastError("sonnetdb_result_value_bool failed")
	}
	return value != 0, nil
}

// Text reads the current row value as UTF-8 text. The second return value is false for NULL.
func (r *Result) Text(ordinal int) (string, bool, error) {
	valueType, err := r.ValueType(ordinal)
	if err != nil {
		return "", false, err
	}
	if valueType == ValueNull {
		return "", false, nil
	}

	handle, err := r.ensureOpen()
	if err != nil {
		return "", false, err
	}
	value := C.sonnetdb_result_value_text(handle, C.int32_t(ordinal))
	if value == nil {
		return "", false, lastError("sonnetdb_result_value_text failed")
	}
	return C.GoString(value), true, nil
}

// Value reads the current row value using the natural Go type.
func (r *Result) Value(ordinal int) (Value, error) {
	valueType, err := r.ValueType(ordinal)
	if err != nil {
		return nil, err
	}

	switch valueType {
	case ValueNull:
		return nil, nil
	case ValueInt64:
		return r.Int64(ordinal)
	case ValueDouble:
		return r.Double(ordinal)
	case ValueBool:
		return r.Bool(ordinal)
	case ValueText:
		value, _, err := r.Text(ordinal)
		return value, err
	default:
		return nil, fmt.Errorf("sonnetdb: unsupported value type %s", valueType)
	}
}

func (c *Connection) ensureOpen() (*C.sonnetdb_connection, error) {
	if c == nil || c.handle == nil {
		return nil, ErrClosed
	}
	return c.handle, nil
}

func (kv *KV) ensureOpen() (*C.sonnetdb_kv, error) {
	if kv == nil || kv.handle == nil {
		return nil, ErrClosed
	}
	return kv.handle, nil
}

func (d *DocumentCollection) ensureOpen() (*C.sonnetdb_doc, error) {
	if d == nil || d.handle == nil {
		return nil, ErrClosed
	}
	return d.handle, nil
}

func (r *Result) ensureOpen() (*C.sonnetdb_result, error) {
	if r == nil || r.handle == nil {
		return nil, ErrClosed
	}
	return r.handle, nil
}

type documentOperation int

const (
	documentCreateCollection documentOperation = iota
	documentInsert
	documentUpdate
	documentDelete
	documentFindPage
	documentAggregate
)

func configureBulk(bulk *C.sonnetdb_bulk, options BulkOptions) error {
	if err := setBulkString(bulk, options.Measurement, "sonnetdb_bulk_set_measurement", func(value *C.char) C.int32_t {
		return C.sonnetdb_bulk_set_measurement(bulk, value)
	}); err != nil {
		return err
	}
	if err := setBulkString(bulk, options.OnError, "sonnetdb_bulk_set_onerror", func(value *C.char) C.int32_t {
		return C.sonnetdb_bulk_set_onerror(bulk, value)
	}); err != nil {
		return err
	}
	return setBulkString(bulk, options.Flush, "sonnetdb_bulk_set_flush", func(value *C.char) C.int32_t {
		return C.sonnetdb_bulk_set_flush(bulk, value)
	})
}

func setBulkString(_ *C.sonnetdb_bulk, value string, fallback string, setter func(*C.char) C.int32_t) error {
	if value == "" {
		return nil
	}

	cValue := C.CString(value)
	defer C.sonnetdb_go_free(cValue)
	if setter(cValue) != 0 {
		return lastError(fallback)
	}
	return nil
}

func (d *DocumentCollection) executeDocumentJSON(operation documentOperation, payload string, required bool, fallback string) (string, error) {
	handle, err := d.ensureOpen()
	if err != nil {
		return "", err
	}
	if required && payload == "" {
		return "", errors.New("sonnetdb: document JSON payload must not be empty")
	}

	var cPayload *C.char
	if payload != "" {
		cPayload = C.CString(payload)
		defer C.sonnetdb_go_free(cPayload)
	}

	var result *C.sonnetdb_doc_result
	switch operation {
	case documentCreateCollection:
		result = C.sonnetdb_doc_create_collection(handle, cPayload)
	case documentInsert:
		result = C.sonnetdb_doc_insert(handle, cPayload)
	case documentUpdate:
		result = C.sonnetdb_doc_update(handle, cPayload)
	case documentDelete:
		result = C.sonnetdb_doc_delete(handle, cPayload)
	case documentFindPage:
		result = C.sonnetdb_doc_find_page(handle, cPayload)
	case documentAggregate:
		result = C.sonnetdb_doc_aggregate(handle, cPayload)
	default:
		return "", errors.New("sonnetdb: unknown document operation")
	}
	if result == nil {
		return "", lastError(fallback)
	}
	defer C.sonnetdb_doc_result_free(result)

	return copyDocumentJSON(result, fallback)
}

func copyDocumentJSON(result *C.sonnetdb_doc_result, fallback string) (string, error) {
	required := int(C.sonnetdb_doc_result_json_length(result))
	if required < 0 {
		return "", lastError(fallback)
	}
	if required >= maxInt32 {
		return "", errors.New("sonnetdb: document JSON response is out of range")
	}

	buffer := C.sonnetdb_go_alloc(C.int32_t(required + 1))
	if buffer == nil {
		return "", errors.New("sonnetdb: cannot allocate document JSON buffer")
	}
	defer C.sonnetdb_go_free(buffer)

	copied := int(C.sonnetdb_doc_result_copy_json(result, buffer, C.int32_t(required+1)))
	if copied < 0 {
		return "", lastError(fallback)
	}
	return C.GoString(buffer), nil
}

func materializeKVEntry(entry *C.sonnetdb_kv_entry) (*KVEntry, error) {
	key := C.sonnetdb_kv_entry_key(entry)
	if key == nil {
		return nil, lastError("sonnetdb_kv_entry_key failed")
	}

	value, err := copyKVEntryValue(entry)
	if err != nil {
		return nil, err
	}

	return &KVEntry{
		Key:             C.GoString(key),
		Value:           value,
		Version:         int64(C.sonnetdb_kv_entry_version(entry)),
		ExpiresAtUnixMs: int64(C.sonnetdb_kv_entry_expires_at_unix_ms(entry)),
	}, nil
}

func materializeKVScanEntry(scan *C.sonnetdb_kv_scan) (*KVEntry, error) {
	key := C.sonnetdb_kv_scan_key(scan)
	if key == nil {
		return nil, lastError("sonnetdb_kv_scan_key failed")
	}

	value, err := copyKVScanValue(scan)
	if err != nil {
		return nil, err
	}

	return &KVEntry{
		Key:             C.GoString(key),
		Value:           value,
		Version:         int64(C.sonnetdb_kv_scan_version(scan)),
		ExpiresAtUnixMs: int64(C.sonnetdb_kv_scan_expires_at_unix_ms(scan)),
	}, nil
}

func copyKVEntryValue(entry *C.sonnetdb_kv_entry) ([]byte, error) {
	required := int(C.sonnetdb_kv_entry_value_length(entry))
	if required < 0 {
		return nil, lastError("sonnetdb_kv_entry_value_length failed")
	}
	value := make([]byte, required)
	var ptr unsafe.Pointer
	if len(value) > 0 {
		ptr = unsafe.Pointer(&value[0])
	}
	copied := int(C.sonnetdb_kv_entry_copy_value(entry, ptr, C.int32_t(required)))
	if copied < 0 {
		return nil, lastError("sonnetdb_kv_entry_copy_value failed")
	}
	return value, nil
}

func copyKVScanValue(scan *C.sonnetdb_kv_scan) ([]byte, error) {
	required := int(C.sonnetdb_kv_scan_value_length(scan))
	if required < 0 {
		return nil, lastError("sonnetdb_kv_scan_value_length failed")
	}
	value := make([]byte, required)
	var ptr unsafe.Pointer
	if len(value) > 0 {
		ptr = unsafe.Pointer(&value[0])
	}
	copied := int(C.sonnetdb_kv_scan_copy_value(scan, ptr, C.int32_t(required)))
	if copied < 0 {
		return nil, lastError("sonnetdb_kv_scan_copy_value failed")
	}
	return value, nil
}

func checkedByteLength(value []byte) (int, error) {
	if len(value) > maxInt32 {
		return 0, errors.New("sonnetdb: value length is out of range")
	}
	return len(value), nil
}

func validateOrdinal(ordinal int) error {
	if ordinal < 0 || ordinal > maxInt32 {
		return fmt.Errorf("sonnetdb: column ordinal %d is out of range", ordinal)
	}
	return nil
}

func valueTypeFromCode(code int) (ValueType, error) {
	switch ValueType(code) {
	case ValueNull, ValueInt64, ValueDouble, ValueBool, ValueText:
		return ValueType(code), nil
	default:
		return ValueNull, fmt.Errorf("sonnetdb: unknown value type code %d", code)
	}
}

func lastError(fallback string) error {
	message := LastError()
	if message == "" {
		message = fallback
	}
	return &Error{Message: message}
}

func copyNativeString(kind nativeStringKind) (string, error) {
	value, required, err := readNativeString(kind, nativeStringBufferSize)
	if err != nil {
		return "", err
	}
	if required < nativeStringBufferSize {
		return value, nil
	}
	value, _, err = readNativeString(kind, required+1)
	return value, err
}

func readNativeString(kind nativeStringKind, length int) (string, int, error) {
	if length <= 0 || length > maxInt32 {
		return "", 0, errors.New("sonnetdb: native string buffer length is out of range")
	}

	buffer := C.sonnetdb_go_alloc(C.int32_t(length))
	if buffer == nil {
		return "", 0, errors.New("sonnetdb: cannot allocate native string buffer")
	}
	defer C.sonnetdb_go_free(buffer)

	required := int(callNativeString(kind, buffer, C.int32_t(length)))
	if required < 0 {
		return "", required, lastError("sonnetdb native string copy failed")
	}
	return C.GoString(buffer), required, nil
}

func callNativeString(kind nativeStringKind, buffer *C.char, length C.int32_t) C.int32_t {
	switch kind {
	case nativeStringVersion:
		return C.sonnetdb_version(buffer, length)
	case nativeStringLastError:
		return C.sonnetdb_last_error(buffer, length)
	default:
		return -1
	}
}
