using System;
using System.Text;
using System.Collections.Generic;
using Cassandra;
using Cassandra.Serialization.Rust;

namespace SerializationExample
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine("Rust serialization tests");
            var result = SerializationExampleTests.RunAll();
            Environment.Exit(result ? 0 : 2);
        }
    }

    internal static class SerializationExampleTests
    {
        private static int _passed = 0;
        private static int _failed = 0;

        public static bool RunAll()
        {
            Console.WriteLine("\n-- Running example serialization tests --");
            Run(Test_SerializePrimitiveTypes);

            Console.WriteLine($"\nTests finished. Passed: {_passed}, Failed: {_failed}");
            return _failed == 0;
        }

        private static void Run(Action test)
        {
            try
            {
                test();
                Console.WriteLine($"[PASS] {test.Method.Name}");
                _passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] {test.Method.Name} - {ex.GetType().Name}: {ex.Message}");
                _failed++;
            }
        }

        private static void Assert(bool cond, string msg = null)
        {
            if (!cond) throw new InvalidOperationException(msg ?? "Assertion failed");
        }

        private static void Test_SerializePrimitiveTypes()
        {
            // Test serializing various primitive types
            var values = new object[]
            {
                "abc",
                123,
                null,
                456L,
                true,
                new byte[] { 1, 2, 3 }
            };

            var columnSpecs = new ColumnDesc[]
            {
                new ColumnDesc { TypeCode = ColumnTypeCode.Text },
                new ColumnDesc { TypeCode = ColumnTypeCode.Int },
                new ColumnDesc { TypeCode = ColumnTypeCode.Int },
                new ColumnDesc { TypeCode = ColumnTypeCode.Bigint },
                new ColumnDesc { TypeCode = ColumnTypeCode.Boolean },
                new ColumnDesc { TypeCode = ColumnTypeCode.Blob }
            };

            var bytes = RustSerializationFactory.SerializeRow(values, columnSpecs);
            Assert(bytes != null && bytes.Length > 0, "SerializeRow returned empty bytes");
            Console.WriteLine($"Serialized primitive types ({bytes.Length} bytes): {BitConverter.ToString(bytes)}");
        }
    }
}
