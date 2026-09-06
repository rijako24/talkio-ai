import assert from "node:assert/strict";
import test from "node:test";

import {
  identificationTypeForPartyChange,
  identificationTypesForParty,
} from "./party-identification";

const options = [
  { id: "cc", code: "CC", label: "Cédula", description: "NaturalPerson", sortOrder: 10 },
  { id: "nit", code: "NIT", label: "NIT", description: "Organization", sortOrder: 20 },
  { id: "ppt", code: "PPT", label: "Permiso por Protección Temporal", description: "NaturalPerson", sortOrder: 30 },
];

test("defaults organizations to the catalog NIT", () => {
  assert.equal(identificationTypeForPartyChange("CC", "Organization", options), "NIT");
});

test("keeps a valid natural-person identification and exposes PPT", () => {
  assert.equal(identificationTypeForPartyChange("PPT", "NaturalPerson", options), "PPT");
  assert.deepEqual(
    identificationTypesForParty(options, "NaturalPerson").map((option) => option.code),
    ["CC", "PPT"],
  );
});

test("returns to the natural-person default when changing from an organization", () => {
  assert.equal(identificationTypeForPartyChange("NIT", "NaturalPerson", options), "CC");
});
