# Alfred - Personal Assistant

## Deployment

Deploy to Azure using the Azure Functions Core Tools CLI:

```bash
cd src/Alfred.Functions
func azure functionapp publish func-matt-scerri-alfred-prod-westeu-001
```

Requires `azure-functions-core-tools@4` installed via npm. The `func` CLI handles building, packaging, and deploying correctly (including the `.azurefunctions` folder required by .NET isolated worker).

Do NOT use manual zip deploy — `Compress-Archive` on Windows skips dotfiles like `.azurefunctions`.
