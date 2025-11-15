using System;
using System.Runtime.InteropServices;

namespace Cassandra
{
    /// Safe wrapper around Rust CellWriter.
    /// Used to write individual cell values in a row.
    public sealed class RustCellWriter : IDisposable
    {
        private IntPtr _handle;
        private bool _consumed;

        internal RustCellWriter(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(handle));
            }
            _handle = handle;
            _consumed = false;
        }

        /// Sets the cell value to NULL, consuming this writer.
        public void SetNull()
        {
            ThrowIfConsumed();
            var result = RustSerializationNative.cell_writer_set_null(_handle);
            _consumed = true;
            _handle = IntPtr.Zero;
            
            if (result != 1)
            {
                throw new Exception("Failed to set cell to NULL");
            }
        }

        /// Sets the cell value to UNSET, consuming this writer.
        public void SetUnset()
        {
            ThrowIfConsumed();
            var result = RustSerializationNative.cell_writer_set_unset(_handle);
            _consumed = true;
            _handle = IntPtr.Zero;
            
            if (result != 1)
            {
                throw new Exception("Failed to set cell to UNSET");
            }
        }

        /// Sets the cell value to the provided byte array, consuming this writer.
        public void SetValue(byte[] data)
        {
            ThrowIfConsumed();
            
            if (data == null || data.Length == 0)
            {
                SetValueInternal(IntPtr.Zero, 0);
                return;
            }

            unsafe
            {
                fixed (byte* ptr = data)
                {
                    SetValueInternal((IntPtr)ptr, data.Length);
                }
            }
        }

        /// Sets the cell value to the provided span, consuming this writer.
        public void SetValue(ReadOnlySpan<byte> data)
        {
            ThrowIfConsumed();
            
            if (data.Length == 0)
            {
                SetValueInternal(IntPtr.Zero, 0);
                return;
            }

            unsafe
            {
                fixed (byte* ptr = data)
                {
                    SetValueInternal((IntPtr)ptr, data.Length);
                }
            }
        }

        private void SetValueInternal(IntPtr dataPtr, int length)
        {
            var result = RustSerializationNative.cell_writer_set_value(
                _handle,
                dataPtr,
                (UIntPtr)length);
            
            _consumed = true;
            _handle = IntPtr.Zero;

            if (result == -1)
            {
                throw new Exception("Cell value size exceeds maximum allowed (i32::MAX)");
            }
            else if (result != 1)
            {
                throw new Exception("Failed to set cell value");
            }
        }

        /// Converts this writer into a CellValueBuilder for gradual value construction.
        /// Consumes this writer.
        public RustCellValueBuilder IntoValueBuilder()
        {
            ThrowIfConsumed();
            var builderHandle = RustSerializationNative.cell_writer_into_value_builder(_handle);
            _consumed = true;
            _handle = IntPtr.Zero;

            if (builderHandle == IntPtr.Zero)
            {
                throw new Exception("Failed to create CellValueBuilder");
            }

            return new RustCellValueBuilder(builderHandle);
        }

        private void ThrowIfConsumed()
        {
            if (_consumed)
            {
                throw new InvalidOperationException("CellWriter has already been consumed");
            }
        }

        public void Dispose()
        {
            // CellWriter is consumed by using any of its methods
            // If not consumed, it's dropped without writing anything
            _consumed = true;
            _handle = IntPtr.Zero;
        }
    }
}
