using System;

namespace SC4Evolution.Core
{
    /// <summary>City-local presentation episodes, owned by one UI thread.</summary>
    public sealed class AdvisorAlertQueue
    {
        private const int AdvisorCount = 7;
        private readonly AdvisorBriefing[] _latestSeen = new AdvisorBriefing[AdvisorCount];
        private readonly AdvisorBriefing[] _pending = new AdvisorBriefing[AdvisorCount];
        private readonly bool[] _suspended = new bool[AdvisorCount];
        private long _lastSequence = -1;
        private bool _usable;
        private bool _hasPopulatedSample;

        public int PendingCount
        {
            get { int count = 0; for (int i = 0; i < AdvisorCount; i++) if (_pending[i] != null) count++; return count; }
        }

        public bool Observe(CabinetReport report)
        {
            if (report == null) { _usable = false; return false; }
            // The watermark includes invalid new batches: an older valid report
            // cannot re-enable delivery after a newer invalid one. Episode arrays
            // remain transactional; invalid data changes only watermark/usability.
            if (report.Sequence < 0 || report.Sequence <= _lastSequence) return false;
            _lastSequence = report.Sequence;
            if (!Valid(report)) { _usable = false; return false; }

            if (report.Snapshot.Population <= 0)
            {
                _usable = false;
                Array.Clear(_pending, 0, AdvisorCount);
                return true; // Founding guidance does not replace populated episodes.
            }

            _usable = true;
            bool first = !_hasPopulatedSample;
            for (int i = 0; i < AdvisorCount; i++)
            {
                AdvisorBriefing current = Copy(report.Briefings[i]);
                int index = (int)current.Advisor;
                _suspended[index] = current.IsSuspended;
                // Explicit missing-measurement metadata preserves the last actual
                // episode and any pending alert. It is unavailable until refreshed.
                if (current.IsSuspended) continue;
                AdvisorBriefing previous = _latestSeen[index];
                bool eligible = Eligible(current);
                bool enqueue = eligible && !first && (_pending[index] != null || Changed(previous, current));
                _latestSeen[index] = current;
                _pending[index] = enqueue ? current : null;
            }
            if (first)
            {
                int best = -1;
                for (int i = 0; i < AdvisorCount; i++)
                    if (!_suspended[i] && Eligible(_latestSeen[i]) && (best < 0 || HigherPriority(_latestSeen[i], _latestSeen[best]))) best = i;
                if (best >= 0) _pending[best] = _latestSeen[best];
                _hasPopulatedSample = true;
            }
            return true;
        }

        public bool TryTake(out AdvisorBriefing briefing)
        {
            if (!TryPeek(out briefing)) return false;
            _pending[(int)briefing.Advisor] = null;
            return true;
        }

        public bool TryPeek(out AdvisorBriefing briefing)
        {
            briefing = null;
            if (!_usable) return false;
            int best = -1;
            for (int i = 0; i < AdvisorCount; i++)
                if (!_suspended[i] && _pending[i] != null && (best < 0 || HigherPriority(_pending[i], _pending[best]))) best = i;
            if (best < 0) return false;
            briefing = Copy(_pending[best]);
            return true;
        }

        public void Acknowledge(AdvisorId advisor)
        {
            if (!ValidAdvisor(advisor)) throw new ArgumentOutOfRangeException("advisor");
            _pending[(int)advisor] = null;
        }

        public void Reset()
        {
            Array.Clear(_latestSeen, 0, AdvisorCount);
            Array.Clear(_pending, 0, AdvisorCount);
            Array.Clear(_suspended, 0, AdvisorCount);
            _lastSequence = -1; _usable = false; _hasPopulatedSample = false;
        }

        private static bool Valid(CabinetReport report)
        {
            if (report == null || report.Sequence < 0 || report.Snapshot == null || !report.Snapshot.DataValid || report.Snapshot.Population < 0 ||
                report.Briefings == null || report.Briefings.Length != AdvisorCount) return false;
            int seen = 0;
            foreach (AdvisorBriefing briefing in report.Briefings)
            {
                if (briefing == null || !ValidAdvisor(briefing.Advisor) || briefing.ConcernDays < 0 ||
                    !RequiredText(briefing.Name) || !RequiredText(briefing.Department) || !RequiredText(briefing.Title) || !RequiredText(briefing.Message) ||
                    briefing.Mood < AdvisorMood.Calm || briefing.Mood > AdvisorMood.Pleased ||
                    briefing.Severity < IssueSeverity.None || briefing.Severity > IssueSeverity.Critical ||
                    briefing.Action < AdvisorAction.Overview || briefing.Action > AdvisorAction.Garbage) return false;
                int bit = 1 << (int)briefing.Advisor;
                if ((seen & bit) != 0) return false;
                seen |= bit;
            }
            return seen == (1 << AdvisorCount) - 1;
        }
        private static bool RequiredText(string value)
        { if (value == null) return false; foreach (char character in value) if (!char.IsWhiteSpace(character)) return true; return false; }
        private static bool ValidAdvisor(AdvisorId advisor) { return advisor >= AdvisorId.Finance && advisor <= AdvisorId.Transport; }
        private static bool Eligible(AdvisorBriefing briefing)
        {
            return briefing != null && !briefing.IsSuspended && (briefing.Severity == IssueSeverity.Warning || briefing.Severity == IssueSeverity.Critical) &&
                (briefing.Mood == AdvisorMood.Concerned || briefing.Mood == AdvisorMood.Angry);
        }
        private static bool Changed(AdvisorBriefing previous, AdvisorBriefing current)
        {
            return !Eligible(previous) || !string.Equals(previous.Title, current.Title, StringComparison.Ordinal) || previous.Action != current.Action ||
                current.Severity > previous.Severity || MoodRank(current.Mood) > MoodRank(previous.Mood);
        }
        private static bool HigherPriority(AdvisorBriefing candidate, AdvisorBriefing previous)
        {
            if (candidate.Severity != previous.Severity) return candidate.Severity > previous.Severity;
            int mood = MoodRank(candidate.Mood), otherMood = MoodRank(previous.Mood);
            if (mood != otherMood) return mood > otherMood;
            return candidate.Advisor < previous.Advisor;
        }
        private static int MoodRank(AdvisorMood mood)
        { return mood == AdvisorMood.Angry ? 2 : mood == AdvisorMood.Concerned ? 1 : 0; }
        private static AdvisorBriefing Copy(AdvisorBriefing source)
        {
            return new AdvisorBriefing { Advisor = source.Advisor, Name = source.Name, Department = source.Department,
                Title = source.Title, Message = source.Message, MetricText = source.MetricText, Detail = source.Detail,
                Mood = source.Mood, Severity = source.Severity, Action = source.Action, ConcernDays = source.ConcernDays, IsSuspended = source.IsSuspended };
        }
    }
}
