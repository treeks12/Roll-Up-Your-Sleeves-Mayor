using System;
using System.IO;
using SC4Evolution.Simulation;

internal static class Program
{
    private static int checks;
    private static int Main(string[] args)
    {
        try
        {
            string root = args.Length == 1 ? args[0] : Directory.GetCurrentDirectory();
            ResidentialWaterProfile water;
            using (FileStream stream = File.OpenRead(Path.Combine(root, "analysis/generated/sc4-residential-profile/water-prerequisites.s4ew"))) water = ResidentialWaterProfileCodec.Read(stream);
            Selection(); Migration(water);
            Console.WriteLine("PASS: " + checks + " RAW selection policy checks; pure synthetic inputs, no native execution."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Selection()
    {
        Equal(ResidentialClass.High, ResidentialSelectionPolicy.ChooseRawWindow(20, 22, 90, ResidentialClassMask.All));
        Equal(ResidentialClass.Medium, ResidentialSelectionPolicy.ChooseRawWindow(20, 22, 90, ResidentialClassMask.Low | ResidentialClassMask.Medium));
        Equal(ResidentialClass.Low, ResidentialSelectionPolicy.ChooseRawWindow(20, 20, 20, ResidentialClassMask.All));
        Equal(ResidentialClass.Medium, ResidentialSelectionPolicy.ChooseRawWindow(20, 20, 20, ResidentialClassMask.Medium | ResidentialClassMask.High));
        Equal((ResidentialClass)0, ResidentialSelectionPolicy.ChooseRawWindow(20, 20, 20, ResidentialClassMask.None));
        // Exhausted Low is ineligible even if its original RAW outranks every other class.
        Equal(ResidentialClass.High, ResidentialSelectionPolicy.ChooseRawWindow(1000, 6, 7, ResidentialClassMask.Medium | ResidentialClassMask.High));
        for (int i = 0; i < 8; i++)
        {
            ResidentialClass expected = i == 0 ? 0 : (i & 1) != 0 ? ResidentialClass.Low : (i & 2) != 0 ? ResidentialClass.Medium : ResidentialClass.High;
            Equal(expected, ResidentialSelectionPolicy.ChooseRawWindow(6, 6, 6, (ResidentialClassMask)i));
        }
        Throws(delegate { ResidentialSelectionPolicy.ChooseRawWindow(float.NaN, 6, 6, ResidentialClassMask.All); });
        Throws(delegate { ResidentialSelectionPolicy.ChooseRawWindow(6, float.PositiveInfinity, 6, ResidentialClassMask.None); });
        Throws(delegate { ResidentialSelectionPolicy.ChooseRawWindow(6, 6, 6, (ResidentialClassMask)8); });
        byte[] hash = ResidentialSelectionPolicy.PolicyHash(ResidentialSelectionPolicyId.SC4WaterRawCapacityV1);
        byte original = hash[0]; hash[0] ^= 255;
        Equal(original, ResidentialSelectionPolicy.PolicyHash(ResidentialSelectionPolicyId.SC4WaterRawCapacityV1)[0]);
        Equal(false, Hex(ResidentialSelectionPolicy.PolicyHash(ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1)) == Hex(ResidentialSelectionPolicy.PolicyHash(ResidentialSelectionPolicyId.SC4WaterRawCapacityV1)));
    }
    private static void Migration(ResidentialWaterProfile profile)
    {
        Guid id = Guid.NewGuid();
        ResidentialClassLedger old = new ResidentialClassLedger(id, 1, ResidentialSelectionPolicy.PolicyHash(ResidentialSelectionPolicyId.NativeSafetyOnlyV1));
        old.ImportExisting(Identity(1, 0), ResidentialClass.High);
        old.CommitCreated(Identity(2, 33), ResidentialClass.Medium);
        ResidentialClassLedger migrated = ResidentialSelectionPolicy.MigrateKnownToRawCapacityV1(old, profile);
        Equal(ResidentialSelectionPolicyId.SC4WaterRawCapacityV1, ResidentialSelectionPolicy.Identify(migrated));
        Equal(id, migrated.LedgerId); Equal(old.NextGeneration, migrated.NextGeneration); Equal(old.Count, migrated.Count);
        ResidentialLotRecord[] before = old.GetRecords(), after = migrated.GetRecords();
        for (int i = 0; i < before.Length; i++)
        {
            Equal(before[i].Generation, after[i].Generation); Equal(before[i].Origin, after[i].Origin);
            Equal(before[i].DesignClass, after[i].DesignClass); Equal(before[i].OccupantClass, after[i].OccupantClass);
            Equal(before[i].Identity.NativeBuildIndex, after[i].Identity.NativeBuildIndex);
            Equal(before[i].Identity.PrefabName, after[i].Identity.PrefabName);
        }
        Equal(true, ReferenceEquals(migrated, ResidentialSelectionPolicy.MigrateKnownToRawCapacityV1(migrated, profile)));
        Equal(ResidentialSelectionPolicyId.NativeSafetyOnlyV1, ResidentialSelectionPolicy.Identify(old));
        ResidentialClassLedger water = ResidentialSelectionPolicy.MigrateKnownNativeSafetyV1ToWaterV1(old, profile);
        Equal(id, ResidentialSelectionPolicy.MigrateKnownToRawCapacityV1(water, profile).LedgerId);
        Throws(delegate { ResidentialSelectionPolicy.MigrateKnownNativeSafetyV1ToWaterV1(migrated, profile); });
        Throws(delegate { ResidentialSelectionPolicy.MigrateKnownToRawCapacityV1(new ResidentialClassLedger(id, 3, new byte[32]), profile); });
        Throws(delegate { ResidentialSelectionPolicy.MigrateKnownToRawCapacityV1(old, new ResidentialWaterProfile(profile.SourceSha256, 0, 3, 3)); });
        using (MemoryStream stream = new MemoryStream())
        {
            ResidentialClassLedgerCodec.Write(stream, migrated); stream.Position = 0;
            ResidentialClassLedger restored = ResidentialClassLedgerCodec.Read(stream);
            Equal(ResidentialSelectionPolicyId.SC4WaterRawCapacityV1, ResidentialSelectionPolicy.Identify(restored));
            Equal(migrated.NextGeneration, restored.NextGeneration);
        }
    }
    private static ResidentialLotIdentity Identity(ushort id, uint index)
    { return new ResidentialLotIdentity(id, index, 0, 0, "synthetic-policy-test-" + id, 1, 1, 1, 1); }
    private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }
    private static void Equal<T>(T expected, T actual)
    { checks++; if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
    private static void Throws(Action action)
    { checks++; try { action(); } catch (ArgumentException) { return; } catch (InvalidDataException) { return; } throw new Exception("Expected validation rejection."); }
}
