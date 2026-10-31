using System;

namespace SC4Evolution.Simulation.Taxation
{
    /// <summary>Tax account keys (sprint 04): three residential wealth accounts plus the
    /// nine business segments, in the same segment order CityCensus already publishes
    /// (CS$, CS$$, CS$$$, CO$$, CO$$$, IR, ID, IM, IHT). Independent key set from sprint 01
    /// on purpose: each sprint stays reviewable alone; alignment is documented, not coupled.</summary>
    public static class TaxAccounts
    {
        public const int ResidentialAccounts = 3;
        public const int BusinessAccounts = 9;
        public const int Accounts = ResidentialAccounts + BusinessAccounts;

        private static readonly string[] Names = new[]
        {
            "R$", "R$$", "R$$$", "CS$", "CS$$", "CS$$$", "CO$$", "CO$$$", "IR", "ID", "IM", "IHT"
        };

        /// <summary>Residential wealth account 0..2.</summary>
        public static int Residential(int wealth)
        {
            if (wealth < 0 || wealth >= ResidentialAccounts) throw new ArgumentOutOfRangeException("wealth");
            return wealth;
        }

        /// <summary>Business account for segment 0..8 (CityCensus order).</summary>
        public static int Business(int segment)
        {
            if (segment < 0 || segment >= BusinessAccounts) throw new ArgumentOutOfRangeException("segment");
            return ResidentialAccounts + segment;
        }

        public static bool IsResidential(int account)
        {
            Validate(account);
            return account < ResidentialAccounts;
        }

        public static string Name(int account)
        {
            Validate(account);
            return Names[account];
        }

        public static void Validate(int account)
        {
            if (account < 0 || account >= Accounts) throw new ArgumentOutOfRangeException("account");
        }

        public static void ValidateVector<T>(T[] values, string name)
        {
            if (values == null) throw new ArgumentNullException(name);
            if (values.Length != Accounts) throw new ArgumentException("Expected " + Accounts + " tax accounts.", name);
        }
    }

    /// <summary>Demand response of one account to its effective (windowed) rate. The pure
    /// accounting model never owns curve data: the integration wraps the already-tested
    /// TaxDemandModel curves here, and tests inject synthetic curves. Factor 1.0 is neutral.</summary>
    public interface ITaxResponseCurve
    {
        float DemandFactor(int account, int effectiveRatePercent);
    }

    /// <summary>Neutral curve used when no response model is wired yet; keeps the meter at
    /// 1.0 instead of inventing a response.</summary>
    public sealed class NeutralTaxResponseCurve : ITaxResponseCurve
    {
        public static readonly NeutralTaxResponseCurve Instance = new NeutralTaxResponseCurve();
        public float DemandFactor(int account, int effectiveRatePercent)
        {
            TaxAccounts.Validate(account);
            TaxRateValidation.Validate(effectiveRatePercent);
            return 1f;
        }
    }

    public static class TaxRateValidation
    {
        /// <summary>CS1 exposes whole-percent rates (slider 1..29); the model accepts the
        /// wider 0..100 domain and rejects anything else.</summary>
        public static void Validate(int ratePercent)
        {
            if (ratePercent < 0 || ratePercent > 100)
                throw new ArgumentOutOfRangeException("ratePercent", "Rates live on 0..100 percent.");
        }
    }
}
