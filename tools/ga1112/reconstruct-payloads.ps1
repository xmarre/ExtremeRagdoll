$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Assert-Chunk([string]$Path,[string]$Expected){
 if(!(Test-Path -LiteralPath $Path)){throw "Missing payload chunk: $Path"}
 $actual=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
 if($actual -ne $Expected){throw "Payload chunk hash mismatch for $Path: $actual"}
}
function Join-Chunks([array]$Specs,[string]$Output,[string]$ExpectedCombined){
 foreach($spec in $Specs){Assert-Chunk $spec.Path $spec.Hash}
 $payload=($Specs|ForEach-Object{([IO.File]::ReadAllText($_.Path)).Trim()})-join''
 [IO.File]::WriteAllText($Output,$payload,[Text.UTF8Encoding]::new($false))
 $actual=(Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant()
 if($actual -ne $ExpectedCombined){throw "Reconstructed payload hash mismatch for $Output: $actual"}
}
$root=(Resolve-Path $PSScriptRoot).Path
$patch=@(
 @{Path=(Join-Path $root 'patchchunks/p00.b64');Hash='a4030fd1977b01fee79e8e140c93e0ed16a75e0850e27a4bc3af76cad55fbc82'},
 @{Path=(Join-Path $root 'patchchunks/p01.b64');Hash='dda604261f99504a29a776f3921211965507cada38c4e21b024da894c4c5a0a4'},
 @{Path=(Join-Path $root 'patchchunks/p02.b64');Hash='64d73a09ba3896b9a2c244fcc791af039dd8577c5fc6cf5b2b458f7b4febacd3'},
 @{Path=(Join-Path $root 'patchchunks/p03.b64');Hash='7f2797d325f93cd3c59be9414f53bb706c703f2ca10288cab3ed1e227fd71116'},
 @{Path=(Join-Path $root 'patchchunks/p04.b64');Hash='3cb04f2339f725744a062d01d7760183107e688630e6bd0e359b4bfb503bbf2e'},
 @{Path=(Join-Path $root 'patchchunks/p05.b64');Hash='f454b2c9a45cba62a6a9731e7789787cae085c2a3b70dc3fe2963f7e3049fed9'},
 @{Path=(Join-Path $root 'patchchunks/p06.b64');Hash='33dc47af4b7285808d7416a932560a4442d24aa87e1d627d036f32bd3c1f04e8'},
 @{Path=(Join-Path $root 'patchchunks/p07.b64');Hash='b4ee8ae5df818222ce1d029946157cbc1d078b6ee57a453e07a4c4da5809f3e8'},
 @{Path=(Join-Path $root 'patchchunks/p08.b64');Hash='c09be6fa5fdd359b9325e005f20b14a63972bb10b2f0c097acb3941359796516'},
 @{Path=(Join-Path $root 'patchchunks/p09.b64');Hash='80e29feead895e228e7fe902056151928705c77f840a272c64edd122f7c564b8'},
 @{Path=(Join-Path $root 'patchchunks/p10.b64');Hash='1866a4fd31ad00dced903d97a3b7420fbb4d3b0118f8bd394c693a8d8fd71736'}
)
$test=@(
 @{Path=(Join-Path $root 'testchunks/p00.b64');Hash='74e45f9e5204333e89b6e0f740c88086087cf61ab9a524e46d3b432e9326b51c'},
 @{Path=(Join-Path $root 'testchunks/p01.b64');Hash='b695832b751b80fcb9afdfcbcad9be4bdf5f4ac196041afb43ee92eedb8c90d2'}
)
Join-Chunks $patch (Join-Path $root 'patch.gz.b64') 'b188351f0f7b3252e337d713bd0c81b2028cfd4949a31fd1cad0b1d6e190f5f9'
Join-Chunks $test (Join-Path $root 'test.gz.b64') '4d0fe5595920ca6f64ab9ac6a535f611a030cff471f9985b462b5fa438923239'
Write-Host 'GA1112_PAYLOAD_RECONSTRUCTION=SUCCESS'
