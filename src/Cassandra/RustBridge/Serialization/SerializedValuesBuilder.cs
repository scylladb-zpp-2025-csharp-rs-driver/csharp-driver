using System;
using System.Collections.Generic;
using Cassandra.Serialization;

namespace Cassandra
{
    internal sealed class SerializedValuesBuilder
    {
        private readonly ISerializer _serializer;
        private readonly List<object> _values = new();

        internal SerializedValuesBuilder()
        {
            _serializer = SerializerManager.Default.GetCurrentSerializer();
        }
        

        /// <summary>
        /// Adds a value to this build. Serialization rules:
        ///  - null: stored as null (NULL marker)
        ///  - Unset.Value: stored as Unset.Value (UNSET marker)
        ///  - byte[]: assumed already serialized
        ///  - any other object: serialized using the configured serializer
        /// </summary>
        public SerializedValuesBuilder Add(object value)
        {
            if (value == null)
            {
                _values.Add(null);
                return this;
            }
            if (ReferenceEquals(value, Unset.Value))
            {
                _values.Add(Unset.Value);
                return this;
            }
            var buf = _serializer.Serialize(value);
            _values.Add(buf);
            return this;
        }

        public SerializedValuesBuilder AddMany(IEnumerable<object> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            foreach (var v in values)
            {
                Add(v);
            }
            return this;
        }

        public int Count => _values.Count;

        public void Reset() => _values.Clear();

        public ISerializedValues Build()
        {
            return SafeSerializedValues.Build(_values);
        }
    }
}
