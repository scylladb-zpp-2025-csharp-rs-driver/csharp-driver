use crate::error_conversion::FFIMaybeException;
use std::ffi::{CStr, c_char, c_void};
use std::fmt::Debug;
use std::marker::PhantomData;
use std::ptr::NonNull;
use std::sync::{Arc, Weak};

mod sealed {
    // This is a sealed trait - its whole purpose is to be unnameable.
    // This means we need to disable the check.
    #[expect(unnameable_types)]
    pub trait Sealed {}
}

/// A trait representing ownership (i.e. Rust mutability) of the pointer.
///
/// Pointer can either be [`Exclusive`] or [`Shared`].
///
/// ## Shared pointers
/// Shared pointers can only be converted to **immutable** Rust referential types.
/// There is no way to obtain a mutable reference from such pointer.
///
/// In some cases, we need to be able to mutate the data behind a shared pointer.
///
/// ## Exclusive pointers
/// Exclusive pointers can be converted to both immutable and mutable Rust referential types.
pub trait Ownership: sealed::Sealed {}

/// Represents shared (immutable) pointer.
pub struct Shared;
impl sealed::Sealed for Shared {}
impl Ownership for Shared {}

/// Represents exclusive (mutable) pointer.
pub struct Exclusive;
impl sealed::Sealed for Exclusive {}
impl Ownership for Exclusive {}

/// Represents additional properties of the pointer.
pub trait Properties: sealed::Sealed {
    type Ownership: Ownership;
}

impl<O: Ownership> Properties for O {
    type Ownership = O;
}

/// Represents a valid non-dangling pointer to Rust-allocated data.
///
/// ## Safety and validity guarantees
/// Apart from trivial constructors such as [`BridgedPtr::null()`] and [`BridgedPtr::null_mut()`], there
/// is only one way to construct a [`BridgedPtr`] instance - from raw pointer via [`BridgedPtr::from_raw()`].
/// This constructor is `unsafe`. It is user's responsibility to ensure that the raw pointer
/// provided to the constructor is **valid**. In other words, the pointer comes from some valid
/// allocation, or from some valid reference.
///
/// ## Generic lifetime and aliasing guarantees
/// We distinguish two types of pointers: shared ([`Shared`]) and exclusive ([`Exclusive`]).
/// Shared pointers can be converted to immutable (&) references, while exclusive pointers
/// can be converted to either immutable (&) or mutable (&mut) reference. User needs to pick
/// the correct mutability property of the pointer during construction. This is yet another
/// reason why [`BridgedPtr::from_raw`] is `unsafe`.
///
/// Pointer is parameterized by the lifetime. Thanks to that, we can tell whether the pointer
/// **owns** or **borrows** the pointee. Once again, user is responsible for "picking"
/// the correct lifetime when creating the pointer. For example, when raw pointer
/// comes from [`Box::into_raw()`], user could create a [`BridgedPtr<'static, T, (Exclusive,)>`].
/// `'static` lifetime represents that user is the exclusive **owner** of the pointee, and
/// is responsible for freeing the memory (e.g. via [`Box::from_raw()`]).
/// On the other hand, when pointer is created from some immutable reference `&'a T`,
/// the correct choice of BridgedPtr would be [`BridgedPtr<'a, T, (Shared,)>`]. It means that
/// holder of the created pointer **borrows** the pointee (with some lifetime `'a`
/// inherited from the immutable borrow `&'a T`).
///
/// Both [`BridgedPtr::into_ref()`] and [`BridgedPtr::into_mut_ref()`] consume the pointer.
/// At first glance, it seems impossible to obtain multiple immutable reference from one pointer.
/// This is why pointer reborrowing mechanism is introduced. There are two methods: [`BridgedPtr::borrow()`]
/// and [`BridgedPtr::borrow_mut()`]. Both of them cooperate with borrow checker and enforce
/// aliasing XOR mutability principle at compile time.
///
/// ## Safe conversions to referential types
/// Thanks to the above guarantees, conversions to referential types are **safe**.
/// See methods [`BridgedPtr::into_ref()`] and [`BridgedPtr::into_mut_ref()`].
///
/// ## Memory layout
/// We use repr(transparent), so the struct has the same layout as underlying [`Option<NonNull<T>>`].
/// Thanks to <https://doc.rust-lang.org/std/option/#representation optimization>,
/// we are guaranteed, that for `T: Sized`, our struct has the same layout
/// and function call ABI as simply [`NonNull<T>`].
#[repr(transparent)]
pub struct BridgedPtr<'a, T: Sized, P: Properties> {
    ptr: Option<NonNull<T>>,
    _phantom: PhantomData<&'a P>,
}

// Compile-time assertion that `BridgedPtr` is pointer-sized.
// Ensures ABI compatibility with C# (opaque GCHandle/IntPtr across FFI).
const _: [(); std::mem::size_of::<BridgedPtr<'_, (), Shared>>()] =
    [(); std::mem::size_of::<*const ()>()];

/// Casts the pointer to a pointer to `c_void`.
/// This is useful to accomodate for non-generics APIs,
/// i.e., C FFI functions that accept `*mut c_void` parameters.
impl<'a, T: Sized, P: Properties> BridgedPtr<'a, T, P> {
    pub(crate) fn cast_to_void(self) -> BridgedPtr<'a, c_void, P> {
        BridgedPtr {
            ptr: self.ptr.map(|p| p.cast()),
            _phantom: PhantomData,
        }
    }

    /// Casts the pointer to a pointer to `S`.
    /// Used to cast from `c_void` back to concrete type.
    pub(crate) unsafe fn cast<S>(self) -> BridgedPtr<'a, S, P> {
        BridgedPtr {
            ptr: self.ptr.map(|p| p.cast()),
            _phantom: PhantomData,
        }
    }
}

/// Owned shared pointer.
/// Can be used for pointers with shared ownership - e.g. pointers coming from [`Arc`] allocation.
pub type BridgedOwnedSharedPtr<T> = BridgedPtr<'static, T, Shared>;

/// Borrowed shared pointer.
/// Can be used for pointers created from some immutable reference.
pub type BridgedBorrowedSharedPtr<'a, T> = BridgedPtr<'a, T, Shared>;

/// Owned exclusive pointer.
/// Can be used for pointers with exclusive ownership - e.g. pointers coming from [`Box`] allocation.
pub type BridgedOwnedExclusivePtr<T> = BridgedPtr<'static, T, Exclusive>;

/// Borrowed exclusive pointer.
/// This can be for example obtained from mutable reborrow of some [`BridgedOwnedExclusivePtr`].
pub type BridgedBorrowedExclusivePtr<'a, T> = BridgedPtr<'a, T, Exclusive>;

/// Pointer constructors.
impl<T: Sized, P: Properties> BridgedPtr<'_, T, P> {
    pub fn null() -> Self {
        BridgedPtr {
            ptr: None,
            _phantom: PhantomData,
        }
    }

    pub fn is_null(&self) -> bool {
        self.ptr.is_none()
    }

    /// Constructs [`Bridged`] from raw pointer.
    ///
    /// ## Safety
    /// User needs to ensure that the pointer is **valid**.
    /// User is also responsible for picking correct ownership property and lifetime
    /// of the created pointer.
    unsafe fn from_raw(raw: *const T) -> Self {
        BridgedPtr {
            ptr: NonNull::new(raw as *mut T),
            _phantom: PhantomData,
        }
    }
}

/// Conversion to raw pointer.
impl<T: Sized, P: Properties> BridgedPtr<'_, T, P> {
    pub(crate) fn to_raw(&self) -> Option<*mut T> {
        self.ptr.map(|ptr| ptr.as_ptr())
    }
}

/// Constructors for to exclusive pointers.
impl<T: Sized> BridgedPtr<'_, T, Exclusive> {
    pub(crate) fn null_mut() -> Self {
        BridgedPtr {
            ptr: None,
            _phantom: PhantomData,
        }
    }
}

impl<'a, T: Sized, P: Properties> BridgedPtr<'a, T, P> {
    /// Converts a pointer to an optional valid reference.
    /// The reference inherits the lifetime of the pointer.
    fn into_ref(self) -> Option<&'a T> {
        // SAFETY: Thanks to the validity and aliasing ^ mutability guarantees,
        // we can safely convert the pointer to valid immutable reference with
        // correct lifetime.
        unsafe { self.ptr.map(|p| p.as_ref()) }
    }
}

impl<'a, T: Sized> BridgedPtr<'a, T, Exclusive> {
    /// Converts a pointer to an optional valid mutable reference.
    /// The reference inherits the lifetime of the pointer.
    pub(crate) fn into_mut_ref(self) -> Option<&'a mut T> {
        // SAFETY: Thanks to the validity and aliasing ^ mutability guarantees,
        // we can safely convert the pointer to valid mutable (and exclusive) reference with
        // correct lifetime.
        unsafe { self.ptr.map(|mut p| p.as_mut()) }
    }
}

impl<T: Sized, P: Properties> BridgedPtr<'_, T, P> {
    /// Immutably reborrows the pointer.
    /// Resulting pointer inherits the lifetime from the immutable borrow
    /// of original pointer.
    #[allow(clippy::needless_lifetimes)]
    pub fn borrow<'a>(&'a self) -> BridgedPtr<'a, T, Shared> {
        BridgedPtr {
            ptr: self.ptr,
            _phantom: PhantomData,
        }
    }
}

impl<T: Sized> BridgedPtr<'_, T, Exclusive> {
    /// Mutably reborrows the pointer.
    /// Resulting pointer inherits the lifetime from the mutable borrow
    /// of original pointer. Since the method accepts a mutable reference
    /// to the original pointer, we enforce aliasing ^ mutability principle at compile time.
    #[allow(clippy::needless_lifetimes)]
    pub fn borrow_mut<'a>(&'a mut self) -> BridgedPtr<'a, T, Exclusive> {
        BridgedPtr {
            ptr: self.ptr,
            _phantom: PhantomData,
        }
    }
}

/*
 * FFI traits and implementations - various ownership kinds
 */

mod origin_sealed {
    // This is a sealed trait - its whole purpose is to be unnameable.
    // This means we need to disable the check.
    #[expect(unnameable_types)]
    pub trait FromBoxSealed {}

    // This is a sealed trait - its whole purpose is to be unnameable.
    // This means we need to disable the check.
    #[expect(unnameable_types)]
    pub trait FromArcSealed {}

    // This is a sealed trait - its whole purpose is to be unnameable.
    // This means we need to disable the check.
    #[expect(unnameable_types)]
    pub trait FromRefSealed {}
}

/// Defines a pointer manipulation API for non-shared heap-allocated data.
///
/// Implement this trait for types that are allocated by the driver via [`Box::new`],
/// and then returned to the user as a pointer. The user is responsible for freeing
/// the memory associated with the pointer using corresponding driver's API function.
pub trait BoxFFI: Sized + origin_sealed::FromBoxSealed {
    /// Consumes the Box and returns a pointer with exclusive ownership.
    /// The pointer needs to be freed. See [`BoxFFI::free()`].
    fn into_ptr(self: Box<Self>) -> BridgedPtr<'static, Self, Exclusive> {
        #[allow(clippy::disallowed_methods)]
        let ptr = Box::into_raw(self);

        // SAFETY:
        // 1. validity guarantee - pointer is obviously valid. It comes from box allocation.
        // 2. pointer's lifetime - we choose 'static lifetime. It is ok, because holder of the
        //    pointer becomes the owner of pointee. He is responsible for freeing the memory
        //    via BoxFFI::free() - which accepts 'static pointer. User is not able to obtain
        //    another pointer with 'static lifetime pointing to the same memory.
        // 3. ownership - user becomes an exclusive owner of the pointee. Thus, it's ok
        //    for the pointer to be `Exclusive`.
        unsafe { BridgedPtr::from_raw(ptr) }
    }

    /// Consumes the pointer with exclusive ownership back to the Box.
    fn from_ptr(ptr: BridgedPtr<'static, Self, Exclusive>) -> Option<Box<Self>> {
        // SAFETY:
        // The only way to obtain an owned pointer (with 'static lifetime) is BoxFFI::into_ptr().
        // It creates a pointer based on Box allocation. It is thus safe to convert the pointer
        // back to owned `Box`.
        unsafe {
            ptr.to_raw().map(|p| {
                #[allow(clippy::disallowed_methods)]
                Box::from_raw(p)
            })
        }
    }

    /// Creates a reference from an exclusive pointer.
    /// Reference inherits the lifetime of the pointer's borrow.
    #[allow(clippy::needless_lifetimes)]
    fn as_ref<'a, O: Ownership>(ptr: BridgedPtr<'a, Self, O>) -> Option<&'a Self> {
        ptr.into_ref()
    }

    /// Creates a mutable from an exlusive pointer.
    /// Reference inherits the lifetime of the pointer's mutable borrow.
    #[allow(clippy::needless_lifetimes)]
    fn as_mut_ref<'a>(ptr: BridgedPtr<'a, Self, Exclusive>) -> Option<&'a mut Self> {
        ptr.into_mut_ref()
    }

    /// Frees the pointee.
    fn free(ptr: BridgedPtr<'static, Self, Exclusive>) {
        std::mem::drop(BoxFFI::from_ptr(ptr));
    }

    // Currently used only in tests.
    #[allow(dead_code)]
    fn null<'a>() -> BridgedPtr<'a, Self, Shared> {
        BridgedPtr::null()
    }

    fn null_mut<'a>() -> BridgedPtr<'a, Self, Exclusive> {
        BridgedPtr::null_mut()
    }
}

/// Defines a pointer manipulation API for shared heap-allocated data.
///
/// Implement this trait for types that require a shared ownership of data.
/// The data should be allocated via [`Arc::new`], and then returned to the user as a pointer.
/// The user is responsible for freeing the memory associated
/// with the pointer using corresponding driver's API function.
pub trait ArcFFI: Sized + origin_sealed::FromArcSealed {
    /// Creates a pointer from a valid reference to Arc-allocated data.
    /// Holder of the pointer borrows the pointee.
    #[allow(clippy::needless_lifetimes)]
    fn as_ptr<'a>(self: &'a Arc<Self>) -> BridgedPtr<'a, Self, Shared> {
        #[allow(clippy::disallowed_methods)]
        let ptr = Arc::as_ptr(self);

        // SAFETY:
        // 1. validity guarantee - pointer is valid, since it's obtained from Arc allocation.
        // 2. pointer's lifetime - pointer inherits the lifetime of provided Arc's borrow.
        //    What's important is that the returned pointer borrows the data, and is not the
        //    shared owner. Thus, user cannot call ArcFFI::free() on such pointer.
        // 3. ownership - we always create a `Shared` pointer.
        unsafe { BridgedPtr::from_raw(ptr) }
    }

    /// Creates a pointer from a valid Arc allocation.
    fn into_ptr(self: Arc<Self>) -> BridgedPtr<'static, Self, Shared> {
        #[allow(clippy::disallowed_methods)]
        let ptr = Arc::into_raw(self);

        // SAFETY:
        // 1. validity guarantee - pointer is valid, since it's obtained from Arc allocation
        // 2. pointer's lifetime - returned pointer has a 'static lifetime. It is a shared
        //    owner of the pointee. User has to decrement the RC of the pointer (and potentially free the memory)
        //    via ArcFFI::free().
        // 3. ownership - we always create a `Shared` pointer.
        unsafe { BridgedPtr::from_raw(ptr) }
    }

    /// Converts shared owned pointer back to owned Arc.
    fn from_ptr(ptr: BridgedPtr<'static, Self, Shared>) -> Option<Arc<Self>> {
        // SAFETY:
        // The only way to obtain a pointer with shared ownership ('static lifetime) is
        // ArcFFI::into_ptr(). It converts an owned Arc into the pointer. It is thus safe
        // to convert such pointer back to owned Arc.
        unsafe {
            ptr.to_raw().map(|p| {
                #[allow(clippy::disallowed_methods)]
                Arc::from_raw(p)
            })
        }
    }

    /// Increases the reference count of the pointer, and returns an owned Arc.
    fn cloned_from_ptr(ptr: BridgedPtr<'_, Self, Shared>) -> Option<Arc<Self>> {
        // SAFETY:
        // All pointers created via ArcFFI API are originated from Arc allocation.
        // It is thus safe to increase the reference count of the pointer, and convert
        // it to Arc. Because of the borrow-checker, it is not possible for the user
        // to provide a pointer that points to already deallocated memory.
        unsafe {
            ptr.to_raw().map(|p| {
                #[allow(clippy::disallowed_methods)]
                Arc::increment_strong_count(p);
                #[allow(clippy::disallowed_methods)]
                Arc::from_raw(p)
            })
        }
    }

    /// Converts a shared borrowed pointer to reference.
    /// The reference inherits the lifetime of pointer's borrow.
    #[allow(clippy::needless_lifetimes)]
    fn as_ref<'a>(ptr: BridgedPtr<'a, Self, Shared>) -> Option<&'a Self> {
        ptr.into_ref()
    }

    /// Decreases the reference count (and potentially frees) of the owned pointer.
    fn free(ptr: BridgedPtr<'static, Self, Shared>) {
        std::mem::drop(ArcFFI::from_ptr(ptr));
    }

    fn null<'a>() -> BridgedPtr<'a, Self, Shared> {
        BridgedPtr::null()
    }

    fn is_null(ptr: &BridgedPtr<'_, Self, Shared>) -> bool {
        ptr.is_null()
    }
}

/// Defines a pointer manipulation API for data owned by some other object.
///
/// Implement this trait for the types that do not need to be freed (directly) by the user.
/// The lifetime of the data is bound to some other object owning it.
pub trait RefFFI: Sized + origin_sealed::FromRefSealed {
    /// Creates a borrowed pointer from a valid reference.
    #[allow(clippy::needless_lifetimes)]
    fn as_ptr<'a>(&'a self) -> BridgedPtr<'a, Self, Shared> {
        // SAFETY:
        // 1. validity guarantee - pointer is valid, since it's obtained a valid reference.
        // 2. pointer's lifetime - pointer inherits the lifetime of provided reference's borrow.
        // 3. ownerhsip - we always create a `Shared` pointer.
        unsafe { BridgedPtr::from_raw(self) }
    }

    /// Creates a borrowed pointer from a weak reference.
    ///
    /// ## SAFETY
    /// User needs to ensure that the pointee is not freed when pointer is being
    /// dereferenced.
    ///
    /// ## Why this method is unsafe? - Example
    /// ```
    /// # use csharp_wrapper::ffi::{BridgedBorrowedSharedPtr, FFI, FromRef, RefFFI};
    /// # use std::sync::{Arc, Weak};
    ///
    /// struct Foo;
    /// impl FFI for Foo {
    ///     type Origin = FromRef;
    /// }
    ///
    /// let arc = Arc::new(Foo);
    /// let weak = Arc::downgrade(&arc);
    /// let ptr: BridgedBorrowedSharedPtr<Foo> = unsafe { RefFFI::weak_as_ptr(&weak) };
    /// std::mem::drop(arc);
    ///
    /// // The ptr is now dangling. The user can "safely" dereference it using RefFFI API.
    ///
    /// ```
    #[allow(clippy::needless_lifetimes)]
    unsafe fn weak_as_ptr<'a>(w: &'a Weak<Self>) -> BridgedPtr<'a, Self, Shared> {
        match w.upgrade() {
            Some(a) => {
                #[allow(clippy::disallowed_methods)]
                let ptr = Arc::as_ptr(&a);
                unsafe { BridgedPtr::from_raw(ptr) }
            }
            None => BridgedPtr::null(),
        }
    }

    /// Converts a borrowed pointer to reference.
    /// The reference inherits the lifetime of pointer's borrow.
    #[allow(clippy::needless_lifetimes)]
    fn as_ref<'a>(ptr: BridgedPtr<'a, Self, Shared>) -> Option<&'a Self> {
        ptr.into_ref()
    }

    fn null<'a>() -> BridgedPtr<'a, Self, Shared> {
        BridgedPtr::null()
    }

    fn is_null(ptr: &BridgedPtr<'_, Self, Shared>) -> bool {
        ptr.is_null()
    }
}

/// This trait should be implemented for types that are passed between
/// C and Rust API. We currently distinguish 3 kinds of implementors,
/// wrt. the origin of the pointer. The implementor should pick one of the 3 ownership
/// kinds as the associated type:
/// - [`FromBox`]
/// - [`FromArc`]
/// - [`FromRef`]
#[allow(clippy::upper_case_acronyms)]
pub trait FFI {
    type Origin;
}

/// Represents types with an exclusive ownership.
///
/// Use this associated type for implementors that require:
/// - owned exclusive pointer manipulation via [`BoxFFI`]
/// - exclusive ownership of the corresponding object
/// - potential mutability of the corresponding object
/// - manual memory freeing
///
/// C API user should be responsible for freeing associated memory manually
/// via corresponding API call.
pub struct FromBox;
impl<T> origin_sealed::FromBoxSealed for T where T: FFI<Origin = FromBox> {}
impl<T> BoxFFI for T where T: FFI<Origin = FromBox> {}

/// Represents types with a shared ownership.
///
/// Use this associated type for implementors that require:
/// - pointer with shared ownership manipulation via [`ArcFFI`]
/// - shared ownership of the corresponding object
/// - manual memory freeing
///
/// C API user should be responsible for freeing (decreasing reference count of)
/// associated memory manually via corresponding API call.
pub struct FromArc;
impl<T> origin_sealed::FromArcSealed for T where T: FFI<Origin = FromArc> {}
impl<T> ArcFFI for T where T: FFI<Origin = FromArc> {}

/// Represents borrowed types.
///
/// Use this associated type for implementors that do not require any assumptions
/// about the pointer type (apart from validity).
/// The implementation will enable [`BridgedBorrowedPtr`] manipulation via [`RefFFI`]
///
/// C API user is not responsible for freeing associated memory manually. The memory
/// should be freed automatically, when the owner is being dropped.
pub struct FromRef;
impl<T> origin_sealed::FromRefSealed for T where T: FFI<Origin = FromRef> {}
impl<T> RefFFI for T where T: FFI<Origin = FromRef> {}

pub mod blittable {
    mod blittable_sealed {
        // This is a sealed trait - its whole purpose is to be unnameable.
        // This means we need to disable the check.
        #[expect(unnameable_types)]
        pub trait Sealed {}
    }

    /// Marker trait for types that are safe to pass across FFI boundaries.
    ///
    /// Blittable types have the same representation in managed (C#) and unmanaged (Rust) code.
    /// This trait is sealed and can only be implemented for known FFI-safe types.
    ///
    /// Types that can be blittable include:
    /// - Primitive types: integers, floats
    /// - Our FFI types: `FFIStr`, `FFIBool`
    pub trait Blittable: blittable_sealed::Sealed + Sized {}

    // Implement Blittable for primitive types
    impl blittable_sealed::Sealed for u8 {}
    impl Blittable for u8 {}

    impl blittable_sealed::Sealed for u16 {}
    impl Blittable for u16 {}

    impl blittable_sealed::Sealed for u32 {}
    impl Blittable for u32 {}

    impl blittable_sealed::Sealed for u64 {}
    impl Blittable for u64 {}

    impl blittable_sealed::Sealed for i8 {}
    impl Blittable for i8 {}

    impl blittable_sealed::Sealed for i16 {}
    impl Blittable for i16 {}

    impl blittable_sealed::Sealed for i32 {}
    impl Blittable for i32 {}

    impl blittable_sealed::Sealed for i64 {}
    impl Blittable for i64 {}

    impl blittable_sealed::Sealed for f32 {}
    impl Blittable for f32 {}

    impl blittable_sealed::Sealed for f64 {}
    impl Blittable for f64 {}

    // Implement Blittable for our FFI types
    impl<'a> blittable_sealed::Sealed for super::FFIStr<'a> {}
    impl<'a> Blittable for super::FFIStr<'a> {}

    impl blittable_sealed::Sealed for super::FFIBool {}
    impl Blittable for super::FFIBool {}
}

pub use blittable::Blittable;

/// Layout descriptions for the FFI structs defined in this module. See [`crate::abi`].
#[cfg(any(feature = "integration_testing", test))]
pub(crate) mod abi {
    use super::{FFIBool, FFIGCHandle, FFIMaybeGCHandle, FFISlice, FFIStr};
    use crate::abi::{AbiType, abi_type};

    /// Stand-in for the managed payload type of the generic handle structs. Only its layout
    /// contribution matters, which is none - the handles are a pointer plus a function pointer
    /// regardless of `T`.
    enum AbiProbe {}

    pub(crate) const TYPES: &[AbiType] = &[
        abi_type!(
            "FFIGCHandle",
            FFIGCHandle<AbiProbe>,
            "gchandle" => gchandle,
            "free" => free,
        ),
        abi_type!(
            "FFIMaybeGCHandle",
            FFIMaybeGCHandle<AbiProbe>,
            "gchandle" => gchandle,
            "free" => free,
        ),
        abi_type!(
            "FFISlice",
            FFISlice<'static, u8>,
            "ptr" => ptr,
            "len" => len,
        ),
        // C# flattens this newtype into a (ptr, len) struct called FFIString, so describe it the
        // way the managed side declares it rather than as the single `slice` field Rust sees.
        abi_type!(
            "FFIString",
            FFIStr<'static>,
            "ptr" => slice.ptr,
            "len" => slice.len,
        ),
        abi_type!("FFIBool", FFIBool, "value" => value),
    ];
}

mod tests {
    /// ```compile_fail,E0499
    /// # use csharp_wrapper::ffi::{BridgedOwnedExclusivePtr, BridgedBorrowedExclusivePtr, FFI, BoxFFI, FromBox};
    /// struct Foo;
    /// impl FFI for Foo {
    ///     type Origin = FromBox;
    /// }
    ///
    /// let mut ptr: BridgedOwnedExclusivePtr<Foo> = BoxFFI::into_ptr(Box::new(Foo));
    /// let borrowed_mut_ptr1: BridgedBorrowedExclusivePtr<Foo> = ptr.borrow_mut();
    /// let borrowed_mut_ptr2: BridgedBorrowedExclusivePtr<Foo> = ptr.borrow_mut();
    /// let mutref1 = BoxFFI::as_mut_ref(borrowed_mut_ptr2);
    /// let mutref2 = BoxFFI::as_mut_ref(borrowed_mut_ptr1);
    /// ```
    fn _test_box_ffi_cannot_have_two_mutable_references() {}

    /// ```compile_fail,E0502
    /// # use csharp_wrapper::ffi::{BridgedOwnedExclusivePtr, BridgedBorrowedSharedPtr, BridgedBorrowedExclusivePtr, FFI, BoxFFI, FromBox};
    /// struct Foo;
    /// impl FFI for Foo {
    ///     type Origin = FromBox;
    /// }
    ///
    /// let mut ptr: BridgedOwnedExclusivePtr<Fo> = BoxFFI::into_ptr(Box::new(Foo));
    /// let borrowed_mut_ptr: BridgedBorrowedExclusivePtr<Foo> = ptr.borrow_mut();
    /// let borrowed_ptr: BridgedBorrowedSharedPtr<Foo> = ptr.borrow();
    /// let immref = BoxFFI::as_ref(borrowed_ptr);
    /// let mutref = BoxFFI::as_mut_ref(borrowed_mut_ptr);
    /// ```
    fn _test_box_ffi_cannot_have_mutable_and_immutable_references_at_the_same_time() {}

    /// ```compile_fail,E0505
    /// # use csharp_wrapper::ffi::{BridgedOwnedExclusivePtr, BridgedBorrowedSharedPtr, FFI, BoxFFI, FromBox};
    /// struct Foo;
    /// impl FFI for Foo {
    ///     type Origin = FromBox;
    /// }
    ///
    /// let ptr: BridgedOwnedExclusivePtr<Foo> = BoxFFI::into_ptr(Box::new(Foo));
    /// let borrowed_ptr: BridgedBorrowedSharedPtr<Foo> = ptr.borrow();
    /// BoxFFI::free(ptr);
    /// let immref = BoxFFI::as_ref(borrowed_ptr);
    /// ```
    fn _test_box_ffi_cannot_free_while_having_borrowed_pointer() {}

    /// ```compile_fail,E0505
    /// # use csharp_wrapper::ffi::{BridgedOwnedSharedPtr, BridgedBorrowedSharedPtr, FFI, ArcFFI, FromArc};
    /// # use std::sync::Arc;
    /// struct Foo;
    /// impl FFI for Foo {
    ///     type Origin = FromArc;
    /// }
    ///
    /// let ptr: BridgedOwnedSharedPtr<Foo> = ArcFFI::into_ptr(Arc::new(Foo));
    /// let borrowed_ptr: BridgedBorrowedSharedPtr<Foo> = ptr.borrow();
    /// ArcFFI::free(ptr);
    /// let immref = ArcFFI::cloned_from_ptr(borrowed_ptr);
    /// ```
    fn _test_arc_ffi_cannot_clone_after_free() {}

    /// ```compile_fail,E0505
    /// # use csharp_wrapper::ffi::{BridgedBorrowedSharedPtr, FFI, ArcFFI, FromArc};
    /// # use std::sync::Arc;
    /// struct Foo;
    /// impl FFI for Foo {
    ///     type Origin = FromArc;
    /// }
    ///
    /// let arc = Arc::new(Foo);
    /// let borrowed_ptr: BridgedBorrowedSharedPtr<Foo> = ArcFFI::as_ptr(&arc);
    /// std::mem::drop(arc);
    /// let immref = ArcFFI::cloned_from_ptr(borrowed_ptr);
    /// ```
    fn _test_arc_ffi_cannot_dereference_borrowed_after_drop() {}

    /// ```compile_fail,E0597
    /// # use csharp_wrapper::ffi::FFISlice;
    /// let ffi_slice = {
    ///     let slice = vec![1u32, 2, 3];
    ///     FFISlice::new(&slice)
    /// };
    /// assert_eq!(ffi_slice.as_slice(), &[1u32, 2, 3]);
    /// ```
    fn _test_ffi_slice_cannot_outlive_borrowed_data() {}
}

/*
 * Compound FFI types with length - slices and strings.
 */

/// Represents a slice passed over FFI from Rust to C#.
/// SAFETY: `ptr` must be a valid pointer to an array of length `len`.
#[repr(C)]
pub struct FFISlice<'a, T: Sized + Blittable> {
    ptr: BridgedBorrowedSharedPtr<'a, T>,
    len: usize,
}

impl<'a, T: Sized + Blittable> FFISlice<'a, T> {
    pub fn new(slice: &'a [T]) -> Self {
        let ptr = unsafe {
            // SAFETY: slice.as_ptr() returns a valid pointer to a slice.
            // Lifetime 'a is bound to the input slice reference, ensuring the
            // returned FFISlice cannot outlive the data it points to.
            BridgedBorrowedSharedPtr::from_raw(slice.as_ptr())
        };
        FFISlice {
            ptr,
            len: slice.len(),
        }
    }

    pub fn as_slice(&self) -> &[T] {
        if self.len == 0 {
            return &[];
        }

        unsafe {
            std::slice::from_raw_parts(
                self.ptr.ptr.expect("non-null slice pointer").as_ptr(),
                self.len,
            )
        }
    }
}

// Compile-time assertions for size and alignment of `FFISlice` to ensure it matches the expected layout.
// Ensures ABI compatibility with C#'s representation i.e. (*const u8, usize) for FFISlice<'static, u8>.
const _: [(); std::mem::size_of::<FFISlice<'static, u8>>()] =
    [(); std::mem::size_of::<(*const u8, usize)>()];
const _: [(); std::mem::align_of::<FFISlice<'static, u8>>()] =
    [(); std::mem::align_of::<(*const u8, usize)>()];

pub(crate) enum IpOctets {
    V4([u8; 4]),
    V6([u8; 16]),
}

impl IpOctets {
    pub(crate) fn new(ip: std::net::IpAddr) -> Self {
        match ip {
            std::net::IpAddr::V4(v4) => IpOctets::V4(v4.octets()),
            std::net::IpAddr::V6(v6) => IpOctets::V6(v6.octets()),
        }
    }

    pub(crate) fn as_slice(&self) -> &[u8] {
        match self {
            IpOctets::V4(bytes) => bytes,
            IpOctets::V6(bytes) => bytes,
        }
    }
}

/// Represents a string passed over FFI from Rust to C#.
/// SAFETY: `slice` must be a valid pointer a UTF-8 encoded string with correctly set length.
#[repr(transparent)]
pub struct FFIStr<'a> {
    slice: FFISlice<'a, u8>,
}

impl<'a> FFIStr<'a> {
    pub(crate) fn new(s: &'a str) -> Self {
        Self {
            slice: FFISlice::new(s.as_bytes()),
        }
    }

    pub(crate) fn null() -> Self {
        Self {
            slice: FFISlice {
                ptr: BridgedBorrowedSharedPtr::null(),
                len: 0,
            },
        }
    }

    /// Views the underlying UTF-8 bytes.
    ///
    /// `FFIStr` is normally a write-only, Rust-to-C# type, so this exists for the few places that
    /// receive one coming the other way (currently only the test exports, which echo managed strings
    /// back). Note that this returns the bytes, not a `&str`: validating UTF-8 is the caller's
    /// decision, since a malformed string arriving from C# is a contract violation rather than
    /// something to silently paper over.
    #[cfg(any(feature = "integration_testing", test))]
    pub(crate) fn as_bytes(&self) -> &[u8] {
        self.slice.as_slice()
    }
}

// Compile-time assertions for size and alignment of `FFIStr` to ensure it matches the expected layout.
// Ensures ABI compatibility with C#'s representation i.e. (*const u8, usize).
const _: [(); std::mem::size_of::<FFIStr<'static>>()] =
    [(); std::mem::size_of::<(*const u8, usize)>()];
const _: [(); std::mem::align_of::<FFIStr<'static>>()] =
    [(); std::mem::align_of::<(*const u8, usize)>()];

/// Represents a boolean passed over FFI between Rust and C#.
/// Uses u8 representation to match C#'s byte.
/// SAFETY: Only 0 (false) and 1 (true) are valid values.
#[repr(transparent)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FFIBool {
    value: u8,
}

impl From<bool> for FFIBool {
    fn from(value: bool) -> Self {
        Self {
            value: if value { 1 } else { 0 },
        }
    }
}

impl From<FFIBool> for bool {
    fn from(value: FFIBool) -> Self {
        value.value != 0
    }
}

// Compile-time assertions for size and alignment of `FFIBool` to ensure it matches u8.
// Ensures ABI compatibility with C#'s byte representation of bool.
const _: [(); std::mem::size_of::<FFIBool>()] = [(); std::mem::size_of::<u8>()];
const _: [(); std::mem::align_of::<FFIBool>()] = [(); std::mem::align_of::<u8>()];

/// Represents a non-null pointer to C#-allocated data.
#[repr(transparent)]
pub struct FFINonNullPtr<'a, T: Sized> {
    ptr: NonNull<T>,
    _phantom: PhantomData<&'a ()>,
}

impl<'a, T> FFINonNullPtr<'a, T> {
    pub(crate) fn from_ref(value: &'a T) -> Self {
        Self {
            ptr: NonNull::from(value),
            _phantom: PhantomData,
        }
    }
}

// Manual implementation to avoid `T: Clone` bound.
impl<T> Clone for FFINonNullPtr<'_, T> {
    fn clone(&self) -> Self {
        *self
    }
}

// Manual implementation to avoid `T: Copy` bound.
impl<T> Copy for FFINonNullPtr<'_, T> {}

impl<'a, T> Debug for FFINonNullPtr<'a, T> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "FFINonNullPtr({:p})", self.ptr)
    }
}

// Compile-time assertion that `FFINonNullPtr` is pointer-sized.
// Ensures ABI compatibility with C# (opaque GCHandle/IntPtr across FFI).
const _: [(); std::mem::size_of::<FFINonNullPtr<'_, ()>>()] =
    [(); std::mem::size_of::<*const ()>()];

/// Represents a nullable pointer to C#-allocated data.
#[repr(transparent)]
pub struct FFIPtr<'a, T: Sized> {
    ptr: Option<FFINonNullPtr<'a, T>>,
}

// Manual implementation to avoid `T: Clone` bound.
impl<T> Clone for FFIPtr<'_, T> {
    fn clone(&self) -> Self {
        *self
    }
}

// Manual implementation to avoid `T: Copy` bound.
impl<T> Copy for FFIPtr<'_, T> {}

impl<'a, T> Debug for FFIPtr<'a, T> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let ptr = self
            .ptr
            .map(|nn| nn.ptr.as_ptr())
            .unwrap_or(std::ptr::null::<T>() as *mut T);
        write!(f, "FFIPtr({:p})", ptr)
    }
}

impl<'a, T: Sized> FFIPtr<'a, T> {
    pub(crate) fn as_non_null(&self) -> Option<std::ptr::NonNull<T>> {
        self.ptr.map(|nn| nn.ptr)
    }
}

// Compile-time assertion that `FFIPtr` is pointer-sized.
// Ensures ABI compatibility with C# (opaque GCHandle/IntPtr across FFI).
const _: [(); std::mem::size_of::<FFIPtr<'_, ()>>()] = [(); std::mem::size_of::<*const ()>()];

pub(crate) type CSharpStr<'a> = FFIPtr<'a, c_char>;
impl<'a> CSharpStr<'a> {
    pub(crate) fn as_cstr(&self) -> Option<&'a CStr> {
        self.ptr
            .map(|nn| unsafe { CStr::from_ptr(nn.ptr.as_ptr()) })
    }
}

enum CSharpManagedString {}

#[derive(Clone, Copy)]
#[repr(transparent)]
pub(crate) struct CSharpManagedStringPtr(FFIPtr<'static, CSharpManagedString>);

pub(crate) type WriteStringCallback =
    extern "C" fn(FFIStr<'_>, CSharpManagedStringPtr) -> FFIMaybeException;

/// Feeds each item from an iterator to a C FFI callback, one at a time.
///
/// This avoids materializing the full iterator into a `Vec`/`FFISlice`.
/// The callback is invoked once per item with the context pointer and the item.
///
/// # Safety
/// - `callback` must be a valid function pointer with C calling convention
/// - `context` must remain valid for the duration of iteration
pub(crate) unsafe fn ffi_callback_for_each<Ctx: Copy, T>(
    context: Ctx,
    callback: unsafe extern "C" fn(Ctx, T) -> FFIMaybeException,
    iter: impl Iterator<Item = T>,
) -> FFIMaybeException {
    for item in iter {
        unsafe {
            let res = callback(context, item);
            if res.has_exception() {
                return res;
            }
        }
    }
    FFIMaybeException::ok()
}

#[repr(transparent)]
pub(crate) struct GCHandlePtr<'a, T>(FFINonNullPtr<'a, T>);

impl<'a, T> Clone for GCHandlePtr<'a, T> {
    fn clone(&self) -> Self {
        *self
    }
}
impl<'a, T> Copy for GCHandlePtr<'a, T> {}

impl<'a, T> Debug for GCHandlePtr<'a, T> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        self.0.fmt(f)
    }
}

unsafe impl<T> Send for GCHandlePtr<'_, T> {}

// Compile-time assertion that `GCHandlePtr` is pointer-sized.
// Ensures ABI compatibility with C#.
const _: [(); std::mem::size_of::<GCHandlePtr<'_, ()>>()] = [(); std::mem::size_of::<*const ()>()];

/// A pointer to GCHandle owned by Rust, together with the destructor.
/// This is useful to ensure that GCHandle is freed when Rust-side
/// object is dropped. Mainly employable in async scenarios.
#[repr(C)]
pub struct FFIGCHandle<T> {
    gchandle: GCHandlePtr<'static, T>,
    free: unsafe extern "C" fn(GCHandlePtr<T>),
}

impl<T> FFIGCHandle<T> {
    /// Borrows the GCHandle, for use by C#.
    /// Borrow checker prevents use-after-free, ensuring that FFIGCHandle
    /// is kept alive.
    pub(crate) fn borrow<'gc>(&'gc self) -> GCHandlePtr<'gc, T> {
        GCHandlePtr(self.gchandle.0)
    }

    pub(crate) fn into_ffi_maybe_gc_handle(self) -> FFIMaybeGCHandle<T> {
        // We perform a move wrt ownership: MaybeRustFreeableHandle now owns the gchandle, not we.
        let ret = FFIMaybeGCHandle {
            gchandle: Some(GCHandlePtr(self.gchandle.0)),
            free: Some(self.free),
        };

        // This is crucial: we must prevent freeing the GCHandle here.
        std::mem::forget(self);

        ret
    }
}

impl<T> Debug for FFIGCHandle<T> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FFIGCHandle")
            .field("gchandle", &self.gchandle)
            .field("free", &self.free)
            .finish()
    }
}

impl<T> Drop for FFIGCHandle<T> {
    fn drop(&mut self) {
        unsafe {
            // SAFETY: free function is provided by the user of the struct.
            // The GCHandlePtr is "cloned" manually here, because it purposely does not
            // implement Clone to avoid accidental double-free, as well as accidental UAF.
            (self.free)(GCHandlePtr(self.gchandle.0));
        }
    }
}

/// An **optional** pointer to GCHandle owned by Rust, together with the destructor.
/// This is useful to ensure that GCHandle is freed when Rust-side
/// object is dropped. Mainly employable in async scenarios.
#[repr(C)]
pub struct FFIMaybeGCHandle<T> {
    gchandle: Option<GCHandlePtr<'static, T>>,
    free: Option<unsafe extern "C" fn(GCHandlePtr<T>)>,
}

impl<T> FFIMaybeGCHandle<T> {
    pub(crate) fn empty() -> Self {
        Self {
            gchandle: None,
            free: None,
        }
    }

    pub(crate) fn is_empty(&self) -> bool {
        self.gchandle.is_none()
    }

    /// Borrows the GCHandle, for use by C#.
    /// Borrow checker prevents use-after-free, ensuring that FFIGCHandle
    /// is kept alive.
    // Exercised by the runtime FFI tests; still unused in the production (non-test) build.
    #[cfg_attr(not(test), expect(dead_code))] // Will be used soon.
    pub(crate) fn borrow<'gc>(&'gc self) -> Option<GCHandlePtr<'gc, T>> {
        self.gchandle
            .as_ref()
            .map(|&GCHandlePtr(ptr)| GCHandlePtr(ptr))
    }

    pub(crate) fn try_into_ffi_gc_handle(self) -> Option<FFIGCHandle<T>> {
        // We perform a move wrt ownership: RustFreeableHandle now owns the gchandle, not we.
        let (Some(gchandle), Some(free)) = (&self.gchandle, self.free) else {
            return None;
        };

        let ret = FFIGCHandle {
            gchandle: GCHandlePtr(gchandle.0),
            free,
        };

        // This is crucial: we must prevent freeing the GCHandle here.
        std::mem::forget(self);

        Some(ret)
    }
}

impl<T> Debug for FFIMaybeGCHandle<T> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FFIMaybeGCHandle")
            .field("gchandle", &self.gchandle)
            .field("free", &self.free)
            .finish()
    }
}

impl<T> Drop for FFIMaybeGCHandle<T> {
    fn drop(&mut self) {
        if let (Some(gchandle), Some(free)) = (&self.gchandle, self.free) {
            // SAFETY: free function is provided by the user of the struct.
            // The GCHandlePtr is "cloned" manually here, because it purposely does not
            // implement Clone to avoid accidental double-free, as well as accidental UAF.
            unsafe {
                (free)(GCHandlePtr(gchandle.0));
            }
        }
    }
}

// Runtime unit tests for the FFI abstractions.
//
// These complement the `compile_fail` doctests above (which prove the borrow-checker invariants) by
// asserting the *runtime* behaviour the type system cannot: that ownership transfers free memory
// exactly once, that reference counts move as intended, and that the handle wrappers call their
// destructor callback precisely when they should.
//
// Deliberately absent are assertions that `Box::into_raw` / `Arc::as_ptr` / `slice::as_ptr` return
// the address they were handed. That is std's contract, not this module's, and testing it only
// creates edits to make whenever the wrappers are refactored. Where an address does matter it is
// asserted as part of a behavioural test (see `arc_ffi_cloned_from_ptr_shares_the_allocation`).
//
// Run under ASAN (`make test-rust-asan`) these additionally prove there is no leak, no double-free
// and no use-after-free on any of these paths.
#[cfg(test)]
mod runtime_tests {
    use super::*;
    use std::cell::Cell;
    use std::sync::atomic::{AtomicUsize, Ordering};

    // A trivial Box-backed FFI type.
    struct BoxThing {
        value: u64,
    }
    impl FFI for BoxThing {
        type Origin = FromBox;
    }

    // A trivial Arc-backed FFI type.
    struct ArcThing {
        value: u64,
    }
    impl FFI for ArcThing {
        type Origin = FromArc;
    }

    // A Ref-backed FFI type (lifetime bound to an owner).
    struct RefThing {
        value: u64,
    }
    impl FFI for RefThing {
        type Origin = FromRef;
    }

    // Counts drops so we can assert a value is freed exactly once (no leak, no double-free).
    struct DropCounter<'a>(&'a AtomicUsize);
    impl FFI for DropCounter<'_> {
        type Origin = FromBox;
    }
    impl Drop for DropCounter<'_> {
        fn drop(&mut self) {
            self.0.fetch_add(1, Ordering::SeqCst);
        }
    }

    #[test]
    fn box_ffi_round_trip_preserves_the_payload() {
        let ptr = BoxFFI::into_ptr(Box::new(BoxThing { value: 7 }));
        let reclaimed = BoxFFI::from_ptr(ptr).expect("non-null");
        assert_eq!(reclaimed.value, 7);
    }

    #[test]
    fn box_ffi_mutation_through_exclusive_borrow_is_visible() {
        let mut ptr = BoxFFI::into_ptr(Box::new(BoxThing { value: 1 }));

        // Mutate through a borrowed exclusive pointer...
        BoxFFI::as_mut_ref(ptr.borrow_mut())
            .expect("non-null")
            .value = 99;

        // ...and observe it through an immutable reborrow of the same pointer.
        assert_eq!(BoxFFI::as_ref(ptr.borrow()).map(|r| r.value), Some(99));

        BoxFFI::free(ptr);
    }

    #[test]
    fn box_ffi_free_drops_exactly_once() {
        let counter = AtomicUsize::new(0);
        let ptr = BoxFFI::into_ptr(Box::new(DropCounter(&counter)));
        assert_eq!(
            counter.load(Ordering::SeqCst),
            0,
            "must not drop while owned as a pointer"
        );

        BoxFFI::free(ptr);
        assert_eq!(
            counter.load(Ordering::SeqCst),
            1,
            "free must drop exactly once"
        );
    }

    #[test]
    fn arc_ffi_round_trip_preserves_the_payload_and_refcount() {
        let ptr = ArcFFI::into_ptr(Arc::new(ArcThing { value: 5 }));
        let reclaimed = ArcFFI::from_ptr(ptr).expect("non-null");
        assert_eq!(reclaimed.value, 5);
        assert_eq!(
            Arc::strong_count(&reclaimed),
            1,
            "into_ptr/from_ptr must move the single owning reference, not add one"
        );
    }

    #[test]
    fn arc_ffi_as_ptr_only_borrows() {
        let arc = Arc::new(ArcThing { value: 8 });
        let _ptr = ArcFFI::as_ptr(&arc);
        assert_eq!(
            Arc::strong_count(&arc),
            1,
            "as_ptr must not take an owning reference"
        );
    }

    #[test]
    fn arc_ffi_cloned_from_ptr_shares_the_allocation() {
        let arc = Arc::new(ArcThing { value: 3 });
        let borrowed = ArcFFI::as_ptr(&arc);

        let cloned = ArcFFI::cloned_from_ptr(borrowed).expect("non-null");
        assert_eq!(
            Arc::strong_count(&arc),
            2,
            "cloned_from_ptr must bump the refcount"
        );
        // Same allocation, not a copy: this is the property C# relies on when it clones a handle.
        assert!(std::ptr::eq(Arc::as_ptr(&cloned), Arc::as_ptr(&arc)));
        assert_eq!(cloned.value, 3);

        drop(cloned);
        assert_eq!(Arc::strong_count(&arc), 1);
    }

    #[test]
    fn arc_ffi_free_decrements_strong_count() {
        let arc = Arc::new(ArcThing { value: 2 });
        // Simulate handing an owned pointer to C# (into_ptr consumes one owning reference).
        let owned_ptr = ArcFFI::into_ptr(Arc::clone(&arc));
        assert_eq!(Arc::strong_count(&arc), 2);

        ArcFFI::free(owned_ptr);
        assert_eq!(
            Arc::strong_count(&arc),
            1,
            "free must drop one owning reference"
        );
    }

    #[test]
    fn ref_ffi_as_ptr_yields_a_usable_reference() {
        let thing = RefThing { value: 11 };
        let ptr = RefFFI::as_ptr(&thing);
        assert_eq!(RefFFI::as_ref(ptr).map(|r| r.value), Some(11));
    }

    #[test]
    fn ref_ffi_weak_as_ptr_is_null_after_owner_dropped() {
        let arc = Arc::new(RefThing { value: 4 });
        let weak = Arc::downgrade(&arc);

        // While the owner is alive, the pointer is non-null.
        // SAFETY: `arc` is alive for the duration of this borrow.
        let live = unsafe { RefFFI::weak_as_ptr(&weak) };
        assert!(!live.is_null());

        drop(arc);
        // Once the owner is gone, weak_as_ptr yields null instead of a dangling pointer.
        // SAFETY: no dereference occurs; we only inspect nullness.
        let dead = unsafe { RefFFI::weak_as_ptr(&weak) };
        assert!(dead.is_null());
    }

    #[test]
    fn ffi_slice_is_zero_copy() {
        let data = [10u32, 20, 30, 40];
        let slice = FFISlice::new(&data);
        let out = slice.as_slice();
        // Same backing storage - C#'s `ToSpan()` aliases Rust memory, and that only holds if this does.
        assert!(std::ptr::eq(out.as_ptr(), data.as_ptr()));
        assert_eq!(out, &data);
    }

    #[test]
    fn ffi_slice_empty_is_empty() {
        let data: [u8; 0] = [];
        assert_eq!(FFISlice::new(&data).as_slice(), &[] as &[u8]);
    }

    #[test]
    fn ffi_str_is_byte_accurate() {
        // Multi-byte, plus an interior NUL: a length-honouring bridge keeps every byte.
        let s = "h\u{e9}llo\0world";
        assert_eq!(FFIStr::new(s).slice.as_slice(), s.as_bytes());
    }

    #[test]
    fn ffi_str_null_is_distinct_from_empty() {
        let null = FFIStr::null();
        assert!(null.slice.ptr.is_null());

        // An empty string still has a non-null pointer, so C# can tell the two apart.
        assert!(!FFIStr::new("").slice.ptr.is_null());
    }

    #[test]
    fn ffi_bool_uses_exact_wire_bytes() {
        // C# declares this as a single `byte`, so the values 0 and 1 are part of the ABI.
        assert_eq!(FFIBool::from(true).value, 1);
        assert_eq!(FFIBool::from(false).value, 0);
        assert!(bool::from(FFIBool::from(true)));
        assert!(!bool::from(FFIBool::from(false)));
    }

    // The GCHandle wrappers take a bare `extern "C" fn(GCHandlePtr<T>)` with no context parameter,
    // so a test callback has nowhere to put per-test state except a thread-local. That is also what
    // makes these tests safe to run in parallel: cargo gives each test its own thread, whereas the
    // process-global counters this replaced raced with each other (~3 failures in 40 runs).
    thread_local! {
        static FREE_CALLS: Cell<usize> = const { Cell::new(0) };
        static LAST_FREED_ADDR: Cell<usize> = const { Cell::new(0) };
    }

    extern "C" fn record_free<T>(handle: GCHandlePtr<T>) {
        FREE_CALLS.set(FREE_CALLS.get() + 1);
        LAST_FREED_ADDR.set(handle.0.ptr.as_ptr() as usize);
    }

    fn free_calls() -> usize {
        FREE_CALLS.get()
    }

    // Builds a GCHandlePtr from a `'static` anchor. In production the pointee is a C# GCHandle; any
    // stable non-null address works here because `record_free` never dereferences it. Callers pass a
    // `static`, which yields a genuine `'static` borrow - no lifetime transmute, and nothing leaked
    // for ASAN to report.
    fn make_gchandle_ptr<T: 'static>(anchor: &'static T) -> GCHandlePtr<'static, T> {
        GCHandlePtr(FFINonNullPtr::from_ref(anchor))
    }

    #[test]
    fn ffi_gc_handle_drop_calls_free_once_with_stored_address() {
        static ANCHOR: u8 = 0;
        let expected_addr = &ANCHOR as *const u8 as usize;

        {
            let handle = FFIGCHandle {
                gchandle: make_gchandle_ptr(&ANCHOR),
                free: record_free::<u8>,
            };
            // Borrowing must not free.
            let _ = handle.borrow();
            assert_eq!(free_calls(), 0);
        } // drop here

        assert_eq!(free_calls(), 1, "Drop must free exactly once");
        assert_eq!(LAST_FREED_ADDR.get(), expected_addr);
    }

    // The single most load-bearing test in this module: both conversions `std::mem::forget` the
    // source so ownership of the GCHandle moves rather than being duplicated. Drop either `forget`
    // and this turns into a double-free (or, with the ownership reversed, a leak) - which nothing
    // else here would notice.
    #[test]
    fn ffi_gc_handle_into_maybe_and_back_frees_once() {
        static ANCHOR: u16 = 0;

        let handle = FFIGCHandle {
            gchandle: make_gchandle_ptr(&ANCHOR),
            free: record_free::<u16>,
        };

        let maybe = handle.into_ffi_maybe_gc_handle();
        assert_eq!(free_calls(), 0, "ownership transfer must not free");
        assert!(!maybe.is_empty());

        let back = maybe.try_into_ffi_gc_handle().expect("was non-empty");
        assert_eq!(free_calls(), 0, "converting back must not free either");

        drop(back);
        assert_eq!(
            free_calls(),
            1,
            "exactly one free after the full round trip"
        );
    }

    #[test]
    fn ffi_maybe_gc_handle_empty_does_not_free() {
        let empty: FFIMaybeGCHandle<u8> = FFIMaybeGCHandle::empty();
        assert!(empty.is_empty());
        assert!(empty.borrow().is_none());
        assert!(empty.try_into_ffi_gc_handle().is_none());
        assert_eq!(free_calls(), 0, "empty handle must never call free");
    }

    #[test]
    fn ffi_maybe_gc_handle_drop_frees_once() {
        static ANCHOR: u32 = 0;
        {
            let _maybe = FFIMaybeGCHandle {
                gchandle: Some(make_gchandle_ptr(&ANCHOR)),
                free: Some(record_free::<u32>),
            };
        }
        assert_eq!(free_calls(), 1);
    }
}

// Positive controls for the sanitizer build.
//
// A green AddressSanitizer run is only meaningful if the sanitizer was actually armed - and it is
// easy for it not to be, since it depends on RUSTFLAGS reaching the right compilation units. These
// two tests contain real defects: if `make test-rust-asan-selftest` does not see ASAN report them,
// the sanitizer is not instrumenting this crate and every clean run should be distrusted.
//
// They are `#[ignore]`d so that an ordinary `cargo test` never executes them. Outside a sanitizer
// build the first one is plain undefined behaviour, so the make target is the only correct way to
// run them.
#[cfg(test)]
mod asan_selftest {
    #[test]
    #[ignore = "deliberate use-after-free; run only under ASAN via `make test-rust-asan-selftest`"]
    fn deliberate_use_after_free() {
        let raw = Box::into_raw(Box::new(0xAAu8));
        // SAFETY: none. That is the point - ASAN must abort on the read below.
        unsafe {
            drop(Box::from_raw(raw));
            let value = std::ptr::read_volatile(raw);
            println!("read {value} after free - ASAN is NOT armed");
        }
    }

    #[test]
    #[ignore = "deliberate leak; run only under ASAN via `make test-rust-asan-selftest`"]
    fn deliberate_leak() {
        let leaked = Box::leak(Box::new([0u8; 4096]));
        std::hint::black_box(leaked.as_ptr());
    }
}
