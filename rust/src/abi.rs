//! Machine-readable description of the layout Rust chose for every struct that crosses the FFI
//! boundary.
//!
//! ## Why this exists
//! Every `#[repr(C)]` struct in this crate has a hand-written C# twin. The only thing keeping the
//! two in sync is a comment saying "mirror all changes in the exact same order". Comparing total
//! `size_of` is not enough: two structs with the same size but two fields transposed compare equal,
//! and that is precisely the edit a human makes by accident.
//!
//! So each module that owns FFI structs publishes a [`AbiType`] describing *where the Rust compiler
//! actually placed each field*. The `ffi_test_abi_manifest` export streams the whole set to the
//! managed test suite, which compares it against `Marshal.SizeOf` / `Marshal.OffsetOf` **by field
//! name**. Adding a field on one side and forgetting the other then fails with the field's name in
//! the message, instead of silently corrupting reads at run time.
//!
//! Field *sizes* are deliberately not described. A field whose size diverges shifts every later
//! field's offset, and a divergence in the final field changes the total size - both of which are
//! already checked. Recording sizes would mean naming each field's type a second time, which is
//! another thing that can rot.
//!
//! This module is compiled only for tests and for the `integration_testing` feature; it is absent
//! from a shippable build.

/// Where the Rust compiler placed one field of an FFI struct.
pub(crate) struct AbiField {
    /// The field's name **as the managed side spells it**. Usually identical to the Rust name, but
    /// deliberately allowed to differ where a Rust newtype flattens into a C# struct (for example
    /// `FFIStr`'s single `slice` field is described as the `ptr`/`len` pair that C# declares).
    pub(crate) name: &'static str,
    pub(crate) offset: usize,
}

/// The layout of one struct that crosses the FFI boundary.
pub(crate) struct AbiType {
    /// Name used to pair this description with a managed type. See `AbiLayoutTests` for the map.
    pub(crate) name: &'static str,
    pub(crate) size: usize,
    pub(crate) align: usize,
    pub(crate) fields: &'static [AbiField],
}

/// Describes a type and its field offsets.
///
/// `$name` is the name the managed side knows the type by; `$ty` is the Rust type. Each remaining
/// entry is `managed_name => rust_field_path`, where the path may be nested so that a newtype
/// wrapper can be described in terms of the flat fields C# declares.
macro_rules! abi_type {
    ($name:literal, $ty:ty $(, $field:literal => $($path:tt).+)* $(,)?) => {
        $crate::abi::AbiType {
            name: $name,
            size: ::std::mem::size_of::<$ty>(),
            align: ::std::mem::align_of::<$ty>(),
            fields: &[
                $(
                    $crate::abi::AbiField {
                        name: $field,
                        offset: ::std::mem::offset_of!($ty, $($path).+),
                    }
                ),*
            ],
        }
    };
}

pub(crate) use abi_type;

/// Every FFI struct with a managed twin, gathered from the modules that own them.
pub(crate) fn all_types() -> impl Iterator<Item = &'static AbiType> {
    crate::ffi::abi::TYPES
        .iter()
        .chain(crate::task::abi::TYPES)
        .chain(crate::error_conversion::abi::TYPES)
}
