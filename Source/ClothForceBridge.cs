using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

[assembly: AssemblyVersion("1.0.0.0")]

namespace ExtremeRagdoll
{
    public enum DeathLaunchRoute
    {
        NativeHandled = 1,
        NativeIneffective = 2,
        Fallback = 3
    }

    // Historical assembly/type name retained as the binary ABI used by ExtremeRagdoll.dll.
    // v1.2.74 retains the scoped HandleBlow -> Die observation boundary but intentionally
    // bypasses all ExtremeRagdoll native Blow amplification. Every confirmed combat death
    // is owned by the controlled post-ragdoll actuator in the main mission behavior.
    public static class ClothForceBridge
    {
        private sealed class DeferredCall
        {
            internal MethodInfo Method;
            internal object Target;
            internal object[] Arguments;
            internal bool IsRagdollHandoff;
            internal bool IsRagdollForce;
            internal int AgentIndex;
            internal float QueuedAt;
        }

        private sealed class CombatBlowContext
        {
            internal Agent Agent;
            internal int OwnerId;
            internal int Damage;
            internal sbyte BoneIndex;
            internal bool IsMissile;
            internal bool Consumed;
        }

        private sealed class DeathRouteRecord
        {
            internal bool NativePrepared;
            internal bool ResultObserved;
            internal bool ResultLogged;
            internal bool DecisionLogged;
            internal DeathLaunchRoute Route;
            internal int AgentIndex;
            internal int Damage;
            internal string KillKind;
            internal Vec3 CapturedDirection;
            internal Vec3 VictimMomentum;
            internal float RequestedForce;
            internal int PulseCount;
            internal float PulseDecay;
            internal float DeliveredForceCeiling;
            internal float EquivalentDeliveredForce;
            internal float NativeBaseMagnitude;
            internal Vec3 NativeWeaponVelocity;
            internal Vec3 ResultingImpulse;
            internal string ResultSource;
            internal float ExpiresAt;
        }

        private static readonly List<DeferredCall> Queue = new List<DeferredCall>(32);
        private static readonly Dictionary<Agent, DeathRouteRecord> DeathRoutes =
            new Dictionary<Agent, DeathRouteRecord>();
        private static readonly object DeathRouteGate = new object();

        private const float HandoffRetryTimeout = 0.50f;
        private const float DeathRouteLifetime = 15.0f;
        private const float NativeForceToBlowMagnitudeScale = 1.0f;
        private const string HarmonyOwnerId = "xmarre.extremeragdoll.native-death-impulse";

        [ThreadStatic]
        private static CombatBlowContext _activeCombatBlow;

        private static bool _handleBlowPatchInstalled;
        private static bool _agentDiePatchInstalled;
        private static bool _installationLogged;
        private static float _nextHandledCleanupAt;
        private static Type _safeSettingsType;

        public static bool EnsureNativeDeathPatch()
        {
            if (_handleBlowPatchInstalled && _agentDiePatchInstalled)
                return true;

            try
            {
                Assembly harmonyAssembly = FindHarmonyAssembly();
                if (harmonyAssembly == null)
                    return false;

                Type harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", false);
                Type harmonyMethodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", false);
                if (harmonyType == null || harmonyMethodType == null)
                    return false;

                object harmony = Activator.CreateInstance(harmonyType, new object[] { HarmonyOwnerId });
                MethodInfo patch = FindHarmonyPatchMethod(harmonyType, harmonyMethodType);
                if (harmony == null || patch == null)
                    return false;

                if (!_handleBlowPatchInstalled)
                {
                    MethodInfo target = FindAgentHandleBlowMethod();
                    MethodInfo prefix = typeof(ClothForceBridge).GetMethod(
                        "HandleBlowPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                    MethodInfo finalizer = typeof(ClothForceBridge).GetMethod(
                        "HandleBlowFinalizer", BindingFlags.Static | BindingFlags.NonPublic);
                    if (target == null || prefix == null || finalizer == null)
                        return false;

                    object prefixPatch = CreateHarmonyMethod(harmonyMethodType, prefix);
                    object finalizerPatch = CreateHarmonyMethod(harmonyMethodType, finalizer);
                    if (prefixPatch == null || finalizerPatch == null ||
                        !InvokeHarmonyPatch(harmony, patch, target, prefixPatch, null, finalizerPatch))
                        return false;

                    _handleBlowPatchInstalled = true;
                }

                if (!_agentDiePatchInstalled)
                {
                    MethodInfo target = FindAgentDieMethod();
                    MethodInfo prefix = typeof(ClothForceBridge).GetMethod(
                        "AgentDiePrefix", BindingFlags.Static | BindingFlags.NonPublic);
                    if (target == null || prefix == null)
                        return false;

                    object prefixPatch = CreateHarmonyMethod(harmonyMethodType, prefix);
                    if (prefixPatch == null ||
                        !InvokeHarmonyPatch(harmony, patch, target, prefixPatch, null, null))
                        return false;

                    _agentDiePatchInstalled = true;
                }

                if (!_installationLogged)
                {
                    _installationLogged = true;
                    TryLog(
                        "Installed scoped native death-impulse patches: Agent.HandleBlow establishes an exact synchronous context, " +
                        "Agent.Die amplification requires that matching context, and HandleBlow finalizer teardown is active.");
                }
                return true;
            }
            catch (Exception ex)
            {
                TryLog("Failed to install scoped native death-impulse patches: " + ex);
                return false;
            }
        }

        // ABI intentionally retained. The main runtime uses this for both StartRagdollAsCorpse
        // and the explicit Fallback ApplyForceOnRagdoll route. Native-route suppression happens
        // in the main state machine before this method is called; this helper never silently drops it.
        public static object DeferInvoke(MethodInfo method, object target, object[] arguments)
        {
            if (method == null || target == null)
                throw new ArgumentNullException();

            EnsureNativeDeathPatch();

            Agent targetAgent = target as Agent;
            bool isHandoff = string.Equals(method.Name, "StartRagdollAsCorpse", StringComparison.Ordinal) && arguments == null;
            if (!isHandoff && arguments == null)
                throw new ArgumentNullException("arguments");

            for (int i = 0; i < Queue.Count; i++)
            {
                if (object.ReferenceEquals(Queue[i].Target, target))
                    throw new InvalidOperationException("A ragdoll operation is already deferred for this agent.");
            }

            float now = GetMissionTime(targetAgent);
            Queue.Add(new DeferredCall
            {
                Method = method,
                Target = target,
                Arguments = arguments,
                IsRagdollHandoff = isHandoff,
                IsRagdollForce = string.Equals(method.Name, "ApplyForceOnRagdoll", StringComparison.Ordinal),
                AgentIndex = SafeAgentIndex(targetAgent),
                QueuedAt = now
            });
            return null;
        }

        public static void Flush()
        {
            EnsureNativeDeathPatch();

            Mission current = Mission.Current;
            if (object.ReferenceEquals(current, null))
            {
                Queue.Clear();
                lock (DeathRouteGate)
                    DeathRoutes.Clear();
                return;
            }

            CleanupDeathRoutes(current);

            if (Queue.Count == 0)
                return;

            for (int i = 0; i < Queue.Count; )
            {
                DeferredCall call = Queue[i];
                Agent agent = call.Target as Agent;
                if (!IsCurrentMissionAgent(agent, current))
                {
                    Queue.RemoveAt(i);
                    if (call.IsRagdollForce)
                        LogDeferredForceResult(call, "DROPPED_INVALID_AGENT", null);
                    continue;
                }

                if (!call.IsRagdollHandoff)
                {
                    Queue.RemoveAt(i);
                    try
                    {
                        call.Method.Invoke(call.Target, call.Arguments);
                        if (call.IsRagdollForce)
                            LogDeferredForceResult(call, "EXECUTED", null);
                    }
                    catch (Exception ex)
                    {
                        if (call.IsRagdollForce)
                            LogDeferredForceResult(call, "EXECUTION_FAILED", UnwrapInvocationException(ex));
                    }
                    continue;
                }

                try
                {
                    call.Method.Invoke(call.Target, null);
                    Queue.RemoveAt(i);
                    continue;
                }
                catch
                {
                    float now = 0f;
                    try { now = current.CurrentTime; } catch { }
                    if (now - call.QueuedAt >= HandoffRetryTimeout)
                    {
                        Queue.RemoveAt(i);
                        continue;
                    }
                    i++;
                }
            }
        }

        public static void ReportNativeDeathResult(
            Agent agent,
            Vec3 resultingKillingBlowImpulse,
            string killKind,
            string source)
        {
            if (object.ReferenceEquals(agent, null))
                return;

            bool debugLogging = IsDebugLoggingEnabled();
            DeathRouteRecord snapshot = null;
            bool shouldLog = false;
            float now = GetMissionTime(agent);
            lock (DeathRouteGate)
            {
                DeathRouteRecord record;
                if (!DeathRoutes.TryGetValue(agent, out record) || record == null || record.ExpiresAt < now)
                {
                    record = new DeathRouteRecord
                    {
                        NativePrepared = false,
                        Route = DeathLaunchRoute.Fallback,
                        AgentIndex = SafeAgentIndex(agent),
                        ExpiresAt = now + DeathRouteLifetime
                    };
                    DeathRoutes[agent] = record;
                }

                DeathLaunchRoute previousRoute = record.Route;
                record.ResultObserved = true;
                record.ResultingImpulse = IsFinite(resultingKillingBlowImpulse)
                    ? resultingKillingBlowImpulse
                    : Vec3.Zero;
                record.ResultSource = string.IsNullOrEmpty(source) ? "unknown" : source;
                // Preserve the exact type captured from the verified HandleBlow context.
                // The removal callbacks only supply a generic fallback label when no native
                // record exists.
                if (!record.NativePrepared && !string.IsNullOrEmpty(killKind))
                    record.KillKind = killKind;
                record.Route = record.NativePrepared
                    ? (IsUsableVector(record.ResultingImpulse)
                        ? DeathLaunchRoute.NativeHandled
                        : DeathLaunchRoute.NativeIneffective)
                    : DeathLaunchRoute.Fallback;
                record.ExpiresAt = now + DeathRouteLifetime;

                if (previousRoute != record.Route)
                    record.DecisionLogged = false;

                if (debugLogging && (!record.ResultLogged || previousRoute != record.Route))
                {
                    record.ResultLogged = true;
                    snapshot = CopyRecord(record);
                    shouldLog = true;
                }
            }

            if (shouldLog)
            {
                Vec3 resultCallbackVelocity = CaptureAgentMomentum(agent);
                Vec3 resultCallbackPosition = Vec3.Zero;
                Vec3 resultCallbackVisualOrigin = Vec3.Zero;
                Vec3 resultCallbackVisualForward = Vec3.Zero;
                Vec3 resultCallbackVisualUp = Vec3.Zero;
                bool hasResultCallbackPosition = false;
                bool hasResultCallbackVisualOrigin = false;
                bool hasResultCallbackVisualRotation = false;
                try
                {
                    resultCallbackPosition = agent.Position;
                    hasResultCallbackPosition = IsFinite(resultCallbackPosition);
                }
                catch { }
                try
                {
                    MBAgentVisuals visuals = agent.AgentVisuals;
                    if (!object.ReferenceEquals(visuals, null))
                    {
                        MatrixFrame frame = visuals.GetGlobalFrame();
                        resultCallbackVisualOrigin = frame.origin;
                        resultCallbackVisualForward = frame.rotation.f;
                        resultCallbackVisualUp = frame.rotation.u;
                        hasResultCallbackVisualOrigin = IsFinite(resultCallbackVisualOrigin);
                        hasResultCallbackVisualRotation = IsFinite(resultCallbackVisualForward) && IsFinite(resultCallbackVisualUp);
                    }
                }
                catch { }

                TryLog(
                    "Native death result: agent #" + snapshot.AgentIndex +
                    " killType=" + SafeText(snapshot.KillKind) +
                    " resultingKillingBlowImpulse=" + FormatVec(snapshot.ResultingImpulse) +
                    " resultingImpulseMagnitude=" + VectorMagnitude(snapshot.ResultingImpulse).ToString("0.000", CultureInfo.InvariantCulture) +
                    " resultCallbackAgentVelocity=" + FormatVec(resultCallbackVelocity) +
                    " resultCallbackAgentVelocityMagnitude=" + VectorMagnitude(resultCallbackVelocity).ToString("0.000", CultureInfo.InvariantCulture) +
                    " resultCallbackAgentPosition=" + (hasResultCallbackPosition ? FormatVec(resultCallbackPosition) : "UNAVAILABLE") +
                    " resultCallbackVisualOrigin=" + (hasResultCallbackVisualOrigin ? FormatVec(resultCallbackVisualOrigin) : "UNAVAILABLE") +
                    " resultCallbackVisualForward=" + (hasResultCallbackVisualRotation ? FormatVec(resultCallbackVisualForward) : "UNAVAILABLE") +
                    " resultCallbackVisualUp=" + (hasResultCallbackVisualRotation ? FormatVec(resultCallbackVisualUp) : "UNAVAILABLE") +
                    " route=" + FormatRoute(snapshot.Route) +
                    " nativePrepared=" + snapshot.NativePrepared +
                    " source=" + SafeText(snapshot.ResultSource) + ".");
            }
        }

        public static DeathLaunchRoute GetDeathLaunchRoute(Agent agent)
        {
            if (object.ReferenceEquals(agent, null))
                return DeathLaunchRoute.Fallback;

            bool debugLogging = IsDebugLoggingEnabled();
            DeathRouteRecord snapshot = null;
            DeathLaunchRoute route;
            float now = GetMissionTime(agent);
            lock (DeathRouteGate)
            {
                DeathRouteRecord record;
                if (!DeathRoutes.TryGetValue(agent, out record) || record == null || record.ExpiresAt < now)
                {
                    record = new DeathRouteRecord
                    {
                        NativePrepared = false,
                        ResultObserved = false,
                        Route = DeathLaunchRoute.Fallback,
                        AgentIndex = SafeAgentIndex(agent),
                        ExpiresAt = now + DeathRouteLifetime
                    };
                    DeathRoutes[agent] = record;
                }

                route = record.Route;
                if (record.NativePrepared && !record.ResultObserved)
                    route = DeathLaunchRoute.NativeIneffective;

                if (debugLogging && !record.DecisionLogged)
                {
                    record.DecisionLogged = true;
                    snapshot = CopyRecord(record);
                    snapshot.Route = route;
                }
            }

            if (snapshot != null)
            {
                TryLog(
                    "Death launch ownership: agent #" + snapshot.AgentIndex +
                    " route=" + FormatRoute(snapshot.Route) +
                    " resultObserved=" + snapshot.ResultObserved +
                    " resultingKillingBlowImpulse=" + FormatVec(snapshot.ResultingImpulse) +
                    " legacyBurst=" + (snapshot.Route == DeathLaunchRoute.Fallback ? "QUEUED_WHEN_ACTIVE" : "SUPPRESSED") + ".");
            }
            return route;
        }

        public static void ForgetDeathRoute(Agent agent)
        {
            lock (DeathRouteGate)
            {
                // Null is an explicit mission-boundary reset used by SafeSubModule.OnMissionBehaviorInitialize.
                // This reuses an existing public API so no new cross-assembly member reference is
                // required in the exact-binary reconstruction.
                if (object.ReferenceEquals(agent, null))
                {
                    DeathRoutes.Clear();
                    _nextHandledCleanupAt = 0f;
                    return;
                }

                DeathRouteRecord record;
                if (DeathRoutes.TryGetValue(agent, out record) && record != null && record.Damage == int.MinValue)
                    return;
                DeathRoutes.Remove(agent);
            }
        }

        // Retained for binary/API compatibility with earlier releases. v1.2.74 does not create
        // new native-owned records, so normal runtime callers resolve to Fallback.
        public static bool IsNativeDeathHandled(Agent agent)
        {
            return GetDeathLaunchRoute(agent) != DeathLaunchRoute.Fallback;
        }

        public static string FormatRoute(DeathLaunchRoute route)
        {
            switch (route)
            {
                case DeathLaunchRoute.NativeHandled:
                    return "NATIVE_HANDLED";
                case DeathLaunchRoute.NativeIneffective:
                    return "NATIVE_INEFFECTIVE";
                default:
                    return "FALLBACK";
            }
        }

        private static void HandleBlowPrefix(Agent __instance, ref Blow b, out CombatBlowContext __state)
        {
            __state = _activeCombatBlow;
            _activeCombatBlow = null;

            if (!IsEligibleCombatDeath(__instance) || b.InflictedDamage <= 0)
                return;

            try
            {
                float health = __instance.Health;
                if (!IsFinite(health) || health <= 0f)
                    return;

                // Capture every positive-damage combat HandleBlow, not only blows that are already
                // predictably lethal at method entry. Bannerlord/TOR can turn an initially nonlethal
                // missile hit into a synchronous death later in the same HandleBlow (for example via
                // secondary damage processed during Mission.OnAgentHit). The thread-static/finalizer
                // scope still guarantees that only the currently executing HandleBlow can own the Die.
                _activeCombatBlow = new CombatBlowContext
                {
                    Agent = __instance,
                    OwnerId = b.OwnerId,
                    Damage = b.InflictedDamage,
                    BoneIndex = b.BoneIndex,
                    IsMissile = b.IsMissile
                };
            }
            catch
            {
                _activeCombatBlow = null;
            }
        }

        private static Exception HandleBlowFinalizer(Exception __exception, CombatBlowContext __state)
        {
            CombatBlowContext completed = _activeCombatBlow;
            if (completed != null && completed.Consumed && completed.Agent != null && IsDebugLoggingEnabled())
            {
                Vec3 postHandleVelocity = CaptureAgentMomentum(completed.Agent);
                Vec3 postHandlePosition = Vec3.Zero;
                Vec3 postHandleVisualOrigin = Vec3.Zero;
                Vec3 postHandleVisualForward = Vec3.Zero;
                Vec3 postHandleVisualUp = Vec3.Zero;
                bool hasPostHandlePosition = false;
                bool hasPostHandleVisualOrigin = false;
                bool hasPostHandleVisualRotation = false;
                try
                {
                    postHandlePosition = completed.Agent.Position;
                    hasPostHandlePosition = IsFinite(postHandlePosition);
                }
                catch { }
                try
                {
                    MBAgentVisuals visuals = completed.Agent.AgentVisuals;
                    if (!object.ReferenceEquals(visuals, null))
                    {
                        MatrixFrame frame = visuals.GetGlobalFrame();
                        postHandleVisualOrigin = frame.origin;
                        postHandleVisualForward = frame.rotation.f;
                        postHandleVisualUp = frame.rotation.u;
                        hasPostHandleVisualOrigin = IsFinite(postHandleVisualOrigin);
                        hasPostHandleVisualRotation = IsFinite(postHandleVisualForward) && IsFinite(postHandleVisualUp);
                    }
                }
                catch { }

                TryLog(
                    "Lethal HandleBlow completed: agent #" + SafeAgentIndex(completed.Agent) +
                    " ownerId=" + completed.OwnerId +
                    " postHandleBlowAgentVelocity=" + FormatVec(postHandleVelocity) +
                    " postHandleBlowAgentVelocityMagnitude=" + VectorMagnitude(postHandleVelocity).ToString("0.000", CultureInfo.InvariantCulture) +
                    " postHandleBlowAgentPosition=" + (hasPostHandlePosition ? FormatVec(postHandlePosition) : "UNAVAILABLE") +
                    " postHandleBlowVisualOrigin=" + (hasPostHandleVisualOrigin ? FormatVec(postHandleVisualOrigin) : "UNAVAILABLE") +
                    " postHandleBlowVisualForward=" + (hasPostHandleVisualRotation ? FormatVec(postHandleVisualForward) : "UNAVAILABLE") +
                    " postHandleBlowVisualUp=" + (hasPostHandleVisualRotation ? FormatVec(postHandleVisualUp) : "UNAVAILABLE") +
                    " exception=" + (__exception == null ? "NONE" : __exception.GetType().FullName) + ".");
            }
            _activeCombatBlow = __state;
            return __exception;
        }

        // Harmony prefix for Agent.Die(Blow, KillInfo). The first-death sentinel remains an
        // ownership safeguard only; launch direction is validated by the main post-ragdoll path.
        // No ExtremeRagdoll Blow mutation is performed here.
        private static void AgentDiePrefix(Agent __instance, ref Blow b)
        {
            if (!IsEligibleCombatDeath(__instance))
                return;

            CombatBlowContext context = _activeCombatBlow;

            // Claim the actual first combat death once per mission. Keep a sentinel record alive
            // until mission teardown so re-entrant Die calls for that same agent also remain on the
            // post-ragdoll fallback route. This avoids feeding an ExtremeRagdoll-sized impulse into
            // Bannerlord's first corpse-ragdoll initialization.
            bool firstDeathWarmup = false;
            lock (DeathRouteGate)
            {
                // Mission initialization clears DeathRoutes through ForgetDeathRoute(null).
                // Therefore an empty registry here means this is the actual first Agent.Die in
                // this combat mission. Avoid enumerating Dictionary<TKey,TValue> here: a prior
                // method-body transplant introduced a KeyValuePair value-type identity conflict
                // under Bannerlord's .NET Framework runtime.
                DeathRouteRecord existingRecord;
                bool sameFirstDeath = DeathRoutes.TryGetValue(__instance, out existingRecord) &&
                    existingRecord != null && existingRecord.Damage == int.MinValue;
                if (sameFirstDeath || DeathRoutes.Count == 0)
                {
                    firstDeathWarmup = true;
                    if (!sameFirstDeath)
                    {
                        DeathRoutes[__instance] = new DeathRouteRecord
                        {
                            NativePrepared = false,
                            ResultObserved = false,
                            Route = DeathLaunchRoute.Fallback,
                            AgentIndex = SafeAgentIndex(__instance),
                            Damage = int.MinValue,
                            KillKind = "first-death-post-ragdoll",
                            ResultSource = "FIRST_COMBAT_DEATH_POST_RAGDOLL_WARMUP",
                            ExpiresAt = float.MaxValue
                        };
                    }
                }
            }

            if (firstDeathWarmup)
            {
                if (context != null && object.ReferenceEquals(context.Agent, __instance))
                    context.Consumed = true;
                if (IsDebugLoggingEnabled())
                {
                    TryLog(
                        "Native death impulse bypassed: agent #" + SafeAgentIndex(__instance) +
                        " route=FALLBACK reason=FIRST_COMBAT_DEATH_POST_RAGDOLL_WARMUP.");
                }
                return;
            }

            // v1.2.74: every confirmed combat death uses the same controlled post-ragdoll
            // ExtremeRagdoll actuator. The native Blow/KillingBlow amplification path remains
            // intentionally bypassed because runtime testing showed that NATIVE_HANDLED deaths
            // often only produced a weak upward hop, while the post-ragdoll central-force route
            // produced the intended visible deathblow. Mount-body collisions still enter the same
            // route but remain independently scaled by MountCollisionKillStrength in the main
            // mission behavior. The first-combat-death sentinel above remains an ownership-only
            // safeguard; the main resolver rejects reversed engine impulses for every death.
            //
            // Keep the harmless current-Blow missile probe aligned with the exact patched release
            // body, then return before any native magnitude/direction/WeaponRecord mutation. No
            // Blow or WeaponRecord state is retained across calls or ticks.
            bool isMissile = false;
            try { isMissile = b.IsMissile; } catch { }
            return;
        }

        private static bool IsMatchingCombatBlow(CombatBlowContext context, Agent agent, Blow b)
        {
            if (context == null || context.Consumed || !object.ReferenceEquals(context.Agent, agent))
                return false;
            try
            {
                // InflictedDamage may be finalized/changed during HandleBlow before Agent.Die is
                // reached. Agent identity + synchronous thread scope + owner/bone/missile identity are
                // the stable invariants; requiring the original damage value caused real late-lethal
                // arrows to miss the native route.
                return context.OwnerId == b.OwnerId &&
                       context.BoneIndex == b.BoneIndex &&
                       context.IsMissile == b.IsMissile;
            }
            catch { return false; }
        }

        private static float ComputeRequestedForce(int damage)
        {
            float damageInfluence = ReadFloatSetting("DamageInfluence", 1.25f);
            float overallStrength = ReadFloatSetting("OverallStrength", 6f);
            float minimumForce = ReadFloatSetting("MinimumForce", 100000f);
            float maximumForce = ReadFloatSetting("MaximumForce", 0f);

            float force = (30000f + Math.Max(0, damage) * 300f * damageInfluence) * overallStrength;
            force = Math.Max(force, minimumForce);
            if (maximumForce > 0f)
                force = Math.Min(force, maximumForce);
            return force;
        }

        private static float ComputeEquivalentDeliveredForce(
            float requestedForce,
            out int pulseCount,
            out float decay,
            out float ceiling)
        {
            pulseCount = ReadIntSetting("PulseCount", 2);
            if (pulseCount < 1)
                pulseCount = 1;
            decay = ReadFloatSetting("PulseDecay", 0.85f);
            ceiling = ReadFloatSetting("DeliveredForceCeiling", 60000f);

            // Treat the configured ceiling as the historical baseline at OverallStrength=6.
            // This keeps one native missile actuator responsive to the master strength setting.
            if (ceiling > 0f)
            {
                float overallStrength = ReadFloatSetting("OverallStrength", 6f);
                float ceilingScale = overallStrength / 6f;
                if (!IsFinite(ceilingScale) || ceilingScale < 0.25f)
                    ceilingScale = 0.25f;
                ceiling *= ceilingScale;
                if (!IsFinite(ceiling) || ceiling < 0f)
                    ceiling = 60000f;
            }

            double total = 0d;
            double scale = 1d;
            for (int i = 0; i < pulseCount; i++)
            {
                double pulse = requestedForce * scale;
                if (ceiling > 0f && pulse > ceiling)
                    pulse = ceiling;
                if (pulse > 0d && !double.IsNaN(pulse) && !double.IsInfinity(pulse))
                    total += pulse;
                scale *= decay;
                if (double.IsNaN(scale) || double.IsInfinity(scale) || scale < 0d)
                    scale = 1d;
            }

            if (total <= 0d || double.IsNaN(total) || double.IsInfinity(total))
                return 0f;
            if (total > float.MaxValue)
                return float.MaxValue;
            return (float)total;
        }

        private static Vec3 ResolveNativeDeathDirection(Agent agent, Blow b)
        {
            Vec3 direction = Vec3.Zero;
            try
            {
                if (b.IsMissile && IsUsableVector(b.WeaponRecord.Velocity))
                    direction = b.WeaponRecord.Velocity;
            }
            catch { }

            if (!IsUsableVector(direction) && IsUsableVector(b.Direction))
                direction = b.Direction;
            if (!IsUsableVector(direction) && IsUsableVector(b.SwingDirection))
                direction = b.SwingDirection;

            Vec3 momentum = CaptureAgentMomentum(agent);
            if (!IsUsableVector(direction) && IsUsableVector(momentum))
                direction = momentum;

            if (!IsUsableVector(direction))
            {
                try { direction = agent.LookDirection; }
                catch { direction = new Vec3(0f, 1f, 0f); }
            }
            if (!IsUsableVector(direction))
                direction = new Vec3(0f, 1f, 0f);

            direction = direction.NormalizedCopy();

            float momentumCarryover = ReadFloatSetting("MomentumCarryover", 1f);
            if (IsUsableVector(momentum) && momentumCarryover > 0f)
                direction += momentum * (0.10f * momentumCarryover);

            direction.z += ReadFloatSetting("UpwardLift", 0.05f);

            float impactSpin = ReadFloatSetting("ImpactSpin", 0f);
            if (impactSpin > 0f && IsUsableVector(direction))
            {
                float sign = (SafeAgentIndex(agent) & 1) == 0 ? 1f : -1f;
                Vec3 lateral = new Vec3(-direction.y * sign, direction.x * sign, 0f);
                if (IsUsableVector(lateral))
                    direction += lateral.NormalizedCopy() * impactSpin;
            }

            if (!IsUsableVector(direction))
                return new Vec3(0f, 1f, 0.25f).NormalizedCopy();
            return direction.NormalizedCopy();
        }

        private static Vec3 CaptureAgentMomentum(Agent agent)
        {
            if (object.ReferenceEquals(agent, null))
                return Vec3.Zero;
            try
            {
                Vec3 value = agent.AverageVelocity;
                return IsFinite(value) ? value : Vec3.Zero;
            }
            catch { return Vec3.Zero; }
        }

        private static string ClassifyBlowKind(Blow b)
        {
            try
            {
                if (b.IsMissile)
                    return "missile";
                if (b.IsFallDamage)
                    return "fall";
                if ((b.BlowFlag & BlowFlags.NoSound) != 0)
                    return "spell-or-scripted";
            }
            catch { }
            return "direct";
        }

        private static bool IsEligibleCombatDeath(Agent agent)
        {
            if (object.ReferenceEquals(agent, null))
                return false;
            try
            {
                Mission mission = agent.Mission;
                if (object.ReferenceEquals(mission, null) || !object.ReferenceEquals(mission, Mission.Current))
                    return false;
                string combatType = mission.CombatType.ToString();
                return !string.Equals(combatType, "NoCombat", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void CleanupDeathRoutes(Mission current)
        {
            float now;
            try { now = current.CurrentTime; }
            catch { return; }
            if (now < _nextHandledCleanupAt)
                return;
            _nextHandledCleanupAt = now + 1f;

            lock (DeathRouteGate)
            {
                if (DeathRoutes.Count == 0)
                    return;

                List<Agent> remove = null;
                foreach (KeyValuePair<Agent, DeathRouteRecord> pair in DeathRoutes)
                {
                    bool stale = pair.Value == null || pair.Value.ExpiresAt < now;
                    if (!stale)
                        continue;
                    if (remove == null)
                        remove = new List<Agent>();
                    remove.Add(pair.Key);
                }
                if (remove == null)
                    return;
                for (int i = 0; i < remove.Count; i++)
                    DeathRoutes.Remove(remove[i]);
            }
        }

        private static DeathRouteRecord CopyRecord(DeathRouteRecord source)
        {
            return new DeathRouteRecord
            {
                NativePrepared = source.NativePrepared,
                ResultObserved = source.ResultObserved,
                Route = source.Route,
                AgentIndex = source.AgentIndex,
                Damage = source.Damage,
                KillKind = source.KillKind,
                CapturedDirection = source.CapturedDirection,
                VictimMomentum = source.VictimMomentum,
                RequestedForce = source.RequestedForce,
                PulseCount = source.PulseCount,
                PulseDecay = source.PulseDecay,
                DeliveredForceCeiling = source.DeliveredForceCeiling,
                EquivalentDeliveredForce = source.EquivalentDeliveredForce,
                NativeBaseMagnitude = source.NativeBaseMagnitude,
                NativeWeaponVelocity = source.NativeWeaponVelocity,
                ResultingImpulse = source.ResultingImpulse,
                ResultSource = source.ResultSource,
                ExpiresAt = source.ExpiresAt
            };
        }

        private static void LogDeferredForceResult(DeferredCall call, string status, Exception error)
        {
            if (!IsDebugLoggingEnabled())
                return;

            Vec3 vector = Vec3.Zero;
            try
            {
                if (call.Arguments != null && call.Arguments.Length > 1 && call.Arguments[1] is Vec3)
                    vector = (Vec3)call.Arguments[1];
            }
            catch { }

            TryLog(
                "Legacy ragdoll force: agent #" + call.AgentIndex +
                " route=FALLBACK legacyBurst=" + status +
                " vector=" + FormatVec(vector) +
                (error == null ? "." : " error=" + error + "."));
        }

        private static Exception UnwrapInvocationException(Exception ex)
        {
            TargetInvocationException invocation = ex as TargetInvocationException;
            return invocation != null && invocation.InnerException != null ? invocation.InnerException : ex;
        }

        private static float ReadFloatSetting(string propertyName, float fallback)
        {
            try
            {
                Type type = GetSafeSettingsType();
                if (type == null)
                    return fallback;
                PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null)
                    return fallback;
                object value = property.GetValue(null, null);
                if (value == null)
                    return fallback;
                float parsed = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                return IsFinite(parsed) && parsed >= 0f ? parsed : fallback;
            }
            catch { return fallback; }
        }

        private static int ReadIntSetting(string propertyName, int fallback)
        {
            try
            {
                Type type = GetSafeSettingsType();
                if (type == null)
                    return fallback;
                PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null)
                    return fallback;
                object value = property.GetValue(null, null);
                return value == null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch { return fallback; }
        }

        private static bool ReadBoolSetting(string propertyName, bool fallback)
        {
            try
            {
                Type type = GetSafeSettingsType();
                if (type == null)
                    return fallback;
                PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null)
                    return fallback;
                object value = property.GetValue(null, null);
                return value == null ? fallback : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch { return fallback; }
        }

        private static bool IsDebugLoggingEnabled()
        {
            return ReadBoolSetting("DebugLogging", false);
        }

        private static Type GetSafeSettingsType()
        {
            if (_safeSettingsType != null)
                return _safeSettingsType;
            _safeSettingsType = Type.GetType("ExtremeRagdoll.SafeRuntime.SafeSettings, ExtremeRagdoll", false);
            return _safeSettingsType;
        }

        private static Assembly FindHarmonyAssembly()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                string name;
                try { name = assemblies[i].GetName().Name; }
                catch { continue; }
                if (string.Equals(name, "0Harmony", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "HarmonyLib", StringComparison.OrdinalIgnoreCase))
                    return assemblies[i];
            }
            try { return Assembly.Load("0Harmony"); }
            catch { return null; }
        }

        private static MethodInfo FindAgentHandleBlowMethod()
        {
            MethodInfo[] methods = typeof(Agent).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, "HandleBlow", StringComparison.Ordinal))
                    continue;
                ParameterInfo[] p = method.GetParameters();
                if (p.Length != 2)
                    continue;
                Type first = p[0].ParameterType;
                Type second = p[1].ParameterType;
                if (first.IsByRef && first.GetElementType() == typeof(Blow) &&
                    second.IsByRef && second.GetElementType() == typeof(AttackCollisionData))
                    return method;
            }
            return null;
        }

        private static MethodInfo FindAgentDieMethod()
        {
            MethodInfo[] methods = typeof(Agent).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, "Die", StringComparison.Ordinal))
                    continue;
                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 2 && p[0].ParameterType == typeof(Blow))
                    return method;
            }
            return null;
        }

        private static object CreateHarmonyMethod(Type harmonyMethodType, MethodInfo method)
        {
            ConstructorInfo[] constructors = harmonyMethodType.GetConstructors();
            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] p = constructors[i].GetParameters();
                if (p.Length == 1 && p[0].ParameterType == typeof(MethodInfo))
                    return constructors[i].Invoke(new object[] { method });
            }

            object instance = Activator.CreateInstance(harmonyMethodType);
            FieldInfo methodField = harmonyMethodType.GetField("method", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (methodField != null)
            {
                methodField.SetValue(instance, method);
                return instance;
            }
            return null;
        }

        private static MethodInfo FindHarmonyPatchMethod(Type harmonyType, Type harmonyMethodType)
        {
            MethodInfo best = null;
            MethodInfo[] methods = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < methods.Length; i++)
            {
                if (!string.Equals(methods[i].Name, "Patch", StringComparison.Ordinal))
                    continue;
                ParameterInfo[] p = methods[i].GetParameters();
                if (p.Length < 2 || !typeof(MethodBase).IsAssignableFrom(p[0].ParameterType) ||
                    p[1].ParameterType != harmonyMethodType)
                    continue;
                if (best == null || p.Length > best.GetParameters().Length)
                    best = methods[i];
            }
            return best;
        }

        private static bool InvokeHarmonyPatch(
            object harmony,
            MethodInfo patch,
            MethodInfo target,
            object prefix,
            object postfix,
            object finalizer)
        {
            ParameterInfo[] parameters = patch.GetParameters();
            object[] args = new object[parameters.Length];
            bool finalizerMapped = finalizer == null;
            for (int i = 0; i < parameters.Length; i++)
            {
                string name = parameters[i].Name ?? string.Empty;
                if (i == 0 || string.Equals(name, "original", StringComparison.OrdinalIgnoreCase))
                    args[i] = target;
                else if (string.Equals(name, "prefix", StringComparison.OrdinalIgnoreCase))
                    args[i] = prefix;
                else if (string.Equals(name, "postfix", StringComparison.OrdinalIgnoreCase))
                    args[i] = postfix;
                else if (string.Equals(name, "finalizer", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = finalizer;
                    finalizerMapped = true;
                }
                else
                    args[i] = null;
            }
            if (!finalizerMapped)
                return false;
            patch.Invoke(harmony, args);
            return true;
        }

        private static bool IsCurrentMissionAgent(Agent agent, Mission current)
        {
            if (object.ReferenceEquals(agent, null) || object.ReferenceEquals(current, null))
                return false;
            try
            {
                Mission mission = agent.Mission;
                return !object.ReferenceEquals(mission, null) && object.ReferenceEquals(mission, current);
            }
            catch { return false; }
        }

        private static float GetMissionTime(Agent agent)
        {
            try
            {
                Mission mission = agent == null ? Mission.Current : agent.Mission;
                return mission == null ? 0f : mission.CurrentTime;
            }
            catch { return 0f; }
        }

        private static bool IsUsableVector(Vec3 value)
        {
            return IsFinite(value) && value.LengthSquared >= 0.000001f;
        }

        private static bool IsFinite(Vec3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float VectorMagnitude(Vec3 value)
        {
            if (!IsFinite(value))
                return 0f;
            double squared = value.LengthSquared;
            if (double.IsNaN(squared) || double.IsInfinity(squared) || squared <= 0d)
                return 0f;
            return (float)Math.Sqrt(squared);
        }

        private static int SafeAgentIndex(Agent agent)
        {
            try { return agent == null ? -1 : agent.Index; }
            catch { return -1; }
        }

        private static string FormatVec(Vec3 value)
        {
            if (!IsFinite(value))
                return "(invalid)";
            return "(" +
                value.x.ToString("0.000", CultureInfo.InvariantCulture) + "," +
                value.y.ToString("0.000", CultureInfo.InvariantCulture) + "," +
                value.z.ToString("0.000", CultureInfo.InvariantCulture) + ")";
        }

        private static string SafeText(string value)
        {
            return string.IsNullOrEmpty(value) ? "unknown" : value;
        }

        private static void TryLog(string message)
        {
            if (!IsDebugLoggingEnabled())
                return;
            try
            {
                Type logType = Type.GetType("ExtremeRagdoll.SafeRuntime.SafeLog, ExtremeRagdoll", false);
                if (logType == null)
                    return;
                MethodInfo info = logType.GetMethod("Info", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (info != null)
                    info.Invoke(null, new object[] { message });
            }
            catch { }
        }
    }
}
