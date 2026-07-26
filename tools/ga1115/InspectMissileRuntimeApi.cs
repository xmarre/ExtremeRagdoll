using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class InspectMissileRuntimeApi
{
    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in AllNested(type))
                yield return nested;
        }
    }

    private static IEnumerable<TypeDefinition> AllNested(TypeDefinition type)
    {
        foreach (var nested in type.NestedTypes)
        {
            yield return nested;
            foreach (var child in AllNested(nested))
                yield return child;
        }
    }

    private static TypeDefinition FindType(ModuleDefinition module, string name)
    {
        return AllTypes(module).FirstOrDefault(t => t.FullName == name || t.FullName.Replace('/', '+') == name || t.Name == name);
    }

    private static void DumpType(AssemblyDefinition assembly, string name)
    {
        var type = FindType(assembly.MainModule, name);
        if (type == null)
        {
            Console.WriteLine("TYPE_NOT_FOUND=" + name);
            return;
        }
        Console.WriteLine("TYPE=" + type.FullName + " BASE=" + type.BaseType);
        foreach (var field in type.Fields.OrderBy(f => f.Name))
            Console.WriteLine($"FIELD {field.Attributes} {field.FieldType.FullName} {field.Name}");
        foreach (var property in type.Properties.OrderBy(p => p.Name))
            Console.WriteLine($"PROPERTY {property.PropertyType.FullName} {property.Name} GET={property.GetMethod != null} SET={property.SetMethod != null}");
        foreach (var method in type.Methods.OrderBy(m => m.Name))
        {
            string parameters = string.Join(", ", method.Parameters.Select(p => (p.ParameterType.IsByReference ? "ref " : "") + p.ParameterType.FullName + " " + p.Name));
            Console.WriteLine($"METHOD {method.Attributes} IMPL={method.ImplAttributes} RVA={method.RVA} BODY={method.HasBody} {method.ReturnType.FullName} {method.Name}({parameters})");
        }
    }

    public static int Main()
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packages))
            packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string root = Path.Combine(packages, "bannerlord.referenceassemblies.core", "1.3.15.110062");
        string[] assemblies = Directory.GetFiles(root, "TaleWorlds.MountAndBlade.dll", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        Console.WriteLine("ASSEMBLY_COUNT=" + assemblies.Length);
        foreach (string path in assemblies)
        {
            Console.WriteLine("ASSEMBLY=" + path);
            using (var assembly = AssemblyDefinition.ReadAssembly(path))
            {
                DumpType(assembly, "TaleWorlds.MountAndBlade.MBMissile");
                DumpType(assembly, "TaleWorlds.MountAndBlade.Mission/Missile");
                DumpType(assembly, "TaleWorlds.MountAndBlade.MissionWeapon");
                var mission = FindType(assembly.MainModule, "TaleWorlds.MountAndBlade.Mission");
                if (mission != null)
                {
                    foreach (var method in mission.Methods.Where(m => m.Name.IndexOf("Missile", StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(m => m.Name))
                    {
                        string parameters = string.Join(", ", method.Parameters.Select(p => p.ParameterType.FullName + " " + p.Name));
                        Console.WriteLine($"MISSION_METHOD {method.Attributes} IMPL={method.ImplAttributes} RVA={method.RVA} BODY={method.HasBody} {method.ReturnType.FullName} {method.Name}({parameters})");
                    }
                }
            }
        }
        return 0;
    }
}
