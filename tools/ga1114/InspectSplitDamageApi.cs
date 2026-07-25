using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class InspectSplitDamageApi
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
            foreach (var descendant in AllNested(nested))
                yield return descendant;
        }
    }

    private static void DumpType(string assemblyPath, string typeName)
    {
        using (var assembly = AssemblyDefinition.ReadAssembly(assemblyPath))
        {
            var type = AllTypes(assembly.MainModule).FirstOrDefault(t =>
                t.FullName == typeName || t.FullName.Replace('/', '+') == typeName || t.Name == typeName);
            if (type == null)
            {
                Console.WriteLine("TYPE_NOT_FOUND=" + typeName);
                return;
            }

            Console.WriteLine("TYPE=" + type.FullName + " ASSEMBLY=" + assemblyPath);
            foreach (var field in type.Fields.OrderBy(f => f.Name))
                Console.WriteLine($"FIELD {field.Attributes} {field.FieldType.FullName} {field.Name}");
            foreach (var property in type.Properties.OrderBy(p => p.Name))
                Console.WriteLine($"PROPERTY {property.PropertyType.FullName} {property.Name} GET={property.GetMethod != null} SET={property.SetMethod != null}");
            foreach (var method in type.Methods.OrderBy(m => m.Name))
            {
                string parameters = string.Join(", ", method.Parameters.Select(p => (p.ParameterType.IsByReference ? "ref " : "") + p.ParameterType.FullName + " " + p.Name));
                Console.WriteLine($"METHOD {method.Attributes} {method.ReturnType.FullName} {method.Name}({parameters}) BODY={method.HasBody}");
            }
        }
    }

    private static void DumpMethodIL(string assemblyPath, string typeName, string methodName)
    {
        using (var assembly = AssemblyDefinition.ReadAssembly(assemblyPath))
        {
            var type = AllTypes(assembly.MainModule).FirstOrDefault(t => t.FullName == typeName || t.FullName.Replace('/', '+') == typeName);
            if (type == null)
            {
                Console.WriteLine("IL_TYPE_NOT_FOUND=" + typeName);
                return;
            }
            foreach (var method in type.Methods.Where(m => m.Name == methodName))
            {
                Console.WriteLine("IL_METHOD=" + method.FullName + " BODY=" + method.HasBody + " ASSEMBLY=" + assemblyPath);
                if (!method.HasBody)
                    continue;
                foreach (var instruction in method.Body.Instructions)
                    Console.WriteLine("  " + instruction);
            }
        }
    }

    private static string[] FindAssemblies(string root, string fileName)
    {
        return Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static int Main()
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packages))
            packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var mountAssemblies = FindAssemblies(packages, "TaleWorlds.MountAndBlade.dll");
        var coreAssemblies = FindAssemblies(packages, "TaleWorlds.Core.dll");
        Console.WriteLine("MOUNT_ASSEMBLIES=" + mountAssemblies.Length);
        foreach (var path in mountAssemblies)
            Console.WriteLine("MOUNT=" + path);
        Console.WriteLine("CORE_ASSEMBLIES=" + coreAssemblies.Length);
        foreach (var path in coreAssemblies)
            Console.WriteLine("CORE=" + path);

        string mount = mountAssemblies.FirstOrDefault(p => p.IndexOf("1.3.15.110062", StringComparison.OrdinalIgnoreCase) >= 0 && p.IndexOf("ref", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? mountAssemblies.First();
        string core = coreAssemblies.FirstOrDefault(p => p.IndexOf("1.3.15.110062", StringComparison.OrdinalIgnoreCase) >= 0 && p.IndexOf("ref", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? coreAssemblies.First();

        DumpType(mount, "TaleWorlds.MountAndBlade.Mission/Missile");
        DumpType(mount, "TaleWorlds.MountAndBlade.MissionWeapon");
        DumpType(mount, "TaleWorlds.MountAndBlade.WeaponStatsData");
        DumpType(mount, "TaleWorlds.MountAndBlade.WeaponData");
        DumpType(mount, "TaleWorlds.MountAndBlade.AttackCollisionData");
        DumpType(mount, "TaleWorlds.MountAndBlade.Blow");
        DumpType(mount, "TaleWorlds.MountAndBlade.Mission");
        DumpType(core, "TaleWorlds.Core.WeaponComponentData");

        foreach (var candidate in mountAssemblies.Where(p => p.IndexOf("1.3.15.110062", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            DumpMethodIL(candidate, "TaleWorlds.MountAndBlade.Mission", "AddCustomMissile");
            DumpMethodIL(candidate, "TaleWorlds.MountAndBlade.Mission", "AddMissileAux");
            DumpMethodIL(candidate, "TaleWorlds.MountAndBlade.Mission", "CreateMissileBlow");
        }
        return 0;
    }
}
