using System;
using System.Net;
using System.Numerics;

namespace Cassandra.Serialization.Rust
{
    /// Serializer for 32-bit integers.
    internal sealed class IntSerializer : RustTypeSerializer<int>
    {
        protected override void SerializeValue(int value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var bytes = BeConverter.GetBytes(value);
            writer.SetValue(bytes);
        }
    }

    /// Serializer for 64-bit integers (bigint, counter, timestamp).
    internal sealed class LongSerializer : RustTypeSerializer<long>
    {
        protected override void SerializeValue(long value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var bytes = BeConverter.GetBytes(value);
            writer.SetValue(bytes);
        }
    }

    /// Serializer for 16-bit integers (smallint).
    internal sealed class ShortSerializer : RustTypeSerializer<short>
    {
        protected override void SerializeValue(short value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var bytes = BeConverter.GetBytes(value);
            writer.SetValue(bytes);
        }
    }

    /// Serializer for 8-bit integers (tinyint).
    internal sealed class SByteSerializer : RustTypeSerializer<sbyte>
    {
        protected override void SerializeValue(sbyte value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            Span<byte> buffer = stackalloc byte[1];
            buffer[0] = (byte)value;
            writer.SetValue(buffer);
        }
    }

    /// Serializer for boolean values.
    internal sealed class BooleanSerializer : RustTypeSerializer<bool>
    {
        protected override void SerializeValue(bool value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            Span<byte> buffer = stackalloc byte[1];
            buffer[0] = value ? (byte)1 : (byte)0;
            writer.SetValue(buffer);
        }
    }

    /// Serializer for single-precision floating point.
    internal sealed class FloatSerializer : RustTypeSerializer<float>
    {
        protected override void SerializeValue(float value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var bytes = BeConverter.GetBytes(value);
            writer.SetValue(bytes);
        }
    }

    /// Serializer for double-precision floating point.
    internal sealed class DoubleSerializer : RustTypeSerializer<double>
    {
        protected override void SerializeValue(double value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var bytes = BeConverter.GetBytes(value);
            writer.SetValue(bytes);
        }
    }

    /// Serializer for decimal values.
    internal sealed class DecimalSerializer : RustTypeSerializer<decimal>
    {
        protected override void SerializeValue(decimal value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var adapter = new DecimalTypeAdapter();
            var bytes = adapter.ConvertTo(value);
            writer.SetValue(bytes);
        }
    }

    /// Serializer for BigInteger (varint).
    internal sealed class BigIntegerSerializer : RustTypeSerializer<BigInteger>
    {
        protected override void SerializeValue(BigInteger value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var buffer = value.ToByteArray();
            Array.Reverse(buffer);
            writer.SetValue(buffer);
        }
    }

    /// Serializer for string values (text, varchar, ascii).
    internal sealed class StringSerializer : RustTypeSerializer<string>
    {
        protected override void SerializeValue(string value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            writer.SetValue(bytes);
        }
    }

    /// Serializer for byte arrays (blob).
    internal sealed class ByteArraySerializer : RustTypeSerializer<byte[]>
    {
        protected override void SerializeValue(byte[] value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            writer.SetValue(value);
        }
    }

    /// Serializer for GUID (uuid, timeuuid).
    internal sealed class GuidSerializer : RustTypeSerializer<Guid>
    {
        protected override void SerializeValue(Guid value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            Span<byte> buffer = stackalloc byte[16];
            
            var guidBytes = value.ToByteArray();
            
            // Reorder to big-endian
            buffer[0] = guidBytes[3];
            buffer[1] = guidBytes[2];
            buffer[2] = guidBytes[1];
            buffer[3] = guidBytes[0];
            buffer[4] = guidBytes[5];
            buffer[5] = guidBytes[4];
            buffer[6] = guidBytes[7];
            buffer[7] = guidBytes[6];
            
            for (int i = 8; i < 16; i++)
            {
                buffer[i] = guidBytes[i];
            }
            
            writer.SetValue(buffer);
        }
    }

    /// Serializer for TimeUuid.
    internal sealed class TimeUuidSerializer : RustTypeSerializer<TimeUuid>
    {
        protected override void SerializeValue(TimeUuid value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var guidValue = value.ToGuid();
            Span<byte> buffer = stackalloc byte[16];
            
            var guidBytes = guidValue.ToByteArray();
            
            // Reorder to big-endian
            buffer[0] = guidBytes[3];
            buffer[1] = guidBytes[2];
            buffer[2] = guidBytes[1];
            buffer[3] = guidBytes[0];
            buffer[4] = guidBytes[5];
            buffer[5] = guidBytes[4];
            buffer[6] = guidBytes[7];
            buffer[7] = guidBytes[6];
            
            for (int i = 8; i < 16; i++)
            {
                buffer[i] = guidBytes[i];
            }
            
            writer.SetValue(buffer);
        }
    }

    /// Serializer for DateTime (timestamp).
    internal sealed class DateTimeSerializer : RustTypeSerializer<DateTime>
    {
        protected override void SerializeValue(DateTime value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var timestamp = TypeSerializer.SinceUnixEpoch(new DateTimeOffset(value.ToUniversalTime())).Ticks / 10;
            var bytes = BeConverter.GetBytes(timestamp);
            writer.SetValue(bytes);
        }
    }

    /// Serializer for DateTimeOffset (timestamp).
    internal sealed class DateTimeOffsetSerializer : RustTypeSerializer<DateTimeOffset>
    {
        protected override void SerializeValue(DateTimeOffset value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var timestamp = TypeSerializer.SinceUnixEpoch(value).Ticks / 10;
            var bytes = BeConverter.GetBytes(timestamp);
            writer.SetValue(bytes);
        }
    }

    /// Serializer for LocalDate (date type).
    internal sealed class LocalDateSerializer : RustTypeSerializer<LocalDate>
    {
        protected override void SerializeValue(LocalDate value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var bytes = BeConverter.GetBytes((int)value.DaysSinceEpochCentered);
            writer.SetValue(bytes);
        }
    }

    /// Serializer for LocalTime (time type).
    internal sealed class LocalTimeSerializer : RustTypeSerializer<LocalTime>
    {
        protected override void SerializeValue(LocalTime value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var bytes = BeConverter.GetBytes(value.TotalNanoseconds);
            writer.SetValue(bytes);
        }
    }

    /// Serializer for IPAddress (inet type).
    internal sealed class IpAddressSerializer : RustTypeSerializer<IPAddress>
    {
        protected override void SerializeValue(IPAddress value, RustCellWriter writer, ColumnTypeCode typeCode, IColumnInfo typeInfo)
        {
            var bytes = value.GetAddressBytes();
            writer.SetValue(bytes);
        }
    }
}
