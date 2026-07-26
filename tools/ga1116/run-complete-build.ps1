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
$old="`$hotfix=Join-Path `$workspace 'GuidedArrow-v1.1.16-Direct-Resolved-Spawn.hotfix.patch'"
$new="$old`n`$engineHotfix=Join-Path `$workspace 'GuidedArrow-v1.1.16-Engine-Namespace.hotfix.patch'"
$text=Replace-Required $text $old $new 'Engine hotfix path'
$old="Decode-GzipBase64 (Join-Path `$payload 'direct-spawn-hotfix.gz.b64') `$hotfix '1c5fab2c47f1807d0467ae2a8e5df712b72689eab804b6665d73f7a4b3f0249b' '72ae959a135686e03255de7fff9d8eee508d6697f11bd17b12d233c4917195c8' '1eb09f5e45305eb7beac5a1a1f9861583777abe9ff20e5368a34a9272fb8564b' 'v1.1.16 direct-spawn hotfix'"
$new="$old`nDecode-GzipBase64 (Join-Path `$payload 'engine-namespace-hotfix.gz.b64') `$engineHotfix '7165cba9a3593bbe4d721ab98a2f2b7e5d510467eace690ae19adb9eb3b125d0' '002956a6ee90755c30cc8ffb66051a80aae1b0546c5bd1c672c2990c65663cd8' '105048dc6aa2630fbe3694506e8306db71ad78d91bb08b40c4af5a0bf22fac16' 'v1.1.16 Engine namespace hotfix'"
$text=Replace-Required $text $old $new 'Engine hotfix decoder'
$old="    if(`$LASTEXITCODE -ne 0){throw 'v1.1.16 direct-spawn hotfix apply failed'}"
$new="$old`n    & git apply --check --whitespace=error-all `$engineHotfix`n    if(`$LASTEXITCODE -ne 0){throw 'v1.1.16 Engine namespace hotfix check failed'}`n    & git apply --whitespace=error-all `$engineHotfix`n    if(`$LASTEXITCODE -ne 0){throw 'v1.1.16 Engine namespace hotfix apply failed'}"
$text=Replace-Required $text $old $new 'Engine hotfix application'
$text=Replace-Required $text 'fb911e8b983caacb6b9fdfa2b19aeae1155828168e16fa9da8704a0fa7c830ed' '33f42ee52b13bc89cdd87f883eeef6daf9c13cfe38fd45e3ad893ccaecb13c40' 'final bridge hash'
$old="Copy-Item `$hotfix (Join-Path `$runtime 'GuidedArrow-v1.1.16-Direct-Resolved-Spawn.hotfix.patch')"
$new="$old`nCopy-Item `$engineHotfix (Join-Path `$runtime 'GuidedArrow-v1.1.16-Engine-Namespace.hotfix.patch')"
$text=Replace-Required $text $old $new 'Engine hotfix artifact copy'
$text=Replace-Required $text 'DIRECT_SPAWN_HOTFIX_SHA256=1eb09f5e45305eb7beac5a1a1f9861583777abe9ff20e5368a34a9272fb8564b`nTEST_SHA256=' 'DIRECT_SPAWN_HOTFIX_SHA256=1eb09f5e45305eb7beac5a1a1f9861583777abe9ff20e5368a34a9272fb8564b`nENGINE_NAMESPACE_HOTFIX_SHA256=105048dc6aa2630fbe3694506e8306db71ad78d91bb08b40c4af5a0bf22fac16`nTEST_SHA256=' 'Engine hotfix verification metadata'
[IO.File]::WriteAllText($runtimeBuild,$text,[Text.UTF8Encoding]::new($false))
& $runtimeBuild -BaseOutputRoot "$env:RUNNER_TEMP\ga1115-output" -OutputRoot "$env:RUNNER_TEMP\ga1116-output"
Write-Host 'GA1116_COMPLETE_BUILD=SUCCESS'
