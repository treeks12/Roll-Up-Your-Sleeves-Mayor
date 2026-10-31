using System;
using System.Collections.Generic;

namespace SC4Evolution.Simulation
{
    public enum ResidentialStockPhase : byte { Constructing = 1, Completed = 2, Upgrading = 3, Unavailable = 4 }

    /// <summary>Observed counts only. HOME units are households, never nominal person capacity.</summary>
    public sealed class ResidentialStockLot
    {
        public ushort BuildingId { get; private set; }
        public uint ObservedBuildIndex { get; private set; }
        // Zero means no validated lot class. It is not inferred from native level or wealth.
        public int LotClass { get; private set; }
        public ResidentialStockPhase Phase { get; private set; }
        public int HomeUnits { get; private set; }
        public int EmptyHomeUnits { get; private set; }
        public int LiveHomeUnits { get; private set; }
        public int OtherNonemptyHomeUnits { get; private set; }
        public int SettledPeople { get; private set; }
        public int IncomingPeople { get; private set; }
        public int PeopleWithoutHomeUnitReference { get; private set; }
        public int InvalidMemberReferences { get; private set; }

        public ResidentialStockLot(ushort buildingId, uint observedBuildIndex, int lotClass, ResidentialStockPhase phase,
            int homeUnits, int emptyHomeUnits, int liveHomeUnits, int otherNonemptyHomeUnits,
            int settledPeople, int incomingPeople, int peopleWithoutHomeUnitReference, int invalidMemberReferences)
        {
            if (buildingId == 0) throw new ArgumentOutOfRangeException("buildingId");
            ValidateClass(lotClass);
            ValidatePhase(phase);
            int[] counts = { homeUnits, emptyHomeUnits, liveHomeUnits, otherNonemptyHomeUnits,
                settledPeople, incomingPeople, peopleWithoutHomeUnitReference, invalidMemberReferences };
            foreach (int value in counts) if (value < 0) throw new ArgumentOutOfRangeException("counts");
            if ((long)emptyHomeUnits + liveHomeUnits + otherNonemptyHomeUnits != homeUnits)
                throw new ArgumentException("The three observed household states must partition HOME units.");
            if ((long)peopleWithoutHomeUnitReference > (long)settledPeople + incomingPeople)
                throw new ArgumentException("Unlinked people must be part of the observed live population.");
            BuildingId = buildingId; ObservedBuildIndex = observedBuildIndex; LotClass = lotClass; Phase = phase;
            HomeUnits = homeUnits; EmptyHomeUnits = emptyHomeUnits; LiveHomeUnits = liveHomeUnits;
            OtherNonemptyHomeUnits = otherNonemptyHomeUnits; SettledPeople = settledPeople; IncomingPeople = incomingPeople;
            PeopleWithoutHomeUnitReference = peopleWithoutHomeUnitReference; InvalidMemberReferences = invalidMemberReferences;
        }

        internal static void ValidateClass(int value)
        { if (value < 0 || value > 3) throw new ArgumentOutOfRangeException("lotClass"); }
        internal static void ValidatePhase(ResidentialStockPhase value)
        { if (value < ResidentialStockPhase.Constructing || value > ResidentialStockPhase.Unavailable) throw new ArgumentOutOfRangeException("phase"); }
    }

    public sealed class ResidentialStockTotals
    {
        public int Lots { get; private set; }
        public long HomeUnits { get; private set; }
        public long EmptyHomeUnits { get; private set; }
        public long LiveHomeUnits { get; private set; }
        public long OtherNonemptyHomeUnits { get; private set; }
        public long SettledPeople { get; private set; }
        public long IncomingPeople { get; private set; }
        public long PeopleWithoutHomeUnitReference { get; private set; }
        public long InvalidMemberReferences { get; private set; }

        internal ResidentialStockTotals(int lots, long[] counts)
        {
            Lots = lots; HomeUnits = counts[0]; EmptyHomeUnits = counts[1]; LiveHomeUnits = counts[2];
            OtherNonemptyHomeUnits = counts[3]; SettledPeople = counts[4]; IncomingPeople = counts[5];
            PeopleWithoutHomeUnitReference = counts[6]; InvalidMemberReferences = counts[7];
        }
    }

    /// <summary>A frame-local inventory, not a capacity profile or authority for a later building generation.</summary>
    public sealed class ResidentialStockSnapshot
    {
        private readonly ResidentialStockLot[] _lots;
        private readonly ResidentialStockTotals[] _buckets;
        public uint Frame { get; private set; }
        public ResidentialStockTotals Totals { get; private set; }
        public ResidentialStockLot[] Lots { get { return (ResidentialStockLot[])_lots.Clone(); } }

        public ResidentialStockSnapshot(uint frame, IEnumerable<ResidentialStockLot> lots)
        {
            if (lots == null) throw new ArgumentNullException("lots");
            List<ResidentialStockLot> observed = new List<ResidentialStockLot>();
            HashSet<ushort> seen = new HashSet<ushort>();
            long[][] counts = new long[16][];
            int[] sizes = new int[16];
            long[] all = new long[8];
            for (int i = 0; i < counts.Length; i++) counts[i] = new long[8];
            foreach (ResidentialStockLot lot in lots)
            {
                if (lot == null) throw new ArgumentException("Null lot observation.", "lots");
                if (!seen.Add(lot.BuildingId)) throw new ArgumentException("Duplicate building in one stock observation.", "lots");
                int bucket = Index(lot.LotClass, lot.Phase);
                sizes[bucket]++;
                long[] values = { lot.HomeUnits, lot.EmptyHomeUnits, lot.LiveHomeUnits, lot.OtherNonemptyHomeUnits,
                    lot.SettledPeople, lot.IncomingPeople, lot.PeopleWithoutHomeUnitReference, lot.InvalidMemberReferences };
                for (int field = 0; field < values.Length; field++)
                { counts[bucket][field] = checked(counts[bucket][field] + values[field]); all[field] = checked(all[field] + values[field]); }
                observed.Add(lot);
            }
            _lots = observed.ToArray(); _buckets = new ResidentialStockTotals[16];
            for (int i = 0; i < counts.Length; i++) _buckets[i] = new ResidentialStockTotals(sizes[i], counts[i]);
            Frame = frame; Totals = new ResidentialStockTotals(_lots.Length, all);
        }

        public ResidentialStockTotals GetBucket(int lotClass, ResidentialStockPhase phase)
        { ResidentialStockLot.ValidateClass(lotClass); ResidentialStockLot.ValidatePhase(phase); return _buckets[Index(lotClass, phase)]; }

        private static int Index(int lotClass, ResidentialStockPhase phase) { return lotClass * 4 + (int)phase - 1; }
    }
}
