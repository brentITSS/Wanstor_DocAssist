# Deploying the document change check to a new Azure subscription

Power Automate sends a change request and a live document to the `CheckChanges` function. The function uses Azure OpenAI to work out which requested changes were applied and returns `PASS`, `FAIL` or `PARTIAL` with one row per change.

Rebuild everything in this order, so each part is ready when the next one needs it.

## What's in this folder

| Path | Purpose |
|---|---|
| `DocumentTextFunction\` | The Azure Function app (.NET 8 isolated). This is what gets published. |
| `DocumentTextFunction.Tests\` | Unit tests. Run with `dotnet test` from this folder. |

`bin\`, `obj\` and `local.settings.json` are build or local-only files and don't need copying. No keys are stored in the code; they live in the function app's settings.

## Before you start

- Check that you're allowed to create **Azure OpenAI** resources in the work subscription.
- Check that the subscription has **GPT-4o quota** in the region (UK South). The Standard (regional) deployment type worked when Global Standard had no quota.
- Check that **custom connectors** are allowed by your Power Platform data policies. Custom connectors need a **Power Automate Premium** licence.
- Install the [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) and the .NET 8 SDK on the machine you publish from.

## 1. Azure OpenAI

1. In the Azure portal, create an **Azure OpenAI** resource:
   - Region: **UK South**
   - Pricing tier: **S0**
   - If creation fails with `MissingSubscriptionRegistration`, register the provider first: **Subscription → Resource providers → Microsoft.CognitiveServices → Register**.
2. Open the resource in **Azure AI Foundry** and deploy a model:
   - Model: **gpt-4o** (version 2024-11-20)
   - Deployment name: `gpt-4o`
   - Deployment type: **Standard**
   - Capacity: about **50** (thousand tokens per minute)
3. From **Keys and Endpoint**, note the **endpoint** and one **key**.

gpt-4o 2024-11-20 is a legacy model due to retire in April 2027. A newer model works too, as long as it accepts images and PDFs. Set `AZURE_OPENAI_DEPLOYMENT` to its deployment name and re-test with sample documents.

## 2. Function app

1. Create a **Function App**:
   - Hosting plan: **Consumption**
   - Operating system: **Linux**
   - Runtime stack: **.NET**, version **8 (LTS), isolated worker model**
   - Region: **UK South**
   - Let it create a new storage account and turn on **Application Insights**.
2. Under **Settings → Environment variables → App settings**, add:

| Name | Value |
|---|---|
| `AZURE_OPENAI_ENDPOINT` | the endpoint from step 1, e.g. `https://<openai-name>.openai.azure.com/` |
| `AZURE_OPENAI_API_KEY` | the key from step 1 |
| `AZURE_OPENAI_DEPLOYMENT` | `gpt-4o` |
| `AZURE_OPENAI_API_VERSION` | `2025-01-01-preview` |

   Optional, only to change the cost estimate (defaults shown):

| Name | Default |
|---|---|
| `MODEL_INPUT_COST_PER_MILLION` | `2.75` |
| `MODEL_OUTPUT_COST_PER_MILLION` | `11.00` |
| `MODEL_COST_CURRENCY` | `USD` |

3. Choose **Apply**, then **Confirm**.

.NET 8 support ends on 10 November 2026. Plan an upgrade to .NET 10.

## 3. Publish the code

From the `DocumentTextFunction` folder:

```powershell
az login
func azure functionapp publish <work-app-name> --dotnet-isolated
```

The function app should then list three functions: **CheckChanges**, **ExtractText** and **ReadChangeRequest**.

Copy the key from **Functions → App keys → default**. The connector uses it in step 4. The app key covers every function; a function key only covers its own function.

## 4. Custom connector

1. In Power Automate, go to **Custom connectors → New custom connector → Create from blank**, and name it **DocumentProcessor**.
2. Turn on the **Swagger editor** and paste the Swagger below, replacing `<work-app-name>` in the `host` line.
3. Turn the Swagger editor off and choose **Create connector**.
4. On **Test**, choose **New connection** and paste the app key from step 3.

```yaml
swagger: '2.0'
info:
  title: DocumentProcessor
  description: Extracts document text and checks change requests against a live document
  version: '1.0'
host: <work-app-name>.azurewebsites.net
basePath: /
schemes:
  - https
consumes:
  - application/json
produces:
  - application/json
paths:
  /api/ExtractText:
    post:
      summary: Extract text
      operationId: ExtractText
      description: Extract text
      parameters:
        - name: body
          in: body
          required: true
          schema:
            type: object
            properties:
              fileName:
                type: string
                description: fileName
              contentBase64:
                type: string
                description: contentBase64
      responses:
        default:
          description: default
          schema:
            type: object
            properties:
              fileName:
                type: string
              text:
                type: string
              warning:
                type: string
  /api/CheckChanges:
    post:
      summary: Check changes
      operationId: CheckChanges
      description: Compare a change request with the live document
      parameters:
        - name: body
          in: body
          required: true
          schema:
            type: object
            properties:
              requestFileName:
                type: string
                description: requestFileName
              requestContentBase64:
                type: string
                description: requestContentBase64
              liveFileName:
                type: string
                description: liveFileName
              liveContentBase64:
                type: string
                description: liveContentBase64
      responses:
        default:
          description: default
          schema:
            type: object
            properties:
              docRef:
                type: string
              summary:
                type: string
              status:
                type: string
              outcome:
                type: string
              changePercentCompleted:
                type: integer
              confidence:
                type: integer
              confidenceReason:
                type: string
              model:
                type: string
              promptTokens:
                type: integer
              completionTokens:
                type: integer
              totalTokens:
                type: integer
              estimatedCost:
                type: number
              costCurrency:
                type: string
              changes:
                type: array
                items:
                  type: object
                  properties:
                    request:
                      type: string
                    field:
                      type: string
                    oldValue:
                      type: string
                    newValue:
                      type: string
                    expectedText:
                      type: string
                    result:
                      type: string
                    notes:
                      type: string
definitions: {}
parameters: {}
responses: {}
security:
  - api_key: []
securityDefinitions:
  api_key:
    type: apiKey
    in: query
    name: code
tags: []
```

## 5. Flow

1. **Trigger:** SharePoint **When a file is created** on the change-request library.
2. **Get file content** for the change request (the trigger file).
3. **Get file content** for the live document.
4. **DocumentProcessor → Check changes**:

| Field | Value |
|---|---|
| requestFileName | the change-request file name, including its extension (`.pdf`, `.docx`, `.rtf`, `.xlsx`, `.png`, ...) |
| requestContentBase64 | `body('Get_file_content')?['$content']` |
| liveFileName | the live file name, including its extension |
| liveContentBase64 | `body('Get_file_content_2')?['$content']` |

   Use your real action names if they differ. Pass `$content` as it is; it's already base64, so don't wrap it in `base64()`. If the live file comes from an **Apply to each** over a single file, `body('Get_file_content_1')?['$content']` works inside or after the loop. Don't use `first()`, because the body is an object, not an array.

5. **Parse JSON** with Content set to the body of **Check changes**, using this schema (or generate it from a sample run):

```json
{
  "type": "object",
  "properties": {
    "docRef": { "type": "string" },
    "summary": { "type": "string" },
    "status": { "type": "string" },
    "outcome": { "type": "string" },
    "changePercentCompleted": { "type": "integer" },
    "confidence": { "type": "integer" },
    "confidenceReason": { "type": "string" },
    "model": { "type": "string" },
    "promptTokens": { "type": "integer" },
    "completionTokens": { "type": "integer" },
    "totalTokens": { "type": "integer" },
    "estimatedCost": { "type": "number" },
    "costCurrency": { "type": "string" },
    "changes": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "request": { "type": "string" },
          "field": { "type": "string" },
          "oldValue": { "type": "string" },
          "newValue": { "type": "string" },
          "expectedText": { "type": "string" },
          "result": { "type": "string" },
          "notes": { "type": "string" }
        }
      }
    }
  }
}
```

6. **Use the result:**
   - Overall outcome: `body('Parse_JSON')?['status']` (`PASS`, `FAIL` or `PARTIAL`)
   - Summary: `body('Parse_JSON')?['summary']`
   - Document reference: `body('Parse_JSON')?['docRef']`
   - Percent of changes applied: `body('Parse_JSON')?['changePercentCompleted']`
   - **Apply to each** over `body('Parse_JSON')?['changes']`; inside it, use `items('Apply_to_each')?['field']`, `['oldValue']`, `['newValue']`, `['result']` and `['notes']`.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| 401 from the connector | The connection uses a function key or the old app's key. Create a new connection with **App keys → default**. |
| "The file content is not valid base64." | The file content isn't being passed straight from **Get file content** `$content`. |
| 422 "Vision is not configured on the function app." | `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY` or `AZURE_OPENAI_DEPLOYMENT` is missing. |
| 422 with an Azure OpenAI error (e.g. `DeploymentNotFound`) | `AZURE_OPENAI_DEPLOYMENT` doesn't match the deployment name, or the endpoint or key is wrong. |
| 400 "The documents could not be checked." | Unexpected error. Check `exceptions` in Application Insights. |
| `InsufficientQuota` when deploying the model | Use the **Standard** deployment type, or request quota for the region. |

Logs are in the function app's **Application Insights → Logs** (`traces` and `exceptions` tables).

## After moving

Delete or rotate the keys on the old personal resources, and delete the old resource group once the work version is running.
