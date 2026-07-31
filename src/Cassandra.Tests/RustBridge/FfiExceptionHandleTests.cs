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
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    /// <summary>
    /// The managed half of the GCHandle wrappers: how a Rust-reported error is thrown and released, and
    /// how an empty handle behaves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RunWithIncrement</c> calls <c>ThrowIfException</c> inside a <c>try</c> and
    /// <c>FreeExceptionHandle</c> in the matching <c>finally</c>, so on the failure path both run. That
    /// is only safe because throwing resets the struct to empty, making the subsequent free a no-op
    /// rather than a double free. These tests pin that idempotence.
    /// </para>
    /// <para>
    /// Leak checking is not done here any more. <see cref="RustBridgeTestBase"/> asserts on teardown
    /// that every handle allocated during a test was reclaimed, which covers all of these
    /// automatically and deterministically. The three <c>WeakReference</c>-plus-<c>GC.Collect</c> tests
    /// this replaced depended on an unrooted local genuinely becoming collectable, which varies with
    /// JIT tier and debug codegen - flaky in the direction that hides bugs.
    /// </para>
    /// </remarks>
    [TestFixture, Category("unit")]
    public class FfiExceptionHandleTests : RustBridgeTestBase
    {
        [Test]
        public void Ok_HasNoException()
        {
            Assert.IsFalse(RustBridge.FFIMaybeException.Ok().HasException);
        }

        [Test]
        public void ThrowIfException_Ok_DoesNotThrow()
        {
            var ok = RustBridge.FFIMaybeException.Ok();
            NUnit.Framework.Assert.DoesNotThrow(() => RustBridge.ThrowIfException(ref ok));
        }

        [Test]
        public void ThrowIfException_ThrowsTheExactInstance_AndResetsToEmpty()
        {
            var original = new InvalidOperationException("failure from rust");
            var res = RustBridge.FFIMaybeException.FromException(original);
            Assert.IsTrue(res.HasException);

            var thrown = NUnit.Framework.Assert.Throws<InvalidOperationException>(
                () => RustBridge.ThrowIfException(ref res));

            // The same object, not a copy or a wrapper: callers match on exception identity and type.
            Assert.AreSame(original, thrown);

            // Reset to empty, so the finally-block free in RunWithIncrement cannot double-free.
            Assert.IsFalse(res.HasException);
            NUnit.Framework.Assert.DoesNotThrow(() => RustBridge.FreeExceptionHandle(ref res));
        }

        [Test]
        public void FreeExceptionHandle_FreesAndIsIdempotent()
        {
            var res = RustBridge.FFIMaybeException.FromException(new Exception("x"));
            Assert.IsTrue(res.HasException);

            RustBridge.FreeExceptionHandle(ref res);
            Assert.IsFalse(res.HasException);

            // A second call must be a safe no-op, not a double free.
            NUnit.Framework.Assert.DoesNotThrow(() => RustBridge.FreeExceptionHandle(ref res));
        }

        [Test]
        public void FreeExceptionHandle_Ok_IsANoOp()
        {
            var ok = RustBridge.FFIMaybeException.Ok();
            NUnit.Framework.Assert.DoesNotThrow(() => RustBridge.FreeExceptionHandle(ref ok));
            Assert.IsFalse(ok.HasException);
        }

        [Test]
        public void EmptyMaybeGCHandle_HasBothFieldsNull()
        {
            // Rust's Drop impl only calls the destructor when *both* the handle and the free pointer
            // are present, so an empty handle must zero both - not just the handle.
            var empty = RustBridge.FFIMaybeGCHandle.Empty();

            Assert.IsTrue(empty.IsEmpty());
            Assert.AreEqual(IntPtr.Zero, empty.gchandle);
            Assert.AreEqual(IntPtr.Zero, empty.free);
        }

        [Test]
        public void GCHandleWrapper_StoresTheHandleAndANonNullDestructor()
        {
            // Rust calls `free` unconditionally when dropping an FFIGCHandle, so a null there would be
            // an immediate crash rather than a leak.
            var handle = GCHandle.Alloc(new object());
            try
            {
                var ffi = new RustBridge.FFIGCHandle(handle);

                Assert.AreEqual(GCHandle.ToIntPtr(handle), ffi.gchandle);
                Assert.AreNotEqual(IntPtr.Zero, ffi.free);
            }
            finally
            {
                handle.Free();
                // Constructing the wrapper is what registers the handle as owned by Rust, so releasing
                // it by hand here has to be accounted for or the teardown check would report a leak.
                RustBridge.HandleAccounting.Released();
            }
        }
    }
}
