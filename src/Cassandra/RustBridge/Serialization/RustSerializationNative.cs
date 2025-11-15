using System;
using System.Runtime.InteropServices;

namespace Cassandra
{
    /// Low-level P/Invoke declarations for Rust serialization FFI.
    /// These are internal and should not be used directly by application code.
    internal static class RustSerializationNative
    {
        private const string LibraryName = "csharp_wrapper";

        // ============================================================================
        // SerializedRow FFI
        // ============================================================================

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr serialized_row_new();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr serialized_row_get_writer(IntPtr row);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int serialized_row_get_data(
            IntPtr row,
            out IntPtr dataPtr,
            out UIntPtr len);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void serialized_row_free(IntPtr row);

        // ============================================================================
        // RowWriter FFI
        // ============================================================================

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr row_writer_new();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void row_writer_free(IntPtr writer);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr row_writer_value_count(IntPtr writer);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr row_writer_make_cell_writer(IntPtr writer);

        // ============================================================================
        // CellWriter FFI
        // ============================================================================

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cell_writer_set_null(IntPtr writer);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cell_writer_set_unset(IntPtr writer);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cell_writer_set_value(
            IntPtr writer,
            IntPtr data,
            UIntPtr len);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cell_writer_into_value_builder(IntPtr writer);

        // ============================================================================
        // CellValueBuilder FFI
        // ============================================================================

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cell_value_builder_append(
            IntPtr builder,
            IntPtr data,
            UIntPtr len);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cell_value_builder_set_size(
            IntPtr builder,
            int size);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cell_value_builder_finish(IntPtr builder);
    }
}
