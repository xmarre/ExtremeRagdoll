using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class InspectLaunchIL
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

    private static string FormatOperand(object operand)
    {
        if (operand == null) return string.Empty;
        if (operand is Instruction target) return "IL_" + target.Offset.ToString("X4");
        if (operand is Instruction[] targets) return string.Join(",", targets.Select(t => "IL_" + t.Offset.ToString("X4")));
        if (operand is VariableDefinition variable) return "V_" + variable.Index + ":" + variable.VariableType.FullName;
        if (operand is ParameterDefinition parameter) return parameter.Name + ":" + parameter.ParameterType.FullName;
        return operand.ToString();
    }

    private static void DumpMethod(MethodDefinition method)
    {
        Console.WriteLine("METHOD=" + method.FullName);
        Console.WriteLine("ATTRIBUTES=" + method.Attributes + " IMPL=" + method.ImplAttributes + " BODY=" + method.HasBody);
        if (!method.HasBody)
        {
            Console.WriteLine("END_METHOD");
            return;
        }
        Console.WriteLine("LOCALS=" + string.Join(" | ", method.Body.Variables.Select(v => "V_" + v.Index + ":" + v.VariableType.FullName)));
        foreach (var instruction in method.Body.Instructions)
            Console.WriteLine("IL_" + instruction.Offset.ToString("X4") + ": " + instruction.OpCode + " " + FormatOperand(instruction.Operand));
        Console.WriteLine("END_METHOD");
    }

    public static int Main()
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packages))
            packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string root = Path.Combine(packages, "bannerlord.referenceassemblies.core", "1.3.15.110062");
        string path = Directory.GetFiles(root, "TaleWorlds.MountAndBlade.dll", SearchOption.AllDirectories)
            .First(p => p.IndexOf(Path.DirectorySeparatorChar + "net472" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0);
        Console.WriteLine("ASSEMBLY=" + path);
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var allMethods = AllTypes(assembly.MainModule).SelectMany(t => t.Methods).ToList();
        var callers = allMethods.Where(m => m.HasBody && m.Body.Instructions.Any(i =>
        {
            if (!(i.Operand is MethodReference mr)) return false;
            return mr.Name == "AddMissileAux" || mr.Name == "AddCustomMissile";
        })).ToList();
        Console.WriteLine("CALLER_COUNT=" + callers.Count);
        foreach (var method in callers.OrderBy(m => m.FullName))
            DumpMethod(method);

        string[] names = { "AddCustomMissile", "AddMissileAux", "OnAgentShootMissile", "ShootMissile", "GetWeaponStatsData", "GetAmmoWeaponStatsData", "GetWeaponData", "GetAmmoWeaponData" };
        foreach (var method in allMethods.Where(m => names.Contains(m.Name)).OrderBy(m => m.FullName))
            DumpMethod(method);

        foreach (var method in allMethods.Where(m => m.HasBody && m.Body.Instructions.Any(i =>
        {
            if (!(i.Operand is MethodReference mr)) return false;
            return mr.Name.IndexOf("Missile", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (mr.Name.IndexOf("Damage", StringComparison.OrdinalIgnoreCase) >= 0 || mr.Name.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0);
        })).OrderBy(m => m.FullName))
            DumpMethod(method);
        return 0;
    }
}
