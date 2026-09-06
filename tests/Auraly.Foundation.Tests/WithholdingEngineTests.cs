using Auraly.Commerce.Taxation.Domain;

namespace Auraly.Foundation.Tests;

public sealed class WithholdingEngineTests
{
    private static readonly Guid BusinessId = Guid.NewGuid();
    private static readonly Guid CounterpartyId = Guid.NewGuid();

    [Fact]
    public void Calculates_income_vat_and_ica_on_their_legal_bases()
    {
        var rules = new[]
        {
            Rule(WithholdingKind.IncomeTax, WithholdingBaseKind.TaxExclusiveAmount, 2.5m),
            Rule(WithholdingKind.Vat, WithholdingBaseKind.VatAmount, 15m),
            Rule(WithholdingKind.IndustryCommerce, WithholdingBaseKind.TaxExclusiveAmount, 0.966m, "11001")
        };
        var result = new WithholdingEngine().Calculate(Context(100_000m, 19_000m, "11001"), rules);

        Assert.Equal(119_000m, result.GrossAmount);
        Assert.Equal(6_316m, result.WithholdingTotal);
        Assert.Equal(112_684m, result.NetAmount);
        Assert.Equal(2_850m, result.Lines.Single(line => line.Kind == WithholdingKind.Vat).Amount);
        result.EnsureBalanced();
    }

    [Fact]
    public void Does_not_apply_below_minimum_without_responsibility_or_wrong_jurisdiction()
    {
        var minimum = Rule(WithholdingKind.IncomeTax, WithholdingBaseKind.TaxExclusiveAmount, 2m,
            minimum: 200_000m, responsibilities: ["O-13"]);
        var ica = Rule(WithholdingKind.IndustryCommerce, WithholdingBaseKind.TaxExclusiveAmount, 1m, "05001");

        var result = new WithholdingEngine().Calculate(Context(100_000m, 19_000m, "11001"), [minimum, ica]);

        Assert.Empty(result.Lines);
        Assert.Equal(result.GrossAmount, result.NetAmount);
    }

    [Fact]
    public void Does_not_recognize_the_same_rule_at_payment_after_accrual()
    {
        var rule = Rule(WithholdingKind.IncomeTax, WithholdingBaseKind.TaxExclusiveAmount, 2.5m);
        var context = Context(100_000m, 19_000m, "11001") with
        {
            Moment = WithholdingRecognitionMoment.Payment,
            PreviouslyRecognizedRuleIds = new HashSet<Guid> { rule.RuleId }
        };

        var result = new WithholdingEngine().Calculate(context, [rule]);

        Assert.Empty(result.Lines);
        Assert.Equal(119_000m, result.NetAmount);
    }

    [Fact]
    public void Applies_only_rules_configured_for_the_recognition_moment()
    {
        var paymentRule = Rule(
            WithholdingKind.IncomeTax, WithholdingBaseKind.TaxExclusiveAmount, 2.5m,
            moment: WithholdingRecognitionMoment.Payment);

        Assert.Empty(new WithholdingEngine().Calculate(
            Context(100_000m, 19_000m, "11001"), [paymentRule]).Lines);
        Assert.Single(new WithholdingEngine().Calculate(
            Context(100_000m, 19_000m, "11001") with { Moment = WithholdingRecognitionMoment.Payment },
            [paymentRule]).Lines);
    }

    [Fact]
    public void Multiple_responsibilities_classify_the_party_but_do_not_define_rates()
    {
        var matching = Rule(
            WithholdingKind.IncomeTax, WithholdingBaseKind.TaxExclusiveAmount, 2.5m,
            responsibilities: ["O-13", "O-23"]);
        var missingOne = Context(100_000m, 19_000m, "11001") with
        {
            CounterpartyResponsibilities = new HashSet<string>(["O-13"], StringComparer.OrdinalIgnoreCase)
        };
        var hasAll = missingOne with
        {
            CounterpartyResponsibilities = new HashSet<string>(["O-13", "O-23"], StringComparer.OrdinalIgnoreCase)
        };

        Assert.Empty(new WithholdingEngine().Calculate(missingOne, [matching]).Lines);
        var applied = Assert.Single(new WithholdingEngine().Calculate(hasAll, [matching]).Lines);
        Assert.Equal(2.5m, applied.Rate);
        Assert.Equal(2_500m, applied.Amount);
    }

    [Theory]
    [InlineData(100_000, 0)]
    [InlineData(10_000_000, 200_000)]
    public void Income_tax_is_not_withheld_from_a_self_withholding_supplier(
        decimal subtotal, decimal minimum)
    {
        var rule = Rule(
            WithholdingKind.IncomeTax, WithholdingBaseKind.TaxExclusiveAmount, 2.5m,
            minimum: minimum);
        var context = Context(subtotal, 0m, "11001") with
        {
            CounterpartyResponsibilities = new HashSet<string>(
                [TaxResponsibilityCodes.IncomeTaxSelfWithholder],
                StringComparer.OrdinalIgnoreCase)
        };

        var result = new WithholdingEngine().Calculate(context, [rule]);

        Assert.Empty(result.Lines);
        Assert.Equal(subtotal, result.NetAmount);
        result.EnsureBalanced();
    }

    [Fact]
    public void Self_withholding_supplier_can_still_have_vat_and_ica_withholdings()
    {
        var rules = new[]
        {
            Rule(WithholdingKind.IncomeTax, WithholdingBaseKind.TaxExclusiveAmount, 2.5m),
            Rule(WithholdingKind.Vat, WithholdingBaseKind.VatAmount, 15m),
            Rule(WithholdingKind.IndustryCommerce, WithholdingBaseKind.TaxExclusiveAmount, 1m, "11001")
        };
        var context = Context(100_000m, 19_000m, "11001") with
        {
            CounterpartyResponsibilities = new HashSet<string>(["o-15"])
        };

        var result = new WithholdingEngine().Calculate(context, rules);

        Assert.DoesNotContain(result.Lines, line => line.Kind == WithholdingKind.IncomeTax);
        Assert.Contains(result.Lines, line => line.Kind == WithholdingKind.Vat);
        Assert.Contains(result.Lines, line => line.Kind == WithholdingKind.IndustryCommerce);
        Assert.Equal(3_850m, result.WithholdingTotal);
        result.EnsureBalanced();
    }

    [Fact]
    public void Self_withholder_status_does_not_suppress_income_tax_withheld_by_a_customer()
    {
        var rule = Rule(
            WithholdingKind.IncomeTax, WithholdingBaseKind.TaxExclusiveAmount, 2.5m,
            direction: WithholdingDirection.Sale);
        var context = Context(100_000m, 0m, "11001") with
        {
            Direction = WithholdingDirection.Sale,
            CounterpartyResponsibilities = new HashSet<string>(
                [TaxResponsibilityCodes.IncomeTaxSelfWithholder])
        };

        var result = new WithholdingEngine().Calculate(context, [rule]);

        Assert.Equal(2_500m, Assert.Single(result.Lines).Amount);
    }

    [Fact]
    public void Rejects_reteiva_configured_on_invoice_subtotal()
    {
        Assert.Throws<WithholdingRuleException>(() => Rule(
            WithholdingKind.Vat, WithholdingBaseKind.TaxExclusiveAmount, 15m));
    }

    private static WithholdingRule Rule(
        WithholdingKind kind, WithholdingBaseKind basis, decimal rate,
        string? jurisdiction = null, decimal minimum = 0, string[]? responsibilities = null,
        WithholdingRecognitionMoment moment = WithholdingRecognitionMoment.Accrual,
        WithholdingDirection direction = WithholdingDirection.Purchase) =>
        WithholdingRule.Create(Guid.NewGuid(), BusinessId, 1, Guid.NewGuid().ToString("N")[..12],
            kind.ToString(), kind, direction, moment, basis, null, jurisdiction,
            rate, minimum, responsibilities, new DateOnly(2026, 1, 1), null, true);

    private static WithholdingCalculationContext Context(
        decimal subtotal, decimal vat, string jurisdiction) => new(
            BusinessId, WithholdingDirection.Purchase, WithholdingRecognitionMoment.Accrual,
            CounterpartyId, null, jurisdiction, subtotal, vat,
            new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero),
            true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<Guid>());
}
