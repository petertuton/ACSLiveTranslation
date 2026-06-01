# ACS Live Translation

Real-time video calling with multi-language translation powered by **Azure Communication Services** and **Azure AI Speech**.

Users join an ACS video call, see **live translated captions** in their chosen language, and optionally hear **synthesized translated audio**.

> [!IMPORTANT]
> This project is provided for demonstration and educational purposes only. It is not intended for production use without additional review, hardening, security validation, operational monitoring, compliance assessment, and testing.

---

## Architecture

| Component | Technology | Purpose |
|---|---|---|
| **Backend API** | Azure Function App (.NET 8, C# isolated worker) | Issues ACS access tokens, manages sessions, negotiates Web PubSub |
| **Media Processor** | ASP.NET Core (.NET 9) → Azure Container Apps | Taps call audio via Call Automation → Speech SDK for STT + Translation → publishes captions via Web PubSub, optional TTS |
| **Kiosk Client** | Blazor Server (.NET 9) | ACS Calling SDK (JS interop), displays video + live captions from Web PubSub |

See `ACS-LiveTranslation-Architecture.drawio` for the full cloud architecture diagram.

### Authentication

All Azure service connections use **Microsoft Entra ID (managed identity)** via `DefaultAzureCredential` — no connection strings or access keys in code or config. This applies to:

- **Azure Communication Services** — `CommunicationIdentityClient` and `CallAutomationClient`
- **Azure Web PubSub** — `WebPubSubServiceClient`
- **Azure AI Speech** — Speech SDK with `aad#resourceId#token` authorization

Locally, `DefaultAzureCredential` uses your Azure CLI login. In Azure, it uses the resource's system-assigned managed identity.

---

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (also builds .NET 8 for BackendApi)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
- [Azure Dev Tunnels](https://learn.microsoft.com/azure/developer/dev-tunnels/get-started) (for local end-to-end testing)
- An Azure subscription

---

## Azure Resource Setup

> All CLI commands below are shown in both **Bash** and **PowerShell**. Set the configuration variables at the start of your terminal session — they are referenced throughout this guide and in the [Deployment](#deployment-to-azure) section.

### Configuration Variables

**Bash:**
```bash
RG="rg-live-translation"
LOCATION="eastus"
ACS_NAME="acs-live-translation"
ACS_DATA_LOCATION="unitedstates"
SPEECH_NAME="speech-live-translation"
PUBSUB_NAME="pubsub-live-translation"
PUBSUB_HUB="captions"
LAW_NAME="law-live-translation"
CAE_NAME="cae-live-translation"
AI_NAME="ai-live-translation"
ACR_NAME="acrlivetranslation"
FUNC_NAME="func-live-translation"
STORAGE_NAME="stlivetranslation"
CA_NAME="media-processor"
APP_NAME="app-kiosk-live-translation"
ASP_NAME="asp-live-translation"
```

**PowerShell:**
```powershell
$RG = "rg-live-translation"
$LOCATION = "eastus"
$ACS_NAME = "acs-live-translation"
$ACS_DATA_LOCATION = "unitedstates"
$SPEECH_NAME = "speech-live-translation"
$PUBSUB_NAME = "pubsub-live-translation"
$PUBSUB_HUB = "captions"
$LAW_NAME = "law-live-translation"
$CAE_NAME = "cae-live-translation"
$AI_NAME = "ai-live-translation"
$ACR_NAME = "acrlivetranslation"
$FUNC_NAME = "func-live-translation"
$STORAGE_NAME = "stlivetranslation"
$CA_NAME = "media-processor"
$APP_NAME = "app-kiosk-live-translation"
$ASP_NAME = "asp-live-translation"
```

### 1. Create a Resource Group

**Bash:**
```bash
az group create --name $RG --location $LOCATION
```

**PowerShell:**
```powershell
az group create --name $RG --location $LOCATION
```

### 2. Azure Communication Services

**Bash:**
```bash
az communication create \
  --name $ACS_NAME \
  --resource-group $RG \
  --data-location $ACS_DATA_LOCATION \
  --location global
```

**PowerShell:**
```powershell
az communication create `
  --name $ACS_NAME `
  --resource-group $RG `
  --data-location $ACS_DATA_LOCATION `
  --location global
```

Note the endpoint (e.g. `https://acs-live-translation.unitedstates.communication.azure.com/`) — you'll need it for configuration.

### 3. Azure AI Speech Service

**Bash:**
```bash
az cognitiveservices account create \
  --name $SPEECH_NAME \
  --resource-group $RG \
  --kind SpeechServices \
  --sku S0 \
  --location $LOCATION \
  --yes
```

**PowerShell:**
```powershell
az cognitiveservices account create `
  --name $SPEECH_NAME `
  --resource-group $RG `
  --kind SpeechServices `
  --sku S0 `
  --location $LOCATION `
  --yes
```

Note the endpoint and full resource ID for configuration.

### 4. Azure Web PubSub

**Bash:**
```bash
az webpubsub create \
  --name $PUBSUB_NAME \
  --resource-group $RG \
  --sku Free_F1 \
  --location $LOCATION

az webpubsub hub create \
  --name $PUBSUB_NAME \
  --resource-group $RG \
  --hub-name $PUBSUB_HUB \
  --allow-anonymous false
```

**PowerShell:**
```powershell
az webpubsub create `
  --name $PUBSUB_NAME `
  --resource-group $RG `
  --sku Free_F1 `
  --location $LOCATION

az webpubsub hub create `
  --name $PUBSUB_NAME `
  --resource-group $RG `
  --hub-name $PUBSUB_HUB `
  --allow-anonymous false
```

Note the endpoint (e.g. `https://pubsub-live-translation.webpubsub.azure.com`).

### 5. RBAC Role Assignments

All services authenticate via `DefaultAzureCredential`. You must assign the correct roles to every identity that will access these resources (your user account for local dev, managed identities for Azure).

**Bash:**
```bash
USER_ID=$(az ad signed-in-user show --query id -o tsv)

# ACS — create users, issue tokens, Call Automation
az role assignment create \
  --assignee $USER_ID \
  --role "Communication and Email Service Owner" \
  --scope $(az communication show -n $ACS_NAME -g $RG --query id -o tsv)

# Web PubSub — generate client access URIs, send messages to groups
az role assignment create \
  --assignee $USER_ID \
  --role "Web PubSub Service Owner" \
  --scope $(az webpubsub show -n $PUBSUB_NAME -g $RG --query id -o tsv)

# Speech — use STT, translation, and TTS
az role assignment create \
  --assignee $USER_ID \
  --role "Cognitive Services Speech User" \
  --scope $(az cognitiveservices account show -n $SPEECH_NAME -g $RG --query id -o tsv)
```

**PowerShell:**
```powershell
$USER_ID = az ad signed-in-user show --query id -o tsv

# ACS — create users, issue tokens, Call Automation
$ACS_SCOPE = az communication show -n $ACS_NAME -g $RG --query id -o tsv
az role assignment create `
  --assignee $USER_ID `
  --role "Communication and Email Service Owner" `
  --scope $ACS_SCOPE

# Web PubSub — generate client access URIs, send messages to groups
$PUBSUB_SCOPE = az webpubsub show -n $PUBSUB_NAME -g $RG --query id -o tsv
az role assignment create `
  --assignee $USER_ID `
  --role "Web PubSub Service Owner" `
  --scope $PUBSUB_SCOPE

# Speech — use STT, translation, and TTS
$SPEECH_SCOPE = az cognitiveservices account show -n $SPEECH_NAME -g $RG --query id -o tsv
az role assignment create `
  --assignee $USER_ID `
  --role "Cognitive Services Speech User" `
  --scope $SPEECH_SCOPE
```

> **Note:** Role assignments can take 1-2 minutes to propagate.

### 6. Optional: Azure Monitor & Application Insights

**Bash:**
```bash
az monitor app-insights component create \
  --app $AI_NAME \
  --resource-group $RG \
  --location $LOCATION \
  --application-type web
```

**PowerShell:**
```powershell
az monitor app-insights component create `
  --app $AI_NAME `
  --resource-group $RG `
  --location $LOCATION `
  --application-type web
```

### 7. Azure Container Apps Environment (for Media Processor)

**Bash:**
```bash
az monitor log-analytics workspace create \
  --resource-group $RG \
  --workspace-name $LAW_NAME \
  --location $LOCATION

az containerapp env create \
  --name $CAE_NAME \
  --resource-group $RG \
  --location $LOCATION \
  --logs-destination log-analytics \
  --logs-workspace-name $LAW_NAME
```

**PowerShell:**
```powershell
az monitor log-analytics workspace create `
  --resource-group $RG `
  --workspace-name $LAW_NAME `
  --location $LOCATION

az containerapp env create `
  --name $CAE_NAME `
  --resource-group $RG `
  --location $LOCATION `
  --logs-destination log-analytics `
  --logs-workspace-name $LAW_NAME
```

---

## Local Configuration

Copy the template files and fill in your resource endpoints:

**Bash:**
```bash
cp src/BackendApi/local.settings.template.json  src/BackendApi/local.settings.json
cp src/MediaProcessor/appsettings.template.json  src/MediaProcessor/appsettings.json
cp src/KioskClient/appsettings.template.json     src/KioskClient/appsettings.json
```

**PowerShell:**
```powershell
Copy-Item src/BackendApi/local.settings.template.json  src/BackendApi/local.settings.json
Copy-Item src/MediaProcessor/appsettings.template.json  src/MediaProcessor/appsettings.json
Copy-Item src/KioskClient/appsettings.template.json     src/KioskClient/appsettings.json
```

### Backend API (`src/BackendApi/local.settings.json`)

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AcsEndpoint": "https://<your-acs-resource>.unitedstates.communication.azure.com/",
    "WebPubSubEndpoint": "https://<your-webpubsub-resource>.webpubsub.azure.com",
    "WebPubSubHubName": "captions"
  },
  "Host": {
    "CORS": "http://localhost:5177,https://localhost:5177",
    "CORSCredentials": true
  }
}
```

### Media Processor (`src/MediaProcessor/appsettings.json`)

```json
{
  "AcsEndpoint": "https://<your-acs-resource>.unitedstates.communication.azure.com/",
  "CallbackBaseUrl": "https://<your-devtunnel-id>.devtunnels.ms/api/callbacks",
  "Speech": {
    "Endpoint": "https://<your-speech-resource>.cognitiveservices.azure.com/",
    "ResourceId": "/subscriptions/<sub-id>/resourceGroups/<rg>/providers/Microsoft.CognitiveServices/accounts/<your-speech-resource>",
    "Region": "<your-region>",
    "SourceLanguage": "en-US",
    "TargetLanguages": ["en", "es", "fr", "de", "zh-Hans", "ja", "ko", "pt", "ar"]
  },
  "WebPubSub": {
    "Endpoint": "https://<your-webpubsub-resource>.webpubsub.azure.com",
    "HubName": "captions"
  }
}
```

### Kiosk Client (`src/KioskClient/appsettings.json`)

```json
{
  "BackendApi": {
    "BaseUrl": "http://localhost:7071"
  },
  "MediaProcessor": {
    "BaseUrl": "http://localhost:5001"
  }
}
```

> **No secrets in config.** All config values are public resource endpoints. Authentication is handled by `DefaultAzureCredential` at runtime.

---

## Running Locally

### 1. Sign in to Azure CLI

```bash
az login
```

`DefaultAzureCredential` automatically uses your Azure CLI credentials for local development. Make sure the signed-in account has the RBAC roles listed in [RBAC Role Assignments](#5-rbac-role-assignments).

### 2. Set up a Dev Tunnel (required for Call Automation callbacks)

The Media Processor receives audio via ACS Call Automation webhooks, which require a publicly reachable URL:

```bash
devtunnel create --allow-anonymous
devtunnel port create -p 5001
devtunnel host
```

Copy the tunnel URL and set it as `CallbackBaseUrl` in `src/MediaProcessor/appsettings.json`:

```json
{
  "CallbackBaseUrl": "https://<your-tunnel-id>.devtunnels.ms/api/callbacks"
}
```

Without a tunnel, video calling still works but the real-time caption pipeline (audio → STT → translation → Web PubSub) will not receive audio from ACS.

### 3. Start All Services (three terminals)

**Terminal 1 — Backend API**

```bash
cd src/BackendApi
dotnet build
func start
```

**Terminal 2 — Media Processor**

```bash
cd src/MediaProcessor
dotnet build
dotnet run
```

**Terminal 3 — Kiosk Client**

```bash
cd src/KioskClient
dotnet build
dotnet run
```

### 4. Verify All Services

| Service | Bash | PowerShell |
|---|---|---|
| Backend API | `curl http://localhost:7071/api/token` | `Invoke-RestMethod http://localhost:7071/api/token` |
| Media Processor | `curl http://localhost:5001/health` | `Invoke-RestMethod http://localhost:5001/health` |
| Kiosk Client | Open the URL from terminal output in your browser | Same |

---

## Testing Locally (Two-User Simulation)

You can test with **two browser tabs/windows** on the same machine — no second device needed.

1. **Start all three services** as described above.

2. **Generate a Group Call ID** (any GUID works as a shared "room name"):

   **Bash:**
   ```bash
   uuidgen
   ```

   **PowerShell:**
   ```powershell
   [guid]::NewGuid()
   ```

3. **Open two browser tabs** to the Blazor app at `https://localhost:7177`.

4. In each tab, enter a **different display name**, paste the **same GUID** as the Group Call ID, pick a **different caption language**, and click **Join Call**.

Both tabs join the same ACS group call — you'll see each other's video and receive translated captions.

### Tips

- **Use different browser profiles** (or one regular + one incognito window) if you hit microphone/camera device locking.
- **Headphones recommended** to avoid audio echo/feedback when two tabs play audio on the same machine.
- Video calling between tabs works immediately via the ACS Calling SDK (fully client-side).

---

## How It Works

1. **User opens the Blazor Kiosk Client** and enters a display name, group call ID, and selects a caption language.
2. **Client requests an ACS token** from the Backend API (`GET /api/token`).
3. **Client negotiates a Web PubSub connection** for their selected language (`GET /api/pubsub/negotiate?language=es`).
4. **Client joins the ACS video call** using the ACS Calling SDK (JavaScript interop).
5. **Media Processor joins the call** via ACS Call Automation (`ConnectCallAsync`) and starts media streaming.
6. **Call audio streams over WebSocket** to the Media Processor, which feeds it into the Azure AI Speech SDK.
7. **Speech SDK performs STT + Translation** in real-time, producing translations for all configured target languages.
8. **Translated captions are published** to Azure Web PubSub, one group per language (`captions-en`, `captions-es`, etc.).
9. **Each client receives captions** for their selected language over WebSocket and displays them as an overlay.
10. Optionally, **TTS synthesizes translated audio** and injects it back into the call via Call Automation.

---

## API Endpoints (Backend)

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/token` | Create ACS user + issue VoIP access token |
| `POST` | `/api/sessions` | Create a translation session for a call |
| `DELETE` | `/api/sessions/{sessionId}` | End a translation session |
| `GET` | `/api/pubsub/negotiate?language={lang}` | Get Web PubSub client URL for a language group |

---

## Deployment to Azure

All deployments use **system-assigned managed identities** — no secrets to manage.

### Deploy Backend API (Function App)

**Bash:**
```bash
az functionapp create \
  --name $FUNC_NAME \
  --resource-group $RG \
  --storage-account $STORAGE_NAME \
  --consumption-plan-location $LOCATION \
  --runtime dotnet-isolated \
  --runtime-version 8 \
  --functions-version 4

# Enable system-assigned managed identity
az functionapp identity assign \
  --name $FUNC_NAME \
  --resource-group $RG

# Get the identity's principal ID
FUNC_IDENTITY=$(az functionapp identity show \
  --name $FUNC_NAME \
  --resource-group $RG \
  --query principalId -o tsv)

# Assign RBAC roles
az role assignment create --assignee $FUNC_IDENTITY \
  --role "Communication and Email Service Owner" \
  --scope $(az communication show -n $ACS_NAME -g $RG --query id -o tsv)

az role assignment create --assignee $FUNC_IDENTITY \
  --role "Web PubSub Service Owner" \
  --scope $(az webpubsub show -n $PUBSUB_NAME -g $RG --query id -o tsv)

# Configure app settings (endpoints only — no secrets)
az functionapp config appsettings set \
  --name $FUNC_NAME \
  --resource-group $RG \
  --settings \
    "AcsEndpoint=https://$ACS_NAME.$ACS_DATA_LOCATION.communication.azure.com/" \
    "WebPubSubEndpoint=https://$PUBSUB_NAME.webpubsub.azure.com" \
    "WebPubSubHubName=$PUBSUB_HUB"

# Publish
cd src/BackendApi
func azure functionapp publish $FUNC_NAME
```

**PowerShell:**
```powershell
az functionapp create `
  --name $FUNC_NAME `
  --resource-group $RG `
  --storage-account $STORAGE_NAME `
  --consumption-plan-location $LOCATION `
  --runtime dotnet-isolated `
  --runtime-version 8 `
  --functions-version 4

# Enable system-assigned managed identity
az functionapp identity assign `
  --name $FUNC_NAME `
  --resource-group $RG

# Get the identity's principal ID
$FUNC_IDENTITY = az functionapp identity show `
  --name $FUNC_NAME `
  --resource-group $RG `
  --query principalId -o tsv

# Assign RBAC roles
$ACS_SCOPE = az communication show -n $ACS_NAME -g $RG --query id -o tsv
az role assignment create --assignee $FUNC_IDENTITY `
  --role "Communication and Email Service Owner" `
  --scope $ACS_SCOPE

$PUBSUB_SCOPE = az webpubsub show -n $PUBSUB_NAME -g $RG --query id -o tsv
az role assignment create --assignee $FUNC_IDENTITY `
  --role "Web PubSub Service Owner" `
  --scope $PUBSUB_SCOPE

# Configure app settings (endpoints only — no secrets)
az functionapp config appsettings set `
  --name $FUNC_NAME `
  --resource-group $RG `
  --settings `
    "AcsEndpoint=https://$ACS_NAME.$ACS_DATA_LOCATION.communication.azure.com/" `
    "WebPubSubEndpoint=https://$PUBSUB_NAME.webpubsub.azure.com" `
    "WebPubSubHubName=$PUBSUB_HUB"

# Publish
Push-Location src/BackendApi
func azure functionapp publish $FUNC_NAME
Pop-Location
```

### Deploy Media Processor (Container App)

**Bash:**
```bash
# Create ACR (if it doesn't exist)
az acr create --name $ACR_NAME --resource-group $RG --sku Basic --admin-enabled true

# Build and push the container image
cd src/MediaProcessor
docker build -t $ACR_NAME.azurecr.io/$CA_NAME:latest .

# Login to ACR and push
ACR_PASSWORD=$(az acr credential show --name $ACR_NAME --query "passwords[0].value" -o tsv)
echo "$ACR_PASSWORD" | docker login $ACR_NAME.azurecr.io -u $ACR_NAME --password-stdin
docker push $ACR_NAME.azurecr.io/$CA_NAME:latest

# Deploy to Container Apps with system-assigned managed identity
az containerapp create \
  --name $CA_NAME \
  --resource-group $RG \
  --environment $CAE_NAME \
  --image $ACR_NAME.azurecr.io/$CA_NAME:latest \
  --registry-server $ACR_NAME.azurecr.io \
  --ingress external --target-port 8080 \
  --system-assigned \
  --env-vars \
    "AcsEndpoint=https://$ACS_NAME.$ACS_DATA_LOCATION.communication.azure.com/" \
    "CallbackBaseUrl=https://$CA_NAME.<your-cae-domain>.azurecontainerapps.io/api/callbacks" \
    "Speech__Endpoint=https://$SPEECH_NAME.cognitiveservices.azure.com/" \
    "Speech__ResourceId=/subscriptions/<sub-id>/resourceGroups/$RG/providers/Microsoft.CognitiveServices/accounts/$SPEECH_NAME" \
    "Speech__Region=$LOCATION" \
    "Speech__SourceLanguage=en-US" \
    "Speech__TargetLanguages__0=en" \
    "Speech__TargetLanguages__1=es" \
    "Speech__TargetLanguages__2=fr" \
    "Speech__TargetLanguages__3=de" \
    "WebPubSub__Endpoint=https://$PUBSUB_NAME.webpubsub.azure.com" \
    "WebPubSub__HubName=$PUBSUB_HUB"

# Get the managed identity principal ID
CA_IDENTITY=$(az containerapp identity show \
  --name $CA_NAME \
  --resource-group $RG \
  --query principalId -o tsv)

# Assign RBAC roles
az role assignment create --assignee $CA_IDENTITY \
  --role "Communication and Email Service Owner" \
  --scope $(az communication show -n $ACS_NAME -g $RG --query id -o tsv)

az role assignment create --assignee $CA_IDENTITY \
  --role "Web PubSub Service Owner" \
  --scope $(az webpubsub show -n $PUBSUB_NAME -g $RG --query id -o tsv)

az role assignment create --assignee $CA_IDENTITY \
  --role "Cognitive Services Speech User" \
  --scope $(az cognitiveservices account show -n $SPEECH_NAME -g $RG --query id -o tsv)
```

**PowerShell:**
```powershell
# Create ACR (if it doesn't exist)
az acr create --name $ACR_NAME --resource-group $RG --sku Basic --admin-enabled true

# Build and push the container image
Push-Location src/MediaProcessor
docker build -t "$ACR_NAME.azurecr.io/${CA_NAME}:latest" .

# Login to ACR and push
$ACR_PASSWORD = az acr credential show --name $ACR_NAME --query "passwords[0].value" -o tsv
$ACR_PASSWORD | docker login "$ACR_NAME.azurecr.io" -u $ACR_NAME --password-stdin
docker push "$ACR_NAME.azurecr.io/${CA_NAME}:latest"

# Deploy to Container Apps with system-assigned managed identity
az containerapp create `
  --name $CA_NAME `
  --resource-group $RG `
  --environment $CAE_NAME `
  --image "$ACR_NAME.azurecr.io/${CA_NAME}:latest" `
  --registry-server "$ACR_NAME.azurecr.io" `
  --ingress external --target-port 8080 `
  --system-assigned `
  --env-vars `
    "AcsEndpoint=https://$ACS_NAME.$ACS_DATA_LOCATION.communication.azure.com/" `
    "CallbackBaseUrl=https://$CA_NAME.<your-cae-domain>.azurecontainerapps.io/api/callbacks" `
    "Speech__Endpoint=https://$SPEECH_NAME.cognitiveservices.azure.com/" `
    "Speech__ResourceId=/subscriptions/<sub-id>/resourceGroups/$RG/providers/Microsoft.CognitiveServices/accounts/$SPEECH_NAME" `
    "Speech__Region=$LOCATION" `
    "Speech__SourceLanguage=en-US" `
    "Speech__TargetLanguages__0=en" `
    "Speech__TargetLanguages__1=es" `
    "Speech__TargetLanguages__2=fr" `
    "Speech__TargetLanguages__3=de" `
    "WebPubSub__Endpoint=https://$PUBSUB_NAME.webpubsub.azure.com" `
    "WebPubSub__HubName=$PUBSUB_HUB"
Pop-Location

# Get the managed identity principal ID
$CA_IDENTITY = az containerapp identity show `
  --name $CA_NAME `
  --resource-group $RG `
  --query principalId -o tsv

# Assign RBAC roles
$ACS_SCOPE = az communication show -n $ACS_NAME -g $RG --query id -o tsv
az role assignment create --assignee $CA_IDENTITY `
  --role "Communication and Email Service Owner" `
  --scope $ACS_SCOPE

$PUBSUB_SCOPE = az webpubsub show -n $PUBSUB_NAME -g $RG --query id -o tsv
az role assignment create --assignee $CA_IDENTITY `
  --role "Web PubSub Service Owner" `
  --scope $PUBSUB_SCOPE

$SPEECH_SCOPE = az cognitiveservices account show -n $SPEECH_NAME -g $RG --query id -o tsv
az role assignment create --assignee $CA_IDENTITY `
  --role "Cognitive Services Speech User" `
  --scope $SPEECH_SCOPE
```

### Deploy Kiosk Client (App Service)

**Bash:**
```bash
cd src/KioskClient
dotnet publish -c Release -o ./publish

az webapp create \
  --name $APP_NAME \
  --resource-group $RG \
  --plan $ASP_NAME \
  --runtime "DOTNETCORE:9.0"

# Enable WebSockets (required for Blazor Server SignalR transport)
az webapp config set \
  --name $APP_NAME \
  --resource-group $RG \
  --web-sockets-enabled true

az webapp config appsettings set \
  --name $APP_NAME \
  --resource-group $RG \
  --settings \
    "BackendApi__BaseUrl=https://$FUNC_NAME.azurewebsites.net" \
    "MediaProcessor__BaseUrl=https://$CA_NAME.<your-cae-domain>.azurecontainerapps.io"

zip -r ./publish.zip ./publish/*

az webapp deploy \
  --name $APP_NAME \
  --resource-group $RG \
  --src-path ./publish.zip \
  --type zip
```

**PowerShell:**
```powershell
Push-Location src/KioskClient
dotnet publish -c Release -o ./publish

az webapp create `
  --name $APP_NAME `
  --resource-group $RG `
  --plan $ASP_NAME `
  --runtime "DOTNETCORE:9.0"

# Enable WebSockets (required for Blazor Server SignalR transport)
az webapp config set `
  --name $APP_NAME `
  --resource-group $RG `
  --web-sockets-enabled true

az webapp config appsettings set `
  --name $APP_NAME `
  --resource-group $RG `
  --settings `
    "BackendApi__BaseUrl=https://$FUNC_NAME.azurewebsites.net" `
    "MediaProcessor__BaseUrl=https://$CA_NAME.<your-cae-domain>.azurecontainerapps.io"

Compress-Archive -Path ./publish/* -DestinationPath ./publish.zip -Force

az webapp deploy `
  --name $APP_NAME `
  --resource-group $RG `
  --src-path ./publish.zip `
  --type zip
Pop-Location
```

---

## Supported Languages

| Code | Language |
|---|---|
| `en-US` | English (source) |
| `es` | Spanish |
| `fr` | French |
| `de` | German |
| `zh-Hans` | Chinese (Simplified) |
| `ja` | Japanese |
| `ko` | Korean |
| `pt` | Portuguese |
| `ar` | Arabic |

---

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
