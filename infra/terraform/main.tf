# Dev resource group
resource "azurerm_resource_group" "dev_rg" {
  name     = "mae-${local.workspace}-rg"
  location = local.location
}

# Storage account for Functions / general use
# NOTE: storage account name must be globally unique, all lowercase, 3–24 chars.
resource "azurerm_storage_account" "dev_storage" {
  name                     = "maedevstorage1234"
  resource_group_name      = azurerm_resource_group.dev_rg.name
  location                 = azurerm_resource_group.dev_rg.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
}
