#!/usr/bin/env bash
set -euo pipefail

# Keeps the retired .NET Core 3.1 host usable on minimal modern Linux images.
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-1}"

BUILD_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_DIR="$(cd "$BUILD_DIR/.." && pwd)"
OUT_DIR="${1:-$BUILD_DIR/out}"
STUB_DIR="$OUT_DIR/stubs"
BIN_DIR="$OUT_DIR/bin"
TOOL_DIR="$OUT_DIR/tools"

if [[ -z "${DOTNET_ROOT:-}" ]]; then
    dotnet_path="$(command -v dotnet || true)"
    if [[ -z "$dotnet_path" ]]; then
        echo "Set DOTNET_ROOT to a .NET Core 3.1 SDK installation." >&2
        exit 2
    fi
    DOTNET_ROOT="$(cd "$(dirname "$(readlink -f "$dotnet_path")")" && pwd)"
fi

DOTNET="${DOTNET:-$DOTNET_ROOT/dotnet}"
if [[ -n "${DOTNET_SDK_DIR:-}" ]]; then
    SDK_DIR="$DOTNET_SDK_DIR"
else
    sdk_candidates=("$DOTNET_ROOT"/sdk/3.1.*)
    SDK_DIR="${sdk_candidates[${#sdk_candidates[@]}-1]}"
fi

CSC="$SDK_DIR/Roslyn/bincore/csc.dll"
NETSTANDARD_REF="$SDK_DIR/ref/netstandard.dll"
core_ref_candidates=("$DOTNET_ROOT"/packs/Microsoft.NETCore.App.Ref/3.1.*/ref/netcoreapp3.1)
NETCORE_REF_DIR="${NETCORE_REF_DIR:-${core_ref_candidates[0]}}"
CECIL_PROJECT="$BUILD_DIR/Tools/Mono.Cecil.csproj"
NUGET_PACKAGES_DIR="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
CECIL=""

if [[ ! -f "$CECIL_PROJECT" ]]; then
    echo "Required Mono.Cecil restore project not found: $CECIL_PROJECT" >&2
    exit 2
fi

"$DOTNET" restore "$CECIL_PROJECT" --nologo
while IFS= read -r candidate; do
    CECIL="$candidate"
    [[ "$candidate" == *netstandard* ]] && break
done < <(find "$NUGET_PACKAGES_DIR/mono.cecil/0.10.1/lib" -type f -name Mono.Cecil.dll | sort -r)

if [[ -z "$CECIL" || ! -f "$CECIL" ]]; then
    echo "Restored Mono.Cecil 0.10.1 assembly was not found under $NUGET_PACKAGES_DIR." >&2
    exit 2
fi

for required in "$DOTNET" "$CSC" "$NETSTANDARD_REF" "$CECIL"; do
    if [[ ! -f "$required" ]]; then
        echo "Required build input not found: $required" >&2
        exit 2
    fi
done
if [[ ! -d "$NETCORE_REF_DIR" ]]; then
    echo "Required .NET Core 3.1 reference directory not found: $NETCORE_REF_DIR" >&2
    exit 2
fi

mkdir -p "$STUB_DIR" "$BIN_DIR" "$TOOL_DIR"

common=("$DOTNET" "$CSC" /nologo /noconfig /nostdlib /deterministic+ /optimize+ /debug- /langversion:7.3 /target:library "/r:$NETSTANDARD_REF")
stubs="$BUILD_DIR/ReferenceStubs"

"${common[@]}" "/out:$STUB_DIR/TaleWorlds.Library.dll" "$stubs/TaleWorlds.Library.Stub.cs"
"${common[@]}" "/out:$STUB_DIR/TaleWorlds.Core.dll" "$stubs/TaleWorlds.Core.Stub.cs"
"${common[@]}" "/out:$STUB_DIR/TaleWorlds.Localization.dll" "$stubs/TaleWorlds.Localization.Stub.cs"
"${common[@]}" "/out:$STUB_DIR/TaleWorlds.Engine.dll" "/r:$STUB_DIR/TaleWorlds.Library.dll" "$stubs/TaleWorlds.Engine.Stub.cs"
"${common[@]}" "/out:$STUB_DIR/MCMv5.dll" "$stubs/MCMv5.Stub.cs"
"${common[@]}" "/out:$STUB_DIR/TaleWorlds.MountAndBlade.dll" \
    "/r:$STUB_DIR/TaleWorlds.Library.dll" "/r:$STUB_DIR/TaleWorlds.Core.dll" "/r:$STUB_DIR/TaleWorlds.Engine.dll" \
    "$stubs/TaleWorlds.MountAndBlade.Stub.cs"

"${common[@]}" "/out:$BIN_DIR/ExtremeRagdoll.ClothSync.dll" \
    "/r:$STUB_DIR/TaleWorlds.Library.dll" "/r:$STUB_DIR/TaleWorlds.MountAndBlade.dll" \
    "$SOURCE_DIR/ClothForceBridge.cs"

"${common[@]}" "/out:$BIN_DIR/ExtremeRagdoll.raw.dll" \
    "/r:$STUB_DIR/TaleWorlds.Library.dll" "/r:$STUB_DIR/TaleWorlds.Core.dll" "/r:$STUB_DIR/TaleWorlds.Engine.dll" \
    "/r:$STUB_DIR/TaleWorlds.MountAndBlade.dll" "/r:$STUB_DIR/TaleWorlds.Localization.dll" "/r:$STUB_DIR/MCMv5.dll" "/r:$BIN_DIR/ExtremeRagdoll.ClothSync.dll" \
    "$SOURCE_DIR/SafeSubModule.cs" "$SOURCE_DIR/McmLiveLocalizationRefresh.cs"

netcore_refs=()
for reference in "$NETCORE_REF_DIR"/*.dll; do netcore_refs+=("/r:$reference"); done
tool_common=("$DOTNET" "$CSC" /nologo /noconfig /nostdlib /deterministic+ /optimize+ /debug- /langversion:7.3 /target:exe /nowarn:1701 "${netcore_refs[@]}" "/r:$CECIL")

cp "$CECIL" "$TOOL_DIR/Mono.Cecil.dll"
cp "$BUILD_DIR/PatchOverride.runtimeconfig.json" "$TOOL_DIR/PatchOverride.runtimeconfig.json"
cp "$BUILD_DIR/ValidateAssemblies.runtimeconfig.json" "$TOOL_DIR/ValidateAssemblies.runtimeconfig.json"
"${tool_common[@]}" "/out:$TOOL_DIR/PatchOverride.dll" "$SOURCE_DIR/PatchOverride.cs"
"${tool_common[@]}" "/out:$TOOL_DIR/ValidateAssemblies.dll" "$BUILD_DIR/ValidateAssemblies.cs"

"$DOTNET" "$TOOL_DIR/PatchOverride.dll" \
    "$BIN_DIR/ExtremeRagdoll.raw.dll" "$STUB_DIR/TaleWorlds.MountAndBlade.dll" "$BIN_DIR/ExtremeRagdoll.dll"
"$DOTNET" "$TOOL_DIR/ValidateAssemblies.dll" \
    "$BIN_DIR/ExtremeRagdoll.dll" "$BIN_DIR/ExtremeRagdoll.ClothSync.dll"

echo "Build complete: $BIN_DIR"
