use scylla::serialize::row::{RowSerializationContext, SerializeRow};
use scylla::serialize::writers::RowWriter;
use scylla::serialize::SerializationError;
use crate::pre_serialized_values::{PreSerializedValuesTrait, HasCells};

#[derive(Debug)]
pub struct SafePreSerializedValues {
    cells: Vec<SafeCell>,
}

#[derive(Debug, Clone)]
enum SafeCell { Bytes(Vec<u8>), Null, Unset }

impl SafePreSerializedValues {
    pub fn new() -> Self { Self { cells: Vec::new() } }
}

impl PreSerializedValuesTrait for SafePreSerializedValues {
    unsafe fn add_value(&mut self, value_ptr: *const u8, value_len: usize) {
        let slice = unsafe { std::slice::from_raw_parts(value_ptr, value_len) };
        self.cells.push(SafeCell::Bytes(slice.to_vec()));
    }
    fn add_null(&mut self) { self.cells.push(SafeCell::Null); }
    fn add_unset(&mut self) { self.cells.push(SafeCell::Unset); }
    fn len(&self) -> usize { self.cells.len() }
}

impl SerializeRow for SafePreSerializedValues {
    fn serialize(&self, ctx: &RowSerializationContext<'_>, writer: &mut RowWriter) -> Result<(), SerializationError> {
        crate::pre_serialized_values::serialize_each_cell::<SafeCell, _>(
            self,
            ctx,
            writer,
            |cw, cell| match cell {
                SafeCell::Bytes(b) => cw.set_value(b).map(|_proof| ()).map_err(SerializationError::new),
                SafeCell::Null => { let _ = cw.set_null(); Ok(()) },
                SafeCell::Unset => { let _ = cw.set_unset(); Ok(()) },
            },
        )
    }
    fn is_empty(&self) -> bool { self.cells.is_empty() }
}

impl HasCells<SafeCell> for SafePreSerializedValues {
    fn get_cells(&self) -> &Vec<SafeCell> {
        &self.cells
    }
}
