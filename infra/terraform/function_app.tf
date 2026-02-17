resource "azurerm_linux_function_app" "func" { #$ azurerm_windows_function_app    for Windows function app instead
  name                = "mae${local.workspace}func${local.name_suffix}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location

  service_plan_id            = azurerm_service_plan.plan.id
  storage_account_name       = azurerm_storage_account.funcsa.name
  storage_account_access_key = azurerm_storage_account.funcsa.primary_access_key

  site_config {
    application_stack {
      dotnet_version = "8.0" #  azurerm provider expects "8.0" on Linux, and "v8.0" on Windows in many examples
    }
  }
  identity {
    type = "SystemAssigned"
  }
  app_settings = local.app_settings
}

resource "azurerm_role_assignment" "func_kv_secrets_user" {
  scope                = data.azurerm_key_vault.basekv.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_function_app.func.identity[0].principal_id
}


