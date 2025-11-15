#!/bin/bash
# Helper script to run the SerializationExample with proper library path

# Set the directory where the Rust native library is located
RUST_LIB_DIR="$(dirname "$0")/../../rust/target/release"

# Add to LD_LIBRARY_PATH so .NET can find libcsharp_wrapper.so
export LD_LIBRARY_PATH="${RUST_LIB_DIR}:${LD_LIBRARY_PATH}"

echo "LD_LIBRARY_PATH=${LD_LIBRARY_PATH}"
echo "Running SerializationExample..."
echo ""

# Run the example
dotnet run --project "$(dirname "$0")"
