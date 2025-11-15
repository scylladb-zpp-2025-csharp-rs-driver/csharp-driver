using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Cassandra
{
    internal sealed class SafeSerializedValues : AbstractSerializedValues
    {
        private SafeSerializedValues()
        {
            _nativeHandle = pre_serialized_values_new();
            if (_nativeHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("pre_serialized_values_new returned null");
            }
        }

        // Implement abstract recording methods (eager copy into native container)
        protected override void AddBytesImpl(byte[] bytes)
        {
            var gch = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                pre_serialized_values_add_value(_nativeHandle, gch.AddrOfPinnedObject(), (UIntPtr)bytes.Length);
            }
            finally { gch.Free(); }
        }
        protected override void FreeResources()
        {
            _nativeHandle = IntPtr.Zero;
        }
        
        internal static ISerializedValues Build(IEnumerable<object> values)
        {
            var inst = new SafeSerializedValues();
            inst.AddMany(values);
            return inst;
        }
    }
}
