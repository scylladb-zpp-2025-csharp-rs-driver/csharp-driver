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
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    /// <summary>
    /// String marshalling in both directions, with Rust owning the bytes.
    /// </summary>
    /// <remarks>
    /// Every string produced here is a Rust <c>&amp;str</c> handed over as an <c>FFIStr</c> and decoded
    /// by the production <c>FFIManagedStringWriter</c> callback. Nothing on the managed side encodes
    /// the bytes being tested, so a mistake in either encoder shows up rather than cancelling out.
    /// The kinds are chosen for the specific ways a UTF-8 bridge breaks - see
    /// <c>ffi_test_exports.rs::produced_str</c>.
    /// </remarks>
    [TestFixture, Category("unit")]
    public class StringMarshallingTests : RustBridgeTestBase
    {
        // Kind indices must match produced_str() in rust/src/ffi_test_exports.rs.
        private const byte Empty = 0;
        private const byte Ascii = 1;
        private const byte MultiByte = 2;
        private const byte Astral = 3;
        private const byte EmbeddedNul = 4;
        private const byte Long = 5;
        private const byte Null = 6;

        private static string Produce(byte kind) => FfiTestExports.CollectString(
            (cb, ctx) => FfiTestExports.ffi_test_produce_str(kind, cb, ctx));

        private static int RustByteLength(byte kind) => (int)FfiTestExports.ffi_test_produced_str_len(kind);

        [Test]
        public void KindCount_IsFullyCovered()
        {
            // Fails if a kind is added in Rust without a case here, rather than leaving it untested.
            Assert.AreEqual(7, FfiTestExports.ffi_test_str_kind_count());
        }

        [Test]
        public void NullString_DecodesToNull_NotEmpty()
        {
            // The distinction matters: a null FFIStr means "absent" (no keyspace selected), an empty
            // one means "present but empty". Collapsing them would silently change behaviour.
            Assert.IsNull(Produce(Null));
        }

        [Test]
        public void EmptyString_DecodesToEmpty_NotNull()
        {
            Assert.AreEqual(string.Empty, Produce(Empty));
        }

        [Test]
        public void AsciiString_RoundTrips()
        {
            Assert.AreEqual("SELECT * FROM system.peers", Produce(Ascii));
        }

        [Test]
        public void MultiByteString_RoundTrips()
        {
            var decoded = Produce(MultiByte);
            Assert.AreEqual("café / 日本語", decoded);
            // Cross-check the two encoders: Rust's byte count must equal .NET's for the same string.
            Assert.AreEqual(RustByteLength(MultiByte), Encoding.UTF8.GetByteCount(decoded));
            Assert.Greater(RustByteLength(MultiByte), decoded.Length);
        }

        [Test]
        public void AstralString_RoundTrips_AsSurrogatePairs()
        {
            var decoded = Produce(Astral);

            // Three scalars outside the BMP: 4 UTF-8 bytes each, and a surrogate pair each in UTF-16.
            Assert.AreEqual("\U0001F600\U0001F680\U0010FFFF", decoded);
            Assert.AreEqual(12, RustByteLength(Astral));
            Assert.AreEqual(6, decoded.Length, "each astral scalar must decode to a surrogate pair");
        }

        [Test]
        public void StringWithInteriorNul_KeepsEveryByte()
        {
            // The bug this exists for: a decode that stops at the first NUL instead of honouring the
            // length would return "a" and look perfectly reasonable.
            var decoded = Produce(EmbeddedNul);
            Assert.AreEqual("a\0b", decoded);
            Assert.AreEqual(3, decoded.Length);
            Assert.AreEqual(3, RustByteLength(EmbeddedNul));
        }

        [Test]
        public void LongString_RoundTripsCompletely()
        {
            var decoded = Produce(Long);
            Assert.AreEqual(RustByteLength(Long), Encoding.UTF8.GetByteCount(decoded));
            // Spans multiple pages, so a truncating copy shows up as a length mismatch.
            Assert.Greater(RustByteLength(Long), 16384);
            NUnit.Framework.Assert.That(decoded, Does.StartWith("scÿlla-"));
            NUnit.Framework.Assert.That(decoded, Does.EndWith("scÿlla-"));
        }

        [Test]
        public void ManagedString_SurvivesARoundTripThroughRust()
        {
            // The other direction: C# -> Rust as an FFIStr, echoed back through the same callback.
            const string original = "café / 日本語 keyspace \U0001F600";
            var bytes = Encoding.UTF8.GetBytes(original);

            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var input = new RustBridge.FFIString(pin.AddrOfPinnedObject(), (nuint)bytes.Length);
                Assert.AreEqual(original, FfiTestExports.CollectString(
                    (cb, ctx) => FfiTestExports.ffi_test_echo_str(input, cb, ctx)));
            }
            finally
            {
                pin.Free();
            }
        }

        [Test]
        public void ManagedByteSlice_SurvivesARoundTripAsAString()
        {
            // Exercises the FFISlice<u8> -> FFIStr conversion Rust performs when handing
            // variable-length data to a managed string callback.
            const string original = "hello from a byte slice";
            var bytes = Encoding.UTF8.GetBytes(original);

            unsafe
            {
                fixed (byte* ptr = bytes)
                {
                    var slice = new RustBridge.FFISlice<byte>((IntPtr)ptr, (nuint)bytes.Length);
                    Assert.AreEqual(original, FfiTestExports.CollectString(
                        (cb, ctx) => FfiTestExports.ffi_test_echo_slice_as_str(slice, cb, ctx)));
                }
            }
        }

        [Test]
        public void NulTerminatedManagedString_SurvivesARoundTripThroughRust()
        {
            // The CSharpStr path, where Rust derives the length itself with CStr::from_ptr. Used
            // throughout metadata.rs for keyspace, table and UDT names, and previously untested.
            const string original = "my_keyspace_ünïcode";
            var native = Marshal.StringToCoTaskMemUTF8(original);
            try
            {
                Assert.AreEqual(original, FfiTestExports.CollectString(
                    (cb, ctx) => FfiTestExports.ffi_test_echo_cstr(native, cb, ctx)));
            }
            finally
            {
                Marshal.ZeroFreeCoTaskMemUTF8(native);
            }
        }

        [Test]
        public void NulTerminatedManagedString_IsTruncatedAtAnInteriorNul()
        {
            // Not a bug to fix, a constraint to pin: the CSharpStr path cannot carry an interior NUL,
            // because Rust has only the terminator to go on. Documenting it here means a future
            // caller who needs NUL-safe names finds out from a test rather than from production.
            var bytes = Encoding.UTF8.GetBytes("keep\0drop\0");
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                Assert.AreEqual("keep", FfiTestExports.CollectString(
                    (cb, ctx) => FfiTestExports.ffi_test_echo_cstr(pin.AddrOfPinnedObject(), cb, ctx)));
            }
            finally
            {
                pin.Free();
            }
        }

        [Test]
        public void NullCSharpStr_DecodesToNull()
        {
            Assert.IsNull(FfiTestExports.CollectString(
                (cb, ctx) => FfiTestExports.ffi_test_echo_cstr(IntPtr.Zero, cb, ctx)));
        }
    }
}
