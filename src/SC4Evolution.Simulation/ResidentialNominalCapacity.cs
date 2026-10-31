using System;
using System.Security.Cryptography;
using System.Text;

namespace SC4Evolution.Simulation
{
    // Supplied by the native adapter's ItemClass predicates, never by SC4 wealth.
    // Other means remaining supported residential categories using the verified
    // ResidentialBuildingAI, never unknown/custom AI or unsupported ploppables.
    public enum ResidentialNominalCategory : byte { LowGeneric = 1, LowEco = 2, Other = 3 }

    public sealed class ResidentialHomeCountBounds
    {
        public long MinimumHomes { get; }
        public long MaximumHomes { get; }
        public long MinimumNominalQ { get; }
        public long MaximumNominalQ { get; }

        internal ResidentialHomeCountBounds(long minimumHomes, long maximumHomes, long quartersPerHome)
        {
            MinimumHomes = minimumHomes;
            MaximumHomes = maximumHomes;
            MinimumNominalQ = checked(minimumHomes * quartersPerHome);
            MaximumNominalQ = checked(maximumHomes * quartersPerHome);
        }
    }

    /// <summary>
    /// Project statistical adapter, not an SC4 exemplar or expected occupied population.
    /// Q is one quarter of a nominal person. The coefficients describe offered
    /// Single/Family reasons, not completed matches, actual residents or a ceiling.
    /// Persist a committed generation's Q in its owner; do not recalculate from occupancy.
    /// This pure model neither samples RNG nor allocates native objects/IDs.
    /// </summary>
    public static class ResidentialNominalCapacity
    {
        public const string PolicyName = "NativeArrivalNominalV1";
        public const uint PolicyVersion = 1;
        public const long QuartersPerPerson = 4;
        public const string SourceVersion = "Cities:Skylines1.Windows.1.21.1-f9";

        // Exact UTF-8, no BOM/newline. Changing coefficients, classification or
        // rounding/domain semantics requires a new policy identity.
        public const string CanonicalPolicyMaterial =
            "SC4Evolution.ResidentialNominalCapacity;policy=NativeArrivalNominalV1;version=1" +
            ";kind=project-statistical-adapter;source=Cities:Skylines1.Windows.1.21.1-f9" +
            ";sources=ResidentialBuildingAI.CalculateHomeCount,ResidentialBuildingAI.GetHomeReason,OutsideConnectionAI.StartConnectionTransferImpl" +
            ";unit=quarter-nominal-person;quarters-per-person=4;home-unit=native-HOME-household" +
            ";category=LowGeneric|LowEco|Other;category-independent-of-wealth" +
            ";Other=supported-residential-ResidentialBuildingAI;unknown-custom-AI-unsupported-ploppable=reject" +
            ";offered-family-percent=90|90|10;offered-single-percent=10|10|90" +
            ";offered-family-probability=9/10|9/10|1/10" +
            ";single-persons=1;family-persons-uniform-inclusive=2..5;family-mean=7/2" +
            ";nominalQ-per-HOME=13|13|5;meaning=offered-reasons-only;not=matches,occupancy,ceiling,SC4-building-profile" +
            ";native-level-index=0..4;dimensions=1..255;low-density=20,25,30,35,40;other-density=60,100,130,150,160" +
            ";A=width*length*density;Hmin=max(1,trunc(A/100));Hmax=max(1,trunc((A+99)/100))" +
            ";known-HOME-count=positive-int64;nominalQ=HOME-count*coefficient;arithmetic=checked-int64;rounding-per-home=none";

        private static readonly byte[] Hash = ComputeHash();
        public static byte[] PolicyHash { get { return (byte[])Hash.Clone(); } }

        public static ResidentialHomeCountBounds GetHomeCountBounds(ResidentialNominalCategory category,
            int nativeLevel, int width, int length)
        {
            long quarters = GetQuartersPerHome(category);
            if (nativeLevel < 0 || nativeLevel > 4) throw new ArgumentOutOfRangeException("nativeLevel");
            if (width < 1 || width > 255) throw new ArgumentOutOfRangeException("width");
            if (length < 1 || length > 255) throw new ArgumentOutOfRangeException("length");
            long density;
            if (category == ResidentialNominalCategory.LowGeneric || category == ResidentialNominalCategory.LowEco)
                density = nativeLevel == 0 ? 20L : nativeLevel == 1 ? 25L : nativeLevel == 2 ? 30L : nativeLevel == 3 ? 35L : 40L;
            else
                density = nativeLevel == 0 ? 60L : nativeLevel == 1 ? 100L : nativeLevel == 2 ? 130L : nativeLevel == 3 ? 150L : 160L;
            long areaDensity = checked(checked((long)width * (long)length) * density);
            long minimum = Math.Max(1L, areaDensity / 100L);
            long maximum = Math.Max(1L, checked(areaDensity + 99L) / 100L);
            return new ResidentialHomeCountBounds(minimum, maximum, quarters);
        }

        /// <summary>Use an actual native HOME count after creation; zero is unsupported, not fictitious supply.</summary>
        public static long ComputeNominalQ(ResidentialNominalCategory category, long actualHomeCount)
        {
            long quarters = GetQuartersPerHome(category);
            if (actualHomeCount <= 0) throw new ArgumentOutOfRangeException("actualHomeCount");
            return checked(actualHomeCount * quarters);
        }

        public static long GetQuartersPerHome(ResidentialNominalCategory category)
        {
            switch (category)
            {
                case ResidentialNominalCategory.LowGeneric:
                case ResidentialNominalCategory.LowEco: return 13L;
                case ResidentialNominalCategory.Other: return 5L;
                default: throw new ArgumentOutOfRangeException("category");
            }
        }

        private static byte[] ComputeHash()
        { using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(CanonicalPolicyMaterial)); }
    }
}
