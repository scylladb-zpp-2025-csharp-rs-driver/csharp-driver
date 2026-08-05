# Testing the FFI layer

How the boundary between `src/Cassandra/RustBridge` and `rust/src` is tested, and why it is arranged
this way. Read this before adding a test that touches an FFI struct.

## The rule

**The managed side must never simulate the unmanaged side.**

A test that builds an `FFIString` from a pinned `byte[]` and decodes it again asserts a layout it
invented, using an encoder it also owns. It never involves Rust, so it cannot fail for any reason that
would matter in production — and it passes just as happily if both sides are wrong in the same way.
The same goes for fabricating an `FFISliceRaw` from a locally declared struct with matching fields, or
standing in for a Rust destructor with a C# callback that increments a counter.

So: Rust owns the strings, slices, UUID bytes, resources and exception handles. C# hands them over or
receives them through the same callbacks production uses, and only asserts on what arrived. The
test-only exports that make this possible live in `rust/src/ffi_test_exports.rs`; the managed
declarations and sinks are in `src/Cassandra.Tests/RustBridge/FfiTestExports.cs`.

Two exceptions are legitimate, and both are marked at the call site:

- A **null** value Rust cannot produce (a null `ManuallyDestructible`, a null `CSharpStr`).
- The **input** side of a round trip: to test C# → Rust, C# necessarily builds the struct. It is
  still Rust that reads and echoes it, so a bad encode is caught.

## `#[cfg(test)]` versus the `integration_testing` feature

`cargo test` builds a separate test-harness binary, so a `#[cfg(test)]` symbol never appears in the
`cdylib` that C# loads. Anything C# calls must therefore be behind the `integration_testing`
**feature**, not `cfg(test)`. `make check-no-test-exports` asserts a default build exports none of
them; `make test-unit` builds the library with the feature, via `build-rust-testing`.

The ABI descriptors are gated `#[cfg(any(feature = "integration_testing", test))]`, so they are
compiled and checked in both configurations.

## Layout parity

`ffi_test_abi_manifest` streams the layout Rust chose for every FFI struct — built from `offset_of!`
on the real types — and `AbiLayoutTests` compares it against `Marshal.SizeOf` / `Marshal.OffsetOf`
**by field name**.

Comparing total sizes is not enough: two structs of the same size with two fields transposed compare
equal, and transposing two of the 23 same-signature exception constructors is exactly the edit a human
makes by accident. Adding a struct or field on one side and forgetting the other fails with the
field's name in the message.

Two types are only partly covered by the manifest, both deliberately:

- `FFISlice<T>` — `Marshal.OffsetOf` refuses generic structs. Its layout is checked through
  `FFISliceRaw`, the non-generic twin `As<T>()` reinterprets it as.
- `Tcb<R>` — generic *and* its function-pointer fields are private, so not even a friend assembly can
  take their offsets. Covered behaviourally instead: `TcbRoundTripTests` has Rust call
  `complete_task` / `fail_task` and read `constructors` through the real struct, which would crash
  outright if any of the three were misplaced.

Layout parity says nothing about *contents*. `Constructors` is filled by a 23-positional-argument
initializer, so `ExceptionTableTests` separately drives every slot and asserts the exact managed
exception type — no two slots share a type, so any transposition shows up.

## Leak detection

Handle leaks are counted, not sampled. `RustBridge.HandleAccounting` increments when a GCHandle is
wrapped for Rust and decrements at each of the seven release sites; `RustBridgeTestBase` asserts on
teardown that the count returned to its baseline. That makes every test in the suite a leak test,
deterministically.

This is not a stylistic preference:

- **LeakSanitizer cannot see a leaked GCHandle at all.** The target stays *reachable* from the CLR's
  handle table, so by LSAN's definition nothing leaked.
- **`WeakReference` + `GC.Collect()` cannot prove the negative reliably.** Whether an unrooted local
  is genuinely collectable depends on JIT tier and debug codegen, so such tests are flaky in the
  direction that hides bugs. Three of them were replaced by the accounting check.

Rust-side allocations are counted the same way, via `ffi_test_live_resources()`, which is how
`RustResourceTests` asserts that disposing a `SafeHandle` really ran the Rust destructor exactly once.

## Sanitizers

| Target | What it covers |
| --- | --- |
| `make test-rust` | Rust unit tests **and doctests** — the `compile_fail` doctests are what prove the borrow-checker invariants the pointer design rests on, and `--lib` alone skips them |
| `make test-rust-asan` | use-after-free, double free, overflow, and leaked Rust allocations, in a clean process |
| `make test-rust-asan-selftest` | that the sanitizer is actually armed |
| `make test-rust-miri` | aliasing and pointer-provenance violations ASAN structurally cannot see |
| `make test-unit-gcstress` | the managed suite under frequent compacting collections |
| `make test-unit-asan` | the managed suite with the sanitized `cdylib` loaded into the CLR |

### Always run the self-test

`make test-rust-asan-selftest` runs two tests containing real defects (`asan_selftest` in
`rust/src/ffi.rs`) and requires ASAN to report them. Without it, a green sanitizer run is
indistinguishable from the sanitizer being switched off — which is not hypothetical: the flags that
arm it have been silently dropped from the Makefile before now. CI runs the self-test *before*
`test-rust-asan` for this reason.

### Why not `-Zbuild-std`

It would also instrument std's own loads and stores, but it currently fails on this crate:
`[profile.dev] panic = "abort"` makes cargo build `core` twice with incompatible settings
(`duplicate lang item in crate core`). Heap checks and leak detection work either way, because the
allocator is intercepted regardless; what is lost is instrumentation of accesses *inside* std, and
better stack traces through it. Worth revisiting if the panic strategy changes.

### Getting ASAN into the .NET host

Harder, and why `test-unit-asan` needs more setup than the others.

rustc ships only the **static** sanitizer runtime, which is not supported inside a DSO `dlopen`'d by
an uninstrumented host: the malloc interceptors install only at load time, so memory the CLR
allocated earlier came from glibc `malloc` and freeing it later lands in ASAN's allocator. The
supported configuration is the **shared** runtime, linked with `-Zexternal-clangrt
-C link-arg=-shared-libasan` and `LD_PRELOAD`ed ahead of the CLR. That needs a clang whose LLVM major
version matches rustc's.

Two `ASAN_OPTIONS` are not optional there:

- `handle_segv=0:handle_sigbus=0:handle_sigfpe=0:handle_abort=0` — CoreCLR installs its own SIGSEGV
  handler for null-reference checks and GC write barriers. If ASAN claims those signals the runtime
  dies at startup or reports nonsense.
- `detect_leaks=0` — the CLR never frees its JIT arenas, type loader or GC segments, by design.
  LeakSanitizer would report thousands of intentional "leaks". Leaks are covered by `test-rust-asan`
  (clean process) and by the handle accounting above.

## Memory movement

**ASAN cannot detect the GC relocating managed memory.** The CLR's GC heap is not tracked by the
sanitizer — segments arrive via `mmap` and the collector sub-allocates and moves objects inside them —
so a compacting collection moving an object out from under a native pointer is, to ASAN, a legal
write to legal memory. Rust's lifetimes do not help either: the `'a` on `FFISlice<'a, T>` constrains
Rust's use of the pointer, not the CLR's freedom to move what it points at.

Three things cover it instead.

**By construction.** Every pointer handed to Rust must be one of exactly four kinds:

1. Unmanaged — `NativeMemory.Alloc`. The constructor table is the only one, and it is intentionally
   never freed, which is what makes Rust's `&'static ExceptionConstructors` sound.
2. Pinned — `GCHandleType.Pinned` or `fixed`.
3. A blittable struct passed **by value** — no address escapes.
4. A **stack** address, valid only for the duration of a *synchronous* P/Invoke — the
   `Unsafe.AsPointer(ref local)` pattern in `BridgedSession.GetKeyspace` and in the test sinks.

Only the fourth is fragile, and only because nothing stops someone using it on an async path. The
remaining follow-up in this area is to wrap it in a `ref struct` context type, which cannot be
captured by a lambda or stored in a field, so the compiler rejects the async misuse.

**Empirically.** `ffi_test_gc_move_probe` records the address it was given and hashes the bytes, calls
back into C# (which churns gen0 and then forces a blocking, compacting collection), and re-reads *the
same address*. `GcMovementTests.PinnedBuffer_DoesNotMoveAcrossACompactingCollection` requires both
hashes to match.

That test only means something because of its negative control.
`GcMovementTests.UnpinnedBuffer_IsObservablyUnsafe` runs the same probe on an **unpinned** array and
requires the probe to notice. It is `[Explicit]` because it deliberately hands Rust a pointer that may
be invalidated mid-call — undefined behaviour, and non-deterministic by nature — so it must never run
in the normal suite. Run it by hand after changing the probe:

```
dotnet test src/Cassandra.Tests/Cassandra.Tests.csproj --property:BuildRust=false \
  --filter UnpinnedBuffer_IsObservablyUnsafe -l "console;verbosity=detailed"
```

It should report a changed address and a changed hash, e.g.:

```
address handed to Rust: 0x746770c0bf18, address after GC: 0x746770d01698,
hash before: 14137654348093743909, hash after: 5130076177682013955
```

**Statistically.** `make test-unit-gcstress` re-runs the whole managed suite with a tiny gen0
(`DOTNET_GCgen0size=8000`), no concurrent or server GC, and tiered compilation off — so the JIT does
not extend local lifetimes and mask a rooting mistake. A missing pin anywhere is far likelier to be
caught by an ordinary test under those settings.

## Known gaps

- **`FFISlice.ToSpan`'s `FailFast` paths** (length above `int.MaxValue`; non-zero length with a null
  pointer) cannot be asserted in-process. They need a child-process runner checking the exit code and
  stderr.
- **Deliberate use-after-free driven from C#** — freeing a Rust pointer and then using it. The
  `compile_fail` doctests prove this is unreachable from safe Rust; C# has no borrow checker, so it is
  reachable in production and ASAN would catch it. Needs the same child-process harness.
- **`panic = "abort"` makes `catch_unwind` dead code.** `BridgedFuture::spawn` catches panics and
  reports them through `rust_exception_constructor`, but both profiles set `panic = "abort"`, so in
  any shipped build a panic aborts the process instead. A Rust unit test cannot reveal this — cargo
  overrides the panic strategy for the test profile — only a managed test that provokes a panic across
  the boundary can. Decide deliberately: either drop `panic = "abort"` for the cdylib, or delete the
  `catch_unwind` as misleading.
- **A Rust-native mock of the managed side** (`rust/tests/`), implementing a fake GCHandle table,
  constructor table and TCS in Rust. It would bring the production entry points in `metadata.rs` and
  `session.rs` under ASAN, LSAN, Miri and TSAN without the CLR in the process.
