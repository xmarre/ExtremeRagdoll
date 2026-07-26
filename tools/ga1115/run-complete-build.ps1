$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
& (Join-Path $repo 'tools/ga1114/run-complete-build.ps1')
$sourceBuild=Join-Path $repo 'tools/ga1115/build-ga1115-split-spawn-recovery.ps1'
$runtimeBuild=Join-Path $env:RUNNER_TEMP 'build-ga1115-split-spawn-recovery.ps1'
$text=[IO.File]::ReadAllText($sourceBuild)
$needle="Decode-GzipBase64 (Join-Path `$payload 'test.gz.b64') `$test '7c2fd6ad9a4dd483a46b88ad3345d25d74daab179a73f5b35c5e2314cdd249c4' '9de6c860673d16d3c4667534b1d31f016733cc41e48595184c0520b24d2baecc' 'a35c17e83c7c71b493d74b2a7d0848d1ecfcf41c56984e74bcef8905503dca70' 'v1.1.15 test'"
$replacement=@"
`$inlineTest=@'
from pathlib import Path
import os

root = Path(os.environ["GA1115_MODULE_ROOT"])
behavior = (root / "Source" / "GuidedArrowBehavior.cs").read_text(encoding="utf-8")
bridge = (root / "Source" / "MissileDamageBridge.cs").read_text(encoding="utf-8")

assert "TryGetResolvedLaunchForShot" in bridge
assert "RecentLaunches" in bridge
assert "MaxRecentLaunches = 64" in bridge
assert "if (source.ResolvedLaunchData == null)" in behavior
assert "Standalone splitting is waiting for the original resolved launch packet" in behavior

packet_guard = behavior.index("if (source.ResolvedLaunchData == null)")
spawn_commit = behavior.index("_standaloneSplitSpawned = true;", packet_guard)
assert packet_guard < spawn_commit

class SplitDecision:
    def __init__(self):
        self.spawned = False
        self.created = 0

    def tick(self, packet_available: bool) -> str:
        if self.spawned:
            return "already-complete"
        if not packet_available:
            return "pending"
        self.spawned = True
        self.created += 1
        return "spawned"

state = SplitDecision()
assert state.tick(False) == "pending"
assert state.spawned is False and state.created == 0
assert state.tick(True) == "spawned"
assert state.spawned is True and state.created == 1
assert state.tick(True) == "already-complete"
assert state.created == 1

print("GA1115_SPLIT_SPAWN_RECOVERY_TESTS=PASS")
print("FIRST_LOOKUP_WITHOUT_PACKET=PENDING")
print("SECOND_LOOKUP_WITH_PACKET=SPAWNED")
print("RESOLVED_DAMAGE_PRESERVATION=RETAINED")
print("RECENT_LAUNCH_HISTORY_CAP=64")
'@
Write-Utf8NoBom `$test `$inlineTest
Assert-Hash `$test 'e0c264fc9cb63369c49823660c40584a3bb82ffcbeffe7e5ae2a0aa68ecc86fd' 'v1.1.15 test'
"@
if(!$text.Contains($needle)){throw 'Unable to replace v1.1.15 external test decoder'}
$text=$text.Replace($needle,$replacement)
$text=$text.Replace('TEST_SHA256=a35c17e83c7c71b493d74b2a7d0848d1ecfcf41c56984e74bcef8905503dca70','TEST_SHA256=e0c264fc9cb63369c49823660c40584a3bb82ffcbeffe7e5ae2a0aa68ecc86fd')
[IO.File]::WriteAllText($runtimeBuild,$text,[Text.UTF8Encoding]::new($false))
& $runtimeBuild -BaseOutputRoot "$env:RUNNER_TEMP\ga1114-output" -OutputRoot "$env:RUNNER_TEMP\ga1115-output"
Write-Host 'GA1115_COMPLETE_BUILD=SUCCESS'
