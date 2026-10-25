//! Transport-neutral SQL Execute v1 Frame codec.
//!
//! The byte contract mirrors `SonnetDB.Protocol.SqlExecuteFrameCodec`: values
//! are encoded with an explicit type and little-endian representation. This
//! module only builds and validates frames; an HTTP/2 transport is deliberately
//! kept outside the dependency-free Rust connector.

use std::fmt;

pub const FRAME_HEADER_SIZE: usize = 12;
pub const FRAME_VERSION: u8 = 1;
pub const SQL_SERVICE: u8 = 3;
pub const SQL_EXECUTE_OPERATION: u8 = 2;
pub const RESPONSE_FLAG: u8 = 1;
pub const CURRENT_VERSION: u8 = 1;
pub const MAX_FRAME_PAYLOAD_BYTES: usize = 132 * 1024 * 1024;
pub const MAX_REQUEST_PAYLOAD_BYTES: usize = 4 * 1024 * 1024;
pub const MAX_PARAMETER_COUNT: usize = 256;
pub const MAX_NAME_BYTES: usize = 512;
pub const MAX_SQL_BYTES: usize = 1024 * 1024;
pub const MAX_VALUE_BYTES: usize = MAX_REQUEST_PAYLOAD_BYTES;
pub const MAX_VECTOR_DIMENSION: usize = 1_000_000;
const COMMITTED_OUTCOME: u8 = 1;
const MAX_DATETIME_TICKS: i64 = 3_155_378_975_999_999_999;
const TICKS_PER_DAY: i64 = 864_000_000_000;

/// Codec error with no dependency on a transport or database engine.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FrameError(String);

impl FrameError {
    fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
}

impl fmt::Display for FrameError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl std::error::Error for FrameError {}

/// Exact parameter type codes used by SQL Execute v1.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum ParameterKind {
    Null = 0,
    Int64 = 1,
    Float64 = 2,
    Boolean = 3,
    String = 4,
    Blob = 5,
    Timestamp = 6,
    Decimal = 7,
    DateTimeOffset = 8,
    Duration = 9,
    Time = 10,
    Vector = 11,
    DateTime = 12,
}

impl TryFrom<u8> for ParameterKind {
    type Error = FrameError;

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(Self::Null),
            1 => Ok(Self::Int64),
            2 => Ok(Self::Float64),
            3 => Ok(Self::Boolean),
            4 => Ok(Self::String),
            5 => Ok(Self::Blob),
            6 => Ok(Self::Timestamp),
            7 => Ok(Self::Decimal),
            8 => Ok(Self::DateTimeOffset),
            9 => Ok(Self::Duration),
            10 => Ok(Self::Time),
            11 => Ok(Self::Vector),
            12 => Ok(Self::DateTime),
            _ => Err(FrameError::new("unknown SQL Execute parameter type")),
        }
    }
}

/// An explicitly typed parameter value. Date/time fields use UTC ticks so no
/// precision is lost when crossing the language boundary.
#[derive(Debug, Clone, PartialEq)]
pub enum ParameterValue {
    Int64(i64),
    Float64(f64),
    Boolean(bool),
    String(String),
    Blob(Vec<u8>),
    Timestamp(i64),
    Decimal([i32; 4]),
    DateTimeOffset(i64),
    Duration(i64),
    Time(i64),
    Vector(Vec<f32>),
    DateTime(i64),
}

/// One named or positional SQL Execute parameter. `None` keeps the declared
/// type while encoding a SQL NULL value.
#[derive(Debug, Clone, PartialEq)]
pub struct Parameter {
    pub name: String,
    pub kind: ParameterKind,
    pub value: Option<ParameterValue>,
}

/// Decoded SQL Execute request.
#[derive(Debug, Clone, PartialEq)]
pub struct Request {
    pub database: String,
    pub sql: String,
    pub parameters: Vec<Parameter>,
}

/// Decoded committed SQL Execute response.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Response {
    pub records_affected: i64,
}

/// Encode a complete SQL Execute request frame.
pub fn encode_sql_execute_frame(
    stream_id: u32,
    database: &str,
    sql: &str,
    parameters: &[Parameter],
) -> Result<Vec<u8>, FrameError> {
    validate_string(database, MAX_NAME_BYTES, "database")?;
    validate_string(sql, MAX_SQL_BYTES, "sql")?;
    if sql.is_empty() {
        return Err(FrameError::new("sql must not be empty"));
    }
    if parameters.len() > MAX_PARAMETER_COUNT {
        return Err(FrameError::new("parameter count exceeds the protocol limit"));
    }
    validate_bindings(parameters)?;

    for parameter in parameters {
        validate_parameter(parameter)?;
    }
    let database_size = measure_string(database)?;
    let sql_size = measure_string(sql)?;
    let mut payload_length = 1usize
        .checked_add(database_size)
        .and_then(|length| length.checked_add(sql_size))
        .and_then(|length| length.checked_add(measure_varuint(parameters.len() as u32)))
        .ok_or_else(|| FrameError::new("SQL Execute payload length overflow"))?;
    for parameter in parameters {
        let name_size = measure_string(&parameter.name)?;
        let value_size = measure_value(parameter)?;
        payload_length = payload_length
            .checked_add(name_size)
            .and_then(|length| length.checked_add(2))
            .and_then(|length| length.checked_add(value_size))
            .ok_or_else(|| FrameError::new("SQL Execute payload length overflow"))?;
    }
    if payload_length > MAX_REQUEST_PAYLOAD_BYTES {
        return Err(FrameError::new("SQL Execute payload exceeds the protocol limit"));
    }
    let mut payload = Vec::with_capacity(payload_length);
    payload.push(CURRENT_VERSION);
    write_string(&mut payload, database)?;
    write_string(&mut payload, sql)?;
    write_varuint(&mut payload, parameters.len() as u32);
    for parameter in parameters {
        write_string(&mut payload, &parameter.name)?;
        payload.push(parameter.kind as u8);
        payload.push(u8::from(parameter.value.is_some()));
        if let Some(value) = &parameter.value {
            write_value(&mut payload, parameter.kind, value)?;
        }
    }
    let mut frame = frame_header(payload.len(), SQL_SERVICE, SQL_EXECUTE_OPERATION, 0, stream_id)?;
    frame.extend_from_slice(&payload);
    Ok(frame)
}

/// Decode a SQL Execute payload without its 12-byte header.
pub fn decode_sql_execute_payload(payload: &[u8]) -> Result<Request, FrameError> {
    if payload.len() > MAX_REQUEST_PAYLOAD_BYTES {
        return Err(FrameError::new("SQL Execute payload exceeds the protocol limit"));
    }
    let mut reader = Reader::new(payload);
    if reader.byte()? != CURRENT_VERSION {
        return Err(FrameError::new("unsupported SQL Execute payload version"));
    }
    let database = reader.string(MAX_NAME_BYTES, "database")?;
    let sql = reader.string(MAX_SQL_BYTES, "sql")?;
    if sql.is_empty() {
        return Err(FrameError::new("sql must not be empty"));
    }
    let parameter_count = reader.varuint()? as usize;
    if parameter_count > MAX_PARAMETER_COUNT {
        return Err(FrameError::new("parameter count exceeds the protocol limit"));
    }
    let mut parameters = Vec::with_capacity(parameter_count);
    for _ in 0..parameter_count {
        let name = reader.string(MAX_NAME_BYTES, "parameter name")?;
        let kind = ParameterKind::try_from(reader.byte()?)?;
        let has_value = reader.boolean()?;
        if kind == ParameterKind::Null && has_value {
            return Err(FrameError::new("NULL parameter must not contain a value"));
        }
        let value = if has_value {
            Some(read_value(&mut reader, kind)?)
        } else {
            None
        };
        parameters.push(Parameter { name, kind, value });
    }
    if reader.remaining() != 0 {
        return Err(FrameError::new("SQL Execute payload contains trailing bytes"));
    }
    validate_bindings(&parameters)?;
    Ok(Request { database, sql, parameters })
}

/// Validate and decode a complete SQL Execute request frame.
pub fn decode_sql_execute_frame(frame: &[u8]) -> Result<Request, FrameError> {
    let payload = read_frame(frame, SQL_SERVICE, SQL_EXECUTE_OPERATION, 0)?;
    decode_sql_execute_payload(payload)
}

/// Encode a complete committed SQL Execute response frame.
pub fn encode_sql_execute_response_frame(stream_id: u32, records_affected: i64) -> Result<Vec<u8>, FrameError> {
    if records_affected < 0 {
        return Err(FrameError::new("records_affected must be non-negative"));
    }
    let mut payload = Vec::with_capacity(10);
    payload.push(CURRENT_VERSION);
    payload.extend_from_slice(&records_affected.to_le_bytes());
    payload.push(COMMITTED_OUTCOME);
    let mut frame = frame_header(payload.len(), SQL_SERVICE, SQL_EXECUTE_OPERATION, RESPONSE_FLAG, stream_id)?;
    frame.extend_from_slice(&payload);
    Ok(frame)
}

/// Decode a committed SQL Execute response payload.
pub fn decode_sql_execute_response_payload(payload: &[u8]) -> Result<Response, FrameError> {
    if payload.len() != 10 || payload[0] != CURRENT_VERSION {
        return Err(FrameError::new("invalid SQL Execute response payload"));
    }
    let records_affected = i64::from_le_bytes(payload[1..9].try_into().expect("fixed response size"));
    if records_affected < 0 || payload[9] != COMMITTED_OUTCOME {
        return Err(FrameError::new("SQL Execute response is not a committed terminal state"));
    }
    Ok(Response { records_affected })
}

/// Validate and decode a complete committed response frame.
pub fn decode_sql_execute_response_frame(frame: &[u8]) -> Result<Response, FrameError> {
    let payload = read_frame(frame, SQL_SERVICE, SQL_EXECUTE_OPERATION, RESPONSE_FLAG)?;
    decode_sql_execute_response_payload(payload)
}

fn frame_header(payload_length: usize, service: u8, operation: u8, flags: u8, stream_id: u32) -> Result<Vec<u8>, FrameError> {
    if payload_length > u32::MAX as usize || payload_length > MAX_FRAME_PAYLOAD_BYTES {
        return Err(FrameError::new("frame payload exceeds the protocol limit"));
    }
    let mut header = Vec::with_capacity(FRAME_HEADER_SIZE);
    header.extend_from_slice(&(payload_length as u32).to_le_bytes());
    header.extend_from_slice(&[FRAME_VERSION, service, operation, flags]);
    header.extend_from_slice(&stream_id.to_le_bytes());
    Ok(header)
}

fn read_frame(frame: &[u8], service: u8, operation: u8, flags: u8) -> Result<&[u8], FrameError> {
    if frame.len() < FRAME_HEADER_SIZE {
        return Err(FrameError::new("frame is shorter than its header"));
    }
    let payload_length = u32::from_le_bytes(frame[0..4].try_into().expect("fixed header size")) as usize;
    if frame[4] != FRAME_VERSION || frame[5] != service || frame[6] != operation || frame[7] != flags {
        return Err(FrameError::new("frame identity does not match SQL Execute"));
    }
    if payload_length > MAX_FRAME_PAYLOAD_BYTES || frame.len() != FRAME_HEADER_SIZE + payload_length {
        return Err(FrameError::new("frame payload length is invalid"));
    }
    Ok(&frame[FRAME_HEADER_SIZE..])
}

fn validate_string(value: &str, maximum: usize, label: &str) -> Result<(), FrameError> {
    if value.len() > maximum {
        return Err(FrameError::new(format!("{label} exceeds its UTF-8 byte limit")));
    }
    Ok(())
}

fn write_varuint(target: &mut Vec<u8>, mut value: u32) {
    while value >= 0x80 {
        target.push((value as u8 & 0x7F) | 0x80);
        value >>= 7;
    }
    target.push(value as u8);
}

fn write_string(target: &mut Vec<u8>, value: &str) -> Result<(), FrameError> {
    if value.len() > u32::MAX as usize {
        return Err(FrameError::new("string exceeds varuint32 range"));
    }
    write_varuint(target, value.len() as u32);
    target.extend_from_slice(value.as_bytes());
    Ok(())
}

fn validate_bindings(parameters: &[Parameter]) -> Result<(), FrameError> {
    let mut named: Option<bool> = None;
    for (index, parameter) in parameters.iter().enumerate() {
        let is_named = !parameter.name.is_empty();
        if let Some(previous) = named {
            if previous != is_named {
                return Err(FrameError::new("named and positional SQL parameters cannot be mixed"));
            }
        } else {
            named = Some(is_named);
        }
        if is_named {
            validate_string(&parameter.name, MAX_NAME_BYTES, "parameter name")?;
            if !parameter.name.is_ascii() {
                return Err(FrameError::new("parameter names must contain ASCII characters"));
            }
            if parameter.name.starts_with('@') || parameter.name.starts_with(':') || parameter.name.trim().is_empty() {
                return Err(FrameError::new("parameter names must omit @/: prefixes"));
            }
            if parameters[..index].iter().any(|previous| previous.name.eq_ignore_ascii_case(&parameter.name)) {
                return Err(FrameError::new("parameter names must be unique"));
            }
        }
    }
    Ok(())
}

fn validate_parameter(parameter: &Parameter) -> Result<(), FrameError> {
    validate_string(&parameter.name, MAX_NAME_BYTES, "parameter name")?;
    if parameter.value.is_none() {
        return Ok(());
    }
    let value = parameter.value.as_ref().expect("checked above");
    let valid = match (parameter.kind, value) {
        (ParameterKind::Null, _) => false,
        (ParameterKind::Int64, ParameterValue::Int64(_))
        | (ParameterKind::Timestamp, ParameterValue::Timestamp(_))
        | (ParameterKind::DateTimeOffset, ParameterValue::DateTimeOffset(_))
        | (ParameterKind::Duration, ParameterValue::Duration(_))
        | (ParameterKind::Time, ParameterValue::Time(_))
        | (ParameterKind::DateTime, ParameterValue::DateTime(_)) => true,
        (ParameterKind::Float64, ParameterValue::Float64(value)) => value.is_finite(),
        (ParameterKind::Boolean, ParameterValue::Boolean(_)) => true,
        (ParameterKind::String, ParameterValue::String(value)) => value.len() <= MAX_VALUE_BYTES,
        (ParameterKind::Blob, ParameterValue::Blob(value)) => value.len() <= MAX_VALUE_BYTES,
        (ParameterKind::Decimal, ParameterValue::Decimal(bits)) => valid_decimal_bits(bits),
        (ParameterKind::Vector, ParameterValue::Vector(value)) => {
            !value.is_empty() && value.len() <= MAX_VECTOR_DIMENSION && value.iter().all(|item| item.is_finite())
        }
        _ => false,
    };
    if !valid {
        return Err(FrameError::new("parameter value does not match its protocol type"));
    }
    if matches!(parameter.kind, ParameterKind::DateTimeOffset | ParameterKind::DateTime)
        && !matches!(value, ParameterValue::DateTimeOffset(ticks) | ParameterValue::DateTime(ticks) if (0..=MAX_DATETIME_TICKS).contains(ticks))
    {
        return Err(FrameError::new("date ticks are out of range"));
    }
    if parameter.kind == ParameterKind::Time
        && !matches!(value, ParameterValue::Time(ticks) if (0..TICKS_PER_DAY).contains(ticks))
    {
        return Err(FrameError::new("time ticks are out of range"));
    }
    Ok(())
}

fn valid_decimal_bits(bits: &[i32; 4]) -> bool {
    let flags = bits[3] as u32;
    flags & 0x7F00_FFFF == 0 && ((flags >> 16) & 0xFF) <= 28
}

fn measure_varuint(mut value: u32) -> usize {
    let mut length = 1;
    while value >= 0x80 {
        value >>= 7;
        length += 1;
    }
    length
}

fn measure_string(value: &str) -> Result<usize, FrameError> {
    if value.len() > u32::MAX as usize {
        return Err(FrameError::new("string exceeds varuint32 range"));
    }
    Ok(measure_varuint(value.len() as u32) + value.len())
}

fn measure_value(parameter: &Parameter) -> Result<usize, FrameError> {
    let Some(value) = &parameter.value else { return Ok(0); };
    let size = match (parameter.kind, value) {
        (ParameterKind::Int64, ParameterValue::Int64(_))
        | (ParameterKind::Float64, ParameterValue::Float64(_))
        | (ParameterKind::Timestamp, ParameterValue::Timestamp(_))
        | (ParameterKind::DateTimeOffset, ParameterValue::DateTimeOffset(_))
        | (ParameterKind::Duration, ParameterValue::Duration(_))
        | (ParameterKind::Time, ParameterValue::Time(_))
        | (ParameterKind::DateTime, ParameterValue::DateTime(_)) => 8,
        (ParameterKind::Boolean, ParameterValue::Boolean(_)) => 1,
        (ParameterKind::String, ParameterValue::String(value)) => measure_string(value)?,
        (ParameterKind::Blob, ParameterValue::Blob(value)) => measure_varuint(value.len() as u32) + value.len(),
        (ParameterKind::Decimal, ParameterValue::Decimal(_)) => 16,
        (ParameterKind::Vector, ParameterValue::Vector(value)) => measure_varuint(value.len() as u32) + 4 * value.len(),
        _ => return Err(FrameError::new("parameter value does not match its protocol type")),
    };
    Ok(size)
}

fn write_value(target: &mut Vec<u8>, kind: ParameterKind, value: &ParameterValue) -> Result<(), FrameError> {
    match (kind, value) {
        (ParameterKind::Int64, ParameterValue::Int64(value))
        | (ParameterKind::Timestamp, ParameterValue::Timestamp(value))
        | (ParameterKind::DateTimeOffset, ParameterValue::DateTimeOffset(value))
        | (ParameterKind::Duration, ParameterValue::Duration(value))
        | (ParameterKind::Time, ParameterValue::Time(value))
        | (ParameterKind::DateTime, ParameterValue::DateTime(value)) => target.extend_from_slice(&value.to_le_bytes()),
        (ParameterKind::Float64, ParameterValue::Float64(value)) => target.extend_from_slice(&value.to_le_bytes()),
        (ParameterKind::Boolean, ParameterValue::Boolean(value)) => target.push(u8::from(*value)),
        (ParameterKind::String, ParameterValue::String(value)) => write_string(target, value)?,
        (ParameterKind::Blob, ParameterValue::Blob(value)) => {
            write_varuint(target, value.len() as u32);
            target.extend_from_slice(value);
        }
        (ParameterKind::Decimal, ParameterValue::Decimal(bits)) => {
            for bit in bits { target.extend_from_slice(&bit.to_le_bytes()); }
        }
        (ParameterKind::Vector, ParameterValue::Vector(values)) => {
            write_varuint(target, values.len() as u32);
            for value in values { target.extend_from_slice(&value.to_le_bytes()); }
        }
        _ => return Err(FrameError::new("parameter value does not match its protocol type")),
    }
    Ok(())
}

fn read_value(reader: &mut Reader<'_>, kind: ParameterKind) -> Result<ParameterValue, FrameError> {
    match kind {
        ParameterKind::Int64 => Ok(ParameterValue::Int64(reader.i64()?)),
        ParameterKind::Timestamp => Ok(ParameterValue::Timestamp(reader.i64()?)),
        ParameterKind::Float64 => {
            let value = f64::from_le_bytes(reader.take(8)?.try_into().expect("fixed float size"));
            if !value.is_finite() { return Err(FrameError::new("Float64 values must be finite")); }
            Ok(ParameterValue::Float64(value))
        }
        ParameterKind::Boolean => Ok(ParameterValue::Boolean(reader.boolean()?)),
        ParameterKind::String => Ok(ParameterValue::String(reader.string(MAX_VALUE_BYTES, "string parameter")?)),
        ParameterKind::Blob => {
            let length = reader.varuint()? as usize;
            if length > MAX_VALUE_BYTES { return Err(FrameError::new("Blob exceeds its byte limit")); }
            Ok(ParameterValue::Blob(reader.take(length)?.to_vec()))
        }
        ParameterKind::Decimal => {
            let mut bits = [0i32; 4];
            for bit in &mut bits { *bit = reader.i32()?; }
            if !valid_decimal_bits(&bits) {
                return Err(FrameError::new("decimal flags use a reserved bit or scale above 28"));
            }
            Ok(ParameterValue::Decimal(bits))
        }
        ParameterKind::DateTimeOffset => {
            let value = reader.i64()?;
            if !(0..=MAX_DATETIME_TICKS).contains(&value) { return Err(FrameError::new("date ticks are out of range")); }
            Ok(ParameterValue::DateTimeOffset(value))
        }
        ParameterKind::Duration => Ok(ParameterValue::Duration(reader.i64()?)),
        ParameterKind::Time => {
            let value = reader.i64()?;
            if !(0..TICKS_PER_DAY).contains(&value) { return Err(FrameError::new("time ticks are out of range")); }
            Ok(ParameterValue::Time(value))
        }
        ParameterKind::Vector => {
            let dimension = reader.varuint()? as usize;
            if dimension == 0 || dimension > MAX_VECTOR_DIMENSION || dimension.checked_mul(4).is_none_or(|size| size > reader.remaining()) {
                return Err(FrameError::new("vector dimension is invalid"));
            }
            let mut values = Vec::with_capacity(dimension);
            for _ in 0..dimension {
                let value = f32::from_le_bytes(reader.take(4)?.try_into().expect("fixed float size"));
                if !value.is_finite() { return Err(FrameError::new("vector values must be finite")); }
                values.push(value);
            }
            Ok(ParameterValue::Vector(values))
        }
        ParameterKind::DateTime => {
            let value = reader.i64()?;
            if !(0..=MAX_DATETIME_TICKS).contains(&value) { return Err(FrameError::new("date ticks are out of range")); }
            Ok(ParameterValue::DateTime(value))
        }
        ParameterKind::Null => Err(FrameError::new("NULL with a value is invalid")),
    }
}

struct Reader<'a> { data: &'a [u8], offset: usize }

impl<'a> Reader<'a> {
    fn new(data: &'a [u8]) -> Self { Self { data, offset: 0 } }
    fn remaining(&self) -> usize { self.data.len().saturating_sub(self.offset) }
    fn take(&mut self, length: usize) -> Result<&'a [u8], FrameError> {
        let end = self.offset.checked_add(length).ok_or_else(|| FrameError::new("payload length overflow"))?;
        if end > self.data.len() { return Err(FrameError::new("payload ended before the value was complete")); }
        let value = &self.data[self.offset..end];
        self.offset = end;
        Ok(value)
    }
    fn byte(&mut self) -> Result<u8, FrameError> { Ok(self.take(1)?[0]) }
    fn boolean(&mut self) -> Result<bool, FrameError> {
        match self.byte()? { 0 => Ok(false), 1 => Ok(true), _ => Err(FrameError::new("boolean values must be 0 or 1")) }
    }
    fn varuint(&mut self) -> Result<u32, FrameError> {
        let mut value = 0u32;
        for index in 0..5 {
            let byte = self.byte()?;
            if index == 4 && byte & 0xF0 != 0 { return Err(FrameError::new("varuint32 value out of range")); }
            value |= u32::from(byte & 0x7F) << (index * 7);
            if byte & 0x80 == 0 { return Ok(value); }
        }
        Err(FrameError::new("varuint32 uses more than five bytes"))
    }
    fn string(&mut self, maximum: usize, label: &str) -> Result<String, FrameError> {
        let length = self.varuint()? as usize;
        if length > maximum { return Err(FrameError::new(format!("{label} exceeds its byte limit"))); }
        String::from_utf8(self.take(length)?.to_vec()).map_err(|_| FrameError::new(format!("{label} is not valid UTF-8")))
    }
    fn i32(&mut self) -> Result<i32, FrameError> { Ok(i32::from_le_bytes(self.take(4)?.try_into().expect("fixed integer size"))) }
    fn i64(&mut self) -> Result<i64, FrameError> { Ok(i64::from_le_bytes(self.take(8)?.try_into().expect("fixed integer size"))) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn minimal_request_matches_protocol_bytes() {
        let frame = encode_sql_execute_frame(42, "d", "S", &[]).expect("encode");
        assert_eq!(frame, hex("06000000010302002a000000010164015300"));
        let request = decode_sql_execute_frame(&frame).expect("decode");
        assert_eq!(request.database, "d");
        assert_eq!(request.sql, "S");
        assert!(request.parameters.is_empty());
    }

    #[test]
    fn all_parameter_kinds_round_trip() {
        let parameters = vec![
            Parameter { name: "p0".into(), kind: ParameterKind::Null, value: None },
            Parameter { name: "p1".into(), kind: ParameterKind::Int64, value: Some(ParameterValue::Int64(-7)) },
            Parameter { name: "p2".into(), kind: ParameterKind::Float64, value: Some(ParameterValue::Float64(1.5)) },
            Parameter { name: "p3".into(), kind: ParameterKind::Boolean, value: Some(ParameterValue::Boolean(true)) },
            Parameter { name: "p4".into(), kind: ParameterKind::String, value: Some(ParameterValue::String("你好".into())) },
            Parameter { name: "p5".into(), kind: ParameterKind::Blob, value: Some(ParameterValue::Blob(vec![0, 255])) },
            Parameter { name: "p6".into(), kind: ParameterKind::Timestamp, value: Some(ParameterValue::Timestamp(123456789)) },
            Parameter { name: "p7".into(), kind: ParameterKind::Decimal, value: Some(ParameterValue::Decimal([3, 0, 0, 0])) },
            Parameter { name: "p8".into(), kind: ParameterKind::DateTimeOffset, value: Some(ParameterValue::DateTimeOffset(638000000000000000)) },
            Parameter { name: "p9".into(), kind: ParameterKind::Duration, value: Some(ParameterValue::Duration(-123)) },
            Parameter { name: "p10".into(), kind: ParameterKind::Time, value: Some(ParameterValue::Time(123456789)) },
            Parameter { name: "p11".into(), kind: ParameterKind::Vector, value: Some(ParameterValue::Vector(vec![1.0, -2.5])) },
            Parameter { name: "p12".into(), kind: ParameterKind::DateTime, value: Some(ParameterValue::DateTime(638000000000000000)) },
        ];
        let frame = encode_sql_execute_frame(9, "demo", "INSERT", &parameters).expect("encode");
        let request = decode_sql_execute_frame(&frame).expect("decode");
        assert_eq!(request.parameters, parameters);
    }

    #[test]
    fn committed_response_round_trip_and_terminal_guard() {
        let frame = encode_sql_execute_response_frame(7, 13).expect("encode");
        assert_eq!(decode_sql_execute_response_frame(&frame).expect("decode").records_affected, 13);
        let mut invalid = frame;
        *invalid.last_mut().expect("outcome") = 0;
        assert!(decode_sql_execute_response_frame(&invalid).is_err());
    }

    #[test]
    fn rejects_non_ascii_names_invalid_decimal_and_oversize_values() {
        let non_ascii = Parameter { name: "参数".into(), kind: ParameterKind::Int64, value: Some(ParameterValue::Int64(1)) };
        assert!(encode_sql_execute_frame(1, "db", "SELECT 1", &[non_ascii]).is_err());
        assert!(!valid_decimal_bits(&[0, 0, 0, 0x0100_0000]));
        let oversized = Parameter { name: "p".into(), kind: ParameterKind::String, value: Some(ParameterValue::String("x".repeat(MAX_REQUEST_PAYLOAD_BYTES))) };
        assert!(encode_sql_execute_frame(1, "db", "SELECT 1", &[oversized]).is_err());
        let non_finite = Parameter { name: "p".into(), kind: ParameterKind::Float64, value: Some(ParameterValue::Float64(f64::NAN)) };
        assert!(encode_sql_execute_frame(1, "db", "SELECT 1", &[non_finite]).is_err());
    }

    fn hex(value: &str) -> Vec<u8> {
        value.as_bytes().chunks(2).map(|pair| {
            let digit = |byte: u8| match byte { b'0'..=b'9' => byte - b'0', b'a'..=b'f' => byte - b'a' + 10, b'A'..=b'F' => byte - b'A' + 10, _ => panic!("invalid hex") };
            digit(pair[0]) * 16 + digit(pair[1])
        }).collect()
    }
}
