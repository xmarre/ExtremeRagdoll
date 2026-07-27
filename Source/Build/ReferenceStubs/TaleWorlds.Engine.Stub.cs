using System.Reflection;
using TaleWorlds.Library;

[assembly: AssemblyVersion("1.0.0.0")]

namespace TaleWorlds.Engine
{
    public enum RagdollState
    {
        Disabled = 0,
        NeedsActivation = 1,
        ActiveFirstTick = 2,
        Active = 3,
        NeedsDeactivation = 4
    }

    public class Skeleton
    {
        public RagdollState GetCurrentRagdollState() { return RagdollState.Disabled; }
        public sbyte GetBoneCount() { return 0; }
        public sbyte GetParentBoneIndex(sbyte boneIndex) { return -1; }
        public string GetBoneName(sbyte boneIndex) { return null; }
        public MatrixFrame GetBoneEntitialFrameWithIndex(sbyte boneIndex) { return default(MatrixFrame); }
        public void ForceUpdateBoneFrames() { }
    }

    public struct WeakGameEntity
    {
        public bool IsValid { get { return false; } }
    }

    public class GameEntity { }

    public static class GameEntityPhysicsExtensions
    {
        public static void SetPhysicsState(this WeakGameEntity entity, bool isEnabled, bool setChildren) { }
    }
}
