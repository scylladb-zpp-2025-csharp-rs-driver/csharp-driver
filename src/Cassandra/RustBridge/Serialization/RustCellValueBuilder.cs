using System;

namespace Cassandra
{
    /// Safe wrapper around Rust CellValueBuilder.
    /// Used for gradual construction of complex cell values.
    public sealed class RustCellValueBuilder : IDisposable
    {
        private IntPtr _handle;
        private bool _finished;

        internal RustCellValueBuilder(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(handle));
            }
            _handle = handle;
            _finished = false;
        }

        /// Appends data to the cell value being built.
        public void Append(byte[] data)
        {
            ThrowIfFinished();
            
            if (data == null || data.Length == 0)
            {
                return;
            }

            unsafe
            {
                fixed (byte* ptr = data)
                {
                    AppendInternal((IntPtr)ptr, data.Length);
                }
            }
        }

        /// Appends data to the cell value being built.
        public void Append(ReadOnlySpan<byte> data)
        {
            ThrowIfFinished();
            
            if (data.Length == 0)
            {
                return;
            }

            unsafe
            {
                fixed (byte* ptr = data)
                {
                    AppendInternal((IntPtr)ptr, data.Length);
                }
            }
        }

        private void AppendInternal(IntPtr dataPtr, int length)
        {
            var result = RustSerializationNative.cell_value_builder_append(
                _handle,
                dataPtr,
                (UIntPtr)length);

            if (result == -1)
            {
                throw new Exception("Total cell value size exceeds maximum allowed (i32::MAX)");
            }
            else if (result != 1)
            {
                throw new Exception("Failed to append data to cell value");
            }
        }

        /// Sets the size prefix for the cell value.
        /// Must be called before appending data for types that require a size prefix.
        public void SetSize(int size)
        {
            ThrowIfFinished();
            
            if (size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "Size must be non-negative");
            }

            var result = RustSerializationNative.cell_value_builder_set_size(_handle, size);
            
            if (result != 1)
            {
                throw new Exception("Failed to set cell value size");
            }
        }

        /// Finishes building the cell value and consumes this builder.
        public void Finish()
        {
            ThrowIfFinished();
            
            var result = RustSerializationNative.cell_value_builder_finish(_handle);
            _finished = true;
            _handle = IntPtr.Zero;

            if (result != 1)
            {
                throw new Exception("Failed to finish cell value");
            }
        }

        private void ThrowIfFinished()
        {
            if (_finished)
            {
                throw new InvalidOperationException("CellValueBuilder has already been finished");
            }
        }

        public void Dispose()
        {
            if (!_finished && _handle != IntPtr.Zero)
            {
                // If not finished, we should still finish it to maintain invariants
                try
                {
                    RustSerializationNative.cell_value_builder_finish(_handle);
                }
                catch
                {
                    // Ignore errors during disposal
                }
                _handle = IntPtr.Zero;
            }
        }
    }
}
