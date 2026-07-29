param(
    [Parameter(Mandatory = $true)]
    [string]$RuntimeAssembly
)

$ErrorActionPreference = 'Stop'
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
    <PackageReference Include="Bannerlord.ReferenceAssemblies" Version="1.4.7.117484" />
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
            throw new ArgumentException("usage: Probe <package-cache> <runtime.dll>");

        string packageCache = args[0];
        string runtimePath = args[1];
        string[] referenceDirectories = Directory.GetDirectories(packageCache, "ref", SearchOption.AllDirectories)
            .Where(path => path.IndexOf("bannerlord.referenceassemblies", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           path.IndexOf("1.4.7.117484", StringComparison.OrdinalIgnoreCase) >= 0)
            .SelectMany(path => Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly).Concat(new[] { path }))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string mountAndBladePath = referenceDirectories
            .Select(path => Path.Combine(path, "TaleWorlds.MountAndBlade.dll"))
            .FirstOrDefault(File.Exists);
        if (mountAndBladePath == null)
            throw new FileNotFoundException("TaleWorlds.MountAndBlade.dll was not restored in the complete Bannerlord 1.4.7 reference set.");

        var resolver = new DefaultAssemblyResolver();
        foreach (string path in referenceDirectories)
            resolver.AddSearchDirectory(path);
        resolver.AddSearchDirectory(Path.GetDirectoryName(runtimePath));
        var parameters = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };

        Console.WriteLine("Bannerlord 1.4.7 assembly: " + mountAndBladePath);
        using (AssemblyDefinition mb = AssemblyDefinition.ReadAssembly(mountAndBladePath, parameters))
        using (AssemblyDefinition runtime = AssemblyDefinition.ReadAssembly(runtimePath, parameters))
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
            bool signatureMatch = candidates.Any(candidate => SignatureEquals(runtimeOverride, candidate));
            bool overrideTargetMatch = runtimeOverride.Overrides.Any(o =>
                o.DeclaringType.FullName == missionBehavior.FullName &&
                candidates.Any(candidate => MethodReferenceEquals(o, candidate)));
            Console.WriteLine("1.4.7 OnRegisterBlow signature match: " + signatureMatch);
            Console.WriteLine("1.4.7 explicit override target match: " + overrideTargetMatch);
            if (!signatureMatch || !overrideTargetMatch)
                throw new InvalidOperationException("ExtremeRagdoll.dll contains an invalid MissionBehavior.OnRegisterBlow override for Bannerlord 1.4.7.");

            var failures = new SortedSet<string>(StringComparer.Ordinal);
            foreach (TypeDefinition type in AllTypes(runtime.MainModule.Types))
            {
                ResolveType(type.BaseType, failures, "base " + type.FullName);
                foreach (InterfaceImplementation iface in type.Interfaces)
                    ResolveType(iface.InterfaceType, failures, "interface " + type.FullName);
                foreach (FieldDefinition field in type.Fields)
                    ResolveType(field.FieldType, failures, "field " + field.FullName);
                foreach (MethodDefinition method in type.Methods)
                {
                    ResolveType(method.ReturnType, failures, "return " + method.FullName);
                    foreach (ParameterDefinition parameter in method.Parameters)
                        ResolveType(parameter.ParameterType, failures, "parameter " + method.FullName);
                    foreach (MethodReference methodOverride in method.Overrides)
                        ResolveMethod(methodOverride, failures, "override " + method.FullName);
                    if (!method.HasBody) continue;
                    foreach (Instruction instruction in method.Body.Instructions)
                    {
                        MethodReference called = instruction.Operand as MethodReference;
                        if (called != null) ResolveMethod(called, failures, method.FullName);
                        FieldReference accessed = instruction.Operand as FieldReference;
                        if (accessed != null) ResolveField(accessed, failures, method.FullName);
                        TypeReference referenced = instruction.Operand as TypeReference;
                        if (referenced != null) ResolveType(referenced, failures, method.FullName);
                    }
                }
            }

            if (failures.Count != 0)
            {
                foreach (string failure in failures)
                    Console.Error.WriteLine("UNRESOLVED_1_4_7: " + failure);
                throw new InvalidOperationException("ExtremeRagdoll.dll has " + failures.Count + " TaleWorlds member reference(s) that do not resolve against Bannerlord 1.4.7.");
            }
            Console.WriteLine("All direct TaleWorlds runtime references resolve against Bannerlord 1.4.7.");
        }
        return 0;
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
    {
        foreach (TypeDefinition type in roots)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes))
                yield return nested;
        }
    }

    private static bool IsTaleWorlds(TypeReference type)
    {
        TypeReference current = type;
        while (current is TypeSpecification)
            current = ((TypeSpecification)current).ElementType;
        return current != null && current.Scope != null &&
            current.Scope.Name != null && current.Scope.Name.StartsWith("TaleWorlds.", StringComparison.Ordinal);
    }

    private static void ResolveType(TypeReference reference, ISet<string> failures, string owner)
    {
        if (reference == null || !IsTaleWorlds(reference)) return;
        try { if (reference.Resolve() == null) failures.Add(owner + " -> type " + reference.FullName); }
        catch (Exception ex) { failures.Add(owner + " -> type " + reference.FullName + " (" + ex.GetType().Name + ")"); }
    }

    private static void ResolveMethod(MethodReference reference, ISet<string> failures, string owner)
    {
        if (reference == null || !IsTaleWorlds(reference.DeclaringType)) return;
        try { if (reference.Resolve() == null) failures.Add(owner + " -> method " + reference.FullName); }
        catch (Exception ex) { failures.Add(owner + " -> method " + reference.FullName + " (" + ex.GetType().Name + ")"); }
    }

    private static void ResolveField(FieldReference reference, ISet<string> failures, string owner)
    {
        if (reference == null || !IsTaleWorlds(reference.DeclaringType)) return;
        try { if (reference.Resolve() == null) failures.Add(owner + " -> field " + reference.FullName); }
        catch (Exception ex) { failures.Add(owner + " -> field " + reference.FullName + " (" + ex.GetType().Name + ")"); }
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
            if (left.Parameters[i].ParameterType.FullName != right.Parameters[i].ParameterType.FullName) return false;
        return true;
    }

    private static bool MethodReferenceEquals(MethodReference left, MethodDefinition right)
    {
        if (left.Name != right.Name || left.ReturnType.FullName != right.ReturnType.FullName || left.Parameters.Count != right.Parameters.Count)
            return false;
        for (int i = 0; i < left.Parameters.Count; i++)
            if (left.Parameters[i].ParameterType.FullName != right.Parameters[i].ParameterType.FullName) return false;
        return true;
    }
}
'@
[System.IO.File]::WriteAllText((Join-Path $work 'Program.cs'), $program, [System.Text.UTF8Encoding]::new($false))

Push-Location $work
try {
    dotnet restore .\Probe.csproj
    if ($LASTEXITCODE -ne 0) { throw "Reference assembly restore failed with exit code $LASTEXITCODE." }
    $packageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
    dotnet run --project .\Probe.csproj --no-restore -- $packageCache ([System.IO.Path]::GetFullPath($RuntimeAssembly))
    if ($LASTEXITCODE -ne 0) { throw "Bannerlord 1.4.7 API probe failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
