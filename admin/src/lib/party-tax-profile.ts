import type { WithholdingRule } from "@/services/api/taxation";

export const incomeTaxSelfWithholderResponsibilityCode = "O-15";

export function counterpartyWithholdingRuleIsCandidate(
  rule: WithholdingRule,
  role: "customer" | "supplier",
  responsibilities: ReadonlySet<string>,
  jurisdictionCode: string | null,
) {
  const direction = role === "customer" ? "Sale" : "Purchase";
  if (!rule.isActive || rule.direction !== direction) return false;
  if (role === "supplier" && rule.kind === "IncomeTax" && hasResponsibility(
    responsibilities,
    incomeTaxSelfWithholderResponsibilityCode,
  )) return false;
  return rule.requiredResponsibilities.every((code) => hasResponsibility(responsibilities, code))
    && (!rule.jurisdictionCode || rule.jurisdictionCode === jurisdictionCode);
}

export function isIncomeTaxSelfWithholdingSupplier(
  role: "customer" | "supplier",
  responsibilities: ReadonlySet<string>,
) {
  return role === "supplier" && hasResponsibility(
    responsibilities,
    incomeTaxSelfWithholderResponsibilityCode,
  );
}

function hasResponsibility(responsibilities: ReadonlySet<string>, code: string) {
  return [...responsibilities].some((value) =>
    value.localeCompare(code, undefined, { sensitivity: "accent" }) === 0);
}
