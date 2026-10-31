using System;
using System.Collections.Generic;
using SC4Evolution.Core;
using SC4Evolution.Core.Decisions;
using SC4Evolution.Simulation;
using SC4Evolution.Simulation.Access;
using SC4Evolution.Simulation.Employment;
using SC4Evolution.Simulation.Freight;
using SC4Evolution.Simulation.Neighborhoods;
using SC4Evolution.Simulation.Taxation;

namespace SC4Evolution.Mod.Economy
{
    /// <summary>Explicit opt-in switches for the pure gameplay integration layer.
    /// A flag creates the corresponding session, but never applies anything to the game.
    /// </summary>
    [Flags]
    public enum GameplaySprint : byte
    {
        None = 0,
        Employment = 1,
        Neighborhoods = 2,
        Taxation = 4,
        AccessFreight = 8,
        Decisions = 16,
        // Aggregate diagnostic adapters. They are deliberately separate from
        // Taxation/AccessFreight: those sprints require native account/route
        // identity, while these flags opt into disclosed shadow estimates.
        ShadowTaxation = 32,
        ShadowAccessFreight = 64
    }

    /// <summary>Immutable configuration for one gameplay integration session. Defaults
    /// are the same defaults used by the existing sprint adapters; callers may inject
    /// reviewed tables and curves before opting a sprint in.</summary>
    public sealed class GameplayIntegrationConfig
    {
        private const GameplaySprint AllSprints = GameplaySprint.Employment |
            GameplaySprint.Neighborhoods | GameplaySprint.Taxation |
            GameplaySprint.AccessFreight | GameplaySprint.Decisions |
            GameplaySprint.ShadowTaxation | GameplaySprint.ShadowAccessFreight;

        public GameplaySprint EnabledSprints { get; private set; }
        public EmploymentQualification EmploymentQualification { get; private set; }
        public NeighborhoodAttractiveness NeighborhoodAttractiveness { get; private set; }
        public NeighborhoodThresholds NeighborhoodThresholds { get; private set; }
        public ITaxResponseCurve TaxResponseCurve { get; private set; }
        public int TaxWindowDays { get; private set; }
        public int TaxMaxEntries { get; private set; }
        public long AccessTimeBudgetTicks { get; private set; }
        public int FreightStockCapacityUnits { get; private set; }
        public AccessEffectBands AccessEffectBands { get; private set; }
        public uint DecisionDurationFrames { get; private set; }
        public uint DecisionCooldownFrames { get; private set; }

        public bool EmploymentEnabled { get { return IsEnabled(GameplaySprint.Employment); } }
        public bool NeighborhoodsEnabled { get { return IsEnabled(GameplaySprint.Neighborhoods); } }
        public bool TaxationEnabled { get { return IsEnabled(GameplaySprint.Taxation); } }
        public bool AccessFreightEnabled { get { return IsEnabled(GameplaySprint.AccessFreight); } }
        public bool DecisionsEnabled { get { return IsEnabled(GameplaySprint.Decisions); } }
        public bool ShadowTaxationEnabled { get { return IsEnabled(GameplaySprint.ShadowTaxation); } }
        public bool ShadowAccessFreightEnabled { get { return IsEnabled(GameplaySprint.ShadowAccessFreight); } }

        public static GameplayIntegrationConfig Off
        {
            get { return new GameplayIntegrationConfig(GameplaySprint.None); }
        }

        public GameplayIntegrationConfig(GameplaySprint enabledSprints)
            : this(enabledSprints, EmploymentQualification.Default,
                NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default,
                NeutralTaxResponseCurve.Instance, TaxAccountingModel.DefaultWindowDays,
                TaxAccountingModel.DefaultMaxEntries, EmploymentAccessMetric.DefaultTimeBudgetTicks,
                100, AccessEffectBands.Default, 600, 120)
        {
        }

        public GameplayIntegrationConfig(GameplaySprint enabledSprints,
            EmploymentQualification employmentQualification,
            NeighborhoodAttractiveness neighborhoodAttractiveness,
            NeighborhoodThresholds neighborhoodThresholds,
            ITaxResponseCurve taxResponseCurve,
            int taxWindowDays,
            int taxMaxEntries,
            long accessTimeBudgetTicks,
            int freightStockCapacityUnits,
            AccessEffectBands accessEffectBands,
            uint decisionDurationFrames,
            uint decisionCooldownFrames)
        {
            if ((((int)enabledSprints) & ~((int)AllSprints)) != 0)
                throw new ArgumentOutOfRangeException("enabledSprints");
            if (taxWindowDays <= 0) throw new ArgumentOutOfRangeException("taxWindowDays");
            if (taxMaxEntries <= 0) throw new ArgumentOutOfRangeException("taxMaxEntries");
            if (accessTimeBudgetTicks <= 0) throw new ArgumentOutOfRangeException("accessTimeBudgetTicks");
            if (freightStockCapacityUnits <= 0) throw new ArgumentOutOfRangeException("freightStockCapacityUnits");
            if (decisionDurationFrames == 0) throw new ArgumentOutOfRangeException("decisionDurationFrames");
            if (employmentQualification == null) throw new ArgumentNullException("employmentQualification");
            if (neighborhoodAttractiveness == null) throw new ArgumentNullException("neighborhoodAttractiveness");
            if (neighborhoodThresholds == null) throw new ArgumentNullException("neighborhoodThresholds");
            if (taxResponseCurve == null) throw new ArgumentNullException("taxResponseCurve");
            if (accessEffectBands == null) throw new ArgumentNullException("accessEffectBands");
            EnabledSprints = enabledSprints;
            EmploymentQualification = employmentQualification;
            NeighborhoodAttractiveness = neighborhoodAttractiveness;
            NeighborhoodThresholds = neighborhoodThresholds;
            TaxResponseCurve = taxResponseCurve;
            TaxWindowDays = taxWindowDays;
            TaxMaxEntries = taxMaxEntries;
            AccessTimeBudgetTicks = accessTimeBudgetTicks;
            FreightStockCapacityUnits = freightStockCapacityUnits;
            AccessEffectBands = accessEffectBands;
            DecisionDurationFrames = decisionDurationFrames;
            DecisionCooldownFrames = decisionCooldownFrames;
        }

        public bool IsEnabled(GameplaySprint sprint)
        {
            if (sprint == GameplaySprint.None) return false;
            return (EnabledSprints & sprint) == sprint;
        }
    }

    /// <summary>Immutable employment census block. Arrays are copied and validated by
    /// the adapter input when this block is consumed.</summary>
    public sealed class GameplayEmploymentObservation
    {
        private readonly int[] _workforceByCell;
        private readonly int[] _jobCapacityBySegment;
        private readonly int[] _jobFilledBySegment;
        public int[] WorkforceByCell { get { return (int[])_workforceByCell.Clone(); } }
        public int[] JobCapacityBySegment { get { return (int[])_jobCapacityBySegment.Clone(); } }
        public int[] JobFilledBySegment { get { return (int[])_jobFilledBySegment.Clone(); } }
        public int UnmappedJobCapacity { get; private set; }
        public int UnmappedFilledJobs { get; private set; }
        public int AvailableHomeUnits { get; private set; }
        public bool EntryPathOpen { get; private set; }

        public GameplayEmploymentObservation(int[] workforceByCell, int[] jobCapacityBySegment,
            int[] jobFilledBySegment, int unmappedJobCapacity, int unmappedFilledJobs,
            int availableHomeUnits, bool entryPathOpen)
        {
            if (workforceByCell == null) throw new ArgumentNullException("workforceByCell");
            if (jobCapacityBySegment == null) throw new ArgumentNullException("jobCapacityBySegment");
            if (jobFilledBySegment == null) throw new ArgumentNullException("jobFilledBySegment");
            if (unmappedJobCapacity < 0 || unmappedFilledJobs < 0)
                throw new ArgumentOutOfRangeException("unmappedJobCapacity");
            if (availableHomeUnits < 0) throw new ArgumentOutOfRangeException("availableHomeUnits");
            EmploymentSegments.ValidateCellVector(workforceByCell, "workforceByCell");
            EmploymentSegments.ValidateSegmentVector(jobCapacityBySegment, "jobCapacityBySegment");
            EmploymentSegments.ValidateSegmentVector(jobFilledBySegment, "jobFilledBySegment");
            ValidateNonNegative(workforceByCell, "workforceByCell");
            ValidateNonNegative(jobCapacityBySegment, "jobCapacityBySegment");
            ValidateNonNegative(jobFilledBySegment, "jobFilledBySegment");
            _workforceByCell = (int[])workforceByCell.Clone();
            _jobCapacityBySegment = (int[])jobCapacityBySegment.Clone();
            _jobFilledBySegment = (int[])jobFilledBySegment.Clone();
            UnmappedJobCapacity = unmappedJobCapacity;
            UnmappedFilledJobs = unmappedFilledJobs;
            AvailableHomeUnits = availableHomeUnits;
            EntryPathOpen = entryPathOpen;
        }

        private static void ValidateNonNegative(int[] values, string name)
        {
            for (int i = 0; i < values.Length; i++)
                if (values[i] < 0) throw new ArgumentException("Values cannot be negative.", name);
        }
    }

    /// <summary>One immutable residential lot observation with its sampled neighborhood
    /// factors. The lot id is the native identity; build index distinguishes generations.
    /// </summary>
    public sealed class GameplayNeighborhoodLotObservation
    {
        public ushort LotId { get; private set; }
        public uint BuildIndex { get; private set; }
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
        public NeighborhoodFactors Factors { get; private set; }

        public GameplayNeighborhoodLotObservation(ushort lotId, uint buildIndex, int lotClass,
            ResidentialStockPhase phase, int homeUnits, int emptyHomeUnits, int liveHomeUnits,
            int otherNonemptyHomeUnits, int settledPeople, int incomingPeople,
            int peopleWithoutHomeUnitReference, int invalidMemberReferences,
            NeighborhoodFactors factors)
        {
            // ResidentialStockLot owns all count, class and phase validation. Constructing
            // this temporary object has no side effects and keeps the two DTO contracts equal.
            ResidentialStockLot validated = new ResidentialStockLot(lotId, buildIndex, lotClass, phase,
                homeUnits, emptyHomeUnits, liveHomeUnits, otherNonemptyHomeUnits,
                settledPeople, incomingPeople, peopleWithoutHomeUnitReference, invalidMemberReferences);
            if (!factors.IsValid()) throw new ArgumentException("Factors must be finite values on 0..100.", "factors");
            LotId = validated.BuildingId;
            BuildIndex = validated.ObservedBuildIndex;
            LotClass = validated.LotClass;
            Phase = validated.Phase;
            HomeUnits = validated.HomeUnits;
            EmptyHomeUnits = validated.EmptyHomeUnits;
            LiveHomeUnits = validated.LiveHomeUnits;
            OtherNonemptyHomeUnits = validated.OtherNonemptyHomeUnits;
            SettledPeople = validated.SettledPeople;
            IncomingPeople = validated.IncomingPeople;
            PeopleWithoutHomeUnitReference = validated.PeopleWithoutHomeUnitReference;
            InvalidMemberReferences = validated.InvalidMemberReferences;
            Factors = factors;
        }

        internal ResidentialStockLot ToStockLot()
        {
            return new ResidentialStockLot(LotId, BuildIndex, LotClass, Phase, HomeUnits,
                EmptyHomeUnits, LiveHomeUnits, OtherNonemptyHomeUnits, SettledPeople,
                IncomingPeople, PeopleWithoutHomeUnitReference, InvalidMemberReferences);
        }
    }

    /// <summary>Immutable neighborhood census block. Lots must be ordered strictly by
    /// native id so reports cannot silently attach a result to another building.</summary>
    public sealed class GameplayNeighborhoodObservation
    {
        private readonly GameplayNeighborhoodLotObservation[] _lots;
        private readonly int[] _demandByClass;

        public GameplayNeighborhoodLotObservation[] Lots { get { return (GameplayNeighborhoodLotObservation[])_lots.Clone(); } }
        public int[] DemandByClass { get { return (int[])_demandByClass.Clone(); } }

        public GameplayNeighborhoodObservation(GameplayNeighborhoodLotObservation[] lots, int[] demandByClass)
        {
            if (lots == null) throw new ArgumentNullException("lots");
            if (demandByClass == null) throw new ArgumentNullException("demandByClass");
            if (demandByClass.Length != 3) throw new ArgumentException("Expected three demand classes.", "demandByClass");
            ValidateNonNegative(demandByClass, "demandByClass");
            HashSet<uint> buildIdentities = new HashSet<uint>();
            for (int i = 0; i < lots.Length; i++)
            {
                if (lots[i] == null) throw new ArgumentException("Null lot observation.", "lots");
                if (i > 0 && lots[i - 1].LotId >= lots[i].LotId)
                    throw new ArgumentException("Neighborhood lots must be strictly ordered by LotId.", "lots");
                if (!buildIdentities.Add(lots[i].BuildIndex))
                    throw new ArgumentException("Neighborhood build identities must be unique in one observation.", "lots");
            }
            _lots = (GameplayNeighborhoodLotObservation[])lots.Clone();
            _demandByClass = (int[])demandByClass.Clone();
        }

        private static void ValidateNonNegative(int[] values, string name)
        {
            for (int i = 0; i < values.Length; i++)
                if (values[i] < 0) throw new ArgumentException("Values cannot be negative.", name);
        }
    }

    /// <summary>Immutable tax period observation. The account vectors are copied and the
    /// existing TaxAccountingInput performs the canonical size/rate checks.</summary>
    public sealed class GameplayTaxObservation
    {
        private readonly int[] _nominalRatePercent;
        private readonly long[] _baseIncome;
        private readonly long[] _collectedActual;
        public int Day { get; private set; }
        public int[] NominalRatePercent { get { return (int[])_nominalRatePercent.Clone(); } }
        public long[] BaseIncome { get { return (long[])_baseIncome.Clone(); } }
        public long[] CollectedActual { get { return (long[])_collectedActual.Clone(); } }

        public GameplayTaxObservation(int day, int[] nominalRatePercent, long[] baseIncome,
            long[] collectedActual)
        {
            TaxAccountingInput validated = new TaxAccountingInput(day, nominalRatePercent,
                baseIncome, collectedActual);
            Day = validated.Day;
            _nominalRatePercent = (int[])validated.NominalRatePercent.Clone();
            _baseIncome = validated.BaseIncome;
            _collectedActual = validated.CollectedActual;
        }
    }

    /// <summary>Immutable access/freight observations. Origin ids are ordered and unique
    /// independently in each vector; this makes position-aligned batches deterministic.
    /// </summary>
    public sealed class GameplayAccessFreightObservation
    {
        private readonly EmploymentAccessObservation[] _employment;
        private readonly FreightOutflowObservation[] _freight;

        public EmploymentAccessObservation[] Employment { get { return (EmploymentAccessObservation[])_employment.Clone(); } }
        public FreightOutflowObservation[] Freight { get { return (FreightOutflowObservation[])_freight.Clone(); } }

        public GameplayAccessFreightObservation(EmploymentAccessObservation[] employment,
            FreightOutflowObservation[] freight)
        {
            if (employment == null) throw new ArgumentNullException("employment");
            if (freight == null) throw new ArgumentNullException("freight");
            ValidateEmploymentOrder(employment);
            ValidateFreightOrder(freight);
            _employment = (EmploymentAccessObservation[])employment.Clone();
            _freight = (FreightOutflowObservation[])freight.Clone();
        }

        private static void ValidateEmploymentOrder(EmploymentAccessObservation[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0 && values[i - 1].OriginId >= values[i].OriginId)
                    throw new ArgumentException("Employment origins must be strictly ordered.", "employment");
                if (values[i].RouteAttempts < 0 || values[i].TripsCompleted < 0 ||
                    values[i].ArrivalFailures < 0 || values[i].TravelTimeTicks < 0 ||
                    values[i].JobCapacityAtDestinations < 0 || values[i].FilledJobsAtDestinations < 0)
                    throw new ArgumentException("Employment observations cannot be negative.", "employment");
                if (values[i].TripsCompleted + values[i].ArrivalFailures > values[i].RouteAttempts)
                    throw new ArgumentException("Employment outcomes cannot exceed attempts.", "employment");
                if (values[i].FilledJobsAtDestinations > values[i].JobCapacityAtDestinations)
                    throw new ArgumentException("Filled jobs cannot exceed capacity.", "employment");
            }
        }

        private static void ValidateFreightOrder(FreightOutflowObservation[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0 && values[i - 1].OriginId >= values[i].OriginId)
                    throw new ArgumentException("Freight origins must be strictly ordered.", "freight");
                if (values[i].RouteAttempts < 0 || values[i].DeliveriesCompleted < 0 ||
                    values[i].TransferFailures < 0 || values[i].StockUnits < 0)
                    throw new ArgumentException("Freight observations cannot be negative.", "freight");
                if (values[i].DeliveriesCompleted + values[i].TransferFailures > values[i].RouteAttempts)
                    throw new ArgumentException("Freight outcomes cannot exceed attempts.", "freight");
            }
        }
    }

    /// <summary>One immutable decision condition. AdvisorRecommendation is the existing
    /// sprint-06 value object; this DTO adds the UI text identity needed to resolve a notice.
    /// </summary>
    public sealed class GameplayDecisionObservation
    {
        public string CauseKey { get; private set; }
        public bool Active { get; private set; }
        public AdvisorRecommendation Recommendation { get; private set; }
        public DecisionUrgency Urgency { get; private set; }
        public string AdvisorName { get; private set; }
        public string TitleFormat { get; private set; }
        public string MessageFormat { get; private set; }

        public GameplayDecisionObservation(string causeKey, bool active,
            AdvisorRecommendation recommendation, DecisionUrgency urgency, string advisorName,
            string titleFormat, string messageFormat)
        {
            if (string.IsNullOrEmpty(causeKey)) throw new ArgumentNullException("causeKey");
            if (recommendation == null) throw new ArgumentNullException("recommendation");
            if (string.IsNullOrEmpty(advisorName)) throw new ArgumentNullException("advisorName");
            if (double.IsNaN(recommendation.IndicatorValue) || double.IsInfinity(recommendation.IndicatorValue) ||
                double.IsNaN(recommendation.ExpectedIndicatorAfterAction) ||
                double.IsInfinity(recommendation.ExpectedIndicatorAfterAction))
                throw new ArgumentException("Decision indicators must be finite.", "recommendation");
            CauseKey = causeKey;
            Active = active;
            // Keep the DTO detached from any caller-owned recommendation instance. The
            // engine may mark opportunity costs on this copy during conflict analysis.
            Recommendation = AdvisorRecommendation.Create(recommendation.Advisor,
                recommendation.Department, recommendation.Consequence, recommendation.SuggestedAction,
                recommendation.IndicatorName, recommendation.IndicatorValue,
                recommendation.ExpectedIndicatorAfterAction, recommendation.TargetKey);
            Urgency = urgency;
            AdvisorName = advisorName;
            TitleFormat = titleFormat ?? "{0}";
            MessageFormat = messageFormat ?? "{0}";
        }
    }

    /// <summary>Immutable frame-local census payload. A section may be null when its
    /// opt-in is enabled but native sampling is not available for this frame; no report is
    /// fabricated in that case. The city identity and strict frame order protect stateful
    /// sessions from cross-city or stale observations.</summary>
    public sealed class GameplayIntegrationObservation
    {
        private readonly GameplayDecisionObservation[] _decisions;

        public string CityIdentity { get; private set; }
        public uint Frame { get; private set; }
        public GameplayEmploymentObservation Employment { get; private set; }
        public GameplayNeighborhoodObservation Neighborhoods { get; private set; }
        public GameplayTaxObservation Taxation { get; private set; }
        public GameplayAccessFreightObservation AccessFreight { get; private set; }
        public GameplayDecisionObservation[] Decisions { get { return (GameplayDecisionObservation[])_decisions.Clone(); } }

        public GameplayIntegrationObservation(string cityIdentity, uint frame,
            GameplayEmploymentObservation employment,
            GameplayNeighborhoodObservation neighborhoods,
            GameplayTaxObservation taxation,
            GameplayAccessFreightObservation accessFreight,
            GameplayDecisionObservation[] decisions)
        {
            if (string.IsNullOrEmpty(cityIdentity)) throw new ArgumentNullException("cityIdentity");
            if (decisions == null) throw new ArgumentNullException("decisions");
            for (int i = 0; i < decisions.Length; i++)
            {
                if (decisions[i] == null) throw new ArgumentException("Null decision observation.", "decisions");
                if (i > 0 && string.CompareOrdinal(decisions[i - 1].CauseKey, decisions[i].CauseKey) >= 0)
                    throw new ArgumentException("Decision observations must be strictly ordered by CauseKey.", "decisions");
            }
            CityIdentity = cityIdentity;
            Frame = frame;
            Employment = employment;
            Neighborhoods = neighborhoods;
            Taxation = taxation;
            AccessFreight = accessFreight;
            _decisions = (GameplayDecisionObservation[])decisions.Clone();
        }

        public GameplayIntegrationObservation(string cityIdentity, uint frame,
            GameplayEmploymentObservation employment,
            GameplayNeighborhoodObservation neighborhoods,
            GameplayTaxObservation taxation,
            GameplayAccessFreightObservation accessFreight)
            : this(cityIdentity, frame, employment, neighborhoods, taxation, accessFreight,
                new GameplayDecisionObservation[0])
        {
        }
    }

    /// <summary>Immutable result for one frame. Existing sprint reports remain optional;
    /// the orchestrator only publishes them and never applies their meters to native state.
    /// </summary>
    public sealed class GameplayIntegrationSnapshot
    {
        private readonly DecisionNotice[] _decisionNotices;

        public string CityIdentity { get; private set; }
        public uint Frame { get; private set; }
        public GameplaySprint EnabledSprints { get; private set; }
        public EmploymentMarketReport Employment { get; private set; }
        public NeighborhoodReport Neighborhoods { get; private set; }
        public TaxAccountingReport Taxation { get; private set; }
        public AccessFreightBatch AccessFreight { get; private set; }
        public int NeighborhoodLotsWithoutFactorSamples { get; private set; }
        public DecisionNotice[] DecisionNotices { get { return (DecisionNotice[])_decisionNotices.Clone(); } }

        // Verbose aliases keep the snapshot easy to consume from bridges and tests.
        public EmploymentMarketReport EmploymentReport { get { return Employment; } }
        public NeighborhoodReport NeighborhoodReport { get { return Neighborhoods; } }
        public TaxAccountingReport TaxReport { get { return Taxation; } }
        public AccessFreightBatch AccessFreightReport { get { return AccessFreight; } }
        public DecisionNotice[] Notices { get { return DecisionNotices; } }

        internal GameplayIntegrationSnapshot(string cityIdentity, uint frame, GameplaySprint enabledSprints,
            EmploymentMarketReport employment, NeighborhoodReport neighborhoods,
            TaxAccountingReport taxation, AccessFreightBatch accessFreight,
            int lotsWithoutFactorSamples, DecisionNotice[] decisionNotices)
        {
            CityIdentity = cityIdentity;
            Frame = frame;
            EnabledSprints = enabledSprints;
            Employment = employment;
            Neighborhoods = neighborhoods;
            Taxation = taxation;
            AccessFreight = accessFreight;
            NeighborhoodLotsWithoutFactorSamples = lotsWithoutFactorSamples;
            _decisionNotices = decisionNotices == null ? new DecisionNotice[0] : (DecisionNotice[])decisionNotices.Clone();
        }
    }

    /// <summary>Pure frame orchestrator for sprints 01/02/04/05/06. It constructs only
    /// the existing sessions and DecisionEngine, consumes immutable observations, and
    /// returns reports. It has no reference to Cities, Harmony, Unity, money, population,
    /// demand application or any native writer.</summary>
    public sealed class GameplayIntegrationSession
    {
        private string _cityIdentity;
        private readonly GameplayIntegrationConfig _config;
        private readonly EmploymentSegmentSession _employment;
        private readonly NeighborhoodCycleSession _neighborhoods;
        private readonly TaxAccountingSession _taxation;
        private readonly AccessFreightSession _accessFreight;
        private readonly DecisionEngine _decisions;
        private bool _hasFrame;
        private uint _lastFrame;
        private bool _hasTaxDay;
        private int _lastTaxDay;
        private bool _bindIdentityOnFirstObservation;

        public GameplayIntegrationSession(string cityIdentity, GameplayIntegrationConfig config)
            : this(cityIdentity, config, false)
        {
        }

        private GameplayIntegrationSession(string cityIdentity, GameplayIntegrationConfig config,
            bool bindIdentityOnFirstObservation)
        {
            if (!bindIdentityOnFirstObservation && string.IsNullOrEmpty(cityIdentity))
                throw new ArgumentNullException("cityIdentity");
            if (config == null) throw new ArgumentNullException("config");
            _cityIdentity = cityIdentity;
            _bindIdentityOnFirstObservation = bindIdentityOnFirstObservation;
            _config = config;
            _employment = EmploymentSegmentSession.TryCreate(config.IsEnabled(GameplaySprint.Employment), config.EmploymentQualification);
            _neighborhoods = NeighborhoodCycleSession.TryCreate(config.IsEnabled(GameplaySprint.Neighborhoods),
                config.NeighborhoodAttractiveness, config.NeighborhoodThresholds);
            _taxation = TaxAccountingSession.TryCreate(config.IsEnabled(GameplaySprint.Taxation),
                config.TaxResponseCurve, config.TaxWindowDays, config.TaxMaxEntries);
            _accessFreight = AccessFreightSession.TryCreate(config.IsEnabled(GameplaySprint.AccessFreight),
                config.AccessTimeBudgetTicks, config.FreightStockCapacityUnits, config.AccessEffectBands);
            _decisions = config.IsEnabled(GameplaySprint.Decisions)
                ? new DecisionEngine(config.DecisionDurationFrames, config.DecisionCooldownFrames)
                : null;
        }

        public static GameplayIntegrationSession Create(string cityIdentity, GameplayIntegrationConfig config)
        {
            return new GameplayIntegrationSession(cityIdentity, config);
        }

        /// <summary>Adapter-facing factory used when the native bridge cannot know the
        /// city identity until its first census. The first observation binds the identity;
        /// every later frame must match it. Disabled opt-in returns null.</summary>
        public static GameplayIntegrationSession TryCreate(bool enabled, GameplayIntegrationConfig config)
        {
            if (!enabled) return null;
            if (config == null) throw new ArgumentNullException("config");
            return new GameplayIntegrationSession(null, config, true);
        }

        public string CityIdentity { get { return _cityIdentity; } }
        public GameplayIntegrationConfig Config { get { return _config; } }
        public GameplaySprint EnabledSprints { get { return _config.EnabledSprints; } }
        public EmploymentSegmentSession Employment { get { return _employment; } }
        public NeighborhoodCycleSession Neighborhoods { get { return _neighborhoods; } }
        public NeighborhoodCycleSession Neighborhood { get { return _neighborhoods; } }
        public TaxAccountingSession Taxation { get { return _taxation; } }
        public TaxAccountingSession Tax { get { return _taxation; } }
        public AccessFreightSession AccessFreight { get { return _accessFreight; } }
        public DecisionEngine Decisions { get { return _decisions; } }
        public uint LastFrame { get { return _lastFrame; } }

        public GameplayIntegrationSnapshot Apply(GameplayIntegrationObservation observation)
        {
            if (observation == null) throw new ArgumentNullException("observation");
            if (_bindIdentityOnFirstObservation)
            {
                _cityIdentity = observation.CityIdentity;
                _bindIdentityOnFirstObservation = false;
            }
            if (!string.Equals(observation.CityIdentity, _cityIdentity, StringComparison.Ordinal))
                throw new ArgumentException("Observation belongs to another city.", "observation");
            if (_hasFrame && observation.Frame <= _lastFrame)
                throw new ArgumentException("Gameplay observations must advance strictly by frame.", "observation");
            if (_taxation != null && observation.Taxation != null && _hasTaxDay &&
                observation.Taxation.Day < _lastTaxDay)
                throw new ArgumentException("Tax observations must not move backward in day.", "observation");

            EmploymentMarketReport employment = null;
            NeighborhoodReport neighborhoods = null;
            TaxAccountingReport taxation = null;
            AccessFreightBatch accessFreight = null;
            int missingNeighborhoodFactors = 0;

            if (_employment != null && observation.Employment != null)
            {
                GameplayEmploymentObservation value = observation.Employment;
                employment = _employment.Apply(observation.Frame, value.WorkforceByCell,
                    value.JobCapacityBySegment, value.JobFilledBySegment, value.UnmappedJobCapacity,
                    value.UnmappedFilledJobs, value.AvailableHomeUnits, value.EntryPathOpen);
            }
            if (_neighborhoods != null && observation.Neighborhoods != null)
            {
                GameplayNeighborhoodObservation value = observation.Neighborhoods;
                List<ResidentialStockLot> stockLots = new List<ResidentialStockLot>(value.Lots.Length);
                Dictionary<ushort, NeighborhoodFactors> factors = new Dictionary<ushort, NeighborhoodFactors>();
                GameplayNeighborhoodLotObservation[] lots = value.Lots;
                for (int i = 0; i < lots.Length; i++)
                {
                    stockLots.Add(lots[i].ToStockLot());
                    factors.Add(lots[i].LotId, lots[i].Factors);
                }
                ResidentialStockSnapshot stock = new ResidentialStockSnapshot(observation.Frame, stockLots);
                neighborhoods = _neighborhoods.Observe(observation.Frame, stock, factors,
                    value.DemandByClass, out missingNeighborhoodFactors);
            }
            if (_taxation != null && observation.Taxation != null)
            {
                GameplayTaxObservation value = observation.Taxation;
                taxation = _taxation.Apply(new TaxAccountingInput(value.Day,
                    value.NominalRatePercent, value.BaseIncome, value.CollectedActual));
                _lastTaxDay = value.Day;
                _hasTaxDay = true;
            }
            if (_accessFreight != null && observation.AccessFreight != null)
            {
                GameplayAccessFreightObservation value = observation.AccessFreight;
                accessFreight = _accessFreight.Evaluate(value.Employment, value.Freight);
            }

            DecisionNotice[] notices = new DecisionNotice[0];
            if (_config.IsEnabled(GameplaySprint.Decisions))
                notices = EvaluateDecisions(observation.Frame, observation.Decisions);

            _lastFrame = observation.Frame;
            _hasFrame = true;
            return new GameplayIntegrationSnapshot(_cityIdentity, observation.Frame,
                _config.EnabledSprints, employment, neighborhoods, taxation, accessFreight,
                missingNeighborhoodFactors, notices);
        }

        private DecisionNotice[] EvaluateDecisions(uint frame, GameplayDecisionObservation[] observations)
        {
            List<DecisionEvent> emitted = new List<DecisionEvent>();
            List<GameplayDecisionObservation> emittedInputs = new List<GameplayDecisionObservation>();
            for (int i = 0; i < observations.Length; i++)
            {
                GameplayDecisionObservation value = observations[i];
                DecisionEvent decision = _decisions.Observe(frame, value.CauseKey, value.Active,
                    value.Recommendation, value.Urgency);
                if (decision != null)
                {
                    emitted.Add(decision);
                    emittedInputs.Add(value);
                }
            }
            // Expire only after current conditions are observed. This preserves the
            // DecisionEngine contract: an active condition at its duration boundary is
            // refreshed, while an unobserved condition recovers by disappearance.
            _decisions.Expire(frame);
            if (emitted.Count == 0) return new DecisionNotice[0];
            DecisionEngine.MarkConflicts(emitted);
            DecisionNotice[] notices = new DecisionNotice[emitted.Count];
            // Emitted order is the validated CauseKey order, so no dictionary order leaks.
            for (int i = 0; i < emitted.Count; i++)
            {
                GameplayDecisionObservation value = emittedInputs[i];
                notices[i] = DecisionEngine.BuildNotice(emitted[i], frame, value.AdvisorName,
                    value.TitleFormat, value.MessageFormat);
            }
            return notices;
        }
    }
}
