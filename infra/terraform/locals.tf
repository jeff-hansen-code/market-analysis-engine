locals {
  location  = var.location
  workspace = terraform.workspace

  app_settings = {
    FUNCTIONS_WORKER_RUNTIME = "dotnet"
    WEBSITE_RUN_FROM_PACKAGE = "1"
  }

}