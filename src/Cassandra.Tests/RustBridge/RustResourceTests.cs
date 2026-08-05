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
using System.Threading.Tasks;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    /// <summary>
    /// Exercises <see cref="RustResource"/> against a genuinely Rust-allocated resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resource comes from <c>ffi_test_make_resource</c>, which allocates an <c>Arc</c> in Rust and
    /// returns a real <c>ManuallyDestructible</c> whose destructor is the production
    /// <c>ArcFFI</c>-based one. Rust also exposes a live-allocation count, so "the destructor ran
    /// exactly once" is asserted against Rust's own bookkeeping rather than inferred.
    /// </para>
    /// <para>
    /// The previous version used a fabricated handle (the constant <c>0x1000</c>) and a C#
    /// "destructor" that incremented a managed counter. That covers
    /// <see cref="System.Runtime.InteropServices.SafeHandle"/>'s behaviour but nothing about the
    /// bridge: the pointer was never valid, no Rust memory was ever freed, and the test would pass
    /// unchanged if <c>ManuallyDestructible</c>'s two fields were transposed - in which case production
    /// would call the handle as a function pointer.
    /// </para>
    /// </remarks>
    [TestFixture, Category("unit")]
    public class RustResourceTests : RustBridgeTestBase
    {
        private const ulong Payload = 0xFEEDFACEDEADBEEF;

        /// <summary>Minimal concrete <see cref="RustResource"/> over a Rust-owned test allocation.</summary>
        private sealed class TestRustResource : RustResource
        {
            internal TestRustResource(RustBridge.ManuallyDestructible md) : base(md)
            {
            }

            internal static TestRustResource Allocate(ulong value = Payload)
            {
                return new TestRustResource(FfiTestExports.ffi_test_make_resource(value));
            }

            /// <summary>The raw handle, for handing back to Rust in a read-back check.</summary>
            internal IntPtr Handle => DangerousGetHandle();
        }

        private static int LiveResources() => (int)FfiTestExports.ffi_test_live_resources();

        [SetUp]
        public void AssertCleanStart()
        {
            Assert.AreEqual(0, LiveResources(), "a previous test leaked a Rust test resource");
        }

        [Test]
        public void Allocate_ProducesAValidNonNullHandle()
        {
            using var res = TestRustResource.Allocate();

            Assert.IsFalse(res.IsInvalid);
            Assert.AreEqual(1, LiveResources());
        }

        [Test]
        public void Handle_PointsAtTheRustObject()
        {
            // Reads the payload back through ArcFFI::as_ref on the Rust side. If ManuallyDestructible
            // were marshalled wrongly - fields transposed, or the pointer truncated - this returns
            // garbage or trips the sanitizer, instead of quietly appearing to work.
            using var res = TestRustResource.Allocate();

            Assert.AreEqual(Payload, FfiTestExports.ffi_test_resource_value(res.Handle));
        }

        [Test]
        public void IsInvalid_IsTrueForANullHandle()
        {
            // The null case is the one that still needs a hand-built struct: Rust has no way to hand
            // out a null resource, and this asserts only the managed IsInvalid predicate.
            using var res = new TestRustResource(new RustBridge.ManuallyDestructible(IntPtr.Zero, IntPtr.Zero));

            Assert.IsTrue(res.IsInvalid);
        }

        [Test]
        public void Dispose_RunsTheRustDestructorExactlyOnce()
        {
            var res = TestRustResource.Allocate();
            Assert.AreEqual(1, LiveResources());

            res.Dispose();
            Assert.AreEqual(0, LiveResources(), "Dispose must free the Rust allocation");

            // SafeHandle guarantees release-once. If it did not, this would be a double free - which
            // under ASAN aborts with a report rather than merely returning a wrong count.
            res.Dispose();
            Assert.AreEqual(0, LiveResources(), "a second Dispose must not free again");
        }

        [Test]
        public void ReferenceCount_DefersReleaseUntilBalanced()
        {
            var res = TestRustResource.Allocate();

            Assert.IsTrue(res.TryIncreaseReferenceCount());

            // With a reference outstanding, disposing must not free the Rust object yet - this is what
            // keeps a handle valid for the duration of an in-flight native call.
            res.Dispose();
            Assert.AreEqual(1, LiveResources(), "release must be deferred while a reference is held");

            // The pointer is therefore still valid, so Rust can still read through it.
            Assert.AreEqual(Payload, FfiTestExports.ffi_test_resource_value(res.Handle));

            res.DecreaseReferenceCount();
            Assert.AreEqual(0, LiveResources(), "releasing the last reference must free exactly once");
        }

        [Test]
        public void RunWithIncrement_PassesTheRealHandleAndKeepsItAlive()
        {
            using var res = TestRustResource.Allocate();
            var observed = 0UL;

            res.RunWithIncrement(handle =>
            {
                // The handle the helper passes must be usable by Rust for the duration of the call.
                observed = FfiTestExports.ffi_test_resource_value(handle);
                return RustBridge.FFIMaybeException.Ok();
            });

            Assert.AreEqual(Payload, observed);
            Assert.AreEqual(1, LiveResources(), "the resource must outlive a synchronous call");
        }

        [Test]
        public void RunWithIncrement_ThrowsAndFreesWhenTheNativeCallReports()
        {
            using var res = TestRustResource.Allocate();

            // The throw-then-free-in-finally path. The exception must surface, and its GCHandle must be
            // released exactly once - the base fixture's accounting check enforces the second half.
            var ex = NUnit.Framework.Assert.Throws<InvalidOperationException>(() =>
                res.RunWithIncrement(_ => RustBridge.FFIMaybeException.FromException(
                    new InvalidOperationException("native call failed"))));

            Assert.AreEqual("native call failed", ex.Message);
        }

        [Test]
        public async Task RunAsyncWithIncrement_CompletesThroughRust()
        {
            using var res = TestRustResource.Allocate();

            // Rust completes the task from a tokio worker after the native call has returned, which is
            // the production shape. Previously this was simulated by invoking the managed CompleteTask
            // directly, so it never crossed the boundary at all.
            var task = res.RunAsyncWithIncrement<RustBridge.FFIBool>((tcb, handle) =>
            {
                Assert.AreEqual(res.Handle, handle, "the native call received an unexpected handle");
                FfiTestExports.ffi_test_complete_bool_task_async(tcb, true);
            });

            Assert.IsTrue((bool)await task.ConfigureAwait(false));
        }

        [Test]
        public void RunAsyncWithIncrement_FaultsThroughRust()
        {
            using var res = TestRustResource.Allocate();

            var task = res.RunAsyncWithIncrement<RustBridge.FFIBool>((tcb, _) =>
            {
                unsafe
                {
                    FfiTestExports.ffi_test_fail_bool_task_async(
                        tcb, (IntPtr)RustBridge.Globals.ConstructorsPtr);
                }
            });

            var ex = NUnit.Framework.Assert.ThrowsAsync<ArgumentException>(
                async () => await task.ConfigureAwait(false));
            NUnit.Framework.Assert.That(ex.Message, Does.Contain("test async failure"));
        }
    }
}
