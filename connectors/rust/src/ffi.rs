#![allow(non_camel_case_types)]

use std::ffi::{c_char, c_double, c_int};

#[repr(C)]
pub struct sonnetdb_connection {
    _private: [u8; 0],
}

#[repr(C)]
pub struct sonnetdb_result {
    _private: [u8; 0],
}

#[repr(C)]
pub struct sonnetdb_bulk {
    _private: [u8; 0],
}

#[repr(C)]
pub struct sonnetdb_doc {
    _private: [u8; 0],
}

#[repr(C)]
pub struct sonnetdb_doc_result {
    _private: [u8; 0],
}

#[repr(C)]
pub struct sonnetdb_kv {
    _private: [u8; 0],
}

#[repr(C)]
pub struct sonnetdb_kv_entry {
    _private: [u8; 0],
}

#[repr(C)]
pub struct sonnetdb_kv_scan {
    _private: [u8; 0],
}

pub type sonnetdb_value_type = c_int;

pub const SONNETDB_TYPE_NULL: sonnetdb_value_type = 0;
pub const SONNETDB_TYPE_INT64: sonnetdb_value_type = 1;
pub const SONNETDB_TYPE_DOUBLE: sonnetdb_value_type = 2;
pub const SONNETDB_TYPE_BOOL: sonnetdb_value_type = 3;
pub const SONNETDB_TYPE_TEXT: sonnetdb_value_type = 4;

extern "C" {
    pub fn sonnetdb_open(data_source: *const c_char) -> *mut sonnetdb_connection;
    pub fn sonnetdb_close(connection: *mut sonnetdb_connection);

    pub fn sonnetdb_execute(
        connection: *mut sonnetdb_connection,
        sql: *const c_char,
    ) -> *mut sonnetdb_result;
    pub fn sonnetdb_result_free(result: *mut sonnetdb_result);

    pub fn sonnetdb_bulk_create(payload: *const c_char) -> *mut sonnetdb_bulk;
    pub fn sonnetdb_bulk_set_measurement(
        bulk: *mut sonnetdb_bulk,
        measurement: *const c_char,
    ) -> c_int;
    pub fn sonnetdb_bulk_set_onerror(
        bulk: *mut sonnetdb_bulk,
        onerror: *const c_char,
    ) -> c_int;
    pub fn sonnetdb_bulk_set_flush(bulk: *mut sonnetdb_bulk, flush: *const c_char) -> c_int;
    pub fn sonnetdb_bulk_execute(
        connection: *mut sonnetdb_connection,
        bulk: *mut sonnetdb_bulk,
    ) -> *mut sonnetdb_result;
    pub fn sonnetdb_bulk_free(bulk: *mut sonnetdb_bulk);

    pub fn sonnetdb_doc_open(
        connection: *mut sonnetdb_connection,
        collection: *const c_char,
    ) -> *mut sonnetdb_doc;
    pub fn sonnetdb_doc_close(document: *mut sonnetdb_doc);
    pub fn sonnetdb_doc_create_collection(
        document: *mut sonnetdb_doc,
        options_json: *const c_char,
    ) -> *mut sonnetdb_doc_result;
    pub fn sonnetdb_doc_drop_collection(document: *mut sonnetdb_doc) -> c_int;
    pub fn sonnetdb_doc_insert(
        document: *mut sonnetdb_doc,
        payload_json: *const c_char,
    ) -> *mut sonnetdb_doc_result;
    pub fn sonnetdb_doc_update(
        document: *mut sonnetdb_doc,
        payload_json: *const c_char,
    ) -> *mut sonnetdb_doc_result;
    pub fn sonnetdb_doc_delete(
        document: *mut sonnetdb_doc,
        payload_json: *const c_char,
    ) -> *mut sonnetdb_doc_result;
    pub fn sonnetdb_doc_find_page(
        document: *mut sonnetdb_doc,
        payload_json: *const c_char,
    ) -> *mut sonnetdb_doc_result;
    pub fn sonnetdb_doc_aggregate(
        document: *mut sonnetdb_doc,
        payload_json: *const c_char,
    ) -> *mut sonnetdb_doc_result;
    pub fn sonnetdb_doc_result_free(result: *mut sonnetdb_doc_result);
    pub fn sonnetdb_doc_result_json_length(result: *mut sonnetdb_doc_result) -> c_int;
    pub fn sonnetdb_doc_result_copy_json(
        result: *mut sonnetdb_doc_result,
        buffer: *mut c_char,
        buffer_length: c_int,
    ) -> c_int;

    pub fn sonnetdb_result_records_affected(result: *mut sonnetdb_result) -> c_int;
    pub fn sonnetdb_result_column_count(result: *mut sonnetdb_result) -> c_int;
    pub fn sonnetdb_result_column_name(
        result: *mut sonnetdb_result,
        ordinal: c_int,
    ) -> *const c_char;
    pub fn sonnetdb_result_next(result: *mut sonnetdb_result) -> c_int;

    pub fn sonnetdb_result_value_type(
        result: *mut sonnetdb_result,
        ordinal: c_int,
    ) -> sonnetdb_value_type;
    pub fn sonnetdb_result_value_int64(
        result: *mut sonnetdb_result,
        ordinal: c_int,
    ) -> i64;
    pub fn sonnetdb_result_value_double(
        result: *mut sonnetdb_result,
        ordinal: c_int,
    ) -> c_double;
    pub fn sonnetdb_result_value_bool(result: *mut sonnetdb_result, ordinal: c_int) -> c_int;
    pub fn sonnetdb_result_value_text(
        result: *mut sonnetdb_result,
        ordinal: c_int,
    ) -> *const c_char;

    pub fn sonnetdb_kv_open(
        connection: *mut sonnetdb_connection,
        keyspace: *const c_char,
        namespace: *const c_char,
    ) -> *mut sonnetdb_kv;
    pub fn sonnetdb_kv_close(kv: *mut sonnetdb_kv);
    pub fn sonnetdb_kv_get(kv: *mut sonnetdb_kv, key: *const c_char) -> *mut sonnetdb_kv_entry;
    pub fn sonnetdb_kv_set(
        kv: *mut sonnetdb_kv,
        key: *const c_char,
        value: *const std::ffi::c_void,
        value_length: c_int,
        expires_at_unix_ms: i64,
    ) -> i64;
    pub fn sonnetdb_kv_delete(kv: *mut sonnetdb_kv, key: *const c_char) -> c_int;
    pub fn sonnetdb_kv_scan_prefix(
        kv: *mut sonnetdb_kv,
        prefix: *const c_char,
        limit: c_int,
    ) -> *mut sonnetdb_kv_scan;
    pub fn sonnetdb_kv_ttl(
        kv: *mut sonnetdb_kv,
        key: *const c_char,
        expires_at_unix_ms: *mut i64,
    ) -> i64;
    pub fn sonnetdb_kv_expire_at(
        kv: *mut sonnetdb_kv,
        key: *const c_char,
        expires_at_unix_ms: i64,
    ) -> c_int;
    pub fn sonnetdb_kv_persist(kv: *mut sonnetdb_kv, key: *const c_char) -> c_int;
    pub fn sonnetdb_kv_incr(
        kv: *mut sonnetdb_kv,
        key: *const c_char,
        delta: i64,
        value: *mut i64,
        version: *mut i64,
    ) -> c_int;
    pub fn sonnetdb_kv_cas(
        kv: *mut sonnetdb_kv,
        key: *const c_char,
        expected_version: i64,
        value: *const std::ffi::c_void,
        value_length: c_int,
        expires_at_unix_ms: i64,
        current_version: *mut i64,
        new_version: *mut i64,
    ) -> c_int;
    pub fn sonnetdb_kv_entry_free(entry: *mut sonnetdb_kv_entry);
    pub fn sonnetdb_kv_entry_key(entry: *mut sonnetdb_kv_entry) -> *const c_char;
    pub fn sonnetdb_kv_entry_value_length(entry: *mut sonnetdb_kv_entry) -> i64;
    pub fn sonnetdb_kv_entry_copy_value(
        entry: *mut sonnetdb_kv_entry,
        buffer: *mut std::ffi::c_void,
        buffer_length: c_int,
    ) -> c_int;
    pub fn sonnetdb_kv_entry_version(entry: *mut sonnetdb_kv_entry) -> i64;
    pub fn sonnetdb_kv_entry_expires_at_unix_ms(entry: *mut sonnetdb_kv_entry) -> i64;
    pub fn sonnetdb_kv_scan_next(scan: *mut sonnetdb_kv_scan) -> c_int;
    pub fn sonnetdb_kv_scan_key(scan: *mut sonnetdb_kv_scan) -> *const c_char;
    pub fn sonnetdb_kv_scan_value_length(scan: *mut sonnetdb_kv_scan) -> i64;
    pub fn sonnetdb_kv_scan_copy_value(
        scan: *mut sonnetdb_kv_scan,
        buffer: *mut std::ffi::c_void,
        buffer_length: c_int,
    ) -> c_int;
    pub fn sonnetdb_kv_scan_version(scan: *mut sonnetdb_kv_scan) -> i64;
    pub fn sonnetdb_kv_scan_expires_at_unix_ms(scan: *mut sonnetdb_kv_scan) -> i64;
    pub fn sonnetdb_kv_scan_free(scan: *mut sonnetdb_kv_scan);

    pub fn sonnetdb_flush(connection: *mut sonnetdb_connection) -> c_int;
    pub fn sonnetdb_version(buffer: *mut c_char, buffer_length: c_int) -> c_int;
    pub fn sonnetdb_last_error(buffer: *mut c_char, buffer_length: c_int) -> c_int;
}
