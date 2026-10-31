using System;
using System.Collections.Generic;
using System.Text;

namespace SC4Evolution.Simulation
{
    /// <summary>Stable facility identity. Position/angle are deliberately excluded so
    /// the same native building can retain its override after relocation.</summary>
    public sealed class CivicFacilityIdentity
    {
        public const int MaximumNameUtf8Bytes = 1024;
        internal static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public ushort BuildingId { get; private set; }
        public uint NativeBuildIndex { get; private set; }
        public string PrefabName { get; private set; }
        public string AiTypeName { get; private set; }
        public CivicFundingArchetype Archetype { get; private set; }

        public CivicFacilityIdentity(ushort buildingId, uint nativeBuildIndex, string prefabName,
            string aiTypeName, CivicFundingArchetype archetype)
        {
            if (buildingId == 0) throw new ArgumentOutOfRangeException("buildingId", "Building ID must be nonzero.");
            ValidateName(prefabName, "prefabName"); ValidateName(aiTypeName, "aiTypeName"); ValidateArchetype(archetype);
            BuildingId = buildingId; NativeBuildIndex = nativeBuildIndex;
            PrefabName = prefabName; AiTypeName = aiTypeName; Archetype = archetype;
        }
        public bool Matches(CivicFacilityIdentity other)
        {
            return other != null && BuildingId == other.BuildingId && NativeBuildIndex == other.NativeBuildIndex &&
                string.Equals(PrefabName, other.PrefabName, StringComparison.Ordinal) &&
                string.Equals(AiTypeName, other.AiTypeName, StringComparison.Ordinal) && Archetype == other.Archetype;
        }
        private static void ValidateName(string value, string argument)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("Facility name must be nonempty.", argument);
            // Unity's legacy Mono does not consistently honor encoder exception fallback.
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c))
                { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new ArgumentException("Facility name must be valid Unicode.", argument); }
                else if (char.IsLowSurrogate(c)) throw new ArgumentException("Facility name must be valid Unicode.", argument);
            }
            int length;
            try { length = StrictUtf8.GetByteCount(value); }
            catch (EncoderFallbackException error) { throw new ArgumentException("Facility name must be valid Unicode.", argument, error); }
            if (length > MaximumNameUtf8Bytes) throw new ArgumentOutOfRangeException(argument, "UTF-8 facility name exceeds the limit.");
        }
        internal static void ValidateArchetype(CivicFundingArchetype archetype)
        {
            switch (archetype)
            {
                case CivicFundingArchetype.ElementarySchool: case CivicFundingArchetype.HighSchool:
                case CivicFundingArchetype.Clinic: case CivicFundingArchetype.Hospital: return;
                default: throw new ArgumentOutOfRangeException("archetype", "Unknown civic funding archetype.");
            }
        }
    }

    public sealed class CivicFundingRecord
    {
        public CivicFacilityIdentity Identity { get; private set; }
        public int? CapacityFundingPercent { get; private set; }
        public int? CoverageFundingPercent { get; private set; }
        public CivicFundingRecord(CivicFacilityIdentity identity, int? capacityFundingPercent, int? coverageFundingPercent)
        {
            if (identity == null) throw new ArgumentNullException("identity");
            ValidatePercent(capacityFundingPercent, "capacityFundingPercent"); ValidatePercent(coverageFundingPercent, "coverageFundingPercent");
            if (!capacityFundingPercent.HasValue && !coverageFundingPercent.HasValue)
                throw new ArgumentException("Both inherited axes mean no override; remove the record instead.");
            Identity = identity; CapacityFundingPercent = capacityFundingPercent; CoverageFundingPercent = coverageFundingPercent;
        }
        internal static void ValidatePercent(int? percent, string argument)
        { if (percent.HasValue && (percent.Value < 0 || percent.Value > 120)) throw new ArgumentOutOfRangeException(argument, "Funding percent must be between 0 and 120, or null to inherit."); }
    }

    /// <summary>Instance-owned overrides only; no budget, money or native authority.
    /// The native caller must validate identity/owner/epoch before mutation.</summary>
    public sealed class CivicFundingLedger
    {
        public const int MaximumRecords = 65535;
        private readonly object _sync = new object();
        private readonly SortedDictionary<ushort, CivicFundingRecord> _records = new SortedDictionary<ushort, CivicFundingRecord>();
        public Guid LedgerId { get; private set; }
        public int Count { get { lock (_sync) return _records.Count; } }
        public CivicFundingLedger(Guid ledgerId)
        { if (ledgerId == Guid.Empty) throw new ArgumentException("Ledger ID must be nonempty.", "ledgerId"); LedgerId = ledgerId; }

        // A mismatched/reused ID is observationally absent. Lookup never prunes.
        public bool TryGet(CivicFacilityIdentity identity, out CivicFundingRecord record)
        {
            lock (_sync)
            {
                record = null; CivicFundingRecord found;
                if (identity == null || !_records.TryGetValue(identity.BuildingId, out found) || !found.Identity.Matches(identity)) return false;
                record = found; return true;
            }
        }
        // Set is explicitly by building ID: a new identity replaces the old one;
        // null/null removes by ID even if the supplied identity differs.
        public bool Set(CivicFacilityIdentity identity, int? capacityFundingPercent, int? coverageFundingPercent)
        {
            if (identity == null) throw new ArgumentNullException("identity");
            CivicFundingRecord.ValidatePercent(capacityFundingPercent, "capacityFundingPercent");
            CivicFundingRecord.ValidatePercent(coverageFundingPercent, "coverageFundingPercent");
            lock (_sync)
            {
                if (!capacityFundingPercent.HasValue && !coverageFundingPercent.HasValue) return _records.Remove(identity.BuildingId);
                CivicFundingRecord old;
                if (_records.TryGetValue(identity.BuildingId, out old))
                {
                    if (old.Identity.Matches(identity) && old.CapacityFundingPercent == capacityFundingPercent && old.CoverageFundingPercent == coverageFundingPercent) return false;
                }
                else if (_records.Count >= MaximumRecords) throw new InvalidOperationException("Civic funding ledger is full.");
                _records[identity.BuildingId] = new CivicFundingRecord(identity, capacityFundingPercent, coverageFundingPercent); return true;
            }
        }
        public bool Remove(ushort buildingId) { lock (_sync) return _records.Remove(buildingId); }
        public CivicFundingRecord[] GetRecords()
        {
            lock (_sync)
            {
                CivicFundingRecord[] records = new CivicFundingRecord[_records.Count];
                _records.Values.CopyTo(records, 0); return records;
            }
        }
    }
}
