$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK is required to build the WiX project.' }
$out = Join-Path $root 'artifacts/installer'
New-Item -ItemType Directory -Force -Path $out | Out-Null
dotnet publish (Join-Path $root 'src/StorageChronicle.Agent/StorageChronicle.Agent.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'agent')
dotnet publish (Join-Path $root 'src/StorageChronicle.SessionAgent/StorageChronicle.SessionAgent.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'session-agent')
dotnet publish (Join-Path $root 'src/StorageChronicle.UI.Desktop/StorageChronicle.UI.Desktop.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'ui')
$agentSource = Join-Path $out 'agent/StorageChronicle.Agent.exe'
$sessionAgentSource = Join-Path $out 'session-agent/StorageChronicle.SessionAgent.exe'
$uiSource = Join-Path $out 'ui/StorageChronicle.UI.Desktop.exe'
dotnet build (Join-Path $root 'installer/StorageChronicle.wixproj') -c Release -p:Platform=x64 "-p:AgentSource=$agentSource" "-p:SessionAgentSource=$sessionAgentSource" "-p:UiSource=$uiSource"
exit $LASTEXITCODE
