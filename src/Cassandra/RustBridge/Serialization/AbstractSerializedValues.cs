using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Cassandra
{
    /// <summary>
    /// Abstract base that implements common lifecycle and additive logic for serialized values containers.
    /// Derived classes provide concrete implementations for how values are recorded (eager copy vs deferred / pinned) and
    /// how the native container is materialized on detach.
    /// </summary>
    internal abstract class AbstractSerializedValues : ISerializedValues
    {
        // Shared native interop for pre_serialized_values API. Derived classes will use these.
        [DllImport("csharp_wrapper", CallingConvention = CallingConvention.Cdecl)]
        protected static extern IntPtr pre_serialized_values_new();
        [DllImport("csharp_wrapper", CallingConvention = CallingConvention.Cdecl)]
        protected static extern IntPtr pre_serialized_values_unsafe_new();
        [DllImport("csharp_wrapper", CallingConvention = CallingConvention.Cdecl)]
        protected static extern void pre_serialized_values_add_value(IntPtr builderPtr, IntPtr valuePtr, UIntPtr valueLen);
        [DllImport("csharp_wrapper", CallingConvention = CallingConvention.Cdecl)]
        private static extern void pre_serialized_values_add_null(IntPtr builderPtr);
        [DllImport("csharp_wrapper", CallingConvention = CallingConvention.Cdecl)]
        private static extern void pre_serialized_values_add_unset(IntPtr builderPtr);
        
        protected bool _detached;
        protected IntPtr _nativeHandle;
        protected int _count;

        public int Count => _count;

        /// <summary>
        /// Adds a pre-serialized value buffer (never null). Increments count afterward.
        /// </summary>
        private void AddValue(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            EnsureNotDetached();
            AddBytesImpl(bytes);
            _count++;
        }
        
        private void AddNull()
        {
            EnsureNotDetached();
            pre_serialized_values_add_null(_nativeHandle);
            _count++;
        }
        
        private void AddUnset()
        {
            EnsureNotDetached();
            pre_serialized_values_add_unset(_nativeHandle);
            _count++;
        }
        
        protected void AddMany(IEnumerable<object> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            foreach (var v in values)
            {
                if (v == null) { AddNull(); continue; }
                if (ReferenceEquals(v, Unset.Value)) { AddUnset(); continue; }
                if (v is byte[] bytes) { AddValue(bytes); continue; }
                throw new ArgumentException("Value must be null, Unset.Value or a pre-serialized byte[].", nameof(values));
            }
        }

        /// <summary>
        /// Detach transfers ownership of the native container to caller and invalidates this instance.
        /// </summary>
        public IntPtr Detach()
        {
            EnsureNotDetached();
            if (_nativeHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Native handle is null; nothing to detach.");
            }
            _detached = true;
            var ptr = _nativeHandle;
            _nativeHandle = IntPtr.Zero; // no longer tracked here
            return ptr;
        }

        private void EnsureNotDetached()
        {
            if (_detached)
            {
                throw new ObjectDisposedException(GetType().Name, "Already detached / disposed");
            }
        }

        public void Dispose()
        {
            DisposeInternal();
            GC.SuppressFinalize(this);
        }

        protected virtual void DisposeInternal()
        {
            if (!_detached)
            {
                // Derived class frees native / unpins resources when disposed prior to detach.
                FreeResources();
                _detached = true;
            }
        }

        /// <summary>
        /// Derived class must implement how to record a bytes value.
        /// </summary>
        protected abstract void AddBytesImpl(byte[] bytes);
        /// <summary>
        /// Cleanup resources when disposing prior to detach. After detach, derived classes may override DisposeInternal
        /// to perform post-detach cleanup (like unpinning). Eager implementations simply free native here.
        /// </summary>
        protected abstract void FreeResources();
        
        public IntPtr GetNativeHandle()
        {
            EnsureNotDetached();
            return _nativeHandle;
        }
    }
}
