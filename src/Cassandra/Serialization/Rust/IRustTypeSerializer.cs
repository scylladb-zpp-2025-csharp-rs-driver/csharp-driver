using System;

namespace Cassandra.Serialization.Rust
{
    /// Interface for serializers that use Rust writers.
    internal interface IRustTypeSerializer
    {
        /// Serializes a value using a Rust CellWriter.
        void Serialize(object value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo);
    }

    /// Base class for type-specific Rust serializers.
    internal abstract class RustTypeSerializer<T> : IRustTypeSerializer
    {
        public void Serialize(object value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            if (value == null)
            {
                writer.SetNull();
                return;
            }

            if (value is T typedValue)
            {
                SerializeValue(typedValue, writer, typeCode, typeInfo);
            }
            else
            {
                throw new InvalidOperationException($"Expected type {typeof(T).Name} but got {value.GetType().Name}");
            }
        }

        protected abstract void SerializeValue(T value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo);
    }
}
