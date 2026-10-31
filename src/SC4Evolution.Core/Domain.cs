using System;

namespace SC4Evolution.Core
{
    public enum AdvisorId
    {
        Finance,
        UrbanPlanning,
        EducationHealth,
        Utilities,
        PublicSafety,
        Environment,
        Transport
    }

    public enum AdvisorMood
    {
        Calm,
        Concerned,
        Angry,
        Pleased
    }

    public enum IssueSeverity
    {
        None,
        Notice,
        Warning,
        Critical
    }

    public enum AdvisorAction
    {
        Overview,
        Budget,
        Education,
        Health,
        Electricity,
        Water,
        Crime,
        Pollution,
        Traffic,
        Zoning,
        Garbage
    }

    public sealed class CitySnapshot
    {
        public bool DataValid { get; set; }
        public string CityName { get; set; }
        public DateTime SimulationDate { get; set; }
        public int Population { get; set; }
        public int Happiness { get; set; }
        public int Health { get; set; }
        public int CrimePercent { get; set; }
        public int PollutionPercent { get; set; }
        public int UnemploymentPercent { get; set; }
        public long Cash { get; set; }
        public long WeeklyBalance { get; set; }
        public int ElementaryNeed { get; set; }
        public int ElementaryCapacity { get; set; }
        // Availability is the native milestone state, distinct from capacity.
        // Core fixtures default to true; the game adapter overwrites these with
        // the actual unlocked state before evaluating advisors.
        public bool ElementaryEducationAvailable { get; set; } = true;
        public int HighSchoolNeed { get; set; }
        public int HighSchoolCapacity { get; set; }
        public bool HighSchoolEducationAvailable { get; set; } = true;
        public int UniversityNeed { get; set; }
        public int UniversityCapacity { get; set; }
        public bool UniversityEducationAvailable { get; set; } = true;
        public int ElectricityUse { get; set; }
        public int ElectricityCapacity { get; set; }
        public int WaterUse { get; set; }
        public int WaterCapacity { get; set; }
        public int SewageUse { get; set; }
        public int SewageCapacity { get; set; }
        public int GarbageStored { get; set; }
        public int GarbageCapacity { get; set; }
        public int SickCitizens { get; set; }
        public int HealthCapacity { get; set; }
        public bool HealthServiceAvailable { get; set; } = true;
        public int ResidentialDemand { get; set; }
        public int CommercialDemand { get; set; }
        public int WorkplaceDemand { get; set; }
        public int ResidentialAbandoned { get; set; }
        public int CommercialAbandoned { get; set; }
        public int IndustrialAbandoned { get; set; }
        public int OfficeAbandoned { get; set; }
        public int ZonedBuildings { get; set; }
        public int TrafficFlowPercent { get; set; } = -1;
        public int OwnedTiles { get; set; }
        public int PurchasableTileLimit { get; set; }
        public double NativeTaxMultiplier { get; set; } = double.NaN;
    }

    public sealed class AdvisorBriefing
    {
        public AdvisorId Advisor { get; set; }
        public string Name { get; set; }
        public string Department { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public string MetricText { get; set; }
        public string Detail { get; set; }
        public AdvisorMood Mood { get; set; }
        public IssueSeverity Severity { get; set; }
        public AdvisorAction Action { get; set; }
        public int ConcernDays { get; set; }
        // An ongoing episode cannot currently be measured; this is not recovery.
        public bool IsSuspended { get; set; }
    }

    public sealed class CityNewsItem
    {
        public DateTime Date { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public IssueSeverity Severity { get; set; }
    }

    public sealed class CabinetReport
    {
        public CitySnapshot Snapshot { get; set; }
        public AdvisorBriefing[] Briefings { get; set; }
        public CityNewsItem[] News { get; set; }
        public long Sequence { get; set; }
    }
}
