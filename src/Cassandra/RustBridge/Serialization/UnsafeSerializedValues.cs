using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Cassandra
{
    internal sealed class UnsafeSerializedValues : AbstractSerializedValues
    {
        private readonly List<GCHandle> _pinnedBuffers = new();

        public UnsafeSerializedValues()
        {
            _nativeHandle = pre_serialized_values_unsafe_new();
            if (_nativeHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("pre_serialized_values_unsafe_new returned null");
            }
        }

        protected override void AddBytesImpl(byte[] bytes)
        {
            var gch = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            _pinnedBuffers.Add(gch);
            pre_serialized_values_add_value(_nativeHandle, gch.AddrOfPinnedObject(), (UIntPtr)bytes.Length);
        }
        protected override void FreeResources()
        {
            foreach (var h in _pinnedBuffers)
            {
                if (h.IsAllocated) h.Free();
            }
            _pinnedBuffers.Clear();
            _nativeHandle = IntPtr.Zero;
        }

        protected override void DisposeInternal()
        {
            if (!_detached)
            {
                FreeResources();
                _detached = true;
            }
            else
            {
                foreach (var h in _pinnedBuffers)
                {
                    if (h.IsAllocated) h.Free();
                }
                _pinnedBuffers.Clear();
            }
        }
         
        internal static ISerializedValues Build(IEnumerable<object> values)
        {
            var inst = new UnsafeSerializedValues();
            inst.AddMany(values);
            return inst;
        }
     }
 }
