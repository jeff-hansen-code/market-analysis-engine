variable "location" {
  description = "Azure region for dev environment"
  type        = string
  default     = "centralus"
}

variable "supabase_api_url" {
  description = "Supabase API URL (e.g. https://your-project.supabase.co)"
  type        = string
  default     = "https://barrkyhggfsjrcllvoeo.supabase.co"
}

variable "name_suffix" {
  type = string
}

