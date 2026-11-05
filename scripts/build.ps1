param([string]$Version='0.1.0', [switch]$FrameworkDependent)
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$'){throw 'Use a version such as 0.1.0 or 0.1.0-preview.1.'}
$root=Split-Path $PSScriptRoot -Parent
$artifact=Join-Path $root 'artifacts'
$packageName=if($FrameworkDependent){"DALIZO-$Version-win-x64-no-runtime"}else{"DALIZO-$Version-win-x64"}
$publish=Join-Path $artifact ($packageName+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force $publish | Out-Null
Push-Location $root
try {
    dotnet run --project tests/ApiChecks.csproj -c Release
    if($LASTEXITCODE -ne 0){throw 'API checks failed.'}
    $contained=if($FrameworkDependent){'false'}else{'true'}
    dotnet publish src/DALIZO.csproj -c Release -r win-x64 --self-contained $contained -p:Optimize=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -o $publish
    if($LASTEXITCODE -ne 0){throw 'Build failed.'}
    foreach($name in @('README.md','PREREQUISITES.md','THIRD-PARTY.md','RELEASE-NOTES.md','LICENSE','COPYRIGHT')){if(Test-Path $name){Copy-Item $name $publish}}
    Copy-Item licenses $publish -Recurse
    if($FrameworkDependent){'Install the .NET 10 Desktop Runtime for Windows x64 before starting DALIZO.exe. The SDK is not required. Download: https://dotnet.microsoft.com/en-us/download/dotnet/10.0 (Run apps > .NET Desktop Runtime > Windows x64). See PREREQUISITES.md.' | Set-Content (Join-Path $publish 'RUNTIME-REQUIRED.txt')}
    if(!$FrameworkDependent){
        $assets=Get-Content (Join-Path $root 'src/obj/project.assets.json') -Raw | ConvertFrom-Json
        foreach($framework in $assets.project.frameworks.PSObject.Properties){
            foreach($dependency in $framework.Value.downloadDependencies){
                if($dependency.name -notmatch '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$'){continue}
                $runtimeVersion=($dependency.version -replace '[\[\] ]','').Split(',')[0]
                $relative=$dependency.name.ToLowerInvariant()+'/'+$runtimeVersion
                $package=$assets.packageFolders.PSObject.Properties.Name | ForEach-Object {Join-Path $_ $relative} | Where-Object {Test-Path $_} | Select-Object -First 1
                if(!$package){throw "Missing runtime license source: $relative"}
                $notices=Get-ChildItem $package -File | Where-Object {$_.Name -match 'LICENSE|NOTICE'}
                if(!$notices){throw "Runtime notices not found: $relative"}
                $target=Join-Path $publish ('licenses/'+$dependency.name+'/'+$runtimeVersion)
                New-Item -ItemType Directory -Force $target | Out-Null
                $notices | Copy-Item -Destination $target
            }
        }
    }
    $zip=Join-Path $artifact ($packageName+'.zip')
    if(Test-Path $zip){throw "Archive already exists: $zip. Choose another version or move the old archive."}
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
    $hash=(Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    "$hash  $packageName.zip" | Set-Content (Join-Path $artifact ($packageName+'.sha256')) -Encoding ascii
    Write-Output "Package: $zip"
    Write-Output "Build folder: $publish"
} finally {Pop-Location}
