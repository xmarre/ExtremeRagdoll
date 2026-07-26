$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Replace-Required([string]$Text,[string]$Old,[string]$New,[string]$Label){
    if(!$Text.Contains($Old)){throw "Unable to inject v1.1.16 ${Label}"}
    return $Text.Replace($Old,$New)
}
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
& (Join-Path $repo 'tools/ga1115/run-complete-build.ps1')
$sourceBuild=Join-Path $repo 'tools/ga1116/build-ga1116-native-shot-data.ps1'
$runtimeBuild=Join-Path $env:RUNNER_TEMP 'build-ga1116-native-shot-data.ps1'
$text=[IO.File]::ReadAllText($sourceBuild).Replace("`r`n","`n")
$old="    if(`$LASTEXITCODE -ne 0){throw 'v1.1.16 direct-spawn hotfix apply failed'}"
$new=@"
$old
    `$bridgePath=Join-Path `$moduleRoot 'Source/MissileDamageBridge.cs'
    `$bridgeSource=[IO.File]::ReadAllText(`$bridgePath).Replace("``r``n","``n")
    `$namespaceNeedle="using TaleWorlds.Core;``nusing TaleWorlds.Library;"
    if(!`$bridgeSource.Contains(`$namespaceNeedle)){throw 'Unable to insert TaleWorlds.Engine namespace'}
    `$bridgeSource=`$bridgeSource.Replace(`$namespaceNeedle,"using TaleWorlds.Core;``nusing TaleWorlds.Engine;``nusing TaleWorlds.Library;")
    Write-Utf8NoBom `$bridgePath `$bridgeSource
"@
$text=Replace-Required $text $old $new 'Engine namespace source correction'
$text=Replace-Required $text 'fb911e8b983caacb6b9fdfa2b19aeae1155828168e16fa9da8704a0fa7c830ed' '33f42ee52b13bc89cdd87f883eeef6daf9c13cfe38fd45e3ad893ccaecb13c40' 'final bridge hash'
$text=Replace-Required $text 'DIRECT_SPAWN_HOTFIX_SHA256=1eb09f5e45305eb7beac5a1a1f9861583777abe9ff20e5368a34a9272fb8564b`nTEST_SHA256=' 'DIRECT_SPAWN_HOTFIX_SHA256=1eb09f5e45305eb7beac5a1a1f9861583777abe9ff20e5368a34a9272fb8564b`nENGINE_NAMESPACE_FIX=INLINE_HASH_VERIFIED`nTEST_SHA256=' 'Engine namespace verification metadata'
[IO.File]::WriteAllText($runtimeBuild,$text,[Text.UTF8Encoding]::new($false))
& $runtimeBuild -BaseOutputRoot "$env:RUNNER_TEMP\ga1115-output" -OutputRoot "$env:RUNNER_TEMP\ga1116-output"
Write-Host 'GA1116_COMPLETE_BUILD=SUCCESS'
