COMPILE-TIME REFERENCE STUBS

These minimal assemblies encode only the external type/member signatures used by
ExtremeRagdoll. They are build inputs, not game replacements, and must never be
copied into Bannerlord's Modules directory.

The TaleWorlds.MountAndBlade definitions, especially Agent.HandleBlow,
Agent.Die and MissionBehavior.OnRegisterBlow, were checked against Bannerlord
1.3.15. PatchOverride copies the exact OnRegisterBlow parameter types and
required InAttribute modifier from the generated reference assembly.

MCMv5.Stub.cs carries assembly version 5.12.1.0 and the attribute constructors
used by the runtime. The shipped module still requires the real MCM v5 module.
