using System;

namespace Cassandra.Serialization.Rust
{
    /// Factory for creating Rust-based row serializers.
    /// This is the main entry point for Rust serialization.
    public static class RustSerializationFactory
    {
        private static readonly RustSerializerRegistry _defaultRegistry = new RustSerializerRegistry();

        /// Serializes a row of values using Rust writers.
        /// Returns serialized byte array.
        public static byte[] SerializeRow(object[] values, ColumnDesc[] columnSpecs)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            if (columnSpecs == null)
            {
                throw new ArgumentNullException(nameof(columnSpecs));
            }

            if (values.Length != columnSpecs.Length)
            {
                throw new ArgumentException(
                    $"Value count ({values.Length}) must match column spec count ({columnSpecs.Length})");
            }

            using (var rowWriter = new RustRowWriter())
            {
                for (int i = 0; i < values.Length; i++)
                {
                    var value = values[i];
                    var spec = columnSpecs[i];

                    using (var cellWriter = rowWriter.MakeCellWriter())
                    {
                        SerializeValue(value, cellWriter, spec.TypeCode, spec.TypeInfo);
                    }
                }

                return rowWriter.ToByteArray();
            }
        }

        /// Serializes a single value using a Rust CellWriter.
        internal static void SerializeValue(
            object value,
            RustCellWriter writer,
            ColumnTypeCode typeCode,
            IColumnInfo typeInfo)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            var serializer = _defaultRegistry.GetSerializer(typeCode, typeInfo);
            serializer.Serialize(value, writer, typeCode, typeInfo);
        }

        /// Gets the default serializer registry.
        /// Can be used to register custom serializers.
        internal static RustSerializerRegistry GetDefaultRegistry()
        {
            return _defaultRegistry;
        }
    }
}
