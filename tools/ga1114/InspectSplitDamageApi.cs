using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

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
            if (type.BaseType != null)
                Console.WriteLine("BASE=" + type.BaseType.FullName);
            foreach (var field in type.Fields.OrderBy(f => f.Name))
                Console.WriteLine($"FIELD {field.Attributes} {field.FieldType.FullName} {field.Name}");
            foreach (var property in type.Properties.OrderBy(p => p.Name))
                Console.WriteLine($"PROPERTY {property.PropertyType.FullName} {property.Name} GET={property.GetMethod != null} SET={property.SetMethod != null}");
            foreach (var method in type.Methods.OrderBy(m => m.Name))
            {
                string parameters = string.Join(", ", method.Parameters.Select(p =>
                    (p.ParameterType.IsByReference ? "ref " : "") + p.ParameterType.FullName + " " + p.Name));
                Console.WriteLine($"METHOD {method.Attributes} {method.ReturnType.FullName} {method.Name}({parameters}) BODY={method.HasBody}");
            }
        }
    }

    private static void DumpTypeNamesContaining(string assemblyPath, params string[] fragments)
    {
        using (var assembly = AssemblyDefinition.ReadAssembly(assemblyPath))
        {
            foreach (var type in AllTypes(assembly.MainModule).Where(t =>
                fragments.Any(f => t.FullName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(t => t.FullName))
                Console.WriteLine("MATCHING_TYPE=" + type.FullName);
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
        string mount = mountAssemblies.FirstOrDefault(p => p.IndexOf("1.3.15.110062", StringComparison.OrdinalIgnoreCase) >= 0 && p.IndexOf("net472", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? mountAssemblies.First();
        string core = coreAssemblies.FirstOrDefault(p => p.IndexOf("1.3.15.110062", StringComparison.OrdinalIgnoreCase) >= 0 && p.IndexOf("net472", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? coreAssemblies.First();

        Console.WriteLine("MOUNT=" + mount);
        Console.WriteLine("CORE=" + core);
        DumpTypeNamesContaining(mount, "DamageModel", "CombatMechanics", "MissionBehavior", "MissionLogic", "CombatLog");

        DumpType(mount, "TaleWorlds.MountAndBlade.Mission/Missile");
        DumpType(mount, "TaleWorlds.MountAndBlade.MissionWeapon");
        DumpType(mount, "TaleWorlds.MountAndBlade.WeaponStatsData");
        DumpType(mount, "TaleWorlds.MountAndBlade.WeaponData");
        DumpType(mount, "TaleWorlds.MountAndBlade.AttackCollisionData");
        DumpType(mount, "TaleWorlds.MountAndBlade.Blow");
        DumpType(mount, "TaleWorlds.MountAndBlade.CombatLogData");
        DumpType(mount, "TaleWorlds.MountAndBlade.MissionBehavior");
        DumpType(mount, "TaleWorlds.MountAndBlade.MissionLogic");
        DumpType(mount, "TaleWorlds.MountAndBlade.Agent");
        DumpType(mount, "TaleWorlds.MountAndBlade.MissionGameModels");
        DumpType(mount, "TaleWorlds.MountAndBlade.AgentApplyDamageModel");
        DumpType(mount, "TaleWorlds.MountAndBlade.MissionCombatMechanicsHelper");
        DumpType(mount, "TaleWorlds.MountAndBlade.Mission");
        DumpType(core, "TaleWorlds.Core.WeaponComponentData");

        foreach (var candidate in mountAssemblies.Where(p => p.IndexOf("1.3.15.110062", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            DumpMethodIL(candidate, "TaleWorlds.MountAndBlade.Mission", "AddCustomMissile");
            DumpMethodIL(candidate, "TaleWorlds.MountAndBlade.Mission", "AddMissileAux");
            DumpMethodIL(candidate, "TaleWorlds.MountAndBlade.Mission", "CreateMissileBlow");
            DumpMethodIL(candidate, "TaleWorlds.MountAndBlade.Mission", "OnAgentHit");
        }
        return 0;
    }
}
