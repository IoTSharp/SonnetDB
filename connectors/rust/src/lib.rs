//! SonnetDB Rust connector over the native C ABI.

mod ffi;

use std::error;
use std::ffi::{c_char, c_int, c_void, CStr, CString};
use std::fmt;
use std::path::Path;
use std::ptr::NonNull;

const NATIVE_STRING_BUFFER_SIZE: usize = 4096;

/// SonnetDB Rust connector result alias.
pub type Result<T> = std::result::Result<T, Error>;

/// SonnetDB native connector error.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Error {
    message: String,
}

impl Error {
    /// Creates a connector error with the provided message.
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }

    /// Returns the error message.
    pub fn message(&self) -> &str {
        &self.message
    }
}

impl fmt::Display for Error {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl error::Error for Error {}

/// SonnetDB C ABI value type.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ValueType {
    /// NULL value.
    Null,

    /// Signed 64-bit integer.
    Int64,

    /// 64-bit floating-point value.
    Double,

    /// Boolean value.
    Bool,

    /// UTF-8 text.
    Text,
}

impl TryFrom<c_int> for ValueType {
    type Error = Error;

    fn try_from(value: c_int) -> Result<Self> {
        match value {
            ffi::SONNETDB_TYPE_NULL => Ok(Self::Null),
            ffi::SONNETDB_TYPE_INT64 => Ok(Self::Int64),
            ffi::SONNETDB_TYPE_DOUBLE => Ok(Self::Double),
            ffi::SONNETDB_TYPE_BOOL => Ok(Self::Bool),
            ffi::SONNETDB_TYPE_TEXT => Ok(Self::Text),
            _ => Err(Error::new(format!(
                "Unknown SonnetDB value type code: {value}."
            ))),
        }
    }
}

/// Natural Rust representation of a SonnetDB cell value.
#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    /// NULL value.
    Null,

    /// Signed 64-bit integer.
    Int64(i64),

    /// 64-bit floating-point value.
    Double(f64),

    /// Boolean value.
    Bool(bool),

    /// UTF-8 text.
    Text(String),
}

/// Options for one synchronous bulk ingest operation.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct BulkOptions {
    /// Optional measurement override.
    pub measurement: Option<String>,

    /// Optional error handling mode, such as `skip` or `failfast`.
    pub on_error: Option<String>,

    /// Optional flush mode, such as `false`, `async`, `true`, or `sync`.
    pub flush: Option<String>,
}

/// Materialized KV entry.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KvEntry {
    /// Entry key without namespace prefix.
    pub key: String,

    /// Binary value bytes.
    pub value: Vec<u8>,

    /// Monotonic write version.
    pub version: i64,

    /// UTC expiration time in Unix milliseconds, or -1 when the key does not expire.
    pub expires_at_unix_ms: i64,
}

/// Redis-style KV TTL result.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct KvTtl {
    /// Remaining milliseconds, -2 for missing keys, -1 for no expiration.
    pub milliseconds: i64,

    /// UTC expiration time in Unix milliseconds, or -1 when absent.
    pub expires_at_unix_ms: i64,
}

/// KV compare-and-set result.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct KvCasResult {
    /// Whether the value was swapped.
    pub swapped: bool,

    /// Version observed before the CAS decision.
    pub current_version: i64,

    /// New version after a successful swap, or -1 on mismatch.
    pub new_version: i64,
}

/// Embedded SonnetDB connection backed by the native C ABI.
pub struct Connection {
    handle: Option<NonNull<ffi::sonnetdb_connection>>,
}

impl Connection {
    /// Opens an embedded SonnetDB database directory.
    pub fn open(data_source: impl AsRef<str>) -> Result<Self> {
        let data_source = to_c_string(data_source.as_ref(), "data_source")?;
        let handle = unsafe { ffi::sonnetdb_open(data_source.as_ptr()) };
        let handle = NonNull::new(handle).ok_or_else(|| last_error_or("sonnetdb_open failed."))?;
        Ok(Self {
            handle: Some(handle),
        })
    }

    /// Opens an embedded SonnetDB database directory from a filesystem path.
    pub fn open_path(path: impl AsRef<Path>) -> Result<Self> {
        let data_source = path.as_ref().to_string_lossy();
        Self::open(data_source.as_ref())
    }

    /// Executes one SQL statement.
    pub fn execute(&self, sql: impl AsRef<str>) -> Result<ResultSet> {
        let handle = self.handle()?;
        let sql = to_c_string(sql.as_ref(), "sql")?;
        let result = unsafe { ffi::sonnetdb_execute(handle.as_ptr(), sql.as_ptr()) };
        let result = NonNull::new(result).ok_or_else(|| last_error_or("sonnetdb_execute failed."))?;
        Ok(ResultSet {
            handle: Some(result),
        })
    }

    /// Executes SQL and returns the affected row count.
    pub fn execute_non_query(&self, sql: impl AsRef<str>) -> Result<i32> {
        let result = self.execute(sql)?;
        result.records_affected()
    }

    /// Ingests a bulk payload synchronously and returns the affected row count.
    pub fn execute_bulk(
        &self,
        payload: impl AsRef<str>,
        options: Option<&BulkOptions>,
    ) -> Result<i32> {
        let handle = self.handle()?;
        let payload = to_c_string(payload.as_ref(), "payload")?;
        let bulk = unsafe { ffi::sonnetdb_bulk_create(payload.as_ptr()) };
        let bulk = NonNull::new(bulk).ok_or_else(|| last_error_or("sonnetdb_bulk_create failed."))?;
        let result = match configure_bulk(bulk, options) {
            Ok(()) => unsafe { ffi::sonnetdb_bulk_execute(handle.as_ptr(), bulk.as_ptr()) },
            Err(error) => {
                unsafe { ffi::sonnetdb_bulk_free(bulk.as_ptr()) };
                return Err(error);
            }
        };
        unsafe { ffi::sonnetdb_bulk_free(bulk.as_ptr()) };

        let result = NonNull::new(result).ok_or_else(|| last_error_or("sonnetdb_bulk_execute failed."))?;
        let result_set = ResultSet {
            handle: Some(result),
        };
        result_set.records_affected()
    }

    /// Forces pending data to durable storage through the native engine.
    pub fn flush(&self) -> Result<()> {
        let handle = self.handle()?;
        let value = unsafe { ffi::sonnetdb_flush(handle.as_ptr()) };
        if value != 0 {
            return Err(last_error_or("sonnetdb_flush failed."));
        }
        Ok(())
    }

    /// Opens a document collection handle.
    pub fn open_document_collection(
        &self,
        collection: impl AsRef<str>,
    ) -> Result<DocumentCollection> {
        let handle = self.handle()?;
        let collection = to_c_string(collection.as_ref(), "collection")?;
        let document = unsafe { ffi::sonnetdb_doc_open(handle.as_ptr(), collection.as_ptr()) };
        let document = NonNull::new(document).ok_or_else(|| last_error_or("sonnetdb_doc_open failed."))?;
        Ok(DocumentCollection {
            handle: Some(document),
        })
    }

    /// Opens a KV keyspace handle. Pass `None` or an empty string for the root namespace.
    pub fn open_kv(&self, keyspace: impl AsRef<str>, namespace: Option<&str>) -> Result<Kv> {
        let handle = self.handle()?;
        let keyspace = to_c_string(keyspace.as_ref(), "keyspace")?;
        let namespace = match namespace {
            Some(value) if !value.is_empty() => Some(to_c_string(value, "namespace")?),
            _ => None,
        };
        let namespace_ptr = namespace.as_ref().map_or(std::ptr::null(), |value| value.as_ptr());
        let kv = unsafe { ffi::sonnetdb_kv_open(handle.as_ptr(), keyspace.as_ptr(), namespace_ptr) };
        let kv = NonNull::new(kv).ok_or_else(|| last_error_or("sonnetdb_kv_open failed."))?;
        Ok(Kv { handle: Some(kv) })
    }

    /// Closes the native connection. Dropping the connection also closes it.
    pub fn close(mut self) -> Result<()> {
        self.close_inner()
    }

    fn handle(&self) -> Result<NonNull<ffi::sonnetdb_connection>> {
        self.handle
            .ok_or_else(|| Error::new("SonnetDB connection is closed."))
    }

    fn close_inner(&mut self) -> Result<()> {
        let Some(handle) = self.handle.take() else {
            return Ok(());
        };

        unsafe { ffi::sonnetdb_close(handle.as_ptr()) };
        let message = last_error();
        if message.is_empty() {
            Ok(())
        } else {
            Err(Error::new(message))
        }
    }
}

impl Drop for Connection {
    fn drop(&mut self) {
        let _ = self.close_inner();
    }
}

/// Document collection handle backed by the native C ABI.
pub struct DocumentCollection {
    handle: Option<NonNull<ffi::sonnetdb_doc>>,
}

impl DocumentCollection {
    /// Creates the collection and returns the native JSON response.
    pub fn create_collection(&self, options_json: Option<&str>) -> Result<String> {
        self.execute_json(
            DocumentOperation::CreateCollection,
            options_json,
            false,
            "sonnetdb_doc_create_collection failed.",
        )
    }

    /// Drops the collection and returns whether it existed.
    pub fn drop_collection(&self) -> Result<bool> {
        let handle = self.handle()?;
        let code = unsafe { ffi::sonnetdb_doc_drop_collection(handle.as_ptr()) };
        bool_from_code(code, "sonnetdb_doc_drop_collection failed.")
    }

    /// Inserts one or more documents from a JSON request.
    pub fn insert(&self, payload_json: impl AsRef<str>) -> Result<String> {
        self.execute_json(
            DocumentOperation::Insert,
            Some(payload_json.as_ref()),
            true,
            "sonnetdb_doc_insert failed.",
        )
    }

    /// Updates documents from a JSON request.
    pub fn update(&self, payload_json: impl AsRef<str>) -> Result<String> {
        self.execute_json(
            DocumentOperation::Update,
            Some(payload_json.as_ref()),
            true,
            "sonnetdb_doc_update failed.",
        )
    }

    /// Deletes documents from a JSON request.
    pub fn delete(&self, payload_json: impl AsRef<str>) -> Result<String> {
        self.execute_json(
            DocumentOperation::Delete,
            Some(payload_json.as_ref()),
            true,
            "sonnetdb_doc_delete failed.",
        )
    }

    /// Finds a page of documents and returns the native JSON response.
    pub fn find_page(&self, payload_json: Option<&str>) -> Result<String> {
        self.execute_json(
            DocumentOperation::FindPage,
            payload_json,
            false,
            "sonnetdb_doc_find_page failed.",
        )
    }

    /// Runs an aggregation pipeline and returns the native JSON response.
    pub fn aggregate(&self, payload_json: impl AsRef<str>) -> Result<String> {
        self.execute_json(
            DocumentOperation::Aggregate,
            Some(payload_json.as_ref()),
            true,
            "sonnetdb_doc_aggregate failed.",
        )
    }

    /// Closes the native document handle. Dropping the handle also closes it.
    pub fn close(mut self) -> Result<()> {
        self.close_inner()
    }

    fn execute_json(
        &self,
        operation: DocumentOperation,
        payload_json: Option<&str>,
        required: bool,
        fallback: &str,
    ) -> Result<String> {
        let handle = self.handle()?;
        let payload = match payload_json {
            Some(value) if !value.is_empty() => Some(to_c_string(value, "payload_json")?),
            Some(_) if required => return Err(Error::new("payload_json must not be empty.")),
            _ => None,
        };
        let payload_ptr = payload
            .as_ref()
            .map_or(std::ptr::null(), |value| value.as_ptr());

        let result = unsafe {
            match operation {
                DocumentOperation::CreateCollection => {
                    ffi::sonnetdb_doc_create_collection(handle.as_ptr(), payload_ptr)
                }
                DocumentOperation::Insert => ffi::sonnetdb_doc_insert(handle.as_ptr(), payload_ptr),
                DocumentOperation::Update => ffi::sonnetdb_doc_update(handle.as_ptr(), payload_ptr),
                DocumentOperation::Delete => ffi::sonnetdb_doc_delete(handle.as_ptr(), payload_ptr),
                DocumentOperation::FindPage => ffi::sonnetdb_doc_find_page(handle.as_ptr(), payload_ptr),
                DocumentOperation::Aggregate => ffi::sonnetdb_doc_aggregate(handle.as_ptr(), payload_ptr),
            }
        };
        let result = NonNull::new(result).ok_or_else(|| last_error_or(fallback))?;
        let json = copy_document_json(result, fallback);
        unsafe { ffi::sonnetdb_doc_result_free(result.as_ptr()) };
        json
    }

    fn handle(&self) -> Result<NonNull<ffi::sonnetdb_doc>> {
        self.handle
            .ok_or_else(|| Error::new("SonnetDB document collection is closed."))
    }

    fn close_inner(&mut self) -> Result<()> {
        let Some(handle) = self.handle.take() else {
            return Ok(());
        };

        unsafe { ffi::sonnetdb_doc_close(handle.as_ptr()) };
        let message = last_error();
        if message.is_empty() {
            Ok(())
        } else {
            Err(Error::new(message))
        }
    }
}

impl Drop for DocumentCollection {
    fn drop(&mut self) {
        let _ = self.close_inner();
    }
}

/// KV keyspace/namespace handle backed by the native C ABI.
pub struct Kv {
    handle: Option<NonNull<ffi::sonnetdb_kv>>,
}

impl Kv {
    /// Reads a KV entry, returning `None` when the key is missing.
    pub fn get(&self, key: impl AsRef<str>) -> Result<Option<KvEntry>> {
        let handle = self.handle()?;
        let key = to_c_string(key.as_ref(), "key")?;
        let entry = unsafe { ffi::sonnetdb_kv_get(handle.as_ptr(), key.as_ptr()) };
        let Some(entry) = NonNull::new(entry) else {
            let message = last_error();
            return if message.is_empty() {
                Ok(None)
            } else {
                Err(Error::new(message))
            };
        };

        let materialized = match materialize_kv_entry(entry) {
            Ok(value) => value,
            Err(error) => {
                unsafe { ffi::sonnetdb_kv_entry_free(entry.as_ptr()) };
                return Err(error);
            }
        };
        unsafe { ffi::sonnetdb_kv_entry_free(entry.as_ptr()) };
        Ok(Some(materialized))
    }

    /// Writes a binary value and returns the written version. Use `None` for no expiration.
    pub fn set(
        &self,
        key: impl AsRef<str>,
        value: impl AsRef<[u8]>,
        expires_at_unix_ms: Option<i64>,
    ) -> Result<i64> {
        let handle = self.handle()?;
        let key = to_c_string(key.as_ref(), "key")?;
        let value = value.as_ref();
        let length = checked_byte_length(value)?;
        let version = unsafe {
            ffi::sonnetdb_kv_set(
                handle.as_ptr(),
                key.as_ptr(),
                value.as_ptr().cast::<c_void>(),
                length,
                expires_at_unix_ms.unwrap_or(-1),
            )
        };
        if version < 0 {
            return Err(last_error_or("sonnetdb_kv_set failed."));
        }
        Ok(version)
    }

    /// Deletes a key and returns whether a value was removed.
    pub fn delete(&self, key: impl AsRef<str>) -> Result<bool> {
        let handle = self.handle()?;
        let key = to_c_string(key.as_ref(), "key")?;
        let code = unsafe { ffi::sonnetdb_kv_delete(handle.as_ptr(), key.as_ptr()) };
        bool_from_code(code, "sonnetdb_kv_delete failed.")
    }

    /// Scans entries matching a prefix. `limit <= 0` uses the keyspace default limit.
    pub fn scan_prefix(&self, prefix: impl AsRef<str>, limit: i32) -> Result<Vec<KvEntry>> {
        let handle = self.handle()?;
        let prefix = to_c_string(prefix.as_ref(), "prefix")?;
        let scan = unsafe { ffi::sonnetdb_kv_scan_prefix(handle.as_ptr(), prefix.as_ptr(), limit) };
        let scan = NonNull::new(scan).ok_or_else(|| last_error_or("sonnetdb_kv_scan_prefix failed."))?;
        let mut entries = Vec::new();
        loop {
            let next = unsafe { ffi::sonnetdb_kv_scan_next(scan.as_ptr()) };
            if next < 0 {
                unsafe { ffi::sonnetdb_kv_scan_free(scan.as_ptr()) };
                return Err(last_error_or("sonnetdb_kv_scan_next failed."));
            }
            if next == 0 {
                unsafe { ffi::sonnetdb_kv_scan_free(scan.as_ptr()) };
                return Ok(entries);
            }
            match materialize_kv_scan_entry(scan) {
                Ok(entry) => entries.push(entry),
                Err(error) => {
                    unsafe { ffi::sonnetdb_kv_scan_free(scan.as_ptr()) };
                    return Err(error);
                }
            }
        }
    }

    /// Returns the remaining TTL in milliseconds.
    pub fn ttl(&self, key: impl AsRef<str>) -> Result<KvTtl> {
        let handle = self.handle()?;
        let key = to_c_string(key.as_ref(), "key")?;
        let mut expires = -1;
        let milliseconds = unsafe { ffi::sonnetdb_kv_ttl(handle.as_ptr(), key.as_ptr(), &mut expires) };
        if milliseconds < -2 {
            return Err(last_error_or("sonnetdb_kv_ttl failed."));
        }
        Ok(KvTtl {
            milliseconds,
            expires_at_unix_ms: expires,
        })
    }

    /// Sets an absolute UTC expiration time in Unix milliseconds.
    pub fn expire_at(&self, key: impl AsRef<str>, expires_at_unix_ms: i64) -> Result<bool> {
        let handle = self.handle()?;
        let key = to_c_string(key.as_ref(), "key")?;
        let code = unsafe { ffi::sonnetdb_kv_expire_at(handle.as_ptr(), key.as_ptr(), expires_at_unix_ms) };
        bool_from_code(code, "sonnetdb_kv_expire_at failed.")
    }

    /// Removes a key expiration.
    pub fn persist(&self, key: impl AsRef<str>) -> Result<bool> {
        let handle = self.handle()?;
        let key = to_c_string(key.as_ref(), "key")?;
        let code = unsafe { ffi::sonnetdb_kv_persist(handle.as_ptr(), key.as_ptr()) };
        bool_from_code(code, "sonnetdb_kv_persist failed.")
    }

    /// Atomically increments a UTF-8 integer value.
    pub fn incr(&self, key: impl AsRef<str>, delta: i64) -> Result<(i64, i64)> {
        let handle = self.handle()?;
        let key = to_c_string(key.as_ref(), "key")?;
        let mut value = 0;
        let mut version = 0;
        let code = unsafe {
            ffi::sonnetdb_kv_incr(handle.as_ptr(), key.as_ptr(), delta, &mut value, &mut version)
        };
        if code != 0 {
            return Err(last_error_or("sonnetdb_kv_incr failed."));
        }
        Ok((value, version))
    }

    /// Compares a key version and swaps in a new value on match.
    pub fn compare_and_set(
        &self,
        key: impl AsRef<str>,
        expected_version: i64,
        value: impl AsRef<[u8]>,
        expires_at_unix_ms: Option<i64>,
    ) -> Result<KvCasResult> {
        let handle = self.handle()?;
        let key = to_c_string(key.as_ref(), "key")?;
        let value = value.as_ref();
        let length = checked_byte_length(value)?;
        let mut current_version = 0;
        let mut new_version = -1;
        let code = unsafe {
            ffi::sonnetdb_kv_cas(
                handle.as_ptr(),
                key.as_ptr(),
                expected_version,
                value.as_ptr().cast::<c_void>(),
                length,
                expires_at_unix_ms.unwrap_or(-1),
                &mut current_version,
                &mut new_version,
            )
        };
        if code < 0 {
            return Err(last_error_or("sonnetdb_kv_cas failed."));
        }
        Ok(KvCasResult {
            swapped: code == 1,
            current_version,
            new_version,
        })
    }

    /// Closes the native KV handle. Dropping the handle also closes it.
    pub fn close(mut self) -> Result<()> {
        self.close_inner()
    }

    fn handle(&self) -> Result<NonNull<ffi::sonnetdb_kv>> {
        self.handle
            .ok_or_else(|| Error::new("SonnetDB KV handle is closed."))
    }

    fn close_inner(&mut self) -> Result<()> {
        let Some(handle) = self.handle.take() else {
            return Ok(());
        };

        unsafe { ffi::sonnetdb_kv_close(handle.as_ptr()) };
        let message = last_error();
        if message.is_empty() {
            Ok(())
        } else {
            Err(Error::new(message))
        }
    }
}

impl Drop for Kv {
    fn drop(&mut self) {
        let _ = self.close_inner();
    }
}

/// Forward-only cursor over a SQL execution result.
pub struct ResultSet {
    handle: Option<NonNull<ffi::sonnetdb_result>>,
}

impl ResultSet {
    /// Returns INSERT/DELETE affected rows. SELECT results return -1.
    pub fn records_affected(&self) -> Result<i32> {
        let handle = self.handle()?;
        let value = unsafe { ffi::sonnetdb_result_records_affected(handle.as_ptr()) };
        if value < 0 {
            let message = last_error();
            if !message.is_empty() {
                return Err(Error::new(message));
            }
        }
        Ok(value)
    }

    /// Returns the number of result columns.
    pub fn column_count(&self) -> Result<usize> {
        let handle = self.handle()?;
        let value = unsafe { ffi::sonnetdb_result_column_count(handle.as_ptr()) };
        if value < 0 {
            return Err(last_error_or("sonnetdb_result_column_count failed."));
        }
        Ok(value as usize)
    }

    /// Returns a result column name by zero-based ordinal.
    pub fn column_name(&self, ordinal: usize) -> Result<String> {
        let handle = self.handle()?;
        let ordinal = checked_ordinal(ordinal)?;
        let value = unsafe { ffi::sonnetdb_result_column_name(handle.as_ptr(), ordinal) };
        if value.is_null() {
            return Err(last_error_or("sonnetdb_result_column_name failed."));
        }
        Ok(unsafe { CStr::from_ptr(value) }
            .to_string_lossy()
            .into_owned())
    }

    /// Returns all result column names.
    pub fn columns(&self) -> Result<Vec<String>> {
        let count = self.column_count()?;
        let mut columns = Vec::with_capacity(count);
        for ordinal in 0..count {
            columns.push(self.column_name(ordinal)?);
        }
        Ok(columns)
    }

    /// Advances the cursor to the next row.
    pub fn next(&mut self) -> Result<bool> {
        let handle = self.handle()?;
        let value = unsafe { ffi::sonnetdb_result_next(handle.as_ptr()) };
        if value < 0 {
            return Err(last_error_or("sonnetdb_result_next failed."));
        }
        Ok(value == 1)
    }

    /// Returns the native value type for the current row and column.
    pub fn value_type(&self, ordinal: usize) -> Result<ValueType> {
        let handle = self.handle()?;
        let ordinal = checked_ordinal(ordinal)?;
        let code = unsafe { ffi::sonnetdb_result_value_type(handle.as_ptr(), ordinal) };
        if code < 0 {
            return Err(last_error_or("sonnetdb_result_value_type failed."));
        }
        ValueType::try_from(code)
    }

    /// Reads the current row value as i64.
    pub fn get_i64(&self, ordinal: usize) -> Result<i64> {
        let value_type = self.value_type(ordinal)?;
        if value_type != ValueType::Int64 {
            return Err(Error::new(format!(
                "Column {ordinal} is {value_type:?}, not Int64."
            )));
        }

        let handle = self.handle()?;
        let ordinal = checked_ordinal(ordinal)?;
        Ok(unsafe { ffi::sonnetdb_result_value_int64(handle.as_ptr(), ordinal) })
    }

    /// Reads the current row value as f64. Integer values are accepted and converted by the native ABI.
    pub fn get_f64(&self, ordinal: usize) -> Result<f64> {
        let value_type = self.value_type(ordinal)?;
        if value_type != ValueType::Double && value_type != ValueType::Int64 {
            return Err(Error::new(format!(
                "Column {ordinal} is {value_type:?}, not Double."
            )));
        }

        let handle = self.handle()?;
        let ordinal = checked_ordinal(ordinal)?;
        Ok(unsafe { ffi::sonnetdb_result_value_double(handle.as_ptr(), ordinal) } as f64)
    }

    /// Reads the current row value as bool.
    pub fn get_bool(&self, ordinal: usize) -> Result<bool> {
        let value_type = self.value_type(ordinal)?;
        if value_type != ValueType::Bool {
            return Err(Error::new(format!(
                "Column {ordinal} is {value_type:?}, not Bool."
            )));
        }

        let handle = self.handle()?;
        let ordinal = checked_ordinal(ordinal)?;
        let value = unsafe { ffi::sonnetdb_result_value_bool(handle.as_ptr(), ordinal) };
        if value < 0 {
            return Err(last_error_or("sonnetdb_result_value_bool failed."));
        }
        Ok(value != 0)
    }

    /// Reads the current row value as UTF-8 text. NULL returns `None`.
    pub fn get_text(&self, ordinal: usize) -> Result<Option<String>> {
        if self.value_type(ordinal)? == ValueType::Null {
            return Ok(None);
        }

        let handle = self.handle()?;
        let ordinal = checked_ordinal(ordinal)?;
        let value = unsafe { ffi::sonnetdb_result_value_text(handle.as_ptr(), ordinal) };
        if value.is_null() {
            return Err(last_error_or("sonnetdb_result_value_text failed."));
        }

        Ok(Some(
            unsafe { CStr::from_ptr(value) }
                .to_string_lossy()
                .into_owned(),
        ))
    }

    /// Reads the current row value using a natural Rust enum representation.
    pub fn get_value(&self, ordinal: usize) -> Result<Value> {
        match self.value_type(ordinal)? {
            ValueType::Null => Ok(Value::Null),
            ValueType::Int64 => Ok(Value::Int64(self.get_i64(ordinal)?)),
            ValueType::Double => Ok(Value::Double(self.get_f64(ordinal)?)),
            ValueType::Bool => Ok(Value::Bool(self.get_bool(ordinal)?)),
            ValueType::Text => Ok(Value::Text(self.get_text(ordinal)?.unwrap_or_default())),
        }
    }

    /// Frees the native result handle. Dropping the result also frees it.
    pub fn close(mut self) -> Result<()> {
        self.close_inner()
    }

    fn handle(&self) -> Result<NonNull<ffi::sonnetdb_result>> {
        self.handle
            .ok_or_else(|| Error::new("SonnetDB result is closed."))
    }

    fn close_inner(&mut self) -> Result<()> {
        let Some(handle) = self.handle.take() else {
            return Ok(());
        };

        unsafe { ffi::sonnetdb_result_free(handle.as_ptr()) };
        let message = last_error();
        if message.is_empty() {
            Ok(())
        } else {
            Err(Error::new(message))
        }
    }
}

impl Drop for ResultSet {
    fn drop(&mut self) {
        let _ = self.close_inner();
    }
}

/// Returns the loaded SonnetDB native library version.
pub fn version() -> Result<String> {
    copy_utf8(ffi::sonnetdb_version, "sonnetdb_version failed.")
}

/// Returns the last native error message for the current native thread.
pub fn last_error() -> String {
    copy_utf8_raw(ffi::sonnetdb_last_error).unwrap_or_default()
}

enum DocumentOperation {
    CreateCollection,
    Insert,
    Update,
    Delete,
    FindPage,
    Aggregate,
}

fn to_c_string(value: &str, name: &str) -> Result<CString> {
    CString::new(value).map_err(|_| Error::new(format!("{name} must not contain NUL bytes.")))
}

fn checked_ordinal(ordinal: usize) -> Result<c_int> {
    c_int::try_from(ordinal)
        .map_err(|_| Error::new(format!("Column ordinal {ordinal} is out of range.")))
}

fn checked_byte_length(value: &[u8]) -> Result<c_int> {
    c_int::try_from(value.len())
        .map_err(|_| Error::new(format!("Byte length {} is out of range.", value.len())))
}

fn bool_from_code(code: c_int, fallback: &str) -> Result<bool> {
    if code < 0 {
        return Err(last_error_or(fallback));
    }
    Ok(code == 1)
}

fn configure_bulk(bulk: NonNull<ffi::sonnetdb_bulk>, options: Option<&BulkOptions>) -> Result<()> {
    let Some(options) = options else {
        return Ok(());
    };

    set_bulk_string(
        bulk,
        options.measurement.as_deref(),
        ffi::sonnetdb_bulk_set_measurement,
        "sonnetdb_bulk_set_measurement failed.",
    )?;
    set_bulk_string(
        bulk,
        options.on_error.as_deref(),
        ffi::sonnetdb_bulk_set_onerror,
        "sonnetdb_bulk_set_onerror failed.",
    )?;
    set_bulk_string(
        bulk,
        options.flush.as_deref(),
        ffi::sonnetdb_bulk_set_flush,
        "sonnetdb_bulk_set_flush failed.",
    )
}

fn set_bulk_string(
    bulk: NonNull<ffi::sonnetdb_bulk>,
    value: Option<&str>,
    setter: unsafe extern "C" fn(*mut ffi::sonnetdb_bulk, *const c_char) -> c_int,
    fallback: &str,
) -> Result<()> {
    let Some(value) = value.filter(|value| !value.is_empty()) else {
        return Ok(());
    };
    let value = to_c_string(value, "bulk option")?;
    let code = unsafe { setter(bulk.as_ptr(), value.as_ptr()) };
    if code != 0 {
        return Err(last_error_or(fallback));
    }
    Ok(())
}

fn copy_document_json(
    result: NonNull<ffi::sonnetdb_doc_result>,
    fallback: &str,
) -> Result<String> {
    let required = unsafe { ffi::sonnetdb_doc_result_json_length(result.as_ptr()) };
    if required < 0 {
        return Err(last_error_or(fallback));
    }
    if required == c_int::MAX {
        return Err(Error::new("Document JSON response length is out of range."));
    }

    let length = usize::try_from(required)
        .map_err(|_| Error::new("Document JSON response length is out of range."))?;
    let mut buffer = vec![0 as c_char; length + 1];
    let copied = unsafe {
        ffi::sonnetdb_doc_result_copy_json(result.as_ptr(), buffer.as_mut_ptr(), required + 1)
    };
    if copied < 0 {
        return Err(last_error_or(fallback));
    }
    Ok(unsafe { CStr::from_ptr(buffer.as_ptr()) }
        .to_string_lossy()
        .into_owned())
}

fn materialize_kv_entry(entry: NonNull<ffi::sonnetdb_kv_entry>) -> Result<KvEntry> {
    let key = unsafe { ffi::sonnetdb_kv_entry_key(entry.as_ptr()) };
    if key.is_null() {
        return Err(last_error_or("sonnetdb_kv_entry_key failed."));
    }
    let value = copy_kv_entry_value(entry)?;
    Ok(KvEntry {
        key: unsafe { CStr::from_ptr(key) }.to_string_lossy().into_owned(),
        value,
        version: unsafe { ffi::sonnetdb_kv_entry_version(entry.as_ptr()) },
        expires_at_unix_ms: unsafe { ffi::sonnetdb_kv_entry_expires_at_unix_ms(entry.as_ptr()) },
    })
}

fn materialize_kv_scan_entry(scan: NonNull<ffi::sonnetdb_kv_scan>) -> Result<KvEntry> {
    let key = unsafe { ffi::sonnetdb_kv_scan_key(scan.as_ptr()) };
    if key.is_null() {
        return Err(last_error_or("sonnetdb_kv_scan_key failed."));
    }
    let value = copy_kv_scan_value(scan)?;
    Ok(KvEntry {
        key: unsafe { CStr::from_ptr(key) }.to_string_lossy().into_owned(),
        value,
        version: unsafe { ffi::sonnetdb_kv_scan_version(scan.as_ptr()) },
        expires_at_unix_ms: unsafe { ffi::sonnetdb_kv_scan_expires_at_unix_ms(scan.as_ptr()) },
    })
}

fn copy_kv_entry_value(entry: NonNull<ffi::sonnetdb_kv_entry>) -> Result<Vec<u8>> {
    let required = unsafe { ffi::sonnetdb_kv_entry_value_length(entry.as_ptr()) };
    if required < 0 {
        return Err(last_error_or("sonnetdb_kv_entry_value_length failed."));
    }
    let length = usize::try_from(required).map_err(|_| Error::new("KV value length is out of range."))?;
    let mut value = vec![0u8; length];
    let copied = unsafe {
        ffi::sonnetdb_kv_entry_copy_value(
            entry.as_ptr(),
            value.as_mut_ptr().cast::<c_void>(),
            checked_byte_length(&value)?,
        )
    };
    if copied < 0 {
        return Err(last_error_or("sonnetdb_kv_entry_copy_value failed."));
    }
    Ok(value)
}

fn copy_kv_scan_value(scan: NonNull<ffi::sonnetdb_kv_scan>) -> Result<Vec<u8>> {
    let required = unsafe { ffi::sonnetdb_kv_scan_value_length(scan.as_ptr()) };
    if required < 0 {
        return Err(last_error_or("sonnetdb_kv_scan_value_length failed."));
    }
    let length = usize::try_from(required).map_err(|_| Error::new("KV value length is out of range."))?;
    let mut value = vec![0u8; length];
    let copied = unsafe {
        ffi::sonnetdb_kv_scan_copy_value(
            scan.as_ptr(),
            value.as_mut_ptr().cast::<c_void>(),
            checked_byte_length(&value)?,
        )
    };
    if copied < 0 {
        return Err(last_error_or("sonnetdb_kv_scan_copy_value failed."));
    }
    Ok(value)
}

fn last_error_or(fallback: &str) -> Error {
    let message = last_error();
    if message.is_empty() {
        Error::new(fallback)
    } else {
        Error::new(message)
    }
}

fn copy_utf8(
    func: unsafe extern "C" fn(*mut c_char, c_int) -> c_int,
    fallback: &str,
) -> Result<String> {
    copy_utf8_raw(func).map_err(|_| last_error_or(fallback))
}

fn copy_utf8_raw(
    func: unsafe extern "C" fn(*mut c_char, c_int) -> c_int,
) -> std::result::Result<String, ()> {
    let mut buffer = vec![0 as c_char; NATIVE_STRING_BUFFER_SIZE];
    let required = unsafe { func(buffer.as_mut_ptr(), buffer.len() as c_int) };
    if required < 0 {
        return Err(());
    }

    if required as usize >= buffer.len() {
        buffer = vec![0 as c_char; required as usize + 1];
        let second = unsafe { func(buffer.as_mut_ptr(), buffer.len() as c_int) };
        if second < 0 {
            return Err(());
        }
    }

    Ok(unsafe { CStr::from_ptr(buffer.as_ptr()) }
        .to_string_lossy()
        .into_owned())
}
