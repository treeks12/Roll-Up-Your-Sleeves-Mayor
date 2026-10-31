using System;
using System.Collections.Generic;

namespace SC4Evolution.Core.Decisions
{
    /// <summary>Engine turning measurable conditions into decision events (sprint 06).
    /// Rules the handoff demands, enforced structurally:
    /// - Every event carries cause (consequence kind + evidence indicator), severity
    ///   (urgency), duration (expiry frame), and a suggested action with an expected
    ///   measurable outcome.
    /// - The same cause never re-alerts inside its cooldown window, so no per-frame spam.
    /// - Expired events recover (disappear) and their cooldowns lapse; recovery is a
    ///   first-class outcome.
    /// - Two departments may emit conflicting recommendations for the same target when the
    ///   decision has a real opportunity cost; both are marked, neither is hidden.
    /// - The engine consumes only already-published reports/values: no native buffer is
    ///   read here, and the UI receives resolved notices only.
    /// Deterministic; all thresholds are injectable.</summary>
    public sealed class DecisionEngine
    {
        private readonly uint _defaultDurationFrames;
        private readonly uint _defaultCooldownFrames;
        private readonly Dictionary<string, DecisionEvent> _active = new Dictionary<string, DecisionEvent>();
        private long _nextId;

        public DecisionEngine(uint defaultDurationFrames, uint defaultCooldownFrames)
        {
            if (defaultDurationFrames == 0) throw new ArgumentOutOfRangeException("defaultDurationFrames", "Events need a positive duration.");
            _defaultDurationFrames = defaultDurationFrames;
            _defaultCooldownFrames = defaultCooldownFrames;
        }

        public int ActiveCount
        {
            get { return _active.Count; }
        }

        /// <summary>Observes one measured condition. `causeKey` identifies the problem
        /// (e.g. "unemployment:W$EQ1"); `activeNow` tells whether the condition currently
        /// holds; the indicator backs the recommendation with evidence. Returns the event
        /// when a NEW alert should be shown, null when suppressed (already active/cooldown)
        /// or when the condition ended (recovery).</summary>
        public DecisionEvent Observe(uint frame, string causeKey, bool activeNow,
            AdvisorRecommendation recommendation, DecisionUrgency urgency)
        {
            if (string.IsNullOrEmpty(causeKey)) throw new ArgumentNullException("causeKey");
            if (recommendation == null) throw new ArgumentNullException("recommendation");
            DecisionEvent existing;
            _active.TryGetValue(causeKey, out existing);
            if (!activeNow)
            {
                if (existing != null)
                {
                    _active.Remove(causeKey);
                    existing.ExpiresAtFrame = frame; // recovery: event ends now
                }
                return null;
            }
            if (existing != null)
            {
                existing.Recommendation = recommendation;
                existing.Urgency = urgency;
                // Same-cause re-alert only after the cooldown, and the event itself must
                // still be within its lifetime.
                if (frame >= existing.LastAlertFrame + existing.CooldownFrames &&
                    frame < existing.ExpiresAtFrame)
                {
                    existing.LastAlertFrame = frame;
                    return existing;
                }
                if (frame >= existing.ExpiresAtFrame)
                {
                    // Duration elapsed while the condition persists: refresh the window.
                    existing.ExpiresAtFrame = frame + _defaultDurationFrames;
                    existing.LastAlertFrame = frame;
                    return existing;
                }
                return null;
            }
            DecisionEvent created = new DecisionEvent();
            created.Id = _nextId++;
            created.FirstFrame = frame;
            created.ExpiresAtFrame = frame + _defaultDurationFrames;
            created.CooldownFrames = _defaultCooldownFrames;
            created.LastAlertFrame = frame;
            created.Recommendation = recommendation;
            created.Urgency = urgency;
            _active[causeKey] = created;
            return created;
        }

        /// <summary>Drops events whose duration elapsed while unobserved. Called by the
        /// host each update; recovery by disappearance is explicit.</summary>
        public void Expire(uint frame)
        {
            List<string> expired = null;
            foreach (KeyValuePair<string, DecisionEvent> pair in _active)
            {
                if (frame >= pair.Value.ExpiresAtFrame)
                {
                    if (expired == null) expired = new List<string>();
                    expired.Add(pair.Key);
                }
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++) _active.Remove(expired[i]);
        }

        /// <summary>Marks recommendations that conflict over the same target: when two
        /// departments ask for opposite actions on one target, both get HasOpportunityCost.
        /// Conflicts are surfaced, never silently resolved.</summary>
        public static int MarkConflicts(IList<DecisionEvent> events)
        {
            if (events == null) throw new ArgumentNullException("events");
            int conflicts = 0;
            for (int i = 0; i < events.Count; i++) events[i].Recommendation.HasOpportunityCost = false;
            for (int i = 0; i < events.Count; i++)
            {
                for (int j = i + 1; j < events.Count; j++)
                {
                    AdvisorRecommendation a = events[i].Recommendation, b = events[j].Recommendation;
                    if (a.Advisor == b.Advisor) continue;
                    if (string.IsNullOrEmpty(a.TargetKey) || a.TargetKey != b.TargetKey) continue;
                    if (a.Consequence == b.Consequence) continue; // same problem, no conflict
                    a.HasOpportunityCost = true;
                    b.HasOpportunityCost = true;
                    conflicts++;
                }
            }
            return conflicts;
        }

        /// <summary>Resolves a notice for the native-style UI: text built on the
        /// simulation side, indicator and target included when identity is valid.</summary>
        public static DecisionNotice BuildNotice(DecisionEvent decision, uint shownAtFrame,
            string advisorName, string titleFormat, string messageFormat)
        {
            if (decision == null) throw new ArgumentNullException("decision");
            if (string.IsNullOrEmpty(advisorName)) throw new ArgumentNullException("advisorName");
            AdvisorRecommendation recommendation = decision.Recommendation;
            DecisionNotice notice = new DecisionNotice();
            notice.EventId = decision.Id;
            notice.Advisor = recommendation.Advisor;
            notice.AdvisorName = advisorName;
            notice.Department = recommendation.Department;
            notice.Title = string.Format(titleFormat ?? "{0}", recommendation.SuggestedAction);
            notice.Message = string.Format(messageFormat ?? "{0}", recommendation.SuggestedAction);
            notice.IndicatorText = recommendation.IndicatorName + ": " + recommendation.IndicatorValue.ToString("0.##") +
                " → " + recommendation.ExpectedIndicatorAfterAction.ToString("0.##");
            notice.TargetText = string.IsNullOrEmpty(recommendation.TargetKey) ? string.Empty : recommendation.TargetKey;
            notice.Urgency = decision.Urgency;
            notice.ShownAtFrame = shownAtFrame;
            notice.ExpiresAtFrame = decision.ExpiresAtFrame;
            return notice;
        }

        /// <summary>A notice is stale when its snapshot frame predates the event's last
        /// alert: the UI must reject it instead of showing outdated advice.</summary>
        public static bool IsStale(DecisionNotice notice, DecisionEvent current)
        {
            if (notice == null) throw new ArgumentNullException("notice");
            if (current == null) return true;
            return notice.EventId != current.Id || notice.ShownAtFrame < current.LastAlertFrame;
        }

        public byte[] Save()
        {
            return DecisionStateCodec.Save(this);
        }

        public static DecisionEngine Restore(uint defaultDurationFrames, uint defaultCooldownFrames, byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            DecisionEngine engine = new DecisionEngine(defaultDurationFrames, defaultCooldownFrames);
            DecisionStateCodec.ReadInto(engine, data);
            return engine;
        }

        internal IEnumerable<KeyValuePair<string, DecisionEvent>> Entries()
        {
            return _active;
        }

        internal void InstallEntry(string causeKey, DecisionEvent decision)
        {
            _active[causeKey] = decision;
            if (decision.Id >= _nextId) _nextId = decision.Id + 1;
        }
    }
}
