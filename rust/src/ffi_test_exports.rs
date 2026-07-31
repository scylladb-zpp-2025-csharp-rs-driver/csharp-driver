//! Test-only FFI exports used to exercise the FFI *layer itself* from managed round-trip tests.
//!
//! ## Why these exist
//! A managed test that fabricates an `FFIString` from a pinned `byte[]` and then decodes it is
//! testing C# against C#: it asserts a layout it just made up, using an encoder it also owns. The
//! bytes never touch Rust, so nothing about the actual bridge is verified.
//!
//! Everything here exists so that the *unmanaged* side produces and consumes the FFI structs and the
//! managed side only observes the result. Rust owns the strings, slices, UUID bytes, resources and
//! exception handles; C# receives them through the same callbacks and the same marshalling code that
//! production uses, and asserts on what arrived.
//!
//! ## Compilation
//! These are gated on the `integration_testing` feature, not `#[cfg(test)]`. `cargo test` builds a
//! separate test harness binary, so a `#[cfg(test)]` symbol never appears in the `cdylib` that C#
//! loads - a feature is the only thing that can put an export in `libcsharp_wrapper.so`. The
//! `check-no-test-exports` make target asserts a shippable build exports none of them.
//!
//! ## Naming
//! `ffi_test_produce_*` hands Rust-owned data to a managed callback. `ffi_test_echo_*` takes managed
//! data, round-trips it through Rust and hands it back. Both directions matter: a producer catches a
//! bad Rust->C# decode, an echo catches a bad C#->Rust encode.

use std::ffi::c_void;
use std::sync::LazyLock;
use std::sync::atomic::{AtomicUsize, Ordering};

use crate::error_conversion::{FFIException, FFIMaybeException};
use crate::ffi::{
    ArcFFI, BridgedBorrowedSharedPtr, CSharpManagedStringPtr, CSharpStr, FFI, FFIBool, FFIPtr,
    FFISlice, FFIStr, FromArc, IpOctets, WriteStringCallback, ffi_callback_for_each,
};
use crate::task::{BridgedFuture, ExceptionConstructors, ManuallyDestructible, Tcb};

/// Opaque handle to whatever managed object a test callback wants to write into. Rust never
/// dereferences it; it is threaded through to the callback unchanged, exactly like the production
/// `CSharpManagedStringPtr`.
enum CSharpTestContext {}

/// Pointer to a managed test context. `Copy`, so it can be handed to `ffi_callback_for_each`.
///
/// A newtype rather than a bare alias, so the opaque pointee stays unnameable outside this module -
/// the same shape as the production `CSharpManagedStringPtr`.
#[derive(Clone, Copy)]
#[repr(transparent)]
pub(crate) struct TestCtx<'a>(FFIPtr<'a, CSharpTestContext>);

/// Managed sink for a Rust-owned byte slice. C# declares the parameter as `FFISliceRaw` because
/// `[UnmanagedCallersOnly]` rejects generic structs - which is exactly the reinterpretation we want
/// covered by a real Rust-produced value.
type ByteSliceSink = extern "C" fn(FFISlice<'_, u8>, TestCtx<'_>) -> FFIMaybeException;

/// Managed sink for a Rust-owned `u32` slice. Distinct element size from [`ByteSliceSink`], so a
/// stride or element-size mistake in `FFISlice<T>.ToSpan()` cannot hide.
type U32SliceSink = extern "C" fn(FFISlice<'_, u32>, TestCtx<'_>) -> FFIMaybeException;

/// Managed sink taking nothing but the context. Used to hand control back to C# in the middle of a
/// Rust call (see [`ffi_test_gc_move_probe`]).
type ContextSink = extern "C" fn(TestCtx<'_>) -> FFIMaybeException;

/// Managed sinks for the ABI manifest: one call per type, then one per field of that type.
type AbiTypeSink = extern "C" fn(TestCtx<'_>, FFIStr<'_>, usize, usize) -> FFIMaybeException;
type AbiFieldSink = extern "C" fn(TestCtx<'_>, FFIStr<'_>, FFIStr<'_>, usize) -> FFIMaybeException;

/*
 * ABI manifest
 */

/// Streams the layout Rust chose for every FFI struct to the managed side.
///
/// `emit_type` is called once per struct with `(name, size, align)`, followed by one `emit_field`
/// call per field with `(type_name, field_name, offset)`. `AbiLayoutTests` compares each entry
/// against `Marshal.SizeOf` / `Marshal.OffsetOf` **by field name**, so a transposed pair of fields -
/// which an equal-total-size check cannot see - fails with the field's name in the message.
///
/// Iteration stops at the first exception a sink returns, which is how a managed assertion failure
/// gets reported back.
///
/// # Safety
/// `emit_type` and `emit_field` must be valid managed callbacks, and `ctx` must stay alive for the
/// duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_abi_manifest(
    ctx: TestCtx<'_>,
    emit_type: AbiTypeSink,
    emit_field: AbiFieldSink,
) -> FFIMaybeException {
    for ty in crate::abi::all_types() {
        // `FFIStr` is deliberately not `Copy` - it borrows - so build a fresh one per call.
        let res = emit_type(ctx, FFIStr::new(ty.name), ty.size, ty.align);
        if res.has_exception() {
            return res;
        }

        for field in ty.fields {
            let res = emit_field(
                ctx,
                FFIStr::new(ty.name),
                FFIStr::new(field.name),
                field.offset,
            );
            if res.has_exception() {
                return res;
            }
        }
    }

    FFIMaybeException::ok()
}

/*
 * Exception constructor table
 */

/// Number of slots in the constructor table, so the managed test can assert it covers every one
/// instead of silently testing a stale subset.
#[unsafe(no_mangle)]
pub extern "C" fn ffi_test_exception_slot_count() -> usize {
    crate::task::CONSTRUCTOR_ABI_FIELDS.len()
}

/// Reports the name of constructor slot `slot`, for readable assertion failures.
///
/// # Safety
/// `cb` must be a valid managed callback and `ctx` must stay alive for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_exception_slot_name(
    slot: usize,
    cb: WriteStringCallback,
    ctx: CSharpManagedStringPtr,
) -> FFIMaybeException {
    let name = crate::task::CONSTRUCTOR_ABI_FIELDS
        .get(slot)
        .map(|f| f.name)
        .unwrap_or("<out of range>");
    cb(FFIStr::new(name), ctx)
}

/// The marker Rust sends through slot `slot`, and which must therefore appear in the managed
/// exception's message.
///
/// Defined here so the managed test asserts against the value Rust actually used rather than a copy
/// that can drift out of sync.
fn slot_marker(slot: usize) -> String {
    if slot == OPERATION_TIMED_OUT_SLOT {
        // The only slot taking a scalar rather than a string. It is sent the slot index as its
        // timeout, so the number - not the textual marker - is what reaches the message.
        return slot.to_string();
    }

    format!("ffi-test-slot-{slot}")
}

/// Index of the one constructor slot whose payload is an `i32` rather than a string.
const OPERATION_TIMED_OUT_SLOT: usize = 10;

/// Reports the marker [`ffi_test_build_exception`] will send through slot `slot`.
///
/// # Safety
/// `cb` must be a valid managed callback and `ctx` must stay alive for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_exception_slot_marker(
    slot: usize,
    cb: WriteStringCallback,
    ctx: CSharpManagedStringPtr,
) -> FFIMaybeException {
    cb(FFIStr::new(&slot_marker(slot)), ctx)
}

/// Builds a managed exception through constructor slot `slot` of the real table.
///
/// Every one of the 23 slots is reachable, and the slot index is the field's position in
/// `ExceptionConstructors`, which is the same position C#'s `Constructors` struct uses. The managed
/// test walks `0..ffi_test_exception_slot_count()` and asserts the *exact* managed type it got back.
/// Since no two slots map to the same exception type, that catches any transposition - in this
/// table, in C#'s struct, or in the 23-positional-argument initializer that fills it, none of which
/// a size or offset check can see.
///
/// # Safety
/// `constructors` must point to a valid, live `ExceptionConstructors` table (the process-wide one C#
/// hands to Rust). The returned `FFIException` owns a C# GCHandle that the caller must release, by
/// throwing it or freeing it explicitly, exactly as in production.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_build_exception(
    slot: usize,
    constructors: *const ExceptionConstructors,
) -> FFIException {
    // SAFETY: contract documented above; C# always passes its live, leaked table pointer.
    let ctors = unsafe { &*constructors };
    let marker = slot_marker(slot);

    match slot {
        0 => ctors
            .already_exists_constructor
            .construct_from_rust(&marker, &format!("{marker}-tbl")),
        1 => ctors
            .already_shutdown_exception_constructor
            .construct_from_rust(&marker),
        2 => ctors
            .argument_exception_constructor
            .construct_from_rust(&marker),
        3 => ctors
            .deserialization_exception_constructor
            .construct_from_rust(&marker),
        4 => ctors
            .function_failure_exception_constructor
            .construct_from_rust(&marker),
        5 => ctors
            .invalid_argument_exception_constructor
            .construct_from_rust(&marker),
        6 => ctors
            .invalid_configuration_in_query_constructor
            .construct_from_rust(&marker),
        7 => ctors.invalid_query_constructor.construct_from_rust(&marker),
        8 => ctors
            .invalid_type_exception_constructor
            .construct_from_rust(&marker),
        9 => ctors
            .no_host_available_exception_constructor
            .construct_from_rust(&marker),
        // No string payload: the slot index doubles as the timeout so the managed message still
        // carries something slot-specific to assert on. See `slot_marker`.
        OPERATION_TIMED_OUT_SLOT => ctors
            .operation_timed_out_exception_constructor
            .construct_from_rust(slot as i32),
        11 => ctors
            .prepared_query_not_found_exception_constructor
            .construct_from_rust(&marker, PREPARED_ID_BYTES),
        12 => ctors
            .request_invalid_exception_constructor
            .construct_from_rust(&marker),
        13 => ctors
            .rust_exception_constructor
            .construct_from_rust(&marker),
        14 => ctors
            .schema_agreement_required_host_absent_exception_constructor
            .construct_from_rust(&marker),
        15 => ctors
            .schema_agreement_rows_result_exception_constructor
            .construct_from_rust(&marker),
        16 => ctors
            .schema_agreement_single_row_exception_constructor
            .construct_from_rust(&marker),
        17 => ctors
            .schema_agreement_timeout_exception_constructor
            .construct_from_rust(&marker),
        18 => ctors
            .serialization_exception_constructor
            .construct_from_rust(&marker),
        19 => ctors
            .syntax_error_exception_constructor
            .construct_from_rust(&marker),
        20 => ctors
            .trace_retrieval_exception_constructor
            .construct_from_rust(&marker),
        21 => ctors
            .truncate_exception_constructor
            .construct_from_rust(&marker),
        22 => ctors
            .unauthorized_exception_constructor
            .construct_from_rust(&marker),
        other => panic!("ffi_test_build_exception: constructor slot {other} out of range"),
    }
}

/// Statement id bytes sent through the `PreparedQueryNotFound` slot. Chosen to be non-UTF-8 and to
/// contain a NUL, so a managed decode that treats the payload as a C string is caught.
const PREPARED_ID_BYTES: &[u8] = &[0xDE, 0xAD, 0x00, 0xBE, 0xEF];

/// Reports the statement id [`ffi_test_build_exception`] sends through the `PreparedQueryNotFound`
/// slot, so the managed test compares against Rust's bytes rather than a duplicated literal.
///
/// # Safety
/// `cb` must be a valid managed callback and `ctx` must stay alive for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_prepared_id_bytes(
    cb: ByteSliceSink,
    ctx: TestCtx<'_>,
) -> FFIMaybeException {
    cb(FFISlice::new(PREPARED_ID_BYTES), ctx)
}

/*
 * Strings produced by Rust
 */

/// A long string, allocated once. Exercises a length that cannot be confused with a small-string
/// optimisation and that spans more than one page.
static LONG_STRING: LazyLock<String> = LazyLock::new(|| "sc\u{ff}lla-".repeat(4096));

/// The strings Rust hands to C#, indexed by the `kind` argument of [`ffi_test_produce_str`].
///
/// Each entry targets a specific way a UTF-8 bridge breaks. In particular `EmbeddedNul` catches a
/// managed decode that stops at the first NUL instead of honouring the length, and `Astral` catches
/// a decoder that assumes one UTF-16 code unit per scalar.
fn produced_str(kind: u8) -> Option<&'static str> {
    match kind {
        // Non-null pointer, zero length: a valid empty string, distinct from a null one.
        0 => Some(""),
        1 => Some("SELECT * FROM system.peers"),
        2 => Some("caf\u{e9} / \u{65e5}\u{672c}\u{8a9e}"),
        // Astral-plane scalars: 4 bytes of UTF-8, a surrogate pair in UTF-16.
        3 => Some("\u{1F600}\u{1F680}\u{10FFFF}"),
        // Interior NUL. A length-honouring decode keeps all three chars.
        4 => Some("a\0b"),
        5 => Some(&LONG_STRING),
        _ => None,
    }
}

/// Number of string kinds, so the managed test can assert it covers all of them.
#[unsafe(no_mangle)]
pub extern "C" fn ffi_test_str_kind_count() -> u8 {
    // `kind` 6 is the null string, which has no `&str` behind it.
    7
}

/// Hands Rust-owned string `kind` to C# through the production [`WriteStringCallback`].
///
/// The `&str` lives in Rust (a `const`, or a `LazyLock<String>`), so what C# decodes is genuinely
/// Rust-produced bytes rather than a buffer the managed test encoded itself.
///
/// # Safety
/// `cb` must be a valid managed callback and `ctx` must stay alive for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_produce_str(
    kind: u8,
    cb: WriteStringCallback,
    ctx: CSharpManagedStringPtr,
) -> FFIMaybeException {
    let ffi = match produced_str(kind) {
        Some(s) => FFIStr::new(s),
        // The one kind with no backing `&str`: a null FFIStr, which C# must surface as a null
        // string rather than as the empty one.
        None => FFIStr::null(),
    };
    cb(ffi, ctx)
}

/// Byte length of string `kind` as Rust measures it.
///
/// The managed test compares this against `Encoding.UTF8.GetByteCount` of the value it decoded,
/// which cross-checks the two encoders instead of trusting either alone.
#[unsafe(no_mangle)]
pub extern "C" fn ffi_test_produced_str_len(kind: u8) -> usize {
    produced_str(kind).map_or(0, str::len)
}

/// Round-trips a managed string: C# -> Rust as an `FFIStr`, back to C# through the production
/// callback. Rust re-borrows the same bytes, so any corruption is the bridge's.
///
/// # Safety
/// `input` must be a valid `FFIStr` that stays alive for the call; `cb` and `ctx` must be a valid
/// managed callback/context pair.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_echo_str(
    input: FFIStr<'_>,
    cb: WriteStringCallback,
    ctx: CSharpManagedStringPtr,
) -> FFIMaybeException {
    cb(input, ctx)
}

/// Round-trips a managed **NUL-terminated** string through `CSharpStr::as_cstr`.
///
/// This is the only path where Rust derives the length itself instead of being told it, and it is
/// used throughout `metadata.rs` for keyspace/table names. A managed string containing an interior
/// NUL is silently truncated here - which is a real constraint worth having pinned by a test.
///
/// # Safety
/// `input` must be a valid pointer to a NUL-terminated, UTF-8 encoded string that stays alive for
/// the call; `cb` and `ctx` must be a valid managed callback/context pair.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_echo_cstr(
    input: CSharpStr<'_>,
    cb: WriteStringCallback,
    ctx: CSharpManagedStringPtr,
) -> FFIMaybeException {
    let ffi = match input.as_cstr() {
        Some(cstr) => FFIStr::new(cstr.to_str().expect("test always sends valid UTF-8")),
        None => FFIStr::null(),
    };
    cb(ffi, ctx)
}

/*
 * Slices produced by Rust
 */

/// A slice larger than one page, with a non-repeating pattern so a truncated or shifted copy is
/// visible rather than plausible.
static LARGE_BYTES: LazyLock<Vec<u8>> =
    LazyLock::new(|| (0..8192u32).map(|i| (i % 251) as u8).collect());

fn produced_bytes(kind: u8) -> Option<&'static [u8]> {
    match kind {
        0 => Some(&[]),
        1 => Some(&[0xAB]),
        2 => Some(&[0xDE, 0xAD, 0xBE, 0xEF]),
        3 => Some(&LARGE_BYTES),
        _ => None,
    }
}

/// Number of byte-slice kinds, so the managed test can assert it covers all of them.
#[unsafe(no_mangle)]
pub extern "C" fn ffi_test_byte_slice_kind_count() -> u8 {
    4
}

/// Hands Rust-owned byte slice `kind` to C#.
///
/// C# receives it as an `FFISliceRaw` and reinterprets it with `As<byte>()`. Because the (ptr, len)
/// pair is produced by `FFISlice::new` on the Rust side, this verifies the reinterpretation against
/// a real Rust layout instead of one the managed test fabricated.
///
/// # Safety
/// `cb` must be a valid managed callback and `ctx` must stay alive for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_produce_byte_slice(
    kind: u8,
    cb: ByteSliceSink,
    ctx: TestCtx<'_>,
) -> FFIMaybeException {
    let bytes = produced_bytes(kind).expect("unknown byte slice kind");
    cb(FFISlice::new(bytes), ctx)
}

/// Length of byte slice `kind` as Rust measures it.
#[unsafe(no_mangle)]
pub extern "C" fn ffi_test_produced_byte_slice_len(kind: u8) -> usize {
    produced_bytes(kind).map_or(0, <[u8]>::len)
}

/// Values chosen so that a wrong element stride, or a byte-order mistake, produces obviously wrong
/// numbers rather than merely different ones.
const U32_VALUES: &[u32] = &[0, 1, 0x1234_5678, u32::MAX, 0x00FF_00FF];

/// Hands a Rust-owned `u32` slice to C#. See [`U32SliceSink`].
///
/// # Safety
/// `cb` must be a valid managed callback and `ctx` must stay alive for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_produce_u32_slice(
    cb: U32SliceSink,
    ctx: TestCtx<'_>,
) -> FFIMaybeException {
    cb(FFISlice::new(U32_VALUES), ctx)
}

/// Reports the expected `u32` values one at a time, so the managed test asserts against Rust's
/// numbers rather than a duplicated literal array.
///
/// # Safety
/// `cb` must be a valid managed callback and `ctx` must stay alive for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_expected_u32_values(
    cb: unsafe extern "C" fn(TestCtx<'_>, u32) -> FFIMaybeException,
    ctx: TestCtx<'_>,
) -> FFIMaybeException {
    // SAFETY: the caller guarantees `cb`/`ctx` are valid for the duration of the iteration.
    unsafe { ffi_callback_for_each(ctx, cb, U32_VALUES.iter().copied()) }
}

/// Round-trips a managed byte slice back as a string, exercising `FFISlice<u8>` -> `FFIStr`.
///
/// # Safety
/// `bytes` must be a valid `FFISlice` of UTF-8 that stays alive for the call; `cb` and `ctx` must be
/// a valid managed callback/context pair.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_echo_slice_as_str(
    bytes: FFISlice<'_, u8>,
    cb: WriteStringCallback,
    ctx: CSharpManagedStringPtr,
) -> FFIMaybeException {
    let s = std::str::from_utf8(bytes.as_slice()).expect("test always sends valid UTF-8");
    cb(FFIStr::new(s), ctx)
}

/// Hands the octets of a Rust-built `IpAddr` to C#, covering both the 4- and 16-byte arms of
/// `IpOctets`, which `metadata.rs` uses for every node address.
///
/// # Safety
/// `cb` must be a valid managed callback and `ctx` must stay alive for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_produce_ip_octets(
    v6: FFIBool,
    cb: ByteSliceSink,
    ctx: TestCtx<'_>,
) -> FFIMaybeException {
    let ip: std::net::IpAddr = if bool::from(v6) {
        "2001:db8::dead:beef".parse().expect("valid IPv6 literal")
    } else {
        "192.0.2.17".parse().expect("valid IPv4 literal")
    };
    let octets = IpOctets::new(ip);
    cb(FFISlice::new(octets.as_slice()), ctx)
}

/*
 * Booleans
 */

/// Round-trips an `FFIBool`. Rust decodes it to a native `bool` and re-encodes it, so a mismatch in
/// the byte convention shows up as an inverted value rather than as silent garbage.
#[unsafe(no_mangle)]
pub extern "C" fn ffi_test_echo_bool(value: FFIBool) -> FFIBool {
    FFIBool::from(bool::from(value))
}

/// Reports the raw byte Rust decoded the `FFIBool` to. Pins the wire convention (0 / 1) that C#'s
/// `byte` field depends on.
#[unsafe(no_mangle)]
pub extern "C" fn ffi_test_bool_as_byte(value: FFIBool) -> u8 {
    u8::from(bool::from(value))
}

/*
 * UUIDs
 */

/// Parses a managed 16-byte UUID and hands back its canonical text form.
///
/// This is the assertion that `GuidToFFIFormat` exists for: .NET's default `Guid` byte order is
/// mixed-endian, while the `uuid` crate expects RFC 4122 network order. Round-tripping a `Guid`
/// through .NET's own inverse cannot detect a byte-order mistake, because both halves make it. Only
/// asking Rust what UUID it sees can.
///
/// # Safety
/// `bytes` must be a valid 16-byte `FFISlice` that stays alive for the call; `cb` and `ctx` must be
/// a valid managed callback/context pair.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_uuid_to_string(
    bytes: FFISlice<'_, u8>,
    cb: WriteStringCallback,
    ctx: CSharpManagedStringPtr,
) -> FFIMaybeException {
    let uuid = uuid::Uuid::from_slice(bytes.as_slice()).expect("test always sends 16 bytes");
    cb(FFIStr::new(&uuid.to_string()), ctx)
}

/// Parses a canonical UUID string in Rust and hands back the 16 bytes in RFC 4122 order, so the
/// managed side can check `GuidFromFFIFormat` against Rust's encoding as well as its decoding.
///
/// # Safety
/// `text` must be a valid `FFIStr` that stays alive for the call; `cb` and `ctx` must be a valid
/// managed callback/context pair.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_uuid_to_bytes(
    text: FFIStr<'_>,
    cb: ByteSliceSink,
    ctx: TestCtx<'_>,
) -> FFIMaybeException {
    // Reconstruct the &str from the FFIStr the caller handed us. `FFIStr` intentionally exposes no
    // accessor (it is a Rust -> C# type), so go through the slice it wraps.
    let uuid = uuid::Uuid::parse_str(ffi_str_as_str(&text)).expect("test sends a canonical UUID");
    cb(FFISlice::new(uuid.as_bytes()), ctx)
}

/// Views an incoming `FFIStr` as a `&str`.
///
/// `FFIStr` is a Rust -> C# type and deliberately has no public accessor, but the test exports need
/// to read strings coming the other way.
fn ffi_str_as_str<'a>(s: &'a FFIStr<'_>) -> &'a str {
    // SAFETY: every caller here is a test export whose managed counterpart always sends valid UTF-8.
    std::str::from_utf8(s.as_bytes()).expect("test always sends valid UTF-8")
}

/*
 * Iterating a managed callback
 */

/// Drives the production [`ffi_callback_for_each`] over `count` items.
///
/// The interesting behaviour is the early exit: as soon as the managed callback returns an
/// exception, iteration must stop and that exception must be propagated unchanged. The managed test
/// throws on a chosen item and then asserts both the number of invocations and the exception that
/// came back - neither of which the type system can guarantee.
///
/// # Safety
/// `cb` must be a valid managed callback and `ctx` must stay alive for the iteration.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_for_each_u32(
    count: u32,
    cb: unsafe extern "C" fn(TestCtx<'_>, u32) -> FFIMaybeException,
    ctx: TestCtx<'_>,
) -> FFIMaybeException {
    // SAFETY: the caller guarantees `cb`/`ctx` are valid for the duration of the iteration.
    unsafe { ffi_callback_for_each(ctx, cb, 0..count) }
}

/*
 * A real Rust-owned resource
 */

/// Number of live [`TestResource`] allocations. Lets the managed test assert deterministically that
/// disposing a `RustResource` really ran the Rust destructor, without relying on a GC probe.
static LIVE_TEST_RESOURCES: AtomicUsize = AtomicUsize::new(0);

/// An `Arc`-backed resource with a real Rust destructor, so `RustResourceTests` can exercise
/// `SafeHandle` against genuine Rust-owned memory instead of a managed stand-in whose "destructor"
/// is a C# counter and whose handle is a fabricated address.
struct TestResource {
    value: u64,
}

impl FFI for TestResource {
    type Origin = FromArc;
}

impl Drop for TestResource {
    fn drop(&mut self) {
        LIVE_TEST_RESOURCES.fetch_sub(1, Ordering::SeqCst);
    }
}

/// Allocates a resource and hands it to C# as a `ManuallyDestructible`, exactly as the production
/// session/metadata constructors do. The destructor in the returned struct is the real
/// `ArcFFI`-based one.
#[unsafe(no_mangle)]
pub extern "C" fn ffi_test_make_resource(value: u64) -> ManuallyDestructible {
    LIVE_TEST_RESOURCES.fetch_add(1, Ordering::SeqCst);
    ManuallyDestructible::from_destructible(std::sync::Arc::new(TestResource { value }))
}

/// Reads back a resource's payload through `ArcFFI::as_ref`.
///
/// Proves the handle C# is holding really points at the Rust object: if `ManuallyDestructible` were
/// marshalled wrongly, or the pointer were mangled on the way out and back, this returns the wrong
/// value (or trips ASAN) instead of appearing to work.
///
/// # Safety
/// `ptr` must be a live pointer previously produced by [`ffi_test_make_resource`] and not yet
/// destroyed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_resource_value(ptr: BridgedBorrowedSharedPtr<'_, c_void>) -> u64 {
    // SAFETY: the caller guarantees the pointer came from `ffi_test_make_resource`, which stored a
    // `TestResource` behind an `Arc` before casting to `c_void`.
    let typed: BridgedBorrowedSharedPtr<'_, TestResource> = unsafe { ptr.cast() };
    ArcFFI::as_ref(typed)
        .expect("resource pointer must be non-null")
        .value
}

/// Live [`TestResource`] count. Zero after every disposal means no leak and no double-free.
#[unsafe(no_mangle)]
pub extern "C" fn ffi_test_live_resources() -> usize {
    LIVE_TEST_RESOURCES.load(Ordering::SeqCst)
}

/*
 * Async completion
 */

/// Completes a `Tcb<FFIBool>` synchronously, on the caller's thread.
#[unsafe(no_mangle)]
pub extern "C" fn ffi_test_complete_bool_task(tcb: Tcb<FFIBool>, value: FFIBool) {
    tcb.complete_task(value);
}

/// Fails a `Tcb<FFIBool>` synchronously with a real `ArgumentException` from the supplied table.
///
/// # Safety
/// `ctors` must point to a valid, live `ExceptionConstructors` table.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_fail_bool_task(tcb: Tcb<FFIBool>, ctors: &ExceptionConstructors) {
    let exception = ctors
        .argument_exception_constructor
        .construct_from_rust("test async failure");
    tcb.fail_task(exception);
}

/// Completes a `Tcb<FFIBool>` from a tokio worker thread, after the P/Invoke has already returned.
///
/// This is the shape production actually uses, and it is a different code path from the synchronous
/// case: the GCHandle has to survive past the call that created it, and the managed continuation
/// runs on a thread the CLR never saw enter. Completing on the caller's thread cannot catch a
/// mistake in either.
#[unsafe(no_mangle)]
pub extern "C" fn ffi_test_complete_bool_task_async(tcb: Tcb<FFIBool>, value: FFIBool) {
    BridgedFuture::spawn_detached(async move {
        tokio::task::yield_now().await;
        tcb.complete_task(value);
    });
}

/// Fails a `Tcb<FFIBool>` from a tokio worker thread. See
/// [`ffi_test_complete_bool_task_async`].
///
/// # Safety
/// `ctors` must point to a valid, live `ExceptionConstructors` table that outlives the spawned task.
/// C#'s table is a leaked unmanaged allocation, so this holds for the process lifetime.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_fail_bool_task_async(
    tcb: Tcb<FFIBool>,
    ctors: &'static ExceptionConstructors,
) {
    BridgedFuture::spawn_detached(async move {
        tokio::task::yield_now().await;
        let exception = ctors
            .argument_exception_constructor
            .construct_from_rust("test async failure");
        tcb.fail_task(exception);
    });
}

/*
 * GC movement probe
 */

/// What Rust observed about a managed buffer either side of a forced garbage collection.
#[repr(C)]
pub struct GcProbeResult {
    /// Address Rust was given for the buffer's first byte.
    pub addr: usize,
    /// Hash of the bytes at that address, read before the managed callback ran.
    pub hash_before: u64,
    /// Hash of the bytes at the *same address*, read after the managed callback ran.
    pub hash_after: u64,
}

fn fnv1a(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325u64;
    for &b in bytes {
        hash ^= u64::from(b);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}

/// Checks that a managed buffer handed to Rust does not move while Rust holds a pointer to it.
///
/// Rust records the address and hashes the contents, calls back into C# (which forces a blocking,
/// compacting collection and churns allocations), then re-reads **the same address** and hashes it
/// again. If the buffer was not pinned and the GC relocated it, the second read observes whatever
/// now occupies that memory and the hashes differ.
///
/// This is the check AddressSanitizer cannot make: the CLR's GC heap is not tracked by ASAN, and a
/// collector relocating an object is, as far as the sanitizer can tell, a legal write to legal
/// memory. Only reading back through the stale pointer can reveal it.
///
/// # Safety
/// `buf` must be a valid slice for the duration of the call - which is precisely the property under
/// test, so a caller passing unpinned managed memory may observe corruption or crash. That negative
/// case belongs in a deliberately isolated test.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_test_gc_move_probe(
    buf: FFISlice<'_, u8>,
    provoke_gc: ContextSink,
    ctx: TestCtx<'_>,
) -> GcProbeResult {
    let addr = buf.as_slice().as_ptr() as usize;
    let hash_before = fnv1a(buf.as_slice());

    // Hand control back to C#, which forces a compacting collection.
    let _ = provoke_gc(ctx);

    // Re-read through the pointer we were originally given.
    let hash_after = fnv1a(buf.as_slice());

    GcProbeResult {
        addr,
        hash_before,
        hash_after,
    }
}
