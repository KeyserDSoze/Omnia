param([string[]] $PlaywrightArgs = @())
# Local E2E of the social sign-up: real Tao realm and first-login flow, real API and PostgreSQL, with a
# synthetic OIDC realm standing in for Google and Microsoft. Run from anywhere: .\social-e2e.ps1
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$tag = [Guid]::NewGuid().ToString('N').Substring(0, 8)
function New-Secret { -join ([Security.Cryptography.RandomNumberGenerator]::GetBytes(24) | ForEach-Object { $_.ToString('x2') }) }
$pg = New-Secret; $kc = New-Secret; $app = New-Secret; $ret = New-Secret
$upstreamSecret = New-Secret; $upstreamPassword = New-Secret
$keyring = Join-Path ([IO.Path]::GetTempPath()) "tao-social-keyring-$tag.json"
$api = $null
$kcName = "tao-social-kc-$tag"; $pgName = "tao-social-pg-$tag"
$apiVars = 'Database__ConnectionString', 'Database__MigrationConnectionString', 'Database__RetentionConnectionString',
  'Crypto__KeyRingPath', 'Auth__Issuer', 'Auth__Audience', 'Auth__SocialProviders', 'Embeddings__Provider',
  'Cors__Origins__0', 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS'
$webVars = 'EXPO_PUBLIC_TAO_OIDC_ISSUER', 'EXPO_PUBLIC_TAO_OIDC_CLIENT_ID', 'EXPO_PUBLIC_TAO_API_URL', 'TAO_WEB_BASE_PATH',
  'TAO_SOCIAL_E2E', 'TAO_E2E_KC_URL', 'TAO_E2E_KC_PASSWORD', 'TAO_E2E_PASSWORD'
try {
  $key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
  (@{ activeKeyId = 'e2e-v1'; keys = @{ 'e2e-v1' = $key } } | ConvertTo-Json -Compress) | Set-Content -Encoding utf8NoBOM $keyring

  docker run -d --rm --name $pgName -e POSTGRES_USER=tao -e POSTGRES_DB=tao_test -e "POSTGRES_PASSWORD=$pg" `
    -p 127.0.0.1:15434:5432 pgvector/pgvector:pg17 | Out-Null
  docker run -d --rm --name $kcName -e KC_BOOTSTRAP_ADMIN_USERNAME=ci-admin -e "KC_BOOTSTRAP_ADMIN_PASSWORD=$kc" `
    -e TAO_PUBLIC_URL=http://127.0.0.1:19006 -e TAO_TENANT_ID=local `
    -e "FAKE_SOCIAL_CLIENT_SECRET=$upstreamSecret" -e "FAKE_SOCIAL_PASSWORD=$upstreamPassword" `
    -v "$root/docker/keycloak/tao-realm.json:/opt/keycloak/data/import/tao-realm.json:ro" `
    -v "$root/docker/keycloak/e2e/fake-social-realm.json:/opt/keycloak/data/import/fake-social-realm.json:ro" `
    -p 127.0.0.1:8081:8080 quay.io/keycloak/keycloak:26.4 start-dev --import-realm --hostname=http://localhost:8081 | Out-Null

  $ready = $false
  for ($i = 0; $i -lt 90 -and -not $ready; $i++) {
    docker exec $pgName psql -U tao -d tao_test -h 127.0.0.1 -Atc 'select 1' 2>$null | Out-Null
    $ready = $LASTEXITCODE -eq 0; if (-not $ready) { Start-Sleep -Seconds 2 }
  }
  if (-not $ready) { throw 'PostgreSQL not ready' }
  $roles = "CREATE ROLE tao_app LOGIN PASSWORD '$app' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE; " +
    "CREATE ROLE tao_retention LOGIN PASSWORD '$ret' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;"
  docker exec $pgName psql -v ON_ERROR_STOP=1 -U tao -d tao_test -c $roles | Out-Null

  $ready = $false
  for ($i = 0; $i -lt 120 -and -not $ready; $i++) {
    try {
      Invoke-RestMethod http://localhost:8081/realms/tao/.well-known/openid-configuration | Out-Null
      Invoke-RestMethod http://localhost:8081/realms/fake-social/.well-known/openid-configuration | Out-Null
      $ready = $true
    } catch { Start-Sleep -Seconds 2 }
  }
  if (-not $ready) { throw 'Keycloak not ready' }

  # The production script, unchanged, with synthetic client ids: it must produce the sign-up settings.
  docker cp "$root/docker/keycloak/configure.sh" "${kcName}:/tmp/configure.sh"
  docker exec -e KEYCLOAK_SERVER=http://localhost:8080 -e KEYCLOAK_ADMIN=ci-admin -e "KEYCLOAK_ADMIN_PASSWORD=$kc" `
    -e TAO_SOCIAL_LOGIN=google,microsoft `
    -e GOOGLE_CLIENT_ID=synthetic -e GOOGLE_CLIENT_SECRET=synthetic `
    -e MICROSOFT_CLIENT_ID=synthetic -e MICROSOFT_CLIENT_SECRET=synthetic `
    $kcName bash /tmp/configure.sh
  if ($LASTEXITCODE -ne 0) { throw 'configure.sh failed' }

  $token = (Invoke-RestMethod -Method Post http://localhost:8081/realms/master/protocol/openid-connect/token `
    -Body @{ grant_type = 'password'; client_id = 'admin-cli'; username = 'ci-admin'; password = $kc }).access_token
  $auth = @{ Authorization = "Bearer $token" }
  $base = 'http://localhost:8081/admin/realms/tao/identity-provider/instances'
  $realm = Invoke-RestMethod http://localhost:8081/admin/realms/tao -Headers $auth
  if ($realm.browserFlow -ne 'tao-social-only-browser') { throw 'The realm browser flow is not social only' }
  $google = Invoke-RestMethod "$base/google" -Headers $auth
  if ($google.config.validateSignature -ne 'true') { throw 'Google id tokens are not signature validated' }
  foreach ($alias in 'google', 'microsoft') {
    $idp = Invoke-RestMethod "$base/$alias" -Headers $auth
    if ($idp.firstBrokerLoginFlowAlias -ne 'first broker login') { throw "$alias does not use the built-in first-login flow" }
    if ($idp.trustEmail) { throw "$alias trusts the provider e-mail" }
    # Same flow and trust settings, but the upstream is the synthetic realm (Google/Microsoft cannot be reached here).
    Invoke-RestMethod -Method Delete "$base/$alias" -Headers $auth | Out-Null
    $body = @{
      alias = $alias; providerId = 'oidc'; enabled = $true; trustEmail = $idp.trustEmail
      storeToken = $false; linkOnly = $false; firstBrokerLoginFlowAlias = $idp.firstBrokerLoginFlowAlias
      config = @{
        clientId = 'tao-broker'; clientSecret = $upstreamSecret; clientAuthMethod = 'client_secret_post'
        authorizationUrl = 'http://localhost:8081/realms/fake-social/protocol/openid-connect/auth'
        tokenUrl = 'http://localhost:8080/realms/fake-social/protocol/openid-connect/token'
        jwksUrl = 'http://localhost:8080/realms/fake-social/protocol/openid-connect/certs'
        issuer = 'http://localhost:8081/realms/fake-social'
        useJwksUrl = 'true'; validateSignature = 'true'; defaultScope = 'openid profile email'
        syncMode = $idp.config.syncMode
      }
    } | ConvertTo-Json -Depth 5
    Invoke-RestMethod -Method Post $base -Headers $auth -ContentType 'application/json' -Body $body | Out-Null
  }
  Write-Host 'Keycloak, PostgreSQL, Tao realm configured by configure.sh and synthetic upstream ready'

  $env:Database__ConnectionString = "Host=127.0.0.1;Port=15434;Database=tao_test;Username=tao_app;Password=$app"
  $env:Database__MigrationConnectionString = "Host=127.0.0.1;Port=15434;Database=tao_test;Username=tao;Password=$pg"
  $env:Database__RetentionConnectionString = "Host=127.0.0.1;Port=15434;Database=tao_test;Username=tao_retention;Password=$ret"
  $env:Crypto__KeyRingPath = $keyring
  $env:Auth__Issuer = 'http://localhost:8081/realms/tao'
  $env:Auth__Audience = 'tao-api'
  $env:Auth__SocialProviders = 'google,microsoft'
  $env:Embeddings__Provider = 'none'
  $env:Cors__Origins__0 = 'http://127.0.0.1:19006'
  $env:ASPNETCORE_ENVIRONMENT = 'Development'
  $env:ASPNETCORE_URLS = 'http://127.0.0.1:8080'
  $apiOut = Join-Path ([IO.Path]::GetTempPath()) "tao-social-api-$tag.log"
  $apiErr = Join-Path ([IO.Path]::GetTempPath()) "tao-social-api-$tag.err.log"
  $api = Start-Process dotnet -ArgumentList 'run', '--project', "$root/src/backend/Tao.Api/Tao.Api.csproj", '--configuration', 'Release', '--no-launch-profile' `
    -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -NoNewWindow -PassThru
  $ready = $false
  for ($i = 0; $i -lt 100 -and -not $ready; $i++) {
    if ($api.HasExited) { break }
    try { Invoke-RestMethod http://127.0.0.1:8080/health -TimeoutSec 5 | Out-Null; $ready = $true } catch { Start-Sleep -Seconds 2 }
  }
  if (-not $ready) { Get-Content $apiOut -Tail 30; Get-Content $apiErr -Tail 30; throw 'API not ready' }
  $options = Invoke-RestMethod http://127.0.0.1:8080/api/login-options
  if (($options.providers -join ',') -ne 'google,microsoft') { throw 'The API does not offer the social providers' }

  Set-Location "$root/src/mobile"
  $env:EXPO_PUBLIC_TAO_OIDC_ISSUER = 'http://localhost:8081/realms/tao'
  $env:EXPO_PUBLIC_TAO_OIDC_CLIENT_ID = 'tao-web'
  $env:EXPO_PUBLIC_TAO_API_URL = 'http://127.0.0.1:8080'
  $env:TAO_WEB_BASE_PATH = '/Tao'
  npm run build:web 2>&1 | Select-Object -Last 3
  if ($LASTEXITCODE -ne 0) { throw 'web build failed' }
  $env:TAO_SOCIAL_E2E = '1'
  $env:TAO_E2E_KC_URL = 'http://localhost:8081'
  $env:TAO_E2E_KC_PASSWORD = $kc
  $env:TAO_E2E_PASSWORD = $upstreamPassword
  npx playwright test e2e/social-signup.spec.ts --reporter=list --retries=0 @PlaywrightArgs 2>&1 | Out-String -Width 250
  if ($LASTEXITCODE -ne 0) { throw 'social sign-up E2E failed' }
}
finally {
  if ($api) { Get-CimInstance Win32_Process -Filter "ParentProcessId=$($api.Id)" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue }
  docker stop $pgName $kcName 2>$null | Out-Null
  foreach ($v in $apiVars + $webVars) { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
  Remove-Item $keyring -ErrorAction SilentlyContinue
  Set-Location $root
}
