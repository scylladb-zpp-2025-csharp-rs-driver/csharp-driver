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
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    /// <summary>
    /// Slice marshalling, with Rust producing the <c>(ptr, len)</c> pairs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every slice here is built by Rust's <c>FFISlice::new</c> over Rust-owned memory and received by
    /// C# as an <c>FFISliceRaw</c> - the non-generic twin that <c>[UnmanagedCallersOnly]</c> signatures
    /// are forced to use, since .NET will not treat a generic struct as blittable in that position.
    /// <c>As&lt;T&gt;()</c> then reinterprets it.
    /// </para>
    /// <para>
    /// That reinterpretation is the thing worth testing, and it can only be tested against a layout
    /// Rust actually produced. Fabricating an <c>FFISliceRaw</c> on the managed side - by declaring a
    /// struct with the same fields and <c>Unsafe.As</c>-ing it across - asserts a layout the test
    /// itself invented, so it passes even if both sides are wrong together.
    /// </para>
    /// </remarks>
    [TestFixture, Category("unit")]
    public unsafe class SliceMarshallingTests : RustBridgeTestBase
    {
        // Kind indices must match produced_bytes() in rust/src/ffi_test_exports.rs.
        private const byte EmptySlice = 0;
        private const byte SingleByte = 1;
        private const byte FourBytes = 2;
        private const byte LargeSlice = 3;

        private static byte[] ProduceBytes(byte kind) => FfiTestExports.CollectBytes(
            (cb, ctx) => FfiTestExports.ffi_test_produce_byte_slice(kind, cb, ctx));

        private static int RustLength(byte kind) => (int)FfiTestExports.ffi_test_produced_byte_slice_len(kind);

        [Test]
        public void KindCount_IsFullyCovered()
        {
            Assert.AreEqual(4, FfiTestExports.ffi_test_byte_slice_kind_count());
        }

        [Test]
        public void EveryKind_ArrivesWithTheLengthRustReports()
        {
            for (byte kind = 0; kind < FfiTestExports.ffi_test_byte_slice_kind_count(); kind++)
            {
                Assert.AreEqual(RustLength(kind), ProduceBytes(kind).Length, $"kind {kind}");
            }
        }

        [Test]
        public void EmptySlice_ArrivesAsEmpty()
        {
            // Rust's empty slice has a non-null but dangling pointer with length 0. ToSpan must take
            // the length-zero path rather than dereferencing it.
            Assert.IsEmpty(ProduceBytes(EmptySlice));
        }

        [Test]
        public void SingleByteSlice_ArrivesIntact()
        {
            NUnit.Framework.Legacy.CollectionAssert.AreEqual(new byte[] { 0xAB }, ProduceBytes(SingleByte));
        }

        [Test]
        public void FourByteSlice_ArrivesIntact()
        {
            NUnit.Framework.Legacy.CollectionAssert.AreEqual(
                new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, ProduceBytes(FourBytes));
        }

        [Test]
        public void LargeSlice_ArrivesIntact()
        {
            var bytes = ProduceBytes(LargeSlice);

            // Spans several pages with a non-repeating pattern, so a truncated or shifted copy is
            // visible rather than merely plausible.
            Assert.AreEqual(8192, bytes.Length);
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)(i % 251))
                {
                    Assert.Fail($"byte {i} is {bytes[i]}, expected {(byte)(i % 251)}");
                }
            }
        }

        [Test]
        public void U32Slice_ArrivesWithTheCorrectElementStride()
        {
            // A byte slice cannot catch a stride mistake, because sizeof(byte) is 1. This can: if
            // ToSpan used the wrong element size, or if the (ptr, len) pair were interpreted as bytes,
            // the values would be shredded rather than merely different.
            var collector = new FfiTestExports.U32SliceCollector();
            var result = FfiTestExports.ffi_test_produce_u32_slice(
                FfiTestExports.ReceiveU32SlicePtr, (IntPtr)Unsafe.AsPointer(ref collector));
            FfiTestExports.ThrowIfFailed(result);

            NUnit.Framework.Legacy.CollectionAssert.AreEqual(ExpectedU32Values(), collector.Values);
        }

        /// <summary>
        /// Asks Rust for the expected values one at a time, so the comparison never involves a literal
        /// duplicated on the managed side.
        /// </summary>
        private static uint[] ExpectedU32Values()
        {
            var stream = new FfiTestExports.U32StreamCollector();
            var result = FfiTestExports.ffi_test_expected_u32_values(
                FfiTestExports.ReceiveU32Ptr, (IntPtr)Unsafe.AsPointer(ref stream));
            FfiTestExports.ThrowIfFailed(result);
            return stream.Values.ToArray();
        }

        [Test]
        public void IpV4Octets_ArriveInNetworkOrder()
        {
            var octets = FfiTestExports.CollectBytes(
                (cb, ctx) => FfiTestExports.ffi_test_produce_ip_octets(false, cb, ctx));

            // Rust built these from 192.0.2.17 - the order is what an IPAddress ctor must accept.
            NUnit.Framework.Legacy.CollectionAssert.AreEqual(new byte[] { 192, 0, 2, 17 }, octets);
            Assert.AreEqual("192.0.2.17", new System.Net.IPAddress(octets).ToString());
        }

        [Test]
        public void IpV6Octets_ArriveInNetworkOrder()
        {
            var octets = FfiTestExports.CollectBytes(
                (cb, ctx) => FfiTestExports.ffi_test_produce_ip_octets(true, cb, ctx));

            // Both arms of IpOctets are exercised, so a v4/v6 mix-up (4 vs 16 bytes) cannot hide.
            Assert.AreEqual(16, octets.Length);
            Assert.AreEqual("2001:db8::dead:beef", new System.Net.IPAddress(octets).ToString());
        }

        [Test]
        public void ToSpan_AliasesRustMemoryRatherThanCopying()
        {
            // FFISlice is documented as zero-copy, and callers rely on that: a copy would be a silent
            // performance regression on every row. Two spans over the same Rust slice must therefore
            // report the same address.
            var first = CaptureSpanAddress();
            var second = CaptureSpanAddress();
            Assert.AreEqual(first, second,
                "repeated views of the same Rust slice must alias the same memory");
        }

        private static IntPtr CaptureSpanAddress()
        {
            var collector = new AddressCollector();
            var result = FfiTestExports.ffi_test_produce_byte_slice(
                FourBytes, AddressCollector.SinkPtr, (IntPtr)Unsafe.AsPointer(ref collector));
            FfiTestExports.ThrowIfFailed(result);
            return collector.Address;
        }

        /// <summary>Records the address <c>ToSpan()</c> hands out for a Rust-owned slice.</summary>
        private sealed unsafe class AddressCollector
        {
            internal IntPtr Address;

            [System.Runtime.InteropServices.UnmanagedCallersOnly(
                CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
            private static RustBridge.FFIMaybeException Receive(RustBridge.FFISliceRaw raw, IntPtr ctx)
            {
                try
                {
                    var collector = Unsafe.AsRef<AddressCollector>((void*)ctx);
                    var span = raw.As<byte>().ToSpan();
                    collector.Address = (IntPtr)Unsafe.AsPointer(ref span.GetPinnableReference());
                    return RustBridge.FFIMaybeException.Ok();
                }
                catch (Exception ex)
                {
                    return RustBridge.FFIMaybeException.FromException(ex);
                }
            }

            internal static IntPtr SinkPtr =>
                (IntPtr)(delegate* unmanaged[Cdecl]<RustBridge.FFISliceRaw, IntPtr, RustBridge.FFIMaybeException>)&Receive;
        }
    }
}
