param(
    [string]$PackageDirectory = '',
    [string]$ExpectedCommit = '',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$packageIds = (Import-PowerShellDataFile (Join-Path $repo 'eng/Packages.psd1')).Packages
$properties = [xml](Get-Content -Raw -LiteralPath (Join-Path $repo 'Directory.Build.props'))
$version = [string]$properties.Project.PropertyGroup.Version
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('serval-packages-' + [guid]::NewGuid().ToString('N'))
$consumer = Join-Path $workspace 'consumer with spaces'
New-Item -ItemType Directory -Path $consumer -Force | Out-Null

function Invoke-Dotnet([string[]]$Arguments)
{
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet $( $Arguments[0] ) failed. Workspace: $workspace"
    }
}

function Read-Entry($Archive, [string]$Name)
{
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry)
    {
        throw "Missing package entry: $Name"
    }
    $reader = [IO.StreamReader]::new($entry.Open())
    try
    {
        return $reader.ReadToEnd()
    }
    finally
    {
        $reader.Dispose()
    }
}

if (!$ExpectedCommit)
{
    $ExpectedCommit = & git -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Could not determine the source commit.'
    }
}

if ($PackageDirectory)
{
    $feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
}
else
{
    $feed = Join-Path $workspace 'feed'
    $artifacts = Join-Path $workspace 'artifacts'
    # A separate output tree catches fixed bin paths and stale bundled analyzers.
    Invoke-Dotnet @('pack', (Join-Path $repo 'Serval.slnx'), '-c', $Configuration, '--artifacts-path', $artifacts,
    '-o', $feed, '--nologo', '--disable-build-servers', '-p:GeneratePackageOnBuild=false',
    '-p:ContinuousIntegrationBuild=true', '-warnaserror')
}

foreach ($extension in @('nupkg', 'snupkg'))
{
    $files = @(Get-ChildItem -LiteralPath $feed -Filter "*.$extension" -File)
    if ($files.Count -ne $packageIds.Count)
    {
        throw "Expected $( $packageIds.Count ) $extension files; found $( $files.Count )."
    }
}

$analyzers = @{
    'Serval' = @('Serval.Generators.dll')
    'Serval.Jobs' = @('Serval.Jobs.Generators.dll')
    'Serval.Savanna' = @('Serval.Savanna.Generators.dll', 'Serval.Savanna.Compiler.dll')
    'Serval.Vite' = @('Serval.Vite.Generators.dll', 'System.Text.Json.dll')
}

foreach ($id in $packageIds)
{
    foreach ($extension in @('nupkg', 'snupkg'))
    {
        $path = Join-Path $feed "$id.$version.$extension"
        $archive = [IO.Compression.ZipFile]::OpenRead($path)
        try
        {
            [xml]$spec = Read-Entry $archive "$id.nuspec"
            $metadata = $spec.package.metadata
            if ($metadata.id -cne $id -or $metadata.version -cne $version)
            {
                throw "Wrong package identity in $path."
            }
            if ($metadata.repository.url -cne 'https://github.com/depthbomb/Serval' -or
                $metadata.repository.type -cne 'git' -or $metadata.repository.commit -cne $ExpectedCommit)
            {
                throw "Wrong repository metadata in $path."
            }

            $duplicates = @($archive.Entries | Group-Object FullName | Where-Object Count -GT 1)
            if ($duplicates.Count -ne 0)
            {
                throw "Duplicate archive entries in $path."
            }

            foreach ($framework in @('net10.0', 'net11.0'))
            {
                $suffixes = if ($extension -eq 'nupkg')
                {
                    @('dll', 'xml')
                }
                else
                {
                    @('pdb')
                }
                foreach ($suffix in $suffixes)
                {
                    if ($null -eq $archive.GetEntry("lib/$framework/$id.$suffix"))
                    {
                        throw "Missing $framework $suffix in $path."
                    }
                }
            }

            if ($extension -eq 'snupkg')
            {
                continue
            }

            if ($metadata.authors -cne 'Caprine Logic' -or $metadata.license.type -cne 'expression' -or
                $metadata.license.InnerText -cne 'MIT' -or $metadata.readme -cne 'README.md' -or
                [string]::IsNullOrWhiteSpace($metadata.description) -or [string]::IsNullOrWhiteSpace($metadata.tags))
            {
                throw "Missing public package metadata in $path."
            }

            $readmePath = Join-Path $repo "src/$id/README.md"
            if (!(Test-Path -LiteralPath $readmePath))
            {
                $readmePath = Join-Path $repo 'README.md'
            }
            if ((Read-Entry $archive 'README.md') -cne [IO.File]::ReadAllText($readmePath) -or
                (Read-Entry $archive 'LICENSE') -cne [IO.File]::ReadAllText((Join-Path $repo 'LICENSE')))
            {
                throw "Outdated README or license in $path."
            }

            foreach ($analyzer in $analyzers[$id])
            {
                $entry = $archive.GetEntry("analyzers/dotnet/cs/$analyzer")
                if ($null -eq $entry)
                {
                    throw "Missing analyzer dependency $analyzer in $path."
                }
                if (!$PackageDirectory -and $analyzer.StartsWith('Serval.'))
                {
                    $built = @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'bin') -Recurse -Filter $analyzer |
                        Where-Object { $_.Directory.Parent.Name -eq [IO.Path]::GetFileNameWithoutExtension($analyzer) })
                    if ($built.Count -ne 1)
                    {
                        throw "Expected one build output for $analyzer."
                    }
                    $stream = $entry.Open()
                    try
                    {
                        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
                    }
                    finally
                    {
                        $stream.Dispose()
                    }
                    if ($hash -cne (Get-FileHash -LiteralPath $built[0].FullName -Algorithm SHA256).Hash)
                    {
                        throw "Stale analyzer $analyzer in $path."
                    }
                }
            }

            if ($id -in @('Serval.Savanna', 'Serval.Vite') -and $null -eq $archive.GetEntry("build/$id.targets"))
            {
                throw "Missing build integration in $path."
            }
            if (@($archive.Entries | Where-Object FullName -Like '*Microsoft.CodeAnalysis*').Count -ne 0)
            {
                throw "Roslyn host assemblies must not be bundled in $path."
            }

            $groups = @($metadata.dependencies.group)
            if ($groups.Count -ne 2 -or (($groups.targetFramework | Sort-Object) -join ',') -cne 'net10.0,net11.0')
            {
                throw "Missing dependency groups in $path."
            }
            foreach ($group in $groups)
            {
                $dependencies = @($group.dependency | Where-Object { $null -ne $_ })
                foreach ($dependency in $dependencies)
                {
                    if ($dependency.id -like 'Serval*' -and
                        ($dependency.id -notin $packageIds -or $dependency.version -notin @($version, "[$version, )")))
                    {
                        throw "Invalid Serval dependency in $path."
                    }
                    if ($dependency.id -like 'Microsoft.CodeAnalysis*' -or $dependency.id -like '*.Generators')
                    {
                        throw "Compiler dependency leaked into $path."
                    }
                }
                $expectedDependency = if ($id -eq 'Serval.OAuth')
                {
                    'Serval.Authentication'
                }
                else
                {
                    'Serval'
                }
                if ($id -notin @('Serval', 'Serval.Configuration', 'Serval.Vite') -and $expectedDependency -notin $dependencies.id)
                {
                    throw "Missing $expectedDependency dependency in $path."
                }
                if ($id -eq 'Serval.Configuration' -and 'Microsoft.Extensions.Hosting' -notin $dependencies.id)
                {
                    throw "Missing host configuration dependency in $path."
                }
                if ($id -eq 'Serval.Vite' -and $dependencies.Count -ne 0)
                {
                    throw 'Vite generator dependencies must not become runtime dependencies.'
                }
            }
        }
        finally
        {
            $archive.Dispose()
        }
    }
}

$references = ($packageIds | ForEach-Object { "    <PackageReference Include=""$_"" Version=""$version""/>" }) -join [Environment]::NewLine
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net10.0;net11.0</TargetFrameworks>
    <LangVersion>15.0</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>
    <RootNamespace>PackageConsumer</RootNamespace>
    <ViteManifest>manifest.json</ViteManifest>
    <ViteBaseUrl>/build</ViteBaseUrl>
  </PropertyGroup>
  <ItemGroup>
$references
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj')

$escapedFeed = [Security.SecurityElement]::Escape($feed)
@"
<configuration>
  <packageSources>
    <clear/>
    <add key="local" value="$escapedFeed"/>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="Serval*"/></packageSource>
    <packageSource key="nuget.org"><package pattern="*"/></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'NuGet.Config')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Consumer.cs') -Destination $consumer
New-Item -ItemType Directory -Path (Join-Path $consumer 'Views') | Out-Null
'<#model type="string"><p>{{ Model }}</p>' | Set-Content -LiteralPath (Join-Path $consumer 'Views/Page.svn')
'{"src/main.js":{"file":"assets/main.js","isEntry":true}}' | Set-Content -LiteralPath (Join-Path $consumer 'manifest.json')

$project = Join-Path $consumer 'Consumer.csproj'
# A private cache and source mapping prevent a published package with the same version from hiding a broken artifact.
Invoke-Dotnet @('restore', $project, '--configfile', (Join-Path $consumer 'NuGet.Config'),
'--packages', (Join-Path $workspace 'packages'), '--disable-build-servers')
Invoke-Dotnet @('build', $project, '-c', $Configuration, '--no-restore', '--disable-build-servers', '-warnaserror')
foreach ($framework in @('net10.0', 'net11.0'))
{
    Invoke-Dotnet @((Join-Path $consumer "bin/$Configuration/$framework/Consumer.dll"), $feed, $ExpectedCommit)
}

Write-Output "Verified $( $packageIds.Count ) packages and symbol packages, including consumers on .NET 10 and 11. Workspace: $workspace"
