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
    /// Covers <c>ffi_callback_for_each</c>, the helper Rust uses to stream a collection into a managed
    /// callback one item at a time instead of materialising a <c>Vec</c>.
    /// </summary>
    /// <remarks>
    /// It carries real control flow - stop on the first exception the callback returns, and propagate
    /// that exception unchanged - and it is used for every keyspace, table, column and node list in
    /// <c>metadata.rs</c>. It previously had no test at all. The early-abort path in particular is one
    /// the type system cannot express: nothing stops the loop from swallowing the exception and
    /// carrying on.
    /// </remarks>
    [TestFixture, Category("unit")]
    public unsafe class CallbackIterationTests : RustBridgeTestBase
    {
        [Test]
        public void AllItems_AreDeliveredInOrder()
        {
            var collector = new FfiTestExports.U32StreamCollector();
            var result = FfiTestExports.ffi_test_for_each_u32(
                5, FfiTestExports.ReceiveU32Ptr, (IntPtr)Unsafe.AsPointer(ref collector));
            FfiTestExports.ThrowIfFailed(result);

            NUnit.Framework.Legacy.CollectionAssert.AreEqual(new uint[] { 0, 1, 2, 3, 4 }, collector.Values);
        }

        [Test]
        public void EmptyIteration_InvokesNothingAndSucceeds()
        {
            var collector = new FfiTestExports.U32StreamCollector();
            var result = FfiTestExports.ffi_test_for_each_u32(
                0, FfiTestExports.ReceiveU32Ptr, (IntPtr)Unsafe.AsPointer(ref collector));
            FfiTestExports.ThrowIfFailed(result);

            Assert.IsEmpty(collector.Values);
        }

        [Test]
        public void CallbackFailure_StopsIterationImmediately()
        {
            var collector = new FfiTestExports.U32StreamCollector
            {
                ThrowOnIndex = 3,
                FailureMessage = "refused item 3",
            };

            var result = FfiTestExports.ffi_test_for_each_u32(
                100, FfiTestExports.ReceiveU32Ptr, (IntPtr)Unsafe.AsPointer(ref collector));

            // The exception the managed callback returned must come back out unchanged...
            var ex = NUnit.Framework.Assert.Throws<InvalidOperationException>(
                () => FfiTestExports.ThrowIfFailed(result));
            NUnit.Framework.Assert.That(ex.Message, Does.Contain("refused item 3"));

            // ...and iteration must have stopped there, not run all 100 items.
            Assert.AreEqual(4, collector.Values.Count,
                "iteration must stop at the failing item, not continue past it");
            NUnit.Framework.Legacy.CollectionAssert.AreEqual(new uint[] { 0, 1, 2, 3 }, collector.Values);
        }

        [Test]
        public void FailureOnTheFirstItem_StopsBeforeTheSecond()
        {
            var collector = new FfiTestExports.U32StreamCollector { ThrowOnIndex = 0 };

            var result = FfiTestExports.ffi_test_for_each_u32(
                10, FfiTestExports.ReceiveU32Ptr, (IntPtr)Unsafe.AsPointer(ref collector));

            NUnit.Framework.Assert.Throws<InvalidOperationException>(
                () => FfiTestExports.ThrowIfFailed(result));
            Assert.AreEqual(1, collector.Values.Count);
        }

        [Test]
        public void FailureOnTheLastItem_IsStillPropagated()
        {
            // Boundary case: an off-by-one in the loop would drop the last item's result and report
            // success for a failed iteration.
            var collector = new FfiTestExports.U32StreamCollector { ThrowOnIndex = 4 };

            var result = FfiTestExports.ffi_test_for_each_u32(
                5, FfiTestExports.ReceiveU32Ptr, (IntPtr)Unsafe.AsPointer(ref collector));

            NUnit.Framework.Assert.Throws<InvalidOperationException>(
                () => FfiTestExports.ThrowIfFailed(result));
            Assert.AreEqual(5, collector.Values.Count);
        }
    }
}
