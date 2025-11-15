use scylla::serialize::writers::{CellValueBuilder, CellWriter, RowWriter};
use std::ffi::{c_void};
use std::ptr;
use std::slice;

// ============================================================================
// RowWriter FFI
// ============================================================================

/// Creates a new RowWriter backed by a Vec<u8>.
/// Returns an opaque pointer to the writer.
/// The caller must free the writer using row_writer_free().
#[unsafe(no_mangle)]
pub extern "C" fn row_writer_new() -> *mut c_void {
    let buf = Box::new(Vec::<u8>::new());
    let writer = Box::new(RowWriter::new(Box::leak(buf)));
    Box::into_raw(writer) as *mut c_void
}

/// Frees a RowWriter and its backing buffer.
#[unsafe(no_mangle)]
pub extern "C" fn row_writer_free(writer: *mut c_void) {
    if writer.is_null() {
        return;
    }
    unsafe {
        let _ = Box::from_raw(writer as *mut RowWriter);
    }
}

/// Returns the number of values written to the row so far.
#[unsafe(no_mangle)]
pub extern "C" fn row_writer_value_count(writer: *const c_void) -> usize {
    if writer.is_null() {
        return 0;
    }
    unsafe {
        let writer_ref = &*(writer as *const RowWriter);
        writer_ref.value_count()
    }
}

/// Creates a new CellWriter for appending a value to the row.
/// The returned CellWriter must be consumed by one of:
/// - cell_writer_set_null
/// - cell_writer_set_unset
/// - cell_writer_set_value
/// - cell_writer_into_value_builder (followed by cell_value_builder_finish)
///
/// Returns an opaque pointer to the CellWriter.
#[unsafe(no_mangle)]
pub extern "C" fn row_writer_make_cell_writer(writer: *mut c_void) -> *mut c_void {
    if writer.is_null() {
        return ptr::null_mut();
    }
    unsafe {
        let writer_ref = &mut *(writer as *mut RowWriter);
        let cell_writer = writer_ref.make_cell_writer();
        Box::into_raw(Box::new(cell_writer)) as *mut c_void
    }
}

// ============================================================================
// CellWriter FFI
// ============================================================================

/// Sets the cell value to NULL and consumes the CellWriter.
/// Returns 1 on success, 0 on failure.
#[unsafe(no_mangle)]
pub extern "C" fn cell_writer_set_null(writer: *mut c_void) -> i32 {
    if writer.is_null() {
        return 0;
    }
    unsafe {
        let cell_writer = *Box::from_raw(writer as *mut CellWriter);
        let _proof = cell_writer.set_null();
        1
    }
}

/// Sets the cell value to UNSET and consumes the CellWriter.
/// Returns 1 on success, 0 on failure.
#[unsafe(no_mangle)]
pub extern "C" fn cell_writer_set_unset(writer: *mut c_void) -> i32 {
    if writer.is_null() {
        return 0;
    }
    unsafe {
        let cell_writer = *Box::from_raw(writer as *mut CellWriter);
        let _proof = cell_writer.set_unset();
        1
    }
}

/// Sets the cell value to the provided byte array and consumes the CellWriter.
/// Returns:
/// - 1 on success
/// - 0 if writer is null or data is null with len > 0
/// - -1 if the value size exceeds i32::MAX
#[unsafe(no_mangle)]
pub extern "C" fn cell_writer_set_value(
    writer: *mut c_void,
    data: *const u8,
    len: usize,
) -> i32 {
    if writer.is_null() {
        return 0;
    }
    if data.is_null() && len > 0 {
        return 0;
    }

    unsafe {
        let cell_writer = *Box::from_raw(writer as *mut CellWriter);
        let contents = if len == 0 {
            &[]
        } else {
            slice::from_raw_parts(data, len)
        };

        match cell_writer.set_value(contents) {
            Ok(_proof) => 1,
            Err(_) => -1, // CellOverflowError
        }
    }
}

/// Converts the CellWriter into a CellValueBuilder for gradual value construction.
/// Returns an opaque pointer to the CellValueBuilder.
/// The builder must be finished with cell_value_builder_finish().
#[unsafe(no_mangle)]
pub extern "C" fn cell_writer_into_value_builder(writer: *mut c_void) -> *mut c_void {
    if writer.is_null() {
        return ptr::null_mut();
    }
    unsafe {
        let cell_writer = *Box::from_raw(writer as *mut CellWriter);
        let builder = cell_writer.into_value_builder();
        Box::into_raw(Box::new(builder)) as *mut c_void
    }
}

// ============================================================================
// CellValueBuilder FFI
// ============================================================================

/// Appends data to the cell value being built.
/// Returns:
/// - 1 on success
/// - 0 if builder or data is null
/// - -1 if the total size would exceed i32::MAX
#[unsafe(no_mangle)]
pub extern "C" fn cell_value_builder_append(
    builder: *mut c_void,
    data: *const u8,
    len: usize,
) -> i32 {
    if builder.is_null() {
        return 0;
    }
    if data.is_null() && len > 0 {
        return 0;
    }

    unsafe {
        let builder_ref = &mut *(builder as *mut CellValueBuilder);
        let contents = if len == 0 {
            &[]
        } else {
            slice::from_raw_parts(data, len)
        };

        builder_ref.append_bytes(contents);
        1
    }
}

/// Finishes building the cell value and consumes the CellValueBuilder.
/// Returns 1 on success, 0 on failure.
#[unsafe(no_mangle)]
pub extern "C" fn cell_value_builder_finish(builder: *mut c_void) -> i32 {
    if builder.is_null() {
        return 0;
    }
    unsafe {
        let cell_builder = *Box::from_raw(builder as *mut CellValueBuilder);
        let _proof = cell_builder.finish();
        1
    }
}


// ============================================================================
// Helper for buffer management
// ============================================================================

/// A helper structure to manage the serialized row data.
/// This owns the Vec<u8> buffer and tracks the leaked Vec pointer.
#[repr(C)]
pub struct SerializedRow {
    data: *mut u8,
    len: usize,
    capacity: usize,
    leaked_vec: *mut Vec<u8>, // Track the leaked Vec so we can query its length later
}

/// Creates a new row buffer for serialization.
/// Returns a pointer to SerializedRow which owns the buffer.
#[unsafe(no_mangle)]
pub extern "C" fn serialized_row_new() -> *mut SerializedRow {
    let vec = Vec::<u8>::new();
    let serialized = SerializedRow {
        data: vec.as_ptr() as *mut u8,
        len: vec.len(),
        capacity: vec.capacity(),
        leaked_vec: ptr::null_mut(), // No Vec leaked yet
    };
    std::mem::forget(vec); // Prevent Vec from being dropped
    Box::into_raw(Box::new(serialized))
}

/// Gets a RowWriter for the SerializedRow.
/// The RowWriter borrows the buffer mutably.
/// Note: This function leaks the Vec buffer to ensure it has a 'static lifetime.
#[unsafe(no_mangle)]
pub extern "C" fn serialized_row_get_writer(row: *mut SerializedRow) -> *mut c_void {
    if row.is_null() {
        return ptr::null_mut();
    }
    
    unsafe {
        let row_ref = &mut *row;
        
        // Reconstruct the Vec from raw parts
        let vec = Vec::from_raw_parts(row_ref.data, row_ref.len, row_ref.capacity);
        
        // We need to leak the vec because RowWriter needs a 'static reference
        let boxed_vec = Box::new(vec);
        let vec_ptr = Box::into_raw(boxed_vec); // Store the Box pointer for later access
        let static_vec = Box::leak(Box::from_raw(vec_ptr));
        
        // Store the vec info before creating the writer
        let data_ptr = static_vec.as_mut_ptr();
        let len = static_vec.len();
        let capacity = static_vec.capacity();
        
        // Create a RowWriter
        let writer = RowWriter::new(static_vec);
        
        // Update the SerializedRow to reflect the current state and store the Vec pointer
        row_ref.data = data_ptr;
        row_ref.len = len;
        row_ref.capacity = capacity;
        row_ref.leaked_vec = vec_ptr;
        
        Box::into_raw(Box::new(writer)) as *mut c_void
    }
}

/// Gets the data pointer and length from a SerializedRow.
/// The data is valid until serialized_row_free is called.
/// 
/// This function accesses the leaked Vec to get the updated length after
/// RowWriter has modified it.
#[unsafe(no_mangle)]
pub extern "C" fn serialized_row_get_data(
    row: *const SerializedRow,
    data_ptr: *mut *const u8,
    len: *mut usize,
) -> i32 {
    if row.is_null() || data_ptr.is_null() || len.is_null() {
        return 0;
    }
    
    unsafe {
        let row_ref = &*row;
        
        // If we have a leaked Vec, use it to get the current length
        if !row_ref.leaked_vec.is_null() {
            let vec_ref = &*row_ref.leaked_vec;
            *data_ptr = vec_ref.as_ptr();
            *len = vec_ref.len();
        } else {
            // Fallback to stored values if no Vec was leaked yet
            *data_ptr = row_ref.data;
            *len = row_ref.len;
        }
        1
    }
}

/// Frees a SerializedRow and its buffer.
#[unsafe(no_mangle)]
pub extern "C" fn serialized_row_free(row: *mut SerializedRow) {
    if row.is_null() {
        return;
    }
    
    unsafe {
        let row_box = Box::from_raw(row);
        // If we have a leaked Vec, reconstruct and drop it
        if !row_box.leaked_vec.is_null() {
            let _ = Box::from_raw(row_box.leaked_vec);
        } else {
            // Otherwise reconstruct and drop from raw parts
            let _ = Vec::from_raw_parts(row_box.data, row_box.len, row_box.capacity);
        }
    }
}
