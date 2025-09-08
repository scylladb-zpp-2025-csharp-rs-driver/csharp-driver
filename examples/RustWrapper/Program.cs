//
//       Copyright (C) DataStax Inc.
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Cassandra;

namespace RustWrapper
{
    /// <summary>
    /// TODO: Add description
    /// </summary>
    internal class Program
    {
        private ICluster _cluster;
        private ISession _session;

        private static void Main(string[] args)
        {
            new Program().MainAsync(args).GetAwaiter().GetResult();
        }

        private async Task MainAsync(string[] args)
        {
            Console.WriteLine($"Beginning RustWrapper example!");

            // build cluster
            _cluster =
                Cluster.Builder()
                    .AddContactPoint("172.42.0.2")
                    .WithLoadBalancingPolicy(new TokenAwarePolicy(new DCAwareRoundRobinPolicy("datacenter1")))
                    .Build();

            // create session via Rust FFI
            var sessionPtr = await CreateSession("172.42.0.2").ConfigureAwait(false);

            // Free session synchronously, not to cause a leak.
            session_free(sessionPtr);

            // create session
            _session = await _cluster.ConnectAsync().ConfigureAwait(false);
        }





        // This shall be called by Rust code when the operation is completed.
        //
        // Signature in Rust: extern "C" fn(tcs: *mut c_void, res: *mut c_void)
        //
        // This attribute makes the method callable from native code.
        // It also allows taking a function pointer to the method.
        [UnmanagedCallersOnly(CallConvs = new Type[] { typeof(CallConvCdecl) })]
        static void CompleteTask(IntPtr tcsPtr, IntPtr resPtr)
        {
            try
            {
                // Recover the GCHandle that was allocated for the TaskCompletionSource.
                var handle = GCHandle.FromIntPtr(tcsPtr);

                if (handle.Target is TaskCompletionSource<IntPtr> tcs)
                {
                    // Simply pass the opaque pointer back as the result.
                    // The Rust code is responsible for interpreting the pointer's contents
                    // and freeing it when no longer needed.
                    tcs.SetResult(resPtr);

                    // Free the handle so the TCS can be collected once no longer used
                    // by the C# code.
                    handle.Free();
                }
                else
                {
                    throw new InvalidOperationException("GCHandle did not reference a TaskCompletionSource<IntPtr>.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FFI] CompleteTask threw exception: {ex}");
            }
        }

        [DllImport("csharp_wrapper", CallingConvention = CallingConvention.Cdecl)]
        unsafe private static extern IntPtr session_create(IntPtr tcsPtr, IntPtr completeTask, string uri);
        [DllImport("csharp_wrapper", CallingConvention = CallingConvention.Cdecl)]
        unsafe private static extern void session_free(IntPtr sessionPtr);
        private Task<IntPtr> CreateSession(string uri)
        {
            /**
             * TaskCompletionSource is a way to programatically control a Task.
             * We create one here and pass it to Rust code, which will complete it.
             * This is a common pattern to bridge async code between C# and native code.
             */
            TaskCompletionSource<IntPtr> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /**
             * Although GC knows that it must not collect items during a synchronous P/Invoke call,
             * it doesn't know that the native code will still require the TCS after the P/Invoke
             * call returns.
             * And tokio task in Rust will likely still run after the P/Invoke call returns.
             * So, since we are passing the TCS to asynchronous native code, we need to pin it
             * so it doesn't get collected by the GC.
             * We must remember to free the handle later when the TCS is completed (see CompleteTask
             * method).
             */
            var handle = GCHandle.Alloc(tcs);
            IntPtr tcsPtr = GCHandle.ToIntPtr(handle);

            // `unsafe` is required to get a function pointer to a static method.
            // Note that we can get this pointer because the method is static and
            // decorated with [UnmanagedCallersOnly].
            unsafe
            {
                // This is the only way to get a function pointer to a method decorated
                // with [UnmanagedCallersOnly] that I've found to compile.
                delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> fnPtr = &CompleteTask;
                IntPtr completeTaskPtr = (IntPtr)fnPtr;

                // Invoke the native code, which will complete the TCS when done.
                // We need to pass a pointer to CompleteTask because Rust code cannot directly
                // call C# methods.
                // Even though Rust code statically knows the name of the method, it cannot
                // directly call it because the .NET runtime does not expose the method
                // in a way that Rust can call it.
                // So we pass a pointer to the method and Rust code will call it via that pointer.
                // This is a common pattern to call C# code from native code ("reversed P/Invoke").
                session_create(tcsPtr, completeTaskPtr, uri);
            }

            return tcs.Task;
        }
    }
}