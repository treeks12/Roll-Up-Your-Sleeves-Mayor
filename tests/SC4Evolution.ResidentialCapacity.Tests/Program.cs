using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SC4Evolution.Simulation;

internal static class Program
{
    private static int _checks;
    private static readonly ResidentialNominalCategory[] Categories = {
        ResidentialNominalCategory.LowGeneric, ResidentialNominalCategory.LowEco, ResidentialNominalCategory.Other };

    private static int Main()
    {
        try
        {
            ExhaustNativeHomeRolls(); OfferedReasonModel(); QuarterAccounting(); DomainsAndOverflow(); ImmutableIdentity();
            Console.WriteLine("PASS: " + _checks + " assertions; exhaustive native HOME roll bounds, offered-reason coefficients, exact Q accounting, domains/overflow and immutable identity. Pure adapter only; no game/RNG/occupancy simulation.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }

    private static void ExhaustNativeHomeRolls()
    {
        // Independent oracle: the decompiled CalculateHomeCount evaluates the
        // whole native formula once for each possible Int32(100u) result.
        int[] lowDensities = { 20, 25, 30, 35, 40 }, otherDensities = { 60, 100, 130, 150, 160 };
        foreach (ResidentialNominalCategory category in Categories)
            for (int level = 0; level < 5; level++)
                for (int width = 1; width <= 4; width++)
                    for (int length = 1; length <= 4; length++)
                    {
                        ResidentialHomeCountBounds bounds = ResidentialNominalCapacity.GetHomeCountBounds(category, level, width, length);
                        int density = category == ResidentialNominalCategory.Other ? otherDensities[level] : lowDensities[level];
                        long observedMin = long.MaxValue, observedMax = long.MinValue;
                        for (int roll = 0; roll < 100; roll++)
                        {
                            long actual = Math.Max(100L, checked(checked((long)width * length * density) + roll)) / 100L;
                            Check(actual >= bounds.MinimumHomes && actual <= bounds.MaximumHomes, "native roll lies in bounds");
                            observedMin = Math.Min(observedMin, actual); observedMax = Math.Max(observedMax, actual);
                        }
                        Equal(observedMin, bounds.MinimumHomes, "minimum is attained");
                        Equal(observedMax, bounds.MaximumHomes, "maximum is attained");
                        Equal(ResidentialNominalCapacity.ComputeNominalQ(category, observedMin), bounds.MinimumNominalQ, "minimum Q");
                        Equal(ResidentialNominalCapacity.ComputeNominalQ(category, observedMax), bounds.MaximumNominalQ, "maximum Q");
                    }
        foreach (ResidentialNominalCategory category in Categories)
        {
            ResidentialHomeCountBounds largest = ResidentialNominalCapacity.GetHomeCountBounds(category, 4, 255, 255);
            Equal(category == ResidentialNominalCategory.Other ? 104040L : 26010L, largest.MaximumHomes, "maximum supported footprint");
            Equal(largest.MinimumHomes, largest.MaximumHomes, "divisible largest footprint is deterministic");
        }
    }

    private static void OfferedReasonModel()
    {
        foreach (ResidentialNominalCategory category in Categories)
        {
            // Enumerate equally weighted reason draws (0..9) crossed with all
            // four inclusive family sizes. Singles repeat once per size draw.
            long totalPersons = 0;
            for (int reasonRoll = 0; reasonRoll < 10; reasonRoll++)
                for (int familySize = 2; familySize <= 5; familySize++)
                {
                    bool family = (category != ResidentialNominalCategory.Other) == (reasonRoll != 0);
                    totalPersons += family ? familySize : 1;
                }
            Equal(0L, checked(totalPersons * 4L) % 40L, "mean exactly represents quarters");
            Equal(checked(totalPersons * 4L) / 40L, ResidentialNominalCapacity.GetQuartersPerHome(category), "offered-reason mean coefficient");
        }
    }

    private static void QuarterAccounting()
    {
        Equal(13L, ResidentialNominalCapacity.ComputeNominalQ(ResidentialNominalCategory.LowGeneric, 1), "low one HOME retains quarter");
        Equal(5L, ResidentialNominalCapacity.ComputeNominalQ(ResidentialNominalCategory.Other, 1), "other one HOME retains quarter");
        Equal(52L, ResidentialNominalCapacity.ComputeNominalQ(ResidentialNominalCategory.LowEco, 4), "four low HOME exactly thirteen nominal persons");
        foreach (ResidentialNominalCategory category in Categories)
        {
            long perLotSum = 0, homes = 0;
            for (int lot = 1; lot <= 31; lot++) { perLotSum = checked(perLotSum + ResidentialNominalCapacity.ComputeNominalQ(category, lot)); homes += lot; }
            Equal(ResidentialNominalCapacity.ComputeNominalQ(category, homes), perLotSum, "sum lots without rounding loss");
            Equal(checked(ResidentialNominalCapacity.ComputeNominalQ(category, 3) + ResidentialNominalCapacity.ComputeNominalQ(category, 7)),
                ResidentialNominalCapacity.ComputeNominalQ(category, 10), "partition invariant");
        }
        Equal(18L, checked(ResidentialNominalCapacity.ComputeNominalQ(ResidentialNominalCategory.LowGeneric, 1) +
            ResidentialNominalCapacity.ComputeNominalQ(ResidentialNominalCategory.Other, 1)), "mixed categories retain half person");
    }

    private static void DomainsAndOverflow()
    {
        foreach (ResidentialNominalCategory category in Categories)
        {
            foreach (int bad in new[] { int.MinValue, -1, 5, int.MaxValue })
                Throws<ArgumentOutOfRangeException>(delegate { ResidentialNominalCapacity.GetHomeCountBounds(category, bad, 1, 1); });
            foreach (int bad in new[] { int.MinValue, -1, 0, 256, int.MaxValue })
            {
                Throws<ArgumentOutOfRangeException>(delegate { ResidentialNominalCapacity.GetHomeCountBounds(category, 0, bad, 1); });
                Throws<ArgumentOutOfRangeException>(delegate { ResidentialNominalCapacity.GetHomeCountBounds(category, 0, 1, bad); });
            }
            foreach (long bad in new[] { long.MinValue, -1L, 0L })
                Throws<ArgumentOutOfRangeException>(delegate { ResidentialNominalCapacity.ComputeNominalQ(category, bad); });
            long coefficient = ResidentialNominalCapacity.GetQuartersPerHome(category), maximumHomes = long.MaxValue / coefficient;
            Equal(checked(maximumHomes * coefficient), ResidentialNominalCapacity.ComputeNominalQ(category, maximumHomes), "largest safe Q product");
            Throws<OverflowException>(delegate { ResidentialNominalCapacity.ComputeNominalQ(category, maximumHomes + 1); });
            Throws<OverflowException>(delegate { ResidentialNominalCapacity.ComputeNominalQ(category, long.MaxValue); });
        }
        foreach (ResidentialNominalCategory bad in new[] { (ResidentialNominalCategory)0, (ResidentialNominalCategory)4, (ResidentialNominalCategory)255 })
        {
            Throws<ArgumentOutOfRangeException>(delegate { ResidentialNominalCapacity.GetHomeCountBounds(bad, 0, 1, 1); });
            Throws<ArgumentOutOfRangeException>(delegate { ResidentialNominalCapacity.GetQuartersPerHome(bad); });
            Throws<ArgumentOutOfRangeException>(delegate { ResidentialNominalCapacity.ComputeNominalQ(bad, 1); });
        }
    }

    private static void ImmutableIdentity()
    {
        Type boundsType = typeof(ResidentialHomeCountBounds);
        Check(boundsType.IsSealed, "bounds cannot be subclassed");
        Equal(0L, boundsType.GetConstructors().Length, "no public unvalidated constructor");
        foreach (PropertyInfo property in boundsType.GetProperties()) Check(!property.CanWrite, "bounds have no setters");
        foreach (FieldInfo field in boundsType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            Check(field.IsInitOnly, "bounds backing fields readonly");
        byte[] first = ResidentialNominalCapacity.PolicyHash;
        Equal(32L, first.Length, "SHA256 length");
        byte saved = first[0]; first[0] ^= 0xff;
        Equal(saved, ResidentialNominalCapacity.PolicyHash[0], "hash getter defensive copy");
        using (SHA256 sha = SHA256.Create())
        {
            byte[] expected = sha.ComputeHash(Encoding.UTF8.GetBytes(ResidentialNominalCapacity.CanonicalPolicyMaterial));
            byte[] actual = ResidentialNominalCapacity.PolicyHash;
            for (int i = 0; i < expected.Length; i++) Equal(expected[i], actual[i], "canonical UTF8 hash");
            Equal("1b496dfd9c278bd5c405a9af514279c659a8c751e6bcf9a66316ac3af7e2d2ce",
                BitConverter.ToString(actual).Replace("-", string.Empty).ToLowerInvariant(), "version 1 canonical identity frozen");
        }
        foreach (char character in ResidentialNominalCapacity.CanonicalPolicyMaterial) Check(character <= 127, "canonical material is ASCII/UTF8 stable");
        Equal("NativeArrivalNominalV1", ResidentialNominalCapacity.PolicyName, "policy identity");
        Equal("Cities:Skylines1.Windows.1.21.1-f9", ResidentialNominalCapacity.SourceVersion, "source version");
        Equal(1L, ResidentialNominalCapacity.PolicyVersion, "policy version");
        Equal(4L, ResidentialNominalCapacity.QuartersPerPerson, "units");
    }

    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Equal(long expected, long actual, string message) { Check(expected == actual, message + ": " + expected + " != " + actual); }
    private static void Equal(string expected, string actual, string message) { Check(expected == actual, message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { _checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
