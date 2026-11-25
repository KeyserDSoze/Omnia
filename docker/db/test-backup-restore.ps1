param([switch] $KeepContainer)
# Real backup/restore rehearsal on a disposable PostgreSQL: the production backup.sh and restore.sh,
# roles from bootstrap.sh, RLS, pgvector and the Keycloak database owner. Run: docker/db/test-backup-restore.ps1
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$name = 'tao-backup-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
function New-Secret { -join ([Security.Cryptography.RandomNumberGenerator]::GetBytes(16) | ForEach-Object { $_.ToString('x2') }) }
$admin = New-Secret; $app = New-Secret; $ret = New-Secret; $kc = New-Secret
$env = @('-e', "PGPASSWORD=$admin", '-e', 'PGHOST=127.0.0.1', '-e', 'PGUSER=tao', '-e', 'PGDATABASE=tao')

function Sh([string[]] $Extra, [string] $Command) {
  $out = & docker exec @env @Extra $name bash -c $Command 2>&1
  [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out | Out-String) }
}
function Sql([string] $Db, [string] $Query, [string] $User = 'tao', [string] $Password = $admin) {
  $out = & docker exec -e "PGPASSWORD=$Password" $name psql -h 127.0.0.1 -U $User -d $Db -Atq -v ON_ERROR_STOP=1 -c $Query 2>&1
  if ($LASTEXITCODE -ne 0) { throw "SQL failed: $Query`n$out" }
  ($out | Out-String).Trim()
}
function Expect($Actual, $Expected, [string] $What) {
  if ("$Actual" -ne "$Expected") { throw "FAIL $What : expected '$Expected', got '$Actual'" }
  Write-Host "ok  $What"
}
function Expect-Failure($Result, [string] $Pattern, [string] $What) {
  if ($Result.Code -eq 0) { throw "FAIL $What : the command succeeded" }
  if ($Result.Text -notmatch $Pattern) { throw "FAIL $What : message did not match '$Pattern':`n$($Result.Text)" }
  Write-Host "ok  $What"
}

try {
  docker run -d --rm --name $name -e POSTGRES_USER=tao -e POSTGRES_DB=tao -e "POSTGRES_PASSWORD=$admin" pgvector/pgvector:pg17 | Out-Null
  for ($i = 0; $i -lt 60; $i++) {
    docker exec $name pg_isready -h 127.0.0.1 -U tao -d tao 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { break }; Start-Sleep -Seconds 1
  }
  Start-Sleep -Seconds 2
  foreach ($file in 'bootstrap.sh', 'backup.sh', 'restore.sh') { docker cp (Join-Path $dir $file) "${name}:/tmp/$file" }
  docker exec $name mkdir -p /backups | Out-Null

  $boot = Sh @('-e', "APP_DB_PASSWORD=$app", '-e', "RETENTION_DB_PASSWORD=$ret", '-e', "KEYCLOAK_DB_PASSWORD=$kc") 'bash /tmp/bootstrap.sh'
  Expect $boot.Code 0 'bootstrap creates roles and the keycloak database'

  Sql tao 'CREATE EXTENSION IF NOT EXISTS vector' | Out-Null
  Sql tao @"
CREATE TABLE memory_items (id int PRIMARY KEY, owner text NOT NULL, body bytea NOT NULL, embedding vector(3));
GRANT SELECT, INSERT ON memory_items TO tao_app;
ALTER TABLE memory_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE memory_items FORCE ROW LEVEL SECURITY;
CREATE POLICY actor ON memory_items USING (owner = current_setting('tao.actor', true));
INSERT INTO memory_items VALUES (1, 'alice', '\xdeadbeef', '[1,2,3]'), (2, 'bob', '\xcafebabe', '[3,2,1]');
"@ | Out-Null
  Sql keycloak 'CREATE TABLE user_entity (id int PRIMARY KEY, email text)' 'keycloak' $kc | Out-Null
  Sql keycloak "INSERT INTO user_entity VALUES (1, 'ada@example.test')" 'keycloak' $kc | Out-Null

  $first = Sh @() 'TAO_BACKUP_KEEP=2 bash /tmp/backup.sh'
  Expect $first.Code 0 'backup succeeds'
  $listing = (Sh @() 'ls /backups/tao-*/').Text
  foreach ($f in 'tao.dump', 'keycloak.dump', 'SHA256SUMS') { if ($listing -notmatch [regex]::Escape($f)) { throw "FAIL backup misses $f`n$listing" } }
  Write-Host 'ok  backup holds both dumps and their checksums'
  $backup = ((Sh @() 'ls /backups').Text -split '\s+' | Where-Object { $_ -like 'tao-*' } | Select-Object -First 1)

  # Retention: keep the newest N, never the partial ones.
  Start-Sleep -Seconds 2; (Sh @() 'TAO_BACKUP_KEEP=2 bash /tmp/backup.sh').Code | Out-Null
  Start-Sleep -Seconds 2; (Sh @() 'TAO_BACKUP_KEEP=2 bash /tmp/backup.sh').Code | Out-Null
  Expect ((Sh @() 'ls -d /backups/tao-*/ | wc -l').Text.Trim()) 2 'only the newest two backups are kept'
  $backup = ((Sh @() 'ls /backups').Text -split '\s+' | Where-Object { $_ -like 'tao-*' } | Sort-Object | Select-Object -Last 1)

  # Damage the data after the backup.
  Sql tao 'TRUNCATE memory_items' | Out-Null
  Sql keycloak 'DROP TABLE user_entity' 'keycloak' $kc | Out-Null

  Expect-Failure (Sh @() "bash /tmp/restore.sh $backup") '--yes' 'restore refuses without --yes'
  Expect-Failure (Sh @() 'bash /tmp/restore.sh ../etc --yes') 'Usage|invalid' 'restore refuses a path outside /backups'

  # An open session on the database blocks the restore.
  docker exec -d -e "PGPASSWORD=$admin" $name psql -h 127.0.0.1 -U tao -d tao -c 'select pg_sleep(60)'
  Start-Sleep -Seconds 2
  Expect-Failure (Sh @() "bash /tmp/restore.sh $backup --yes") 'stop' 'restore refuses while the API holds a connection'
  Sql postgres "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'tao' AND pid <> pg_backend_pid()" | Out-Null

  # A corrupted dump is detected before anything is dropped.
  docker exec $name bash -c "echo corrupt >> /backups/$backup/tao.dump"
  Expect-Failure (Sh @() "bash /tmp/restore.sh $backup --yes") 'checksum|FAILED' 'restore refuses a corrupted dump'
  Expect (Sql tao 'SELECT count(*) FROM memory_items') 0 'a refused restore leaves the database untouched'
  docker exec $name bash -c "cd /backups/$backup && head -c -8 tao.dump > t && mv t tao.dump" | Out-Null

  $restore = Sh @() "bash /tmp/restore.sh $backup --yes"
  Expect $restore.Code 0 'restore succeeds'

  Expect (Sql tao 'SELECT count(*) FROM memory_items') 2 'tao rows are back'
  Expect (Sql tao "SELECT encode(body, 'hex') FROM memory_items WHERE id = 1") 'deadbeef' 'sealed payload bytes are identical'
  Expect (Sql tao 'SELECT embedding FROM memory_items WHERE id = 2') '[3,2,1]' 'pgvector data is back'
  Expect (Sql keycloak 'SELECT email FROM user_entity' 'keycloak' $kc) 'ada@example.test' 'keycloak data is back, readable by the keycloak role'
  Expect (Sql tao "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE relname = 'memory_items'") 'tao' 'tao tables stay owned by tao'
  Expect (Sql keycloak "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE relname = 'user_entity'") 'keycloak' 'keycloak tables are owned by keycloak'
  Expect (Sql tao "SELECT relrowsecurity::text || relforcerowsecurity::text FROM pg_class WHERE relname = 'memory_items'") 'truetrue' 'FORCE row level security survives'
  $actor = & docker exec -e "PGPASSWORD=$app" -e 'PGOPTIONS=-c tao.actor=alice' $name psql -h 127.0.0.1 -U tao_app -d tao -Atq -c 'SELECT count(*) FROM memory_items'
  Expect (($actor | Out-String).Trim()) 1 'the API role still sees only its own rows'
  Write-Host 'Backup and restore rehearsal passed.'
}
finally {
  if (-not $KeepContainer) { docker rm -f $name 2>$null | Out-Null }
}
