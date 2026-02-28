# Dev resource group
resource "azurerm_resource_group" "rg" {
  name     = "mae-${local.workspace}-rg"
  location = local.location
}

resource "azurerm_service_plan" "plan" {
  name                = "mae-${local.workspace}-plan"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location

  os_type  = "Linux"
  sku_name = "Y1" # Consumption
}


resource "azurerm_key_vault_access_policy" "func_secrets" {
  key_vault_id = data.azurerm_key_vault.basekv.id
  tenant_id    = data.azurerm_key_vault.basekv.tenant_id

  object_id = azurerm_linux_function_app.func.identity[0].principal_id
  # if Windows function app:
  # object_id = azurerm_windows_function_app.func.identity[0].principal_id

  secret_permissions = ["Get", "List"]
}