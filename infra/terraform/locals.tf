locals {
  location  = var.location
  workspace = terraform.workspace
  supabase_api_url = var.supabase_api_url
  kv_uri = "https://${data.azurerm_key_vault.basekv.name}.vault.azure.net/"
  name_suffix = var.name_suffix

  
  app_settings = {
    FUNCTIONS_WORKER_RUNTIME              = "dotnet-isolated"
    FUNCTIONS_EXTENSION_VERSION           = "~4"
    WEBSITE_RUN_FROM_PACKAGE              = "1"
    APPINSIGHTS_INSTRUMENTATIONKEY        = azurerm_application_insights.ai.instrumentation_key
    APPLICATIONINSIGHTS_CONNECTION_STRING = azurerm_application_insights.ai.connection_string

    FMP_API_KEY                 = "@Microsoft.KeyVault(SecretUri=${local.kv_uri}secrets/FMP-API-KEY)"
    SUPABASE_SERVICE_ROLE_KEY   = "@Microsoft.KeyVault(SecretUri=${local.kv_uri}secrets/SUPABASE-SERVICE-ROLE-KEY)"  
    SUPABASE_API_URL            = var.supabase_api_url
  }

}