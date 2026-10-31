using System;
using System.Collections.Generic;

namespace SC4Evolution.Core.Decisions
{
    public enum DecisionUrgency : byte { Info = 0, Attention = 1, Urgent = 2, Critical = 3 }

    /// <summary>Kind of measurable consequence an event points at (sprint 06). Every kind
    /// maps to a quantity one of the gameplay systems actually measures; the event never
    /// claims a cause outside this list.</summary>
    public enum DecisionConsequence : byte
    {
        None = 0,
        Unemployment = 1,
        UnfillableJobs = 2,
        HousingShortage = 3,
        NeighborhoodDecline = 4,
        TaxRevenueLoss = 5,
        DemandSuppression = 6,
        FreightGridlock = 7,
        JobAccessLoss = 8,
        ServiceUnderfunding = 9
    }

    /// <summary>An advisor's concrete request: the problem kind, the measurable indicator
    /// backing it, the suggested action and the department paying the opportunity cost.</summary>
    public sealed class AdvisorRecommendation
    {
        public AdvisorId Advisor { get; internal set; }
        public string Department { get; internal set; }
        public DecisionConsequence Consequence { get; internal set; }
        public string SuggestedAction { get; internal set; }
        /// <summary>Indicator name and current value backing the request (evidence).</summary>
        public string IndicatorName { get; internal set; }
        public double IndicatorValue { get; internal set; }
        /// <summary>Indicator value the suggested action is expected to move toward; the
        /// advisor is honest when the measurement does not change after acting.</summary>
        public double ExpectedIndicatorAfterAction { get; internal set; }
        /// <summary>Domain key of the affected building/neighborhood when identity is
        /// valid; empty for city-wide requests.</summary>
        public string TargetKey { get; internal set; }
        /// <summary>True when following this recommendation conflicts with at least one
        /// other department's current request.</summary>
        public bool HasOpportunityCost { get; internal set; }

        /// <summary>Builds a recommendation in one call; the single construction path for
        /// hosts and tests so every field stays explicit.</summary>
        public static AdvisorRecommendation Create(AdvisorId advisor, string department,
            DecisionConsequence consequence, string suggestedAction, string indicatorName,
            double indicatorValue, double expectedIndicatorAfterAction, string targetKey)
        {
            if (string.IsNullOrEmpty(suggestedAction)) throw new ArgumentNullException("suggestedAction");
            if (string.IsNullOrEmpty(indicatorName)) throw new ArgumentNullException("indicatorName");
            AdvisorRecommendation recommendation = new AdvisorRecommendation();
            recommendation.Advisor = advisor;
            recommendation.Department = department ?? string.Empty;
            recommendation.Consequence = consequence;
            recommendation.SuggestedAction = suggestedAction;
            recommendation.IndicatorName = indicatorName;
            recommendation.IndicatorValue = indicatorValue;
            recommendation.ExpectedIndicatorAfterAction = expectedIndicatorAfterAction;
            recommendation.TargetKey = targetKey ?? string.Empty;
            return recommendation;
        }
    }

    /// <summary>One decision event: cause, urgency, remaining duration, evidence and the
    /// recommendation. Created only by the engine; immutable for consumers.</summary>
    public sealed class DecisionEvent
    {
        public long Id { get; internal set; }
        public uint FirstFrame { get; internal set; }
        public uint ExpiresAtFrame { get; internal set; }
        public AdvisorRecommendation Recommendation { get; internal set; }
        public DecisionUrgency Urgency { get; internal set; }
        /// <summary>Frames until a same-cause re-alert is allowed; the engine enforces it,
        /// so one problem cannot fire every frame.</summary>
        public uint CooldownFrames { get; internal set; }
        /// <summary>Last frame this event actually alerted; UI snapshots compare against
        /// it to reject stale notices.</summary>
        public uint LastAlertFrame { get; internal set; }

        /// <summary>Test/host helper to assemble a detached event (no engine state). Used
        /// by conflict analysis over externally collected events.</summary>
        public static DecisionEvent Detached(AdvisorRecommendation recommendation, DecisionUrgency urgency)
        {
            if (recommendation == null) throw new ArgumentNullException("recommendation");
            DecisionEvent decision = new DecisionEvent();
            decision.Recommendation = recommendation;
            decision.Urgency = urgency;
            return decision;
        }
    }

    /// <summary>Native-style UI payload for one visible alert: everything the notice needs,
    /// already resolved (no simulation buffers touched by the UI thread).</summary>
    public sealed class DecisionNotice
    {
        public long EventId { get; internal set; }
        public AdvisorId Advisor { get; internal set; }
        public string AdvisorName { get; internal set; }
        public string Department { get; internal set; }
        public string Title { get; internal set; }
        public string Message { get; internal set; }
        public string IndicatorText { get; internal set; }
        public string TargetText { get; internal set; }
        public DecisionUrgency Urgency { get; internal set; }
        public uint ShownAtFrame { get; internal set; }
        public uint ExpiresAtFrame { get; internal set; }
    }
}
