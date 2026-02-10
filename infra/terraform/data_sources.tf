data "azurerm_key_vault" "basekv" {
  name                = "basekv"
  resource_group_name = "BaseRG"
}
