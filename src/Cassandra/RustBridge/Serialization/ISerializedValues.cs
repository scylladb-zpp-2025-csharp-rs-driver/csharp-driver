using System;

namespace Cassandra
{
    /// <summary>
    /// Abstraction over a native pre_serialized_values container.
    /// Provides methods to add already serialized value buffers (or NULL / UNSET markers) and
    /// to transfer ownership of the native container to Rust via Detach().
    /// </summary>
    internal interface ISerializedValues : IDisposable
    {
        int Count { get; }
        
        IntPtr GetNativeHandle();
        
        /// <summary>
        /// Finalizes the native container (if not yet built) and detaches the raw native pointer,
        /// transferring ownership to the caller (Rust side). After Detach(), the instance should
        /// not be used except for Dispose() (which will only free resources in the unsafe variant).
        /// </summary>
        IntPtr Detach();
    }
}
