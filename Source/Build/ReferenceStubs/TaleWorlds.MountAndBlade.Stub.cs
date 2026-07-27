using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;

[assembly: AssemblyVersion("1.0.0.0")]

namespace TaleWorlds.MountAndBlade
{
    public abstract class MBSubModuleBase
    {
        protected virtual void OnSubModuleLoad() { }
        protected virtual void OnBeforeInitialModuleScreenSetAsRoot() { }
        public virtual void OnMissionBehaviorInitialize(Mission mission) { }
    }

    public enum MissionBehaviorType
    {
        Logic = 0,
        Other = 1
    }

    public abstract class MissionBehavior
    {
        public Mission Mission { get { return null; } }

        public virtual void OnCreated() { }
        public virtual void OnBehaviorInitialize() { }
        public virtual void OnRemoveBehavior() { }
        public virtual void OnAgentCreated(Agent agent) { }
        public virtual void OnEarlyAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow killingBlow) { }
        public virtual void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow killingBlow) { }
        public virtual void OnAgentDeleted(Agent affectedAgent) { }
        public virtual void OnPreMissionTick(float dt) { }
        public virtual void OnMissionTick(float dt) { }
        public virtual void OnRegisterBlow(
            Agent attacker,
            Agent victim,
            WeakGameEntity realHitEntity,
            Blow blow,
            ref AttackCollisionData collisionData,
            in MissionWeapon attackerWeapon) { }

        public abstract MissionBehaviorType BehaviorType { get; }
    }

    public class Mission
    {
        public enum MissionCombatType
        {
            Combat = 0,
            ArenaCombat = 1,
            NoCombat = 2
        }

        public static Mission Current { get { return null; } }
        public MissionCombatType CombatType { get { return MissionCombatType.NoCombat; } }
        public float CurrentTime { get { return 0f; } }
        public Missions.AgentReadOnlyList AllAgents { get { return null; } }
        public void AddMissionBehavior(MissionBehavior behavior) { }
    }

    public class Agent
    {
        public delegate void OnAgentHealthChangedDelegate(Agent agent, float oldHealth, float newHealth);

        public event OnAgentHealthChangedDelegate OnAgentHealthChanged;

        public Mission Mission { get { return null; } }
        public float Health { get { return 0f; } }
        public float HealthLimit { get { return 0f; } }
        public int Index { get { return -1; } }
        public Vec3 Position { get { return Vec3.Zero; } }
        public Vec3 LookDirection { get { return Vec3.Zero; } }
        public Vec3 AverageVelocity { get { return Vec3.Zero; } }
        public Vec2 MovementVelocity { get { return default(Vec2); } }
        public AgentState State { get { return AgentState.None; } }
        public MBAgentVisuals AgentVisuals { get { return null; } }
        public MissionEquipment Equipment { get { return null; } }
        public bool IsMount { get { return false; } }
        public Agent MountAgent { get { return null; } }

        public WeakGameEntity GetWeaponEntityFromEquipmentSlot(EquipmentIndex slot)
        {
            return default(WeakGameEntity);
        }

        public void Die(Blow b, KillInfo overrideKillInfo = KillInfo.Invalid) { }

        private void HandleBlow(ref Blow b, in AttackCollisionData collisionData) { }
    }

    public enum KillInfo : sbyte
    {
        Invalid = -1,
        Gravity = 0,
        TeamSwitch = 1
    }

    [Flags]
    public enum BlowFlags
    {
        None = 0,
        KnockBack = 0x10,
        KnockDown = 0x20,
        NoSound = 0x40
    }

    public struct BlowWeaponRecord
    {
        public Vec3 Velocity;
    }

    public struct Blow
    {
        public int InflictedDamage;
        public int SelfInflictedDamage;
        public int OwnerId;
        public sbyte BoneIndex;
        public float BaseMagnitude;
        public float AbsorbedByArmor;
        public Vec3 Direction;
        public Vec3 SwingDirection;
        public Vec3 GlobalPosition;
        public BlowFlags BlowFlag;
        public BlowWeaponRecord WeaponRecord;
        public bool IsFallDamage;

        public bool IsMissile { get { return false; } }
    }

    public struct AttackCollisionData
    {
        public bool IsColliderAgent { get { return false; } }
        public float ChargeVelocity { get { return 0f; } }
    }

    public struct KillingBlow
    {
        public Vec3 RagdollImpulseAmount;
        public int InflictedDamage;
        public bool IsMissile;
    }

    public struct MissionWeapon
    {
        public bool IsEmpty { get { return true; } }
        public bool IsShield() { return false; }
        public bool IsAnyAmmo() { return false; }
    }

    public class MissionEquipment
    {
        public MissionWeapon this[EquipmentIndex slot] { get { return default(MissionWeapon); } }
    }

    public class MBAgentVisuals
    {
        public Skeleton GetSkeleton() { return null; }
        public MatrixFrame GetGlobalFrame() { return default(MatrixFrame); }
    }
}

namespace TaleWorlds.MountAndBlade.Missions
{
    // Bannerlord 1.3.15: AgentReadOnlyList -> MBReadOnlyList<Agent> -> List<Agent>.
    // Do not declare a derived GetEnumerator(): the real runtime type has no such method.
    public sealed class AgentReadOnlyList : List<TaleWorlds.MountAndBlade.Agent>
    {
    }
}
