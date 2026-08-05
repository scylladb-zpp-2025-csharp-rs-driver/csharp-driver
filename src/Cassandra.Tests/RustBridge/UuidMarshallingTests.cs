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
    /// Checks <c>GuidToFFIFormat</c> / <c>GuidFromFFIFormat</c> against Rust's <c>uuid</c> crate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These helpers exist because .NET's default <see cref="Guid"/> byte order is mixed-endian (the
    /// first three fields little-endian on a little-endian host) while RFC 4122 - and the <c>uuid</c>
    /// crate - expect network order throughout. Getting it wrong means
    /// <c>WaitForSchemaAgreement</c> targets a host that does not exist.
    /// </para>
    /// <para>
    /// Round-tripping a Guid through .NET's own inverse cannot detect that: both halves make the same
    /// assumption, so any byte order round-trips cleanly. The previous fixture did exactly that, 500
    /// times with random values, and could not have failed for a byte-order bug. Asking Rust which
    /// UUID it sees is the only assertion that means anything here.
    /// </para>
    /// </remarks>
    [TestFixture, Category("unit")]
    public class UuidMarshallingTests : RustBridgeTestBase
    {
        /// <summary>
        /// Vectors where mixed-endian and network order visibly disagree, so a byte-order regression
        /// changes the result rather than merely reshuffling indistinguishable bytes. (An all-zero or
        /// all-ones UUID is byte-order agnostic and proves nothing.)
        /// </summary>
        private static readonly string[] Vectors =
        {
            "00112233-4455-6677-8899-aabbccddeeff",
            "550e8400-e29b-41d4-a716-446655440000",
            "01020304-0506-0708-090a-0b0c0d0e0f10",
            "ffffffff-ffff-ffff-ffff-ffffffffffff",
            "00000000-0000-0000-0000-000000000000",
        };

        /// <summary>Sends a Guid to Rust in FFI format and returns the UUID Rust parsed.</summary>
        private static string RustSees(Guid guid)
        {
            var buffer = new byte[16];
            RustBridge.GuidToFFIFormat(guid, buffer);

            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var slice = new RustBridge.FFISlice<byte>(pin.AddrOfPinnedObject(), 16);
                return FfiTestExports.CollectString(
                    (cb, ctx) => FfiTestExports.ffi_test_uuid_to_string(slice, cb, ctx));
            }
            finally
            {
                pin.Free();
            }
        }

        /// <summary>Asks Rust to encode a canonical UUID and returns the 16 bytes it produced.</summary>
        private static byte[] RustEncodes(string canonical)
        {
            var textBytes = Encoding.UTF8.GetBytes(canonical);
            var pin = GCHandle.Alloc(textBytes, GCHandleType.Pinned);
            try
            {
                var text = new RustBridge.FFIString(pin.AddrOfPinnedObject(), (nuint)textBytes.Length);
                return FfiTestExports.CollectBytes(
                    (cb, ctx) => FfiTestExports.ffi_test_uuid_to_bytes(text, cb, ctx));
            }
            finally
            {
                pin.Free();
            }
        }

        [Test]
        public void GuidToFFIFormat_ProducesTheUuidRustExpects()
        {
            foreach (var vector in Vectors)
            {
                var guid = Guid.Parse(vector);
                Assert.AreEqual(vector, RustSees(guid),
                    $"Rust parsed a different UUID than {vector} was meant to encode.");
            }
        }

        [Test]
        public void GuidFromFFIFormat_DecodesWhatRustEncodes()
        {
            // The reverse direction: Rust produces the bytes, C# must recover the same UUID.
            foreach (var vector in Vectors)
            {
                var bytes = RustEncodes(vector);
                Assert.AreEqual(16, bytes.Length);
                Assert.AreEqual(Guid.Parse(vector), RustBridge.GuidFromFFIFormat(bytes));
            }
        }

        [Test]
        public void GuidToFFIFormat_AgreesWithRustsEncoding()
        {
            // Both encoders, same input, compared byte for byte. This is the assertion that pins the
            // endianness rather than assuming it.
            foreach (var vector in Vectors)
            {
                var managed = new byte[16];
                RustBridge.GuidToFFIFormat(Guid.Parse(vector), managed);

                NUnit.Framework.Legacy.CollectionAssert.AreEqual(RustEncodes(vector), managed,
                    $"managed and Rust encodings of {vector} differ");
            }
        }

        [Test]
        public void FFIFormat_IsNotDotNetsNativeByteOrder()
        {
            // Guards the helpers against being "simplified" into Guid.ToByteArray(). If that ever
            // happens this test fails, rather than schema agreement quietly targeting a random host.
            var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

            var ffi = new byte[16];
            RustBridge.GuidToFFIFormat(guid, ffi);

            NUnit.Framework.Legacy.CollectionAssert.AreNotEqual(guid.ToByteArray(), ffi,
                "FFI format must differ from .NET's mixed-endian layout for this vector");
            NUnit.Framework.Legacy.CollectionAssert.AreEqual(
                new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
                             0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF },
                ffi);
        }
    }
}
