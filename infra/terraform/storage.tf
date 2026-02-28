# Storage account for Functions / general use
# NOTE: storage account name must be globally unique, all lowercase, 3–24 chars.
resource "azurerm_storage_account" "funcsa" {
  name                     = "mae${local.workspace}funcsa${local.name_suffix}"
  resource_group_name      = azurerm_resource_group.rg.name
  location                 = azurerm_resource_group.rg.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
}


resource "azurerm_storage_container" "deploy" {
  name                  = "azure-pipelines-deploy"
  storage_account_name  = azurerm_storage_account.funcsa.name
  container_access_type = "private"
}

