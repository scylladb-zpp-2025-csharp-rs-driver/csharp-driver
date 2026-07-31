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
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    /// <summary>
    /// Verifies that every struct crossing the FFI boundary has the same layout on both sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rust streams its own layout across the boundary (<c>ffi_test_abi_manifest</c>), built from
    /// <c>offset_of!</c> on the real types, and this fixture compares it against
    /// <see cref="Marshal.SizeOf{T}()"/> / <see cref="Marshal.OffsetOf{T}(string)"/> field by field.
    /// </para>
    /// <para>
    /// Comparing total sizes - which is all the previous test did - is not enough. Two structs with
    /// the same size and two fields transposed compare equal, and transposing two of the 23
    /// same-signature exception constructors is precisely the edit a human makes by accident. Only a
    /// per-field, by-name comparison catches it, and only a manifest generated from the Rust types
    /// catches a field added on one side and forgotten on the other.
    /// </para>
    /// </remarks>
    [TestFixture, Category("unit")]
    public unsafe class AbiLayoutTests : RustBridgeTestBase
    {
        /// <summary>
        /// Maps each name Rust reports to the managed type it must match.
        /// </summary>
        /// <remarks>
        /// Two entries need explanation. <c>FFIException</c> has no managed struct of its own: Rust's
        /// <c>repr(transparent)</c> newtype arrives as a bare <c>FFIGCHandle</c>, so both map to the
        /// same managed type. <c>FFISliceRaw</c> is the non-generic twin of <c>FFISlice&lt;T&gt;</c>
        /// that <c>[UnmanagedCallersOnly]</c> signatures are forced to use; it must match Rust's
        /// <c>FFISlice</c> too, because <c>As&lt;T&gt;()</c> reinterprets one as the other.
        /// </remarks>
        private static readonly (string RustName, Type Managed)[] TypeMap =
        {
            ("FFIGCHandle", typeof(RustBridge.FFIGCHandle)),
            ("FFIMaybeGCHandle", typeof(RustBridge.FFIMaybeGCHandle)),
            ("FFIException", typeof(RustBridge.FFIGCHandle)),
            ("FFIMaybeException", typeof(RustBridge.FFIMaybeException)),
            ("FFISlice", typeof(RustBridge.FFISlice<byte>)),
            ("FFISlice", typeof(RustBridge.FFISliceRaw)),
            ("FFIString", typeof(RustBridge.FFIString)),
            ("FFIBool", typeof(RustBridge.FFIBool)),
            ("Constructors", typeof(RustBridge.Globals.Constructors)),
            ("Tcb", typeof(RustBridge.Tcb<RustBridge.FFIBool>)),
            ("ManuallyDestructible", typeof(RustBridge.ManuallyDestructible)),
            ("EmptyAsyncResult", typeof(RustBridge.EmptyAsyncResult)),
        };

        private Dictionary<string, FfiTestExports.RustTypeLayout> _rustLayout;

        [OneTimeSetUp]
        public void FetchRustLayout()
        {
            _rustLayout = CollectManifest();
        }

        private static Dictionary<string, FfiTestExports.RustTypeLayout> CollectManifest()
        {
            var collector = new FfiTestExports.AbiCollector();
            var result = FfiTestExports.ffi_test_abi_manifest(
                (IntPtr)Unsafe.AsPointer(ref collector),
                FfiTestExports.EmitTypePtr,
                FfiTestExports.EmitFieldPtr);
            FfiTestExports.ThrowIfFailed(result);
            return collector.Types;
        }

        [Test]
        public void Manifest_DescribesEveryTypeTheManagedSideExpects()
        {
            var missing = TypeMap.Select(e => e.RustName).Distinct()
                                 .Where(n => !_rustLayout.ContainsKey(n))
                                 .ToArray();

            Assert.IsEmpty(missing,
                "Rust's ABI manifest does not describe: " + string.Join(", ", missing) +
                ". Either the manifest is missing an entry, or the managed type map is stale.");
        }

        [Test]
        public void Manifest_HasNoTypeTheManagedSideIgnores()
        {
            // Guards the other direction: a type described by Rust but absent from TypeMap would be
            // silently unchecked, which is how a struct quietly loses its parity test.
            var known = TypeMap.Select(e => e.RustName).Distinct().ToHashSet();
            var unchecked_ = _rustLayout.Keys.Where(k => !known.Contains(k)).ToArray();

            Assert.IsEmpty(unchecked_,
                "Rust describes types that no managed type is compared against: " +
                string.Join(", ", unchecked_) + ". Add them to TypeMap.");
        }

        [Test]
        public void EveryType_HasMatchingSizeAndAlignment()
        {
            foreach (var (rustName, managed) in TypeMap)
            {
                var rust = _rustLayout[rustName];
                var size = ManagedSizeOf(managed);

                Assert.AreEqual((int)rust.Size, size,
                    $"size mismatch for {managed.Name} (Rust {rustName})");

                // Marshal exposes no alignment query, so derive it: every type here is a struct of
                // pointer-sized-or-smaller blittable fields, so it aligns to its largest field -
                // either 1 (FFIBool, EmptyAsyncResult) or IntPtr.Size.
                var expectedAlign = size == 1 ? 1 : IntPtr.Size;
                Assert.AreEqual(expectedAlign, (int)rust.Align,
                    $"alignment mismatch for {managed.Name} (Rust {rustName})");
            }
        }

        [Test]
        public void EveryField_IsAtTheSameOffsetOnBothSides()
        {
            var comparedAny = false;

            foreach (var (rustName, managed) in TypeMap)
            {
                var rust = _rustLayout[rustName];
                if (rust.FieldOffsets.Count == 0)
                {
                    // Size-and-alignment only (see the note on FFIException in TypeMap).
                    continue;
                }

                var managedFields = ManagedFieldOffsets(managed);
                if (managedFields == null)
                {
                    // Marshal.OffsetOf cannot describe a generic struct's fields. That leaves two
                    // types partially covered, both of which are covered another way:
                    //  - FFISlice<T>: its layout is checked through FFISliceRaw, the non-generic twin
                    //    that As<T>() reinterprets it as; only the size check applies here.
                    //  - Tcb<R>: its function-pointer fields are private, so not even a friend
                    //    assembly can take their offsets. They are verified behaviourally instead -
                    //    TcbRoundTripTests has Rust call complete_task/fail_task and read
                    //    `constructors` through the real struct, which would crash outright if any of
                    //    the three were at the wrong offset.
                    continue;
                }

                comparedAny = true;

                Assert.AreEqual(rust.FieldOffsets.Count, managedFields.Count,
                    $"{managed.Name} has {managedFields.Count} field(s) but Rust's {rustName} has " +
                    $"{rust.FieldOffsets.Count}. Managed: [{string.Join(", ", managedFields.Keys)}], " +
                    $"Rust: [{string.Join(", ", rust.FieldOffsets.Keys)}]");

                foreach (var (fieldName, rustOffset) in rust.FieldOffsets)
                {
                    Assert.IsTrue(managedFields.ContainsKey(fieldName),
                        $"{managed.Name} has no field named '{fieldName}', which Rust's {rustName} " +
                        $"places at offset {rustOffset}.");

                    Assert.AreEqual((int)rustOffset, managedFields[fieldName],
                        $"{managed.Name}.{fieldName} is at offset {managedFields[fieldName]} but " +
                        $"Rust places it at {rustOffset}.");
                }
            }

            // Guard against the whole loop silently skipping everything.
            Assert.IsTrue(comparedAny, "No type had its fields compared.");
        }

        /// <summary>
        /// The constructor table is written into a single unmanaged allocation and read by Rust as a
        /// struct, so it must be exactly a packed array of pointers - no padding, no non-pointer
        /// field. Rust asserts the same thing about its own half in <c>task.rs</c>.
        /// </summary>
        [Test]
        public void ConstructorTable_IsAPackedPointerArray()
        {
            var fields = ManagedFieldOffsets(typeof(RustBridge.Globals.Constructors));
            Assert.AreEqual(fields.Count * IntPtr.Size, Marshal.SizeOf<RustBridge.Globals.Constructors>());

            foreach (var (name, offset) in fields)
            {
                Assert.AreEqual(0, offset % IntPtr.Size,
                    $"constructor slot '{name}' is not pointer-aligned.");
            }
        }

        /// <summary>
        /// In-memory size of a managed struct, including generic ones that
        /// <see cref="Marshal.SizeOf(Type)"/> refuses to describe.
        /// </summary>
        private static int ManagedSizeOf(Type type)
        {
            // Unsafe.SizeOf reports the managed layout size, which for a blittable struct is exactly
            // what the P/Invoke marshaller passes - and unlike Marshal.SizeOf it accepts generics.
            var method = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf), BindingFlags.Public | BindingFlags.Static);
            return (int)method.MakeGenericMethod(type).Invoke(null, null);
        }

        /// <summary>
        /// Field offsets of a managed struct, or <c>null</c> for a generic struct, whose fields
        /// <see cref="Marshal.OffsetOf(Type, string)"/> cannot describe.
        /// </summary>
        private static Dictionary<string, int> ManagedFieldOffsets(Type type)
        {
            if (type.IsGenericType)
            {
                return null;
            }

            var offsets = new Dictionary<string, int>();
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                offsets[field.Name] = (int)Marshal.OffsetOf(type, field.Name);
            }

            return offsets;
        }
    }
}
