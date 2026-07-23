from pathlib import Path
import hashlib

root = Path('GuidedArrow')
source = root / 'Source' / 'GuidedArrowBehavior.cs'
text = source.read_text(encoding='utf-8').replace('\r\n', '\n')

expected_v103 = 'e7c2f2171c5ac30ac3f4dc8b0d46e175db21318c7f58d0b49fed3724b5a51ab6'
actual_v103 = hashlib.sha256(text.encode('utf-8')).hexdigest()
if actual_v103 != expected_v103:
    raise SystemExit(f'Unexpected v1.0.3 controller hash: {actual_v103}')

def replace_once(old: str, new: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'Expected one match, found {count}: {old[:80]!r}')
    text = text.replace(old, new, 1)

replace_once(
"        private Vec3 _desiredDirection;\n        private Vec3 _lastMissileDirection;\n",
"        // Used only to identify the just-fired native projectile. Steering never seeks this\n"
"        // heading after acquisition; mouse input is applied directly to the live velocity.\n"
"        private Vec3 _shotDirection;\n"
"        private Vec3 _lastMissileDirection;\n"
"        private float _pendingYawInput;\n"
"        private float _pendingPitchInput;\n")

replace_once(
"            _desiredDirection = NormalizeSafe(velocity, shooterAgent.LookDirection);\n"
"            _lastMissileDirection = _desiredDirection;\n",
"            _shotDirection = NormalizeSafe(velocity, shooterAgent.LookDirection);\n"
"            _lastMissileDirection = _shotDirection;\n"
"            _pendingYawInput = 0f;\n"
"            _pendingPitchInput = 0f;\n")

replace_once(
"                float speed = (float)Math.Sqrt(speedSq);\n"
"                Vec3 currentDirection = velocity / speed;\n"
"                float radius = Clamp(Settings.Instance?.MinimumTurnRadius ?? 24f, 3f, 120f);\n"
"                float maxAngle = (speed / radius) * dt;\n"
"                if (maxAngle > 1.2f)\n"
"                    maxAngle = 1.2f;\n\n"
"                Vec3 steered = RotateTowards(currentDirection, _desiredDirection, maxAngle);\n"
"                Vec3 newVelocity = steered * speed;\n"
"                _missile.SetVelocity(in newVelocity);\n"
"                _lastMissileDirection = steered;\n",
"                // Consume only the mouse movement received since the previous physics tick.\n"
"                // There is deliberately no persistent target heading: releasing the mouse leaves\n"
"                // the native projectile velocity completely untouched, including gravity/drop.\n"
"                float yaw = _pendingYawInput;\n"
"                float pitch = _pendingPitchInput;\n"
"                _pendingYawInput = 0f;\n"
"                _pendingPitchInput = 0f;\n\n"
"                if (Math.Abs(yaw) <= Tiny && Math.Abs(pitch) <= Tiny)\n"
"                    return;\n\n"
"                float speed = (float)Math.Sqrt(speedSq);\n"
"                Vec3 currentDirection = velocity / speed;\n"
"                float radius = Clamp(Settings.Instance?.MinimumTurnRadius ?? 24f, 3f, 120f);\n"
"                float maxAngle = (speed / radius) * dt;\n"
"                if (maxAngle > 1.2f)\n"
"                    maxAngle = 1.2f;\n"
"                if (maxAngle <= Tiny)\n"
"                    return;\n\n"
"                float requestedAngle = (float)Math.Sqrt(yaw * yaw + pitch * pitch);\n"
"                if (requestedAngle > maxAngle)\n"
"                {\n"
"                    float scale = maxAngle / requestedAngle;\n"
"                    yaw *= scale;\n"
"                    pitch *= scale;\n"
"                }\n\n"
"                Vec3 steered = ApplyDirectSteering(currentDirection, yaw, pitch);\n"
"                Vec3 newVelocity = steered * speed; // Direction only; never adds propulsion.\n"
"                _missile.SetVelocity(in newVelocity);\n"
"                _lastMissileDirection = steered;\n")

replace_once(
"                    Vec3 candidateDir = NormalizeSafe(velocity, _desiredDirection);\n"
"                    Vec3 shotDir = NormalizeSafe(_pendingShotVelocity, _desiredDirection);\n",
"                    Vec3 candidateDir = NormalizeSafe(velocity, _shotDirection);\n"
"                    Vec3 shotDir = NormalizeSafe(_pendingShotVelocity, _shotDirection);\n")

replace_once(
"            Vec3 velocityNow = best.GetVelocity();\n"
"            _desiredDirection = NormalizeSafe(velocityNow, _desiredDirection);\n"
"            _lastMissileDirection = _desiredDirection;\n",
"            Vec3 velocityNow = best.GetVelocity();\n"
"            _lastMissileDirection = NormalizeSafe(velocityNow, _shotDirection);\n"
"            _pendingYawInput = 0f;\n"
"            _pendingPitchInput = 0f;\n")

replace_once(
"                float sensitivity = Clamp(Settings.Instance?.MouseSensitivity ?? 1f, 0.10f, 4f);\n"
"                float yaw = Mission.InputManager.GetMouseMoveX() * 0.0026f * sensitivity;\n"
"                float pitch = -Mission.InputManager.GetMouseMoveY() * 0.0026f * sensitivity;\n"
"                ApplyDesiredDirectionInput(yaw, pitch);\n",
"                float sensitivity = Clamp(Settings.Instance?.MouseSensitivity ?? 1f, 0.10f, 4f);\n"
"                // Rodrigues positive rotation around local-up turns +Y toward -X, so mouse-right\n"
"                // must use negative yaw to produce a rightward (+X) projectile turn.\n"
"                float yaw = -Mission.InputManager.GetMouseMoveX() * 0.0026f * sensitivity;\n"
"                float pitch = -Mission.InputManager.GetMouseMoveY() * 0.0026f * sensitivity;\n"
"                QueueDirectSteeringInput(yaw, pitch);\n")

replace_once(
"        private void ApplyDesiredDirectionInput(float yaw, float pitch)\n"
"        {\n"
"            if (Math.Abs(yaw) > Tiny)\n"
"                _desiredDirection = NormalizeSafe(RotateAroundAxis(_desiredDirection, WorldUp, yaw), _desiredDirection);\n\n"
"            if (Math.Abs(pitch) > Tiny)\n"
"            {\n"
"                Vec3 right = NormalizeSafe(Cross(_desiredDirection, WorldUp), new Vec3(1f, 0f, 0f));\n"
"                Vec3 pitched = NormalizeSafe(RotateAroundAxis(_desiredDirection, right, pitch), _desiredDirection);\n"
"                // Prevent singular near-vertical steering where yaw becomes unstable.\n"
"                if (Math.Abs(pitched.z) < 0.985f)\n"
"                    _desiredDirection = pitched;\n"
"            }\n"
"        }\n",
"        private void QueueDirectSteeringInput(float yaw, float pitch)\n"
"        {\n"
"            if (IsFinite(yaw))\n"
"                _pendingYawInput += yaw;\n"
"            if (IsFinite(pitch))\n"
"                _pendingPitchInput += pitch;\n"
"        }\n\n"
"        private static Vec3 ApplyDirectSteering(Vec3 currentDirection, float yaw, float pitch)\n"
"        {\n"
"            Vec3 steered = NormalizeSafe(currentDirection, new Vec3(0f, 1f, 0f));\n\n"
"            // Build controls from the projectile's live flight basis, never from the camera frame.\n"
"            // This keeps mouse-left/right screen-intuitive while remaining fully camera-independent.\n"
"            Vec3 right = Cross(steered, WorldUp);\n"
"            if (!IsFinite(right) || right.LengthSquared <= 0.0001f)\n"
"                right = Cross(steered, new Vec3(0f, 1f, 0f));\n"
"            right = NormalizeSafe(right, new Vec3(1f, 0f, 0f));\n"
"            Vec3 localUp = NormalizeSafe(Cross(right, steered), WorldUp);\n\n"
"            if (Math.Abs(yaw) > Tiny)\n"
"                steered = NormalizeSafe(RotateAroundAxis(steered, localUp, yaw), steered);\n\n"
"            if (Math.Abs(pitch) > Tiny)\n"
"            {\n"
"                right = NormalizeSafe(Cross(steered, localUp), right);\n"
"                Vec3 pitched = NormalizeSafe(RotateAroundAxis(steered, right, pitch), steered);\n"
"                // Avoid the yaw singularity at exactly vertical flight while preserving direct input.\n"
"                if (Math.Abs(pitched.z) < 0.985f)\n"
"                    steered = pitched;\n"
"            }\n\n"
"            return steered;\n"
"        }\n")

replace_once(
"            _stoppedMissileElapsed = 0f;\n"
"            _cinematicElapsed = 0f;\n",
"            _stoppedMissileElapsed = 0f;\n"
"            _pendingYawInput = 0f;\n"
"            _pendingPitchInput = 0f;\n"
"            _cinematicElapsed = 0f;\n")

source.write_text(text, encoding='utf-8', newline='\n')
expected_v104 = 'eb707d35146d7a68cdb81d14aa6781c905f50ed6f5474eca0a667e51f1762dda'
actual_v104 = hashlib.sha256(source.read_bytes()).hexdigest()
if actual_v104 != expected_v104:
    raise SystemExit(f'Unexpected v1.0.4 controller hash: {actual_v104}')

csproj = root / 'Source' / 'GuidedArrow.csproj'
project = csproj.read_text(encoding='utf-8')
project = project.replace('<Version>1.0.3</Version>', '<Version>1.0.4</Version>')
project = project.replace('<FileVersion>1.0.3.0</FileVersion>', '<FileVersion>1.0.4.0</FileVersion>')
project = project.replace('<AssemblyVersion>1.0.3.0</AssemblyVersion>', '<AssemblyVersion>1.0.4.0</AssemblyVersion>')
csproj.write_text(project, encoding='utf-8', newline='\n')

settings = root / 'Source' / 'Settings.cs'
settings_text = settings.read_text(encoding='utf-8')
settings_text = settings_text.replace(
    'HintText = "Mouse steering sensitivity while guiding the projectile."',
    'HintText = "Direct mouse steering sensitivity. Input bends the current velocity only; there is no target-heading auto-centering or forward stabilization."')
settings.write_text(settings_text, encoding='utf-8', newline='\n')

submodule = root / 'SubModule.xml'
module_text = submodule.read_text(encoding='utf-8').replace('v1.0.3', 'v1.0.4')
submodule.write_text(module_text, encoding='utf-8', newline='\n')

print(f'GuidedArrowBehavior.cs SHA-256: {actual_v104}')
