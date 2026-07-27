using System.Reflection;

[assembly: AssemblyVersion("1.0.0.0")]

namespace TaleWorlds.Core
{
    public enum AgentState
    {
        None = 0,
        Active = 1,
        Killed = 2,
        Unconscious = 3,
        Deleted = 4
    }

    public enum EquipmentIndex
    {
        None = -1,
        Weapon0 = 0,
        Weapon1 = 1,
        Weapon2 = 2,
        Weapon3 = 3
    }
}
