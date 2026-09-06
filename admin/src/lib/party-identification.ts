import type { ReferenceOption } from "@/services/api/reference-options";

export type PartyType = "NaturalPerson" | "Organization";

export function identificationTypesForParty(
  options: readonly ReferenceOption[],
  partyType: PartyType,
) {
  return options.filter((option) => option.description === partyType);
}

export function identificationTypeForPartyChange(
  currentCode: string,
  partyType: PartyType,
  options: readonly ReferenceOption[],
) {
  const available = identificationTypesForParty(options, partyType);
  if (available.some((option) => option.code === currentCode)) return currentCode;
  return available[0]?.code ?? (partyType === "Organization" ? "NIT" : "CC");
}
