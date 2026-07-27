from pathlib import Path
import math
import re


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
    path.write_bytes(encoded)


def replace_regex(path: Path, pattern: str, replacement: str):
    text, newline, bom = read_preserving(path)
    updated, count = re.subn(pattern, replacement, text, flags=re.MULTILINE | re.DOTALL)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one replacement, got {count}")
    write_preserving(path, updated, newline, bom)


def replace_exact(path: Path, old: str, new: str):
    text, newline, bom = read_preserving(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one exact replacement, got {count}")
    write_preserving(path, text.replace(old, new), newline, bom)


def normalized(v):
    length = math.sqrt(sum(component * component for component in v))
    return tuple(component / length for component in v)


def horizontal_dot(left, right):
    return left[0] * right[0] + left[1] * right[1]


def model_direction(captured, engine, momentum, away, lift=0.25, influence=1.0):
    captured = normalized(captured)
    engine = normalized(engine)
    captured_h = (captured[0], captured[1], 0.0)
    engine_h = (engine[0], engine[1], 0.0)
    if horizontal_dot(normalized(captured_h), normalized(engine_h)) < -0.05:
        direction = captured
    else:
        direction = normalized(tuple(captured[i] + engine[i] * influence for i in range(3)))
    direction = tuple(direction[i] + momentum[i] * 0.10 for i in range(3))
    direction = (direction[0], direction[1], direction[2] + lift)
    direction_h = (direction[0], direction[1], 0.0)
    away_h = normalized((away[0], away[1], 0.0))
    if horizontal_dot(direction_h, away_h) < 0.0 or horizontal_dot(direction_h, direction_h) <= 1e-12:
        captured_h = (captured[0], captured[1], 0.0)
        replacement_h = normalized(captured_h) if horizontal_dot(captured_h, away_h) > 0.0 else away_h
        direction = (replacement_h[0], replacement_h[1], direction[2])
    return normalized(direction)


# Regression models for the reported first-arrow path and a momentum-heavy path.
first_arrow = model_direction((1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), (0.0, 0.0, 0.0), (1.0, 0.0, 0.0))
if first_arrow[0] <= 0.0:
    raise RuntimeError("opposing KillingBlow impulse still reverses the first-arrow model")
momentum_heavy = model_direction((1.0, 0.0, 0.0), (1.0, 0.0, 0.0), (-30.0, 0.0, 0.0), (1.0, 0.0, 0.0))
if momentum_heavy[0] <= 0.0:
    raise RuntimeError("victim momentum still reverses the source-away invariant")

safe_path = Path("Source/SafeSubModule.cs")
new_resolve_direction = r'''        private static Vec3 ResolveDirection(
            Agent affected,
            Agent affector,
            Vec3 blowDirection,
            Vec3 victimMomentum,
            Vec3 engineImpulse,
            bool hasEngineImpulse,
            string capturedSource,
            out string source)
        {
            Vec3 capturedImpact = IsFinite(blowDirection) ? blowDirection : Vec3.Zero;
            Vec3 direction = capturedImpact;
            source = IsUsableVector(direction)
                ? (string.IsNullOrEmpty(capturedSource) ? "capturedImpact" : capturedSource)
                : "unknown";

            Vec3 awayFromAffector;
            bool hasAwayFromAffector = TryGetAwayFromAffectorDirection(
                affected, affector, out awayFromAffector);

            if (hasEngineImpulse && IsUsableVector(engineImpulse))
            {
                Vec3 engineDirection = engineImpulse.NormalizedCopy();
                float influence = SafeSettings.EngineImpulseInfluence;
                if (IsUsableVector(direction))
                {
                    Vec3 capturedDirection = direction.NormalizedCopy();
                    if (IsOpposingDirection(capturedDirection, engineDirection))
                    {
                        // KillingBlow.RagdollImpulseAmount can be reported with the opposite sign,
                        // especially during the first corpse initialization. The exact hit direction
                        // is authoritative; an opposing engine vector must never cancel or reverse it.
                        direction = capturedDirection;
                        source += "+rejectedOpposingKillingBlow";
                    }
                    else
                    {
                        direction = capturedDirection + engineDirection * influence;
                        source = "capturedImpact+KillingBlow";
                    }
                }
                else
                {
                    direction = engineDirection;
                    source = "KillingBlow.RagdollImpulseAmount";
                }
            }

            if (!IsUsableVector(direction) && hasAwayFromAffector)
            {
                direction = awayFromAffector;
                source = "awayFromAffector";
            }

            if (!IsUsableVector(direction) && IsUsableVector(victimMomentum))
            {
                direction = victimMomentum;
                source = "victimMomentum";
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

            if (IsUsableVector(victimMomentum) && SafeSettings.MomentumCarryover > 0f)
            {
                direction += victimMomentum * (0.10f * SafeSettings.MomentumCarryover);
                source += "+momentum";
            }

            direction.z += SafeSettings.UpwardLift;
            if (hasAwayFromAffector)
            {
                direction = EnforceAwayFromAffectorInvariant(
                    direction, capturedImpact, awayFromAffector, ref source);
            }

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

        private static bool IsOpposingDirection(Vec3 left, Vec3 right)
        {
            if (!IsUsableVector(left) || !IsUsableVector(right))
                return false;

            Vec3 leftHorizontal = left;
            Vec3 rightHorizontal = right;
            leftHorizontal.z = 0f;
            rightHorizontal.z = 0f;
            if (IsUsableVector(leftHorizontal) && IsUsableVector(rightHorizontal))
            {
                leftHorizontal = leftHorizontal.NormalizedCopy();
                rightHorizontal = rightHorizontal.NormalizedCopy();
                return HorizontalDot(leftHorizontal, rightHorizontal) < -0.05f;
            }

            left = left.NormalizedCopy();
            right = right.NormalizedCopy();
            return VectorDot(left, right) < -0.05f;
        }

        private static Vec3 EnforceAwayFromAffectorInvariant(
            Vec3 direction,
            Vec3 capturedImpact,
            Vec3 awayFromAffector,
            ref string source)
        {
            if (!IsUsableVector(direction) || !IsUsableVector(awayFromAffector))
                return direction;

            Vec3 horizontalDirection = direction;
            horizontalDirection.z = 0f;
            if (IsUsableVector(horizontalDirection) &&
                HorizontalDot(horizontalDirection, awayFromAffector) >= 0f)
            {
                return direction;
            }

            float vertical = direction.z;
            Vec3 correctedHorizontal = capturedImpact;
            correctedHorizontal.z = 0f;
            if (!IsUsableVector(correctedHorizontal) ||
                HorizontalDot(correctedHorizontal, awayFromAffector) <= 0f)
            {
                correctedHorizontal = awayFromAffector;
            }
            else
            {
                correctedHorizontal = correctedHorizontal.NormalizedCopy();
            }

            correctedHorizontal.z = vertical;
            if (!IsUsableVector(correctedHorizontal))
                return direction;

            source += "+awayFromAffectorInvariant";
            return correctedHorizontal.NormalizedCopy();
        }

        private static float HorizontalDot(Vec3 left, Vec3 right)
        {
            return left.x * right.x + left.y * right.y;
        }

        private static float VectorDot(Vec3 left, Vec3 right)
        {
            return left.x * right.x + left.y * right.y + left.z * right.z;
        }

        private static Vec3 ResolvePulseDirection'''
replace_regex(
    safe_path,
    r"^        private static Vec3 ResolveDirection\(.*?^        private static Vec3 ResolvePulseDirection",
    new_resolve_direction)

validator_path = Path("Source/Build/ValidateAssemblies.cs")
validator_anchor = '''            MethodDefinition onMissionTick = RequireMethod(behavior, "OnMissionTick");
'''
validator_replacement = '''            MethodDefinition onMissionTick = RequireMethod(behavior, "OnMissionTick");
            MethodDefinition resolveDirection = RequireMethod(behavior, "ResolveDirection");
            Require(CallsMethod(resolveDirection, "IsOpposingDirection"),
                "death direction no longer rejects oppositely signed KillingBlow impulses");
            Require(CallsMethod(resolveDirection, "EnforceAwayFromAffectorInvariant"),
                "death direction no longer enforces the source-away invariant after momentum/lift blending");
            RequireMethod(behavior, "TryGetAwayFromAffectorDirection");
            RequireMethod(behavior, "HorizontalDot");
            RequireMethod(behavior, "VectorDot");
'''
replace_exact(validator_path, validator_anchor, validator_replacement)

strings_anchor = '''            Require(strings.Any(s => s.Contains("FIRST_COMBAT_DEATH_POST_RAGDOLL_WARMUP")),
                "first actual combat death post-ragdoll warmup route is missing");
'''
strings_replacement = '''            Require(strings.Any(s => s.Contains("FIRST_COMBAT_DEATH_POST_RAGDOLL_WARMUP")),
                "first actual combat death post-ragdoll warmup route is missing");
            Require(strings.Any(s => s.Contains("rejectedOpposingKillingBlow")),
                "opposing KillingBlow rejection telemetry is missing");
            Require(strings.Any(s => s.Contains("awayFromAffectorInvariant")),
                "source-away direction invariant telemetry is missing");
'''
replace_exact(validator_path, strings_anchor, strings_replacement)

bridge_path = Path("Source/ClothForceBridge.cs")
replace_exact(
    bridge_path,
    '''        // Harmony prefix for Agent.Die(Blow, KillInfo). v1.2.74 preserves the first-death
        // sentinel, then returns before any ExtremeRagdoll Blow mutation so all deaths use
        // the main controlled post-ragdoll actuator.
''',
    '''        // Harmony prefix for Agent.Die(Blow, KillInfo). The first-death sentinel remains an
        // ownership safeguard only; launch direction is validated by the main post-ragdoll path.
        // No ExtremeRagdoll Blow mutation is performed here.
''')
replace_exact(
    bridge_path,
    '''            // mission behavior. The first-combat-death sentinel above is preserved unchanged; its
            // separate reverse-launch defect is not claimed fixed by this revision.
''',
    '''            // mission behavior. The first-combat-death sentinel above remains an ownership-only
            // safeguard; the main resolver rejects reversed engine impulses for every death.
''')

print("direction fix applied and regression models passed")
