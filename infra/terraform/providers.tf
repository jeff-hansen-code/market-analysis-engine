terraform {
  required_version = ">= 1.5.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.26.0"
    }
  }

  backend "azurerm" {
    resource_group_name  = "BaseRG"
    storage_account_name = "basetfsacenus"
    container_name       = "market-analysis-engine"
    key                  = "market-analysis-engine.tfstate"
  }
}

provider "azurerm" {
  features {}
  resource_provider_registrations = ["Microsoft.Resources", "Microsoft.Storage", "Microsoft.Web", "Microsoft.Insights", "Microsoft.KeyVault"]

}



