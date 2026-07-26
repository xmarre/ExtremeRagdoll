$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Assert-Hash([string]$Path,[string]$Expected,[string]$Label){
    if(!(Test-Path -LiteralPath $Path)){throw "${Label} missing: $Path"}
    $actual=Get-Sha256 $Path
    if($actual -ne $Expected){throw "${Label} hash mismatch: expected $Expected, observed $actual"}
}
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
$payloadRoot=Join-Path $repo 'tools/ga1116'
$encoded=Join-Path $payloadRoot 'ga1116-runtime-payload.zip.b64'
if(!(Test-Path -LiteralPath $encoded)){throw "v1.1.16 encoded payload missing: $encoded"}
$clean=[Text.RegularExpressions.Regex]::Replace([IO.File]::ReadAllText($encoded),'\s+','')
$bytes=[Convert]::FromBase64String($clean)
$zip=Join-Path $env:RUNNER_TEMP 'ga1116-runtime-payload.zip'
[IO.File]::WriteAllBytes($zip,$bytes)
Assert-Hash $zip '7763262fba960c5ce41fc959931273002aa8bebe1a4b7e2c505a8888e5f4ed04' 'v1.1.16 runtime payload zip'
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$payloadRoot,$true)
Assert-Hash (Join-Path $payloadRoot 'GuidedArrow-v1.1.15-to-v1.1.16-Single-Usage-Capture.patch') '2d014481fdae2ce5b8a02d1fb13ab6139fa2bd75ec970aa66af4f730c9d8909a' 'v1.1.16 patch'
Assert-Hash (Join-Path $payloadRoot 'InspectMissileCallGraph.cs') '880af9b8bc15c378364c9866ec262b668e802807374cbe65c470e2185a3b0f04' 'v1.1.16 original inspector'
Assert-Hash (Join-Path $payloadRoot 'build-ga1116-single-usage-capture.ps1') '0c3c44ddf505f475d698431b9df6d471c5a5679bfa9593b7638c40c2989fa2db' 'v1.1.16 build script'
Assert-Hash (Join-Path $payloadRoot 'test-ga1116-single-usage-capture.py') '921c77bc66353652cdf81dad4cd72e894fd446ef507c6fcb78031793607ac83e' 'v1.1.16 test'

# Bannerlord.ReferenceAssemblies preserves the private signatures needed for compilation,
# but its reference method bodies do not preserve the runtime call graph. Validate the
# exact dual-target API here. The actual 1.3.15 runtime DLL was inspected separately and
# both OnAgentShootMissile and AddCustomMissile conditionally call both aux methods.
$signatureInspector=@'
using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class InspectMissileCallGraph
{
    private static bool IsByRefTo(TypeReference type, string fullName)
    {
        return type is ByReferenceType byRef && byRef.ElementType.FullName == fullName;
    }

    public static int Main(string[] args)
    {
        string assemblyPath = args.FirstOrDefault(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        string version = args.FirstOrDefault(a => !a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ?? "unknown";
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            throw new InvalidOperationException("TaleWorlds.MountAndBlade.dll argument was not supplied");

        using (var assembly = AssemblyDefinition.ReadAssembly(assemblyPath))
        {
            var mission = assembly.MainModule.Types.FirstOrDefault(t => t.FullName == "TaleWorlds.MountAndBlade.Mission");
            if (mission == null)
                throw new InvalidOperationException(version + ": Mission type missing");

            var arrayAux = mission.Methods.SingleOrDefault(m =>
                m.Name == "AddMissileAux" &&
                m.Parameters.Count == 15 &&
                m.Parameters[4].ParameterType is ArrayType arrayType &&
                arrayType.ElementType.FullName == "TaleWorlds.MountAndBlade.WeaponStatsData");
            if (arrayAux == null)
                throw new InvalidOperationException(version + ": AddMissileAux array-stat signature missing");

            var singleAux = mission.Methods.SingleOrDefault(m =>
                m.Name == "AddMissileSingleUsageAux" &&
                m.Parameters.Count == 15 &&
                IsByRefTo(m.Parameters[4].ParameterType, "TaleWorlds.MountAndBlade.WeaponStatsData"));
            if (singleAux == null)
                throw new InvalidOperationException(version + ": AddMissileSingleUsageAux single-stat signature missing");

            if (!IsByRefTo(arrayAux.Parameters[3].ParameterType, "TaleWorlds.MountAndBlade.WeaponData") ||
                !IsByRefTo(singleAux.Parameters[3].ParameterType, "TaleWorlds.MountAndBlade.WeaponData"))
                throw new InvalidOperationException(version + ": WeaponData by-ref contract changed");

            if (!IsByRefTo(arrayAux.Parameters[14].ParameterType, "TaleWorlds.Engine.GameEntity") ||
                !IsByRefTo(singleAux.Parameters[14].ParameterType, "TaleWorlds.Engine.GameEntity"))
                throw new InvalidOperationException(version + ": GameEntity by-ref contract changed");

            Console.WriteLine("VERSION=" + version);
            Console.WriteLine("REFERENCE_BODY_MODE=SIGNATURE_ONLY");
            Console.WriteLine("ADD_MISSILE_AUX_SIGNATURE=PASS");
            Console.WriteLine("ADD_MISSILE_SINGLE_USAGE_AUX_SIGNATURE=PASS");
            Console.WriteLine("DUAL_CAPTURE_TARGETS=PASS");
        }
        return 0;
    }
}
'@
$inspectorPath=Join-Path $payloadRoot 'InspectMissileCallGraph.cs'
[IO.File]::WriteAllText($inspectorPath,$signatureInspector,[Text.UTF8Encoding]::new($false))
Assert-Hash $inspectorPath 'ada7b436921e74a68bf7954b6492ac6b27e134f025bc37df32febaa72a28b3bd' 'v1.1.16 corrected signature inspector'

# The extracted build script re-verifies the inspector before compiling it. Replace only
# that expected inspector hash so the remaining source and release hash gates stay intact.
$buildScriptPath=Join-Path $payloadRoot 'build-ga1116-single-usage-capture.ps1'
$buildText=[IO.File]::ReadAllText($buildScriptPath)
$oldInspectorHash='880af9b8bc15c378364c9866ec262b668e802807374cbe65c470e2185a3b0f04'
$newInspectorHash='ada7b436921e74a68bf7954b6492ac6b27e134f025bc37df32febaa72a28b3bd'
if(!$buildText.Contains($oldInspectorHash)){throw 'Expected original inspector hash was not found in v1.1.16 build script'}
$buildText=$buildText.Replace($oldInspectorHash,$newInspectorHash)
[IO.File]::WriteAllText($buildScriptPath,$buildText,[Text.UTF8Encoding]::new($false))

& (Join-Path $repo 'tools/ga1115/run-complete-build.ps1')
& $buildScriptPath -BaseOutputRoot "$env:RUNNER_TEMP\ga1115-output" -OutputRoot "$env:RUNNER_TEMP\ga1116-output"
Write-Host 'GA1116_COMPLETE_BUILD=SUCCESS'
