from pathlib import Path

root = Path('GuidedArrow')
path = root / 'Source' / 'GuidedArrowBehavior.cs'
text = path.read_text(encoding='utf-8-sig').replace('\r\n', '\n')


def replace_once(old: str, new: str, label: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected one match, found {count}')
    text = text.replace(old, new, 1)

replace_once(
    'using TaleWorlds.MountAndBlade.View.MissionViews;\n',
    'using TaleWorlds.MountAndBlade.View.MissionViews;\nusing TaleWorlds.MountAndBlade.View.Screens;\nusing TaleWorlds.ScreenSystem;\n',
    'screen usings',
)
replace_once(
    '        private bool _releaseCameraAfterOverride;\n',
    '''        private bool _releaseCameraAfterOverride;\n        private bool _releaseCustomCameraNextDisplay;\n        private Camera _ownedCustomCamera;\n        private Camera _previousCustomCamera;\n        private bool _ownsCustomCamera;\n        private bool _cameraOwnershipFailureLogged;\n''',
    'camera fields',
)
replace_once(
    '''            ResetTrackedVictim();\n            RemoveOwnTimeRequest();\n\n            _missile = null;\n''',
    '''            ResetTrackedVictim();\n            RemoveOwnTimeRequest();\n            ReleaseCustomCameraOwnership("NewShotSuperseded");\n\n            _missile = null;\n''',
    'new shot camera release',
)
replace_once(
    '''            _stoppedMissileElapsed = 0f;\n            _releaseCameraAfterOverride = false;\n            _desiredDirection = NormalizeSafe(velocity, shooterAgent.LookDirection);\n''',
    '''            _stoppedMissileElapsed = 0f;\n            _releaseCameraAfterOverride = false;\n            _releaseCustomCameraNextDisplay = false;\n            _desiredDirection = NormalizeSafe(velocity, shooterAgent.LookDirection);\n''',
    'new shot release flag',
)
old_submit = '''                if (MissionScreen?.CombatCamera != null)\n                    MissionScreen.CombatCamera.Frame = frame;\n                Mission?.SetCameraFrame(ref frame, 1f);\n'''
new_submit = '''                MissionScreen screen = ResolveMissionScreen();\n                if (screen != null)\n                {\n                    EnsureCustomCameraOwnership(screen);\n                    if (_ownedCustomCamera != null)\n                        _ownedCustomCamera.Frame = frame;\n                    if (screen.CombatCamera != null)\n                        screen.CombatCamera.Frame = frame;\n                }\n                Mission?.SetCameraFrame(ref frame, 1f);\n'''
replace_once(old_submit, new_submit, 'override frame submission')
replace_once(
    '''                _cameraFrameValid = false;\n                _releaseCameraAfterOverride = false;\n                return base.UpdateOverridenCamera(dt);\n''',
    '''                _cameraFrameValid = false;\n                _releaseCameraAfterOverride = false;\n                ReleaseCustomCameraOwnership("CameraOverrideFailure");\n                return base.UpdateOverridenCamera(dt);\n''',
    'override failure release',
)
replace_once(
    '''            if (_releaseCameraAfterOverride)\n            {\n                _releaseCameraAfterOverride = false;\n                _state = State.Idle;\n                _cameraFrameValid = false;\n                Log("Return flight complete.");\n            }\n''',
    '''            if (_releaseCameraAfterOverride)\n            {\n                _releaseCameraAfterOverride = false;\n                _releaseCustomCameraNextDisplay = true;\n            }\n''',
    'deferred custom camera release',
)
replace_once(
    '''            if (Mission == null || dt < 0f)\n                return;\n\n            switch (_state)\n''',
    '''            if (Mission == null || dt < 0f)\n                return;\n\n            // Keep the final return frame owned for one complete render interval, then restore\n            // exactly the camera that was active before guidance.\n            if (_releaseCustomCameraNextDisplay)\n            {\n                _releaseCustomCameraNextDisplay = false;\n                ReleaseCustomCameraOwnership("ReturnComplete");\n                _releaseCameraAfterOverride = false;\n                _state = State.Idle;\n                _cameraFrameValid = false;\n                Log("Return flight complete.");\n                return;\n            }\n\n            switch (_state)\n''',
    'display release block',
)
replace_once(
    '''            _state = State.Guiding;\n            _cameraFrameValid = false;\n            SetOwnTimeSpeed(Settings.Instance?.InitialGuidanceTimeSpeed ?? 0.15f);\n''',
    '''            _state = State.Guiding;\n            _cameraFrameValid = false;\n            AcquireCustomCameraOwnership();\n            SetOwnTimeSpeed(Settings.Instance?.InitialGuidanceTimeSpeed ?? 0.15f);\n''',
    'camera acquire',
)
replace_once(
    '                float yaw = Mission.InputManager.GetMouseMoveX() * 0.0026f * sensitivity;\n',
    '                float yaw = -Mission.InputManager.GetMouseMoveX() * 0.0026f * sensitivity;\n',
    'horizontal input sign',
)
replace_once(
    '                _releaseCameraAfterOverride = true;\n',
    '                _releaseCustomCameraNextDisplay = true;\n',
    'return completion flag',
)
replace_once(
    '            try { frame = Mission.GetCameraFrame(); }\n',
    '''            try\n            {\n                MissionScreen screen = ResolveMissionScreen();\n                frame = screen?.CombatCamera != null ? screen.CombatCamera.Frame : Mission.GetCameraFrame();\n            }\n''',
    'return pose source',
)
old_set = '''                if (MissionScreen?.CombatCamera != null)\n                    MissionScreen.CombatCamera.Frame = frame;\n                Mission.SetCameraFrame(ref frame, 1f);\n'''
new_set = '''                MissionScreen screen = ResolveMissionScreen();\n                if (screen != null)\n                {\n                    EnsureCustomCameraOwnership(screen);\n                    if (_ownedCustomCamera != null)\n                        _ownedCustomCamera.Frame = frame;\n                    if (screen.CombatCamera != null)\n                        screen.CombatCamera.Frame = frame;\n                }\n                Mission.SetCameraFrame(ref frame, 1f);\n'''
replace_once(old_set, new_set, 'display frame submission')
replace_once(
    '        private void SetOwnTimeSpeed(float requested)\n',
    '''        private MissionScreen ResolveMissionScreen()\n        {\n            try\n            {\n                if (MissionScreen != null)\n                    return MissionScreen;\n                return ScreenManager.TopScreen as MissionScreen;\n            }\n            catch\n            {\n                return null;\n            }\n        }\n\n        private void AcquireCustomCameraOwnership()\n        {\n            MissionScreen screen = ResolveMissionScreen();\n            if (screen == null)\n            {\n                if (!_cameraOwnershipFailureLogged)\n                {\n                    _cameraOwnershipFailureLogged = true;\n                    Log("Camera ownership unavailable: active MissionScreen not found.");\n                }\n                return;\n            }\n\n            EnsureCustomCameraOwnership(screen);\n        }\n\n        private void EnsureCustomCameraOwnership(MissionScreen screen)\n        {\n            if (screen == null)\n                return;\n\n            if (!_ownsCustomCamera)\n            {\n                _previousCustomCamera = screen.CustomCamera;\n                if (_ownedCustomCamera == null)\n                    _ownedCustomCamera = Camera.CreateCamera();\n\n                Camera source = _previousCustomCamera ?? screen.CombatCamera;\n                if (source != null)\n                    _ownedCustomCamera.FillParametersFrom(source);\n\n                _ownsCustomCamera = true;\n                _cameraOwnershipFailureLogged = false;\n                Log("Camera ownership acquired through MissionScreen.CustomCamera.");\n            }\n\n            if (!ReferenceEquals(screen.CustomCamera, _ownedCustomCamera))\n                screen.CustomCamera = _ownedCustomCamera;\n        }\n\n        private void ReleaseCustomCameraOwnership(string reason)\n        {\n            if (!_ownsCustomCamera)\n                return;\n\n            try\n            {\n                MissionScreen screen = ResolveMissionScreen();\n                if (screen != null && ReferenceEquals(screen.CustomCamera, _ownedCustomCamera))\n                    screen.CustomCamera = _previousCustomCamera;\n            }\n            catch { }\n\n            _ownsCustomCamera = false;\n            _previousCustomCamera = null;\n            Log("Camera ownership released: " + reason + ".");\n        }\n\n        private void SetOwnTimeSpeed(float requested)\n''',
    'custom camera helpers',
)
replace_once(
    '''            _cameraFrameValid = false;\n            _releaseCameraAfterOverride = false;\n            _pendingAcquireElapsed = 0f;\n''',
    '''            _cameraFrameValid = false;\n            _releaseCameraAfterOverride = false;\n            _releaseCustomCameraNextDisplay = false;\n            ReleaseCustomCameraOwnership(behaviorRemoving ? "BehaviorRemoved" : "Reset");\n            _pendingAcquireElapsed = 0f;\n''',
    'reset camera release',
)
path.write_text(text, encoding='utf-8', newline='\n')

csproj = root / 'Source' / 'GuidedArrow.csproj'
proj = csproj.read_text(encoding='utf-8-sig').replace('\r\n', '\n')
for old, new in [('1.0.1.0', '1.0.2.0'), ('<Version>1.0.1</Version>', '<Version>1.0.2</Version>')]:
    if old not in proj:
        raise RuntimeError(f'csproj token missing: {old}')
    proj = proj.replace(old, new)
csproj.write_text(proj, encoding='utf-8', newline='\n')

submodule = root / 'SubModule.xml'
xml = submodule.read_text(encoding='utf-8-sig').replace('\r\n', '\n')
if '<Version value="v1.0.1" />' not in xml:
    raise RuntimeError('SubModule version token missing')
submodule.write_text(xml.replace('<Version value="v1.0.1" />', '<Version value="v1.0.2" />'), encoding='utf-8', newline='\n')

(root / 'CHANGELOG.txt').write_text('''Guided Arrow v1.0.2\n\n- Replaced the ineffective late MissionView camera-frame path with direct MissionScreen.CustomCamera ownership.\n- Resolves the active MissionScreen through ScreenManager when the inherited MissionView reference was never registered.\n- Preserves and restores any camera that was active before guidance.\n- Keeps the final return frame for one full render interval before releasing camera ownership.\n- Corrected horizontal mouse guidance while retaining corrected vertical guidance.\n- Added camera ownership acquisition/release diagnostics.\n''', encoding='utf-8', newline='\n')
(root / 'BUILD_REPORT.txt').write_text('Guided Arrow v1.0.2 build pending compiler validation.\n', encoding='utf-8', newline='\n')
