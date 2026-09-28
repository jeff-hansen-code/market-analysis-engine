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
  storage_account_id    = azurerm_storage_account.funcsa.id
  container_access_type = "private"
}

resource "azurerm_storage_account" "mae_datalake" {
  name                = "maedatalake001" # Must be globally unique, lowercase only
  resource_group_name = azurerm_resource_group.mae.name
  location            = azurerm_resource_group.mae.location

  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"

  # Makes this Azure Data Lake Storage Gen2
  is_hns_enabled = true

  min_tls_version = "TLS1_2"

  public_network_access_enabled   = true
  allow_nested_items_to_be_public = false

  tags = {
    project     = "market-analysis-engine"
    environment = "dev"
    purpose     = "adls-gen2"
  }
}

resource "azurerm_storage_container" "raw" {
  name                  = "raw"
  storage_account_id    = azurerm_storage_account.mae_datalake.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "staging" {
  name                  = "staging"
  storage_account_id    = azurerm_storage_account.mae_datalake.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "curated" {
  name                  = "curated"
  storage_account_id    = azurerm_storage_account.mae_datalake.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "ml" {
  name                  = "ml"
  storage_account_id    = azurerm_storage_account.mae_datalake.id
  container_access_type = "private"
}