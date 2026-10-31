using System;
using System.Collections.Generic;

namespace SC4Evolution.Simulation.Taxation
{
    /// <summary>One account's nominal-rate history over simulation days (sprint 04).
    /// Semantics follow the project's existing tax model: the effective rate is the MAXIMUM
    /// nominal rate over an inclusive trailing window of days. Raising a rate therefore
    /// reacts immediately, while a cut only takes effect for the demand meter once the old
    /// high leaves the window — the documented reaction delay. Samples must arrive with
    /// strictly increasing days; duplicates and out-of-order entries are rejected.</summary>
    public sealed class TaxReactionHistory
    {
        private readonly int _windowDays;
        private readonly int _maxEntries;
        private readonly List<int> _days = new List<int>();
        private readonly List<int> _rates = new List<int>();

        public TaxReactionHistory(int windowDays, int maxEntries)
        {
            if (windowDays < 1) throw new ArgumentOutOfRangeException("windowDays", "Window must cover at least one day.");
            if (maxEntries < 1) throw new ArgumentOutOfRangeException("maxEntries", "History must hold at least one entry.");
            _windowDays = windowDays;
            _maxEntries = maxEntries;
        }

        public int WindowDays
        {
            get { return _windowDays; }
        }

        public int Count
        {
            get { return _days.Count; }
        }

        public void Record(int day, int ratePercent)
        {
            if (day < 0) throw new ArgumentOutOfRangeException("day");
            TaxRateValidation.Validate(ratePercent);
            if (_days.Count > 0 && day <= _days[_days.Count - 1])
                throw new ArgumentException("Samples must use strictly increasing days.", "day");
            _days.Add(day);
            _rates.Add(ratePercent);
            if (_days.Count > _maxEntries)
            {
                _days.RemoveAt(0);
                _rates.RemoveAt(0);
            }
        }

        /// <summary>Maximum nominal rate among samples inside the inclusive window ending at
        /// `day`. With no samples in the window the answer is 0: an untracked account has no
        /// reaction memory, and callers record the current rate every period.</summary>
        public int EffectiveRate(int day)
        {
            if (day < 0) throw new ArgumentOutOfRangeException("day");
            int firstDay = day - _windowDays + 1;
            int effective = 0;
            for (int i = _days.Count - 1; i >= 0; i--)
            {
                if (_days[i] < firstDay) break;
                if (_rates[i] > effective) effective = _rates[i];
            }
            return effective;
        }

        internal void CopyEntriesForSave(List<int> days, List<int> rates)
        {
            for (int i = 0; i < _days.Count; i++) { days.Add(_days[i]); rates.Add(_rates[i]); }
        }

        internal static TaxReactionHistory FromEntries(int windowDays, int maxEntries, int[] days, int[] rates)
        {
            TaxReactionHistory history = new TaxReactionHistory(windowDays, maxEntries);
            if (days == null || rates == null) throw new ArgumentNullException("days");
            if (days.Length != rates.Length) throw new ArgumentException("Day/rate arrays must align.");
            for (int i = 0; i < days.Length; i++) history.Record(days[i], rates[i]);
            return history;
        }
    }
}
