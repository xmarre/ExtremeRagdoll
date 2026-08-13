using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll.SafeRuntime
{
    /// <summary>
    /// Late-binds MissionBehavior.OnRegisterBlow so the runtime does not encode one exact
    /// Bannerlord callback signature in SafeRagdollBehavior's type metadata. Bannerlord has
    /// changed callback modifiers/signatures between runtime revisions; an invalid CLR override
    /// can fail while the type is being loaded, before any managed compatibility code can run.
    /// </summary>
    internal static class RegisterBlowCompatibility
    {
        private const string HarmonyId = "xmarre.extremeragdoll.register-blow-compatibility";
        private static readonly object Gate = new object();
        private static bool _installed;

        internal static bool EnsureInstalled()
        {
            lock (Gate)
            {
                if (_installed)
                    return true;

                try
                {
                    Type harmonyType = FindLoadedType("HarmonyLib.Harmony");
                    Type harmonyMethodType = FindLoadedType("HarmonyLib.HarmonyMethod");
                    if (harmonyType == null || harmonyMethodType == null)
                    {
                        SafeLog.Info("Register-blow compatibility bridge is waiting for Harmony.");
                        return false;
                    }

                    ConstructorInfo harmonyConstructor = harmonyType.GetConstructor(new[] { typeof(string) });
                    ConstructorInfo harmonyMethodConstructor = harmonyMethodType.GetConstructor(new[] { typeof(MethodInfo) });
                    MethodInfo patchMethod = FindHarmonyPatchMethod(harmonyType, harmonyMethodType);
                    MethodInfo prefixMethod = typeof(RegisterBlowCompatibility).GetMethod(
                        "OnRegisterBlowPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                    if (harmonyConstructor == null || harmonyMethodConstructor == null ||
                        patchMethod == null || prefixMethod == null)
                    {
                        throw new MissingMethodException(
                            "Harmony reflection members required for the register-blow compatibility bridge were not found.");
                    }

                    MethodInfo[] targets = FindCompatibleTargets();
                    if (targets.Length == 0)
                    {
                        SafeLog.Info(
                            "Bannerlord exposes no compatible MissionBehavior.OnRegisterBlow callback; " +
                            "health/state/removal death fallbacks remain active.");
                        return false;
                    }

                    object harmony = harmonyConstructor.Invoke(new object[] { HarmonyId });
                    int installedCount = 0;
                    for (int i = 0; i < targets.Length; i++)
                    {
                        try
                        {
                            PatchPrefix(
                                harmony,
                                patchMethod,
                                harmonyMethodType,
                                harmonyMethodConstructor,
                                targets[i],
                                prefixMethod);
                            installedCount++;
                        }
                        catch (TargetInvocationException ex)
                        {
                            SafeLog.Error(
                                "Register-blow compatibility patch failed for " + targets[i],
                                ex.InnerException ?? ex);
                        }
                        catch (Exception ex)
                        {
                            SafeLog.Error(
                                "Register-blow compatibility patch failed for " + targets[i], ex);
                        }
                    }

                    if (installedCount == 0)
                        return false;

                    _installed = true;
                    SafeLog.Info(
                        "Installed late-bound MissionBehavior.OnRegisterBlow compatibility bridge on " +
                        installedCount + " callback(s); no exact callback override is encoded in SafeRagdollBehavior.");
                    return true;
                }
                catch (TargetInvocationException ex)
                {
                    SafeLog.Error(
                        "Register-blow compatibility bridge installation failed",
                        ex.InnerException ?? ex);
                    return false;
                }
                catch (Exception ex)
                {
                    SafeLog.Error("Register-blow compatibility bridge installation failed", ex);
                    return false;
                }
            }
        }

        private static MethodInfo[] FindCompatibleTargets()
        {
            var targets = new List<MethodInfo>();
            MethodInfo[] methods = typeof(MissionBehavior).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.IsAbstract ||
                    !string.Equals(method.Name, "OnRegisterBlow", StringComparison.Ordinal))
                {
                    continue;
                }

                ParameterInfo[] parameters;
                try { parameters = method.GetParameters(); }
                catch { continue; }

                bool hasBlow = false;
                bool hasAgent = false;
                for (int p = 0; p < parameters.Length; p++)
                {
                    Type parameterType = Unwrap(parameters[p].ParameterType);
                    if (parameterType == typeof(Blow))
                        hasBlow = true;
                    else if (parameterType == typeof(Agent))
                        hasAgent = true;
                }

                if (hasBlow && hasAgent)
                    targets.Add(method);
            }

            return targets.ToArray();
        }

        private static void OnRegisterBlowPrefix(
            object __instance,
            MethodBase __originalMethod,
            object[] __args)
        {
            SafeRagdollBehavior behavior = __instance as SafeRagdollBehavior;
            if (behavior == null || __originalMethod == null || __args == null)
                return;

            try
            {
                ParameterInfo[] parameters = __originalMethod.GetParameters();
                Agent attacker = null;
                Agent victim = null;
                bool attackerAssigned = false;
                bool victimAssigned = false;
                Blow blow = default(Blow);
                bool hasBlow = false;
                AttackCollisionData collisionData = default(AttackCollisionData);
                WeakGameEntity realHitEntity = default(WeakGameEntity);
                MissionWeapon attackerWeapon = default(MissionWeapon);

                int count = Math.Min(parameters.Length, __args.Length);
                for (int i = 0; i < count; i++)
                {
                    ParameterInfo parameter = parameters[i];
                    Type parameterType = Unwrap(parameter.ParameterType);
                    object argument = __args[i];
                    string name = parameter.Name ?? string.Empty;

                    if (parameterType == typeof(Agent))
                    {
                        Agent agent = argument as Agent;
                        if (NameLooksLikeVictim(name))
                        {
                            victim = agent;
                            victimAssigned = true;
                        }
                        else if (NameLooksLikeAttacker(name))
                        {
                            attacker = agent;
                            attackerAssigned = true;
                        }
                        else if (!attackerAssigned)
                        {
                            attacker = agent;
                            attackerAssigned = true;
                        }
                        else if (!victimAssigned)
                        {
                            victim = agent;
                            victimAssigned = true;
                        }
                    }
                    else if (parameterType == typeof(Blow) && argument is Blow)
                    {
                        blow = (Blow)argument;
                        hasBlow = true;
                    }
                    else if (parameterType == typeof(AttackCollisionData) && argument is AttackCollisionData)
                    {
                        collisionData = (AttackCollisionData)argument;
                    }
                    else if (parameterType == typeof(WeakGameEntity) && argument is WeakGameEntity)
                    {
                        realHitEntity = (WeakGameEntity)argument;
                    }
                    else if (parameterType == typeof(MissionWeapon) && argument is MissionWeapon)
                    {
                        attackerWeapon = (MissionWeapon)argument;
                    }
                }

                // Preserve the historical two-Agent ordering if parameter names were stripped.
                if (!victimAssigned)
                {
                    int seenAgents = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (Unwrap(parameters[i].ParameterType) != typeof(Agent))
                            continue;
                        if (seenAgents == 1)
                        {
                            victim = __args[i] as Agent;
                            victimAssigned = true;
                            break;
                        }
                        seenAgents++;
                    }
                }

                if (!hasBlow || !victimAssigned)
                    return;

                behavior.OnRegisterBlowCompat(
                    attacker,
                    victim,
                    realHitEntity,
                    blow,
                    ref collisionData,
                    ref attackerWeapon);
            }
            catch (Exception ex)
            {
                // This is an observational compatibility callback. Never make a combat hit fatal
                // merely because a future Bannerlord revision supplied an unexpected argument shape.
                SafeLog.Error("Late-bound OnRegisterBlow dispatch failed; fallback death tracking remains active", ex);
            }
        }

        private static bool NameLooksLikeVictim(string name)
        {
            return name.IndexOf("victim", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("affected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("target", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool NameLooksLikeAttacker(string name)
        {
            return name.IndexOf("attacker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("affector", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Type Unwrap(Type type)
        {
            return type != null && type.IsByRef ? type.GetElementType() : type;
        }

        private static void PatchPrefix(
            object harmony,
            MethodInfo patchMethod,
            Type harmonyMethodType,
            ConstructorInfo harmonyMethodConstructor,
            MethodBase original,
            MethodInfo prefixMethod)
        {
            object prefix = harmonyMethodConstructor.Invoke(new object[] { prefixMethod });
            ParameterInfo[] parameters = patchMethod.GetParameters();
            object[] arguments = new object[parameters.Length];
            arguments[0] = original;
            for (int i = 1; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType == harmonyMethodType &&
                    string.Equals(parameters[i].Name, "prefix", StringComparison.OrdinalIgnoreCase))
                {
                    arguments[i] = prefix;
                }
                else if (parameters[i].HasDefaultValue)
                {
                    arguments[i] = parameters[i].DefaultValue;
                }
                else
                {
                    arguments[i] = null;
                }
            }

            patchMethod.Invoke(harmony, arguments);
        }

        private static MethodInfo FindHarmonyPatchMethod(Type harmonyType, Type harmonyMethodType)
        {
            MethodInfo[] methods = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, "Patch", StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2 || !typeof(MethodBase).IsAssignableFrom(parameters[0].ParameterType))
                    continue;

                for (int p = 1; p < parameters.Length; p++)
                {
                    if (parameters[p].ParameterType == harmonyMethodType &&
                        string.Equals(parameters[p].Name, "prefix", StringComparison.OrdinalIgnoreCase))
                    {
                        return method;
                    }
                }
            }
            return null;
        }

        private static Type FindLoadedType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly == null)
                    continue;
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch
                {
                }
            }
            return null;
        }
    }
}
