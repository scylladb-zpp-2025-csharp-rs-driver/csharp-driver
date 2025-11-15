using System;
using System.Collections.Generic;

namespace Cassandra.Serialization.Rust
{
    /// Registry for Rust-based serializers.
    /// Maps types and type codes to appropriate serializers.
    internal sealed class RustSerializerRegistry
    {
        private readonly Dictionary<ColumnTypeCode, IRustTypeSerializer> _primitiveSerializers;
        private readonly Dictionary<Type, IRustTypeSerializer> _typeSerializers;

        public RustSerializerRegistry()
        {
            _primitiveSerializers = new Dictionary<ColumnTypeCode, IRustTypeSerializer>();
            _typeSerializers = new Dictionary<Type, IRustTypeSerializer>();

            InitializePrimitiveSerializers();
        }

        private void InitializePrimitiveSerializers()
        {
            // Numeric types
            RegisterPrimitive(ColumnTypeCode.Int, new IntSerializer());
            RegisterPrimitive(ColumnTypeCode.Bigint, new LongSerializer());
            RegisterPrimitive(ColumnTypeCode.Counter, new LongSerializer());
            RegisterPrimitive(ColumnTypeCode.SmallInt, new ShortSerializer());
            RegisterPrimitive(ColumnTypeCode.TinyInt, new SByteSerializer());
            RegisterPrimitive(ColumnTypeCode.Float, new FloatSerializer());
            RegisterPrimitive(ColumnTypeCode.Double, new DoubleSerializer());
            RegisterPrimitive(ColumnTypeCode.Decimal, new DecimalSerializer());
            RegisterPrimitive(ColumnTypeCode.Varint, new BigIntegerSerializer());

            // Boolean
            RegisterPrimitive(ColumnTypeCode.Boolean, new BooleanSerializer());

            // Text types
            RegisterPrimitive(ColumnTypeCode.Text, new StringSerializer());
            RegisterPrimitive(ColumnTypeCode.Varchar, new StringSerializer());
            RegisterPrimitive(ColumnTypeCode.Ascii, new StringSerializer());

            // Binary
            RegisterPrimitive(ColumnTypeCode.Blob, new ByteArraySerializer());

            // UUID types
            RegisterPrimitive(ColumnTypeCode.Uuid, new GuidSerializer());
            RegisterPrimitive(ColumnTypeCode.Timeuuid, new TimeUuidSerializer());

            // Date/Time types
            RegisterPrimitive(ColumnTypeCode.Timestamp, new DateTimeOffsetSerializer());
            RegisterPrimitive(ColumnTypeCode.Date, new LocalDateSerializer());
            RegisterPrimitive(ColumnTypeCode.Time, new LocalTimeSerializer());

            // Network
            RegisterPrimitive(ColumnTypeCode.Inet, new IpAddressSerializer());

            // Also register by CLR type
            RegisterByType<int>(new IntSerializer());
            RegisterByType<long>(new LongSerializer());
            RegisterByType<short>(new ShortSerializer());
            RegisterByType<sbyte>(new SByteSerializer());
            RegisterByType<float>(new FloatSerializer());
            RegisterByType<double>(new DoubleSerializer());
            RegisterByType<decimal>(new DecimalSerializer());
            RegisterByType<System.Numerics.BigInteger>(new BigIntegerSerializer());
            RegisterByType<bool>(new BooleanSerializer());
            RegisterByType<string>(new StringSerializer());
            RegisterByType<byte[]>(new ByteArraySerializer());
            RegisterByType<Guid>(new GuidSerializer());
            RegisterByType<TimeUuid>(new TimeUuidSerializer());
            RegisterByType<DateTime>(new DateTimeSerializer());
            RegisterByType<DateTimeOffset>(new DateTimeOffsetSerializer());
            RegisterByType<LocalDate>(new LocalDateSerializer());
            RegisterByType<LocalTime>(new LocalTimeSerializer());
            RegisterByType<System.Net.IPAddress>(new IpAddressSerializer());
        }

        private void RegisterPrimitive(ColumnTypeCode typeCode, IRustTypeSerializer serializer)
        {
            _primitiveSerializers[typeCode] = serializer;
        }

        private void RegisterByType<T>(IRustTypeSerializer serializer)
        {
            _typeSerializers[typeof(T)] = serializer;
        }

        /// Gets a serializer for the specified column type.
        public IRustTypeSerializer GetSerializer(ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            // Try primitive serializers first
            if (_primitiveSerializers.TryGetValue(typeCode, out var serializer))
            {
                return serializer;
            }

            throw new NotSupportedException($"Serialization not supported for type code: {typeCode}");
        }

        /// Gets a serializer for the specified CLR type.
        public IRustTypeSerializer GetSerializer(Type type)
        {
            if (_typeSerializers.TryGetValue(type, out var serializer))
            {
                return serializer;
            }

            // Handle nullable types
            var underlyingType = Nullable.GetUnderlyingType(type);
            if (underlyingType != null && _typeSerializers.TryGetValue(underlyingType, out serializer))
            {
                return serializer;
            }

            throw new NotSupportedException($"Serialization not supported for CLR type: {type.Name}");
        }
    }
}
