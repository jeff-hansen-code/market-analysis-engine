resource "azurerm_windows_function_app" "func" {
  name                = "mae-${local.workspace}-func-${local.name_suffix} "
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
