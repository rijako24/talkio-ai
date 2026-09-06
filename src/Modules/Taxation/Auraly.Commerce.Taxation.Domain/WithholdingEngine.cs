namespace Auraly.Commerce.Taxation.Domain;

public enum WithholdingKind { IncomeTax, Vat, IndustryCommerce }
public enum WithholdingDirection { Purchase, Sale }
public enum WithholdingBaseKind { TaxExclusiveAmount, VatAmount }
public enum WithholdingRecognitionMoment { Accrual, Payment }

public static class TaxResponsibilityCodes
{
    public const string IncomeTaxSelfWithholder = "O-15";
}

public sealed record WithholdingRule(
    Guid RuleId,
    Guid BusinessId,
    int Version,
    string Code,
    string Name,
    WithholdingKind Kind,
    WithholdingDirection Direction,
    WithholdingRecognitionMoment Moment,
    WithholdingBaseKind BaseKind,
    string? ConceptCode,
    string? JurisdictionCode,
    decimal Rate,
    decimal MinimumBase,
    IReadOnlySet<string> RequiredResponsibilities,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsActive)
{
    public static WithholdingRule Create(
        Guid ruleId, Guid businessId, int version, string code, string name,
        WithholdingKind kind, WithholdingDirection direction,
        WithholdingRecognitionMoment moment, WithholdingBaseKind baseKind,
        string? conceptCode, string? jurisdictionCode, decimal rate, decimal minimumBase,
        IEnumerable<string>? requiredResponsibilities, DateOnly effectiveFrom,
        DateOnly? effectiveTo, bool isActive)
    {
        if (ruleId == Guid.Empty || businessId == Guid.Empty)
            throw new WithholdingRuleException("RuleId and BusinessId are required.");
        if (version < 1) throw new WithholdingRuleException("Version must be positive.");
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 32)
            throw new WithholdingRuleException("Code is required and cannot exceed 32 characters.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new WithholdingRuleException("Name is required and cannot exceed 120 characters.");
        if (rate is <= 0 or > 100)
            throw new WithholdingRuleException("Rate must be greater than zero and at most 100.");
        if (minimumBase < 0) throw new WithholdingRuleException("MinimumBase cannot be negative.");
        if (effectiveTo < effectiveFrom)
            throw new WithholdingRuleException("EffectiveTo cannot precede EffectiveFrom.");
        if (kind == WithholdingKind.Vat && baseKind != WithholdingBaseKind.VatAmount)
            throw new WithholdingRuleException("ReteIVA must use the VAT amount as its taxable base.");
        if (kind != WithholdingKind.Vat && baseKind == WithholdingBaseKind.VatAmount)
            throw new WithholdingRuleException("Only ReteIVA can use the VAT amount as its taxable base.");
        var normalizedConcept = Normalize(conceptCode);
        var normalizedJurisdiction = Normalize(jurisdictionCode);
        if (normalizedConcept?.Length > 32)
            throw new WithholdingRuleException("ConceptCode cannot exceed 32 characters.");
        if (normalizedJurisdiction?.Length > 16)
            throw new WithholdingRuleException("JurisdictionCode cannot exceed 16 characters.");
        var responsibilities = (requiredResponsibilities ?? [])
            .Select(Normalize).Where(value => value is not null).Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (responsibilities.Count > 20 || responsibilities.Any(value => value.Length > 32))
            throw new WithholdingRuleException("Tax responsibilities must contain at most 20 values of 32 characters.");
        if (kind == WithholdingKind.IndustryCommerce && string.IsNullOrWhiteSpace(jurisdictionCode))
            throw new WithholdingRuleException("ReteICA requires a jurisdiction.");

        return new WithholdingRule(
            ruleId, businessId, version, code.Trim().ToUpperInvariant(), name.Trim(), kind, direction,
            moment, baseKind, normalizedConcept, normalizedJurisdiction, rate, minimumBase,
            responsibilities, effectiveFrom, effectiveTo, isActive);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}

public sealed record WithholdingCalculationContext(
    Guid BusinessId,
    WithholdingDirection Direction,
    WithholdingRecognitionMoment Moment,
    Guid CounterpartyId,
    string? ConceptCode,
    string? JurisdictionCode,
    decimal TaxExclusiveAmount,
    decimal VatAmount,
    DateTimeOffset OccurredAt,
    bool AppliesWithholding,
    IReadOnlySet<string> CounterpartyResponsibilities,
    IReadOnlySet<Guid> PreviouslyRecognizedRuleIds);

public sealed record WithholdingCalculationLine(
    Guid RuleId, int RuleVersion, string RuleCode, string Name, WithholdingKind Kind,
    WithholdingBaseKind BaseKind, decimal TaxableBase, decimal Rate, decimal Amount,
    string? JurisdictionCode);

public sealed record WithholdingCalculation(
    decimal GrossAmount, decimal WithholdingTotal, decimal NetAmount,
    IReadOnlyList<WithholdingCalculationLine> Lines)
{
    public void EnsureBalanced()
    {
        if (GrossAmount - WithholdingTotal != NetAmount ||
            Lines.Sum(line => line.Amount) != WithholdingTotal)
            throw new InvalidOperationException("The withholding calculation does not reconcile.");
    }
}

public sealed class WithholdingEngine
{
    public WithholdingCalculation Calculate(
        WithholdingCalculationContext context, IEnumerable<WithholdingRule> candidateRules)
    {
        if (context.BusinessId == Guid.Empty || context.CounterpartyId == Guid.Empty)
            throw new WithholdingRuleException("Business and counterparty are required.");
        if (context.TaxExclusiveAmount < 0 || context.VatAmount < 0)
            throw new WithholdingRuleException("Tax bases cannot be negative.");

        if (!context.AppliesWithholding)
            return new WithholdingCalculation(
                Money(context.TaxExclusiveAmount + context.VatAmount), 0,
                Money(context.TaxExclusiveAmount + context.VatAmount), []);

        var date = DateOnly.FromDateTime(context.OccurredAt.UtcDateTime);
        var lines = candidateRules
            .Where(rule => Applies(rule, context, date))
            .OrderBy(rule => rule.Kind).ThenBy(rule => rule.Code, StringComparer.Ordinal)
            .Select(rule => CalculateLine(rule, context))
            .Where(line => line.Amount > 0)
            .ToArray();
        var gross = Money(context.TaxExclusiveAmount + context.VatAmount);
        var withheld = Money(lines.Sum(line => line.Amount));
        if (withheld > gross)
            throw new WithholdingRuleException("Withholdings cannot exceed the document gross amount.");
        var result = new WithholdingCalculation(gross, withheld, Money(gross - withheld), lines);
        result.EnsureBalanced();
        return result;
    }

    private static bool Applies(WithholdingRule rule, WithholdingCalculationContext context, DateOnly date)
    {
        if (!rule.IsActive || rule.BusinessId != context.BusinessId ||
            rule.Direction != context.Direction || rule.Moment != context.Moment)
            return false;
        if (rule.Kind == WithholdingKind.IncomeTax &&
            context.Direction == WithholdingDirection.Purchase &&
            HasResponsibility(
                context.CounterpartyResponsibilities,
                TaxResponsibilityCodes.IncomeTaxSelfWithholder))
            return false;
        if (date < rule.EffectiveFrom || rule.EffectiveTo is not null && date > rule.EffectiveTo)
            return false;
        if (context.PreviouslyRecognizedRuleIds.Contains(rule.RuleId)) return false;
        if (rule.ConceptCode is not null && !Same(rule.ConceptCode, context.ConceptCode)) return false;
        if (rule.JurisdictionCode is not null && !Same(rule.JurisdictionCode, context.JurisdictionCode)) return false;
        return rule.RequiredResponsibilities.All(context.CounterpartyResponsibilities.Contains);
    }

    private static WithholdingCalculationLine CalculateLine(
        WithholdingRule rule, WithholdingCalculationContext context)
    {
        var basis = Money(rule.BaseKind == WithholdingBaseKind.VatAmount
            ? context.VatAmount : context.TaxExclusiveAmount);
        var amount = basis < rule.MinimumBase ? 0 : Money(basis * rule.Rate / 100m);
        return new(rule.RuleId, rule.Version, rule.Code, rule.Name, rule.Kind, rule.BaseKind,
            basis, rule.Rate, amount, rule.JurisdictionCode);
    }

    private static bool Same(string left, string? right) =>
        string.Equals(left, right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool HasResponsibility(IEnumerable<string> responsibilities, string code) =>
        responsibilities.Any(value => Same(code, value));
    private static decimal Money(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}

public sealed class WithholdingRuleException(string message) : Exception(message);
