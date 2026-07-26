using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class InspectDamageApi
{
    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in AllNested(type)) yield return nested;
        }
    }
    private static IEnumerable<TypeDefinition> AllNested(TypeDefinition type)
    {
        foreach (var nested in type.NestedTypes)
        {
            yield return nested;
            foreach (var child in AllNested(nested)) yield return child;
        }
    }
    private static void DumpType(TypeDefinition type)
    {
        Console.WriteLine("TYPE=" + type.FullName + " BASE=" + type.BaseType);
        foreach (var field in type.Fields.OrderBy(f => f.Name))
            Console.WriteLine(" FIELD " + field.Attributes + " " + field.FieldType.FullName + " " + field.Name);
        foreach (var property in type.Properties.OrderBy(p => p.Name))
            Console.WriteLine(" PROPERTY " + property.PropertyType.FullName + " " + property.Name + " GET=" + (property.GetMethod != null) + " SET=" + (property.SetMethod != null));
        foreach (var method in type.Methods.OrderBy(m => m.Name))
        {
            string ps = string.Join(", ", method.Parameters.Select(p => (p.ParameterType.IsByReference ? "ref " : "") + p.ParameterType.FullName + " " + p.Name));
            Console.WriteLine(" METHOD " + method.Attributes + " " + method.ReturnType.FullName + " " + method.Name + "(" + ps + ")");
        }
    }
    public static int Main()
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packages)) packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string root = Path.Combine(packages, "bannerlord.referenceassemblies.core", "1.3.15.110062");
        var dlls = Directory.GetFiles(root, "TaleWorlds.*.dll", SearchOption.AllDirectories)
            .Where(p => p.IndexOf(Path.DirectorySeparatorChar + "net472" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(p => p).ToArray();
        string[] exact = {
            "TaleWorlds.MountAndBlade.AgentStatCalculateModel",
            "TaleWorlds.MountAndBlade.AgentApplyDamageModel",
            "TaleWorlds.MountAndBlade.MissionGameModels",
            "TaleWorlds.MountAndBlade.MissionCombatMechanicsHelper",
            "TaleWorlds.MountAndBlade.Blow",
            "TaleWorlds.MountAndBlade.AttackCollisionData",
            "TaleWorlds.MountAndBlade.MissionWeapon",
            "TaleWorlds.MountAndBlade.Agent"
        };
        foreach (string dll in dlls)
        {
            using var asm = AssemblyDefinition.ReadAssembly(dll);
            var types = AllTypes(asm.MainModule).ToList();
            foreach (var type in types.Where(t => exact.Contains(t.FullName) ||
                t.Name.IndexOf("ApplyDamageModel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.Name.IndexOf("StatCalculateModel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.Name.IndexOf("CombatMechanics", StringComparison.OrdinalIgnoreCase) >= 0))
                DumpType(type);
            var mission = types.FirstOrDefault(t => t.FullName == "TaleWorlds.MountAndBlade.Mission");
            if (mission != null)
            {
                Console.WriteLine("TYPE=" + mission.FullName + " SELECTED_METHODS");
                foreach (var method in mission.Methods.Where(m =>
                    m.Name.IndexOf("Damage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Name.IndexOf("Blow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Name.IndexOf("Hit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Name.IndexOf("Missile", StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(m => m.Name))
                {
                    string ps = string.Join(", ", method.Parameters.Select(p => (p.ParameterType.IsByReference ? "ref " : "") + p.ParameterType.FullName + " " + p.Name));
                    Console.WriteLine(" METHOD " + method.Attributes + " " + method.ReturnType.FullName + " " + method.Name + "(" + ps + ")");
                }
            }
        }
        return 0;
    }
}
