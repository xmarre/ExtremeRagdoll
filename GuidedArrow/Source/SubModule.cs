using TaleWorlds.MountAndBlade;

namespace GuidedArrow
{
    public sealed class SubModule : MBSubModuleBase
    {
        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            if (mission == null)
                return;

            mission.AddMissionBehavior(new GuidedArrowBehavior());
        }
    }
}
