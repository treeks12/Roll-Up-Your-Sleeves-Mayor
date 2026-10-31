using System;
using System.IO;
using SC4Evolution.Core;

namespace SC4Evolution.Core.Tests
{
    internal static class Program
    {
        private static int _assertions;

        private static int Main(string[] args)
        {
            try
            {
                TestInvalidAndEmptyData();
                TestVerifiedPortugueseRosterNames();
                TestThresholdsAndSafeFinanceMath();
                TestLockedServicesDoNotAlert();
                TestSameDayAndSevenDayEscalation();
                TestRecoveryAndReturningIssue();
                TestZeroPopulationPreservesIssueHistory();
                TestPrioritySwitchPreservesHiddenIssueHistory();
                TestSuspendedFinanceIssueHistory();
                TestFinanceDeficitEpisodeTransitions();
                TestUnavailableUrbanIssueHistory();
                TestUnavailableTrafficAndZeroDenominators();
                TestSerializationRoundTripAndReload();
                TestInvalidSerializationDoesNotReplaceState();
                TestNewsHistoryBound();
                Console.WriteLine("PASS: " + _assertions + " assertions across 15 advisor-engine scenarios.");
                Console.WriteLine("PASS: " + AdvisorAlertQueueTests.Run() + " assertions across 13 advisor-alert queue scenarios.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: " + exception);
                return 1;
            }
        }

        private static void TestInvalidAndEmptyData()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot invalid = Good(Date(2026, 1, 1));
            invalid.DataValid = false;
            invalid.Cash = -50L;
            invalid.WeeklyBalance = -10L;
            invalid.CrimePercent = 99;
            invalid.PollutionPercent = 99;
            invalid.ElectricityUse = 100;
            invalid.ElectricityCapacity = 0;

            CabinetReport report = engine.Evaluate(invalid);
            Equal(7, report.Briefings.Length, "invalid sample still returns one briefing per advisor");
            Equal(0, report.News.Length, "invalid sample creates no news");
            AssertAllNoIssue(report, "invalid sample");

            report = engine.Evaluate(null);
            Equal(7, report.Briefings.Length, "null sample is handled without throwing");
            AssertAllNoIssue(report, "null sample");

            CitySnapshot empty = Good(Date(2026, 1, 2));
            empty.Population = 0;
            empty.Cash = -500L;
            empty.WeeklyBalance = -100L;
            empty.CrimePercent = 80;
            empty.PollutionPercent = 90;
            empty.ElectricityUse = 10;
            empty.ElectricityCapacity = 0;
            empty.SickCitizens = 50;
            empty.HealthCapacity = 0;
            report = engine.Evaluate(empty);
            AssertAllNoIssue(report, "empty valid city");
            Contains(report.Briefings[(int)AdvisorId.Finance].Message, "Antes da expansão", "empty city gets founding guidance");
            Equal(0, report.News.Length, "empty city creates no crisis news");

            CitySnapshot active = Good(Date(2026, 1, 3));
            active.CrimePercent = 10;
            engine.Evaluate(active);
            CitySnapshot invalidWhileActive = Good(Date(2026, 1, 8));
            invalidWhileActive.DataValid = false;
            engine.Evaluate(invalidWhileActive);
            CitySnapshot stillActive = Good(Date(2026, 1, 8));
            stillActive.CrimePercent = 10;
            report = engine.Evaluate(stillActive);
            Equal(5, report.Briefings[(int)AdvisorId.PublicSafety].ConcernDays, "invalid samples do not clear or reset an ongoing issue");
            Equal(1, report.News.Length, "invalid samples do not spam or resolve news");
        }

        private static void TestVerifiedPortugueseRosterNames()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CabinetReport report = engine.Evaluate(Good(Date(2026, 1, 1)));
            Equal("Betty D. Fato", report.Briefings[(int)AdvisorId.Finance].Name, "finance advisor uses the verified Portuguese roster name");
            Equal("Ney Einstein", report.Briefings[(int)AdvisorId.UrbanPlanning].Name, "planning advisor uses the verified Portuguese roster name");
            Equal("Dinalva Gaze", report.Briefings[(int)AdvisorId.EducationHealth].Name, "health and education advisor uses the verified Portuguese roster name");
            Equal("Jonas Pronto", report.Briefings[(int)AdvisorId.Utilities].Name, "utilities advisor uses the verified Portuguese roster name");
            Equal("Samuel Fogaça", report.Briefings[(int)AdvisorId.PublicSafety].Name, "safety advisor uses the verified Portuguese roster name");
            Equal("Camila Camomila", report.Briefings[(int)AdvisorId.Environment].Name, "environment advisor uses the verified Portuguese roster name");
            Equal("Roberto Carros", report.Briefings[(int)AdvisorId.Transport].Name, "transport advisor uses the verified Portuguese roster name");
        }

        private static void TestThresholdsAndSafeFinanceMath()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 2, 1));

            city.Cash = 3L;
            city.WeeklyBalance = -1L;
            Equal(IssueSeverity.Critical, Finance(engine.Evaluate(city)).Severity, "cash covers less than four weeks of a deficit");
            city = Good(Date(2026, 2, 1));
            city.Cash = 4L;
            city.WeeklyBalance = -1L;
            Equal(IssueSeverity.Warning, Finance(engine.Evaluate(city)).Severity, "cash covering exactly four weeks is a warning");
            city = Good(Date(2026, 2, 1));
            city.Cash = long.MaxValue;
            city.WeeklyBalance = long.MinValue;
            Equal(IssueSeverity.Critical, Finance(engine.Evaluate(city)).Severity, "long.MinValue deficit calculation does not overflow");
            city = Good(Date(2026, 2, 1));
            city.Cash = long.MaxValue;
            city.WeeklyBalance = -1L;
            Equal(IssueSeverity.Warning, Finance(engine.Evaluate(city)).Severity, "large positive cash does not overflow the four-week comparison");
            city = Good(Date(2026, 2, 1));
            city.Cash = long.MinValue;
            city.WeeklyBalance = 0L;
            Equal(IssueSeverity.Critical, Finance(engine.Evaluate(city)).Severity, "negative cash is critical at the signed minimum");

            city = Good(Date(2026, 2, 1));
            city.Population = 50;
            city.UnemploymentPercent = 12;
            Equal(IssueSeverity.Warning, Urban(engine.Evaluate(city)).Severity, "unemployment warning threshold is inclusive");
            city.UnemploymentPercent = 25;
            Equal(IssueSeverity.Critical, Urban(engine.Evaluate(city)).Severity, "unemployment critical threshold is inclusive");
            city = Good(Date(2026, 2, 1));
            city.ZonedBuildings = 100;
            city.ResidentialAbandoned = 5;
            Equal(IssueSeverity.Warning, Urban(engine.Evaluate(city)).Severity, "five percent abandonment is a warning");
            city.ResidentialAbandoned = 15;
            Equal(IssueSeverity.Critical, Urban(engine.Evaluate(city)).Severity, "fifteen percent abandonment is critical");

            city = Good(Date(2026, 2, 1));
            city.ElementaryNeed = 100;
            city.ElementaryCapacity = 75;
            Equal(IssueSeverity.Warning, EducationHealth(engine.Evaluate(city)).Severity, "school capacity exactly 75 percent remains warning");
            city.ElementaryCapacity = 74;
            Equal(IssueSeverity.Critical, EducationHealth(engine.Evaluate(city)).Severity, "school capacity below 75 percent is critical");
            city = Good(Date(2026, 2, 1));
            city.SickCitizens = 100;
            city.HealthCapacity = 67;
            Equal(IssueSeverity.Warning, EducationHealth(engine.Evaluate(city)).Severity, "health capacity exactly 67 percent remains warning");
            city.HealthCapacity = 66;
            Equal(IssueSeverity.Critical, EducationHealth(engine.Evaluate(city)).Severity, "health capacity below 67 percent is critical");

            city = Good(Date(2026, 2, 1));
            city.ElectricityUse = 100;
            city.ElectricityCapacity = 80;
            Equal(IssueSeverity.Warning, Utilities(engine.Evaluate(city)).Severity, "utility capacity exactly 80 percent remains warning");
            city.ElectricityCapacity = 79;
            Equal(IssueSeverity.Critical, Utilities(engine.Evaluate(city)).Severity, "utility capacity below 80 percent is critical");

            city = Good(Date(2026, 2, 1));
            city.CrimePercent = 10;
            Equal(IssueSeverity.Warning, Safety(engine.Evaluate(city)).Severity, "crime warning threshold is inclusive");
            city.CrimePercent = 25;
            Equal(IssueSeverity.Critical, Safety(engine.Evaluate(city)).Severity, "crime critical threshold is inclusive");
            city = Good(Date(2026, 2, 1));
            city.PollutionPercent = 20;
            Equal(IssueSeverity.Warning, Environment(engine.Evaluate(city)).Severity, "pollution warning threshold is inclusive");
            city.PollutionPercent = 40;
            Equal(IssueSeverity.Critical, Environment(engine.Evaluate(city)).Severity, "pollution critical threshold is inclusive");
            city = Good(Date(2026, 2, 1));
            city.GarbageCapacity = 100;
            city.GarbageStored = 85;
            Equal(IssueSeverity.Warning, Environment(engine.Evaluate(city)).Severity, "garbage warning threshold is inclusive");
            city.GarbageStored = 100;
            Equal(IssueSeverity.Critical, Environment(engine.Evaluate(city)).Severity, "garbage full capacity is critical");

            city = Good(Date(2026, 2, 1));
            city.TrafficFlowPercent = 35;
            Equal(IssueSeverity.Warning, Transport(engine.Evaluate(city)).Severity, "35 percent traffic flow remains warning");
            city.TrafficFlowPercent = 34;
            Equal(IssueSeverity.Critical, Transport(engine.Evaluate(city)).Severity, "traffic flow below 35 percent is critical");
            city.TrafficFlowPercent = 60;
            Equal(IssueSeverity.None, Transport(engine.Evaluate(city)).Severity, "60 percent traffic flow has no alert");
        }

        private static void TestLockedServicesDoNotAlert()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 3, 1));
            city.ElementaryNeed = 1000;
            city.ElementaryCapacity = 0;
            city.HighSchoolNeed = 500;
            city.HighSchoolCapacity = 0;
            city.UniversityNeed = 200;
            city.UniversityCapacity = 0;
            city.SickCitizens = 200;
            city.HealthCapacity = 0;
            city.ElementaryEducationAvailable = false;
            city.HighSchoolEducationAvailable = false;
            city.UniversityEducationAvailable = false;
            city.HealthServiceAvailable = false;

            CabinetReport report = engine.Evaluate(city);
            Equal(IssueSeverity.None, EducationHealth(report).Severity, "locked education and health services do not create an alert");
            Equal(AdvisorMood.Calm, EducationHealth(report).Mood, "locked education and health services keep the advisor calm");
            Contains(EducationHealth(report).Title, "não liberados", "locked services explain why the advisor is quiet");
            Equal(0, report.News.Length, "locked services do not create crisis news");

            city.ElementaryEducationAvailable = true;
            report = engine.Evaluate(city);
            Equal(IssueSeverity.Critical, EducationHealth(report).Severity, "an unlocked service reports its actual shortage");
            Equal(1, report.News.Length, "the first actionable shortage creates one news item");

            city.ElementaryEducationAvailable = false;
            report = engine.Evaluate(city);
            Equal(IssueSeverity.None, EducationHealth(report).Severity, "a locked service suspends its old episode");
            True(EducationHealth(report).IsSuspended, "a locked service marks the briefing as suspended");
            Equal(1, report.News.Length, "locking a service does not add a recovery spam item");
        }

        private static void TestSameDayAndSevenDayEscalation()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 3, 1));
            city.CrimePercent = 10;
            CabinetReport report = engine.Evaluate(city);
            AdvisorBriefing briefing = Safety(report);
            Equal(AdvisorMood.Concerned, briefing.Mood, "new warning starts concerned");
            Equal(0, briefing.ConcernDays, "new issue starts at day zero");
            Equal(1, report.News.Length, "problem start creates one news item");

            city = Good(Date(2026, 3, 1));
            city.CrimePercent = 10;
            report = engine.Evaluate(city);
            Equal(0, Safety(report).ConcernDays, "repeated same-day poll does not advance duration");
            Equal(1, report.News.Length, "repeated same-day poll creates no duplicate news");

            city = Good(Date(2026, 3, 7));
            city.CrimePercent = 25;
            report = engine.Evaluate(city);
            Equal(6, Safety(report).ConcernDays, "severity change keeps the original issue start date");
            Equal(AdvisorMood.Concerned, Safety(report).Mood, "issue is not angry before seven calendar days");
            Equal(2, report.News.Length, "severity change creates one news item");

            city = Good(Date(2026, 3, 8));
            city.CrimePercent = 25;
            report = engine.Evaluate(city);
            Equal(7, Safety(report).ConcernDays, "concern duration uses simulation calendar days");
            Equal(AdvisorMood.Angry, Safety(report).Mood, "same issue becomes angry after seven days");
            Equal(2, report.News.Length, "daily persistence creates no repeated news");
        }

        private static void TestRecoveryAndReturningIssue()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 4, 1));
            city.CrimePercent = 10;
            engine.Evaluate(city);

            city = Good(Date(2026, 4, 2));
            city.CrimePercent = 9;
            CabinetReport report = engine.Evaluate(city);
            Equal(AdvisorMood.Pleased, Safety(report).Mood, "resolution is acknowledged once");
            Equal(2, report.News.Length, "resolution creates one recovery item");
            Equal(IssueSeverity.None, report.News[1].Severity, "recovery news has no active severity");

            report = engine.Evaluate(city);
            Equal(AdvisorMood.Calm, Safety(report).Mood, "repeated clear sample returns to calm");
            Equal(2, report.News.Length, "repeated clear sample does not repeat recovery news");

            city = Good(Date(2026, 4, 3));
            city.CrimePercent = 10;
            report = engine.Evaluate(city);
            Equal(AdvisorMood.Concerned, Safety(report).Mood, "returning issue starts a new episode");
            Equal(0, Safety(report).ConcernDays, "returning issue resets concern duration");
            Equal(3, report.News.Length, "returning issue creates a new start item");
        }

        private static void TestZeroPopulationPreservesIssueHistory()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 4, 10));
            city.Cash = -100L;
            CabinetReport report = engine.Evaluate(city);
            Equal(IssueSeverity.Critical, Finance(report).Severity, "negative cash starts an issue before the city empties");
            Equal(1, report.News.Length, "initial financial issue creates one news item");

            city = Good(Date(2026, 4, 11));
            city.Population = 0;
            city.Cash = -100L;
            report = engine.Evaluate(city);
            Equal(IssueSeverity.None, Finance(report).Severity, "empty city receives suspended founding guidance");
            Contains(Finance(report).Message, "Antes da expansão", "empty-city message is useful founding guidance");
            Equal(1, report.News.Length, "empty population does not falsely resolve a still-negative cash issue");

            city = Good(Date(2026, 4, 14));
            city.Cash = -100L;
            report = engine.Evaluate(city);
            Equal(4, Finance(report).ConcernDays, "returning population resumes the preserved issue episode");
            Equal(1, report.News.Length, "resumed unresolved issue does not start a duplicate episode");

            city = Good(Date(2026, 4, 15));
            city.Cash = 0L;
            report = engine.Evaluate(city);
            Equal(AdvisorMood.Pleased, Finance(report).Mood, "measured recovery is acknowledged after data resumes");
            Equal(2, report.News.Length, "measured recovery creates exactly one recovery item");

            engine = new AdvisorEngine();
            city = Good(Date(2026, 4, 20));
            city.Cash = -100L;
            engine.Evaluate(city);
            city = Good(Date(2026, 4, 21));
            city.Population = 0;
            city.Cash = 0L;
            city.WeeklyBalance = 0L;
            report = engine.Evaluate(city);
            Equal(AdvisorMood.Calm, Finance(report).Mood, "empty-city guidance remains calm when a prior metric recovers");
            Equal(2, report.News.Length, "empty city records recovery only when its cash measurement actually clears");
            city = Good(Date(2026, 4, 22));
            city.Cash = 0L;
            city.WeeklyBalance = 0L;
            report = engine.Evaluate(city);
            Equal(2, report.News.Length, "measured empty-city recovery does not repeat after population returns");
        }

        private static void TestPrioritySwitchPreservesHiddenIssueHistory()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 5, 1));
            city.ElectricityUse = 100;
            city.ElectricityCapacity = 90;
            CabinetReport report = engine.Evaluate(city);
            Equal(AdvisorAction.Electricity, Utilities(report).Action, "electricity warning is initially the utility priority");
            Equal(1, report.News.Length, "initial electricity issue creates one news item");

            city = Good(Date(2026, 5, 8));
            city.ElectricityUse = 100;
            city.ElectricityCapacity = 90;
            city.WaterUse = 100;
            city.WaterCapacity = 70;
            report = engine.Evaluate(city);
            Equal(AdvisorAction.Water, Utilities(report).Action, "critical water issue becomes the displayed priority");
            Equal(0, Utilities(report).ConcernDays, "new higher-priority issue starts its own episode");
            Equal(2, report.News.Length, "new water issue adds one news item without resolving electricity");

            byte[] saved = engine.SaveState();
            Equal((byte)2, saved[4], "per-issue state uses serialization version 2");
            AdvisorEngine reloaded = new AdvisorEngine();
            reloaded.LoadState(saved);
            BytesEqual(saved, reloaded.SaveState(), "priority-switched issue history survives a roundtrip");

            city = Good(Date(2026, 5, 9));
            city.ElectricityUse = 100;
            city.ElectricityCapacity = 90;
            city.WaterUse = 0;
            city.WaterCapacity = 70;
            report = reloaded.Evaluate(city);
            Equal(AdvisorAction.Electricity, Utilities(report).Action, "electricity returns to the displayed priority after water clears");
            Equal(8, Utilities(report).ConcernDays, "lower-priority electricity issue keeps its original start date across reload");
            Equal(AdvisorMood.Angry, Utilities(report).Mood, "hidden persistent electricity issue escalates by its own duration");
            Equal(3, report.News.Length, "water recovery adds one item without restarting electricity");
            Contains(report.News[2].Title, "Recuperação", "priority switch creates a recovery item only for the cleared water issue");

            city = Good(Date(2026, 5, 10));
            city.ElectricityUse = 100;
            city.ElectricityCapacity = 90;
            city.WaterUse = 0;
            city.WaterCapacity = 70;
            report = reloaded.Evaluate(city);
            Equal(3, report.News.Length, "priority changes and repeated polls do not spam recovery or start news");
            Equal(9, Utilities(report).ConcernDays, "ongoing lower-priority issue continues escalating");
        }

        private static void TestSuspendedFinanceIssueHistory()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 5, 1));
            city.Cash = 100L;
            city.WeeklyBalance = -1L;
            CabinetReport report = engine.Evaluate(city);
            Equal(IssueSeverity.Warning, Finance(report).Severity, "negative reported balance starts a finance issue when cash is nonnegative");
            Equal(1, report.News.Length, "initial deficit creates one news item");

            city = Good(Date(2026, 5, 2));
            city.Cash = -1L;
            city.WeeklyBalance = -1L;
            report = engine.Evaluate(city);
            Equal(IssueSeverity.Critical, Finance(report).Severity, "negative cash becomes the finance priority");
            Equal(2, report.News.Length, "cash priority starts without falsely resolving the ongoing deficit");

            city = Good(Date(2026, 5, 8));
            city.Cash = 100L;
            city.WeeklyBalance = -1L;
            report = engine.Evaluate(city);
            Equal(AdvisorMood.Angry, Finance(report).Mood, "deficit resumes with its original date after cash recovers");
            Equal(7, Finance(report).ConcernDays, "suspended deficit history survives priority changes");
            Equal(3, report.News.Length, "cash recovery is reported without restarting the still-active deficit");
        }

        private static void TestFinanceDeficitEpisodeTransitions()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 5, 1));
            city.Cash = 100L;
            city.WeeklyBalance = -1L;
            CabinetReport report = engine.Evaluate(city);
            Equal(IssueSeverity.Warning, Finance(report).Severity, "measured negative balance starts a tracked deficit episode");
            Equal(1, report.News.Length, "initial deficit episode adds one news item");

            city = Good(Date(2026, 5, 2));
            city.Cash = -1L;
            city.WeeklyBalance = 1L;
            report = engine.Evaluate(city);
            Equal(IssueSeverity.Critical, Finance(report).Severity, "negative cash remains the displayed finance priority");
            Equal(3, report.News.Length, "cash alert and measured deficit recovery are both recorded under cash priority");
            Contains(report.News[2].Title, "Recuperação", "measured positive balance closes the hidden deficit episode");

            city = Good(Date(2026, 5, 3));
            city.Cash = -1L;
            city.WeeklyBalance = -1L;
            report = engine.Evaluate(city);
            Equal(4, report.News.Length, "a newly negative balance after measured recovery starts a new episode");

            byte[] saved = engine.SaveState();
            AdvisorEngine reloaded = new AdvisorEngine();
            reloaded.LoadState(saved);
            BytesEqual(saved, reloaded.SaveState(), "restarted deficit episode survives serialization");

            city = Good(Date(2026, 5, 8));
            city.Cash = 100L;
            city.WeeklyBalance = -1L;
            report = reloaded.Evaluate(city);
            Equal(IssueSeverity.Warning, Finance(report).Severity, "deficit becomes the displayed finance issue after cash recovers");
            Equal(5, Finance(report).ConcernDays, "new deficit episode duration starts at the measured restart date");
            Equal(AdvisorMood.Concerned, Finance(report).Mood, "new deficit does not inherit anger from the earlier episode");
            Equal(5, report.News.Length, "cash recovery is recorded without restarting the current deficit");

            city = Good(Date(2026, 5, 8));
            city.Cash = 100L;
            city.WeeklyBalance = -1L;
            report = reloaded.Evaluate(city);
            Equal(5, report.News.Length, "same-day deficit poll does not spam start or recovery news");
            Equal(5, Finance(report).ConcernDays, "same-day deficit poll does not advance episode duration");
        }

        private static void TestUnavailableUrbanIssueHistory()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 5, 1));
            city.Population = 50;
            city.UnemploymentPercent = 25;
            CabinetReport report = engine.Evaluate(city);
            Equal(IssueSeverity.Critical, Urban(report).Severity, "unemployment issue starts at its minimum population");

            city = Good(Date(2026, 5, 2));
            city.Population = 49;
            city.UnemploymentPercent = 25;
            report = engine.Evaluate(city);
            Equal(IssueSeverity.None, Urban(report).Severity, "unemployment alert is suspended below its population minimum");
            Contains(Urban(report).Message, "abaixo do mínimo", "suspended unemployment gets an accurate unavailable message");
            Equal(1, report.News.Length, "population gating does not falsely resolve unemployment");

            city = Good(Date(2026, 5, 3));
            city.Population = 50;
            city.UnemploymentPercent = 25;
            report = engine.Evaluate(city);
            Equal(2, Urban(report).ConcernDays, "unemployment resumes with its saved issue start date");
            Equal(1, report.News.Length, "resumed unemployment is not a new episode");

            engine = new AdvisorEngine();
            city = Good(Date(2026, 5, 1));
            city.ZonedBuildings = 100;
            city.ResidentialAbandoned = 15;
            report = engine.Evaluate(city);
            Equal(IssueSeverity.Critical, Urban(report).Severity, "abandonment issue starts with an available denominator");
            city = Good(Date(2026, 5, 2));
            city.ZonedBuildings = 0;
            city.ResidentialAbandoned = 15;
            report = engine.Evaluate(city);
            Equal(1, report.News.Length, "zero denominator preserves abandonment without a recovery item");
            city = Good(Date(2026, 5, 3));
            city.ZonedBuildings = 100;
            city.ResidentialAbandoned = 15;
            report = engine.Evaluate(city);
            Equal(2, Urban(report).ConcernDays, "abandonment resumes with its original issue date");
            Equal(1, report.News.Length, "abandonment does not restart after an unavailable denominator");
        }

        private static void TestUnavailableTrafficAndZeroDenominators()
        {
            CitySnapshot uninitialized = new CitySnapshot { DataValid = true, Population = 100 };
            Equal(-1, uninitialized.TrafficFlowPercent, "an unfilled snapshot defaults traffic to unavailable");
            True(double.IsNaN(uninitialized.NativeTaxMultiplier), "an unfilled snapshot defaults the diagnostic tax multiplier to unavailable");
            CabinetReport uninitializedReport = new AdvisorEngine().Evaluate(uninitialized);
            Equal(IssueSeverity.None, Transport(uninitializedReport).Severity, "default traffic data cannot create a false zero-flow crisis");

            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 5, 1));
            city.TrafficFlowPercent = 59;
            CabinetReport report = engine.Evaluate(city);
            Equal(IssueSeverity.Warning, Transport(report).Severity, "known low traffic flow is reported");

            city = Good(Date(2026, 5, 2));
            city.TrafficFlowPercent = -1;
            report = engine.Evaluate(city);
            Equal(IssueSeverity.None, Transport(report).Severity, "unavailable traffic data does not produce an alarm");
            Equal(AdvisorMood.Calm, Transport(report).Mood, "unavailable traffic is reported neutrally");
            Equal(1, report.News.Length, "unavailable traffic does not falsely resolve or spam the issue");

            city = Good(Date(2026, 5, 8));
            city.TrafficFlowPercent = 59;
            report = engine.Evaluate(city);
            Equal(AdvisorMood.Angry, Transport(report).Mood, "unknown traffic interval does not erase the persistent episode");
            Equal(7, Transport(report).ConcernDays, "traffic concern duration remains tied to its original simulation date");
            Equal(1, report.News.Length, "returning known traffic issue is not treated as a new episode after unavailable data");

            engine = new AdvisorEngine();
            city = Good(Date(2026, 5, 1));
            city.TrafficFlowPercent = -1;
            city.ZonedBuildings = 0;
            city.ResidentialAbandoned = 100;
            city.GarbageStored = 0;
            city.GarbageCapacity = 0;
            city.SickCitizens = 0;
            city.HealthCapacity = 0;
            city.ElementaryNeed = 0;
            city.ElementaryCapacity = 0;
            city.ElectricityUse = 0;
            city.ElectricityCapacity = 0;
            city.WaterUse = 0;
            city.WaterCapacity = 0;
            city.SewageUse = 0;
            city.SewageCapacity = 0;
            report = engine.Evaluate(city);
            Equal(IssueSeverity.None, Urban(report).Severity, "zero zoned-building denominator is unavailable, not total abandonment");
            Equal(IssueSeverity.None, Environment(report).Severity, "zero garbage capacity and zero storage is not a crisis");
            Equal(IssueSeverity.None, EducationHealth(report).Severity, "zero school and health needs do not create capacity alarms");
            Equal(IssueSeverity.None, Utilities(report).Severity, "zero usage and capacity do not create utility alarms");
            Equal(IssueSeverity.None, Transport(report).Severity, "negative traffic flow is unavailable, not zero flow");

            city.GarbageStored = 1;
            report = engine.Evaluate(city);
            Equal(IssueSeverity.Critical, Environment(report).Severity, "positive garbage with zero capacity is critical");
        }

        private static void TestSerializationRoundTripAndReload()
        {
            AdvisorEngine first = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 6, 1));
            city.PollutionPercent = 20;
            CabinetReport original = first.Evaluate(city);
            byte[] saved = first.SaveState();

            AdvisorEngine second = new AdvisorEngine();
            second.LoadState(saved);
            BytesEqual(saved, second.SaveState(), "state save-load-save is byte-stable");
            city = Good(Date(2026, 6, 8));
            city.PollutionPercent = 20;
            CabinetReport reloaded = second.Evaluate(city);
            Equal(AdvisorMood.Angry, Environment(reloaded).Mood, "reload preserves ongoing issue start date");
            Equal(7, Environment(reloaded).ConcernDays, "reload preserves concern duration history");
            Equal(1, reloaded.News.Length, "reload does not repeat the original start news");
            Equal(original.Sequence + 1L, reloaded.Sequence, "reload preserves sequence");

            byte[] secondSave = second.SaveState();
            AdvisorEngine third = new AdvisorEngine();
            third.LoadState(secondSave);
            reloaded.News[0].Title = "texto alterado pelo consumidor";
            city = Good(Date(2026, 6, 8));
            city.PollutionPercent = 20;
            CabinetReport sameDay = third.Evaluate(city);
            Equal(1, sameDay.News.Length, "news returned in a report is a defensive copy");
            Contains(sameDay.News[0].Title, "Alerta", "consumer edits cannot mutate engine history");
            Equal(7, Environment(sameDay).ConcernDays, "reloaded same-day poll preserves the same concern duration");
        }

        private static void TestInvalidSerializationDoesNotReplaceState()
        {
            AdvisorEngine engine = new AdvisorEngine();
            CitySnapshot city = Good(Date(2026, 7, 1));
            city.CrimePercent = 10;
            engine.Evaluate(city);
            byte[] valid = engine.SaveState();

            byte[] corrupted = (byte[])valid.Clone();
            corrupted[corrupted.Length - 1] ^= 0x40;
            ExpectInvalidState(engine, corrupted, valid, "bad checksum is rejected transactionally");

            byte[] truncated = new byte[valid.Length - 1];
            Array.Copy(valid, truncated, truncated.Length);
            ExpectInvalidState(engine, truncated, valid, "truncated save is rejected transactionally");

            byte[] unknownVersion = (byte[])valid.Clone();
            unknownVersion[4] = 0x7F;
            ExpectInvalidState(engine, unknownVersion, valid, "unknown state version is rejected transactionally");

            byte[] trailing = new byte[valid.Length + 1];
            Array.Copy(valid, trailing, valid.Length);
            ExpectInvalidState(engine, trailing, valid, "trailing bytes are rejected transactionally");

            city = Good(Date(2026, 7, 8));
            city.CrimePercent = 10;
            CabinetReport report = engine.Evaluate(city);
            Equal(AdvisorMood.Angry, Safety(report).Mood, "failed loads preserve the original issue start date");
            Equal(7, Safety(report).ConcernDays, "failed loads preserve duration state");
            Equal(1, report.News.Length, "failed loads preserve prior news without duplication");
        }

        private static void TestNewsHistoryBound()
        {
            AdvisorEngine engine = new AdvisorEngine();
            DateTime start = Date(2026, 8, 1);
            int index;
            for (index = 0; index < 40; index++)
            {
                CitySnapshot bad = Good(start.AddDays(index * 2));
                bad.TrafficFlowPercent = 59;
                engine.Evaluate(bad);
                CitySnapshot recovered = Good(start.AddDays(index * 2 + 1));
                recovered.TrafficFlowPercent = 80;
                engine.Evaluate(recovered);
            }

            CabinetReport report = engine.Evaluate(Good(start.AddDays(80)));
            Equal(32, report.News.Length, "news history stays bounded at 32 items");
            Equal(81L, report.Sequence, "each evaluation advances sequence exactly once");
            Equal(IssueSeverity.None, report.News[31].Severity, "newest retained event is the final recovery");
            byte[] saved = engine.SaveState();
            AdvisorEngine loaded = new AdvisorEngine();
            loaded.LoadState(saved);
            Equal(32, loaded.Evaluate(Good(start.AddDays(80))).News.Length, "bounded news history survives serialization");
        }

        private static void ExpectInvalidState(AdvisorEngine engine, byte[] invalid, byte[] validBaseline, string message)
        {
            bool rejected = false;
            try
            {
                engine.LoadState(invalid);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            True(rejected, message + " (throws InvalidDataException)");
            BytesEqual(validBaseline, engine.SaveState(), message + " (prior state remains byte-identical)");
        }

        private static CitySnapshot Good(DateTime date)
        {
            return new CitySnapshot
            {
                DataValid = true,
                CityName = "Cidade de teste",
                SimulationDate = date,
                Population = 1000,
                Happiness = 75,
                Health = 75,
                CrimePercent = 0,
                PollutionPercent = 0,
                UnemploymentPercent = 0,
                Cash = 10000L,
                WeeklyBalance = 500L,
                TrafficFlowPercent = 80,
                NativeTaxMultiplier = double.NaN
            };
        }

        private static DateTime Date(int year, int month, int day)
        {
            return new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Unspecified);
        }

        private static AdvisorBriefing Finance(CabinetReport report) { return report.Briefings[(int)AdvisorId.Finance]; }
        private static AdvisorBriefing Urban(CabinetReport report) { return report.Briefings[(int)AdvisorId.UrbanPlanning]; }
        private static AdvisorBriefing EducationHealth(CabinetReport report) { return report.Briefings[(int)AdvisorId.EducationHealth]; }
        private static AdvisorBriefing Utilities(CabinetReport report) { return report.Briefings[(int)AdvisorId.Utilities]; }
        private static AdvisorBriefing Safety(CabinetReport report) { return report.Briefings[(int)AdvisorId.PublicSafety]; }
        private static AdvisorBriefing Environment(CabinetReport report) { return report.Briefings[(int)AdvisorId.Environment]; }
        private static AdvisorBriefing Transport(CabinetReport report) { return report.Briefings[(int)AdvisorId.Transport]; }

        private static void AssertAllNoIssue(CabinetReport report, string context)
        {
            int index;
            for (index = 0; index < report.Briefings.Length; index++)
            {
                Equal(IssueSeverity.None, report.Briefings[index].Severity, context + " severity for " + report.Briefings[index].Advisor);
                Equal(AdvisorMood.Calm, report.Briefings[index].Mood, context + " mood for " + report.Briefings[index].Advisor);
            }
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            _assertions++;
            if (!object.Equals(expected, actual))
                throw new Exception(message + ": expected " + expected + ", got " + actual + ".");
        }

        private static void True(bool condition, string message)
        {
            _assertions++;
            if (!condition)
                throw new Exception(message + ": condition was false.");
        }

        private static void Contains(string actual, string expectedText, string message)
        {
            _assertions++;
            if (actual == null || actual.IndexOf(expectedText, StringComparison.Ordinal) < 0)
                throw new Exception(message + ": expected '" + actual + "' to contain '" + expectedText + "'.");
        }

        private static void BytesEqual(byte[] expected, byte[] actual, string message)
        {
            _assertions++;
            if (expected.Length != actual.Length)
                throw new Exception(message + ": byte lengths differ.");
            int index;
            for (index = 0; index < expected.Length; index++)
            {
                if (expected[index] != actual[index])
                    throw new Exception(message + ": byte arrays differ at offset " + index + ".");
            }
        }
    }
}
