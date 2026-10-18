# TFLint configuration for infra/terraform.
#
# Run from infra/terraform (CI does exactly this):
#   tflint --init   --config "$PWD/../../.config/tflint.hcl"
#   tflint --recursive --config "$PWD/../../.config/tflint.hcl"
#
# On Windows PowerShell, from infra\terraform:
#   tflint --init      --config "$PWD\..\..\.config\tflint.hcl"
#   tflint --recursive --config "$PWD\..\..\.config\tflint.hcl"
#
# The azurerm ruleset catches values Azure would reject only at apply time
# (invalid SKU names, unsupported regions, bad TLS values) -- the class of
# error terraform validate cannot see.

config {
  call_module_type = "local"
}

plugin "terraform" {
  enabled = true
  preset  = "recommended"
}

plugin "azurerm" {
  enabled = true
  version = "0.32.0"
  source  = "github.com/terraform-linters/tflint-ruleset-azurerm"
}

rule "terraform_naming_convention" {
  enabled = true
  format  = "snake_case"
}

rule "terraform_documented_variables" {
  enabled = true
}

rule "terraform_documented_outputs" {
  enabled = true
}

rule "terraform_required_version" {
  enabled = true
}

rule "terraform_required_providers" {
  enabled = true
}

rule "terraform_unused_declarations" {
  enabled = true
}

rule "terraform_typed_variables" {
  enabled = true
}

# Disabled deliberately (decide again if circumstances change):
#
# prevent_destroy cannot vary per environment in Terraform -- the modules are
# shared by dev (which must be disposable) and prod. Production deletion is
# guarded instead by the reviewed plan in the "prod" GitHub Environment, Key
# Vault purge protection, blob soft delete/versioning, SQL PITR/LTR backups,
# and (recommended, docs/operations/infrastructure.md) a CanNotDelete lock on
# the prod resource group.
rule "azurerm_resources_missing_prevent_destroy" {
  enabled = false
}

# Auto-heal "Recycle" restarts the worker process. For a Blazor Server app
# that drops every user's live circuit (unsaved review work) at once, so it is
# not switched on blind. /health + health_check_eviction already removes a
# genuinely unhealthy instance from rotation when there is more than one.
rule "azurerm_app_service_missing_auto_heal_setting" {
  enabled = false
}
