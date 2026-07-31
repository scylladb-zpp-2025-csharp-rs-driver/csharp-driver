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
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    /// <summary>
    /// Drives every slot of the real exception constructor table from Rust and checks which managed
    /// exception came back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the highest-risk structure in the bridge. <c>Constructors</c> is filled by a
    /// constructor taking 23 positional <see cref="IntPtr"/> arguments, and 21 of the 23 slots have
    /// the identical native signature <c>(FFIString) -&gt; FFIGCHandle</c>. Transpose any two - in the
    /// Rust struct, in the C# struct, or in that 23-argument initializer - and everything still
    /// compiles, every layout check still passes, and production silently throws the wrong exception
    /// type for the rest of the driver's life.
    /// </para>
    /// <para>
    /// No two slots map to the same managed exception type, so asserting the <em>exact</em> type
    /// returned by each slot detects any transposition. The previous version of this test covered 4
    /// of the 23 slots; this one covers all of them and fails if the count ever grows without the map
    /// below being updated.
    /// </para>
    /// </remarks>
    [TestFixture, Category("unit")]
    public class ExceptionTableTests : RustBridgeTestBase
    {
        /// <summary>
        /// Managed exception type expected from each slot, in the table's declaration order (which is
        /// alphabetical by field name - Rust asserts that separately in <c>task.rs</c>).
        /// </summary>
        private static readonly Type[] ExpectedBySlot =
        {
            typeof(AlreadyExistsException),
            typeof(AlreadyShutdownException),
            typeof(ArgumentException),
            typeof(DeserializationException),
            typeof(FunctionFailureException),
            typeof(InvalidArgumentException),
            typeof(InvalidConfigurationInQueryException),
            typeof(InvalidQueryException),
            typeof(InvalidTypeException),
            typeof(NoHostAvailableException),
            typeof(OperationTimedOutException),
            typeof(PreparedQueryNotFoundException),
            typeof(RequestInvalidException),
            typeof(RustException),
            typeof(SchemaAgreementRequiredHostAbsentException),
            typeof(SchemaAgreementRowsResultException),
            typeof(SchemaAgreementSingleRowException),
            typeof(SchemaAgreementTimeoutException),
            typeof(SerializationException),
            typeof(SyntaxError),
            typeof(TraceRetrievalException),
            typeof(TruncateException),
            typeof(UnauthorizedException),
        };

        /// <summary>
        /// Recovers the managed exception Rust built and releases the GCHandle it handed over, exactly
        /// as production does when it throws or discards one.
        /// </summary>
        private static Exception BuildViaSlot(int slot)
        {
            RustBridge.FFIGCHandle ffiHandle;
            unsafe
            {
                ffiHandle = FfiTestExports.ffi_test_build_exception(
                    (nuint)slot, (IntPtr)RustBridge.Globals.ConstructorsPtr);
            }

            var handle = GCHandle.FromIntPtr(ffiHandle.gchandle);
            try
            {
                return (Exception)handle.Target;
            }
            finally
            {
                handle.Free();
                RustBridge.HandleAccounting.Released();
            }
        }

        private static string SlotName(int slot) => FfiTestExports.CollectString(
            (cb, ctx) => FfiTestExports.ffi_test_exception_slot_name((nuint)slot, cb, ctx));

        private static string SlotMarker(int slot) => FfiTestExports.CollectString(
            (cb, ctx) => FfiTestExports.ffi_test_exception_slot_marker((nuint)slot, cb, ctx));

        [Test]
        public void SlotCount_MatchesTheManagedTable()
        {
            var rustCount = (int)FfiTestExports.ffi_test_exception_slot_count();

            Assert.AreEqual(rustCount, ExpectedBySlot.Length,
                "A constructor slot was added or removed in Rust without updating ExpectedBySlot, " +
                "so some slots would go untested.");

            var managedFieldCount = typeof(RustBridge.Globals.Constructors)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length;
            Assert.AreEqual(rustCount, managedFieldCount);
        }

        [Test]
        public void EverySlot_ProducesItsOwnExceptionType()
        {
            var failures = new List<string>();

            for (var slot = 0; slot < ExpectedBySlot.Length; slot++)
            {
                var actual = BuildViaSlot(slot).GetType();
                if (actual != ExpectedBySlot[slot])
                {
                    failures.Add(
                        $"slot {slot} ('{SlotName(slot)}') produced {actual.Name}, " +
                        $"expected {ExpectedBySlot[slot].Name}");
                }
            }

            // Report every mismatch at once: a transposition always shows up as a pair, and seeing
            // both halves makes the cause obvious.
            Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
        }

        [Test]
        public void EverySlot_CarriesItsPayloadIntoTheManagedException()
        {
            var failures = new List<string>();

            for (var slot = 0; slot < ExpectedBySlot.Length; slot++)
            {
                // The marker comes from Rust, so a drift between what Rust sends and what the test
                // expects is impossible by construction.
                var marker = SlotMarker(slot);
                var message = BuildViaSlot(slot).Message;

                if (message == null || !message.Contains(marker))
                {
                    failures.Add($"slot {slot} ('{SlotName(slot)}') message '{message}' does not contain '{marker}'");
                }
            }

            Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
        }

        [Test]
        public void AlreadyExistsSlot_CarriesKeyspaceAndTableSeparately()
        {
            // Two FFIStr arguments in one call: if they were swapped, or if the second were read from
            // the wrong register, keyspace and table would come back exchanged.
            var marker = SlotMarker(0);
            var ex = (AlreadyExistsException)BuildViaSlot(0);

            Assert.AreEqual(marker, ex.Keyspace);
            Assert.AreEqual(marker + "-tbl", ex.Table);
        }

        [Test]
        public void PreparedQueryNotFoundSlot_CarriesTheIdBytesVerbatim()
        {
            // The bytes come from Rust twice over: once as the exception payload, once directly, so
            // the comparison never involves a literal duplicated on the managed side. They contain an
            // interior NUL and are not valid UTF-8, which catches a payload treated as a C string.
            var expected = FfiTestExports.CollectBytes(FfiTestExports.ffi_test_prepared_id_bytes);
            var ex = (PreparedQueryNotFoundException)BuildViaSlot(11);

            NUnit.Framework.Legacy.CollectionAssert.AreEqual(expected, ex.UnknownId);
            NUnit.Framework.Assert.That(expected, Does.Contain((byte)0),
                "the fixture is meant to include a NUL byte");
        }

        [Test]
        public void OperationTimedOutSlot_CarriesTheIntegerPayload()
        {
            // The only slot taking a scalar rather than a string, so the only one where a mistake in
            // integer marshalling could pass unnoticed. Rust sends the slot index as the timeout.
            var ex = (OperationTimedOutException)BuildViaSlot(10);
            NUnit.Framework.Assert.That(ex.Message, Does.Contain("10ms"));
        }

        [Test]
        public unsafe void EveryConstructorPointer_IsNonNull()
        {
            // The table lives in a NativeMemory allocation filled by a 23-positional-argument
            // constructor. A forgotten assignment leaves a null slot, which would crash Rust on the
            // first error of that kind. (The allocation is zeroed for exactly this reason - otherwise
            // a missed field would be indistinguishable from a valid pointer.)
            var table = *RustBridge.Globals.ConstructorsPtr;
            foreach (var field in typeof(RustBridge.Globals.Constructors)
                     .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.AreNotEqual(IntPtr.Zero, (IntPtr)field.GetValue(table),
                    $"Exception constructor '{field.Name}' is a null function pointer.");
            }
        }
    }
}
