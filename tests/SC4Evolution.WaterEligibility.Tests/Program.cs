using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using SC4Evolution.Simulation;

internal static class Program
{
    private static int _checks;
    private static ResidentialWaterProfile _profile;
    private static int Main(string[] args)
    {
        try
        {
            string root = args.Length == 1 ? args[0] : Directory.GetCurrentDirectory();
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, "analysis/generated/sc4-residential-profile/water-prerequisites.s4ew"));
            Equal(100, bytes.Length, "real private profile length");
            using (SHA256 sha = SHA256.Create()) Equal(ResidentialSelectionPolicy.WaterProfileSha256, Hex(sha.ComputeHash(bytes)), "real profile SHA");
            using (MemoryStream stream = new MemoryStream(bytes)) _profile = ResidentialWaterProfileCodec.Read(stream);
            ResidentialSelectionPolicy.ValidateWaterProfile(_profile); _checks++;
            TestSelection(); TestProfileRejection(bytes); TestDomain(); TestMigration();
            Console.WriteLine("PASS: " + _checks + " water eligibility/policy assertions; synthetic demands, no native execution.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }
    private static DemandNodeState[] States(int low, int medium, int high)
    {
        // Deliberately reverse array order. This is synthetic ranking input, not city census.
        return new[] { new DemandNodeState { Key = 0x21030, ActiveDemand = high, NormalizedDemand = high },
            new DemandNodeState { Key = 0x21020, ActiveDemand = medium, NormalizedDemand = medium },
            new DemandNodeState { Key = 0x21010, ActiveDemand = low, NormalizedDemand = low } };
    }
    private static void Choice(DemandNodeState[] nodes, ResidentialWaterProfile profile, ResidentialWaterEvidence evidence,
        ResidentialChoiceKind kind, ResidentialClass selected, ResidentialClassMask mask)
    {
        ResidentialClassChoice result = ResidentialWaterPrerequisite.Choose(nodes, profile, evidence);
        Equal(kind, result.Kind, "choice kind"); Equal(selected, result.Class, "selected class"); Equal(mask, result.Mask, "mask");
    }
    private static void TestSelection()
    {
        Choice(States(3, 2, 17), _profile, ResidentialWaterEvidence.Served, ResidentialChoiceKind.Selected, ResidentialClass.High, ResidentialClassMask.All);
        Choice(States(3, 2, 17), _profile, ResidentialWaterEvidence.Unserved, ResidentialChoiceKind.Selected, ResidentialClass.Low, ResidentialClassMask.Low | ResidentialClassMask.Medium);
        Choice(States(0, 2, 17), _profile, ResidentialWaterEvidence.Unserved, ResidentialChoiceKind.Selected, ResidentialClass.Medium, ResidentialClassMask.Low | ResidentialClassMask.Medium);
        Choice(States(0, 0, 17), _profile, ResidentialWaterEvidence.Unserved, ResidentialChoiceKind.ControlledReject, 0, ResidentialClassMask.Low | ResidentialClassMask.Medium);
        Choice(States(0, -2, 0), _profile, ResidentialWaterEvidence.Served, ResidentialChoiceKind.ControlledReject, 0, ResidentialClassMask.All);
        Choice(States(3, 2, 17), _profile, ResidentialWaterEvidence.Unknown, ResidentialChoiceKind.NativeFallback, 0, ResidentialClassMask.None);
        Choice(States(3, 2, 17), null, ResidentialWaterEvidence.Served, ResidentialChoiceKind.NativeFallback, 0, ResidentialClassMask.None);
        Choice(States(17, 17, 17), _profile, ResidentialWaterEvidence.Served, ResidentialChoiceKind.Selected, ResidentialClass.Low, ResidentialClassMask.All);
        Choice(States(0, 17, 17), _profile, ResidentialWaterEvidence.Served, ResidentialChoiceKind.Selected, ResidentialClass.Medium, ResidentialClassMask.All);
        // Generic pure rule must filter cap0 for ANY class, not a hardcoded High veto.
        ResidentialWaterProfile reverse = new ResidentialWaterProfile(_profile.SourceSha256, 0, 0, 3);
        Choice(States(17, 17, 3), reverse, ResidentialWaterEvidence.Unserved, ResidentialChoiceKind.Selected, ResidentialClass.High, ResidentialClassMask.High);
        Choice(States(3, 2, 17), new ResidentialWaterProfile(_profile.SourceSha256, 0, 0, 0), ResidentialWaterEvidence.Unserved,
            ResidentialChoiceKind.ControlledReject, 0, ResidentialClassMask.None);
        DemandNodeState[] nodes = States(3, 2, 17); nodes[0].ActiveDemand = 0;
        Choice(nodes, _profile, ResidentialWaterEvidence.Served, ResidentialChoiceKind.Selected, ResidentialClass.Low, ResidentialClassMask.All);
        nodes = States(3, 2, 17); nodes[0].ActiveDemand = -1;
        Choice(nodes, _profile, ResidentialWaterEvidence.Served, ResidentialChoiceKind.Selected, ResidentialClass.Low, ResidentialClassMask.All);
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            nodes = States(3, 2, 17); nodes[0].ActiveDemand = invalid;
            Choice(nodes, _profile, ResidentialWaterEvidence.Unserved, ResidentialChoiceKind.NativeFallback, 0, ResidentialClassMask.None);
        }
        nodes = States(3, 2, 17); nodes[0].NormalizedDemand = 1001;
        Choice(nodes, _profile, ResidentialWaterEvidence.Served, ResidentialChoiceKind.NativeFallback, 0, ResidentialClassMask.None);
        nodes[0].NormalizedDemand = -1001;
        Choice(nodes, _profile, ResidentialWaterEvidence.Served, ResidentialChoiceKind.NativeFallback, 0, ResidentialClassMask.None);
        nodes = States(3, 2, 17); nodes[0].Key = 0x21020;
        Choice(nodes, _profile, ResidentialWaterEvidence.Served, ResidentialChoiceKind.NativeFallback, 0, ResidentialClassMask.None);
        Choice(new DemandNodeState[0], _profile, ResidentialWaterEvidence.Served, ResidentialChoiceKind.NativeFallback, 0, ResidentialClassMask.None);
        Equal(ResidentialClass.Low, ResidentialWaterPrerequisite.ChoosePermitted(States(3, 2, 17), ResidentialClassMask.Low, ResidentialWaterEvidence.Unknown).Class, "native policy can rank without water evidence");
        Reject(delegate { ResidentialWaterPrerequisite.ChoosePermitted(States(1, 1, 1), (ResidentialClassMask)8, ResidentialWaterEvidence.Served); }, "invalid mask");
        Reject(delegate { ResidentialWaterPrerequisite.Choose(States(1, 1, 1), _profile, (ResidentialWaterEvidence)9); }, "invalid evidence");
    }
    private static void TestProfileRejection(byte[] original)
    {
        byte[] source = _profile.SourceSha256; source[0] ^= 1;
        Reject(delegate { ResidentialSelectionPolicy.ValidateWaterProfile(new ResidentialWaterProfile(source, 3, 3, 0)); }, "wrong source");
        foreach (ResidentialWaterProfile profile in new[] { new ResidentialWaterProfile(_profile.SourceSha256, 0, 3, 0),
            new ResidentialWaterProfile(_profile.SourceSha256, 3, 0, 0), new ResidentialWaterProfile(_profile.SourceSha256, 3, 3, 3) })
            Reject(delegate { ResidentialSelectionPolicy.ValidateWaterProfile(profile); }, "wrong cap");
        Reject(delegate { ResidentialSelectionPolicy.ValidateWaterProfile(null); }, "absent profile");
        for (int index = 0; index < original.Length; index++)
        {
            byte[] changed = (byte[])original.Clone(); changed[index] ^= 1;
            Reject(delegate { using (MemoryStream stream = new MemoryStream(changed)) ResidentialSelectionPolicy.ValidateWaterProfile(ResidentialWaterProfileCodec.Read(stream)); }, "corrupt byte " + index);
        }
    }
    private static void TestDomain()
    {
        Equal(true, ResidentialWaterPrerequisite.IsSupportedNativeQueryPosition(0, 0, 0), "origin");
        Equal(true, ResidentialWaterPrerequisite.IsSupportedNativeQueryPosition(-4896, 100, -4896), "inclusive lower edge");
        Equal(true, ResidentialWaterPrerequisite.IsSupportedNativeQueryPosition(4895, -100, 4895), "inside upper edge");
        foreach (float value in new[] { -4896.001f, 4896f, 10000f, -10000f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Equal(false, ResidentialWaterPrerequisite.IsSupportedNativeQueryPosition(value, 0, 0), "bad X");
            Equal(false, ResidentialWaterPrerequisite.IsSupportedNativeQueryPosition(0, 0, value), "bad Z");
        }
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Equal(false, ResidentialWaterPrerequisite.IsSupportedNativeQueryPosition(0, value, 0), "bad Y");
        float justInside = BitConverter.ToSingle(BitConverter.GetBytes(BitConverter.ToInt32(BitConverter.GetBytes(4896f), 0) - 1), 0);
        // IEEE float division/addition can round a nominally inside coordinate into cell256.
        Equal(justInside / 38.25f + 128f < 256f, ResidentialWaterPrerequisite.IsSupportedNativeQueryPosition(justInside, 0, 0), "float32 upper edge");
    }
    private static void TestMigration()
    {
        Equal("7353d077622c6f87da8631de0fd072609cf116af6671d168c464fb3975a812f3", Hex(ResidentialSelectionPolicy.PolicyHash(ResidentialSelectionPolicyId.NativeSafetyOnlyV1)), "frozen v1 hash");
        ResidentialClassLedger old = new ResidentialClassLedger(new Guid("8ac2f7dc-a352-41c2-934a-d761f52ebc63"), 1,
            ResidentialSelectionPolicy.PolicyHash(ResidentialSelectionPolicyId.NativeSafetyOnlyV1));
        old.ImportExisting(Identity(1, 0, "native-low"), ResidentialClass.Low);
        old.CommitCreated(Identity(2, uint.MaxValue, "selected-high"), ResidentialClass.High);
        old.CommitCreated(Identity(3, 45, "selected-medium"), ResidentialClass.Medium);
        old.Remove(3); // Deleted generations must never be reused by migration.
        byte[] before = Encode(old);
        ResidentialClassLedger changed = ResidentialSelectionPolicy.MigrateKnownNativeSafetyV1ToWaterV1(old, _profile);
        Equal(false, ReferenceEquals(old, changed), "migration clones");
        Equal(ResidentialSelectionPolicyId.NativeSafetyOnlyV1, ResidentialSelectionPolicy.Identify(old), "source policy retained");
        Equal(ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1, ResidentialSelectionPolicy.Identify(changed), "target policy");
        Equal((uint)2, changed.PolicyVersion, "policy version distinct from wire version");
        Equal(old.LedgerId, changed.LedgerId, "GUID"); Equal(old.NextGeneration, changed.NextGeneration, "NextGeneration");
        Equal(Hex(before), Hex(Encode(old)), "source bytes unchanged");
        Equal(old.Count, changed.Count, "count");
        for (int i = 0; i < old.Count; i++)
        {
            ResidentialLotRecord a = old.GetRecords()[i], b = changed.GetRecords()[i];
            Equal(a.Generation, b.Generation, "generation"); Equal(a.DesignClass, b.DesignClass, "design"); Equal(a.OccupantClass, b.OccupantClass, "occupant"); Equal(a.Origin, b.Origin, "origin");
            Equal(a.Identity.BuildingId, b.Identity.BuildingId, "building"); Equal(a.Identity.NativeBuildIndex, b.Identity.NativeBuildIndex, "native index including zero");
            Equal(a.Identity.OriginXBits, b.Identity.OriginXBits, "X"); Equal(a.Identity.OriginZBits, b.Identity.OriginZBits, "Z");
            Equal(a.Identity.PrefabName, b.Identity.PrefabName, "prefab"); Equal(a.Identity.Service, b.Identity.Service, "service"); Equal(a.Identity.SubService, b.Identity.SubService, "subservice");
            Equal(a.Identity.Width, b.Identity.Width, "width"); Equal(a.Identity.Length, b.Identity.Length, "length");
        }
        Equal(true, ReferenceEquals(changed, ResidentialSelectionPolicy.MigrateKnownNativeSafetyV1ToWaterV1(changed, _profile)), "idempotent");
        byte[] encoded = Encode(changed);
        Equal((byte)1, encoded[8], "wire v1 retained"); Equal((byte)0, encoded[9], "wire version high byte");
        using (MemoryStream stream = new MemoryStream(encoded)) Equal(Hex(encoded), Hex(Encode(ResidentialClassLedgerCodec.Read(stream))), "v2 policy roundtrip");
        foreach (uint version in new uint[] { 0, 1, 2, 3, uint.MaxValue })
            foreach (ResidentialSelectionPolicyId hashPolicy in new[] { ResidentialSelectionPolicyId.NativeSafetyOnlyV1, ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1 })
            {
                if ((version == 1 && hashPolicy == ResidentialSelectionPolicyId.NativeSafetyOnlyV1) || (version == 2 && hashPolicy == ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1)) continue;
                ResidentialClassLedger invalid = new ResidentialClassLedger(Guid.NewGuid(), version, ResidentialSelectionPolicy.PolicyHash(hashPolicy));
                Equal(ResidentialSelectionPolicyId.Unknown, ResidentialSelectionPolicy.Identify(invalid), "unknown version/hash pair");
                Reject(delegate { ResidentialSelectionPolicy.MigrateKnownNativeSafetyV1ToWaterV1(invalid, _profile); }, "unknown never promoted");
            }
        byte[] arbitraryHash = new byte[32]; arbitraryHash[0] = 1;
        ResidentialClassLedger arbitrary = new ResidentialClassLedger(Guid.NewGuid(), 1, arbitraryHash);
        Reject(delegate { ResidentialSelectionPolicy.MigrateKnownNativeSafetyV1ToWaterV1(arbitrary, _profile); }, "arbitrary hash");
        Reject(delegate { ResidentialSelectionPolicy.MigrateKnownNativeSafetyV1ToWaterV1(old, null); }, "migration requires valid profile");
        // Exercise the internal validated clone at the maximum generation counter; migration allocates none.
        ConstructorInfo ctor = typeof(ResidentialClassLedger).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(Guid), typeof(uint), typeof(byte[]), typeof(ulong), typeof(ResidentialLotRecord[]) }, null);
        ResidentialClassLedger exhausted = (ResidentialClassLedger)ctor.Invoke(new object[] { old.LedgerId, 1u, old.PolicyHash, ulong.MaxValue, old.GetRecords() });
        Equal(ulong.MaxValue, ResidentialSelectionPolicy.MigrateKnownNativeSafetyV1ToWaterV1(exhausted, _profile).NextGeneration, "migration at exhausted counter");
        byte[] cloneHash = ResidentialSelectionPolicy.PolicyHash(ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1); cloneHash[0] ^= 1;
        Equal(ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1, ResidentialSelectionPolicy.Identify(changed), "hash arrays detached");
        Console.WriteLine("Water policy v2 hash: " + Hex(changed.PolicyHash));
    }
    private static ResidentialLotIdentity Identity(ushort id, uint index, string name)
    { return new ResidentialLotIdentity(id, index, BitConverter.ToInt32(BitConverter.GetBytes(1.5f), 0), BitConverter.ToInt32(BitConverter.GetBytes(-2f), 0), name, 1, 2, 3, 4); }
    private static byte[] Encode(ResidentialClassLedger ledger)
    { using (MemoryStream stream = new MemoryStream()) { ResidentialClassLedgerCodec.Write(stream, ledger); return stream.ToArray(); } }
    private static string Hex(byte[] value) { return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant(); }
    private static void Equal<T>(T expected, T actual, string name)
    { _checks++; if (!object.Equals(expected, actual)) throw new Exception(name + ": expected " + expected + ", got " + actual); }
    private static void Reject(Action action, string name)
    { _checks++; try { action(); } catch (ArgumentException) { return; } catch (InvalidDataException) { return; } throw new Exception("Did not reject " + name); }
}
