resource "azurerm_role_assignment" "pipeline_blob_contributor" {
  scope                = azurerm_storage_account.funcsa.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = var.pipeline_service_principal_object_id
}

resource "azurerm_role_assignment" "func_blob_reader" {
  scope                = azurerm_storage_account.funcsa.id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = azurerm_linux_function_app.func.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_kv_secrets_user" {
  scope                = data.azurerm_key_vault.basekv.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_function_app.func.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_storage_queue" {
  scope                = azurerm_storage_account.funcsa.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_linux_function_app.func.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_storage_table" {
  scope                = azurerm_storage_account.funcsa.id
  role_definition_name = "Storage Table Data Contributor"
  principal_id         = azurerm_linux_function_app.func.identity[0].principal_id
}

resource "azurerm_role_assignment" "func_storage_blob_contrib" {
  scope                = azurerm_storage_account.funcsa.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_linux_function_app.func.identity[0].principal_id
}