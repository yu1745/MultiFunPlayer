#!/usr/bin/env bash
# Build the real P4 receive/processing stages on x86, then run MFP against them.
set -euo pipefail
mfp_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ctw_root="$(realpath "${1:-$mfp_root/../ctw_esp32s3_0909}")"
native_build="$ctw_root/build/p4-native"
results="$mfp_root/Source/MultiFunPlayer.TCode.Tests/TestResults/native-p4"
dotnet_bin="${DOTNET:-dotnet}"
export CCACHE_BASEDIR="${CCACHE_BASEDIR:-$HOME}" CCACHE_NOHASHDIR=1
mkdir -p "$results"
cmake -S "$ctw_root/test/p4_native" -B "$native_build" -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build "$native_build" -j "${NATIVE_BUILD_JOBS:-8}"
ctest --test-dir "$native_build" --output-on-failure
export CTW_TCODE_HARNESS="$native_build/p4_native" CTW_NATIVE_P4="$native_build/p4_native"
export MFP_NATIVE_TRACE_DIR="$results"
"$dotnet_bin" test "$mfp_root/Source/MultiFunPlayer.TCode.Tests/MultiFunPlayer.TCode.Tests.csproj" \
    --logger 'trx;LogFileName=native-p4.trx' --results-directory "$results"
printf 'Native P4 test report, trajectory CSVs and request traces: %s\n' "$results"
