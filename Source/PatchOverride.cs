using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class PatchOverride
{
    public static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: PatchOverride raw.dll TaleWorlds.MountAndBlade.dll out.dll");
            return 2;
        }

        var resolver = new DefaultAssemblyResolver();
        AddSearchDirectory(resolver, Path.GetDirectoryName(Path.GetFullPath(args[0])));
        AddSearchDirectory(resolver, Path.GetDirectoryName(Path.GetFullPath(args[1])));
        AddSearchDirectory(resolver, AppContext.BaseDirectory);
        AddSearchDirectory(resolver, Directory.GetCurrentDirectory());

        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };
        var asm = AssemblyDefinition.ReadAssembly(args[0], rp);
        var mb = AssemblyDefinition.ReadAssembly(args[1], rp);

        var type = asm.MainModule.GetType("ExtremeRagdoll.SafeRuntime.SafeRagdollBehavior");
        if (type == null) throw new Exception("SafeRagdollBehavior missing");
        var method = type.Methods.Single(m => m.Name == "OnRegisterBlowCompat");

        var baseType = mb.MainModule.GetType("TaleWorlds.MountAndBlade.MissionBehavior");
        if (baseType == null) throw new Exception("MissionBehavior missing");
        var baseMethod = baseType.Methods.Single(m => m.Name == "OnRegisterBlow" && m.Parameters.Count == 6);

        method.Name = "OnRegisterBlow";
        method.Attributes &= ~MethodAttributes.NewSlot;
        method.Attributes |= MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.Public;
        method.Attributes &= ~MethodAttributes.Final;

        for (int i = 0; i < method.Parameters.Count; i++)
        {
            method.Parameters[i].ParameterType = asm.MainModule.ImportReference(baseMethod.Parameters[i].ParameterType);
            method.Parameters[i].Attributes = baseMethod.Parameters[i].Attributes;
        }

        method.Overrides.Clear();
        method.Overrides.Add(asm.MainModule.ImportReference(baseMethod));

        asm.Name.Name = "ExtremeRagdoll";
        asm.Name.Version = new Version(0, 0, 0, 0);
        asm.MainModule.Name = "ExtremeRagdoll.dll";
        asm.Write(args[2], new WriterParameters { WriteSymbols = false });

        Console.WriteLine("patched override: " + method.FullName);
        Console.WriteLine("base: " + baseMethod.FullName);
        return 0;
    }

    private static void AddSearchDirectory(DefaultAssemblyResolver resolver, string path)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            resolver.AddSearchDirectory(path);
    }
}
