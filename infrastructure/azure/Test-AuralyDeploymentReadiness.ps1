#Requires -Version 5.1
#Requires -Modules Az.Accounts, Az.Resources, Az.ServiceBus, Az.Websites

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Dev', 'Prod')]
    [string]$Environment,

    [Parameter(Mandatory)]
    [string]$ReleaseVersion,

    [string]$SubscriptionId = '5ea009ce-23c5-4bbd-b1c8-62116d58f596',

    [switch]$LocalOnly,

    [switch]$SkipHealth
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$releasePath = Join-Path $repoRoot "artifacts\releases\$ReleaseVersion"
$manifestPath = Join-Path $releasePath 'manifest.json'
$compactEnvironment = $Environment.ToLowerInvariant()
$suffix = if ($Environment -eq 'Dev') { 'w5usmo6w' } else { '7sov4nxc' }
$resourceGroup = "RG-AURALY-$($Environment.ToUpperInvariant())"
$apiName = "api-auraly-$compactEnvironment-$suffix"
$functionName = "func-auraly-$compactEnvironment-$suffix"
$serviceBusName = "sb-auraly-$compactEnvironment-$suffix"
$webPubSubName = "wps-auraly-$compactEnvironment-$suffix"
$staticAdminName = "admin-auraly-$compactEnvironment-$suffix"
$emailServiceName = "email-auraly-$compactEnvironment-$suffix"
$communicationServiceName = "acs-auraly-$compactEnvironment-$suffix"
$sqlServerName = "sql-auraly-$compactEnvironment-$suffix"
$databaseName = "auraly-$compactEnvironment"
$requiredQueues = @(
    'auraly-document-processing',
    'auraly-accounting-processing',
    'auraly-fiscal-processing',
    'auraly-sales-reporting'
)

function Assert-Condition {
    param(
        [bool]$Condition,
        [string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Test-ReleaseArtifacts {
    Assert-Condition (Test-Path -LiteralPath $manifestPath) `
        "No existe el manifiesto inmutable $manifestPath."
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-Condition ($manifest.product -eq 'AURALY') 'El manifiesto no pertenece a Auraly.'
    Assert-Condition ($manifest.version -eq $ReleaseVersion) 'La version del manifiesto no coincide.'
    Assert-Condition (-not [bool]$manifest.dirty) 'El release fue creado desde un arbol sucio.'
    Assert-Condition ([version]($manifest.node.TrimStart('v')) -ge [version]'20.19.0') `
        'El release debe construirse con Node.js 20.19 o superior.'

    foreach ($artifact in $manifest.artifacts) {
        $path = Join-Path $releasePath $artifact.name
        Assert-Condition (Test-Path -LiteralPath $path) "Falta el artefacto $($artifact.name)."
        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-Condition ($actualHash -eq $artifact.sha256) `
            "El hash no coincide para $($artifact.name)."
        Assert-Condition ((Get-Item -LiteralPath $path).Length -eq $artifact.bytes) `
            "El tamano no coincide para $($artifact.name)."
    }

    $currentCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    Assert-Condition ($LASTEXITCODE -eq 0) 'No fue posible leer el commit actual.'
    Assert-Condition ($currentCommit -eq $manifest.commit) `
        'El release no corresponde al commit actual. Cree una nueva version; no reutilice artefactos.'
}

function Test-Templates {
    $output = Join-Path ([IO.Path]::GetTempPath()) "auraly-main-$([guid]::NewGuid().ToString('N')).json"
    try {
        & az bicep build --file (Join-Path $PSScriptRoot 'main.bicep') --outfile $output
        Assert-Condition ($LASTEXITCODE -eq 0) 'main.bicep no compila.'
    }
    finally {
        if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
    }
}

function ConvertTo-SettingsMap {
    param($AppSettings)
    $map = @{}
    foreach ($setting in $AppSettings) { $map[$setting.Name] = [string]$setting.Value }
    return $map
}

function Test-RemoteEnvironment {
    Set-AzContext -SubscriptionId $SubscriptionId | Out-Null
    $api = Get-AzWebApp -ResourceGroupName $resourceGroup -Name $apiName
    Assert-Condition ($null -ne $api) "No existe $apiName."
    Assert-Condition ($api.State -eq 'Running') "$apiName no esta en ejecucion."
    $function = Get-AzWebApp -ResourceGroupName $resourceGroup -Name $functionName
    Assert-Condition ($null -ne $function) "No existe $functionName."
    Assert-Condition ($function.State -eq 'Running') "$functionName no esta en ejecucion."
    $functionSettings = ConvertTo-SettingsMap $function.SiteConfig.AppSettings
    $requiredFunctionSettings = @(
        'AzureWebJobsStorage__accountName',
        'ServiceBusConnection__fullyQualifiedNamespace',
        'AppConfiguration__Endpoint',
        'AZURE_CLIENT_ID',
        'Release__Version'
    )
    $missingFunctionSettings = @($requiredFunctionSettings | Where-Object { -not $functionSettings.ContainsKey($_) -or [string]::IsNullOrWhiteSpace($functionSettings[$_]) })
    Assert-Condition ($missingFunctionSettings.Count -eq 0) "Faltan configuraciones del worker: $($missingFunctionSettings -join ', ')."
    Assert-Condition ($functionSettings['Release__Version'] -eq $ReleaseVersion) 'La version configurada en el worker no coincide con el release solicitado.'
    $settings = ConvertTo-SettingsMap $api.SiteConfig.AppSettings

    $requiredSettings = @(
        'AppConfiguration__Endpoint',
        'AZURE_CLIENT_ID',
        'ServiceBusConnection__fullyQualifiedNamespace',
        'ServiceBusConnection__clientId',
        'Auraly__DocumentProcessing__ServiceBus__QueueName',
        'Auraly__Accounting__ServiceBus__QueueName',
        'Auraly__SalesReporting__ServiceBus__QueueName',
        'PosInstaller__ContainerName',
        'PosInstaller__BlobName',
        'PosInstaller__Version',
        'PosInstaller__Sha256',
        'Auraly__Fiscal__ServiceBus__QueueName',
        'Auraly__Fiscal__SecretProtectionKey',
        'Auraly__Fiscal__CredentialStore',
        'Auraly__Fiscal__KeyVaultUri',
        'Authentication__Jwt__Issuer',
        'Authentication__Jwt__Audience',
        'Authentication__Jwt__SigningKey',
        'Authentication__OfflineLeaseSigning__KeyId',
        'Authentication__OfflineLeaseSigning__PrivateKeyPem',
        'Authentication__OfflineLeaseSigning__DurationHours',
        'Notifications__WebPush__PublicKey',
        'Notifications__WebPush__PrivateKey',
        'Notifications__WebPush__Subject',
        'Notifications__WebPush__PublicAppUrl',
        'Auraly__Email__ConnectionString',
        'Auraly__Email__SenderAddress',
        'Auraly__Email__PublicAppUrl',
        'Auraly__Email__LogoUrl',
        'Auraly__Email__SupportEmail',
        'Auraly__PosSynchronization__WebPubSub__Endpoint',
        'Auraly__PosSynchronization__WebPubSub__ManagedIdentityClientId',
        'Auraly__PosSynchronization__WebPubSub__Hub',
        'Release__Version'
    )
    $missing = @($requiredSettings | Where-Object {
        -not $settings.ContainsKey($_) -or [string]::IsNullOrWhiteSpace($settings[$_])
    })
    Assert-Condition ($missing.Count -eq 0) `
        "Faltan configuraciones de runtime: $($missing -join ', ')."

    try {
        $fiscalBytes = [Convert]::FromBase64String($settings['Auraly__Fiscal__SecretProtectionKey'])
    }
    catch {
        throw 'Auraly__Fiscal__SecretProtectionKey no es Base64 valido.'
    }
    Assert-Condition ($fiscalBytes.Length -eq 32) `
        'Auraly__Fiscal__SecretProtectionKey debe contener exactamente 32 bytes.'
    Assert-Condition ($settings['Auraly__Fiscal__CredentialStore'] -eq 'AzureKeyVault') `
        'Auraly__Fiscal__CredentialStore debe usar AzureKeyVault en Azure.'
    Assert-Condition ($settings['Auraly__Fiscal__KeyVaultUri'] -match '^https://[^/]+\.vault\.azure\.net/?$') `
        'Auraly__Fiscal__KeyVaultUri no es una URI valida de Azure Key Vault.'
    $jwtLength = [Text.Encoding]::UTF8.GetByteCount(
        $settings['Authentication__Jwt__SigningKey'])
    Assert-Condition ($jwtLength -ge 32) `
        'Authentication__Jwt__SigningKey debe contener al menos 32 bytes.'
    Assert-Condition ($settings['Authentication__OfflineLeaseSigning__KeyId'] -match '^auraly-(dev|prod)-offline-v[0-9]+$') `
        'Authentication__OfflineLeaseSigning__KeyId no identifica una versión de clave válida.'
    Assert-Condition ($settings['Authentication__OfflineLeaseSigning__PrivateKeyPem'] -match 'BEGIN PRIVATE KEY') `
        'Authentication__OfflineLeaseSigning__PrivateKeyPem no contiene una clave privada PEM.'
    Assert-Condition ([int]$settings['Authentication__OfflineLeaseSigning__DurationHours'] -gt 0) `
        'Authentication__OfflineLeaseSigning__DurationHours debe ser mayor que cero.'
    Assert-Condition ($settings['Notifications__WebPush__PublicKey'].Length -ge 80) `
        'Notifications__WebPush__PublicKey no contiene una clave VAPID pública válida.'
    Assert-Condition ($settings['Notifications__WebPush__PrivateKey'].Length -ge 40) `
        'Notifications__WebPush__PrivateKey no contiene una clave VAPID privada válida.'
    Assert-Condition ($settings['Notifications__WebPush__Subject'] -match '^(mailto:|https://)') `
        'Notifications__WebPush__Subject debe ser mailto: o https://.'
    Assert-Condition ($settings['Notifications__WebPush__PublicAppUrl'] -match '^https://[^/]+/?$') `
        'Notifications__WebPush__PublicAppUrl debe ser el origen HTTPS de la aplicación.'
    Assert-Condition ($settings['Auraly__Email__SenderAddress'] -match '^DoNotReply@') `
        'Auraly__Email__SenderAddress no usa el remitente administrado esperado.'
    Assert-Condition ($settings['Release__Version'] -eq $ReleaseVersion) `
        'La version configurada en la API no coincide con el release solicitado.'
    Assert-Condition ($settings['PosInstaller__Version'] -eq $ReleaseVersion) `
        'La version del instalador POS no coincide con el release solicitado.'
    Assert-Condition ($settings['PosInstaller__Sha256'] -match '^[0-9A-Fa-f]{64}$') `
        'PosInstaller__Sha256 debe ser un SHA-256 valido.'

    $queues = @(
        Get-AzServiceBusQueue `
            -ResourceGroupName $resourceGroup `
            -NamespaceName $serviceBusName
    )
    foreach ($queueName in $requiredQueues) {
        $queue = $queues | Where-Object Name -eq $queueName | Select-Object -First 1
        Assert-Condition ($null -ne $queue) "Falta la cola $queueName."
        Assert-Condition ($queue.RequiresSession) `
            "La cola $queueName debe exigir sesiones por BusinessId."
    }

    $webPubSub = Get-AzResource `
        -ResourceGroupName $resourceGroup `
        -ResourceType 'Microsoft.SignalRService/WebPubSub' `
        -Name $webPubSubName `
        -ExpandProperties `
        -ErrorAction SilentlyContinue
    Assert-Condition ($null -ne $webPubSub) "Falta Web PubSub $webPubSubName."
    Assert-Condition ($webPubSub.Properties.provisioningState -eq 'Succeeded') `
        "Web PubSub $webPubSubName no termino de aprovisionarse."
    Assert-Condition ([bool]$webPubSub.Properties.disableLocalAuth) `
        "Web PubSub $webPubSubName debe bloquear autenticacion local."

    $emailService = Get-AzResource `
        -ResourceGroupName $resourceGroup `
        -ResourceType 'Microsoft.Communication/emailServices' `
        -Name $emailServiceName `
        -ExpandProperties `
        -ErrorAction SilentlyContinue
    Assert-Condition ($null -ne $emailService) "Falta Email Service $emailServiceName."
    Assert-Condition ($emailService.Properties.provisioningState -eq 'Succeeded') `
        "Email Service $emailServiceName no termino de aprovisionarse."

    $emailDomain = Get-AzResource `
        -ResourceGroupName $resourceGroup `
        -ResourceType 'Microsoft.Communication/emailServices/domains' `
        -Name "$emailServiceName/AzureManagedDomain" `
        -ExpandProperties `
        -ErrorAction SilentlyContinue
    Assert-Condition ($null -ne $emailDomain) `
        "Falta el dominio administrado de $emailServiceName."

    $communicationService = Get-AzResource `
        -ResourceGroupName $resourceGroup `
        -ResourceType 'Microsoft.Communication/communicationServices' `
        -Name $communicationServiceName `
        -ExpandProperties `
        -ErrorAction SilentlyContinue
    Assert-Condition ($null -ne $communicationService) `
        "Falta Communication Service $communicationServiceName."
    Assert-Condition ($communicationService.Properties.provisioningState -eq 'Succeeded') `
        "Communication Service $communicationServiceName no termino de aprovisionarse."
    Assert-Condition (@($communicationService.Properties.linkedDomains) -contains $emailDomain.ResourceId) `
        "Communication Service $communicationServiceName no esta vinculado al dominio de correo."

    $database = Get-AzResource `
        -ResourceGroupName $resourceGroup `
        -ResourceType 'Microsoft.Sql/servers/databases' `
        -Name "$sqlServerName/$databaseName" `
        -ExpandProperties `
        -ErrorAction SilentlyContinue
    Assert-Condition ($null -ne $database) "Falta la base SQL $databaseName."
    $expectedDatabaseSku = if ($Environment -eq 'Dev') { 'Basic' } else { 'S1' }
    Assert-Condition ($database.Sku.Name -eq $expectedDatabaseSku) `
        "La base $databaseName usa $($database.Sku.Name); se esperaba $expectedDatabaseSku."

    $staticAdmin = Get-AzResource `
        -ResourceGroupName $resourceGroup `
        -ResourceType 'Microsoft.Web/staticSites' `
        -Name $staticAdminName `
        -ExpandProperties `
        -ErrorAction SilentlyContinue
    Assert-Condition ($null -ne $staticAdmin) "Falta el frontend $staticAdminName."

    if (-not $SkipHealth) {
        $response = Invoke-WebRequest `
            -Uri "https://$apiName.azurewebsites.net/health" `
            -UseBasicParsing `
            -TimeoutSec 30
        Assert-Condition ($response.StatusCode -eq 200) 'La API no responde salud HTTP 200.'
        $health = $response.Content | ConvertFrom-Json
        Assert-Condition ($health.status -eq 'Healthy') 'La API no reporta estado Healthy.'

        $loginProbeStatus = 0
        try {
            $loginProbe = Invoke-WebRequest `
                -Uri "https://$($staticAdmin.Properties.defaultHostname)/api/auth/login" `
                -Method Post `
                -ContentType 'application/json' `
                -Body '{"username":"auraly-connectivity-probe","password":"invalid-probe"}' `
                -UseBasicParsing `
                -TimeoutSec 30
            $loginProbeStatus = [int]$loginProbe.StatusCode
        }
        catch {
            if (-not $_.Exception.Response) { throw }
            $loginProbeStatus = [int]$_.Exception.Response.StatusCode
        }
        Assert-Condition ($loginProbeStatus -eq 401) `
            'El BFF de autenticacion no alcanza la API; se esperaba 401 para credenciales de prueba.'
    }

    [pscustomobject]@{
        Environment = $Environment
        Release = $ReleaseVersion
        Api = $apiName
        ApiState = $api.State
        Worker = $functionName
        WorkerState = $function.State
        RuntimeSettings = 'Complete (values hidden)'
        Queues = $requiredQueues.Count
        WebPubSub = "$($webPubSub.Name) ($($webPubSub.Sku.Name))"
        Email = "$communicationServiceName -> $emailServiceName/AzureManagedDomain"
        Database = "$databaseName ($($database.Sku.Name))"
        Frontend = $staticAdmin.Properties.defaultHostname
        Health = if ($SkipHealth) { 'Skipped' } else { 'Healthy' }
    }
}

Test-ReleaseArtifacts
Test-Templates
if (-not $LocalOnly) { Test-RemoteEnvironment }
else {
    [pscustomobject]@{
        Environment = $Environment
        Release = $ReleaseVersion
        LocalArtifacts = 'Verified'
        Bicep = 'Verified'
        Azure = 'Skipped'
    }
}
