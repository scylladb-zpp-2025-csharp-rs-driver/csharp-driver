use scylla::serialize::row::{RowSerializationContext, SerializeRow};
use scylla::serialize::writers::RowWriter;
use scylla::serialize::SerializationError;
use crate::pre_serialized_values::{PreSerializedValuesTrait, HasCells};


#[derive(Debug)]
pub struct UnsafePreSerializedValues { cells: Vec<UnsafeCell> }

#[derive(Debug, Clone, Copy)]
struct ConstPtr(*const u8);

// Safety:
// `ConstPtr` is a transparent wrapper around a raw `*const u8`. Raw pointers do not
// implement `Send`/`Sync` by default because the compiler cannot reason about the
// lifetime, ownership, or aliasing guarantees of the pointee. We declare `ConstPtr`
// as `Send` and `Sync` here under the following contract that the caller must uphold:
//  - The pointer must point to valid, initialized memory for the entire period in
//    which Rust code may access it (including across thread boundaries and inside
//    async tasks). In practice this means the C# side must pin the backing buffer or
//    allocate it in stable unmanaged memory and ensure it is not freed or moved.
//  - The pointee must not be mutated.
//  - The pointer must live for the whole time it is used by Rust code.
// When these conditions are met, sharing the `ConstPtr` across threads is sound.
unsafe impl Send for ConstPtr {}
unsafe impl Sync for ConstPtr {}

#[derive(Debug, Clone, Copy)]
enum UnsafeCell { Bytes(ConstPtr, usize), Null, Unset }

impl UnsafePreSerializedValues {
    pub fn new() -> Self { Self { cells: Vec::new() } }
}

impl PreSerializedValuesTrait for UnsafePreSerializedValues {
    unsafe fn add_value(&mut self, value_ptr: *const u8, value_len: usize) {
        self.cells.push(UnsafeCell::Bytes(ConstPtr(value_ptr), value_len));
    }
    fn add_null(&mut self) { self.cells.push(UnsafeCell::Null); }
    fn add_unset(&mut self) { self.cells.push(UnsafeCell::Unset); }
    fn len(&self) -> usize { self.cells.len() }
}

impl SerializeRow for UnsafePreSerializedValues {
    fn serialize(&self, ctx: &RowSerializationContext<'_>, writer: &mut RowWriter) -> Result<(), SerializationError> {
        crate::pre_serialized_values::validate_number_of_columns(self.len(), ctx)?;
        crate::pre_serialized_values::serialize_each_cell::<UnsafeCell, _>(
            self,
            ctx,
            writer,
            |cw, cell| match cell {
                UnsafeCell::Bytes(ptr, len) => {
                    let slice = unsafe { std::slice::from_raw_parts(ptr.0, *len) };
                    cw.set_value(slice).map(|_proof| ()).map_err(SerializationError::new)
                }
                UnsafeCell::Null => { let _ = cw.set_null(); Ok(()) },
                UnsafeCell::Unset => { let _ = cw.set_unset(); Ok(()) },
            },
        )
    }
    fn is_empty(&self) -> bool { self.cells.is_empty() }
}

impl HasCells<UnsafeCell> for UnsafePreSerializedValues {
    fn get_cells(&self) -> &Vec<UnsafeCell> { &self.cells }
}
