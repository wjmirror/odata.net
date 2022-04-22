# Get-ChildItem -Path .\ -Recurse -ErrorAction SilentlyContinue -Force
type msbuild.log
if (((Get-Content .\msbuild.log) -match 'Building target "_CopyOutOfDateSourceItemsToOutputDirectory"').Length -gt 0)
{
  Write-Host "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!fail"
  exit 1
}

Write-Host "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!success"