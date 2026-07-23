from pathlib import Path

root = Path('GuidedArrow')
source = root / 'Source' / 'GuidedArrowBehavior.cs'
text = source.read_text(encoding='utf-8').replace('\r\n', '\n')

base_hash = __import__('hashlib').sha256(text.encode('utf-8')).hexdigest()
if base_hash != '6b0146db647ccb1c1f4b271c3ec2bdaf0ac817fbea558f93d52acc792c012d9c':
    raise SystemExit(f'Unexpected v1.0.5 controller hash: {base_hash}')

def once(old: str, new: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'Expected one match, found {count}: {old[:90]!r}')
    text = text.replace(old, new, 1)

once('using System.Reflection;\n', 'using System.Reflection;\nusing System.Diagnostics;\n')
once('''        private sealed class TrackedMissile
        {
            public Mission.Missile Missile;
            public int Index;
            public GameEntity VisualEntity;
            public MetaMesh FullDetailMesh;
        }
''', '''        private sealed class TrackedMissile
        {
            public Mission.Missile Missile;
            public int Index;
            public GameEntity NativeEntity;
            public bool NativeVisibilitySuppressed;
            public GameEntity VisualEntity;
            public MetaMesh FullDetailMesh;
        }
''')
once('''        private Agent _cinematicVictim;
        private GameEntity _cinematicArrowEntity;
        private Vec3 _impactDirection;
        private Vec3 _impactPosition;
        private float _cinematicElapsed;
''', '''        private Agent _cinematicVictim;
        private GameEntity _cinematicArrowEntity;
        private int _cinematicCollisionBoneIndex = -1;
        private Vec3 _impactDirection;
        private Vec3 _impactPosition;
        private float _cinematicElapsed;
        private long _cinematicLastTimestamp;
''')
once('''            _pendingPitchInput = 0f;
            _cinematicArrowEntity = null;
            CaptureReturnPose(shooterAgent);
''', '''            _pendingPitchInput = 0f;
            _cinematicArrowEntity = null;
            ResetCinematicBoneAnchor();
            CaptureReturnPose(shooterAgent);
''')
once('''            else
                _impactDirection = _lastMissileDirection;

            TrackedMissile impactMissile = FindClosestTrackedMissile(_impactPosition);
''', '''            else
                _impactDirection = _lastMissileDirection;

            _cinematicCollisionBoneIndex = victim != null ? collisionData.CollisionBoneIndex : -1;

            TrackedMissile impactMissile = FindClosestTrackedMissile(_impactPosition);
''')
once('''            Vec3 position;
            Vec3 forward;
            float movingCount;
''', '''            UpdateFullDetailVisuals();

            Vec3 position;
            Vec3 forward;
            float movingCount;
''')

start = text.index('        private void ApplyFullDetailVisual(TrackedMissile tracked)')
end = text.index('        private void UpdateImpactPendingCamera', start)
text = text[:start] + '''        private void ApplyFullDetailVisual(TrackedMissile tracked)
        {
            if (tracked == null || !(Settings.Instance?.UseFullDetailProjectileModels ?? true) || Mission?.Scene == null)
                return;
            try
            {
                GameEntity nativeEntity = tracked.Missile.Entity;
                ItemObject item = tracked.Missile.Weapon.Item;
                string meshName = item?.MultiMeshName;
                if (nativeEntity == null || string.IsNullOrEmpty(meshName))
                    return;

                MetaMesh full = MetaMesh.GetCopy(meshName, false, true);
                if (full == null || !full.IsValid)
                    return;

                GameEntity visualEntity = GameEntity.CreateEmpty(Mission.Scene, true);
                if (visualEntity == null)
                    return;
                visualEntity.AddMultiMesh(full, true);

                tracked.NativeEntity = nativeEntity;
                tracked.VisualEntity = visualEntity;
                tracked.FullDetailMesh = full;
                UpdateFullDetailVisual(tracked);
                nativeEntity.SetVisibilityExcludeParents(false);
                tracked.NativeVisibilitySuppressed = true;
                Log("Replaced simplified missile visual with full-detail mesh '" + meshName + "' for #" + tracked.Index + ".");
            }
            catch (Exception ex)
            {
                Log("Full-detail projectile replacement unavailable: " + ex.GetType().Name);
                RemoveFullDetailVisual(tracked);
            }
        }

        private void UpdateFullDetailVisuals()
        {
            for (int i = _trackedMissiles.Count - 1; i >= 0; i--)
                UpdateFullDetailVisual(_trackedMissiles[i]);
        }

        private void UpdateFullDetailVisual(TrackedMissile tracked)
        {
            if (tracked?.VisualEntity == null || tracked.Missile == null)
                return;
            try
            {
                Vec3 position = tracked.Missile.GetPosition();
                Vec3 velocity = tracked.Missile.GetVelocity();
                if (!IsFinite(position) || !IsFinite(velocity) || velocity.LengthSquared <= Tiny)
                    return;
                tracked.VisualEntity.SetGlobalFrame(MakeProjectileVisualFrame(position, velocity));
            }
            catch { }
        }

        private static MatrixFrame MakeProjectileVisualFrame(Vec3 position, Vec3 velocity)
        {
            Vec3 forward = NormalizeSafe(velocity, new Vec3(0f, 1f, 0f));
            Vec3 side = Cross(forward, WorldUp);
            if (!IsFinite(side) || side.LengthSquared <= Tiny)
                side = Cross(forward, Math.Abs(forward.z) > 0.9f ? new Vec3(0f, 1f, 0f) : WorldUp);
            side = NormalizeSafe(side, new Vec3(1f, 0f, 0f));
            Vec3 up = NormalizeSafe(Cross(side, forward), WorldUp);
            Mat3 rotation = Mat3.Identity;
            rotation.s = side;
            rotation.f = forward;
            rotation.u = up;
            return new MatrixFrame(rotation, position);
        }

        private void RemoveFullDetailVisual(TrackedMissile tracked)
        {
            if (tracked == null)
                return;
            if (tracked.NativeVisibilitySuppressed)
            {
                try
                {
                    if (tracked.NativeEntity != null)
                        tracked.NativeEntity.SetVisibilityExcludeParents(true);
                }
                catch { }
                tracked.NativeVisibilitySuppressed = false;
            }
            try
            {
                if (tracked.VisualEntity != null)
                    tracked.VisualEntity.Remove(0);
            }
            catch
            {
                try
                {
                    if (tracked.VisualEntity != null && tracked.FullDetailMesh != null)
                        tracked.VisualEntity.RemoveMultiMesh(tracked.FullDetailMesh);
                }
                catch { }
            }
            tracked.FullDetailMesh = null;
            tracked.VisualEntity = null;
            tracked.NativeEntity = null;
        }

''' + text[end:]

start = text.index('        private Vec3 GetCinematicFocus(Agent victim)')
end = text.index('        private void TrackHitVictim', start)
text = text[:start] + '''        private Vec3 GetCinematicFocus(Agent victim)
        {
            Vec3 corpseCenter = GetRagdollVisualPosition(victim);
            if (TryGetBoneWorldPosition(victim, _cinematicCollisionBoneIndex, out Vec3 hitBone))
                return Lerp(corpseCenter, hitBone, 0.72f);

            GameEntity arrowEntity = _cinematicArrowEntity;
            if (arrowEntity != null)
            {
                try
                {
                    Vec3 arrowPosition = arrowEntity.GlobalPosition;
                    if (IsFinite(arrowPosition) && (arrowPosition - corpseCenter).LengthSquared <= 25f)
                        return Lerp(corpseCenter, arrowPosition, 0.60f);
                }
                catch { }
            }
            return corpseCenter;
        }

        private void ResetCinematicBoneAnchor()
        {
            _cinematicCollisionBoneIndex = -1;
        }

        private static Vec3 GetRagdollVisualPosition(Agent victim)
        {
            if (victim == null)
                return Vec3.Zero;
            int spineIndex = GetMonsterBoneIndex(victim, "SpineUpperBoneIndex");
            int pelvisIndex = GetMonsterBoneIndex(victim, "PelvisBoneIndex");
            bool hasSpine = TryGetBoneWorldPosition(victim, spineIndex, out Vec3 spine);
            bool hasPelvis = TryGetBoneWorldPosition(victim, pelvisIndex, out Vec3 pelvis);
            if (hasSpine && hasPelvis)
                return Lerp(pelvis, spine, 0.58f);
            if (hasSpine)
                return spine;
            if (hasPelvis)
                return pelvis;
            return GetVisualPosition(victim) + WorldUp * 0.95f;
        }

        private static int GetMonsterBoneIndex(Agent victim, string propertyName)
        {
            try
            {
                object monster = victim?.Monster;
                PropertyInfo property = monster?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                object value = property?.GetValue(monster, null);
                if (value != null)
                    return Convert.ToInt32(value);
            }
            catch { }
            return -1;
        }

        private static bool TryGetBoneWorldPosition(Agent victim, int boneIndex, out Vec3 position)
        {
            position = Vec3.Zero;
            if (victim == null || boneIndex < 0)
                return false;
            try
            {
                MBAgentVisuals visuals = victim.AgentVisuals;
                Skeleton skeleton = visuals?.GetSkeleton();
                if (visuals == null || skeleton == null)
                    return false;
                MethodInfo[] methods = skeleton.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != "GetBoneEntitialFrame" && method.Name != "GetBoneEntitialFrameWithIndex")
                        continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1)
                        continue;
                    object argument = Convert.ChangeType(boneIndex, parameters[0].ParameterType);
                    object result = method.Invoke(skeleton, new[] { argument });
                    if (!(result is MatrixFrame localFrame))
                        continue;
                    MatrixFrame rootFrame = visuals.GetGlobalFrame();
                    position = rootFrame.origin
                        + rootFrame.rotation.s * localFrame.origin.x
                        + rootFrame.rotation.f * localFrame.origin.y
                        + rootFrame.rotation.u * localFrame.origin.z;
                    return IsFinite(position);
                }
            }
            catch { }
            return false;
        }

''' + text[end:]

once('''            _cinematicElapsed = 0f;
            _impactConfirmElapsed = 0f;
''', '''            _cinematicElapsed = 0f;
            _cinematicLastTimestamp = Stopwatch.GetTimestamp();
            _impactConfirmElapsed = 0f;
''')
once('''            _cinematicElapsed += dt;

            Vec3 center;
''', '''            float realDt = GetCinematicRealDelta(dt);
            _cinematicElapsed += realDt;

            Vec3 center;
''')
once('            ApplySmoothedCamera(desiredFrame, dt, 14f, 16f);\n', '            ApplySmoothedCamera(desiredFrame, realDt, 14f, 16f);\n')
once('                TickSettledMode(victim, dt);\n', '                TickSettledMode(victim, realDt);\n')
once('            TickFullFinalizationMode(victim, dt);\n', '            TickFullFinalizationMode(victim, realDt);\n')
once('''        private void TickSettledMode(Agent victim, float dt)
''', '''        private float GetCinematicRealDelta(float fallback)
        {
            long now = Stopwatch.GetTimestamp();
            long previous = _cinematicLastTimestamp;
            _cinematicLastTimestamp = now;
            if (previous <= 0 || now <= previous)
                return Clamp(fallback, 0f, 0.10f);
            double seconds = (double)(now - previous) / Stopwatch.Frequency;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds))
                return Clamp(fallback, 0f, 0.10f);
            return Clamp((float)seconds, 0f, 0.10f);
        }

        private void TickSettledMode(Agent victim, float dt)
''')
once('''            _cinematicVictim = null;
            _cinematicArrowEntity = null;
            RemoveOwnTimeRequest();
''', '''            _cinematicVictim = null;
            _cinematicArrowEntity = null;
            ResetCinematicBoneAnchor();
            RemoveOwnTimeRequest();
''')
once('''            _cinematicArrowEntity = null;
            _state = State.Idle;
''', '''            _cinematicArrowEntity = null;
            ResetCinematicBoneAnchor();
            _state = State.Idle;
''')
once('''            _cinematicElapsed = 0f;
            _returnElapsed = 0f;
''', '''            _cinematicElapsed = 0f;
            _cinematicLastTimestamp = 0;
            _returnElapsed = 0f;
''')

source.write_text(text, encoding='utf-8', newline='\n')
final_hash = __import__('hashlib').sha256(source.read_bytes()).hexdigest()
if final_hash != '1ba687abc03afe518221ae5fbe553608052bdc382621e711290760c5d2e48264':
    raise SystemExit(f'Unexpected v1.0.6 controller hash: {final_hash}')

project = root / 'Source' / 'GuidedArrow.csproj'
p = project.read_text(encoding='utf-8')
p = p.replace('<Version>1.0.5</Version>', '<Version>1.0.6</Version>')
p = p.replace('<FileVersion>1.0.5.0</FileVersion>', '<FileVersion>1.0.6.0</FileVersion>')
p = p.replace('<AssemblyVersion>1.0.5.0</AssemblyVersion>', '<AssemblyVersion>1.0.6.0</AssemblyVersion>')
project.write_text(p, encoding='utf-8', newline='\n')

settings = root / 'Source' / 'Settings.cs'
s = settings.read_text(encoding='utf-8')
s = s.replace("Adds the ammo item's full model over Bannerlord's simplified in-flight arrow/bolt mesh for the currently guided swarm only.", "Replaces Bannerlord's simplified flying mesh with one correctly aligned full-detail arrow/bolt model for the currently guided swarm only.")
settings.write_text(s, encoding='utf-8', newline='\n')

submodule = root / 'SubModule.xml'
submodule.write_text(submodule.read_text(encoding='utf-8').replace('v1.0.5', 'v1.0.6'), encoding='utf-8', newline='\n')
print(f'GuidedArrowBehavior.cs SHA-256: {final_hash}')
