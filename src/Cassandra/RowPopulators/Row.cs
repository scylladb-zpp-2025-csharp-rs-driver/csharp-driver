//
//      Copyright (C) DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Cassandra.Mapping.TypeConversion;

// ReSharper disable once CheckNamespace
namespace Cassandra
{
    /// <summary>
    /// Represents an CQL row
    /// </summary>
    public class Row : IEnumerable<object>, IRow
    {
        /// <summary>
        /// Gets or sets the index of the columns within the row
        /// </summary>
        protected Dictionary<string, int> ColumnIndexes
        {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        /// <summary>
        /// Gets or sets the columns information
        /// </summary>
        protected CqlColumn[] Columns { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        [Obsolete("This property is deprecated and to be removed in future versions.")]
        protected byte[][] Values { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        [Obsolete("This property is deprecated and to be removed in future versions.")]
        protected int ProtocolVersion { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        /// <summary>
        /// Gets the total amount of values inside the row
        /// </summary>
        public int Length
        {
            get => throw new NotImplementedException();
        }

        /// <summary>
        /// Gets the stored value in the column specified by index
        /// </summary>
        public object this[int index]
        {
            get { return GetValue(typeof(object), index); }
        }

        /// <summary>
        /// Gets the stored value in the column specified by name
        /// </summary>
        public object this[string name]
        {
            get { return this[ColumnIndexes[name]]; }
        }

        /// <summary>
        /// Initializes a new instance of the Cassandra.Row class
        /// </summary>
        public Row()
        {
            //Default constructor for client test and mocking frameworks
        }

        /// <summary>
        /// Initializes a new instance of the Cassandra.Row class
        /// </summary>
        [Obsolete("This constructor is deprecated and to be removed in future versions. " +
                  "If you need to create mock instances of Row, use the parameter-less constructor and override GetValue<T>()")]
        public Row(int protocolVersion, byte[][] values, CqlColumn[] columns, Dictionary<string, int> columnIndexes)
        {
            ProtocolVersion = protocolVersion;
            Values = values;
            Columns = columns;
            ColumnIndexes = columnIndexes;
        }

        /// <summary>
        /// Returns an enumerator that iterates through the row values from the first position to the last one.
        /// </summary>
        public IEnumerator<object> GetEnumerator()
        {
            return Columns.Select(c => GetValue(c.Type, c.Index)).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            //.NET legacy enumerator
            return GetEnumerator();
        }

        /// <summary>
        /// Determines if the value in the column is null
        /// </summary>
        public bool IsNull(string name)
        {
            return IsNull(ColumnIndexes[name]);
        }

        /// <summary>
        /// Determines if the value in the column is null
        /// </summary>
        public virtual bool IsNull(int index)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Gets a column information by name. Returns null if not found.
        /// </summary>
        public CqlColumn GetColumn(string name)
        {
            return ColumnIndexes.TryGetValue(name, out var index)
                ? Columns[index]
                : null;
        }

        /// <summary>
        /// Returns true if the row contains information of the provided column name.
        /// </summary>
        bool IRow.ContainsColumn(string name)
        {
            return ContainsColumn(name);
        }

        /// <summary>
        /// Returns true if the row contains information of the provided column name.
        /// </summary>
        internal bool ContainsColumn(string name)
        {
            return GetColumn(name) != null;
        }

        /// <summary>
        /// Gets the stored value in the column specified by index
        /// </summary>
        /// <param name="type">Target type</param>
        /// <param name="index">Index of the column</param>
        /// <returns></returns>
        public object GetValue(Type type, int index)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Gets the stored value in the column specified by name
        /// </summary>
        /// <param name="type">Target type</param>
        /// <param name="name">Name of the column</param>
        /// <returns></returns>
        public object GetValue(Type type, string name)
        {
            return GetValue(type, ColumnIndexes[name]);
        }


        /// <summary>
        /// Gets the stored value in the column specified by index.
        /// </summary>
        /// <typeparam name="T">Target type</typeparam>
        /// <param name="index">Index of the column</param>
        /// <returns></returns>
        public virtual T GetValue<T>(int index)
        {
            //The method is marked virtual to allow to be mocked
            var type = typeof(T);
            var value = GetValue(type, index);
            //Check that the value is null but the type is not nullable (structs)
            //A little overhead in case of misuse but improved Error message
            if (value == null && default(T) != null)
            {
                throw new NullReferenceException(string.Format("Cannot convert null to {0} because it is a value type, try using Nullable<{0}>", type.Name));
            }
            return (T)value;
        }

        /// <summary>
        /// Gets the stored value in the column specified by name.
        /// </summary>
        /// <typeparam name="T">Target type</typeparam>
        /// <param name="name">Name of the column</param>
        /// <returns></returns>
        public virtual T GetValue<T>(string name)
        {
            //The method is marked virtual to allow to be mocked
            if (!ColumnIndexes.ContainsKey(name))
            {
                throw new ArgumentException(string.Format("Column {0} not found", name));
            }
            return GetValue<T>(ColumnIndexes[name]);
        }
    }

    /// <summary>
    /// Internal representation of a Row
    /// </summary>
    internal interface IRow
    {
        T GetValue<T>(string name);

        bool ContainsColumn(string name);

        bool IsNull(string name);

        T GetValue<T>(int index);

        CqlColumn GetColumn(string name);
    }
}
