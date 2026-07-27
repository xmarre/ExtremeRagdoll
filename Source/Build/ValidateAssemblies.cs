using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ValidateAssemblies
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: ValidateAssemblies ExtremeRagdoll.dll ExtremeRagdoll.ClothSync.dll");
            return 2;
        }

        using (AssemblyDefinition main = AssemblyDefinition.ReadAssembly(args[0]))
        using (AssemblyDefinition helper = AssemblyDefinition.ReadAssembly(args[1]))
        {
            Require(main.Name.Name == "ExtremeRagdoll" && main.Name.Version == new Version(0, 0, 0, 0),
                "unexpected main assembly identity");
            Require(helper.Name.Name == "ExtremeRagdoll.ClothSync" && helper.Name.Version == new Version(1, 0, 0, 0),
                "unexpected helper assembly identity");

            TypeDefinition behavior = RequireType(main, "ExtremeRagdoll.SafeRuntime.SafeRagdollBehavior");
            MethodDefinition onRegisterBlow = behavior.Methods.Single(m => m.Name == "OnRegisterBlow");
            Require(onRegisterBlow.Parameters.Count == 6, "OnRegisterBlow parameter count changed");
            Require(onRegisterBlow.IsVirtual && !onRegisterBlow.IsNewSlot && onRegisterBlow.Overrides.Count == 1,
                "OnRegisterBlow is not an explicit base override");
            Require(onRegisterBlow.Overrides[0].DeclaringType.FullName == "TaleWorlds.MountAndBlade.MissionBehavior",
                "OnRegisterBlow override target changed");
            Require(onRegisterBlow.Parameters[5].ParameterType.FullName.Contains(
                    "modreq(System.Runtime.InteropServices.InAttribute)"),
                "OnRegisterBlow final in-parameter modreq is missing");

            TypeDefinition bridge = RequireType(helper, "ExtremeRagdoll.ClothForceBridge");
            RequireMethod(bridge, "HandleBlowPrefix");
            MethodDefinition finalizer = RequireMethod(bridge, "HandleBlowFinalizer");
            Require(finalizer.ReturnType.FullName == "System.Exception", "HandleBlow finalizer must return Exception");
            RequireMethod(bridge, "AgentDiePrefix");
            RequireMethod(bridge, "GetDeathLaunchRoute");
            RequireMethod(bridge, "ReportNativeDeathResult");
            RequireMethod(bridge, "ForgetDeathRoute");

            MethodDefinition agentDiePrefix = RequireMethod(bridge, "AgentDiePrefix");
            Require(!agentDiePrefix.Body.Variables.Any(v =>
                    v.VariableType.FullName.StartsWith("System.Collections.Generic.KeyValuePair`2<", StringComparison.Ordinal) ||
                    v.VariableType.FullName.StartsWith("System.Collections.Generic.Dictionary`2/Enumerator<", StringComparison.Ordinal)),
                "AgentDiePrefix contains transplanted dictionary-enumerator value-type locals; this caused the v1.2.70 first-hit TypeLoadException");
            ValidateGenericValueTypeIdentity(helper);

            FieldDefinition activeContext = bridge.Fields.Single(f => f.Name == "_activeCombatBlow");
            Require(activeContext.CustomAttributes.Any(a => a.AttributeType.FullName == "System.ThreadStaticAttribute"),
                "combat-blow context is not thread-static");

            TypeDefinition route = RequireType(helper, "ExtremeRagdoll.DeathLaunchRoute");
            RequireEnumValue(route, "NativeHandled", 1);
            RequireEnumValue(route, "NativeIneffective", 2);
            RequireEnumValue(route, "Fallback", 3);

            Require(!helper.MainModule.AssemblyReferences.Any(r =>
                    string.Equals(r.Name, "0Harmony", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.Name, "HarmonyLib", StringComparison.OrdinalIgnoreCase)),
                "helper gained a hard Harmony assembly reference");
            Require(!helper.MainModule.GetTypeReferences().Any(t => t.FullName.Contains("ClothSimulatorComponent")),
                "helper references a native cloth wrapper");

            Require(main.MainModule.GetMemberReferences().Any(m =>
                    m.Name == "ForgetDeathRoute" && m.DeclaringType.FullName == "ExtremeRagdoll.ClothForceBridge"),
                "main runtime does not tear down helper route state on agent deletion");
            Require(!CallsMethod(RequireMethod(bridge, "DeferInvoke"), "IsNativeDeathHandled"),
                "DeferInvoke still contains hidden native-route suppression");
            Require(!main.MainModule.GetMemberReferences().Any(m =>
                    m.Name == "GetEnumerator" &&
                    m.DeclaringType.FullName == "TaleWorlds.MountAndBlade.Missions.AgentReadOnlyList"),
                "runtime contains nonexistent Bannerlord 1.3.15 AgentReadOnlyList.GetEnumerator member reference");

            ValidateAgentEnumerationBody(RequireMethod(behavior, "TrackExistingAgents"));
            ValidateAgentEnumerationBody(RequireMethod(behavior, "PollAgentHealthAndStateTransitions"));

            TypeDefinition subModule = RequireType(main, "ExtremeRagdoll.SafeRuntime.SafeSubModule");
            MethodDefinition onSubModuleLoad = RequireMethod(subModule, "OnSubModuleLoad");
            MethodDefinition onBeforeInitialScreen = RequireMethod(subModule, "OnBeforeInitialModuleScreenSetAsRoot");
            MethodDefinition onMissionBehaviorInitialize = RequireMethod(subModule, "OnMissionBehaviorInitialize");
            TypeDefinition localizationBootstrap = RequireType(main, "ExtremeRagdoll.SafeRuntime.LocalizationBootstrap");
            MethodDefinition ensureLocalization = RequireMethod(localizationBootstrap, "EnsureRegistered");
            Require(CallsMethod(onSubModuleLoad, "EnsureRegistered"),
                "OnSubModuleLoad no longer registers the module localization manifest");
            Require(CallsMethod(onBeforeInitialScreen, "EnsureRegistered"),
                "initial menu setup no longer retries localization registration");
            Require(MethodContainsString(ensureLocalization, "AddLocalizationXml"),
                "localization bootstrap no longer uses Bannerlord's native manifest registration");
            Require(MethodContainsString(ensureLocalization, "ChangeLanguage"),
                "localization bootstrap no longer reloads the active non-English dictionary");
            MethodDefinition onMissionTick = RequireMethod(behavior, "OnMissionTick");
            MethodDefinition resolveDirection = RequireMethod(behavior, "ResolveDirection");
            Require(!CallsMethod(resolveDirection, "get_EngineImpulseInfluence"),
                "captured death direction still blends native KillingBlow result data");
            Require(!CallsMethod(resolveDirection, "get_MomentumCarryover"),
                "victim momentum is still blended into direction before force construction");
            RequireMethod(behavior, "TryGetAwayFromAffectorDirection");
            MethodDefinition applyMomentumCarryover = RequireMethod(behavior, "ApplyMomentumCarryover");
            Require(CallsMethod(applyMomentumCarryover, "VectorDot"),
                "momentum carryover no longer removes the opposing longitudinal component");
            Require(CallsMethod(onMissionTick, "ApplyMomentumCarryover"),
                "first-pulse force construction no longer owns the single momentum carryover");
            RequireMethod(behavior, "VectorDot");

            Require(!CallsMethod(onSubModuleLoad, "EnsureNativeDeathPatch"),
                "OnSubModuleLoad installs the global Agent patch before the NoCombat/tableau gate");
            Require(CallsMethod(onMissionBehaviorInitialize, "EnsureNativeDeathPatch"),
                "combat mission initialization no longer installs the native death patch");
            Require(MethodContainsString(onMissionBehaviorInitialize, "NoCombat"),
                "mission initialization lost the NoCombat/tableau gate");
            Require(MethodContainsString(onMissionBehaviorInitialize,
                    "Skipped non-combat/tableau mission; no ragdoll behavior or death patch installed."),
                "mission initialization lost the explicit tableau skip path");

            Require(CallsMethod(onMissionTick, "TryStartRagdollAsCorpse"),
                "controlled accelerated StartRagdollAsCorpse handoff is missing");
            MethodDefinition processCorpseFinalizers = RequireMethod(behavior, "ProcessPendingVisualResyncs");
            Require(MethodContainsStringContaining(
                    processCorpseFinalizers,
                    "Dismemberment Plus bounded corpse-safety window elapsed; paired EndRagdollAsCorpse"),
                "Dismemberment Plus path can abandon the mod-owned corpse lifecycle pairing");
            RequireFloatConstant(behavior, "CorpseFinalizationHardDeadline", 3f);
            RequireFloatConstant(behavior, "CorpseActiveStateFallbackTimeout", 2f);
            Require(!behavior.Fields.Any(f =>
                    f.Name == "CorpseFinalizationTimeout" ||
                    f.Name == "CorpseFinalizationFailureGrace"),
                "obsolete 30-second corpse finalization timing remains in the runtime");
            Require(MethodContainsStringContaining(
                    processCorpseFinalizers,
                    "Corpse finalization retry failed"),
                "corpse-finalization retry failures are no longer diagnosable in debug logging");
            Require(!CallsMethod(onMissionTick, "ForceUpdateCorpseBoneFrames"),
                "confirmed-death path regained corpse skeleton resynchronization");
            Require(CallsMethod(onMissionTick, "TryApplyMappedCentralRagdollForce"),
                "fallback/native-ineffective post-ragdoll force delivery is missing");
            Require(MethodContainsStringContaining(onMissionTick, "Completed native-owned death launch"),
                "native-handled single-actuator completion marker is missing");

            TypeDefinition settings = RequireType(main, "ExtremeRagdoll.SafeRuntime.Settings");
            PropertyDefinition mountStrength = settings.Properties.SingleOrDefault(p => p.Name == "MountCollisionKillStrength");
            Require(mountStrength != null, "Mount Collision Kill Strength MCM property is missing");
            Require(mountStrength.CustomAttributes.Any(a => a.AttributeType.FullName == "MCM.Abstractions.Attributes.v2.SettingPropertyTextAttribute"),
                "Mount Collision Kill Strength lost its MCM text-setting attribute");
            TypeDefinition safeSettings = RequireType(main, "ExtremeRagdoll.SafeRuntime.SafeSettings");
            RequireMethod(safeSettings, "get_MountCollisionKillStrength");

            Require(CallsMethod(onRegisterBlow, "get_IsColliderAgent"),
                "OnRegisterBlow does not use Bannerlord AttackCollisionData.IsColliderAgent for mount-body collision detection");
            Require(CallsMethod(onRegisterBlow, "get_ChargeVelocity"),
                "OnRegisterBlow does not inspect charge velocity for mount-body collision detection");
            Require(CallsMethod(onRegisterBlow, "get_IsMount") || CallsMethod(onRegisterBlow, "get_MountAgent"),
                "OnRegisterBlow lost mount-context discrimination");
            Require(MethodContainsStringContaining(onRegisterBlow, "mount-collision"),
                "OnRegisterBlow does not classify mount collisions separately");
            Require(CallsMethod(onMissionTick, "get_MountCollisionKillStrength"),
                "OnMissionTick does not apply the independent mount-collision strength setting");
            Require(MethodContainsStringContaining(onMissionTick, "mountCollisionScale="),
                "mount-collision scale telemetry is missing");

            List<string> strings = ReadStrings(main).Concat(ReadStrings(helper)).ToList();
            Require(!strings.Any(s => s.Contains("Applied temporally smoothed central-body ragdoll force chunk")),
                "obsolete false Applied diagnostic remains");
            Require(strings.Any(s => s.Contains("Queued temporally smoothed central-body ragdoll force chunk")),
                "truthful queued fallback-force diagnostic is missing");
            Require(strings.Any(s => s.Contains("FIRST_COMBAT_DEATH_POST_RAGDOLL_WARMUP")),
                "first actual combat death post-ragdoll warmup route is missing");
            Require(strings.Any(s => s.Contains("KillingBlow.RagdollImpulseAmountFallbackOnly")),
                "native KillingBlow result is no longer marked as fallback-only direction data");
            Require(!strings.Any(s => s.Contains("capturedImpact+KillingBlow")),
                "obsolete captured-impact/native-result direction blend remains");
            Require(!strings.Any(s => s.Contains("rejectedOpposingKillingBlow")),
                "v1.3.12 opposing-vector guard remains after root direction fix");
            Require(!strings.Any(s => s.Contains("awayFromAffectorInvariant")),
                "v1.3.12 final-direction correction remains after root direction fix");
            Require(!strings.Any(s => s.Contains("NONMISSILE_POST_RAGDOLL_ROUTE")),
                "obsolete non-missile-only route marker remains");
            Require(strings.Any(s => s.Contains("resultCallbackAgentVelocity=")),
                "native death result physical-velocity telemetry is missing");
            Require(strings.Any(s => s.Contains("postHandleBlowAgentVelocity=")),
                "post-HandleBlow physical-velocity telemetry is missing");
            Require(strings.Any(s => s.Contains("legacyBurst=")), "route execution telemetry is missing");

            ValidateMethodBodies(main);
            ValidateMethodBodies(helper);
        }

        Console.WriteLine("assembly validation passed");
        return 0;
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName)
    {
        TypeDefinition type = assembly.MainModule.GetType(fullName);
        Require(type != null, "missing type: " + fullName);
        return type;
    }

    private static MethodDefinition RequireMethod(TypeDefinition type, string name)
    {
        MethodDefinition method = type.Methods.SingleOrDefault(m => m.Name == name);
        Require(method != null, "missing method: " + type.FullName + "." + name);
        return method;
    }

    private static void RequireEnumValue(TypeDefinition type, string name, int expected)
    {
        FieldDefinition field = type.Fields.Single(f => f.Name == name);
        Require(field.HasConstant && Convert.ToInt32(field.Constant) == expected,
            type.FullName + "." + name + " has the wrong value");
    }

    private static void RequireFloatConstant(TypeDefinition type, string name, float expected)
    {
        FieldDefinition field = type.Fields.Single(f => f.Name == name);
        Require(field.HasConstant && Math.Abs(Convert.ToSingle(field.Constant) - expected) < 0.0001f,
            type.FullName + "." + name + " has the wrong value");
    }

    private static bool CallsMethod(MethodDefinition method, string name)
    {
        return method.HasBody && method.Body.Instructions.Any(i =>
            (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
            i.Operand is MethodReference && ((MethodReference)i.Operand).Name == name);
    }

    private static bool MethodContainsString(MethodDefinition method, string value)
    {
        return method.HasBody && method.Body.Instructions.Any(i =>
            i.OpCode.Code == Code.Ldstr && string.Equals((string)i.Operand, value, StringComparison.Ordinal));
    }

    private static bool MethodContainsStringContaining(MethodDefinition method, string value)
    {
        return method.HasBody && method.Body.Instructions.Any(i =>
            i.OpCode.Code == Code.Ldstr && ((string)i.Operand).Contains(value));
    }

    private static IEnumerable<string> ReadStrings(AssemblyDefinition assembly)
    {
        foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types))
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody)
                continue;
            foreach (Instruction instruction in method.Body.Instructions)
                if (instruction.OpCode.Code == Code.Ldstr)
                    yield return (string)instruction.Operand;
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
    {
        foreach (TypeDefinition root in roots)
        {
            yield return root;
            foreach (TypeDefinition nested in AllTypes(root.NestedTypes))
                yield return nested;
        }
    }

    private static void ValidateAgentEnumerationBody(MethodDefinition method)
    {
        const string listEnumerator = "System.Collections.Generic.List`1/Enumerator<TaleWorlds.MountAndBlade.Agent>";
        const string interfaceEnumerator = "System.Collections.Generic.IEnumerator`1<TaleWorlds.MountAndBlade.Agent>";

        Require(method.HasBody, method.Name + " has no body");
        Require(method.Body.Variables.Any(v => v.VariableType.FullName == listEnumerator),
            method.Name + " must store Mission.AllAgents enumeration in List<Agent>.Enumerator");
        Require(!method.Body.Variables.Any(v => v.VariableType.FullName == interfaceEnumerator),
            method.Name + " contains the invalid IEnumerator<Agent> local used by v1.2.65");

        Require(method.Body.Instructions.Any(i =>
                (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt) &&
                i.Operand is MethodReference &&
                ((MethodReference)i.Operand).Name == "GetEnumerator" &&
                ((MethodReference)i.Operand).DeclaringType.FullName == "System.Collections.Generic.List`1<TaleWorlds.MountAndBlade.Agent>"),
            method.Name + " does not use the inherited List<Agent>.GetEnumerator path");
        Require(method.Body.Instructions.Any(i =>
                i.OpCode.Code == Code.Constrained &&
                i.Operand is TypeReference &&
                ((TypeReference)i.Operand).FullName == listEnumerator),
            method.Name + " is missing constrained disposal for the List<Agent>.Enumerator value type");
    }


    private static void ValidateGenericValueTypeIdentity(AssemblyDefinition assembly)
    {
        foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types))
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody)
                continue;
            foreach (VariableDefinition variable in method.Body.Variables)
            {
                string name = variable.VariableType.FullName;
                if (name.StartsWith("System.Collections.Generic.KeyValuePair`2<", StringComparison.Ordinal) ||
                    name.StartsWith("System.Collections.Generic.Dictionary`2/Enumerator<", StringComparison.Ordinal))
                {
                    Require(variable.VariableType.IsValueType,
                        "generic value-type identity conflict in " + method.FullName + ": " + name);
                }
            }
        }
    }

    private static void ValidateMethodBodies(AssemblyDefinition assembly)
    {
        foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types))
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody)
                continue;
            HashSet<Instruction> instructions = new HashSet<Instruction>(method.Body.Instructions);
            foreach (Instruction instruction in method.Body.Instructions)
            {
                Instruction target = instruction.Operand as Instruction;
                if (target != null)
                    Require(instructions.Contains(target), "invalid branch target in " + method.FullName);
                Instruction[] targets = instruction.Operand as Instruction[];
                if (targets != null)
                    Require(targets.All(instructions.Contains), "invalid switch target in " + method.FullName);
            }
            foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
            {
                RequireBoundary(instructions, handler.TryStart, method);
                RequireBoundary(instructions, handler.TryEnd, method);
                RequireBoundary(instructions, handler.HandlerStart, method);
                RequireBoundary(instructions, handler.HandlerEnd, method);
                RequireBoundary(instructions, handler.FilterStart, method);
            }
        }
    }

    private static void RequireBoundary(HashSet<Instruction> instructions, Instruction boundary, MethodDefinition method)
    {
        Require(boundary == null || instructions.Contains(boundary), "invalid exception boundary in " + method.FullName);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

