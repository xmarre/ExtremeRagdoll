using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class InspectBannerlordApi
{
    private static void DumpType(string assemblyPath, string typeName, params string[] filters)
    {
        using (var assembly = AssemblyDefinition.ReadAssembly(assemblyPath))
        {
            var type = assembly.MainModule.Types.FirstOrDefault(t => t.FullName == typeName);
            if (type == null)
            {
                Console.WriteLine("TYPE_NOT_FOUND=" + typeName);
                return;
            }

            Console.WriteLine("TYPE=" + type.FullName);
            foreach (var method in type.Methods.OrderBy(m => m.Name))
            {
                if (filters.Length > 0 && !filters.Any(f => method.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                string parameters = string.Join(", ", method.Parameters.Select(p => (p.ParameterType.IsByReference ? "ref " : "") + p.ParameterType.FullName + " " + p.Name));
                Console.WriteLine($"METHOD {method.Attributes} {method.ReturnType.FullName} {method.Name}({parameters})");
            }
            foreach (var property in type.Properties.OrderBy(p => p.Name))
            {
                if (filters.Length > 0 && !filters.Any(f => property.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                Console.WriteLine($"PROPERTY {property.PropertyType.FullName} {property.Name}");
            }
        }
    }

    private static string FindAssembly(string root, string fileName)
    {
        var matches = Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
            .Where(p => p.IndexOf("ref", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(p => p.Length)
            .ToArray();
        if (matches.Length == 0)
            throw new FileNotFoundException(fileName, root);
        return matches[0];
    }

    public static int Main()
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packages))
            packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        string mountAndBlade = FindAssembly(packages, "TaleWorlds.MountAndBlade.dll");
        string engine = FindAssembly(packages, "TaleWorlds.Engine.dll");
        Console.WriteLine("MOUNT_AND_BLADE=" + mountAndBlade);
        Console.WriteLine("ENGINE=" + engine);

        DumpType(mountAndBlade, "TaleWorlds.MountAndBlade.Mission", "Missile");
        DumpType(mountAndBlade, "TaleWorlds.MountAndBlade.Mission/Missile", "");
        DumpType(mountAndBlade, "TaleWorlds.MountAndBlade.Mission+Missile", "");
        DumpType(mountAndBlade, "TaleWorlds.MountAndBlade.Agent", "Equipment", "Weapon", "Missile");
        DumpType(engine, "TaleWorlds.Engine.GameEntity", "Particle", "Scale", "Component", "Frame", "Child");
        DumpType(engine, "TaleWorlds.Engine.ParticleSystem", "Scale", "Parameter", "Particle", "Pause", "Resume");
        DumpType(engine, "TaleWorlds.Engine.ParticleSystemComponent", "Scale", "Parameter", "Particle", "Pause", "Resume");
        return 0;
    }
}
