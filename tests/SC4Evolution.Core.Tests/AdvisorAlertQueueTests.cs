using System;
using SC4Evolution.Core;

namespace SC4Evolution.Core.Tests
{
    internal static class AdvisorAlertQueueTests
    {
        private static int _checks;
        internal static int Run()
        {
            _checks = 0;
            FirstPopulatedPriority(); EpisodesAndWorsening(); PendingRefreshAndRecovery();
            InvalidSuspension(); ZeroPopulation(); BoundedOrderAndAcknowledge();
            ResetAndSequences(); MutationIsolation(); MalformedBatchesAreTransactional();
            PeekAndTextValidation();
            RealTrafficSuspension(); RealPendingSuspension(); RealUrbanSuspension();
            return _checks;
        }
        private static void FirstPopulatedPriority()
        {
            AdvisorAlertQueue queue = new AdvisorAlertQueue();
            CabinetReport report = Report(0);
            Issue(report, AdvisorId.Finance, IssueSeverity.Warning, AdvisorMood.Angry);
            Issue(report, AdvisorId.UrbanPlanning, IssueSeverity.Critical, AdvisorMood.Concerned);
            Issue(report, AdvisorId.Utilities, IssueSeverity.Critical, AdvisorMood.Angry);
            Issue(report, AdvisorId.EducationHealth, IssueSeverity.Critical, AdvisorMood.Angry);
            Issue(report, AdvisorId.Transport, IssueSeverity.Critical, AdvisorMood.Pleased);
            Array.Reverse(report.Briefings);
            Equal(true, queue.Observe(report), "sequence zero accepted");
            Equal(1, queue.PendingCount, "first populated report has one alert");
            Equal(AdvisorId.EducationHealth, Take(queue).Advisor, "severity, explicit mood rank, then advisor ID");
            report.Sequence = 1; queue.Observe(report);
            Empty(queue, "other initial issues are seen, not delayed avalanche");
            report.Sequence = 2;
            Find(report, AdvisorId.Finance).Severity = IssueSeverity.Critical;
            queue.Observe(report);
            Equal(AdvisorId.Finance, Take(queue).Advisor, "a previously suppressed initial issue can worsen");
        }
        private static void EpisodesAndWorsening()
        {
            AdvisorAlertQueue queue = new AdvisorAlertQueue(); queue.Observe(Report(1));
            CabinetReport report = Report(2); Issue(report, AdvisorId.Finance, IssueSeverity.Warning, AdvisorMood.Concerned);
            queue.Observe(report); Take(queue);
            report.Sequence = 3; Find(report, AdvisorId.Finance).Message = "same issue, fresh details";
            queue.Observe(report); Empty(queue, "consumed episode does not repeat in new sequence");
            report.Sequence = 4; Find(report, AdvisorId.Finance).Severity = IssueSeverity.Critical;
            queue.Observe(report); Equal(IssueSeverity.Critical, Take(queue).Severity, "severity worsening queues");
            report.Sequence = 5; Find(report, AdvisorId.Finance).Mood = AdvisorMood.Angry;
            queue.Observe(report); Equal(AdvisorMood.Angry, Take(queue).Mood, "mood worsening queues");
            report.Sequence = 6; Find(report, AdvisorId.Finance).Mood = AdvisorMood.Concerned;
            queue.Observe(report); Empty(queue, "less angry is not worsening");
            report.Sequence = 7; Find(report, AdvisorId.Finance).Title = "different problem";
            queue.Observe(report); Equal("different problem", Take(queue).Title, "new title queues");
            report.Sequence = 8; Find(report, AdvisorId.Finance).Action = AdvisorAction.Water;
            queue.Observe(report); Equal(AdvisorAction.Water, Take(queue).Action, "new action queues");
            report.Sequence = 9; Find(report, AdvisorId.Finance).Mood = AdvisorMood.Pleased;
            queue.Observe(report); Empty(queue, "Pleased numeric3 never outranks Concerned or creates urgency");
            report.Sequence = 10; Find(report, AdvisorId.Finance).Mood = AdvisorMood.Concerned;
            queue.Observe(report); Take(queue);
            report.Sequence = 11; Find(report, AdvisorId.Finance).Severity = IssueSeverity.Notice; Find(report, AdvisorId.Finance).Mood = AdvisorMood.Angry;
            queue.Observe(report); Empty(queue, "angry Notice is not an automatic alert");
            report.Sequence = 12; Find(report, AdvisorId.Finance).Severity = IssueSeverity.Critical; Find(report, AdvisorId.Finance).Mood = AdvisorMood.Calm;
            queue.Observe(report); Empty(queue, "Calm Critical is not an automatic alert");
        }
        private static void PendingRefreshAndRecovery()
        {
            AdvisorAlertQueue queue = new AdvisorAlertQueue(); queue.Observe(Report(1));
            CabinetReport report = Report(2); Issue(report, AdvisorId.Utilities, IssueSeverity.Critical, AdvisorMood.Angry);
            queue.Observe(report); Equal(1, queue.PendingCount, "modal can retain an alert");
            report.Sequence = 3;
            AdvisorBriefing utilities = Find(report, AdvisorId.Utilities);
            utilities.Message = "current reading"; utilities.MetricText = "new metric"; utilities.Detail = "current detail";
            utilities.Severity = IssueSeverity.Warning; utilities.Mood = AdvisorMood.Concerned; utilities.ConcernDays = 20;
            queue.Observe(report);
            AdvisorBriefing result = Take(queue);
            Equal("current reading", result.Message, "pending content refreshed"); Equal("new metric", result.MetricText, "metric refreshed");
            Equal("current detail", result.Detail, "detail refreshed"); Equal(20, result.ConcernDays, "days refreshed");
            Equal(IssueSeverity.Warning, result.Severity, "pending deescalation refreshed");
            report.Sequence = 4; utilities.Title = "another shortage"; queue.Observe(report);
            report.Sequence = 5; utilities.Severity = IssueSeverity.None; utilities.Mood = AdvisorMood.Pleased; queue.Observe(report);
            Empty(queue, "recovery removes pending before modal closes");
            report.Sequence = 6; utilities.Severity = IssueSeverity.Warning; utilities.Mood = AdvisorMood.Concerned;
            queue.Observe(report); Equal(AdvisorId.Utilities, Take(queue).Advisor, "recovered episode can start again");
        }
        private static void InvalidSuspension()
        {
            AdvisorAlertQueue queue = new AdvisorAlertQueue(); queue.Observe(Report(1));
            CabinetReport active = Report(2); Issue(active, AdvisorId.Transport, IssueSeverity.Warning, AdvisorMood.Concerned);
            queue.Observe(active);
            CabinetReport invalid = Report(3); invalid.Snapshot.DataValid = false;
            Equal(false, queue.Observe(invalid), "new invalid report rejected");
            Equal(1, queue.PendingCount, "invalid preserves pending"); Unavailable(queue, "invalid prevents taking retained alert");
            Equal(false, queue.Observe(active), "duplicate cannot resume availability"); Unavailable(queue, "duplicate keeps suspension");
            active.Sequence = 4; Find(active, AdvisorId.Transport).Message = "fresh after unavailable";
            Equal(true, queue.Observe(active), "valid report resumes");
            Equal("fresh after unavailable", Take(queue).Message, "retained pending reconciles before delivery");
            invalid.Sequence = 5; queue.Observe(invalid);
            active.Sequence = 6; queue.Observe(active); Empty(queue, "consumed episode does not return after invalid data");
            Equal(false, queue.Observe(null), "null suspends"); Unavailable(queue, "null unavailable");
            active.Sequence = 10; Find(active, AdvisorId.Transport).Title = "newer real issue"; queue.Observe(active);
            invalid.Sequence = 12; queue.Observe(invalid);
            active.Sequence = 11; Equal(false, queue.Observe(active), "stale valid after newer invalid is ignored");
            Equal(1, queue.PendingCount, "stale valid retains pending"); Unavailable(queue, "stale valid cannot reactivate");
            invalid.Snapshot.DataValid = true; invalid.Sequence = 12;
            Equal(false, queue.Observe(invalid), "same sequence cannot correct invalid batch"); Unavailable(queue, "invalid watermark persists");
            active.Sequence = 13; queue.Observe(active); Take(queue);
        }
        private static void ZeroPopulation()
        {
            AdvisorAlertQueue queue = new AdvisorAlertQueue();
            CabinetReport empty = Report(1); empty.Snapshot.Population = 0;
            Issue(empty, AdvisorId.Finance, IssueSeverity.Critical, AdvisorMood.Angry);
            Equal(true, queue.Observe(empty), "valid zero city accepted"); Empty(queue, "zero city no urgency");
            CabinetReport active = Report(2); Issue(active, AdvisorId.Finance, IssueSeverity.Warning, AdvisorMood.Concerned);
            Issue(active, AdvisorId.Utilities, IssueSeverity.Critical, AdvisorMood.Angry);
            queue.Observe(active); Equal(1, queue.PendingCount, "zero did not consume first-populated throttle");
            empty.Sequence = 3; queue.Observe(empty); Equal(0, queue.PendingCount, "zero clears pending");
            active.Sequence = 4; queue.Observe(active); Empty(queue, "same episodes do not return after zero");
            active.Sequence = 5; Find(active, AdvisorId.Finance).Title = "new real problem";
            queue.Observe(active); Equal(AdvisorId.Finance, Take(queue).Advisor, "changed episode after zero queues");
        }
        private static void BoundedOrderAndAcknowledge()
        {
            AdvisorAlertQueue queue = new AdvisorAlertQueue(); queue.Observe(Report(1));
            CabinetReport report = Report(2);
            for (int i = 0; i < 7; i++) Issue(report, (AdvisorId)i, IssueSeverity.Warning, AdvisorMood.Concerned);
            queue.Observe(report); Equal(7, queue.PendingCount, "bounded one per advisor");
            for (int sequence = 3; sequence < 30; sequence++) { report.Sequence = sequence; queue.Observe(report); Equal(7, queue.PendingCount, "repeated retained reports remain bounded"); }
            queue.Acknowledge(AdvisorId.Utilities); Equal(6, queue.PendingCount, "manual read clears only one pending");
            report.Sequence = 30; queue.Observe(report); Equal(6, queue.PendingCount, "ack preserves episode history");
            for (int i = 0; i < 7; i++) if (i != (int)AdvisorId.Utilities) Equal((AdvisorId)i, Take(queue).Advisor, "deterministic advisor order");
            Empty(queue, "queue drained");
            bool rejected = false; try { queue.Acknowledge((AdvisorId)7); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Equal(true, rejected, "bad acknowledge advisor is explicit");
        }
        private static void ResetAndSequences()
        {
            AdvisorAlertQueue queue = new AdvisorAlertQueue();
            CabinetReport report = Report(20); Issue(report, AdvisorId.Finance, IssueSeverity.Warning, AdvisorMood.Concerned);
            queue.Observe(report);
            CabinetReport negative = Report(-1); negative.Snapshot = null;
            Equal(false, queue.Observe(negative), "negative is ignored without suspending");
            CabinetReport duplicateInvalid = Report(20); duplicateInvalid.Snapshot = null;
            Equal(false, queue.Observe(duplicateInvalid), "duplicate ignored before payload validation");
            Take(queue); report.Sequence = 19; Equal(false, queue.Observe(report), "old sequence ignored"); Empty(queue, "old did not recreate episode");
            report.Sequence = long.MaxValue; queue.Observe(report); Equal(false, queue.Observe(report), "maximum duplicate stable");
            queue.Reset(); Equal(0, queue.PendingCount, "reset clears pending"); Unavailable(queue, "reset unavailable until observation");
            report.Sequence = 0; Equal(true, queue.Observe(report), "new city sequence can restart"); Take(queue);
            queue.Reset(); CabinetReport invalid = Report(-1); Equal(false, queue.Observe(invalid), "negative sequence invalid");
        }
        private static void MutationIsolation()
        {
            AdvisorAlertQueue queue = new AdvisorAlertQueue();
            CabinetReport report = Report(1); Issue(report, AdvisorId.Finance, IssueSeverity.Critical, AdvisorMood.Angry);
            AdvisorBriefing source = Find(report, AdvisorId.Finance);
            source.Name = "Original name"; source.Department = "Original department"; source.Message = "Original message";
            source.MetricText = "Original metric"; source.Detail = "Original detail"; source.ConcernDays = 4;
            queue.Observe(report);
            source.Advisor = AdvisorId.Transport; source.Name = source.Department = source.Title = source.Message = source.MetricText = source.Detail = "mutated";
            source.Mood = AdvisorMood.Calm; source.Severity = IssueSeverity.None; source.Action = AdvisorAction.Garbage; source.ConcernDays = -1;
            source.IsSuspended = true;
            AdvisorBriefing peeked;
            Equal(true, queue.TryPeek(out peeked), "peek available"); Equal(1, queue.PendingCount, "peek preserves pending");
            peeked.Title = "mutated peek"; peeked.Advisor = AdvisorId.Transport; peeked.Message = "mutated peek content";
            AdvisorBriefing delivered = Take(queue);
            Equal(AdvisorId.Finance, delivered.Advisor, "input advisor isolated"); Equal("Original name", delivered.Name, "name copied");
            Equal("Original department", delivered.Department, "department copied"); Equal("Issue Finance", delivered.Title, "title copied");
            Equal("Original message", delivered.Message, "message copied"); Equal("Original metric", delivered.MetricText, "metric copied");
            Equal("Original detail", delivered.Detail, "detail copied"); Equal(4, delivered.ConcernDays, "days copied");
            Equal(AdvisorMood.Angry, delivered.Mood, "mood copied"); Equal(IssueSeverity.Critical, delivered.Severity, "severity copied");
            Equal(AdvisorAction.Budget, delivered.Action, "action copied");
            Equal(false, delivered.IsSuspended, "suspension metadata copied on input");
            delivered.Title = "caller altered"; delivered.Action = AdvisorAction.Water; delivered.Severity = IssueSeverity.None;
            delivered.IsSuspended = true;
            CabinetReport same = Report(2); Issue(same, AdvisorId.Finance, IssueSeverity.Critical, AdvisorMood.Angry);
            queue.Observe(same); Empty(queue, "output mutation cannot alter episode history");
        }
        private static void RealTrafficSuspension()
        {
            AdvisorEngine engine = new AdvisorEngine(); AdvisorAlertQueue queue = new AdvisorAlertQueue();
            CitySnapshot city = RealCity(1); city.TrafficFlowPercent = 50;
            CabinetReport report = engine.Evaluate(city); queue.Observe(report);
            Equal(AdvisorId.Transport, Take(queue).Advisor, "real traffic episode delivered");
            Equal(1, report.News.Length, "real traffic starts one news episode");
            city = RealCity(2); city.TrafficFlowPercent = -1;
            report = engine.Evaluate(city); queue.Observe(report);
            Equal(true, Find(report, AdvisorId.Transport).IsSuspended, "engine marks unavailable ongoing traffic explicitly");
            Equal(1, report.News.Length, "suspension creates no recovery news"); Empty(queue, "consumed traffic stays consumed while suspended");
            city = RealCity(3); city.TrafficFlowPercent = 50;
            report = engine.Evaluate(city); queue.Observe(report);
            Equal(false, Find(report, AdvisorId.Transport).IsSuspended, "available traffic resumes");
            Equal(2, Find(report, AdvisorId.Transport).ConcernDays, "real episode retains its original date");
            Equal(1, report.News.Length, "same real episode has one news item"); Empty(queue, "50 to unavailable to50 cannot replay consumed alert");
            city = RealCity(8); city.TrafficFlowPercent = 50;
            report = engine.Evaluate(city); queue.Observe(report);
            Equal(AdvisorMood.Angry, Take(queue).Mood, "real later mood worsening can notify");
            city = RealCity(9); city.TrafficFlowPercent = 80;
            report = engine.Evaluate(city); queue.Observe(report);
            Equal(false, Find(report, AdvisorId.Transport).IsSuspended, "measured recovery is not suspended");
            Equal(2, report.News.Length, "measured recovery closes episode"); Empty(queue, "recovery has no automatic urgency");
            city = RealCity(10); city.TrafficFlowPercent = 50;
            report = engine.Evaluate(city); queue.Observe(report);
            Equal(0, Take(queue).ConcernDays, "a real new episode may notify after measured recovery");
            Equal(3, report.News.Length, "new real episode starts distinct news");
        }
        private static void RealPendingSuspension()
        {
            AdvisorEngine engine = new AdvisorEngine(); AdvisorAlertQueue queue = new AdvisorAlertQueue();
            CitySnapshot city = RealCity(1); city.TrafficFlowPercent = 50;
            queue.Observe(engine.Evaluate(city)); // Retained behind a modal.
            city = RealCity(2); city.TrafficFlowPercent = -1; city.Cash = -1;
            CabinetReport report = engine.Evaluate(city); queue.Observe(report);
            Equal(2, queue.PendingCount, "suspended traffic is retained beside an available finance alert");
            Equal(AdvisorId.Finance, Take(queue).Advisor, "suspended advisor cannot block available advisor");
            Equal(1, queue.PendingCount, "taking finance preserves suspended traffic");
            AdvisorBriefing peek;
            Equal(false, queue.TryPeek(out peek), "suspended pending cannot be peeked"); Unavailable(queue, "suspended pending cannot be taken");
            Find(report, AdvisorId.Transport).IsSuspended = false; // Caller mutation cannot release the stored suspension.
            Equal(false, queue.TryPeek(out peek), "suspension input is isolated from caller mutation");
            city = RealCity(3); city.TrafficFlowPercent = 55;
            report = engine.Evaluate(city); queue.Observe(report);
            Equal(true, queue.TryPeek(out peek), "available reading resumes pending");
            Equal(2, peek.ConcernDays, "resumed pending uses current concern duration");
            Equal(Find(report, AdvisorId.Transport).MetricText, peek.MetricText, "resumed pending uses current measurement");
            Equal(false, peek.IsSuspended, "resumed copy is available");
            peek.IsSuspended = true;
            Equal(false, Take(queue).IsSuspended, "peek mutation does not suspend stored pending");

            engine = new AdvisorEngine(); queue.Reset();
            city = RealCity(1); city.TrafficFlowPercent = 50; queue.Observe(engine.Evaluate(city));
            city = RealCity(2); city.TrafficFlowPercent = -1; queue.Observe(engine.Evaluate(city));
            city = RealCity(3); city.TrafficFlowPercent = 80; queue.Observe(engine.Evaluate(city));
            Empty(queue, "measured recovery removes a pending suspended episode");
            city = RealCity(4); city.TrafficFlowPercent = 50; queue.Observe(engine.Evaluate(city)); Take(queue);

            // Reset clears the per-advisor suspension as well as sequence/history.
            city = RealCity(5); city.TrafficFlowPercent = -1; queue.Observe(engine.Evaluate(city));
            queue.Reset(); engine = new AdvisorEngine();
            city = RealCity(1); city.TrafficFlowPercent = 50; queue.Observe(engine.Evaluate(city)); Take(queue);
        }
        private static void RealUrbanSuspension()
        {
            for (int issue = 0; issue < 2; issue++)
            {
                AdvisorEngine engine = new AdvisorEngine(); AdvisorAlertQueue queue = new AdvisorAlertQueue();
                CitySnapshot city = RealCity(1);
                if (issue == 0) city.UnemploymentPercent = 25; else city.ResidentialAbandoned = 15;
                CabinetReport report = engine.Evaluate(city); queue.Observe(report);
                Equal(AdvisorId.UrbanPlanning, Take(queue).Advisor, "real urban issue delivered");
                city = RealCity(2);
                if (issue == 0) { city.Population = 49; city.UnemploymentPercent = 25; }
                else { city.ZonedBuildings = 0; city.ResidentialAbandoned = 15; }
                report = engine.Evaluate(city); queue.Observe(report);
                Equal(true, Find(report, AdvisorId.UrbanPlanning).IsSuspended, "population/denominator gates suspend existing urban issue");
                Equal(1, report.News.Length, "urban unavailable keeps news episode");
                city = RealCity(3);
                if (issue == 0) city.UnemploymentPercent = 25; else city.ResidentialAbandoned = 15;
                report = engine.Evaluate(city); queue.Observe(report);
                Equal(2, Find(report, AdvisorId.UrbanPlanning).ConcernDays, "urban episode dates survive unavailable interval");
                Equal(1, report.News.Length, "urban resumption is not a new news episode"); Empty(queue, "same real urban episode cannot replay");
                city = RealCity(4); report = engine.Evaluate(city); queue.Observe(report);
                Equal(false, Find(report, AdvisorId.UrbanPlanning).IsSuspended, "measured urban recovery available");
                Empty(queue, "urban recovery clears episode");
                city = RealCity(5);
                if (issue == 0) city.UnemploymentPercent = 25; else city.ResidentialAbandoned = 15;
                queue.Observe(engine.Evaluate(city)); Take(queue);
            }
        }
        private static CitySnapshot RealCity(int day)
        {
            return new CitySnapshot { DataValid = true, CityName = "Real engine fixture", SimulationDate = new DateTime(2026, 6, day),
                Population = 100, Happiness = 75, Health = 75, Cash = 10000, WeeklyBalance = 500,
                TrafficFlowPercent = 80, ZonedBuildings = 100 };
        }
        private static void MalformedBatchesAreTransactional()
        {
            Action<CabinetReport>[] mutations = new Action<CabinetReport>[] {
                r => r.Snapshot = null, r => r.Snapshot.DataValid = false, r => r.Briefings = null,
                r => r.Snapshot.Population = -1, r => r.Briefings[6].ConcernDays = -1,
                r => r.Briefings = new AdvisorBriefing[6], r => r.Briefings = new AdvisorBriefing[8],
                r => r.Briefings[6] = null, r => r.Briefings[6].Advisor = AdvisorId.Finance,
                r => r.Briefings[6].Advisor = (AdvisorId)(-1), r => r.Briefings[6].Advisor = (AdvisorId)7,
                r => r.Briefings[6].Mood = (AdvisorMood)(-1), r => r.Briefings[6].Mood = (AdvisorMood)4,
                r => r.Briefings[6].Severity = (IssueSeverity)(-1), r => r.Briefings[6].Severity = (IssueSeverity)4,
                r => r.Briefings[6].Action = (AdvisorAction)(-1), r => r.Briefings[6].Action = (AdvisorAction)11
            };
            foreach (Action<CabinetReport> mutate in mutations)
            {
                AdvisorAlertQueue queue = new AdvisorAlertQueue(); queue.Observe(Report(1));
                CabinetReport active = Report(2); Issue(active, AdvisorId.Finance, IssueSeverity.Warning, AdvisorMood.Concerned);
                queue.Observe(active); Take(queue);
                CabinetReport invalid = Report(3); Issue(invalid, AdvisorId.Finance, IssueSeverity.Critical, AdvisorMood.Angry); mutate(invalid);
                Equal(false, queue.Observe(invalid), "malformed batch rejected"); Equal(0, queue.PendingCount, "no partial enqueue"); Unavailable(queue, "malformed batch suspends");
                active.Sequence = 4; queue.Observe(active); Empty(queue, "invalid early entries did not alter history");
                active.Sequence = 5; Find(active, AdvisorId.Finance).Severity = IssueSeverity.Critical;
                queue.Observe(active); Take(queue); // A real later worsening remains observable.
            }
        }
        private static void PeekAndTextValidation()
        {
            Action<AdvisorBriefing>[] badTexts = new Action<AdvisorBriefing>[] {
                b => b.Name = null, b => b.Name = "", b => b.Name = " \t\r\n",
                b => b.Department = null, b => b.Department = "", b => b.Department = "\u2003",
                b => b.Title = null, b => b.Title = "", b => b.Title = " \t",
                b => b.Message = null, b => b.Message = "", b => b.Message = "\r\n"
            };
            foreach (Action<AdvisorBriefing> badText in badTexts)
            {
                AdvisorAlertQueue queue = new AdvisorAlertQueue(); CabinetReport invalid = Report(1);
                Issue(invalid, AdvisorId.Finance, IssueSeverity.Critical, AdvisorMood.Angry);
                badText(invalid.Briefings[6]); Equal(false, queue.Observe(invalid), "required text invalidates whole batch");
                CabinetReport valid = Report(2);
                Issue(valid, AdvisorId.Finance, IssueSeverity.Critical, AdvisorMood.Angry);
                Issue(valid, AdvisorId.Utilities, IssueSeverity.Warning, AdvisorMood.Concerned);
                valid.Briefings[0].MetricText = null; valid.Briefings[0].Detail = null;
                Equal(true, queue.Observe(valid), "optional text may be null"); Equal(1, queue.PendingCount, "invalid batch did not consume first populated throttle");
                AdvisorBriefing first, second;
                Equal(true, queue.TryPeek(out first), "first peek"); Equal(true, queue.TryPeek(out second), "second peek");
                Equal(false, ReferenceEquals(first, second), "peek returns fresh clones");
                Equal(null, first.MetricText, "optional metric copied"); Equal(null, first.Detail, "optional detail copied");
                queue.Acknowledge(first.Advisor); Empty(queue, "successful show acknowledgment consumes only pending");
            }
            AdvisorAlertQueue retained = new AdvisorAlertQueue(); CabinetReport active = Report(1);
            Issue(active, AdvisorId.Finance, IssueSeverity.Warning, AdvisorMood.Concerned); retained.Observe(active);
            AdvisorBriefing clone; retained.TryPeek(out clone); // Simulates Show refusing: no acknowledge.
            Equal(1, retained.PendingCount, "failed show retains candidate");
            CabinetReport recovered = Report(2); retained.Observe(recovered);
            Equal(false, retained.TryPeek(out clone), "recovery prevents stale peek after failed show"); Equal(true, clone == null, "empty peek clears out parameter");
            active.Sequence = 3; retained.Observe(active); retained.Observe(null);
            Equal(false, retained.TryPeek(out clone), "invalid suspends peek as well as take");
        }
        private static CabinetReport Report(long sequence)
        {
            AdvisorBriefing[] briefings = new AdvisorBriefing[7];
            for (int i = 0; i < 7; i++) briefings[i] = new AdvisorBriefing { Advisor = (AdvisorId)i, Name = "Advisor " + i,
                Department = "Department " + i, Title = "Quiet " + i, Message = "Quiet", MetricText = "Metric", Detail = "Detail",
                Mood = AdvisorMood.Calm, Severity = IssueSeverity.None, Action = AdvisorAction.Overview };
            return new CabinetReport { Sequence = sequence, Snapshot = new CitySnapshot { DataValid = true, Population = 100 }, Briefings = briefings };
        }
        private static void Issue(CabinetReport report, AdvisorId advisor, IssueSeverity severity, AdvisorMood mood)
        {
            AdvisorBriefing briefing = Find(report, advisor); briefing.Title = "Issue " + advisor; briefing.Message = "Issue message";
            briefing.Severity = severity; briefing.Mood = mood; briefing.Action = AdvisorAction.Budget;
        }
        private static AdvisorBriefing Find(CabinetReport report, AdvisorId advisor)
        { foreach (AdvisorBriefing briefing in report.Briefings) if (briefing.Advisor == advisor) return briefing; throw new Exception("Missing fixture advisor"); }
        private static AdvisorBriefing Take(AdvisorAlertQueue queue)
        { AdvisorBriefing briefing; Equal(true, queue.TryTake(out briefing), "expected queued alert"); Equal(false, briefing == null, "delivery nonnull"); return briefing; }
        private static void Empty(AdvisorAlertQueue queue, string name)
        { Equal(0, queue.PendingCount, name + " count"); Unavailable(queue, name); }
        private static void Unavailable(AdvisorAlertQueue queue, string name)
        { AdvisorBriefing briefing; Equal(false, queue.TryTake(out briefing), name); Equal(true, briefing == null, name + " null output"); }
        private static void Equal<T>(T expected, T actual, string name)
        { _checks++; if (!object.Equals(expected, actual)) throw new Exception(name + ": expected " + expected + ", got " + actual); }
    }
}
