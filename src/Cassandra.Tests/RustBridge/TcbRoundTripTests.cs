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
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    /// <summary>
    /// Round-trips the async Task Control Block: C# builds a real <c>Tcb&lt;FFIBool&gt;</c>, hands it to
    /// Rust, and Rust dispatches back through the <c>complete_task</c> / <c>fail_task</c> function
    /// pointers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tests also stand in for a layout check on <c>Tcb&lt;R&gt;</c>. Its three function-pointer
    /// fields are private, so not even a friend assembly can take their offsets, and
    /// <see cref="System.Runtime.InteropServices.Marshal.OffsetOf(System.Type,string)"/> refuses
    /// generic structs anyway. But if <c>complete_task</c>, <c>fail_task</c> or <c>constructors</c> were
    /// at the wrong offset, Rust would call a garbage pointer and the process would die here - which
    /// is a stronger guarantee than an offset comparison.
    /// </para>
    /// <para>
    /// Both the synchronous and the worker-thread completion paths are covered. The asynchronous one is
    /// the shape production uses, and it is genuinely different: the GCHandle must survive past the
    /// P/Invoke that created it, and the managed continuation runs on a thread the CLR never saw enter.
    /// </para>
    /// </remarks>
    [TestFixture, Category("unit")]
    public class TcbRoundTripTests : RustBridgeTestBase
    {
        private static TaskCompletionSource<RustBridge.FFIBool> NewTcs() =>
            new TaskCompletionSource<RustBridge.FFIBool>(TaskCreationOptions.RunContinuationsAsynchronously);

        [Test]
        public async Task CompleteSynchronously_DeliversTheValue([Values(true, false)] bool value)
        {
            var tcs = NewTcs();
            var tcb = RustBridge.Tcb<RustBridge.FFIBool>.WithTcs(tcs);

            FfiTestExports.ffi_test_complete_bool_task(tcb, value);

            Assert.AreEqual(value, (bool)await tcs.Task.ConfigureAwait(false));
        }

        [Test]
        public async Task CompleteFromWorkerThread_DeliversTheValue([Values(true, false)] bool value)
        {
            var tcs = NewTcs();
            var tcb = RustBridge.Tcb<RustBridge.FFIBool>.WithTcs(tcs);

            // Returns immediately; a tokio worker completes the task afterwards.
            FfiTestExports.ffi_test_complete_bool_task_async(tcb, value);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)))
                                      .ConfigureAwait(false);
            Assert.AreSame(tcs.Task, completed, "Rust never completed the task from its worker thread");
            Assert.AreEqual(value, (bool)await tcs.Task.ConfigureAwait(false));
        }

        [Test]
        public void FailSynchronously_FaultsWithTheRustBuiltException()
        {
            var tcs = NewTcs();
            var tcb = RustBridge.Tcb<RustBridge.FFIBool>.WithTcs(tcs);

            unsafe
            {
                FfiTestExports.ffi_test_fail_bool_task(tcb, (IntPtr)RustBridge.Globals.ConstructorsPtr);
            }

            var ex = NUnit.Framework.Assert.ThrowsAsync<ArgumentException>(
                async () => await tcs.Task.ConfigureAwait(false));
            NUnit.Framework.Assert.That(ex.Message, Does.Contain("test async failure"));
        }

        [Test]
        public void FailFromWorkerThread_FaultsWithTheRustBuiltException()
        {
            var tcs = NewTcs();
            var tcb = RustBridge.Tcb<RustBridge.FFIBool>.WithTcs(tcs);

            unsafe
            {
                FfiTestExports.ffi_test_fail_bool_task_async(
                    tcb, (IntPtr)RustBridge.Globals.ConstructorsPtr);
            }

            // Also covers the constructors pointer surviving into the spawned task: building the
            // exception there dereferences the table long after the P/Invoke returned.
            var ex = NUnit.Framework.Assert.ThrowsAsync<ArgumentException>(
                async () => await tcs.Task.ConfigureAwait(false));
            NUnit.Framework.Assert.That(ex.Message, Does.Contain("test async failure"));
        }

        [Test]
        public async Task ManyConcurrentCompletions_AllDeliverTheirOwnValue()
        {
            // Rust completes these from several worker threads at once. A confusion between TCBs - or
            // a GCHandle freed by the wrong callback - shows up as a wrong value, a hang, or a crash,
            // none of which a single-task test can produce.
            const int count = 200;

            var sources = Enumerable.Range(0, count).Select(_ => NewTcs()).ToArray();
            for (var i = 0; i < count; i++)
            {
                var tcb = RustBridge.Tcb<RustBridge.FFIBool>.WithTcs(sources[i]);
                FfiTestExports.ffi_test_complete_bool_task_async(tcb, i % 2 == 0);
            }

            var all = Task.WhenAll(sources.Select(s => s.Task));
            var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)))
                                     .ConfigureAwait(false);
            Assert.AreSame(all, finished, "not every task was completed by Rust");

            var results = await all.ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                Assert.AreEqual(i % 2 == 0, (bool)results[i], $"task {i} got the wrong value");
            }
        }

        [Test]
        public void EchoBool_RoundTripsThroughRust([Values(true, false)] bool value)
        {
            // FFIBool as a plain by-value argument and return, rather than only as a task result.
            Assert.AreEqual(value, (bool)FfiTestExports.ffi_test_echo_bool(value));
        }

        [Test]
        public void BoolWireFormat_IsZeroOrOne()
        {
            // C# declares FFIBool as a single byte, so 0 and 1 are part of the ABI, not an
            // implementation detail. Rust reports the byte it decoded.
            Assert.AreEqual(1, FfiTestExports.ffi_test_bool_as_byte(true));
            Assert.AreEqual(0, FfiTestExports.ffi_test_bool_as_byte(false));
        }
    }
}
