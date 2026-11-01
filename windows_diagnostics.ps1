# AsterCache read-only environment inventory. DOES NOT change Windows settings.
# Run from Windows PowerShell 5.1+ or PowerShell 7+.
$ErrorActionPreference='Stop'
$os=Get-CimInstance Win32_OperatingSystem
$phys=Get-CimInstance Win32_DiskDrive | Select-Object Index,Model,InterfaceType,MediaType,Size
$volumes=Get-CimInstance Win32_LogicalDisk | Where-Object {$_.DriveType -eq 3} | Select-Object DeviceID,FileSystem,Size,FreeSpace
$result=[ordered]@{
  computer_os=$os.Caption
  os_version=$os.Version
  ram_bytes=([long]$os.TotalVisibleMemorySize)*1024
  physical_drives=@($phys)
  volumes=@($volumes)
  note='Read-only inventory. MediaType and SSD/HDD identification may be missing or inaccurate; corroborate in Disk Management.'
}
$result | ConvertTo-Json -Depth 5
