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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    /// <summary>
    /// Verifies that managed memory handed to Rust does not move while Rust holds a pointer to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AddressSanitizer cannot check this. The CLR's GC heap is not tracked by ASAN - segments arrive
    /// via <c>mmap</c> and the collector sub-allocates and relocates objects inside them - so a
    /// compacting collection moving an object out from under a native pointer is, to the sanitizer, a
    /// perfectly legal write to perfectly legal memory. Nor does Rust's borrow checker help: the
    /// lifetime on <c>FFISlice&lt;'a, T&gt;</c> constrains Rust's use of the pointer, not the CLR's
    /// freedom to move what it points at.
    /// </para>
    /// <para>
    /// So the check has to be empirical. Rust records the address it was given and hashes the bytes,
    /// calls back into C# (which forces a blocking, compacting collection after churning gen0), then
    /// re-reads <em>the same address</em>. For pinned memory both hashes must match. For unpinned
    /// memory they generally will not - see <see cref="UnpinnedBuffer_IsObservablyUnsafe"/>, the
    /// negative control that keeps this test honest.
    /// </para>
    /// </remarks>
    [TestFixture, Category("unit")]
    public unsafe class GcMovementTests : RustBridgeTestBase
    {
        private static byte[] MakePayload()
        {
            // Distinct, non-repeating content, so relocation shows up as a changed hash rather than
            // coincidentally identical bytes.
            var payload = new byte[4096];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)((i * 31 + 7) % 256);
            }

            return payload;
        }

        [Test]
        public void PinnedBuffer_DoesNotMoveAcrossACompactingCollection()
        {
            var payload = MakePayload();
            var provoker = new FfiTestExports.GcProvoker();

            var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try
            {
                var addressBefore = pin.AddrOfPinnedObject();
                var slice = new RustBridge.FFISlice<byte>(addressBefore, (nuint)payload.Length);

                var probe = FfiTestExports.ffi_test_gc_move_probe(
                    slice, FfiTestExports.ProvokeGcPtr, (IntPtr)Unsafe.AsPointer(ref provoker));

                Assert.AreEqual(1, provoker.Invocations, "Rust did not call back into managed code");

                // Rust saw the address we pinned...
                Assert.AreEqual((nuint)addressBefore, probe.Addr);
                // ...the CLR still reports the same address after compaction...
                Assert.AreEqual(addressBefore, pin.AddrOfPinnedObject(),
                    "a pinned object must not be relocated");
                // ...and the bytes at that address are unchanged, which is the property Rust relies on.
                Assert.AreEqual(probe.HashBefore, probe.HashAfter,
                    "contents at the pinned address changed across a compacting GC");
            }
            finally
            {
                pin.Free();
            }
        }

        [Test]
        public void PinnedBufferContents_SurviveTheRoundTrip()
        {
            // Complements the hash check: confirms the managed array itself is still correct, so a
            // matching hash cannot be explained by Rust reading a buffer that was zeroed on both reads.
            var payload = MakePayload();
            var provoker = new FfiTestExports.GcProvoker();

            var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try
            {
                var slice = new RustBridge.FFISlice<byte>(pin.AddrOfPinnedObject(), (nuint)payload.Length);
                FfiTestExports.ffi_test_gc_move_probe(
                    slice, FfiTestExports.ProvokeGcPtr, (IntPtr)Unsafe.AsPointer(ref provoker));
            }
            finally
            {
                pin.Free();
            }

            for (var i = 0; i < payload.Length; i++)
            {
                if (payload[i] != (byte)((i * 31 + 7) % 256))
                {
                    Assert.Fail($"payload byte {i} was corrupted across the probe");
                }
            }
        }

        [Test]
        public void ProductionUsesOnlyNonMovingMemoryForTheConstructorTable()
        {
            // The constructor table is the one pointer Rust holds indefinitely (as
            // `&'static ExceptionConstructors`), so it must not live on the managed heap at all. It is
            // a NativeMemory allocation that is intentionally never freed; assert it is stable across
            // a compacting collection rather than trusting the comment that says so.
            unsafe
            {
                var before = (IntPtr)RustBridge.Globals.ConstructorsPtr;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                Assert.AreEqual(before, (IntPtr)RustBridge.Globals.ConstructorsPtr);
            }
        }

        /// <summary>
        /// The negative control: an <em>unpinned</em> array's address is not stable, so the probe must
        /// be able to observe the difference. Without this, a passing pinned test proves nothing - it
        /// could equally mean the probe has no teeth.
        /// </summary>
        /// <remarks>
        /// Marked Explicit because it deliberately hands Rust a pointer that may be invalidated
        /// mid-call. That is undefined behaviour, so it must never run as part of the normal suite:
        /// the memory may be reused, unmapped, or - if the GC happens not to relocate this particular
        /// array - unchanged, making the outcome inherently non-deterministic. Run it by hand
        /// (<c>dotnet test --filter UnpinnedBuffer_IsObservablyUnsafe</c>) when changing the probe, to
        /// confirm it can still detect movement.
        /// </remarks>
        [Test, Explicit("Deliberately passes unpinned memory to Rust; outcome is non-deterministic by design.")]
        public void UnpinnedBuffer_IsObservablyUnsafe()
        {
            var payload = MakePayload();
            var provoker = new FfiTestExports.GcProvoker();

            unsafe
            {
                // No pin: Unsafe.AsPointer on a heap array is exactly the mistake this guards against.
                var address = (IntPtr)Unsafe.AsPointer(ref payload[0]);
                var slice = new RustBridge.FFISlice<byte>(address, (nuint)payload.Length);

                var probe = FfiTestExports.ffi_test_gc_move_probe(
                    slice, FfiTestExports.ProvokeGcPtr, (IntPtr)Unsafe.AsPointer(ref provoker));

                var movedAddress = (IntPtr)Unsafe.AsPointer(ref payload[0]);
                TestContext.Out.WriteLine(
                    $"address handed to Rust: 0x{address:x}, address after GC: 0x{movedAddress:x}, " +
                    $"hash before: {probe.HashBefore}, hash after: {probe.HashAfter}");

                Assert.IsTrue(
                    movedAddress != address || probe.HashBefore != probe.HashAfter,
                    "the GC did not relocate the array this run, so the probe could not demonstrate " +
                    "movement. Re-run, or raise the allocation churn in ProvokeGc.");
            }
        }
    }
}
