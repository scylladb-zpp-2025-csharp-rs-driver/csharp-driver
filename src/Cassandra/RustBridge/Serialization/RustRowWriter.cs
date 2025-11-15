using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cassandra
{
    /// Safe handle for Rust SerializedRow.
    public sealed class SerializedRowHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SerializedRowHandle() : base(true)
        {
        }

        internal static SerializedRowHandle Create()
        {
            var handle = new SerializedRowHandle();
            var rowPtr = RustSerializationNative.serialized_row_new();
            
            if (rowPtr == IntPtr.Zero)
            {
                throw new Exception("Failed to create SerializedRow");
            }

            handle.SetHandle(rowPtr);
            return handle;
        }

        protected override bool ReleaseHandle()
        {
            RustSerializationNative.serialized_row_free(handle);
            return true;
        }
    }

    /// Safe handle for Rust RowWriter.
    public sealed class RowWriterHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private RowWriterHandle() : base(true)
        {
        }

        internal static RowWriterHandle CreateFromRow(SerializedRowHandle rowHandle)
        {
            var handle = new RowWriterHandle();
            var writerPtr = RustSerializationNative.serialized_row_get_writer(rowHandle.DangerousGetHandle());
            
            if (writerPtr == IntPtr.Zero)
            {
                throw new Exception("Failed to create RowWriter");
            }

            handle.SetHandle(writerPtr);
            return handle;
        }

        protected override bool ReleaseHandle()
        {
            RustSerializationNative.row_writer_free(handle);
            return true;
        }
    }

    /// Safe wrapper around Rust RowWriter with backing buffer.
    /// Manages serialization of an entire row of values.
    public sealed class RustRowWriter : IDisposable
    {
        private readonly SerializedRowHandle _rowHandle;
        private readonly RowWriterHandle _writerHandle;
        private bool _disposed;

        public RustRowWriter()
        {
            _rowHandle = SerializedRowHandle.Create();
            _writerHandle = RowWriterHandle.CreateFromRow(_rowHandle);
        }

        /// Gets the number of values written to the row so far.
        public int ValueCount
        {
            get
            {
                ThrowIfDisposed();
                var count = RustSerializationNative.row_writer_value_count(_writerHandle.DangerousGetHandle());
                return (int)count;
            }
        }

        /// Creates a new CellWriter for appending a value to the row.
        /// The returned CellWriter must be consumed by calling one of its methods.
        public RustCellWriter MakeCellWriter()
        {
            ThrowIfDisposed();
            
            var cellHandle = RustSerializationNative.row_writer_make_cell_writer(_writerHandle.DangerousGetHandle());
            
            if (cellHandle == IntPtr.Zero)
            {
                throw new Exception("Failed to create CellWriter");
            }

            return new RustCellWriter(cellHandle);
        }

        /// Gets the serialized byte array for this row.
        public byte[] ToByteArray()
        {
            ThrowIfDisposed();
            
            var result = RustSerializationNative.serialized_row_get_data(
                _rowHandle.DangerousGetHandle(),
                out IntPtr dataPtr,
                out UIntPtr len);

            if (result != 1)
            {
                throw new Exception("Failed to get serialized data");
            }

            var length = (int)len;
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            var data = new byte[length];
            Marshal.Copy(dataPtr, data, 0, length);
            return data;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RustRowWriter));
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _writerHandle?.Dispose();
                _rowHandle?.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~RustRowWriter()
        {
            Dispose();
        }
    }
}
