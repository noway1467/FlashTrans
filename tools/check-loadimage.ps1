$sig = @'
using System;
using System.Runtime.InteropServices;
public static class P {
    [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr LoadImage(IntPtr h, string name, uint type, int cx, int cy, uint flags);
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    public static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint n);
}
'@
Add-Type -TypeDefinition $sig

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\FlashTrans-win-x64-fast\FlashTrans.exe'
$ico = Join-Path $root 'src\FlashTrans\Assets\app.ico'

$h1 = [P]::LoadImage([IntPtr]::Zero, $exe, 1, 16, 16, 0x10)
"LoadImage(exe, LR_LOADFROMFILE) = $h1   err=$([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)"

$h2 = [P]::LoadImage([IntPtr]::Zero, $ico, 1, 16, 16, 0x10)
"LoadImage(ico, LR_LOADFROMFILE) = $h2"

$large = New-Object IntPtr[] 1
$small = New-Object IntPtr[] 1
$n = [P]::ExtractIconEx($exe, 0, $large, $small, 1)
"ExtractIconEx(exe) count=$n large=$($large[0]) small=$($small[0])"
