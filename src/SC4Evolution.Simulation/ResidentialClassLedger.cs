using System;
using System.Collections.Generic;
using System.Text;

namespace SC4Evolution.Simulation
{
    public enum ResidentialClass : byte { Low = 1, Medium = 2, High = 3 }
    public enum ClassOrigin : byte { ImportedNative = 1, Selected = 2 }

    /// <summary>A validated fingerprint for one residential building generation.</summary>
    public sealed class ResidentialLotIdentity
    {
        internal const int MaximumPrefabNameBytes = 1024;
        internal static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public ushort BuildingId { get; private set; }
        public uint NativeBuildIndex { get; private set; }
        public int OriginXBits { get; private set; }
        public int OriginZBits { get; private set; }
        public string PrefabName { get; private set; }
        public int Service { get; private set; }
        public int SubService { get; private set; }
        public byte Width { get; private set; }
        public byte Length { get; private set; }

        public ResidentialLotIdentity(ushort buildingId, uint nativeBuildIndex,
            int originXBits, int originZBits, string prefabName,
            int service, int subService, byte width, byte length)
        {
            if (buildingId == 0) throw new ArgumentOutOfRangeException("buildingId", "Building ID must be nonzero.");
            if (!IsFiniteBits(originXBits)) throw new ArgumentOutOfRangeException("originXBits", "Origin X must encode a finite float.");
            if (!IsFiniteBits(originZBits)) throw new ArgumentOutOfRangeException("originZBits", "Origin Z must encode a finite float.");
            if (string.IsNullOrEmpty(prefabName)) throw new ArgumentException("Prefab name must be nonempty.", "prefabName");
            int nameByteCount;
            try { nameByteCount = StrictUtf8.GetByteCount(prefabName); }
            catch (EncoderFallbackException error) { throw new ArgumentException("Prefab name is not valid Unicode.", "prefabName", error); }
            if (nameByteCount < 1 || nameByteCount > MaximumPrefabNameBytes)
                throw new ArgumentOutOfRangeException("prefabName", "UTF-8 prefab name must be at most 1024 bytes.");
            if (service < 0) throw new ArgumentOutOfRangeException("service", "Service must be nonnegative.");
            if (subService < 0) throw new ArgumentOutOfRangeException("subService", "Subservice must be nonnegative.");
            if (width == 0) throw new ArgumentOutOfRangeException("width", "Width must be between 1 and 255.");
            if (length == 0) throw new ArgumentOutOfRangeException("length", "Length must be between 1 and 255.");

            BuildingId = buildingId;
            NativeBuildIndex = nativeBuildIndex;
            OriginXBits = originXBits;
            OriginZBits = originZBits;
            PrefabName = prefabName;
            Service = service;
            SubService = subService;
            Width = width;
            Length = length;
        }

        internal bool Matches(ResidentialLotIdentity other)
        {
            return other != null && BuildingId == other.BuildingId &&
                NativeBuildIndex == other.NativeBuildIndex && OriginXBits == other.OriginXBits &&
                OriginZBits == other.OriginZBits && string.Equals(PrefabName, other.PrefabName, StringComparison.Ordinal) &&
                Service == other.Service && SubService == other.SubService && Width == other.Width && Length == other.Length;
        }

        internal static bool IsFiniteBits(int bits)
        {
            float value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal byte[] GetPrefabNameBytes()
        {
            return StrictUtf8.GetBytes(PrefabName);
        }
    }

    /// <summary>An immutable class assignment tied to one validated building fingerprint.</summary>
    public sealed class ResidentialLotRecord
    {
        public ResidentialLotIdentity Identity { get; private set; }
        public ulong Generation { get; private set; }
        public ResidentialClass DesignClass { get; private set; }
        public ResidentialClass OccupantClass { get; private set; }
        public ClassOrigin Origin { get; private set; }

        internal ResidentialLotRecord(ResidentialLotIdentity identity, ulong generation,
            ResidentialClass designClass, ResidentialClass occupantClass, ClassOrigin origin)
        {
            if (identity == null) throw new ArgumentNullException("identity");
            if (generation == 0) throw new ArgumentOutOfRangeException("generation", "Generation must be nonzero.");
            ResidentialClassLedger.ValidateClass(designClass, "designClass");
            ResidentialClassLedger.ValidateClass(occupantClass, "occupantClass");
            ResidentialClassLedger.ValidateOrigin(origin, "origin");
            if (designClass != occupantClass) throw new ArgumentException("Version 1 requires design and occupant classes to match.");
            Identity = identity;
            Generation = generation;
            DesignClass = designClass;
            OccupantClass = occupantClass;
            Origin = origin;
        }
    }

    /// <summary>Bounded, instance-local ledger of residential lot class assignments.</summary>
    public sealed class ResidentialClassLedger
    {
        public const int MaximumRecords = 65535;
        private readonly SortedDictionary<ushort, ResidentialLotRecord> _records =
            new SortedDictionary<ushort, ResidentialLotRecord>();
        private readonly byte[] _policyHash;
        private ulong _nextGeneration;

        public Guid LedgerId { get; private set; }
        public uint PolicyVersion { get; private set; }
        public byte[] PolicyHash { get { return (byte[])_policyHash.Clone(); } }
        public ulong NextGeneration { get { return _nextGeneration; } }
        public int Count { get { return _records.Count; } }

        public ResidentialClassLedger(Guid ledgerId, uint policyVersion, byte[] policyHash)
        {
            if (ledgerId == Guid.Empty) throw new ArgumentException("Ledger ID must be nonempty.", "ledgerId");
            if (policyHash == null || policyHash.Length != 32)
                throw new ArgumentException("Policy hash must contain exactly 32 bytes.", "policyHash");
            LedgerId = ledgerId;
            PolicyVersion = policyVersion;
            _policyHash = (byte[])policyHash.Clone();
            _nextGeneration = 1;
        }

        internal ResidentialClassLedger(Guid ledgerId, uint policyVersion, byte[] policyHash,
            ulong nextGeneration, ResidentialLotRecord[] records)
            : this(ledgerId, policyVersion, policyHash)
        {
            if (records == null) throw new ArgumentNullException("records");
            if (records.Length > MaximumRecords) throw new ArgumentException("Too many residential records.", "records");
            ulong maximumGeneration = 0;
            int index;
            for (index = 0; index < records.Length; index++)
            {
                ResidentialLotRecord record = records[index];
                if (record == null) throw new ArgumentException("Null residential record.", "records");
                if (_records.ContainsKey(record.Identity.BuildingId))
                    throw new ArgumentException("Duplicate residential building ID.", "records");
                _records.Add(record.Identity.BuildingId, record);
                if (record.Generation > maximumGeneration) maximumGeneration = record.Generation;
            }
            if (nextGeneration == 0 || nextGeneration <= maximumGeneration)
                throw new ArgumentException("Next generation must exceed every stored generation.", "nextGeneration");
            _nextGeneration = nextGeneration;
        }

        public bool TryGet(ResidentialLotIdentity current, out ResidentialLotRecord record)
        {
            record = null;
            if (current == null) return false;
            ResidentialLotRecord found;
            if (!_records.TryGetValue(current.BuildingId, out found) || !found.Identity.Matches(current))
                return false;
            record = found;
            return true;
        }

        public ResidentialLotRecord CommitCreated(ResidentialLotIdentity identity, ResidentialClass selectedClass)
        {
            if (identity == null) throw new ArgumentNullException("identity");
            ValidateClass(selectedClass, "selectedClass");
            return AddGeneration(identity, selectedClass, ClassOrigin.Selected);
        }

        public ResidentialLotRecord ImportExisting(ResidentialLotIdentity identity, ResidentialClass bootstrapClass)
        {
            if (identity == null) throw new ArgumentNullException("identity");
            ValidateClass(bootstrapClass, "bootstrapClass");
            ResidentialLotRecord existing;
            if (TryGet(identity, out existing)) return existing;
            return AddGeneration(identity, bootstrapClass, ClassOrigin.ImportedNative);
        }

        public bool RefreshUpgradeIdentity(ResidentialLotIdentity before, ResidentialLotIdentity after)
        {
            if (before == null || after == null) return false;
            if (before.BuildingId != after.BuildingId || before.NativeBuildIndex != after.NativeBuildIndex ||
                before.OriginXBits != after.OriginXBits || before.OriginZBits != after.OriginZBits ||
                before.Service != after.Service)
                return false;
            ResidentialLotRecord current;
            if (!TryGet(before, out current)) return false;
            ResidentialLotRecord refreshed = new ResidentialLotRecord(after, current.Generation,
                current.DesignClass, current.OccupantClass, current.Origin);
            _records[after.BuildingId] = refreshed;
            return true;
        }

        public bool Remove(ushort buildingId)
        {
            if (buildingId == 0) return false;
            return _records.Remove(buildingId);
        }

        public ResidentialLotRecord[] GetRecords()
        {
            ResidentialLotRecord[] result = new ResidentialLotRecord[_records.Count];
            int index = 0;
            foreach (KeyValuePair<ushort, ResidentialLotRecord> pair in _records)
                result[index++] = pair.Value;
            return result;
        }

        internal static void ValidateClass(ResidentialClass value, string parameterName)
        {
            if ((byte)value < (byte)ResidentialClass.Low || (byte)value > (byte)ResidentialClass.High)
                throw new ArgumentOutOfRangeException(parameterName, "Unknown residential class.");
        }

        internal static void ValidateOrigin(ClassOrigin value, string parameterName)
        {
            if (value != ClassOrigin.ImportedNative && value != ClassOrigin.Selected)
                throw new ArgumentOutOfRangeException(parameterName, "Unknown class origin.");
        }

        private ResidentialLotRecord AddGeneration(ResidentialLotIdentity identity,
            ResidentialClass assignedClass, ClassOrigin origin)
        {
            bool replacing = _records.ContainsKey(identity.BuildingId);
            if (!replacing && _records.Count >= MaximumRecords)
                throw new InvalidOperationException("Residential class ledger is full.");
            if (_nextGeneration == 0 || _nextGeneration == ulong.MaxValue)
                throw new InvalidOperationException("Residential class generation space is exhausted.");

            ulong generation = _nextGeneration;
            ulong followingGeneration = generation + 1;
            ResidentialLotRecord record = new ResidentialLotRecord(identity, generation,
                assignedClass, assignedClass, origin);
            _records[identity.BuildingId] = record;
            _nextGeneration = followingGeneration;
            return record;
        }
    }
}
