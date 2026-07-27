from pathlib import Path
import math
import re
import xml.etree.ElementTree as ET


def read_preserving(path: Path):
    raw = path.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    if bom:
        raw = raw[3:]
    newline = "\r\n" if b"\r\n" in raw else "\n"
    text = raw.decode("utf-8").replace("\r\n", "\n")
    return text, newline, bom


def write_preserving(path: Path, text: str, newline: str, bom: bool):
    encoded = text.replace("\n", newline).encode("utf-8")
    if bom:
        encoded = b"\xef\xbb\xbf" + encoded
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(encoded)


def replace_exact(path: Path, old: str, new: str):
    text, newline, bom = read_preserving(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one exact replacement, got {count}")
    write_preserving(path, text.replace(old, new), newline, bom)


def replace_regex(path: Path, pattern: str, replacement: str):
    text, newline, bom = read_preserving(path)
    updated, count = re.subn(pattern, replacement, text, flags=re.MULTILINE | re.DOTALL)
    if count != 1:
        raise RuntimeError(f"{path}: expected one regex replacement, got {count}")
    write_preserving(path, updated, newline, bom)


def normalized(v):
    length = math.sqrt(sum(c * c for c in v))
    if length <= 1e-12:
        return (0.0, 0.0, 0.0)
    return tuple(c / length for c in v)


def dot(a, b):
    return sum(a[i] * b[i] for i in range(3))


def resolve_direction(captured, away, engine, momentum, lift=0.25):
    direction = captured
    source = "captured"
    if dot(direction, direction) <= 1e-12 and dot(away, away) > 1e-12:
        direction = away
        source = "away"
    if dot(direction, direction) <= 1e-12 and dot(engine, engine) > 1e-12:
        direction = engine
        source = "engine-fallback"
    if dot(direction, direction) <= 1e-12 and dot(momentum, momentum) > 1e-12:
        direction = momentum
        source = "momentum-fallback"
    if dot(direction, direction) <= 1e-12:
        direction = (0.0, 1.0, 0.0)
        source = "world-forward"
    direction = normalized(direction)
    direction = (direction[0], direction[1], direction[2] + lift)
    return normalized(direction), source


def apply_momentum(base_force, momentum, launch_direction, scale):
    axis = normalized(launch_direction)
    m = tuple(momentum[i] * scale for i in range(3))
    parallel = dot(m, axis)
    if parallel < 0.0:
        m = tuple(m[i] - axis[i] * parallel for i in range(3))
    return tuple(base_force[i] + m[i] for i in range(3))


# Regression models for the real defect chain.
direction, source = resolve_direction(
    captured=(1.0, 0.0, 0.0), away=(1.0, 0.0, 0.0),
    engine=(-1.0, 0.0, 0.0), momentum=(-30.0, 0.0, 0.0))
if source != "captured" or direction[0] <= 0.0:
    raise RuntimeError("captured missile direction lost authority")

fallback, source = resolve_direction(
    captured=(0.0, 0.0, 0.0), away=(1.0, 0.0, 0.0),
    engine=(-1.0, 0.0, 0.0), momentum=(-30.0, 0.0, 0.0))
if source != "away" or fallback[0] <= 0.0:
    raise RuntimeError("source geometry did not precede native-result fallback")

engine_only, source = resolve_direction(
    captured=(0.0, 0.0, 0.0), away=(0.0, 0.0, 0.0),
    engine=(-1.0, 0.0, 0.0), momentum=(0.0, 0.0, 0.0))
if source != "engine-fallback" or engine_only[0] >= 0.0:
    raise RuntimeError("native result no longer works as last-resort direction data")

carried = apply_momentum((100.0, 0.0, 0.0), (-30.0, 7.0, 0.0), (1.0, 0.0, 0.0), 10.0)
if carried[0] < 99.999 or carried[1] <= 0.0:
    raise RuntimeError("opposing momentum was not removed or lateral momentum was lost")

safe_path = Path("Source/SafeSubModule.cs")
new_direction_block = r'''        private static Vec3 ResolveDirection(
            Agent affected,
            Agent affector,
            Vec3 blowDirection,
            Vec3 victimMomentum,
            Vec3 engineImpulse,
            bool hasEngineImpulse,
            string capturedSource,
            out string source)
        {
            // The hit callback already provides the authoritative impact direction. A later
            // KillingBlow.RagdollImpulseAmount is native death-result data and may be expressed
            // with a different sign/space; it is used only when no impact or source geometry exists.
            Vec3 direction = IsFinite(blowDirection) ? blowDirection : Vec3.Zero;
            source = IsUsableVector(direction)
                ? (string.IsNullOrEmpty(capturedSource) ? "capturedImpact" : capturedSource)
                : "unknown";

            Vec3 awayFromAffector;
            bool hasAwayFromAffector = TryGetAwayFromAffectorDirection(
                affected, affector, out awayFromAffector);

            if (!IsUsableVector(direction) && hasAwayFromAffector)
            {
                direction = awayFromAffector;
                source = "awayFromAffector";
            }

            if (!IsUsableVector(direction) && hasEngineImpulse && IsUsableVector(engineImpulse))
            {
                direction = engineImpulse;
                source = "KillingBlow.RagdollImpulseAmountFallbackOnly";
            }

            if (!IsUsableVector(direction) && IsUsableVector(victimMomentum))
            {
                direction = victimMomentum;
                source = "victimMomentumFallbackOnly";
            }

            if (!IsUsableVector(direction))
            {
                try
                {
                    direction = affected.LookDirection;
                    source = "victimLookDirection";
                }
                catch { direction = new Vec3(0f, 1f, 0f); }
            }

            if (!IsUsableVector(direction))
            {
                direction = new Vec3(0f, 1f, 0f);
                source = "worldForwardFallback";
            }

            direction = direction.NormalizedCopy();
            direction.z += SafeSettings.UpwardLift;
            if (!IsUsableVector(direction))
                return new Vec3(0f, 1f, 0.25f).NormalizedCopy();
            return direction.NormalizedCopy();
        }

        private static bool TryGetAwayFromAffectorDirection(
            Agent affected,
            Agent affector,
            out Vec3 awayFromAffector)
        {
            awayFromAffector = Vec3.Zero;
            if (affected == null || affector == null || ReferenceEquals(affected, affector))
                return false;

            try
            {
                awayFromAffector = affected.Position - affector.Position;
                awayFromAffector.z = 0f;
            }
            catch
            {
                awayFromAffector = Vec3.Zero;
                return false;
            }

            if (!IsUsableVector(awayFromAffector))
                return false;
            awayFromAffector = awayFromAffector.NormalizedCopy();
            return true;
        }

        private static Vec3 ApplyMomentumCarryover(
            Vec3 baseForce,
            Vec3 victimMomentum,
            Vec3 launchDirection)
        {
            if (!IsUsableVector(baseForce) || !IsUsableVector(victimMomentum) ||
                !IsUsableVector(launchDirection) || SafeSettings.MomentumCarryover <= 0f)
            {
                return baseForce;
            }

            float momentumForceScale = 3000f * SafeSettings.OverallStrength * SafeSettings.MomentumCarryover;
            Vec3 momentumForce = victimMomentum * momentumForceScale;
            Vec3 axis = launchDirection.NormalizedCopy();
            float parallel = VectorDot(momentumForce, axis);
            if (parallel < 0f)
            {
                // Carry lateral/vertical movement once. Motion directly against the killing blow
                // may reduce neither its sign nor its requested launch strength.
                momentumForce -= axis * parallel;
            }
            return baseForce + momentumForce;
        }

        private static float VectorDot(Vec3 left, Vec3 right)
        {
            return left.x * right.x + left.y * right.y + left.z * right.z;
        }

'''
replace_regex(
    safe_path,
    r"        private static Vec3 ResolveDirection\([\s\S]*?(?=        private static Vec3 ResolvePulseDirection\()",
    new_direction_block)

replace_exact(
    safe_path,
    '''                    if (pending.PulseIndex == 0 && IsUsableVector(pending.VictimMomentum))
                    {
                        float momentumForceScale = 3000f * SafeSettings.OverallStrength * SafeSettings.MomentumCarryover;
                        fullPulseForce += pending.VictimMomentum * momentumForceScale;
                    }
''',
    '''                    if (pending.PulseIndex == 0)
                    {
                        fullPulseForce = ApplyMomentumCarryover(
                            fullPulseForce, pending.VictimMomentum, pulseDirection);
                    }
''')

replace_exact(
    safe_path,
    '''[SettingPropertyText("{=ER_MomentumCarryover_Name}Movement Momentum Carryover", 6, false, "{=ER_MomentumCarryover_Hint}Blends the victim's movement at the instant of death into the native launch direction. Default 1.0.")]''',
    '''[SettingPropertyText("{=ER_MomentumCarryover_Name}Movement Momentum Carryover", 6, false, "{=ER_MomentumCarryover_Hint}Carries the victim's movement into the first death-force pulse once. Opposing longitudinal momentum is discarded so movement cannot reverse the killing blow. Default 1.0.")]''')

replace_exact(safe_path, 'using TaleWorlds.MountAndBlade;\n', 'using TaleWorlds.MountAndBlade;\nusing TaleWorlds.Localization;\n')
replace_exact(
    safe_path,
    'public override string DisplayName { get { return "{=ER_DisplayName}Extreme Ragdoll"; } }',
    'public override string DisplayName { get { return new TextObject("{=ER_DisplayName}Extreme Ragdoll").ToString(); } }')

validator = Path("Source/Build/ValidateAssemblies.cs")
replace_exact(
    validator,
    '''            MethodDefinition resolveDirection = RequireMethod(behavior, "ResolveDirection");
            Require(CallsMethod(resolveDirection, "IsOpposingDirection"),
                "death direction no longer rejects oppositely signed KillingBlow impulses");
            Require(CallsMethod(resolveDirection, "EnforceAwayFromAffectorInvariant"),
                "death direction no longer enforces the source-away invariant after momentum/lift blending");
            RequireMethod(behavior, "TryGetAwayFromAffectorDirection");
            RequireMethod(behavior, "HorizontalDot");
            RequireMethod(behavior, "VectorDot");
''',
    '''            MethodDefinition resolveDirection = RequireMethod(behavior, "ResolveDirection");
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
''')
replace_exact(
    validator,
    '''            Require(strings.Any(s => s.Contains("rejectedOpposingKillingBlow")),
                "opposing KillingBlow rejection telemetry is missing");
            Require(strings.Any(s => s.Contains("awayFromAffectorInvariant")),
                "source-away direction invariant telemetry is missing");
''',
    '''            Require(strings.Any(s => s.Contains("KillingBlow.RagdollImpulseAmountFallbackOnly")),
                "native KillingBlow result is no longer marked as fallback-only direction data");
            Require(!strings.Any(s => s.Contains("capturedImpact+KillingBlow")),
                "obsolete captured-impact/native-result direction blend remains");
            Require(!strings.Any(s => s.Contains("rejectedOpposingKillingBlow")),
                "v1.3.12 opposing-vector guard remains after root direction fix");
            Require(!strings.Any(s => s.Contains("awayFromAffectorInvariant")),
                "v1.3.12 final-direction correction remains after root direction fix");
''')

stub = Path("Source/Build/ReferenceStubs/TaleWorlds.Localization.Stub.cs")
stub.write_text('''using System.Collections.Generic;\nusing System.Reflection;\n\n[assembly: AssemblyVersion("1.0.0.0")]\n\nnamespace TaleWorlds.Localization\n{\n    public class TextObject\n    {\n        public TextObject(string value = "", Dictionary<string, object> attributes = null) { }\n        public override string ToString() { return string.Empty; }\n    }\n}\n''', encoding="utf-8")

ps1 = Path("Source/Build/build.ps1")
replace_exact(
    ps1,
    'Compile ($Common + @("/out:$StubDir/TaleWorlds.Core.dll", "$Stubs/TaleWorlds.Core.Stub.cs"))\n',
    'Compile ($Common + @("/out:$StubDir/TaleWorlds.Core.dll", "$Stubs/TaleWorlds.Core.Stub.cs"))\nCompile ($Common + @("/out:$StubDir/TaleWorlds.Localization.dll", "$Stubs/TaleWorlds.Localization.Stub.cs"))\n')
replace_exact(
    ps1,
    '"/r:$StubDir/TaleWorlds.MountAndBlade.dll", "/r:$StubDir/MCMv5.dll", "/r:$BinDir/ExtremeRagdoll.ClothSync.dll", "$SourceDir/SafeSubModule.cs"))',
    '"/r:$StubDir/TaleWorlds.MountAndBlade.dll", "/r:$StubDir/TaleWorlds.Localization.dll", "/r:$StubDir/MCMv5.dll", "/r:$BinDir/ExtremeRagdoll.ClothSync.dll", "$SourceDir/SafeSubModule.cs"))')

sh = Path("Source/Build/build.sh")
replace_exact(
    sh,
    '"${common[@]}" "/out:$STUB_DIR/TaleWorlds.Core.dll" "$stubs/TaleWorlds.Core.Stub.cs"\n',
    '"${common[@]}" "/out:$STUB_DIR/TaleWorlds.Core.dll" "$stubs/TaleWorlds.Core.Stub.cs"\n"${common[@]}" "/out:$STUB_DIR/TaleWorlds.Localization.dll" "$stubs/TaleWorlds.Localization.Stub.cs"\n')
replace_exact(
    sh,
    '    "/r:$STUB_DIR/TaleWorlds.MountAndBlade.dll" "/r:$STUB_DIR/MCMv5.dll" "/r:$BIN_DIR/ExtremeRagdoll.ClothSync.dll" \\\n',
    '    "/r:$STUB_DIR/TaleWorlds.MountAndBlade.dll" "/r:$STUB_DIR/TaleWorlds.Localization.dll" "/r:$STUB_DIR/MCMv5.dll" "/r:$BIN_DIR/ExtremeRagdoll.ClothSync.dll" \\\n')

english = Path("ModuleData/Languages/std_module_strings_xml.xml")
replace_exact(
    english,
    'text="Blends the victim&#x27;s movement at the instant of death into the native launch direction. Default 1.0."',
    'text="Carries the victim&#x27;s movement into the first death-force pulse once. Opposing longitudinal momentum is discarded so movement cannot reverse the killing blow. Default 1.0."')

chinese = Path("ModuleData/Languages/CNs/std_module_strings_xml.xml")
replace_exact(
    chinese,
    'text="将受害者死亡瞬间的移动速度混入原生抛飞方向。默认值：1.0。"',
    'text="仅在第一次死亡推力脉冲中继承一次受害者的移动速度。与致命一击方向相反的纵向动量会被移除，因此移动不会反转抛飞方向。默认值：1.0。"')

language_data = Path("ModuleData/Languages/CNs/language_data.xml")
language_data.write_text('''<?xml version="1.0" encoding="utf-8"?>\n<LanguageData xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="https://raw.githubusercontent.com/BUTR/Bannerlord.XmlSchemas/master/ModuleLanguageData.xsd" id="简体中文" name="简体中文" subtitle_extension="zh-HANS" supported_iso="zh-HANS,zh,zho,chi,zh-cn,zh-sg" under_development="false">\n  <LanguageFile xml_path="CNs/std_module_strings_xml.xml"/>\n</LanguageData>\n''', encoding="utf-8")

replace_exact(Path("SubModule.xml"), '<Version value="v1.3.12" />', '<Version value="v1.3.13" />')
replace_exact(Path("README.md"), 'Current repository version: **v1.3.12**', 'Current repository version: **v1.3.13**')
replace_exact(Path("README.md"), '`Source/` — current v1.3.12 C# source', '`Source/` — current v1.3.13 C# source')
replace_regex(
    Path("README.md"),
    r"## v1\.3\.12 scope\n[\s\S]*$",
    '''## v1.3.13 scope\n\n- Makes the captured hit direction authoritative for normal death launches.\n- Uses `KillingBlow.RagdollImpulseAmount` only as a last-resort fallback when neither impact data nor attacker geometry exists.\n- Applies victim momentum exactly once during first-pulse force construction and removes only the opposing longitudinal component.\n- Registers the Simplified Chinese string file through `CNs/language_data.xml`.\n- Resolves the MCM display name through Bannerlord `TextObject` instead of exposing the raw localization token.\n- Retains force magnitude, pulse delivery, hit-bone routing, mount-collision scaling, corpse lifecycle behavior, and Dismemberment Plus safeguards.\n\nRuntime validation inside Bannerlord remains required for native physics and MCM rendering behavior that cannot be executed in GitHub Actions.\n''')

changelog = Path("CHANGELOG.md")
text, newline, bom = read_preserving(changelog)
entry = '''## v1.3.13\n\n- Replaced the v1.3.12 direction guards with an authoritative-source direction pipeline.\n- Stopped blending native `KillingBlow.RagdollImpulseAmount` result data into an already captured hit direction.\n- Applied victim movement momentum once and prevented its opposing longitudinal component from reversing the killing blow.\n- Added the missing Simplified Chinese `language_data.xml` registration manifest.\n- Resolved the MCM display name through `TextObject` so the raw `{=ER_DisplayName}` token is no longer shown.\n\n'''
if "## v1.3.13" in text:
    raise RuntimeError("CHANGELOG already contains v1.3.13")
text = text.replace("# Changelog\n\n", "# Changelog\n\n" + entry, 1)
write_preserving(changelog, text, newline, bom)

replace_exact(Path(".github/workflows/build.yml"), 'ExtremeRagdoll-v1.3.12.zip', 'ExtremeRagdoll-v1.3.13.zip')
replace_exact(Path(".github/workflows/build.yml"), 'name: ExtremeRagdoll-v1.3.12', 'name: ExtremeRagdoll-v1.3.13')

base_root = ET.parse(english).getroot()
cn_root = ET.parse(chinese).getroot()
base_ids = {node.attrib["id"] for node in base_root.findall("./strings/string")}
cn_ids = {node.attrib["id"] for node in cn_root.findall("./strings/string")}
if base_ids != cn_ids:
    raise RuntimeError(f"Chinese localization key mismatch: missing={sorted(base_ids-cn_ids)} extra={sorted(cn_ids-base_ids)}")
manifest_root = ET.parse(language_data).getroot()
files = [node.attrib.get("xml_path") for node in manifest_root.findall("LanguageFile")]
if files != ["CNs/std_module_strings_xml.xml"]:
    raise RuntimeError(f"Unexpected Simplified Chinese manifest: {files}")

print("Applied v1.3.13 root direction and localization patch")
