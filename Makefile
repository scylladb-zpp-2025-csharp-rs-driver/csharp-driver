SHELL := bash

MAKEFILE_PATH := $(abspath $(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
SCYLLA_VERSION ?= release:2025.4

CCM_CASSANDRA_REPO ?= github.com/apache/cassandra-ccm
CCM_CASSANDRA_VERSION ?= d3225ac6565242b231129e0c4f8f0b7a041219cf

CCM_SCYLLA_REPO ?= github.com/scylladb/scylla-ccm
CCM_SCYLLA_VERSION ?= master

SCYLLA_EXT_OPTS ?= --smp=2 --memory=4G
SIMULACRON_PATH ?= ${MAKEFILE_PATH}/ci/simulacron-standalone-0.12.0.jar

TARGET_FRAMEWORK ?=
SNK_FILE ?=
DEV_SNK_PUBLIC_KEY ?= 0024000004800000940000000602000000240000525341310004000001000100fb083dc01ba81b96b526327f232e7f4c1301c8ec177a2c66adecc315a9c2308f33ecd9dc70d6d1435107578b4dd04658c8f92a51a60d50c528ca6fba3955fa844fe79c884452024b0ba67d19a70140818aa61a1faeb23d5dcfe0bd9820d587829caf36d0ac7e0dc450d3654d5f5bee009dda3d11fd4066d4640b935c2ca048a4

ifneq ($(filter true 1 yes,$(SKIP_DUPLICATE)),)
	NUGET_PUSH_OPTIONS := --skip-duplicate
endif

ifeq (${CCM_CONFIG_DIR},)
	CCM_CONFIG_DIR = ~/.ccm
endif
CCM_CONFIG_DIR := $(shell readlink --canonicalize ${CCM_CONFIG_DIR})

TEST_TARGET_OPTIONS ?=
ifeq (${TEST_TARGET_OPTIONS},)
	ifeq (${TARGET_FRAMEWORK},)
		TEST_TARGET_OPTIONS =
	else ifeq (${TARGET_FRAMEWORK},all)
		TEST_TARGET_OPTIONS = -p:BuildTarget=all
	else
		TEST_TARGET_OPTIONS = -p:BuildTarget=${TARGET_FRAMEWORK} --framework=${TARGET_FRAMEWORK}
	endif
endif

export SCYLLA_EXT_OPTS
export SIMULACRON_PATH
export SCYLLA_VERSION

.PHONY: check
check: check-csharp check-rust

.PHONY: check-csharp
check-csharp:
	dotnet format --verify-no-changes --severity warn --verbosity diagnostic src/Cassandra/Cassandra.csproj
	dotnet format --verify-no-changes --severity warn --verbosity diagnostic src/Cassandra.Tests/Cassandra.Tests.csproj
	dotnet format --verify-no-changes --severity warn --verbosity diagnostic src/Cassandra.IntegrationTests/Cassandra.IntegrationTests.csproj
	dotnet format --verify-no-changes --severity warn --verbosity diagnostic src/LoggingTests/LoggingTests.csproj

.PHONY: check-rust
check-rust:
	cd rust; cargo fmt -- --check && cargo clippy --all-targets --all-features -- -D warnings

.PHONY: fix
fix: fix-csharp fix-rust

.PHONY: fix-csharp
fix-csharp:
	dotnet format --severity warn --verbosity diagnostic src/Cassandra/Cassandra.csproj
	dotnet format --severity warn --verbosity diagnostic src/Cassandra.Tests/Cassandra.Tests.csproj
	dotnet format --severity warn --verbosity diagnostic src/Cassandra.IntegrationTests/Cassandra.IntegrationTests.csproj
	dotnet format --severity warn --verbosity diagnostic src/LoggingTests/LoggingTests.csproj

.PHONY: fix-rust
fix-rust:
	cd rust; cargo fmt && cargo fix && cargo clippy --all-targets --all-features --fix

.PHONY: test-unit
test-unit: .use-development-snk build-rust-testing
	dotnet build-server shutdown
	dotnet test $(TEST_TARGET_OPTIONS) src/Cassandra.Tests/Cassandra.Tests.csproj --property:BuildRust=false

TEST_INTEGRATION_SCYLLA_FILTER ?= (FullyQualifiedName!~ClientWarningsTests & FullyQualifiedName!~CustomPayloadTests & FullyQualifiedName!~Connect_With_Ssl_Test & FullyQualifiedName!~Should_UpdateHosts_When_HostIpChanges & FullyQualifiedName!~Should_UseNewHostInQueryPlans_When_HostIsDecommissionedAndJoinsAgain & FullyQualifiedName!~Should_RemoveNodeMetricsAndDisposeMetricsContext_When_HostIsRemoved & FullyQualifiedName!~Virtual_Keyspaces_Are_Included & FullyQualifiedName!~Virtual_Table_Metadata_Test & FullyQualifiedName!~SessionAuthenticationTests & FullyQualifiedName!~Custom_MetadataTest & FullyQualifiedName!~Should_Use_Custom_TypeSerializers & FullyQualifiedName!~LinqWhere_WithVectors & FullyQualifiedName!~SimpleStatement_With_No_Compact_Enabled_Should_Reveal_Non_Schema_Columns & FullyQualifiedName!~SimpleStatement_With_No_Compact_Disabled_Should_Not_Reveal_Non_Schema_Columns & FullyQualifiedName!~ColumnClusteringOrderReversedTest & FullyQualifiedName!~GetMaterializedView_Should_Refresh_View_Metadata_Via_Events & FullyQualifiedName!~MaterializedView_Base_Table_Column_Addition & FullyQualifiedName!~MultipleSecondaryIndexTest & FullyQualifiedName!~RaiseErrorOnInvalidMultipleSecondaryIndexTest & FullyQualifiedName!~TableMetadataAllTypesTest & FullyQualifiedName!~TableMetadataClusteringOrderTest & FullyQualifiedName!~TableMetadataCollectionsSecondaryIndexTest & FullyQualifiedName!~TableMetadataCompositePartitionKeyTest & FullyQualifiedName!~TupleMetadataTest & FullyQualifiedName!~Udt_Case_Sensitive_Metadata_Test & FullyQualifiedName!~UdtMetadataTest & FullyQualifiedName!~Should_Retrieve_Table_Metadata & FullyQualifiedName!~CreateTable_With_Frozen_Key & FullyQualifiedName!~CreateTable_With_Frozen_Udt & FullyQualifiedName!~CreateTable_With_Frozen_Value & FullyQualifiedName!~Should_AllMetricsHaveValidValues_When_AllNodesAreUp & FullyQualifiedName!~SimpleStatement_Dictionary_Parameters_CaseInsensitivity_ExcessOfParams & FullyQualifiedName!~SimpleStatement_Dictionary_Parameters_CaseInsensitivity_NoOverload & FullyQualifiedName!~TokenAware_TransientReplication_NoHopsAndOnlyFullReplicas & FullyQualifiedName!~GetFunction_Should_Return_Most_Up_To_Date_Metadata_Via_Events & FullyQualifiedName!~LargeDataTests & FullyQualifiedName!~MetadataTests & FullyQualifiedName!~MultiThreadingTests & FullyQualifiedName!~PoolTests & FullyQualifiedName!~PrepareLongTests & FullyQualifiedName!~SpeculativeExecutionLongTests & FullyQualifiedName!~StressTests & FullyQualifiedName!~TransitionalAuthenticationTests & FullyQualifiedName!~ProxyAuthenticationTests & FullyQualifiedName!~CloudIntegrationTests & FullyQualifiedName!~CoreGraphTests & FullyQualifiedName!~GraphTests & FullyQualifiedName!~InsightsIntegrationTests & FullyQualifiedName!~DateRangeTests & FullyQualifiedName!~FoundBugTests & FullyQualifiedName!~GeometryTests & FullyQualifiedName!~LoadBalancingPolicyTests & FullyQualifiedName!~ConsistencyTests & FullyQualifiedName!~LoadBalancingPolicyTests & FullyQualifiedName!~ReconnectionPolicyTests & FullyQualifiedName!~RetryPolicyTests)
TEST_INTEGRATION_SIMULACRON_FILTER ?= (FullyQualifiedName~SessionExecuteAsyncTests | FullyQualifiedName~BasicTypeTests | FullyQualifiedName~TupleTests | FullyQualifiedName~ClusterSimulacronTests)
TEST_INTEGRATION_OPTIONS ?= -l "console;verbosity=detailed"
TEST_INTEGRATION_CSPROJ ?= src/Cassandra.IntegrationTests/Cassandra.IntegrationTests.csproj
.PHONY: test-integration-scylla
test-integration-scylla: .use-development-snk .prepare-scylla-ccm build-rust-testing
	dotnet build-server shutdown
	CCM_DISTRIBUTION=scylla dotnet test $(TEST_TARGET_OPTIONS) $(TEST_INTEGRATION_CSPROJ) $(TEST_INTEGRATION_OPTIONS) --property:BuildRust=false

.PHONY: test-integration-simulacron build-rust-testing
test-integration-simulacron: .use-development-snk
	dotnet build-server shutdown
	dotnet test $(TEST_TARGET_OPTIONS) $(TEST_INTEGRATION_CSPROJ) $(TEST_INTEGRATION_OPTIONS) --filter "$(TEST_INTEGRATION_SIMULACRON_FILTER)" --property:BuildRust=false

TEST_LOGGING_CSPROJ ?= src/LoggingTests/LoggingTests.csproj
TEST_LOGGING_CASES ?= Should_Forward_Rust_Log_Entries_Using_LoggerFactory Should_Forward_Rust_Log_On_Connect Should_Filter_Rust_Log_Entries_At_Off Should_Filter_Rust_Log_Entries_At_Error Should_Filter_Rust_Log_Entries_At_Warning Should_Filter_Rust_Log_Entries_At_Info Should_Filter_Rust_Log_Entries_At_Verbose

.PHONY: test-logging
test-logging: .use-development-snk build-rust-testing
	dotnet build $(TEST_LOGGING_CSPROJ) --property:BuildRust=false
	status=0; \
	for test in $(TEST_LOGGING_CASES); do \
		dotnet test --no-build $(TEST_TARGET_OPTIONS) $(TEST_LOGGING_CSPROJ) $(TEST_INTEGRATION_OPTIONS) --filter "FullyQualifiedName~RustLoggingTests.$$test" || status=1; \
	done; \
	exit $$status

.PHONY: test-integration-cassandra
test-integration-cassandra: .use-development-snk .prepare-cassandra-ccm build-rust-testing
	CCM_DISTRIBUTION=cassandra dotnet test $(TEST_TARGET_OPTIONS) $(TEST_INTEGRATION_CSPROJ) $(TEST_INTEGRATION_OPTIONS)

.prepare-cassandra-ccm:
	@ccm --help 2>/dev/null 1>&2; if [[ $$? -lt 127 ]] && grep CASSANDRA ${CCM_CONFIG_DIR}/ccm-type 2>/dev/null 1>&2 && grep ${CCM_CASSANDRA_VERSION} ${CCM_CONFIG_DIR}/ccm-version 2>/dev//null  1>&2; then \
		echo "Cassandra CCM ${CCM_CASSANDRA_VERSION} is already installed"; \
  	else \
		echo "Installing Cassandra CCM ${CCM_CASSANDRA_VERSION}"; \
		pip install "git+https://${CCM_CASSANDRA_REPO}.git@${CCM_CASSANDRA_VERSION}"; \
		mkdir ${CCM_CONFIG_DIR} 2>/dev/null || true; \
		echo CASSANDRA > ${CCM_CONFIG_DIR}/ccm-type; \
		echo ${CCM_CASSANDRA_VERSION} > ${CCM_CONFIG_DIR}/ccm-version; \
  	fi

.PHONY: install-cassandra-ccm
install-cassandra-ccm:
	@echo "Install CCM ${CCM_CASSANDRA_VERSION}"
	@pip install "git+https://${CCM_CASSANDRA_REPO}.git@${CCM_CASSANDRA_VERSION}"
	@mkdir ${CCM_CONFIG_DIR} 2>/dev/null || true
	@echo CASSANDRA > ${CCM_CONFIG_DIR}/ccm-type
	@echo ${CCM_CASSANDRA_VERSION} > ${CCM_CONFIG_DIR}/ccm-version

.prepare-scylla-ccm:
	@ccm --help 2>/dev/null 1>&2; if [[ $$? -lt 127 ]] && grep SCYLLA ${CCM_CONFIG_DIR}/ccm-type 2>/dev/null 1>&2 && grep ${CCM_SCYLLA_VERSION} ${CCM_CONFIG_DIR}/ccm-version 2>/dev//null  1>&2; then \
		echo "Scylla CCM ${CCM_SCYLLA_VERSION} is already installed"; \
  	else \
		echo "Installing Scylla CCM ${CCM_SCYLLA_VERSION}"; \
		pip install "git+https://${CCM_SCYLLA_REPO}.git@${CCM_SCYLLA_VERSION}"; \
		mkdir ${CCM_CONFIG_DIR} 2>/dev/null || true; \
		echo SCYLLA > ${CCM_CONFIG_DIR}/ccm-type; \
		echo ${CCM_SCYLLA_VERSION} > ${CCM_CONFIG_DIR}/ccm-version; \
  	fi

.PHONY: install-scylla-ccm
install-scylla-ccm:
	@echo "Installing Scylla CCM ${CCM_SCYLLA_VERSION}"
	@pip install "git+https://${CCM_SCYLLA_REPO}.git@${CCM_SCYLLA_VERSION}"
	@mkdir ${CCM_CONFIG_DIR} 2>/dev/null || true
	@echo SCYLLA > ${CCM_CONFIG_DIR}/ccm-type
	@echo ${CCM_SCYLLA_VERSION} > ${CCM_CONFIG_DIR}/ccm-version

.use-development-snk:
	@[ -f build/scylladb.snk ] || ( cp -f build/scylladb-dev.snk build/scylladb.snk )

.prepare-sn-devel:
	if ! sn -h sn 2>/dev/null >&2; then \
  		sudo apt-get update; \
		sudo apt-get install -y mono-devel;\
	fi

.use-production-snk: .prepare-sn-devel
	@if [ -z "${SNK_FILE}" ]; then \
 		echo "Environment variable SNK_FILE is not set. Please set it to the path of the production SNK file."; \
 		exit 1; \
 	else \
 		echo "${SNK_FILE}" | base64 --decode > build/scylladb.snk; \
 	fi; \
 	sn -p build/scylladb.snk /tmp/scylladb.pub; \
 	export PROD_SNK_PUBLIC_KEY=`hexdump -v -e '/1 "%02x"' /tmp/scylladb.pub`; \
 	echo "Switching to production SNK public key: $${PROD_SNK_PUBLIC_KEY}"; \
 	for file in `grep --exclude=Makefile -rIl 'PublicKey=' .`; do \
 	  echo "Processing file: $$file"; \
 	  grep 'PublicKey=$${PROD_SNK_PUBLIC_KEY}' "$$file" 2>/dev/null >&1 || sed -i "s/PublicKey=${DEV_SNK_PUBLIC_KEY}/PublicKey=$${PROD_SNK_PUBLIC_KEY}/g" "$$file" 2> /dev/null 1>&2; \
 	done;

.target-to-dry-run-package:
	grep '<PackageId>ScyllaDBCSharpDriver.DRYRUN' $(PROJECT_PATH) || \
	sed -Ei "s#<PackageId>ScyllaDBCSharpDriver(.*)</PackageId>#<PackageId>ScyllaDBCSharpDriver.DRYRUN\1</PackageId>#g" $(PROJECT_PATH)

.publish-proj-nuget: .use-production-snk
ifndef DRY_RUN
	@echo "Publishing to NuGet with production SNK"
else
	@echo "Dry run publishing to NuGet with production SNK"
	$(MAKE) .target-to-dry-run-package
endif
	dotnet restore $(PROJECT_PATH)
	dotnet build $(PROJECT_PATH) --configuration Release --no-restore
	rm -rf ./nupkgs
	dotnet pack $(PROJECT_PATH) --configuration Release --no-build --output ./nupkgs
	dotnet nuget push ${NUGET_PUSH_OPTIONS} "./nupkgs/*.nupkg" --api-key ${NUGET_API_KEY} --source https://api.nuget.org/v3/index.json

.PHONY: publish-nuget
publish-nuget:
	$(MAKE) .publish-proj-nuget PROJECT_PATH=src/Cassandra/Cassandra.csproj
	$(MAKE) .publish-proj-nuget PROJECT_PATH=src/Extensions/Cassandra.AppMetrics/Cassandra.AppMetrics.csproj
	$(MAKE) .publish-proj-nuget PROJECT_PATH=src/Extensions/Cassandra.OpenTelemetry/Cassandra.OpenTelemetry.csproj

.PHONY: publish-nuget-dry-run
publish-nuget-dry-run:
	$(MAKE) .publish-proj-nuget PROJECT_PATH=src/Cassandra/Cassandra.csproj DRY_RUN=1
	$(MAKE) .publish-proj-nuget PROJECT_PATH=src/Extensions/Cassandra.AppMetrics/Cassandra.AppMetrics.csproj DRY_RUN=1
	$(MAKE) .publish-proj-nuget PROJECT_PATH=src/Extensions/Cassandra.OpenTelemetry/Cassandra.OpenTelemetry.csproj DRY_RUN=1

.PHONY: clean
clean: clean-csharp clean-rust

.PHONY: clean-csharp
clean-csharp:
	find . -name '*.csproj' -print0 | xargs -0 -n1 dotnet clean

.PHONY: clean-rust
clean-rust:
	cd rust; cargo clean

# TODO: Put --release for production builds
.PHONY: build-rust
build-rust:
	cd rust; \
	cargo build;

.PHONY: build-rust-testing
build-rust-testing:
	cd rust; \
	cargo build --features integration_testing;

# Rust unit tests and doctests. The doctests matter as much as the unit tests here: the
# `compile_fail` ones in rust/src/ffi.rs are what prove the borrow-checker invariants the whole
# pointer design rests on, and `cargo test --lib` alone silently skips them.
.PHONY: test-rust
test-rust:
	cd rust; \
	cargo test --all-features --lib; \
	cargo test --all-features --doc;

# Rejects a shippable build that exports the test-only FFI entry points. They are feature-gated, so
# this should never fire - but the whole point of the gate is that nothing outside a test build can
# reach them, and that is worth checking rather than assuming.
.PHONY: check-no-test-exports
check-no-test-exports: build-rust
	@if nm -D --defined-only rust/target/debug/libcsharp_wrapper.so | grep -q ' T ffi_test_'; then \
		echo "FAIL: the default build exports test-only symbols:"; \
		nm -D --defined-only rust/target/debug/libcsharp_wrapper.so | grep ' T ffi_test_'; \
		exit 1; \
	fi; \
	echo "OK: no ffi_test_* symbols in the default build"

#
# --- Sanitizers -------------------------------------------------------------------------------
#
# All of this needs the nightly toolchain:
#   rustup toolchain install nightly --component rust-src,miri
#
ASAN_TARGET ?= x86_64-unknown-linux-gnu
# A separate target dir. The sanitizer build uses different RUSTFLAGS, so sharing `target/` with the
# ordinary build would invalidate the whole cache every time you switch between them.
ASAN_TARGET_DIR ?= target/asan
# `--cfg scylla_unstable` has to be repeated here: setting the RUSTFLAGS environment variable
# *replaces* build.rustflags from rust/.cargo/config.toml rather than appending to it, so omitting it
# silently drops the scylla driver's unstable API surface and the build fails confusingly.
ASAN_RUSTFLAGS ?= --cfg scylla_unstable -Zsanitizer=address
# detect_stack_use_after_return catches an FFIStr or FFISlice returned while borrowing a Rust stack
# local, which is a plausible mistake given how much of this layer hands out borrowed pointers.
ASAN_OPTS ?= detect_leaks=1:detect_stack_use_after_return=1:detect_stack_use_after_scope=1:strict_string_checks=1

# Runs the Rust unit tests under AddressSanitizer: use-after-free, double free, buffer overflow, and
# (via LeakSanitizer) leaked Rust allocations. This is a clean process with no CLR in it, so leak
# detection is usable here in a way it is not in the managed test run - see FFI_TESTING.md.
#
# Note that -Zbuild-std is deliberately *not* used. It would also instrument std's own loads and
# stores, but it currently fails on this crate: `[profile.dev] panic = "abort"` makes cargo build
# `core` twice with incompatible settings ("duplicate lang item in crate core"). Heap checks and leak
# detection work regardless, because the allocator is intercepted either way; what is lost is
# instrumentation of accesses *inside* std, and better stack traces through it.
.PHONY: test-rust-asan
test-rust-asan:
	cd rust; \
	CARGO_TARGET_DIR=${ASAN_TARGET_DIR} \
	RUSTFLAGS="${ASAN_RUSTFLAGS}" \
	ASAN_OPTIONS=${ASAN_OPTS} \
	cargo +nightly test --target ${ASAN_TARGET} --all-features --lib;

# Proves the sanitizer is actually armed, by running two tests that contain real defects and
# requiring ASAN to report them (see rust/src/ffi.rs `asan_selftest`).
#
# Without this, `test-rust-asan` passing is indistinguishable from the sanitizer not being enabled -
# which is not hypothetical: it depends on RUSTFLAGS reaching the right compilation units, and those
# flags have been silently dropped from this Makefile before.
.PHONY: test-rust-asan-selftest
test-rust-asan-selftest:
	@cd rust; \
	run() { \
		CARGO_TARGET_DIR=${ASAN_TARGET_DIR} RUSTFLAGS="${ASAN_RUSTFLAGS}" ASAN_OPTIONS=detect_leaks=1 \
		cargo +nightly test --target ${ASAN_TARGET} --all-features --lib -- \
			--ignored --test-threads=1 "$$1" 2>&1 || true; \
	}; \
	uaf=$$(run asan_selftest::deliberate_use_after_free); \
	echo "$$uaf" | grep -q 'AddressSanitizer: heap-use-after-free' || { \
		echo "FAIL: ASAN did not report the deliberate use-after-free - the sanitizer is not armed."; \
		echo "$$uaf" | tail -30; exit 1; }; \
	leak=$$(run asan_selftest::deliberate_leak); \
	echo "$$leak" | grep -q 'LeakSanitizer: detected memory leaks' || { \
		echo "FAIL: LeakSanitizer did not report the deliberate leak."; \
		echo "$$leak" | tail -30; exit 1; }; \
	echo "OK: AddressSanitizer and LeakSanitizer are both armed"

# Miri catches what ASAN structurally cannot: aliasing and pointer-provenance violations, such as
# producing a &mut through BridgedPtr while a shared borrow is live, or Box::from_raw on a pointer
# that did not come from Box. It cannot run the tokio or dlopen paths, so it is scoped to the pure
# FFI abstractions.
.PHONY: test-rust-miri
test-rust-miri:
	cd rust; \
	cargo +nightly miri test --all-features --lib;

# Builds the cdylib with AddressSanitizer, for loading into the .NET test host.
#
# This is the harder half of the story and is documented in FFI_TESTING.md. In short: rustc
# ships only the *static* sanitizer runtime, which is not supported inside a DSO dlopen'd by an
# uninstrumented host, so the cdylib has to be linked against the *shared* runtime and that runtime
# has to be LD_PRELOADed ahead of the CLR. Both need a clang whose LLVM major version matches
# rustc's. `test-unit-asan` does the preloading; this target only produces the library.
.PHONY: build-rust-asan
build-rust-asan:
	cd rust; \
	CARGO_TARGET_DIR=${ASAN_TARGET_DIR} \
	RUSTFLAGS="${ASAN_RUSTFLAGS} -Zexternal-clangrt -C link-arg=-shared-libasan" \
	cargo +nightly build --features integration_testing --target ${ASAN_TARGET};

# Runs the managed unit tests with the sanitized cdylib loaded into the .NET host.
#
# ASAN_OPTIONS here is not optional. CoreCLR installs its own SIGSEGV handler for null-reference
# checks and GC write barriers, so ASAN must not claim those signals or the runtime dies at startup.
# Leak detection is off for the same kind of reason: the CLR never frees its JIT arenas, type loader
# or GC segments by design, so LeakSanitizer would report thousands of intentional "leaks". Leaks are
# covered by `test-rust-asan` (a clean process) and by the managed handle accounting instead.
ASAN_SO ?= $(shell clang -print-file-name=libclang_rt.asan-x86_64.so 2>/dev/null)
.PHONY: test-unit-asan
test-unit-asan: .use-development-snk build-rust-asan
	@if [ ! -f "${ASAN_SO}" ]; then \
		echo "Could not locate the shared ASAN runtime (got '${ASAN_SO}')."; \
		echo "Install a clang matching rustc's LLVM version, or set ASAN_SO explicitly."; \
		exit 1; \
	fi
	cp rust/${ASAN_TARGET_DIR}/${ASAN_TARGET}/debug/libcsharp_wrapper.so rust/target/debug/
	LD_PRELOAD="${ASAN_SO}" \
	ASAN_OPTIONS=detect_leaks=0:handle_segv=0:handle_sigbus=0:handle_sigfpe=0:handle_abort=0:abort_on_error=1 \
	dotnet test $(TEST_TARGET_OPTIONS) src/Cassandra.Tests/Cassandra.Tests.csproj --property:BuildRust=false

# Re-runs the managed unit tests under a deliberately hostile GC: a tiny gen0 forces frequent
# compacting collections, and disabling tiered compilation stops the JIT from extending local
# lifetimes and masking a rooting mistake. Any managed buffer handed to Rust without being pinned is
# far more likely to be caught here than in the ordinary run.
#
# GcMovementTests probes this directly for one buffer; this target applies the same pressure to every
# test in the suite.
.PHONY: test-unit-gcstress
test-unit-gcstress: .use-development-snk build-rust-testing
	DOTNET_gcServer=0 \
	DOTNET_gcConcurrent=0 \
	DOTNET_GCgen0size=8000 \
	DOTNET_TieredCompilation=0 \
	dotnet test $(TEST_TARGET_OPTIONS) src/Cassandra.Tests/Cassandra.Tests.csproj --property:BuildRust=false

.PHONY: run-wrapper-example
run-wrapper-example:
	dotnet run --project examples/RustWrapper/RustWrapper.csproj

.PHONY: run-wrapper-example-asan
run-wrapper-example-asan:
	LD_PRELOAD=/usr/lib64/libasan.so.8 dotnet run --project examples/RustWrapper/RustWrapper.csproj
