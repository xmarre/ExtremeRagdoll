using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class InspectNativeShotCalls
{
    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (TypeDefinition type in module.Types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllNested(type))
                yield return nested;
        }
    }

    private static IEnumerable<TypeDefinition> AllNested(TypeDefinition type)
    {
        foreach (TypeDefinition nested in type.NestedTypes)
        {
            yield return nested;
            foreach (TypeDefinition child in AllNested(nested))
                yield return child;
        }
    }

    private static IEnumerable<MethodReference> Calls(MethodDefinition method)
    {
        if (method == null || !method.HasBody)
            return Enumerable.Empty<MethodReference>();
        return method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
            .Select(i => i.Operand as MethodReference)
            .Where(m => m != null);
    }

    private static void DumpMethod(MethodDefinition method)
    {
        Console.WriteLine("METHOD=" + method.FullName);
        foreach (MethodReference call in Calls(method))
            Console.WriteLine("  CALL=" + call.FullName);
    }

    public static int Main(string[] args)
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packages))
            packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string version = args.Length > 0 ? args[0] : "1.3.15.110062";
        string root = Path.Combine(packages, "bannerlord.referenceassemblies.core", version);
        string path = Directory.GetFiles(root, "TaleWorlds.MountAndBlade.dll", SearchOption.AllDirectories)
            .First(p => p.Replace('\\', '/').Contains("/ref/net472/"));
        Console.WriteLine("ASSEMBLY=" + path);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path);
        TypeDefinition mission = AllTypes(assembly.MainModule)
            .Single(t => t.FullName == "TaleWorlds.MountAndBlade.Mission");

        MethodDefinition onShoot = mission.Methods.Single(m => m.Name == "OnAgentShootMissile");
        MethodDefinition custom = mission.Methods.Single(m => m.Name == "AddCustomMissile");
        MethodDefinition array = mission.Methods.Single(m => m.Name == "AddMissileAux");
        MethodDefinition single = mission.Methods.Single(m => m.Name == "AddMissileSingleUsageAux");
        DumpMethod(onShoot);
        DumpMethod(custom);
        DumpMethod(array);
        DumpMethod(single);

        Console.WriteLine("REVERSE_CALLERS");
        foreach (TypeDefinition type in AllTypes(assembly.MainModule))
        {
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                string[] targets = Calls(method)
                    .Where(c => c.Name == "AddMissileAux" || c.Name == "AddMissileSingleUsageAux")
                    .Select(c => c.Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (targets.Length > 0)
                    Console.WriteLine("  " + method.FullName + " -> " + string.Join(",", targets));
            }
        }
        return 0;
    }
}
