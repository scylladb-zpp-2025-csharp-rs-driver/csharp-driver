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
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Tasks;

// ReSharper disable DoNotCallOverridableMethodsInConstructor
// ReSharper disable CheckNamespace

namespace Cassandra
{
    /// <summary>
    /// Represents the result of a query returned by the server.
    /// <para>
    /// The retrieval of the rows of a <see cref="RowSet"/> is generally paged (a first page
    /// of result is fetched and the next one is only fetched once all the results
    /// of the first page have been consumed). The size of the pages can be configured
    /// either globally through <see cref="QueryOptions.SetPageSize(int)"/> or per-statement
    /// with <see cref="IStatement.SetPageSize(int)"/>. Though new pages are automatically
    /// and transparently fetched when needed, it is possible to force the retrieval
    /// of the next page early through <see cref="FetchMoreResults"/> and  <see cref="FetchMoreResultsAsync"/>.
    /// </para>
    /// <para>
    /// The RowSet dequeues <see cref="Row"/> items while iterated. After a full enumeration of this instance, following
    /// enumerations will be empty, as all rows have been dequeued.
    /// </para>
    /// </summary>
    /// <remarks>Parallel enumerations are supported and thread-safe.</remarks>
    public class RowSet : IEnumerable<Row>, IDisposable
    {
        /// <summary>
        /// Determines if when dequeuing, it will automatically fetch the following result pages.
        /// </summary>
        protected internal bool AutoPage
        {
            get => throw new NotImplementedException("AutoPage getter is not yet implemented"); // FIXME: bridge with Rust paging.
            set => throw new NotImplementedException("AutoPage setter is not yet implemented"); // FIXME: bridge with Rust paging.
        }

        /// <summary>
        /// Gets or set the internal row list. It contains the rows of the latest query page.
        /// </summary>
        protected virtual ConcurrentQueue<Row> RowQueue
        {
            get => throw new NotImplementedException("RowQueue getter is not yet implemented"); // FIXME: bridge with Rust paging.
            set => throw new NotImplementedException("RowQueue setter is not yet implemented"); // FIXME: bridge with Rust paging.;
        }

        /// <summary>
        /// Gets the execution info of the query
        /// </summary>
        public virtual ExecutionInfo Info { get; set; }

        /// <summary>
        /// Gets or sets the columns in the RowSet
        /// </summary>
        public virtual CqlColumn[] Columns { get; set; }

        /// <summary>
        /// Gets or sets the paging state of the query for the RowSet.
        /// When set it states that there are more pages.
        /// </summary>
        public virtual byte[] PagingState
        {
            get => throw new NotImplementedException("PagingState getter is not yet implemented"); // FIXME: bridge with Rust paging state.
            protected internal set => throw new NotImplementedException("PagingState getter is not yet implemented"); // FIXME: bridge with Rust paging state.;
        }

        /// <summary>
        /// Returns whether this ResultSet has more results.
        /// It has side-effects, if the internal queue has been consumed it will page for more results.
        /// </summary>
        /// <seealso cref="IsFullyFetched"/>
        public virtual bool IsExhausted()
        {
            throw new NotImplementedException("PagingState getter is not yet implemented"); // FIXME: bridge with Rust paging state.
        }

        /// <summary>
        /// Whether all results from this result set has been fetched from the database.
        /// </summary>
        public virtual bool IsFullyFetched => PagingState == null || !AutoPage;

        /// <summary>
        /// Creates a new instance of RowSet.
        /// </summary>
        public RowSet()
        {
            Info = new ExecutionInfo();
        }

        /// <summary>
        /// Forces the fetching the next page of results for this <see cref="RowSet"/>.
        /// </summary>
        public void FetchMoreResults()
        {
            throw new NotImplementedException("FetchMoreResults is not yet implemented"); // FIXME: bridge with Rust paging.
        }

        /// <summary>
        /// Asynchronously retrieves the next page of results for this <see cref="RowSet"/>.
        /// <para>
        /// The Task will be completed once the internal queue is filled with the new <see cref="Row"/>
        /// instances.
        /// </para>
        /// </summary>
        public Task FetchMoreResultsAsync()
        {
            throw new NotImplementedException("FetchMoreResultsAsync is not yet implemented"); // FIXME: bridge with Rust paging.
        }

        /// <summary>
        /// The number of rows available in this row set that can be retrieved without blocking to fetch.
        /// </summary>
        public int GetAvailableWithoutFetching()
        {
            throw new NotImplementedException("GetAvailableWithoutFetching is not yet implemented"); // FIXME: bridge with Rust paging.
        }

        /// <summary>
        /// For backward compatibility: It is possible to iterate using the RowSet as it is enumerable.
        /// <para>Obsolete: Note that it will be removed in future versions</para>
        /// </summary>
        public IEnumerable<Row> GetRows()
        {
            //legacy: Keep the GetRows method for Compatibility.
            return this;
        }

        /// <inheritdoc />
        public virtual IEnumerator<Row> GetEnumerator()
        {
            throw new NotImplementedException("GetEnumerator is not yet implemented"); // FIXME: bridge with Rust paging.
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Gets the next results and add the rows to the current <see cref="RowSet"/> queue.
        /// </summary>
        protected virtual void PageNext()
        {
            throw new NotImplementedException("PageNext is not yet implemented"); // FIXME: bridge with Rust paging.
        }

        /// <summary>
        /// For backward compatibility only
        /// </summary>
        [Obsolete("Explicitly releasing the RowSet resources is not required. It will be removed in future versions.", false)]
        public void Dispose()
        {

        }
    }
}
