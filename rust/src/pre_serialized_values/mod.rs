#[path = "pre_serialized_values.rs"]
pub(crate) mod pre_serialized_values;
#[path = "pre_serialized_values_safe.rs"]
pub(crate) mod pre_serialized_values_safe;
#[path = "pre_serialized_values_unsafe.rs"]
pub(crate) mod pre_serialized_values_unsafe;

pub(crate) use pre_serialized_values::{PreSerializedValuesTrait, PreSerializedValues, validate_number_of_columns, HasCells, serialize_each_cell};