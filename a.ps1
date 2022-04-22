# Get-ChildItem -Path .\ -Recurse -ErrorAction SilentlyContinue -Force
type msbuild.log
if (((Get-Content .\msbuild.log) -match 'Building target "_CopyOutOfDateSourceItemsToOutputDirectory"').Length -gt 0)
{
  exit 1
}