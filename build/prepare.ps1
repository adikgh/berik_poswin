# Орнатқышқа керек бәрін dist\ папкасына жинайды: PHP, MariaDB, касса сайты (berik_pos), синхрон агенті, программа.
# GitHub Actions (windows) ішінде іске қосылады, қолмен де болады: powershell -File build\prepare.ps1 -Server https://...
param(
  [string]$Server = "",
  [string]$PhpMinor = "8.2",          # 8.3+ Windows 7/8-де жүрмейді
  [string]$MariaVer = "10.4.34",      # Windows 7-мен үйлесімді нұсқа
  [string]$SiteRepo = "https://github.com/adikgh/berik_pos.git",
  [string]$SiteRef = "main"
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$tmp = Join-Path $root "tmp"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist, $tmp | Out-Null

function Get-File($url, $out) { if (!(Test-Path $out)) { Write-Host "download $url"; Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing } }

# --- PHP (NTS x64) ---
$rel = Invoke-RestMethod "https://windows.php.net/downloads/releases/releases.json"
$php = $rel.$PhpMinor
$zipName = ($php.PSObject.Properties | Where-Object { $_.Name -like "nts-vs*-x64" } | Select-Object -First 1).Value.zip.path
$phpZip = Join-Path $tmp $zipName
try { Get-File "https://windows.php.net/downloads/releases/$zipName" $phpZip } catch { Get-File "https://windows.php.net/downloads/releases/archives/$zipName" $phpZip }
Expand-Archive $phpZip (Join-Path $dist "php")
Get-File "https://curl.se/ca/cacert.pem" (Join-Path $dist "php\cacert.pem")
Remove-Item (Join-Path $dist "php\*.pdb"), (Join-Path $dist "php\dev"), (Join-Path $dist "php\extras") -Recurse -Force -ErrorAction SilentlyContinue

# --- MariaDB (x64 zip) ---
$mz = Join-Path $tmp "mariadb-$MariaVer-winx64.zip"
Get-File "https://archive.mariadb.org/mariadb-$MariaVer/winx64-packages/mariadb-$MariaVer-winx64.zip" $mz
Expand-Archive $mz $tmp -Force
Move-Item (Join-Path $tmp "mariadb-$MariaVer-winx64") (Join-Path $dist "mariadb")
foreach ($d in "mysql-test", "sql-bench", "include", "lib\*.lib", "data") { Remove-Item (Join-Path $dist "mariadb\$d") -Recurse -Force -ErrorAction SilentlyContinue }
Get-ChildItem (Join-Path $dist "mariadb") -Recurse -Include *.pdb | Remove-Item -Force

# --- Касса сайты ---
$site = Join-Path $dist "www\site"
git clone --depth 1 --branch $SiteRef $SiteRepo $site
Remove-Item (Join-Path $site ".git") -Recurse -Force
Remove-Item (Join-Path $site "config\db.php") -Force -ErrorAction SilentlyContinue
# Офлайнда кэшбэк/сыйлық өшіріледі (клиент балансы офлайнда дұрыс шықпайды)
$loy = Join-Path $site "config\loyalty.php"
if (Test-Path $loy) {
  $c = [IO.File]::ReadAllText($loy)
  $c = $c -replace "public static function on\(\) \{", "public static function on() {`n`t`t`tif (getenv('BERIK_OFFLINE')) return false;"
  [IO.File]::WriteAllText($loy, $c, (New-Object Text.UTF8Encoding $false))
}
# Офлайнда компания бірінші кіргенде таңдалған (базада біреу ғана), сондықтан ?c= сілтемесі керек емес:
# кіру беті компанияны жергілікті базадан өзі алады
$idx = Join-Path $site "index.php"
if (Test-Path $idx) {
  $c = [IO.File]::ReadAllText($idx)
  $c = $c.Replace('if (@$_GET[''c''] || @$_COOKIE[''c''] || @$_SESSION[''c'']) {', 'if (getenv(''BERIK_OFFLINE'')) { $_GET[''c''] = @mysqli_fetch_assoc(db::query("select id from company order by id limit 1"))[''id'']; }' + "`n   " + 'if (@$_GET[''c''] || @$_COOKIE[''c''] || @$_SESSION[''c'']) {')
  [IO.File]::WriteAllText($idx, $c, (New-Object Text.UTF8Encoding $false))
}
# Google Fonts (Nunito) интернетсіз де көрінсін: шрифтті ішіне саламыз
try {
  $ua = @{ "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/109 Safari/537.36" }
  $css = (Invoke-WebRequest "https://fonts.googleapis.com/css2?family=Nunito:wght@200;300;400;600;700;800;900&display=swap" -Headers $ua -UseBasicParsing).Content
  $fdir = Join-Path $site "assets\fonts\nunito"; New-Item -ItemType Directory -Force -Path $fdir | Out-Null
  $i = 0
  foreach ($m in [regex]::Matches($css, "url\((https://[^)]+)\)")) { $i++; $n = "n$i.woff2"; Get-File $m.Groups[1].Value (Join-Path $fdir $n); $css = $css.Replace($m.Groups[1].Value, "/assets/fonts/nunito/$n") }
  [IO.File]::WriteAllText((Join-Path $fdir "nunito.css"), $css)
  $head = Join-Path $site "block\head.php"
  $h = [IO.File]::ReadAllText($head)
  $h = $h -replace 'https://fonts\.googleapis\.com/css2\?family=Nunito[^"]*', '/assets/fonts/nunito/nunito.css'
  [IO.File]::WriteAllText($head, $h, (New-Object Text.UTF8Encoding $false))
} catch { Write-Warning "Nunito: $_" }

# --- Синхрон агенті ---
Copy-Item (Join-Path $root "www\local") (Join-Path $dist "www\local") -Recurse

# --- Программа ---
dotnet publish (Join-Path $root "src\BerikPOS\BerikPOS.csproj") -c Release -o (Join-Path $dist "bin")
Copy-Item (Join-Path $dist "bin\*") $dist -Recurse
Remove-Item (Join-Path $dist "bin") -Recurse -Force
if ($Server) { Set-Content -Path (Join-Path $dist "server.txt") -Value $Server -NoNewline }

# --- WebView2 (Windows 10/11-де бар; жоқ болса орнатқыш өзі орнатады) ---
Get-File "https://go.microsoft.com/fwlink/p/?LinkId=2124703" (Join-Path $root "installer\MicrosoftEdgeWebview2Setup.exe")

Write-Host "dist ready:" (Get-ChildItem $dist -Recurse | Measure-Object -Property Length -Sum).Sum
