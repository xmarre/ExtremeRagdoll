using System;
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

        using (var asm = AssemblyDefinition.ReadAssembly(args[0]))
        {
            var type = asm.MainModule.GetType("ExtremeRagdoll.SafeRuntime.SafeRagdollBehavior");
            if (type == null) throw new Exception("SafeRagdollBehavior missing");

            var compatMethod = type.Methods.SingleOrDefault(m => m.Name == "OnRegisterBlowCompat");
            if (compatMethod == null) throw new Exception("OnRegisterBlowCompat missing");
            if (type.Methods.Any(m => m.Name == "OnRegisterBlow" || m.Overrides.Any(o =>
                    o.Name == "OnRegisterBlow" &&
                    o.DeclaringType.FullName == "TaleWorlds.MountAndBlade.MissionBehavior")))
            {
                throw new Exception("SafeRagdollBehavior must not encode a hard MissionBehavior.OnRegisterBlow override.");
            }

            // Keep the historical deterministic assembly identity step. OnRegisterBlow itself is now
            // late-bound at runtime so a future Bannerlord callback modifier/signature change cannot
            // invalidate SafeRagdollBehavior while the CLR is loading the type.
            asm.Name.Name = "ExtremeRagdoll";
            asm.Name.Version = new Version(0, 0, 0, 0);
            asm.MainModule.Name = "ExtremeRagdoll.dll";
            asm.Write(args[2], new WriterParameters { WriteSymbols = false });
        }

        Console.WriteLine("kept OnRegisterBlow late-bound; normalized ExtremeRagdoll assembly identity");
        return 0;
    }
}
