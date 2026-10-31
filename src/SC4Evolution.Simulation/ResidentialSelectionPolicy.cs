using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SC4Evolution.Simulation
{
    public enum ResidentialSelectionPolicyId : byte { Unknown = 0, NativeSafetyOnlyV1 = 1, SC4WaterPrerequisiteV1 = 2, SC4WaterRawCapacityV1 = 3 }

    /// <summary>Exact policy identities. Ledger wire version 1 stays unchanged.</summary>
    public static class ResidentialSelectionPolicy
    {
        public const string SourceArchiveSha256 = "84d935c8a7a53b3261a1385f1897167445dd85c14fef76baf61f8b4beec79348";
        public const string WaterProfileSha256 = "81ccd63f3accece1e005a3764b9d610f98f9ed2cefcbf72e3b937c6111d76c4d";
        private const string NativeCanonical = "SC4Evolution.ResidentialLotClass.v1;NativeSafetyOnly;greatest-positive-normalized;key-order-tie;design=occupant;native-prefab-independent";
        private const string WaterCanonical = "SC4Evolution.ResidentialLotClass.v2;SC4WaterPrerequisiteV1;greatest-positive-normalized;key-order-tie;design=occupant;native-prefab-independent;water-unserved-excludes-cap0;water-unknown=native-no-commit;water-anchor=candidate-position;water-domain=-4896-inclusive,4896-exclusive-float32-unclamped;water-profile-sha256=" + WaterProfileSha256 + ";source=" + SourceArchiveSha256 + ";caps=3,3,0";
        private static readonly byte[] NativeHash = Hash(Encoding.UTF8.GetBytes(NativeCanonical));
        private static readonly byte[] WaterHash = Hash(Encoding.UTF8.GetBytes(WaterCanonical));
        private const string RawCanonical = "SC4Evolution.ResidentialLotClass.v3;SC4WaterRawCapacityV1;greatest-opening-window-active-raw;key-order-tie;eligible=open-daily-window-with-unreserved-target;candidate-fit=descending-raw-first-fitting-Hmax-reservation;active-predecessor=same-class-net,cross-class-gross;unavailable-predecessor=gross-no-credit;design=occupant;native-prefab-independent;accounting=ResidentialRawDailyAccountingV1;nominal=NativeArrivalNominalV1;water-unserved-excludes-cap0;water-unknown=native-no-commit;water-anchor=candidate-position;water-domain=-4896-inclusive,4896-exclusive-float32-unclamped;water-profile-sha256=" + WaterProfileSha256 + ";source=" + SourceArchiveSha256 + ";caps=3,3,0";
        private static readonly byte[] RawHash = Hash(Encoding.UTF8.GetBytes(RawCanonical));

        public static string Name(ResidentialSelectionPolicyId policy)
        {
            if (policy == ResidentialSelectionPolicyId.NativeSafetyOnlyV1) return "NativeSafetyOnly";
            if (policy == ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1) return "SC4WaterPrerequisiteV1";
            if (policy == ResidentialSelectionPolicyId.SC4WaterRawCapacityV1) return "SC4WaterRawCapacityV1";
            return "Unknown";
        }
        public static uint Version(ResidentialSelectionPolicyId policy)
        {
            RequireKnown(policy); return (uint)policy;
        }
        public static byte[] PolicyHash(ResidentialSelectionPolicyId policy)
        {
            RequireKnown(policy); return (byte[])(policy == ResidentialSelectionPolicyId.NativeSafetyOnlyV1 ? NativeHash : policy == ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1 ? WaterHash : RawHash).Clone();
        }
        public static ResidentialSelectionPolicyId Identify(ResidentialClassLedger ledger)
        {
            if (ledger == null) return ResidentialSelectionPolicyId.Unknown;
            if (ledger.PolicyVersion == 1 && Equal(ledger.PolicyHash, NativeHash)) return ResidentialSelectionPolicyId.NativeSafetyOnlyV1;
            if (ledger.PolicyVersion == 2 && Equal(ledger.PolicyHash, WaterHash)) return ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1;
            if (ledger.PolicyVersion == 3 && Equal(ledger.PolicyHash, RawHash)) return ResidentialSelectionPolicyId.SC4WaterRawCapacityV1;
            return ResidentialSelectionPolicyId.Unknown;
        }
        public static void ValidateWaterProfile(ResidentialWaterProfile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            if (!Equal(profile.SourceSha256, FromHex(SourceArchiveSha256)) || profile.LowMaximumStage != 3 || profile.MediumMaximumStage != 3 || profile.HighMaximumStage != 0)
                throw new InvalidDataException("Residential water provenance or caps do not match the pinned adapter source.");
            using (MemoryStream stream = new MemoryStream())
            {
                ResidentialWaterProfileCodec.Write(stream, profile);
                if (!Equal(Hash(stream.ToArray()), FromHex(WaterProfileSha256))) throw new InvalidDataException("Residential water semantic profile hash differs.");
            }
        }
        public static ResidentialClassLedger MigrateKnownNativeSafetyV1ToWaterV1(ResidentialClassLedger source, ResidentialWaterProfile profile)
        {
            if (source == null) throw new ArgumentNullException("source");
            ValidateWaterProfile(profile);
            ResidentialSelectionPolicyId existing = Identify(source);
            if (existing == ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1) return source;
            if (existing != ResidentialSelectionPolicyId.NativeSafetyOnlyV1) throw new InvalidDataException("Only the exact known NativeSafetyOnly v1 ledger may migrate.");
            // No generation allocation or class assignment occurs in a policy migration.
            return new ResidentialClassLedger(source.LedgerId, Version(ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1),
                PolicyHash(ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1), source.NextGeneration, source.GetRecords());
        }
        private static void RequireKnown(ResidentialSelectionPolicyId policy)
        { if (policy != ResidentialSelectionPolicyId.NativeSafetyOnlyV1 && policy != ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1 && policy != ResidentialSelectionPolicyId.SC4WaterRawCapacityV1) throw new ArgumentOutOfRangeException("policy"); }
        public static ResidentialClassLedger MigrateKnownToRawCapacityV1(ResidentialClassLedger source, ResidentialWaterProfile profile)
        {
            if (source == null) throw new ArgumentNullException("source");
            ValidateWaterProfile(profile);
            ResidentialSelectionPolicyId existing = Identify(source);
            if (existing == ResidentialSelectionPolicyId.SC4WaterRawCapacityV1) return source;
            if (existing != ResidentialSelectionPolicyId.NativeSafetyOnlyV1 && existing != ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1)
                throw new InvalidDataException("Only known native/water selection policies may migrate to RAW capacity admission.");
            return new ResidentialClassLedger(source.LedgerId, Version(ResidentialSelectionPolicyId.SC4WaterRawCapacityV1),
                PolicyHash(ResidentialSelectionPolicyId.SC4WaterRawCapacityV1), source.NextGeneration, source.GetRecords());
        }

        // RAW has already been frozen by the accounting ledger at window opening.
        // The mask is derived from open windows/held quota, never meter normalization.
        public static ResidentialClass ChooseRawWindow(float low, float medium, float high, ResidentialClassMask eligible)
        {
            if ((eligible & ~ResidentialClassMask.All) != 0) throw new ArgumentOutOfRangeException("eligible");
            float[] values = { low, medium, high }; ResidentialClass chosen = 0; float best = float.NegativeInfinity;
            for (int i = 0; i < 3; i++)
            {
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i])) throw new ArgumentOutOfRangeException("low", "Finite window RAW is required.");
                if ((((int)eligible) & (1 << i)) != 0 && values[i] > best) { best = values[i]; chosen = (ResidentialClass)(i + 1); }
            }
            return chosen;
        }
        private static byte[] Hash(byte[] bytes) { using (SHA256 hash = SHA256.Create()) return hash.ComputeHash(bytes); }
        private static bool Equal(byte[] left, byte[] right)
        { if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false; return true; }
        private static byte[] FromHex(string value)
        { byte[] result = new byte[value.Length / 2]; for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16); return result; }
    }
}
