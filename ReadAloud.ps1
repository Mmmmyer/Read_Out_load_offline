# Read Aloud launcher - starts the tray app (source in ReadAloud.cs).
# Launched by the "Read Aloud" shortcut; runs through Windows PowerShell so Smart App Control allows it.

$created = $false
$mutex = New-Object System.Threading.Mutex($true, "Local\ReadAloudHotkey", [ref]$created)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
if (-not $created) {
    [System.Windows.Forms.MessageBox]::Show("Read Aloud is already running.`nLook for the speaker icon in the system tray.", "Read Aloud") | Out-Null
    exit
}

Add-Type -Path (Join-Path $PSScriptRoot "ReadAloud.cs") -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Core, Microsoft.CSharp

[ReadAloudApp]::StartupCommand = "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -STA -File `"$PSCommandPath`""
[ReadAloudApp]::LogFile = Join-Path $PSScriptRoot "ReadAloud.log"

[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::Run((New-Object ReadAloudApp))
$mutex.ReleaseMutex()
