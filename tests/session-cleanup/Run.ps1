param([string]$ScratchRoot = 'D:\Deepseek\work\.tmp\stage129\host-tests')
$ErrorActionPreference = 'Stop'
$scratch = [IO.Path]::GetFullPath($ScratchRoot)
$allowed = 'D:\Deepseek\work\.tmp\stage129\'
if (-not $scratch.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or
    $scratch.StartsWith(($allowed + 'product-source'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Host test outputs must stay in a separate stage129 scratch tree.'
}
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$artifacts = Join-Path $scratch 'artifacts'
# The overlay omits the binary icon. Borrow the existing source snapshot's icon
# read-only and substitute it only in this scratch build, never in product files.
$icon = Join-Path $scratch 'brand.ico'
Copy-Item -LiteralPath 'D:\Deepseek\work\.tmp\stage129\product-source\host\Assets\我控制.ico' -Destination $icon -Force
$targets = Join-Path $scratch 'HostTestResources.targets'
$override = @'
<Project>
  <Target Name="UseHostTestResources" BeforeTargets="PrepareForBuild" Condition="'$(MSBuildProjectName)' == 'StreamHub.Service'">
    <ItemGroup>
      <EmbeddedResource Remove="../host/Assets/我控制.ico" />
      <EmbeddedResource Include="__ICON__" LogicalName="StreamHub.BrandIcon.ico" />
    </ItemGroup>
  </Target>
</Project>
'@
[IO.File]::WriteAllText($targets, $override.Replace('__ICON__', [Security.SecurityElement]::Escape($icon)), [Text.UTF8Encoding]::new($false))
& dotnet build (Join-Path $PSScriptRoot 'SessionCleanupChecks.csproj') -c Release --artifacts-path $artifacts "-p:CustomAfterMicrosoftCommonTargets=$targets" --nologo 2>&1 |
    Tee-Object -FilePath (Join-Path $scratch 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Host service and focused test Release build failed.' }
& dotnet (Join-Path $artifacts 'bin\SessionCleanupChecks\release\SessionCleanupChecks.dll') (Join-Path $scratch 'fixtures') 2>&1 |
    Tee-Object -FilePath (Join-Path $scratch 'run.log')
if ($LASTEXITCODE -ne 0) { throw 'Focused session cleanup checks failed.' }
