# ACS Live Translation

Real-time video calling with multi-language translation powered by **Azure Communication Services** and **Azure AI Speech**.

Users join an ACS video call, see **live translated captions** in their chosen language, and optionally hear **synthesized translated audio**.

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

### 1. Create a Resource Group

```bash
az group create --name rg-live-translation --location eastus
```

### 2. Azure Communication Services

```bash
az communication create \
  --name acs-live-translation \
  --resource-group rg-live-translation \
  --data-location unitedstates \
  --location global
```

Note the endpoint (e.g. `https://acs-live-translation.unitedstates.communication.azure.com/`) — you'll need it for configuration.

### 3. Azure AI Speech Service

```bash
az cognitiveservices account create \
  --name speech-live-translation \
  --resource-group rg-live-translation \
  --kind SpeechServices \
  --sku S0 \
  --location eastus \
  --yes
```

Note the endpoint and full resource ID for configuration.

### 4. Azure Web PubSub

```bash
az webpubsub create \
  --name pubsub-live-translation \
  --resource-group rg-live-translation \
  --sku Free_F1 \
  --location eastus

# Create the "captions" hub
az webpubsub hub create \
  --name pubsub-live-translation \
  --resource-group rg-live-translation \
  --hub-name captions \
  --allow-anonymous false
```

Note the endpoint (e.g. `https://pubsub-live-translation.webpubsub.azure.com`).

### 5. RBAC Role Assignments

All services authenticate via `DefaultAzureCredential`. You must assign the correct roles to every identity that will access these resources (your user account for local dev, managed identities for Azure).

```bash
# Get your signed-in user object ID (for local development)
USER_ID=$(az ad signed-in-user show --query id -o tsv)
RG="rg-live-translation"

# ACS — create users, issue tokens, Call Automation
az role assignment create \
  --assignee $USER_ID \
  --role "Communication and Email Service Owner" \
  --scope $(az communication show -n acs-live-translation -g $RG --query id -o tsv)

# Web PubSub — generate client access URIs, send messages to groups
az role assignment create \
  --assignee $USER_ID \
  --role "Web PubSub Service Owner" \
  --scope $(az webpubsub show -n pubsub-live-translation -g $RG --query id -o tsv)

# Speech — use STT, translation, and TTS
az role assignment create \
  --assignee $USER_ID \
  --role "Cognitive Services Speech User" \
  --scope $(az cognitiveservices account show -n speech-live-translation -g $RG --query id -o tsv)
```

> **Note:** Role assignments can take 1-2 minutes to propagate.

### 6. Optional: Azure Monitor & Application Insights

```bash
az monitor app-insights component create \
  --app ai-live-translation \
  --resource-group rg-live-translation \
  --location eastus \
  --application-type web
```

### 7. Azure Container Apps Environment (for Media Processor)

```bash
az monitor log-analytics workspace create \
  --resource-group rg-live-translation \
  --workspace-name law-live-translation \
  --location eastus

az containerapp env create \
  --name cae-live-translation \
  --resource-group rg-live-translation \
  --location eastus \
  --logs-destination log-analytics \
  --logs-workspace-name law-live-translation
```

---

## Local Configuration

Copy the template files and fill in your resource endpoints:

```bash
cp src/BackendApi/local.settings.template.json  src/BackendApi/local.settings.json
cp src/MediaProcessor/appsettings.template.json  src/MediaProcessor/appsettings.json
cp src/KioskClient/appsettings.template.json     src/KioskClient/appsettings.json
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

| Service | How to check |
|---|---|
| Backend API | `curl http://localhost:7071/api/token` — returns JSON with token |
| Media Processor | `curl http://localhost:5001/health` — returns healthy status |
| Kiosk Client | Open the URL from terminal output in your browser |

---

## Testing Locally (Two-User Simulation)

You can test with **two browser tabs/windows** on the same machine — no second device needed.

1. **Start all three services** as described above.

2. **Generate a Group Call ID** (any GUID works as a shared "room name"):
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

```bash
az functionapp create \
  --name func-live-translation \
  --resource-group rg-live-translation \
  --storage-account stlivetranslation \
  --consumption-plan-location eastus \
  --runtime dotnet-isolated \
  --runtime-version 8 \
  --functions-version 4

# Enable system-assigned managed identity
az functionapp identity assign \
  --name func-live-translation \
  --resource-group rg-live-translation

# Get the identity's principal ID
FUNC_IDENTITY=$(az functionapp identity show \
  --name func-live-translation \
  --resource-group rg-live-translation \
  --query principalId -o tsv)

# Assign RBAC roles
az role assignment create --assignee $FUNC_IDENTITY \
  --role "Communication and Email Service Owner" \
  --scope $(az communication show -n acs-live-translation -g rg-live-translation --query id -o tsv)

az role assignment create --assignee $FUNC_IDENTITY \
  --role "Web PubSub Service Owner" \
  --scope $(az webpubsub show -n pubsub-live-translation -g rg-live-translation --query id -o tsv)

# Configure app settings (endpoints only — no secrets)
az functionapp config appsettings set \
  --name func-live-translation \
  --resource-group rg-live-translation \
  --settings \
    "AcsEndpoint=https://acs-live-translation.unitedstates.communication.azure.com/" \
    "WebPubSubEndpoint=https://pubsub-live-translation.webpubsub.azure.com" \
    "WebPubSubHubName=captions"

# Publish
cd src/BackendApi
func azure functionapp publish func-live-translation
```

### Deploy Media Processor (Container App)

```bash
# Build and push container image
az acr create --name acrlivetranslation --resource-group rg-live-translation --sku Basic
az acr login --name acrlivetranslation

cd src/MediaProcessor
dotnet publish -c Release
docker build -t acrlivetranslation.azurecr.io/media-processor:latest .
docker push acrlivetranslation.azurecr.io/media-processor:latest

# Deploy to Container Apps with system-assigned managed identity
az containerapp create \
  --name media-processor \
  --resource-group rg-live-translation \
  --environment cae-live-translation \
  --image acrlivetranslation.azurecr.io/media-processor:latest \
  --registry-server acrlivetranslation.azurecr.io \
  --system-assigned \
  --env-vars \
    "AcsEndpoint=https://acs-live-translation.unitedstates.communication.azure.com/" \
    "CallbackBaseUrl=https://media-processor.<your-cae-domain>.azurecontainerapps.io/api/callbacks" \
    "Speech__Endpoint=https://speech-live-translation.cognitiveservices.azure.com/" \
    "Speech__ResourceId=/subscriptions/<sub-id>/resourceGroups/rg-live-translation/providers/Microsoft.CognitiveServices/accounts/speech-live-translation" \
    "Speech__Region=eastus" \
    "Speech__SourceLanguage=en-US" \
    "Speech__TargetLanguages__0=en" \
    "Speech__TargetLanguages__1=es" \
    "Speech__TargetLanguages__2=fr" \
    "Speech__TargetLanguages__3=de" \
    "WebPubSub__Endpoint=https://pubsub-live-translation.webpubsub.azure.com" \
    "WebPubSub__HubName=captions"

# Get the managed identity principal ID
CA_IDENTITY=$(az containerapp identity show \
  --name media-processor \
  --resource-group rg-live-translation \
  --query principalId -o tsv)

# Assign RBAC roles
az role assignment create --assignee $CA_IDENTITY \
  --role "Communication and Email Service Owner" \
  --scope $(az communication show -n acs-live-translation -g rg-live-translation --query id -o tsv)

az role assignment create --assignee $CA_IDENTITY \
  --role "Web PubSub Service Owner" \
  --scope $(az webpubsub show -n pubsub-live-translation -g rg-live-translation --query id -o tsv)

az role assignment create --assignee $CA_IDENTITY \
  --role "Cognitive Services Speech User" \
  --scope $(az cognitiveservices account show -n speech-live-translation -g rg-live-translation --query id -o tsv)
```

### Deploy Kiosk Client (App Service)

```bash
cd src/KioskClient
dotnet publish -c Release -o ./publish

az webapp create \
  --name app-kiosk-live-translation \
  --resource-group rg-live-translation \
  --plan asp-live-translation \
  --runtime "DOTNETCORE:9.0"

az webapp config appsettings set \
  --name app-kiosk-live-translation \
  --resource-group rg-live-translation \
  --settings \
    "BackendApi__BaseUrl=https://func-live-translation.azurewebsites.net" \
    "MediaProcessor__BaseUrl=https://media-processor.<your-cae-domain>.azurecontainerapps.io"

az webapp deploy \
  --name app-kiosk-live-translation \
  --resource-group rg-live-translation \
  --src-path ./publish \
  --type zip
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

This project is for demonstration purposes.
