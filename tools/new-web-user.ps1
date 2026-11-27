param(
  [string]$UsersFile = "$env:ProgramData\MafLibrary\users.json",
  [string]$Username = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Username)) { $Username = Read-Host 'Username' }
if ($Username -notmatch '^[A-Za-z0-9._-]{2,64}$') { throw 'Username must be 2-64 characters: letters, digits, dot, underscore or hyphen.' }
$password = Read-Host "Password for $Username (minimum 14 characters)" -AsSecureString
$confirm = Read-Host 'Confirm password' -AsSecureString
$b1=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($password);$b2=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirm)
try { $plain=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($b1);$plain2=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($b2) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b1);[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b2) }
if ($plain.Length -lt 14 -or $plain -cne $plain2) { throw 'Passwords must match and contain at least 14 characters.' }
$salt=New-Object byte[] 16
$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($salt) } finally { $rng.Dispose() }
$iterations=310000
$kdf=[Security.Cryptography.Rfc2898DeriveBytes]::new($plain,$salt,$iterations,[Security.Cryptography.HashAlgorithmName]::SHA256)
try { $hash=$kdf.GetBytes(32) } finally { $kdf.Dispose() }
$passwordHash='pbkdf2-sha256$'+$iterations+'$'+[Convert]::ToBase64String($salt)+'$'+[Convert]::ToBase64String($hash)
$entry=[ordered]@{username=$Username;passwordHash=$passwordHash}
$dir=Split-Path -Parent $UsersFile;New-Item -ItemType Directory -Force -Path $dir|Out-Null
$users=@();if(Test-Path -LiteralPath $UsersFile){$users=@(Get-Content -LiteralPath $UsersFile -Raw|ConvertFrom-Json)}
$users=@($users|Where-Object{$_.username -ine $Username})+@([pscustomobject]$entry)
$json=ConvertTo-Json -InputObject @($users) -Depth 4;[IO.File]::WriteAllText($UsersFile,$json,[Text.UTF8Encoding]::new($false))
try { $acl=Get-Acl -LiteralPath $UsersFile;$acl.SetAccessRuleProtection($true,$false);$identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name;$rule=[Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','Allow');$acl.SetAccessRule($rule);Set-Acl -LiteralPath $UsersFile -AclObject $acl } catch { Write-Warning 'Could not restrict users-file ACL automatically. Restrict it to the server service account and administrators.' }
Write-Output "User '$Username' saved to $UsersFile (PBKDF2-SHA256, $iterations iterations). Password is not stored in plain text. Restart MafLibrary to apply account changes."
