import assert from "node:assert/strict";
import test from "node:test";

import type { WithholdingRule } from "@/services/api/taxation";
import { counterpartyWithholdingRuleIsCandidate } from "./party-tax-profile";

const baseRule: WithholdingRule = {
  ruleId: "rule", businessId: "business", version: 1, code: "RF-COMPRA",
  name: "Retefuente compra", kind: "IncomeTax", direction: "Purchase",
  moment: "Accrual", baseKind: "TaxExclusiveAmount", conceptCode: null,
  jurisdictionCode: null, rate: 2.5, minimumBase: 0,
  requiredResponsibilities: [], effectiveFrom: "2026-01-01", effectiveTo: null,
  isActive: true,
};

test("does not present purchase income tax as applicable to a self-withholding supplier", () => {
  assert.equal(counterpartyWithholdingRuleIsCandidate(
    baseRule, "supplier", new Set(["o-15"]), null,
  ), false);
});

test("keeps other supplier withholdings and sale income tax candidates", () => {
  assert.equal(counterpartyWithholdingRuleIsCandidate(
    { ...baseRule, kind: "Vat", baseKind: "VatAmount" },
    "supplier", new Set(["O-15"]), null,
  ), true);
  assert.equal(counterpartyWithholdingRuleIsCandidate(
    { ...baseRule, direction: "Sale" },
    "customer", new Set(["O-15"]), null,
  ), true);
});
