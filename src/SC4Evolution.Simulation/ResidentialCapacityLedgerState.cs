using System;

namespace SC4Evolution.Simulation
{
    public enum ResidentialCapacityPhase : byte { Constructing = 1, Completed = 2, Upgrading = 3, Unavailable = 4 }
    public enum ResidentialCapacityWindowPhase : byte { NotQualified = 1, Open = 2, Exhausted = 3, Invalidated = 4 }

    public sealed class ResidentialCapacityRecord
    {
        public Guid ClassLedgerId { get; private set; }
        public ResidentialLotIdentity Identity { get; private set; }
        public ulong Generation { get; private set; }
        public ResidentialClass Class { get; private set; }
        public ResidentialNominalCategory Category { get; private set; }
        public int NativeLevel { get; private set; }
        public long HomeCount { get; private set; }
        public long CapacityQ { get; private set; }
        public ResidentialCapacityPhase Phase { get; private set; }
        // Mirrors the class record's origin. It is not capacity-import provenance.
        public ClassOrigin Origin { get; private set; }
        public bool IsActive { get { return Phase != ResidentialCapacityPhase.Unavailable; } }

        public ResidentialCapacityRecord(Guid classLedgerId, ResidentialLotIdentity identity, ulong generation,
            ResidentialClass lotClass, ResidentialNominalCategory category, int nativeLevel, long homeCount,
            ResidentialCapacityPhase phase, ClassOrigin origin)
        {
            if (classLedgerId == Guid.Empty) throw new ArgumentException("Class ledger identity is required.", "classLedgerId");
            if (identity == null) throw new ArgumentNullException("identity");
            if (generation == 0) throw new ArgumentOutOfRangeException("generation");
            ResidentialClassLedger.ValidateClass(lotClass, "lotClass");
            ResidentialClassLedger.ValidateOrigin(origin, "origin");
            ValidatePhase(phase);
            ResidentialHomeCountBounds bounds = ResidentialNominalCapacity.GetHomeCountBounds(category, nativeLevel, identity.Width, identity.Length);
            if (homeCount < bounds.MinimumHomes || homeCount > bounds.MaximumHomes) throw new ArgumentOutOfRangeException("homeCount", "HOME count is outside the supported native rounding envelope.");
            ClassLedgerId = classLedgerId; Identity = identity; Generation = generation; Class = lotClass;
            Category = category; NativeLevel = nativeLevel; HomeCount = homeCount; Phase = phase; Origin = origin;
            CapacityQ = ResidentialNominalCapacity.ComputeNominalQ(category, homeCount);
        }
        internal ResidentialCapacityRecord WithPhase(ResidentialCapacityPhase phase)
        { return new ResidentialCapacityRecord(ClassLedgerId, Identity, Generation, Class, Category, NativeLevel, HomeCount, phase, Origin); }
        internal static void ValidatePhase(ResidentialCapacityPhase phase)
        { if (phase < ResidentialCapacityPhase.Constructing || phase > ResidentialCapacityPhase.Unavailable) throw new ArgumentOutOfRangeException("phase"); }
    }

    // A fingerprint before allocation. It deliberately has no building ID.
    public sealed class ResidentialCapacityLotSpec
    {
        private readonly ResidentialLotIdentity _shape;
        public ResidentialClass Class { get; private set; }
        public ResidentialNominalCategory Category { get; private set; }
        public int NativeLevel { get; private set; }
        public long MinimumQ { get; private set; }
        public long ReserveQ { get; private set; }
        public ResidentialCapacityLotSpec(uint nativeBuildIndex, int originXBits, int originZBits, string prefabName,
            int service, int subService, byte width, byte length, ResidentialClass lotClass,
            ResidentialNominalCategory category, int nativeLevel)
        {
            _shape = new ResidentialLotIdentity(1, nativeBuildIndex, originXBits, originZBits, prefabName, service, subService, width, length);
            ResidentialClassLedger.ValidateClass(lotClass, "lotClass");
            ResidentialHomeCountBounds bounds = ResidentialNominalCapacity.GetHomeCountBounds(category, nativeLevel, width, length);
            Class = lotClass; Category = category; NativeLevel = nativeLevel; MinimumQ = bounds.MinimumNominalQ; ReserveQ = bounds.MaximumNominalQ;
        }
        internal bool Matches(ResidentialCapacityRecord record)
        {
            ResidentialLotIdentity value = record.Identity;
            return record.Class == Class && record.Category == Category && record.NativeLevel == NativeLevel &&
                value.NativeBuildIndex == _shape.NativeBuildIndex && value.OriginXBits == _shape.OriginXBits && value.OriginZBits == _shape.OriginZBits &&
                value.PrefabName == _shape.PrefabName && value.Service == _shape.Service && value.SubService == _shape.SubService &&
                value.Width == _shape.Width && value.Length == _shape.Length && record.CapacityQ >= MinimumQ && record.CapacityQ <= ReserveQ;
        }
        internal bool ReplacesAt(ResidentialLotIdentity old)
        { return old.OriginXBits == _shape.OriginXBits && old.OriginZBits == _shape.OriginZBits && old.Service == _shape.Service; }
    }

    public sealed class ResidentialCapacityWindow
    {
        public ResidentialClass Class { get; private set; }
        public long Day { get; private set; }
        public ResidentialCapacityWindowPhase Phase { get; private set; }
        public float OpeningRaw { get; private set; }
        public float Overbuild { get; private set; }
        public long DesiredQ { get; private set; }
        public long DesiredMaximumQ { get; private set; }
        public long CommittedQ { get; private set; }
        public long OpeningStockRevision { get; private set; }
        public long OpeningExternalRevision { get; private set; }

        public ResidentialCapacityWindow(ResidentialClass lotClass, long day, ResidentialCapacityWindowPhase phase,
            float openingRaw, float overbuild, long desiredQ, long desiredMaximumQ, long committedQ,
            long openingStockRevision, long openingExternalRevision)
        {
            ResidentialClassLedger.ValidateClass(lotClass, "lotClass");
            if (day < 0 || openingStockRevision < 0 || openingExternalRevision < 0 || committedQ < 0) throw new ArgumentOutOfRangeException("day");
            if (phase < ResidentialCapacityWindowPhase.NotQualified || phase > ResidentialCapacityWindowPhase.Invalidated) throw new ArgumentOutOfRangeException("phase");
            long expected, maximum;
            ResidentialCapacityLedger.CalculateQuota(openingRaw, overbuild, out expected, out maximum);
            if (desiredQ != expected || desiredMaximumQ != maximum) throw new ArgumentException("Quota does not match the opening RAW and overbuild.");
            if (desiredQ == 0 && (phase != ResidentialCapacityWindowPhase.NotQualified || committedQ != 0)) throw new ArgumentException("An unqualified day cannot carry a different window state.");
            if (phase == ResidentialCapacityWindowPhase.NotQualified && (desiredQ != 0 || committedQ != 0)) throw new ArgumentException("Unqualified window cannot have capacity.");
            if (phase == ResidentialCapacityWindowPhase.Open && (desiredQ == 0 || committedQ >= desiredQ)) throw new ArgumentException("Open window is not eligible.");
            if (phase == ResidentialCapacityWindowPhase.Exhausted && (desiredQ == 0 || committedQ < desiredQ)) throw new ArgumentException("Exhausted window has not reached its target.");
            long normalMaximum = phase == ResidentialCapacityWindowPhase.Exhausted ? checked(desiredMaximumQ + 7L) : desiredMaximumQ;
            if (phase != ResidentialCapacityWindowPhase.Invalidated && committedQ > normalMaximum) throw new ArgumentException("Normal replacement floor permits at most7 Q excess in an exhausted window; larger overruns require explicit invalidation.");
            Class = lotClass; Day = day; Phase = phase; OpeningRaw = openingRaw; Overbuild = overbuild;
            DesiredQ = desiredQ; DesiredMaximumQ = desiredMaximumQ; CommittedQ = committedQ;
            OpeningStockRevision = openingStockRevision; OpeningExternalRevision = openingExternalRevision;
        }
        internal ResidentialCapacityWindow Change(ResidentialCapacityWindowPhase phase, long committed)
        { return new ResidentialCapacityWindow(Class, Day, phase, OpeningRaw, Overbuild, DesiredQ, DesiredMaximumQ, committed, OpeningStockRevision, OpeningExternalRevision); }
    }

    // Bounded codec-facing DTO. Restore validates cross-record references.
    // Contains no executable commands or reservation tokens.
    public sealed class ResidentialCapacityLedgerState
    {
        private readonly byte[] _profileHash;
        private readonly byte[] _accountingHash;
        private readonly ResidentialCapacityRecord[] _records;
        private readonly ResidentialCapacityWindow[] _windows;
        public Guid ClassLedgerId { get; private set; }
        public string AccountingPolicyName { get; private set; }
        public uint AccountingPolicyVersion { get; private set; }
        public byte[] AccountingPolicyHash { get { return (byte[])_accountingHash.Clone(); } }
        public string ProfileName { get; private set; }
        public uint ProfileVersion { get; private set; }
        public byte[] ProfileHash { get { return (byte[])_profileHash.Clone(); } }
        public long FirstPlanningDay { get; private set; }
        public long StockRevision { get; private set; }
        public long ExternalRevision { get; private set; }
        public long PublishedStockRevision { get; private set; }
        public long PublishedExternalRevision { get; private set; }
        public ResidentialCapacityRecord[] Records { get { return (ResidentialCapacityRecord[])_records.Clone(); } }
        // Exactly three slots, in class order; null means that class never opened a day.
        public ResidentialCapacityWindow[] Windows { get { return (ResidentialCapacityWindow[])_windows.Clone(); } }
        public ResidentialCapacityLedgerState(Guid classLedgerId, string accountingPolicyName, uint accountingPolicyVersion, byte[] accountingPolicyHash,
            string profileName, uint profileVersion, byte[] profileHash,
            long firstPlanningDay, long stockRevision, long externalRevision, long publishedStockRevision, long publishedExternalRevision,
            ResidentialCapacityRecord[] records, ResidentialCapacityWindow[] windows)
        {
            if (classLedgerId == Guid.Empty) throw new ArgumentException("Class ledger identity is required.");
            if (accountingPolicyName != ResidentialCapacityLedger.PolicyName || accountingPolicyVersion != ResidentialCapacityLedger.PolicyVersion || !SameHash(accountingPolicyHash, ResidentialCapacityLedger.PolicyHash))
                throw new ArgumentException("Only the exact known capacity accounting policy is accepted.");
            if (profileName != ResidentialNominalCapacity.PolicyName || profileVersion != ResidentialNominalCapacity.PolicyVersion || !SameHash(profileHash, ResidentialNominalCapacity.PolicyHash))
                throw new ArgumentException("Only the exact known nominal capacity profile is accepted.");
            if (firstPlanningDay < 0 || stockRevision < 0 || externalRevision < 0 || publishedStockRevision < -1 || publishedStockRevision > stockRevision || publishedExternalRevision < -1 || publishedExternalRevision > externalRevision)
                throw new ArgumentOutOfRangeException("stockRevision");
            if (records == null || records.Length > ResidentialClassLedger.MaximumRecords || windows == null || windows.Length != 3) throw new ArgumentException("State arrays are absent or outside their bounds.");
            ClassLedgerId = classLedgerId; AccountingPolicyName = accountingPolicyName; AccountingPolicyVersion = accountingPolicyVersion; _accountingHash = (byte[])accountingPolicyHash.Clone();
            ProfileName = profileName; ProfileVersion = profileVersion; _profileHash = (byte[])profileHash.Clone();
            FirstPlanningDay = firstPlanningDay; StockRevision = stockRevision; ExternalRevision = externalRevision;
            PublishedStockRevision = publishedStockRevision; PublishedExternalRevision = publishedExternalRevision;
            _records = (ResidentialCapacityRecord[])records.Clone(); _windows = (ResidentialCapacityWindow[])windows.Clone();
        }
        private static bool SameHash(byte[] left, byte[] right)
        { if (left == null || left.Length != right.Length) return false; for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false; return true; }
    }

    public sealed class ResidentialCapacityLedgerSnapshot
    {
        private readonly long[] _stock, _reserved;
        public long StockRevision { get; private set; }
        public long ExternalRevision { get; private set; }
        public bool PublicationCurrent { get; private set; }
        public bool SessionValid { get; private set; }
        public int PendingReservations { get; private set; }
        public int UnresolvedCreations { get; private set; }
        public int Records { get; private set; }
        public long[] StockQ { get { return (long[])_stock.Clone(); } }
        public long[] ReservedQuotaQ { get { return (long[])_reserved.Clone(); } }
        public bool CanCapture { get { return PendingReservations == 0 && UnresolvedCreations == 0; } }
        internal ResidentialCapacityLedgerSnapshot(long stockRevision, long externalRevision, bool current, bool valid,
            int pending, int unresolved, int records, long[] stock, long[] reserved)
        { StockRevision = stockRevision; ExternalRevision = externalRevision; PublicationCurrent = current; SessionValid = valid;
            PendingReservations = pending; UnresolvedCreations = unresolved; Records = records; _stock = (long[])stock.Clone(); _reserved = (long[])reserved.Clone(); }
    }
}
