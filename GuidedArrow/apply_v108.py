from pathlib import Path

ROOT = Path(__file__).resolve().parent
behavior = ROOT / 'Source' / 'GuidedArrowBehavior.cs'
settings = ROOT / 'Source' / 'Settings.cs'
project = ROOT / 'Source' / 'GuidedArrow.csproj'
submodule = ROOT / 'SubModule.xml'

s = behavior.read_text(encoding='utf-8')
old_types = '''        private sealed class RemovedNativeMeshSet
        {
            public GameEntity Entity;
            public readonly List<MetaMesh> Meshes = new List<MetaMesh>();
        }

        private sealed class TrackedMissile
        {
            public Mission.Missile Missile;
            public int Index;
            public GameEntity NativeEntity;
            public MetaMesh FullDetailMesh;
            public readonly List<RemovedNativeMeshSet> RemovedNativeMeshes = new List<RemovedNativeMeshSet>();
        }
'''
new_types = '''        private sealed class NativeVisibilityState
        {
            public GameEntity Entity;
            public bool WasVisible;
        }

        private sealed class TrackedMissile
        {
            public Mission.Missile Missile;
            public int Index;
            public GameEntity NativeEntity;
            public GameEntity VisualSourceEntity;
            public GameEntity VisualEntity;
            public MetaMesh FullDetailMesh;
            public readonly List<NativeVisibilityState> HiddenNativeEntities = new List<NativeVisibilityState>();
        }
'''
if old_types not in s:
    raise SystemExit('v1.0.7 tracked-missile type block not found')
s = s.replace(old_types, new_types, 1)

start_marker = '        private void ApplyFullDetailVisual(TrackedMissile tracked)\n'
end_marker = '        private void UpdateImpactPendingCamera'
start = s.find(start_marker)
end = s.find(end_marker, start)
if start < 0 or end < 0:
    raise SystemExit('v1.0.7 visual method block not found')
new_visual_block = '''        private void ApplyFullDetailVisual(TrackedMissile tracked)
        {
            if (tracked == null || !(Settings.Instance?.UseFullDetailProjectileModels ?? true) || Mission?.Scene == null)
                return;

            try
            {
                GameEntity nativeEntity = tracked.Missile?.Entity;
                ItemObject item = tracked.Missile?.Weapon.Item;
                string meshName = item?.MultiMeshName;
                if (nativeEntity == null || string.IsNullOrEmpty(meshName))
                    return;

                GameEntity sourceEntity = FindFirstNativeMeshEntity(nativeEntity);
                if (sourceEntity == null)
                {
                    Log("Full-detail visual skipped because no native missile mesh entity was found for #" + tracked.Index + ".");
                    return;
                }

                MetaMesh full = MetaMesh.GetCopy(meshName, false, true);
                if (full == null || !full.IsValid)
                    return;

                GameEntity visualEntity = GameEntity.CreateEmpty(Mission.Scene, true);
                if (visualEntity == null)
                    return;

                visualEntity.AddMultiMesh(full, true);
                tracked.NativeEntity = nativeEntity;
                tracked.VisualSourceEntity = sourceEntity;
                tracked.VisualEntity = visualEntity;
                tracked.FullDetailMesh = full;

                UpdateFullDetailVisual(tracked);
                CaptureAndHideNativeMeshEntities(nativeEntity, tracked.HiddenNativeEntities);

                Log("Safely overlaid full-detail mesh '" + meshName + "' and hid " +
                    tracked.HiddenNativeEntities.Count + " native render entity/entities for #" + tracked.Index + ".");
            }
            catch (Exception ex)
            {
                Log("Full-detail projectile presentation unavailable: " + ex.GetType().Name);
                RemoveFullDetailVisual(tracked);
            }
        }

        private static GameEntity FindFirstNativeMeshEntity(GameEntity entity)
        {
            if (entity == null)
                return null;

            try
            {
                if (entity.MultiMeshComponentCount > 0)
                    return entity;
            }
            catch { }

            int childCount = 0;
            try { childCount = entity.ChildCount; }
            catch { childCount = 0; }

            for (int i = 0; i < childCount; i++)
            {
                try
                {
                    GameEntity found = FindFirstNativeMeshEntity(entity.GetChild(i));
                    if (found != null)
                        return found;
                }
                catch { }
            }

            return null;
        }

        private static void CaptureAndHideNativeMeshEntities(GameEntity entity, List<NativeVisibilityState> states)
        {
            if (entity == null || states == null)
                return;

            bool hasMesh = false;
            try { hasMesh = entity.MultiMeshComponentCount > 0; }
            catch { hasMesh = false; }

            if (hasMesh)
            {
                bool wasVisible = true;
                try { wasVisible = entity.GetVisibilityExcludeParents(); }
                catch { }

                states.Add(new NativeVisibilityState
                {
                    Entity = entity,
                    WasVisible = wasVisible
                });

                try { entity.SetVisibilityExcludeParents(false); }
                catch { }
            }

            int childCount = 0;
            try { childCount = entity.ChildCount; }
            catch { childCount = 0; }

            for (int i = 0; i < childCount; i++)
            {
                try { CaptureAndHideNativeMeshEntities(entity.GetChild(i), states); }
                catch { }
            }
        }

        private void UpdateFullDetailVisuals()
        {
            for (int i = _trackedMissiles.Count - 1; i >= 0; i--)
                UpdateFullDetailVisual(_trackedMissiles[i]);
        }

        private void UpdateFullDetailVisual(TrackedMissile tracked)
        {
            if (tracked?.VisualEntity == null || tracked.VisualSourceEntity == null)
                return;

            try
            {
                // Copy the actual native render entity frame. This includes Bannerlord's own
                // missile-axis/local-rotation correction and therefore aligns the detailed model
                // exactly as the native simplified projectile was aligned.
                MatrixFrame nativeRenderFrame = tracked.VisualSourceEntity.GetGlobalFrame();
                if (!IsFinite(nativeRenderFrame.origin))
                    return;
                tracked.VisualEntity.SetGlobalFrame(nativeRenderFrame);
            }
            catch
            {
                try
                {
                    if (tracked.NativeEntity != null)
                        tracked.VisualEntity.SetGlobalFrame(tracked.NativeEntity.GetGlobalFrame());
                }
                catch { }
            }
        }

        private void RemoveFullDetailVisual(TrackedMissile tracked)
        {
            if (tracked == null)
                return;

            // Never add/remove/transfer components on Bannerlord's native missile entity. The
            // collision pipeline still owns that entity and may attach it to an agent bone during
            // Mission.MissileHitCallback. Structural mutation here can cause an engine AV.
            for (int i = 0; i < tracked.HiddenNativeEntities.Count; i++)
            {
                NativeVisibilityState state = tracked.HiddenNativeEntities[i];
                if (state?.Entity == null)
                    continue;
                try { state.Entity.SetVisibilityExcludeParents(state.WasVisible); }
                catch { }
            }
            tracked.HiddenNativeEntities.Clear();

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
            tracked.VisualSourceEntity = null;
            tracked.NativeEntity = null;
        }

'''
s = s[:start] + new_visual_block + s[end:]
behavior.write_text(s, encoding='utf-8', newline='\n')

p = project.read_text(encoding='utf-8')
p = p.replace('<Version>1.0.7</Version>', '<Version>1.0.8</Version>')
p = p.replace('<FileVersion>1.0.7.0</FileVersion>', '<FileVersion>1.0.8.0</FileVersion>')
p = p.replace('<AssemblyVersion>1.0.7.0</AssemblyVersion>', '<AssemblyVersion>1.0.8.0</AssemblyVersion>')
project.write_text(p, encoding='utf-8', newline='\n')

sm = submodule.read_text(encoding='utf-8').replace('v1.0.7', 'v1.0.8')
submodule.write_text(sm, encoding='utf-8', newline='\n')

st = settings.read_text(encoding='utf-8')
st = st.replace(
    "Replaces Bannerlord's simplified flying mesh with one correctly aligned full-detail arrow/bolt model for the currently guided swarm only.",
    "Safely hides Bannerlord's simplified flying render entities and overlays one full-detail arrow/bolt model aligned from the native missile render frame. No native missile components are removed or transferred."
)
settings.write_text(st, encoding='utf-8', newline='\n')
