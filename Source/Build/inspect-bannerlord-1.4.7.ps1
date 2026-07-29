param(
    [Parameter(Mandatory = $true)]
    [string]$RuntimeAssembly
)

$ErrorActionPreference = 'Stop'
$version = '1.4.7.117484'
$work = Join-Path $env:RUNNER_TEMP 'ExtremeRagdoll147Probe'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>netcoreapp3.1</TargetFramework>
    <LangVersion>7.3</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Mono.Cecil" Version="0.10.1" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.Native" Version="1.4.7.117484" GeneratePathProperty="true" />
  </ItemGroup>
</Project>
'@
[System.IO.File]::WriteAllText((Join-Path $work 'Probe.csproj'), $project, [System.Text.UTF8Encoding]::new($false))

$program = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("usage: Probe <package-root> <runtime.dll>");

        string packageRoot = args[0];
        string runtimePath = args[1];
        string mountAndBladePath = Directory.GetFiles(packageRoot, "TaleWorlds.MountAndBlade.dll", SearchOption.AllDirectories).FirstOrDefault();
        if (mountAndBladePath == null)
            throw new FileNotFoundException("TaleWorlds.MountAndBlade.dll not found under " + packageRoot);

        Console.WriteLine("Bannerlord 1.4.7 assembly: " + mountAndBladePath);
        using (AssemblyDefinition mb = AssemblyDefinition.ReadAssembly(mountAndBladePath))
        using (AssemblyDefinition runtime = AssemblyDefinition.ReadAssembly(runtimePath))
        {
            TypeDefinition missionBehavior = RequireType(mb, "TaleWorlds.MountAndBlade.MissionBehavior");
            TypeDefinition agent = RequireType(mb, "TaleWorlds.MountAndBlade.Agent");
            TypeDefinition subModule = RequireType(mb, "TaleWorlds.MountAndBlade.MBSubModuleBase");
            TypeDefinition behavior = RequireType(runtime, "ExtremeRagdoll.SafeRuntime.SafeRagdollBehavior");

            PrintMethods(missionBehavior, "OnRegisterBlow");
            PrintMethods(agent, "HandleBlow");
            PrintMethods(agent, "Die");
            PrintMethods(agent, "StartRagdollAsCorpse");
            PrintMethods(agent, "EndRagdollAsCorpse");
            PrintMethods(agent, "ApplyForceOnRagdoll");
            PrintMethods(subModule, "OnMissionBehaviorInitialize");

            MethodDefinition runtimeOverride = behavior.Methods.SingleOrDefault(m => m.Name == "OnRegisterBlow");
            if (runtimeOverride == null)
                throw new InvalidOperationException("Runtime OnRegisterBlow override is missing.");

            MethodDefinition[] candidates = missionBehavior.Methods
                .Where(m => m.Name == "OnRegisterBlow" && m.Parameters.Count == runtimeOverride.Parameters.Count)
                .ToArray();
            Console.WriteLine("Runtime override: " + runtimeOverride.FullName);
            Console.WriteLine("Runtime explicit overrides: " + string.Join(" | ", runtimeOverride.Overrides.Select(o => o.FullName)));

            bool signatureMatch = candidates.Any(candidate => SignatureEquals(runtimeOverride, candidate));
            bool overrideTargetMatch = runtimeOverride.Overrides.Any(o =>
                o.DeclaringType.FullName == missionBehavior.FullName &&
                candidates.Any(candidate => MethodReferenceEquals(o, candidate)));

            Console.WriteLine("1.4.7 signature match: " + signatureMatch);
            Console.WriteLine("1.4.7 explicit override target match: " + overrideTargetMatch);
            if (!signatureMatch || !overrideTargetMatch)
                throw new InvalidOperationException("ExtremeRagdoll.dll contains a MissionBehavior.OnRegisterBlow override that is not valid against Bannerlord 1.4.7.");
        }
        return 0;
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName)
    {
        TypeDefinition type = assembly.MainModule.GetType(fullName);
        if (type == null) throw new InvalidOperationException("Missing type: " + fullName);
        return type;
    }

    private static void PrintMethods(TypeDefinition type, string name)
    {
        foreach (MethodDefinition method in type.Methods.Where(m => m.Name == name))
            Console.WriteLine(type.FullName + "." + name + ": " + method.FullName + " attrs=" + method.Attributes);
    }

    private static bool SignatureEquals(MethodDefinition left, MethodDefinition right)
    {
        if (left.ReturnType.FullName != right.ReturnType.FullName || left.Parameters.Count != right.Parameters.Count)
            return false;
        for (int i = 0; i < left.Parameters.Count; i++)
        {
            if (left.Parameters[i].ParameterType.FullName != right.Parameters[i].ParameterType.FullName)
                return false;
        }
        return true;
    }

    private static bool MethodReferenceEquals(MethodReference left, MethodDefinition right)
    {
        if (left.Name != right.Name || left.ReturnType.FullName != right.ReturnType.FullName || left.Parameters.Count != right.Parameters.Count)
            return false;
        for (int i = 0; i < left.Parameters.Count; i++)
        {
            if (left.Parameters[i].ParameterType.FullName != right.Parameters[i].ParameterType.FullName)
                return false;
        }
        return true;
    }
}
'@
[System.IO.File]::WriteAllText((Join-Path $work 'Program.cs'), $program, [System.Text.UTF8Encoding]::new($false))

Push-Location $work
try {
    dotnet restore .\Probe.csproj
    if ($LASTEXITCODE -ne 0) { throw "Reference assembly restore failed with exit code $LASTEXITCODE." }

    $packageRoot = Join-Path $HOME ".nuget\packages\bannerlord.referenceassemblies.native\$version"
    if (-not (Test-Path $packageRoot)) { throw "Reference package root not found: $packageRoot" }

    dotnet run --project .\Probe.csproj --no-restore -- $packageRoot ([System.IO.Path]::GetFullPath($RuntimeAssembly))
    if ($LASTEXITCODE -ne 0) { throw "Bannerlord 1.4.7 API probe failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
