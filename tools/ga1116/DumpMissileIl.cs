using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class DumpMissileIl
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

    private static TypeDefinition FindType(ModuleDefinition module, string fullName)
        => AllTypes(module).FirstOrDefault(t => t.FullName == fullName || t.FullName.Replace('/', '+') == fullName);

    private static string FormatOperand(object operand)
    {
        if (operand == null) return string.Empty;
        if (operand is Instruction target) return "IL_" + target.Offset.ToString("X4");
        if (operand is Instruction[] targets) return string.Join(",", targets.Select(t => "IL_" + t.Offset.ToString("X4")));
        if (operand is MethodReference method) return method.FullName;
        if (operand is FieldReference field) return field.FullName;
        if (operand is TypeReference type) return type.FullName;
        if (operand is ParameterDefinition parameter) return "arg:" + parameter.Index + ":" + parameter.Name;
        if (operand is VariableDefinition variable) return "loc:" + variable.Index + ":" + variable.VariableType.FullName;
        return operand.ToString();
    }

    private static void DumpMethod(TypeDefinition type, string methodName)
    {
        foreach (var method in type.Methods.Where(m => m.Name == methodName))
        {
            Console.WriteLine("=== METHOD " + method.FullName + " ===");
            Console.WriteLine("ATTR=" + method.Attributes + " IMPL=" + method.ImplAttributes + " BODY=" + method.HasBody);
            if (!method.HasBody) continue;
            foreach (var variable in method.Body.Variables)
                Console.WriteLine("LOCAL " + variable.Index + " " + variable.VariableType.FullName);
            foreach (var instruction in method.Body.Instructions)
                Console.WriteLine("IL_" + instruction.Offset.ToString("X4") + ": " + instruction.OpCode + " " + FormatOperand(instruction.Operand));
            foreach (var handler in method.Body.ExceptionHandlers)
                Console.WriteLine("HANDLER " + handler.HandlerType + " TRY=IL_" + handler.TryStart.Offset.ToString("X4") + "-IL_" + handler.TryEnd.Offset.ToString("X4") + " HANDLER=IL_" + handler.HandlerStart.Offset.ToString("X4") + "-IL_" + handler.HandlerEnd.Offset.ToString("X4"));
        }
    }

    public static int Main()
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packages))
            packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string root = Path.Combine(packages, "bannerlord.referenceassemblies.core", "1.3.15.110062");
        string path = Directory.GetFiles(root, "TaleWorlds.MountAndBlade.dll", SearchOption.AllDirectories)
            .First(p => p.IndexOf("net472", StringComparison.OrdinalIgnoreCase) >= 0);
        Console.WriteLine("ASSEMBLY=" + path);
        using (var assembly = AssemblyDefinition.ReadAssembly(path))
        {
            var mission = FindType(assembly.MainModule, "TaleWorlds.MountAndBlade.Mission");
            DumpMethod(mission, "AddCustomMissile");
            DumpMethod(mission, "AddMissileAux");
            DumpMethod(mission, "OnAgentShootMissile");
            DumpMethod(mission, "MissileHitCallback");
            DumpMethod(mission, "CreateMissileBlow");
            var missionWeapon = FindType(assembly.MainModule, "TaleWorlds.MountAndBlade.MissionWeapon");
            DumpMethod(missionWeapon, "GetWeaponData");
            DumpMethod(missionWeapon, "GetWeaponStatsData");
            var agent = FindType(assembly.MainModule, "TaleWorlds.MountAndBlade.Agent");
            DumpMethod(agent, "get_Equipment");
        }
        return 0;
    }
}
