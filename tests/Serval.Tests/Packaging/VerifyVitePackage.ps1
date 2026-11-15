param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '../../../src/Serval.Vite/Serval.Vite.csproj')
)

$ErrorActionPreference = 'Stop'
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('serval-vite-package-' + [Guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $workspace 'artifacts'
$consumer = Join-Path $workspace 'consumer with spaces'
$cache = Join-Path $workspace 'packages'
New-Item -ItemType Directory -Path $consumer -Force | Out-Null

function Invoke-Dotnet([string[]]$Arguments, [bool]$ExpectSuccess = $true)
{
    $lines = & dotnet @Arguments 2>&1
    $code = $LASTEXITCODE
    $output = $lines -join [Environment]::NewLine
    if (($ExpectSuccess -and $code -ne 0) -or (!$ExpectSuccess -and $code -eq 0))
    {
        throw "Unexpected dotnet exit code $code.$( [Environment]::NewLine )$output"
    }

    return $output
}

# A successful pack is not enough: a stale bin-path glob can silently leave the analyzer out.
$stamp = 'VitePackageVerification-' + [Guid]::NewGuid().ToString('N')
Invoke-Dotnet @('pack', $ProjectPath, '-c', 'Release', '--artifacts-path', $artifacts, '--disable-build-servers', '-p:UseSharedCompilation=false', '-p:GeneratePackageOnBuild=false', '-nodeReuse:false', "-p:InformationalVersion=$stamp") | Out-Null
$packages = @(Get-ChildItem -LiteralPath $artifacts -Recurse -Filter 'Serval.Vite.*.nupkg')
$generators = @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'bin') -Recurse -Filter 'Serval.Vite.Generators.dll')
if ($packages.Count -ne 1 -or $generators.Count -ne 1)
{
    throw 'Expected one Vite package and one generator build output.'
}

$package = $packages[0]
$builtGenerator = $generators[0]
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try
{
    foreach ($name in @('lib/net11.0/Serval.Vite.xml', 'build/Serval.Vite.targets', 'analyzers/dotnet/cs/Serval.Vite.Generators.dll', 'analyzers/dotnet/cs/System.Text.Json.dll'))
    {
        if ($null -eq $archive.GetEntry($name))
        {
            throw "Missing package file: $name"
        }
    }

    if (@($archive.Entries | Where-Object { $_.FullName -like '*Microsoft.CodeAnalysis*' }).Count -ne 0)
    {
        throw 'The package includes Roslyn host assemblies or satellite resources.'
    }

    $generatorStream = $archive.GetEntry('analyzers/dotnet/cs/Serval.Vite.Generators.dll').Open()
    try
    {
        $packedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($generatorStream))
    }
    finally
    {
        $generatorStream.Dispose()
    }

    if ($packedHash -ne (Get-FileHash -LiteralPath $builtGenerator.FullName -Algorithm SHA256).Hash)
    {
        throw 'The package contains a stale generator instead of the custom-output build.'
    }

    $reader = [IO.StreamReader]::new($archive.GetEntry('Serval.Vite.nuspec').Open())
    try
    {
        $nuspec = [xml]$reader.ReadToEnd()
    }
    finally
    {
        $reader.Dispose()
    }

    if ($nuspec.SelectNodes("//*[local-name()='dependency']").Count -ne 0)
    {
        throw 'Generator dependencies leaked into the runtime dependency list.'
    }

    $version = $nuspec.package.metadata.version
}
finally
{
    $archive.Dispose()
}

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RootNamespace>PackageConsumer</RootNamespace>
    <ViteManifest>manifest.json</ViteManifest>
    <ViteBaseUrl>/build</ViteBaseUrl>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Serval.Vite" Version="$version" />
  </ItemGroup>
  <Target Name="UnexpectedFrontendBuild">
    <Error Text="Development must not run production frontend build dependencies." />
  </Target>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj')
@'
using PackageConsumer.Generated;
Console.WriteLine(Assets.GetUrl("src/main.js"));
Console.WriteLine(Assets.GetEntry("src/main.js").Stylesheets.Count);
'@ | Set-Content -LiteralPath (Join-Path $consumer 'Program.cs')
$manifest = '{"src/main.js":{"file":"assets/main-first.js","isEntry":true},"style.css":{"file":"assets/separate.css","isEntry":true}}'
$manifestPath = Join-Path $consumer 'manifest.json'
$manifest | Set-Content -LiteralPath $manifestPath
$project = Join-Path $consumer 'Consumer.csproj'
$assembly = Join-Path $consumer 'bin/Release/net11.0/Consumer.dll'

# The isolated cache ensures this test cannot accidentally use an older package with the same version.
Invoke-Dotnet @('restore', $project, '--source', $package.DirectoryName, '--packages', $cache, '--disable-build-servers') | Out-Null
Invoke-Dotnet @('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false') | Out-Null
$first = Invoke-Dotnet @($assembly)
if ($first.Trim() -ne ('/build/assets/main-first.js' + [Environment]::NewLine + '0'))
{
    throw "Unexpected generated lookups: $first"
}

$manifest.Replace('main-first.js', 'main-second.js') | Set-Content -LiteralPath $manifestPath
Invoke-Dotnet @('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false') | Out-Null
$second = Invoke-Dotnet @($assembly)
if ($second -notmatch '/build/assets/main-second.js' -or $second -match 'main-first')
{
    throw "The rebuilt consumer retained an old manifest: $second"
}

Invoke-Dotnet @('publish', $project, '-c', 'Release', '--no-build', '--no-restore', '--disable-build-servers', '-p:ViteBuildCommand=exit 99') | Out-Null
if (Test-Path -LiteralPath (Join-Path $consumer 'bin/Release/net11.0/System.Text.Json.dll'))
{
    throw 'The generator JSON library was copied into the application runtime output.'
}

'{"main":{"file":"main.js","imports":["missing"]}}' | Set-Content -LiteralPath $manifestPath
$invalid = Invoke-Dotnet @('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false') $false
if ($invalid -notmatch 'VITE002')
{
    throw "Expected VITE002 for the dangling import: $invalid"
}

$missing = Invoke-Dotnet @('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false', '-p:ViteManifest=missing.json') $false
if ($missing -notmatch 'VITE001')
{
    throw "Expected VITE001 for the missing manifest: $missing"
}

@'
using PackageConsumer.Generated;
Console.WriteLine(Assets.IsDevelopment);
Console.WriteLine(Assets.GetUrl("src/main.js"));
Console.WriteLine(string.Join(",", Assets.GetEntry("src/main.js").Scripts));
'@ | Set-Content -LiteralPath (Join-Path $consumer 'Program.cs')
Invoke-Dotnet @('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false',
'-p:ViteDevelopment=true', '-p:ViteDevServerUrl=/_vite/', '-p:ViteDevEntries=src/main.js', '-p:ViteManifest=missing.json',
'-p:ViteBuildCommand=exit 99', '-p:ViteBuildDependsOn=UnexpectedFrontendBuild') | Out-Null
$development = Invoke-Dotnet @($assembly)
if ($development.Trim() -ne (@('True', '/_vite/src/main.js', '/_vite/@vite/client,/_vite/src/main.js?import') -join [Environment]::NewLine))
{
    throw "Unexpected development lookups: $development"
}

$manifest | Set-Content -LiteralPath $manifestPath
Invoke-Dotnet @('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false') | Out-Null
$production = Invoke-Dotnet @($assembly)
if ($production.Trim() -ne (@('False', '/build/assets/main-first.js', '/build/assets/main-first.js') -join [Environment]::NewLine))
{
    throw "Development settings leaked into a production build: $production"
}

Write-Output "Vite package checks passed: analyzer packaging, manifest lookups, rebuilds, diagnostics, manifest-free development, and switching back to production. Workspace: $workspace"

