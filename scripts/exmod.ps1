# extools driving itself: the launcher in wrappers/ with this checkout as the tools directory.
[CmdletBinding(PositionalBinding = $false)]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
$here = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
$env:EXTOOLS_HOME = $here
& (Join-Path $here 'wrappers/exmod.ps1') @Arguments
exit $LASTEXITCODE
