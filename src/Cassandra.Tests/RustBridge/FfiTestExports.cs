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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    /// <summary>
    /// P/Invoke declarations for the test-only Rust exports (see <c>rust/src/ffi_test_exports.rs</c>),
    /// plus the managed sinks Rust calls back into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These exist so the FFI structs are built and consumed by the <em>unmanaged</em> side. A test
    /// that fabricates an <c>FFIString</c> from a pinned <c>byte[]</c> and decodes it again asserts a
    /// layout it invented, with an encoder it also owns, and never involves Rust - so it cannot fail
    /// for any reason that would matter in production. Here Rust owns the strings, slices, UUID
    /// bytes, resources and exception handles; C# only observes what arrived.
    /// </para>
    /// <para>
    /// The opaque context passed to every sink is the address of a stack local, taken with
    /// <see cref="Unsafe.AsPointer{T}(ref T)"/> in the same frame that performs the synchronous
    /// P/Invoke. That is exactly the production pattern (see <c>BridgedSession.GetKeyspace</c>), and
    /// it is only sound because the call is synchronous: the frame - and therefore the slot holding
    /// the reference - outlives the call. Never lift one of these into an async path.
    /// </para>
    /// </remarks>
    internal static unsafe class FfiTestExports
    {
        private const string Lib = NativeLibrary.CSharpWrapper;
        private const CallingConvention Cdecl = CallingConvention.Cdecl;

        /*
         * ABI manifest
         */

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_abi_manifest(
            IntPtr ctx, IntPtr emitType, IntPtr emitField);

        /*
         * Exception constructor table
         */

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern nuint ffi_test_exception_slot_count();

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_exception_slot_name(
            nuint slot, IntPtr cb, IntPtr ctx);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_exception_slot_marker(
            nuint slot, IntPtr cb, IntPtr ctx);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIGCHandle ffi_test_build_exception(nuint slot, IntPtr constructors);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_prepared_id_bytes(IntPtr cb, IntPtr ctx);

        /*
         * Strings
         */

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern byte ffi_test_str_kind_count();

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_produce_str(byte kind, IntPtr cb, IntPtr ctx);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern nuint ffi_test_produced_str_len(byte kind);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_echo_str(
            RustBridge.FFIString input, IntPtr cb, IntPtr ctx);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_echo_cstr(IntPtr input, IntPtr cb, IntPtr ctx);

        /*
         * Slices
         */

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern byte ffi_test_byte_slice_kind_count();

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_produce_byte_slice(
            byte kind, IntPtr cb, IntPtr ctx);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern nuint ffi_test_produced_byte_slice_len(byte kind);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_produce_u32_slice(IntPtr cb, IntPtr ctx);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_expected_u32_values(IntPtr cb, IntPtr ctx);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_echo_slice_as_str(
            RustBridge.FFISlice<byte> input, IntPtr cb, IntPtr ctx);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_produce_ip_octets(
            RustBridge.FFIBool v6, IntPtr cb, IntPtr ctx);

        /*
         * Booleans
         */

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIBool ffi_test_echo_bool(RustBridge.FFIBool value);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern byte ffi_test_bool_as_byte(RustBridge.FFIBool value);

        /*
         * UUIDs
         */

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_uuid_to_string(
            RustBridge.FFISlice<byte> bytes, IntPtr cb, IntPtr ctx);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_uuid_to_bytes(
            RustBridge.FFIString text, IntPtr cb, IntPtr ctx);

        /*
         * Callback iteration
         */

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.FFIMaybeException ffi_test_for_each_u32(
            uint count, IntPtr cb, IntPtr ctx);

        /*
         * Resources
         */

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern RustBridge.ManuallyDestructible ffi_test_make_resource(ulong value);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern ulong ffi_test_resource_value(IntPtr handle);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern nuint ffi_test_live_resources();

        /*
         * Async completion
         */

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern void ffi_test_complete_bool_task(
            RustBridge.Tcb<RustBridge.FFIBool> tcb, RustBridge.FFIBool value);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern void ffi_test_fail_bool_task(
            RustBridge.Tcb<RustBridge.FFIBool> tcb, IntPtr constructors);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern void ffi_test_complete_bool_task_async(
            RustBridge.Tcb<RustBridge.FFIBool> tcb, RustBridge.FFIBool value);

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern void ffi_test_fail_bool_task_async(
            RustBridge.Tcb<RustBridge.FFIBool> tcb, IntPtr constructors);

        /*
         * GC movement probe
         */

        [StructLayout(LayoutKind.Sequential)]
        internal struct GcProbeResult
        {
            internal nuint Addr;
            internal ulong HashBefore;
            internal ulong HashAfter;
        }

        [DllImport(Lib, CallingConvention = Cdecl)]
        internal static extern GcProbeResult ffi_test_gc_move_probe(
            RustBridge.FFISlice<byte> buf, IntPtr provokeGc, IntPtr ctx);

        /*
         * ---------------------------------------------------------------------------------------
         * Managed sinks. Each recovers its collector from the opaque context, records what arrived,
         * and returns Ok. They deliberately record rather than assert: throwing out of an
         * [UnmanagedCallersOnly] method across the FFI boundary is undefined behaviour, so any
         * failure is surfaced as an FFIMaybeException and re-thrown by the caller on the managed
         * side of the boundary.
         * ---------------------------------------------------------------------------------------
         */

        /// <summary>Rust's description of one FFI struct's layout.</summary>
        internal sealed class RustTypeLayout
        {
            internal nuint Size;
            internal nuint Align;
            internal readonly Dictionary<string, nuint> FieldOffsets = new Dictionary<string, nuint>();
        }

        internal sealed class AbiCollector
        {
            internal readonly Dictionary<string, RustTypeLayout> Types =
                new Dictionary<string, RustTypeLayout>();
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static RustBridge.FFIMaybeException EmitType(
            IntPtr ctx, RustBridge.FFIString name, nuint size, nuint align)
        {
            try
            {
                var collector = Unsafe.AsRef<AbiCollector>((void*)ctx);
                collector.Types[name.ToManagedString()] = new RustTypeLayout { Size = size, Align = align };
                return RustBridge.FFIMaybeException.Ok();
            }
            catch (Exception ex)
            {
                return RustBridge.FFIMaybeException.FromException(ex);
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static RustBridge.FFIMaybeException EmitField(
            IntPtr ctx, RustBridge.FFIString typeName, RustBridge.FFIString fieldName, nuint offset)
        {
            try
            {
                var collector = Unsafe.AsRef<AbiCollector>((void*)ctx);
                collector.Types[typeName.ToManagedString()].FieldOffsets[fieldName.ToManagedString()] = offset;
                return RustBridge.FFIMaybeException.Ok();
            }
            catch (Exception ex)
            {
                return RustBridge.FFIMaybeException.FromException(ex);
            }
        }

        internal static IntPtr EmitTypePtr => (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, RustBridge.FFIString, nuint, nuint, RustBridge.FFIMaybeException>)&EmitType;
        internal static IntPtr EmitFieldPtr => (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, RustBridge.FFIString, RustBridge.FFIString, nuint, RustBridge.FFIMaybeException>)&EmitField;

        /// <summary>Receives one Rust-owned byte slice.</summary>
        internal sealed class ByteSliceCollector
        {
            internal byte[] Bytes;
            /// <summary>Length reported in the slice, kept separately from the copied array.</summary>
            internal nuint ReportedLength;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static RustBridge.FFIMaybeException ReceiveByteSlice(RustBridge.FFISliceRaw raw, IntPtr ctx)
        {
            try
            {
                var collector = Unsafe.AsRef<ByteSliceCollector>((void*)ctx);
                collector.ReportedLength = raw.len;
                // As<byte>() reinterprets the non-generic slice the [UnmanagedCallersOnly] signature
                // forced us to receive. The (ptr, len) pair came from Rust's FFISlice::new, so this
                // checks the reinterpretation against a real Rust layout.
                collector.Bytes = raw.As<byte>().ToSpan().ToArray();
                return RustBridge.FFIMaybeException.Ok();
            }
            catch (Exception ex)
            {
                return RustBridge.FFIMaybeException.FromException(ex);
            }
        }

        internal static IntPtr ReceiveByteSlicePtr => (IntPtr)(delegate* unmanaged[Cdecl]<RustBridge.FFISliceRaw, IntPtr, RustBridge.FFIMaybeException>)&ReceiveByteSlice;

        /// <summary>Receives one Rust-owned <c>u32</c> slice.</summary>
        internal sealed class U32SliceCollector
        {
            internal uint[] Values;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static RustBridge.FFIMaybeException ReceiveU32Slice(RustBridge.FFISliceRaw raw, IntPtr ctx)
        {
            try
            {
                var collector = Unsafe.AsRef<U32SliceCollector>((void*)ctx);
                collector.Values = raw.As<uint>().ToSpan().ToArray();
                return RustBridge.FFIMaybeException.Ok();
            }
            catch (Exception ex)
            {
                return RustBridge.FFIMaybeException.FromException(ex);
            }
        }

        internal static IntPtr ReceiveU32SlicePtr => (IntPtr)(delegate* unmanaged[Cdecl]<RustBridge.FFISliceRaw, IntPtr, RustBridge.FFIMaybeException>)&ReceiveU32Slice;

        /// <summary>
        /// Receives a stream of <c>u32</c> values, optionally throwing on a chosen one to exercise the
        /// early-abort path of <c>ffi_callback_for_each</c>.
        /// </summary>
        internal sealed class U32StreamCollector
        {
            internal readonly List<uint> Values = new List<uint>();
            /// <summary>Item index to fail on, or -1 to accept everything.</summary>
            internal int ThrowOnIndex = -1;
            /// <summary>Message the failing item reports, so the test can identify the exception.</summary>
            internal string FailureMessage = "callback refused the item";
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static RustBridge.FFIMaybeException ReceiveU32(IntPtr ctx, uint value)
        {
            try
            {
                var collector = Unsafe.AsRef<U32StreamCollector>((void*)ctx);
                collector.Values.Add(value);

                if (collector.Values.Count - 1 == collector.ThrowOnIndex)
                {
                    // Report the failure the way a real callback does - as a returned handle, never
                    // as an exception thrown through native code.
                    return RustBridge.FFIMaybeException.FromException(
                        new InvalidOperationException(collector.FailureMessage));
                }

                return RustBridge.FFIMaybeException.Ok();
            }
            catch (Exception ex)
            {
                return RustBridge.FFIMaybeException.FromException(ex);
            }
        }

        internal static IntPtr ReceiveU32Ptr => (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, RustBridge.FFIMaybeException>)&ReceiveU32;

        /// <summary>Forces a blocking, compacting collection from inside a Rust call.</summary>
        internal sealed class GcProvoker
        {
            internal int Invocations;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static RustBridge.FFIMaybeException ProvokeGc(IntPtr ctx)
        {
            try
            {
                var provoker = Unsafe.AsRef<GcProvoker>((void*)ctx);
                provoker.Invocations++;

                // Churn first so gen0 has something to relocate, then compact for real. Without the
                // churn a collection may find nothing worth moving and the probe proves little.
                for (var i = 0; i < 2048; i++)
                {
                    GC.KeepAlive(new byte[128]);
                }

                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                return RustBridge.FFIMaybeException.Ok();
            }
            catch (Exception ex)
            {
                return RustBridge.FFIMaybeException.FromException(ex);
            }
        }

        internal static IntPtr ProvokeGcPtr => (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, RustBridge.FFIMaybeException>)&ProvokeGc;

        /// <summary>The production callback that decodes a Rust-provided string into a container.</summary>
        internal static IntPtr WriteStringPtr => (IntPtr)RustBridge.FFIManagedStringWriter.WriteToStrPtr;

        /*
         * Helpers
         */

        /// <summary>
        /// Re-throws whatever a Rust export reported, and frees the handle either way.
        /// </summary>
        /// <remarks>
        /// Every export here returns an <c>FFIMaybeException</c> that owns a GCHandle when a managed
        /// sink failed. Leaving it unfreed would leak - and would be caught by
        /// <see cref="RustBridgeTestBase"/>'s handle-accounting check, which is the point.
        /// </remarks>
        internal static void ThrowIfFailed(RustBridge.FFIMaybeException result)
        {
            var res = result;
            try
            {
                RustBridge.ThrowIfException(ref res);
            }
            finally
            {
                RustBridge.FreeExceptionHandle(ref res);
            }
        }

        /// <summary>
        /// Asks Rust for a string and returns what the production decode produced.
        /// </summary>
        internal static string CollectString(
            Func<IntPtr, IntPtr, RustBridge.FFIMaybeException> call)
        {
            var container = new RustBridge.FFIManagedStringWriter.StringContainer();
            // The context must be the address of a slot in *this* frame, which then makes the
            // synchronous call below. See the remarks on this class.
            var result = call(WriteStringPtr, (IntPtr)Unsafe.AsPointer(ref container));
            ThrowIfFailed(result);
            return container.Value;
        }

        /// <summary>Asks Rust for a byte slice and returns a managed copy of it.</summary>
        internal static byte[] CollectBytes(Func<IntPtr, IntPtr, RustBridge.FFIMaybeException> call)
        {
            var collector = new ByteSliceCollector();
            var result = call(ReceiveByteSlicePtr, (IntPtr)Unsafe.AsPointer(ref collector));
            ThrowIfFailed(result);
            return collector.Bytes;
        }
    }

    /// <summary>
    /// Base fixture for the RustBridge tests. Asserts on teardown that every GCHandle handed to Rust
    /// during the test was reclaimed.
    /// </summary>
    /// <remarks>
    /// This turns every test in the suite into a leak test at no extra cost, and deterministically -
    /// no <c>GC.Collect</c> loop, no weak references, no dependence on JIT tier. It catches the
    /// failure mode that matters most here and that LeakSanitizer structurally cannot see: a GCHandle
    /// Rust never released (see <see cref="RustBridge.HandleAccounting"/>).
    /// </remarks>
    public abstract class RustBridgeTestBase : BaseUnitTest
    {
        private long _handleBaseline;

        [SetUp]
        public void CaptureHandleBaseline()
        {
            _handleBaseline = RustBridge.HandleAccounting.Live;
        }

        [TearDown]
        public void AssertNoHandlesLeaked()
        {
            // Async completions run on a tokio worker, so the freeing callback may land just after
            // the awaited Task completes. Allow a brief settle before declaring a leak.
            for (var i = 0; i < 50 && RustBridge.HandleAccounting.Live != _handleBaseline; i++)
            {
                System.Threading.Thread.Sleep(10);
            }

            Assert.AreEqual(
                _handleBaseline,
                RustBridge.HandleAccounting.Live,
                "GCHandles handed to Rust were not all reclaimed by the end of the test.");
        }
    }
}
