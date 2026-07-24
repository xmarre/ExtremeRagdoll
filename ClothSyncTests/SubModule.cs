using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll.ClothSyncTests
{
    public sealed class SubModule : MBSubModuleBase
    {
        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            try
            {
                _ = Settings.Instance;
            }
            catch
            {
                // MCM is an existing required dependency of ExtremeRagdoll.
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            mission.AddMissionBehavior(new ClothSyncTestBehavior());
            mission.AddMissionBehavior(new AgentClothOwnerTestBehavior());
        }
    }
}
