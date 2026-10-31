using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SC4Evolution.Core
{
    /// <summary>
    /// Evaluates simulation snapshots without depending on the game runtime.
    /// Thresholds are provisional cabinet notifications, not extracted SC4 rules.
    /// </summary>
    public sealed class AdvisorEngine
    {
        [ThreadStatic] private static bool _english;
        private static string L(string portuguese, string english) { return _english ? english : portuguese; }

        private const uint StateMagic = 0x41344353; // "SC4A" in the byte stream.
        private const ushort StateVersion = 2;
        private const int MaxStateBytes = 131072;
        private const int MaxStringBytes = 1024;
        private const int MaxNewsItems = 32;
        private const int AdvisorCount = 7;
        private const int IssueCount = 15;

        private static readonly string[] IssueIds = new string[]
        {
            "finance.cash-negative",
            "finance.weekly-deficit",
            "urban.unemployment",
            "urban.abandonment",
            "education.elementary",
            "education.high-school",
            "education.university",
            "health.capacity",
            "utilities.electricity",
            "utilities.water",
            "utilities.sewage",
            "safety.crime",
            "environment.pollution",
            "environment.garbage",
            "transport.traffic"
        };

        private static readonly AdvisorId[] IssueAdvisors = new AdvisorId[]
        {
            AdvisorId.Finance, AdvisorId.Finance,
            AdvisorId.UrbanPlanning, AdvisorId.UrbanPlanning,
            AdvisorId.EducationHealth, AdvisorId.EducationHealth, AdvisorId.EducationHealth, AdvisorId.EducationHealth,
            AdvisorId.Utilities, AdvisorId.Utilities, AdvisorId.Utilities,
            AdvisorId.PublicSafety,
            AdvisorId.Environment, AdvisorId.Environment,
            AdvisorId.Transport
        };

        private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly IssueState[] _issueStates = new IssueState[IssueCount];
        private readonly List<CityNewsItem> _news = new List<CityNewsItem>();
        private long _sequence;

        public CabinetReport Evaluate(CitySnapshot snapshot)
        {
            _english = snapshot != null && snapshot.English;
            DateTime date = snapshot == null ? DateTime.MinValue : snapshot.SimulationDate.Date;
            _sequence = _sequence == long.MaxValue ? long.MaxValue : _sequence + 1L;
            AdvisorBriefing[] briefings = new AdvisorBriefing[AdvisorCount];

            if (snapshot == null || !snapshot.DataValid)
            {
                int invalidIndex;
                for (invalidIndex = 0; invalidIndex < AdvisorCount; invalidIndex++)
                    briefings[invalidIndex] = InvalidBriefing((AdvisorId)invalidIndex);
            }
            else
            {
                if (snapshot.Population <= 0)
                {
                    // With no residents, suspend new alerts and show founding guidance.
                    // Existing episodes can close only when their own measurements
                    // are present and now satisfy the resolution condition.
                    CloseMeasuredIssuesOnEmptyCity(snapshot, date);
                    int emptyIndex;
                    for (emptyIndex = 0; emptyIndex < AdvisorCount; emptyIndex++)
                        briefings[emptyIndex] = EmptyCityBriefing((AdvisorId)emptyIndex);
                }
                else
                {
                    Signal[] activeSignals = FindSignals(snapshot, out Signal[] prioritySignals);
                    string[] resolvedIssueIds = new string[AdvisorCount];
                    IssueSeverity[] resolvedSeverities = new IssueSeverity[AdvisorCount];
                    string[] unavailableIssueIds = new string[AdvisorCount];
                    int issueIndex;
                    for (issueIndex = 0; issueIndex < IssueCount; issueIndex++)
                    {
                        IssueState previous = _issueStates[issueIndex];
                        Signal signal = activeSignals[issueIndex];
                        AdvisorId advisor = IssueAdvisors[issueIndex];
                        if (signal != null)
                        {
                            if (previous == null)
                            {
                                _issueStates[issueIndex] = new IssueState(date, signal.Severity);
                                AddNews(date, L("Alerta: ", "Alert: ") + signal.Title, signal.Message + " " + signal.Detail, signal.Severity);
                            }
                            else
                            {
                                if (previous.Severity != signal.Severity)
                                {
                                    IssueSeverity oldSeverity = previous.Severity;
                                    previous.Severity = signal.Severity;
                                    AddNews(date, L("Alerta atualizado: ", "Updated alert: ") + signal.Title,
                                        L("A gravidade de ", "The severity of ") + IssueSubject(IssueIds[issueIndex]) + L(" mudou de ", " changed from ") + SeverityName(oldSeverity) + L(" para ", " to ") + SeverityName(signal.Severity) + ". " + signal.Message,
                                        signal.Severity);
                                }
                            }
                        }
                        else if (IsIssueUnavailable(snapshot, issueIndex))
                        {
                            if (previous != null && unavailableIssueIds[(int)advisor] == null)
                                unavailableIssueIds[(int)advisor] = IssueIds[issueIndex];
                        }
                        else if (previous != null)
                        {
                            AddRecoveryNews(advisor, IssueIds[issueIndex], date);
                            _issueStates[issueIndex] = null;
                            int recoveryAdvisorIndex = (int)advisor;
                            if (resolvedIssueIds[recoveryAdvisorIndex] == null || previous.Severity > resolvedSeverities[recoveryAdvisorIndex])
                            {
                                resolvedIssueIds[recoveryAdvisorIndex] = IssueIds[issueIndex];
                                resolvedSeverities[recoveryAdvisorIndex] = previous.Severity;
                            }
                        }
                    }

                    int advisorIndex;
                    for (advisorIndex = 0; advisorIndex < AdvisorCount; advisorIndex++)
                    {
                        AdvisorId advisor = (AdvisorId)advisorIndex;
                        Signal priority = prioritySignals[advisorIndex];
                        if (priority != null)
                        {
                            int priorityIssueIndex = IssueIndex(priority.IssueId);
                            briefings[advisorIndex] = ActiveBriefing(advisor, _issueStates[priorityIssueIndex], priority, date);
                        }
                        else if (unavailableIssueIds[advisorIndex] != null)
                        {
                            briefings[advisorIndex] = UnavailableBriefing(advisor, unavailableIssueIds[advisorIndex]);
                        }
                        else if (resolvedIssueIds[advisorIndex] != null)
                        {
                            briefings[advisorIndex] = RecoveryBriefing(advisor, resolvedIssueIds[advisorIndex]);
                        }
                        else
                        {
                            briefings[advisorIndex] = CalmBriefing(snapshot, advisor);
                        }
                    }
                }
            }

            return new CabinetReport
            {
                Snapshot = snapshot,
                Briefings = briefings,
                News = CopyNews(),
                Sequence = _sequence
            };
        }

        public byte[] SaveState()
        {
            MemoryStream payloadStream = new MemoryStream();
            BinaryWriter payloadWriter = new BinaryWriter(payloadStream, Encoding.UTF8);
            payloadWriter.Write(_sequence);
            payloadWriter.Write((byte)_news.Count);

            int newsIndex;
            for (newsIndex = 0; newsIndex < _news.Count; newsIndex++)
            {
                CityNewsItem item = _news[newsIndex];
                payloadWriter.Write(item.Date.Date.Ticks);
                WriteString(payloadWriter, item.Title);
                WriteString(payloadWriter, item.Summary);
                payloadWriter.Write((byte)item.Severity);
            }

            payloadWriter.Write((byte)IssueCount);
            int issueIndex;
            for (issueIndex = 0; issueIndex < IssueCount; issueIndex++)
            {
                IssueState state = _issueStates[issueIndex];
                payloadWriter.Write((byte)(state == null ? 0 : 1));
                if (state != null)
                {
                    payloadWriter.Write(state.StartDate.Date.Ticks);
                    payloadWriter.Write((byte)state.Severity);
                }
            }

            payloadWriter.Flush();
            byte[] payload = payloadStream.ToArray();
            payloadWriter.Close();
            payloadStream.Close();

            MemoryStream stateStream = new MemoryStream();
            BinaryWriter stateWriter = new BinaryWriter(stateStream, Encoding.UTF8);
            stateWriter.Write(StateMagic);
            stateWriter.Write(StateVersion);
            stateWriter.Write(payload.Length);
            stateWriter.Write(payload);
            stateWriter.Write(Crc32(payload));
            stateWriter.Flush();
            byte[] data = stateStream.ToArray();
            stateWriter.Close();
            stateStream.Close();

            if (data.Length > MaxStateBytes)
                throw new InvalidOperationException("Advisor state exceeded its serialization bound.");
            return data;
        }

        public void LoadState(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException("data");
            if (data.Length < 14 || data.Length > MaxStateBytes)
                throw new InvalidDataException("Advisor state has an invalid size.");

            try
            {
                MemoryStream stream = new MemoryStream(data, false);
                BinaryReader reader = new BinaryReader(stream, Encoding.UTF8);
                uint magic = reader.ReadUInt32();
                if (magic != StateMagic)
                    throw new InvalidDataException("Advisor state has an invalid signature.");

                ushort version = reader.ReadUInt16();
                if (version != StateVersion)
                    throw new InvalidDataException("Advisor state version is not supported.");

                int payloadLength = reader.ReadInt32();
                if (payloadLength < 0 || payloadLength != data.Length - 14)
                    throw new InvalidDataException("Advisor state payload length is invalid.");

                byte[] payload = reader.ReadBytes(payloadLength);
                if (payload.Length != payloadLength)
                    throw new InvalidDataException("Advisor state payload is truncated.");
                uint checksum = reader.ReadUInt32();
                if (stream.Position != stream.Length || checksum != Crc32(payload))
                    throw new InvalidDataException("Advisor state checksum or trailing data is invalid.");
                reader.Close();
                stream.Close();

                long loadedSequence;
                List<CityNewsItem> loadedNews;
                IssueState[] loadedStates;
                ParsePayload(payload, out loadedSequence, out loadedNews, out loadedStates);

                // Commit only after the complete input has passed validation.
                _sequence = loadedSequence;
                _news.Clear();
                _news.AddRange(loadedNews);
                int index;
                for (index = 0; index < IssueCount; index++)
                    _issueStates[index] = loadedStates[index];
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (exception is EndOfStreamException || exception is IOException ||
                    exception is ArgumentException || exception is OverflowException)
                    throw new InvalidDataException("Advisor state is corrupt or truncated.", exception);
                throw;
            }
        }

        public void Reset()
        {
            int index;
            for (index = 0; index < IssueCount; index++)
                _issueStates[index] = null;
            _news.Clear();
            _sequence = 0L;
        }

        private static AdvisorBriefing ActiveBriefing(AdvisorId advisor, IssueState state, Signal signal, DateTime date)
        {
            int concernDays = DaysSince(state.StartDate, date);
            AdvisorMood mood = concernDays >= 7 ? AdvisorMood.Angry : AdvisorMood.Concerned;
            return new AdvisorBriefing
            {
                Advisor = advisor,
                Name = AdvisorName(advisor),
                Department = DepartmentName(advisor),
                Title = signal.Title,
                Message = signal.Message,
                MetricText = signal.MetricText,
                Detail = signal.Detail,
                Mood = mood,
                Severity = signal.Severity,
                Action = signal.Action,
                ConcernDays = concernDays
            };
        }

        private void AddRecoveryNews(AdvisorId advisor, string issueId, DateTime date)
        {
            string subject = IssueSubject(issueId);
            AddNews(date, L("Recuperação: ", "Recovery: ") + subject,
                RecoveryText(issueId) + L(" A assessoria encerrou este alerta.", " The advisor closed this alert."), IssueSeverity.None);
        }

        private void CloseMeasuredIssuesOnEmptyCity(CitySnapshot snapshot, DateTime date)
        {
            Signal[] measuredSignals = FindSignals(snapshot, out _);
            int issueIndex;
            for (issueIndex = 0; issueIndex < IssueCount; issueIndex++)
            {
                if (_issueStates[issueIndex] != null && measuredSignals[issueIndex] == null && !IsIssueUnavailable(snapshot, issueIndex))
                {
                    AddRecoveryNews(IssueAdvisors[issueIndex], IssueIds[issueIndex], date);
                    _issueStates[issueIndex] = null;
                }
            }
        }

        private void AddNews(DateTime date, string title, string summary, IssueSeverity severity)
        {
            if (_news.Count == MaxNewsItems)
                _news.RemoveAt(0);
            _news.Add(new CityNewsItem
            {
                Date = date.Date,
                Title = title,
                Summary = summary,
                Severity = severity
            });
        }

        private CityNewsItem[] CopyNews()
        {
            CityNewsItem[] copy = new CityNewsItem[_news.Count];
            int index;
            for (index = 0; index < _news.Count; index++)
            {
                CityNewsItem item = _news[index];
                copy[index] = new CityNewsItem
                {
                    Date = item.Date,
                    Title = item.Title,
                    Summary = item.Summary,
                    Severity = item.Severity
                };
            }
            return copy;
        }

        private static Signal[] FindSignals(CitySnapshot snapshot, out Signal[] prioritySignals)
        {
            Signal[] activeByIssue = new Signal[IssueCount];
            prioritySignals = new Signal[AdvisorCount];
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Finance, CashSignal(snapshot));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Finance, WeeklyDeficitSignal(snapshot));

            AddSignal(activeByIssue, prioritySignals, AdvisorId.UrbanPlanning, UnemploymentSignal(snapshot));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.UrbanPlanning, AbandonmentSignal(snapshot));

            if (snapshot.HealthServiceAvailable)
                AddSignal(activeByIssue, prioritySignals, AdvisorId.EducationHealth, HealthSignal(snapshot));
            if (snapshot.ElementaryEducationAvailable)
                AddSignal(activeByIssue, prioritySignals, AdvisorId.EducationHealth,
                    SchoolSignal(snapshot.ElementaryNeed, snapshot.ElementaryCapacity, L("ensino fundamental", "elementary school"), "education.elementary"));
            if (snapshot.HighSchoolEducationAvailable)
                AddSignal(activeByIssue, prioritySignals, AdvisorId.EducationHealth,
                    SchoolSignal(snapshot.HighSchoolNeed, snapshot.HighSchoolCapacity, L("ensino médio", "high school"), "education.high-school"));
            if (snapshot.UniversityEducationAvailable)
                AddSignal(activeByIssue, prioritySignals, AdvisorId.EducationHealth,
                    SchoolSignal(snapshot.UniversityNeed, snapshot.UniversityCapacity, L("ensino superior", "higher education"), "education.university"));

            AddSignal(activeByIssue, prioritySignals, AdvisorId.Utilities,
                UtilitySignal(snapshot.ElectricityUse, snapshot.ElectricityCapacity, L("eletricidade", "electricity"), "utilities.electricity", AdvisorAction.Electricity));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Utilities,
                UtilitySignal(snapshot.WaterUse, snapshot.WaterCapacity, L("água", "water"), "utilities.water", AdvisorAction.Water));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Utilities,
                UtilitySignal(snapshot.SewageUse, snapshot.SewageCapacity, L("esgoto", "sewage"), "utilities.sewage", AdvisorAction.Water));

            AddSignal(activeByIssue, prioritySignals, AdvisorId.PublicSafety, SafetySignal(snapshot));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Environment, PollutionSignal(snapshot));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Environment, GarbageSignal(snapshot));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Transport, TransportSignal(snapshot));
            return activeByIssue;
        }

        private static Signal CashSignal(CitySnapshot snapshot)
        {
            if (snapshot.Cash < 0L)
                return new Signal("finance.cash-negative", IssueSeverity.Critical, L("Caixa municipal negativo", "City treasury is negative"),
                    L("O caixa municipal está negativo (", "The city treasury is negative (") + Currency(snapshot.Cash) + ").",
                    L("Caixa: ", "Treasury: ") + Currency(snapshot.Cash),
                    L("Revise o orçamento e acompanhe receitas e despesas antes de assumir novos compromissos.", "Review your budget and track income and expenses before making new commitments."), AdvisorAction.Budget);
            return null;
        }

        private static Signal WeeklyDeficitSignal(CitySnapshot snapshot)
        {
            if (snapshot.WeeklyBalance >= 0L)
                return null;

            ulong deficit = UnsignedMagnitude(snapshot.WeeklyBalance);
            bool lessThanFourWeeks = snapshot.Cash >= 0L && (ulong)snapshot.Cash / deficit < 4UL;
            IssueSeverity severity = lessThanFourWeeks ? IssueSeverity.Critical : IssueSeverity.Warning;
            string detail = lessThanFourWeeks
                ? L("A reserva financeira é pequena diante do déficit informado. Revise o orçamento e acompanhe receitas e despesas.", "The financial reserve is small compared to the reported deficit. Review the budget and track income and expenses.")
                : L("Acompanhe receitas e despesas para reverter o saldo orçamentário negativo.", "Track income and expenses to reverse negative budget balances.");
            string message = L("O saldo orçamentário informado é negativo (", "The reported budget balance is negative (") + Currency(snapshot.WeeklyBalance) + ").";
            if (lessThanFourWeeks)
                message += L(" A reserva financeira está abaixo do limite de referência para esse déficit.", " The financial reserve is below the reference limit for this deficit.");
            return new Signal("finance.weekly-deficit", severity, L("Déficit orçamentário", "Budget deficit"),
                message, L("Saldo informado: ", "Balance reported: ") + Currency(snapshot.WeeklyBalance) + L("; caixa: ", "; treasury: ") + Currency(snapshot.Cash), detail, AdvisorAction.Budget);
        }

        private static Signal UnemploymentSignal(CitySnapshot snapshot)
        {
            if (snapshot.Population < 50 || snapshot.UnemploymentPercent < 12)
                return null;
            IssueSeverity severity = snapshot.UnemploymentPercent >= 25 ? IssueSeverity.Critical : IssueSeverity.Warning;
            return new Signal("urban.unemployment", severity,
                severity == IssueSeverity.Critical ? L("Desemprego elevado", "High unemployment") : L("Desemprego em alerta", "Unemployment on alert"),
                L("O desemprego chegou a ", "Unemployment reached ") + snapshot.UnemploymentPercent + "%.",
                L("Desemprego: ", "Unemployment: ") + snapshot.UnemploymentPercent + L("%; população: ", "%; population: ") + snapshot.Population,
                L("Acompanhe a demanda por empregos e revise a distribuição do zoneamento.", "Track job demand and review zoning distribution."), AdvisorAction.Zoning);
        }

        private static Signal AbandonmentSignal(CitySnapshot snapshot)
        {
            if (snapshot.ZonedBuildings <= 0)
                return null;
            long abandoned = NonNegative(snapshot.ResidentialAbandoned) + NonNegative(snapshot.CommercialAbandoned) +
                NonNegative(snapshot.IndustrialAbandoned) + NonNegative(snapshot.OfficeAbandoned);
            long denominator = snapshot.ZonedBuildings;
            if (abandoned * 100L < denominator * 5L)
                return null;
            IssueSeverity severity = abandoned * 100L >= denominator * 15L ? IssueSeverity.Critical : IssueSeverity.Warning;
            double share = (double)abandoned * 100.0 / (double)denominator;
            return new Signal("urban.abandonment", severity,
                severity == IssueSeverity.Critical ? L("Abandono de edifícios elevado", "High building abandonment") : L("Edifícios abandonados em alerta", "Abandoned buildings on alert"),
                L("Há ", "There are ") + abandoned + L(" edifícios abandonados entre ", " abandoned buildings among ") + denominator + L(" zoneados (", " zoned (") + Number(share, "0.0") + "%).",
                L("Abandonados: ", "Abandoned: ") + abandoned + L("; zoneados: ", "; zoned: ") + denominator + L("; participação: ", "; participation: ") + Number(share, "0.0") + "%",
                L("Revise o zoneamento e os indicadores locais para identificar obstáculos à ocupação.", "Review zoning and local indicators to identify obstacles to occupancy."), AdvisorAction.Zoning);
        }

        private static Signal SchoolSignal(int need, int capacity, string school, string issueId)
        {
            if (need <= 0 || need <= capacity)
                return null;
            bool critical = (long)capacity * 100L < (long)need * 75L;
            IssueSeverity severity = critical ? IssueSeverity.Critical : IssueSeverity.Warning;
            return new Signal(issueId, severity,
                critical ? L("Capacidade escolar muito abaixo da demanda", "School capacity far below demand") : L("Faltam vagas na rede escolar", "Lack of places in the school network"),
                L("A demanda por ", "The demand for ") + school + L(" é de ", " is ") + need + L(" vagas; a capacidade informada é ", " places; reported capacity is ") + capacity + ".",
                school + L(": demanda ", ": demand ") + need + L(", capacidade ", ", capacity ") + capacity,
                L("Acompanhe a demanda escolar e planeje capacidade compatível com as necessidades informadas.", "Monitor school demand and plan capacity compatible with reported needs."), AdvisorAction.Education);
        }

        private static Signal HealthSignal(CitySnapshot snapshot)
        {
            int sick = snapshot.SickCitizens;
            int capacity = snapshot.HealthCapacity;
            if (sick <= 0 || sick <= capacity)
                return null;
            bool critical = (long)capacity * 100L < (long)sick * 67L;
            IssueSeverity severity = critical ? IssueSeverity.Critical : IssueSeverity.Warning;
            return new Signal("health.capacity", severity,
                critical ? L("Atendimento de saúde muito abaixo da necessidade", "Healthcare capacity far below demand") : L("Atendimento de saúde em alerta", "Health care on alert"),
                L("Há ", "There are ") + sick + L(" moradores doentes e capacidade informada para ", " sick residents and reported capacity for ") + capacity + ".",
                L("Moradores doentes: ", "Sick residents: ") + sick + L("; capacidade de saúde: ", "; health capacity: ") + capacity,
                L("Acompanhe a necessidade de atendimento e planeje capacidade de saúde compatível.", "Track the need for care and plan compatible healthcare capacity."), AdvisorAction.Health);
        }

        private static Signal UtilitySignal(int use, int capacity, string service, string issueId, AdvisorAction action)
        {
            if (use <= 0 || use <= capacity)
                return null;
            bool critical = (long)capacity * 100L < (long)use * 80L;
            IssueSeverity severity = critical ? IssueSeverity.Critical : IssueSeverity.Warning;
            return new Signal(issueId, severity,
                critical ? L("Capacidade de ", "Capacity for ") + service + L(" muito abaixo do uso", " far below usage") : L("Capacidade de ", "Capacity for ") + service + L(" em alerta", " on alert"),
                L("O uso de ", "The use of ") + service + L(" é ", " is ") + use + L(", acima da capacidade informada de ", ", above the reported capacity of ") + capacity + ".",
                L("Uso de ", "Use of ") + service + ": " + use + L("; capacidade: ", "; capacity: ") + capacity,
                L("Acompanhe o uso e planeje capacidade compatível com a demanda informada.", "Track usage and plan capacity compatible with reported demand."), action);
        }

        private static Signal PollutionSignal(CitySnapshot snapshot)
        {
            if (snapshot.PollutionPercent < 20)
                return null;
            IssueSeverity severity = snapshot.PollutionPercent >= 40 ? IssueSeverity.Critical : IssueSeverity.Warning;
            return new Signal("environment.pollution", severity,
                severity == IssueSeverity.Critical ? L("Poluição elevada", "High pollution") : L("Poluição em alerta", "Pollution on alert"),
                L("A poluição chegou a ", "Pollution reached ") + snapshot.PollutionPercent + "%.",
                L("Poluição: ", "Pollution: ") + snapshot.PollutionPercent + "%",
                L("Acompanhe os indicadores ambientais e inspecione as áreas com maior poluição.", "Monitor environmental indicators and inspect areas with the greatest pollution."), AdvisorAction.Pollution);
        }

        private static Signal GarbageSignal(CitySnapshot snapshot)
        {
            int stored = snapshot.GarbageStored;
            int capacity = snapshot.GarbageCapacity;
            IssueSeverity severity;
            if (capacity == 0)
            {
                if (stored <= 0)
                    return null;
                severity = IssueSeverity.Critical;
            }
            else if (capacity < 0 || stored < 0)
            {
                return null;
            }
            else if ((long)stored * 100L >= (long)capacity * 100L)
            {
                severity = IssueSeverity.Critical;
            }
            else if ((long)stored * 100L >= (long)capacity * 85L)
            {
                severity = IssueSeverity.Warning;
            }
            else
            {
                return null;
            }

            string capacityText = capacity == 0 ? L("sem capacidade informada", "no capacity reported") : L("capacidade de ", "capacity of ") + capacity;
            string percentageText = capacity > 0 ? " (" + Number((double)stored * 100.0 / (double)capacity, "0.0") + "%)" : string.Empty;
            return new Signal("environment.garbage", severity,
                severity == IssueSeverity.Critical ? L("Armazenamento de lixo no limite", "Waste storage at the limit") : L("Armazenamento de lixo em alerta", "Garbage storage on alert"),
                L("Há ", "There are ") + stored + L(" unidades de lixo armazenadas, com ", " stored garbage units, with ") + capacityText + percentageText + ".",
                L("Lixo armazenado: ", "Garbage stored: ") + stored + L("; capacidade: ", "; capacity: ") + capacity,
                L("Acompanhe o armazenamento e planeje o escoamento do lixo antes de atingir o limite.", "Monitor storage and plan garbage disposal before reaching the limit."), AdvisorAction.Garbage);
        }

        private static Signal SafetySignal(CitySnapshot snapshot)
        {
            if (snapshot.CrimePercent >= 25)
                return new Signal("safety.crime", IssueSeverity.Critical, L("Criminalidade elevada", "High crime"),
                    L("A criminalidade chegou a ", "Crime reached ") + snapshot.CrimePercent + "%.", L("Criminalidade: ", "Crime: ") + snapshot.CrimePercent + "%",
                    L("Revise os indicadores de segurança e a cobertura dos serviços policiais.", "Review security indicators and coverage of police services."), AdvisorAction.Crime);
            if (snapshot.CrimePercent >= 10)
                return new Signal("safety.crime", IssueSeverity.Warning, L("Criminalidade em alerta", "Crime on alert"),
                    L("A criminalidade chegou a ", "Crime reached ") + snapshot.CrimePercent + "%.", L("Criminalidade: ", "Crime: ") + snapshot.CrimePercent + "%",
                    L("Acompanhe a segurança e inspecione as áreas com maior necessidade.", "Monitor security and inspect areas of greatest need."), AdvisorAction.Crime);
            return null;
        }

        private static Signal TransportSignal(CitySnapshot snapshot)
        {
            if (snapshot.TrafficFlowPercent >= 0 && snapshot.TrafficFlowPercent <= 100 && snapshot.TrafficFlowPercent < 35)
                return new Signal("transport.traffic", IssueSeverity.Critical, L("Trânsito muito congestionado", "Very congested traffic"),
                    L("O fluxo de trânsito está em ", "The traffic flow is ") + snapshot.TrafficFlowPercent + "%.", L("Fluxo: ", "Flow: ") + snapshot.TrafficFlowPercent + "%",
                    L("Inspecione os pontos de congestionamento e as rotas usadas pelos moradores.", "Inspect congestion points and routes used by residents."), AdvisorAction.Traffic);
            if (snapshot.TrafficFlowPercent >= 0 && snapshot.TrafficFlowPercent <= 100 && snapshot.TrafficFlowPercent < 60)
                return new Signal("transport.traffic", IssueSeverity.Warning, L("Fluxo de trânsito baixo", "Low traffic flow"),
                    L("O fluxo de trânsito está em ", "The traffic flow is ") + snapshot.TrafficFlowPercent + "%.", L("Fluxo: ", "Flow: ") + snapshot.TrafficFlowPercent + "%",
                    L("Acompanhe os corredores mais carregados e as alternativas de deslocamento.", "Keep track of the busiest corridors and travel alternatives."), AdvisorAction.Traffic);
            return null;
        }

        private static void AddSignal(Signal[] activeByIssue, Signal[] prioritySignals, AdvisorId advisor, Signal candidate)
        {
            if (candidate == null)
                return;
            int issueIndex = IssueIndex(candidate.IssueId);
            if (issueIndex < 0 || IssueAdvisors[issueIndex] != advisor)
                throw new InvalidOperationException("Advisor signal does not match the finite issue registry.");
            activeByIssue[issueIndex] = candidate;
            Select(ref prioritySignals[(int)advisor], candidate);
        }

        private static void Select(ref Signal selected, Signal candidate)
        {
            if (candidate != null && (selected == null || candidate.Severity > selected.Severity))
                selected = candidate;
        }

        private static bool IsIssueUnavailable(CitySnapshot snapshot, int issueIndex)
        {
            if (issueIndex == 2 && snapshot.Population < 50)
                return true;
            if (issueIndex == 3 && snapshot.ZonedBuildings <= 0)
                return true;
            if (issueIndex == 4 && !snapshot.ElementaryEducationAvailable)
                return true;
            if (issueIndex == 5 && !snapshot.HighSchoolEducationAvailable)
                return true;
            if (issueIndex == 6 && !snapshot.UniversityEducationAvailable)
                return true;
            if (issueIndex == 7 && !snapshot.HealthServiceAvailable)
                return true;
            if (issueIndex == 14 && (snapshot.TrafficFlowPercent < 0 || snapshot.TrafficFlowPercent > 100))
                return true;
            return false;
        }

        private static AdvisorBriefing InvalidBriefing(AdvisorId advisor)
        {
            return NewBriefing(advisor, L("Aguardando dados da cidade", "Waiting for city data"), L("Os dados municipais ainda não estão disponíveis; nenhum alerta foi avaliado.", "Municipal data are not yet available; no alerts were evaluated."),
                L("Dados indisponíveis", "Data unavailable"), L("Esta assessoria será atualizada quando os dados da cidade estiverem disponíveis.", "This briefing will be updated when city data is available."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
        }

        private static AdvisorBriefing UnavailableBriefing(AdvisorId advisor, string issueId)
        {
            AdvisorBriefing briefing;
            if (advisor == AdvisorId.Transport)
                briefing = NewBriefing(advisor, L("Fluxo de trânsito indisponível", "Traffic flow unavailable"),
                    L("O jogo não forneceu um fluxo de trânsito válido neste levantamento; não há base para confirmar um alerta.", "The game did not provide a valid traffic flow in this survey; there is no basis for confirming an alert."),
                    L("Fluxo: indisponível", "Flow: unavailable"), L("O histórico do alerta fica preservado até que o indicador possa ser avaliado novamente.", "The alert history is preserved until the indicator can be evaluated again."),
                    AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Traffic, 0);
            else if (string.Equals(issueId, "urban.unemployment", StringComparison.Ordinal))
                briefing = NewBriefing(advisor, L("Avaliação de desemprego suspensa", "Unemployment assessment suspended"),
                    L("A população está abaixo do mínimo usado para avaliar o desemprego; o histórico do alerta foi preservado.", "The population is below the minimum used to assess unemployment; the alert history was preserved."),
                    L("Desemprego: não avaliado", "Unemployment: not assessed"), L("A assessoria retomará a avaliação quando a população atingir o mínimo definido.", "The advisor will resume the assessment when the population reaches the defined minimum."),
                    AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Zoning, 0);
            else if (advisor == AdvisorId.EducationHealth)
                briefing = NewBriefing(advisor, L("Serviço ainda não liberado", "Service not yet unlocked"),
                    L("Este serviço ainda não está disponível; o alerta anterior permanece suspenso.", "This service is not yet available; the previous alert remains suspended."),
                    L("Avaliação suspensa", "Assessment suspended"), L("A assessoria retomará a avaliação quando o serviço estiver disponível.", "The advisor will resume the assessment once the service is available."),
                    AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
            else briefing = NewBriefing(advisor, L("Indicador de abandono indisponível", "Abandonment data unavailable"),
                L("Não há edifícios zoneados suficientes para calcular a participação de abandono.", "There are not enough zoned buildings to calculate the abandonment share."),
                L("Abandonados: indisponível", "Abandoned: unavailable"), L("Um denominador zero não representa abandono total.", "A zero denominator does not represent total abandonment."),
                AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Zoning, 0);
            briefing.IsSuspended = true;
            return briefing;
        }

        private static AdvisorBriefing RecoveryBriefing(AdvisorId advisor, string issueId)
        {
            return NewBriefing(advisor, L("Situação normalizada", "Situation resolved"), RecoveryText(issueId),
                L("Sem alerta ativo", "No active alert"), L("A assessoria reconhece a recuperação uma vez e encerra este episódio.", "The advisor acknowledges the recovery and closes this episode."),
                AdvisorMood.Pleased, IssueSeverity.None, AdvisorAction.Overview, 0);
        }

        private static AdvisorBriefing CalmBriefing(CitySnapshot snapshot, AdvisorId advisor)
        {
            if (snapshot.Population <= 0)
                return EmptyCityBriefing(advisor);

            switch (advisor)
            {
                case AdvisorId.Finance:
                    return NewBriefing(advisor, L("Contas sem alerta", "Finances: no alerts"), L("O caixa não está negativo e o saldo orçamentário informado não indica déficit.", "Cash is not negative and the reported budget balance does not indicate a deficit."),
                        L("Caixa: ", "Treasury: ") + Currency(snapshot.Cash) + L("; saldo informado: ", "; balance reported: ") + Currency(snapshot.WeeklyBalance),
                        L("Continue acompanhando as contas municipais.", "Continue monitoring municipal accounts."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Budget, 0);
                case AdvisorId.UrbanPlanning:
                    string unemployment = snapshot.Population >= 50
                        ? L("Desemprego: ", "Unemployment: ") + snapshot.UnemploymentPercent + "%"
                        : L("População abaixo do mínimo para avaliar desemprego", "Population below the minimum for assessing unemployment");
                    long abandoned = NonNegative(snapshot.ResidentialAbandoned) + NonNegative(snapshot.CommercialAbandoned) +
                        NonNegative(snapshot.IndustrialAbandoned) + NonNegative(snapshot.OfficeAbandoned);
                    string abandonment = snapshot.ZonedBuildings > 0
                        ? L("; abandonados: ", "; abandoned: ") + abandoned + "/" + snapshot.ZonedBuildings
                        : L("; participação de abandono indisponível", "; abandonment share unavailable");
                    return NewBriefing(advisor, L("Desenvolvimento sem alerta", "Development: no alerts"), L("Os indicadores urbanos avaliados estão abaixo dos limites de alerta.", "The urban indicators evaluated are below the alert limits."),
                        unemployment + abandonment, L("Acompanhe a ocupação e as demandas de zoneamento.", "Track occupancy and zoning demands."),
                        AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Zoning, 0);
                case AdvisorId.EducationHealth:
                    if (!snapshot.ElementaryEducationAvailable && !snapshot.HighSchoolEducationAvailable &&
                        !snapshot.UniversityEducationAvailable && !snapshot.HealthServiceAvailable)
                        return NewBriefing(advisor, L("Serviços ainda não liberados", "Services not yet unlocked"),
                            L("A população ainda não tem os marcos necessários para avaliar escolas ou saúde.", "The milestones needed for schools and healthcare have not been reached."),
                            L("Educação e saúde: avaliação suspensa", "Education and health: evaluation suspended"), L("A assessoria começa a cobrar esses serviços quando o jogo os liberar.", "The advisor will assess these services once they are unlocked."),
                            AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                    return NewBriefing(advisor, L("Capacidade de serviços sem alerta", "Service capacity: no alerts"), L("Não há déficit de capacidade escolar ou de saúde nos dados recebidos.", "There is no deficit in school or health capacity in the data received."),
                        L("Escolas: demanda e capacidade informadas; saúde: ", "Schools: reported demand and capacity; healthcare: ") + snapshot.SickCitizens + L(" doentes / ", " sick / ") + snapshot.HealthCapacity + L(" de capacidade", " of capacity"),
                        L("Acompanhe a capacidade conforme a demanda da população.", "Monitor capacity according to population demand."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.Utilities:
                    return NewBriefing(advisor, L("Infraestrutura sem alerta", "Infrastructure: no alerts"), L("O uso não excede a capacidade informada de eletricidade, água ou esgoto.", "Usage does not exceed reported electricity, water or sewer capacity."),
                        L("Eletricidade ", "Electricity ") + snapshot.ElectricityUse + "/" + snapshot.ElectricityCapacity + L("; água ", "; water ") + snapshot.WaterUse + "/" + snapshot.WaterCapacity + L("; esgoto ", "; sewage ") + snapshot.SewageUse + "/" + snapshot.SewageCapacity,
                        L("Acompanhe o uso e a capacidade dos serviços.", "Track service usage and capacity."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.PublicSafety:
                    return NewBriefing(advisor, L("Segurança sem alerta", "Public safety: no alerts"), L("A criminalidade está abaixo do limite de alerta do gabinete.", "Crime is below the cabinet's alert threshold."),
                        L("Criminalidade: ", "Crime: ") + snapshot.CrimePercent + "%", L("Continue acompanhando os indicadores de segurança.", "Continue monitoring safety indicators."),
                        AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Crime, 0);
                case AdvisorId.Environment:
                    string garbage = snapshot.GarbageCapacity > 0
                        ? snapshot.GarbageStored + "/" + snapshot.GarbageCapacity + L(" unidades de lixo", " garbage units")
                        : snapshot.GarbageStored + L(" unidades de lixo; capacidade indisponível", " garbage units; unavailable capacity");
                    return NewBriefing(advisor, L("Ambiente sem alerta", "Environment: no alerts"), L("Poluição e armazenamento de lixo estão abaixo dos limites de alerta.", "Pollution and waste storage are below alert limits."),
                        L("Poluição: ", "Pollution: ") + snapshot.PollutionPercent + "%; " + garbage,
                        L("Continue acompanhando os indicadores ambientais.", "Continue monitoring environmental indicators."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.Transport:
                    if (snapshot.TrafficFlowPercent >= 0 && snapshot.TrafficFlowPercent <= 100)
                        return NewBriefing(advisor, L("Trânsito sem alerta", "Traffic: no alerts"), L("O fluxo de trânsito está acima dos limites de alerta.", "Traffic flow is above alert limits."),
                            L("Fluxo: ", "Flow: ") + snapshot.TrafficFlowPercent + "%", L("Continue acompanhando os corredores da cidade.", "Continue following the city's corridors."),
                            AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Traffic, 0);
                    return NewBriefing(advisor, L("Fluxo de trânsito indisponível", "Traffic flow unavailable"), L("O jogo não forneceu um fluxo de trânsito válido neste levantamento.", "The game did not provide a valid traffic flow in this survey."),
                        L("Fluxo: indisponível", "Flow: unavailable"), L("Nenhum alerta de trânsito foi avaliado sem uma medição válida.", "No traffic alert was evaluated without a valid measurement."),
                        AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Traffic, 0);
                default:
                    return InvalidBriefing(advisor);
            }
        }

        private static AdvisorBriefing EmptyCityBriefing(AdvisorId advisor)
        {
            switch (advisor)
            {
                case AdvisorId.Finance:
                    return NewBriefing(advisor, L("Preparação financeira", "Financial preparation"), L("Antes da expansão, acompanhe o caixa e o saldo orçamentário da cidade.", "Before expansion, monitor the city's cash and budget balance."),
                        L("Sem moradores: alertas financeiros suspensos", "No residents: financial alerts suspended"), L("Use os valores reais do orçamento ao planejar os próximos serviços.", "Use actual budget amounts when planning upcoming services."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Budget, 0);
                case AdvisorId.UrbanPlanning:
                    return NewBriefing(advisor, L("Primeiros bairros", "First neighborhoods"), L("Planeje zonas residenciais, comerciais e de trabalho conforme a demanda surgir.", "Plan residential, commercial and work zones as demand arises."),
                        L("Sem moradores: alertas de ocupação suspensos", "No residents: occupancy alerts suspended"), L("A participação de abandono depende de edifícios zoneados.", "Abandonment share depends on zoned buildings."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Zoning, 0);
                case AdvisorId.EducationHealth:
                    return NewBriefing(advisor, L("Serviços para o crescimento", "Services for growth"), L("Planeje escolas e atendimento de saúde conforme a população crescer.", "Plan for schools and health care as the population grows."),
                        L("Sem moradores: alertas de capacidade suspensos", "No residents: capacity alerts suspended"), L("A necessidade de serviços será avaliada a partir dos dados da cidade.", "The need for services will be assessed based on city data."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.Utilities:
                    return NewBriefing(advisor, L("Infraestrutura inicial", "Initial infrastructure"), L("Acompanhe o uso e a capacidade de eletricidade, água e esgoto durante o crescimento.", "Track electricity, water, and sewer usage and capacity as you grow."),
                        L("Sem moradores: alertas de capacidade suspensos", "No residents: capacity alerts suspended"), L("Não há consumo informado que justifique um alerta de serviço.", "There is no reported consumption that justifies a service alert."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.PublicSafety:
                    return NewBriefing(advisor, L("Segurança da cidade", "City security"), L("Acompanhe os indicadores de criminalidade quando houver moradores.", "Monitor crime indicators when there are residents."),
                        L("Sem moradores: alertas de segurança suspensos", "No residents: security alerts suspended"), L("Nenhuma crise de segurança foi inferida.", "No security crisis was inferred."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Crime, 0);
                case AdvisorId.Environment:
                    return NewBriefing(advisor, L("Ambiente da cidade", "City environment"), L("Acompanhe poluição e armazenamento de lixo durante o crescimento.", "Track pollution and waste storage during growth."),
                        L("Sem moradores: alertas ambientais suspensos", "No residents: environmental alerts suspended"), L("Capacidade zero e armazenamento vazio não representam uma crise.", "Zero capacity and empty storage do not represent a crisis."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.Transport:
                    return NewBriefing(advisor, L("Mobilidade inicial", "Initial mobility"), L("Acompanhe o fluxo quando houver deslocamentos na cidade.", "Follow the flow when there are commutes in the city."),
                        L("Sem moradores: alertas de trânsito suspensos", "No residents: traffic alerts suspended"), L("Medições ausentes não são tratadas como fluxo zero.", "Missing measurements are not treated as zero flow."), AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Traffic, 0);
                default:
                    return InvalidBriefing(advisor);
            }
        }

        private static AdvisorBriefing NewBriefing(AdvisorId advisor, string title, string message, string metric,
            string detail, AdvisorMood mood, IssueSeverity severity, AdvisorAction action, int concernDays)
        {
            return new AdvisorBriefing
            {
                Advisor = advisor,
                Name = AdvisorName(advisor),
                Department = DepartmentName(advisor),
                Title = title,
                Message = message,
                MetricText = metric,
                Detail = detail,
                Mood = mood,
                Severity = severity,
                Action = action,
                ConcernDays = concernDays
            };
        }

        private static string AdvisorName(AdvisorId advisor)
        {
            switch (advisor)
            {
                case AdvisorId.Finance: return "Betty D. Fato";
                case AdvisorId.UrbanPlanning: return "Ney Einstein";
                case AdvisorId.EducationHealth: return "Dinalva Gaze";
                case AdvisorId.Utilities: return "Jonas Pronto";
                case AdvisorId.PublicSafety: return "Samuel Fogaça";
                case AdvisorId.Environment: return "Camila Camomila";
                case AdvisorId.Transport: return "Roberto Carros";
                default: return "Assessoria";
            }
        }

        private static string DepartmentName(AdvisorId advisor)
        {
            switch (advisor)
            {
                case AdvisorId.Finance: return L("Finanças", "Finance");
                case AdvisorId.UrbanPlanning: return L("Planejamento urbano", "Urban planning");
                case AdvisorId.EducationHealth: return L("Educação e saúde", "Education and health");
                case AdvisorId.Utilities: return L("Serviços públicos", "Public services");
                case AdvisorId.PublicSafety: return L("Segurança pública", "Public safety");
                case AdvisorId.Environment: return L("Meio ambiente", "Environment");
                case AdvisorId.Transport: return L("Transportes", "Transport");
                default: return L("Gabinete", "Cabinet");
            }
        }

        private static string IssueSubject(string issueId)
        {
            switch (issueId)
            {
                case "finance.cash-negative": return L("o caixa municipal", "the city treasury");
                case "finance.weekly-deficit": return L("o déficit orçamentário", "the budget deficit");
                case "urban.unemployment": return L("o desemprego", "unemployment");
                case "urban.abandonment": return L("o abandono de edifícios", "the abandonment of buildings");
                case "education.elementary": return L("as vagas no ensino fundamental", "vacancies in primary education");
                case "education.high-school": return L("as vagas no ensino médio", "vacancies in secondary education");
                case "education.university": return L("as vagas no ensino superior", "vacancies in higher education");
                case "health.capacity": return L("a capacidade de saúde", "the health capacity");
                case "utilities.electricity": return L("a capacidade de eletricidade", "the electricity capacity");
                case "utilities.water": return L("a capacidade de água", "the water capacity");
                case "utilities.sewage": return L("a capacidade de esgoto", "the sewage capacity");
                case "safety.crime": return L("a criminalidade", "crime");
                case "environment.pollution": return L("a poluição", "the pollution");
                case "environment.garbage": return L("o armazenamento de lixo", "garbage storage");
                case "transport.traffic": return L("o fluxo de trânsito", "the traffic flow");
                default: return L("o indicador municipal", "the municipal indicator");
            }
        }

        private static string RecoveryText(string issueId)
        {
            switch (issueId)
            {
                case "finance.cash-negative": return L("O caixa municipal voltou a ficar em zero ou acima.", "The city treasury is back to zero or above.");
                case "finance.weekly-deficit": return L("O saldo orçamentário informado deixou de indicar déficit.", "The reported budget balance no longer indicates a deficit.");
                case "urban.unemployment": return L("O desemprego voltou para abaixo do limite de alerta.", "Unemployment returned below the alert threshold.");
                case "urban.abandonment": return L("A participação de edifícios abandonados voltou para abaixo do limite de alerta.", "The share of abandoned buildings has returned to below the alert threshold.");
                case "education.elementary": return L("A capacidade informada voltou a atender a demanda do ensino fundamental.", "The reported capacity once again met the demand for elementary education.");
                case "education.high-school": return L("A capacidade informada voltou a atender a demanda do ensino médio.", "The reported capacity once again met secondary education demand.");
                case "education.university": return L("A capacidade informada voltou a atender a demanda do ensino superior.", "The reported capacity once again met the demand for higher education.");
                case "health.capacity": return L("A capacidade de saúde informada voltou a atender a necessidade.", "Reported healthcare capacity now meets demand.");
                case "utilities.electricity": return L("A capacidade de eletricidade informada voltou a atender o uso.", "The reported electricity capacity returned to meet usage.");
                case "utilities.water": return L("A capacidade de água informada voltou a atender o uso.", "The reported water capacity returned to meet usage.");
                case "utilities.sewage": return L("A capacidade de esgoto informada voltou a atender o uso.", "The reported sewage capacity returned to meet usage.");
                case "safety.crime": return L("A criminalidade voltou para abaixo do limite de alerta.", "Crime fell below the alert threshold.");
                case "environment.pollution": return L("A poluição voltou para abaixo do limite de alerta.", "Pollution has returned to below the alert limit.");
                case "environment.garbage": return L("O armazenamento de lixo voltou para abaixo do limite de alerta.", "Garbage storage has returned below the alert threshold.");
                case "transport.traffic": return L("O fluxo de trânsito voltou a ficar em 60% ou mais.", "Traffic flow is back to 60% or more.");
                default: return L("O indicador voltou para abaixo do limite de alerta.", "The indicator has returned below the alert threshold.");
            }
        }

        private static string SeverityName(IssueSeverity severity)
        {
            return severity == IssueSeverity.Critical ? L("crítica", "critical") : L("de atenção", "warning");
        }

        private static int DaysSince(DateTime start, DateTime current)
        {
            if (current.Ticks <= start.Ticks)
                return 0;
            long days = (current.Ticks - start.Ticks) / TimeSpan.TicksPerDay;
            return days > int.MaxValue ? int.MaxValue : (int)days;
        }

        private static string Currency(long amount)
        {
            return string.Format(_english ? CultureInfo.GetCultureInfo("en-US") : Portuguese, _english ? "₡ {0:N0}" : "R$ {0:N0}", amount);
        }

        private static string Number(double value, string format)
        {
            return value.ToString(format, _english ? CultureInfo.GetCultureInfo("en-US") : Portuguese);
        }

        private static long NonNegative(int value)
        {
            return value < 0 ? 0L : (long)value;
        }

        private static ulong UnsignedMagnitude(long value)
        {
            return (ulong)(-(value + 1L)) + 1UL;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            if (value == null)
                throw new InvalidOperationException("Advisor state cannot serialize a null string.");
            byte[] bytes = StrictUtf8.GetBytes(value);
            if (bytes.Length > MaxStringBytes)
                throw new InvalidOperationException("Advisor state string exceeded its serialization bound.");
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            if (length > MaxStringBytes)
                throw new InvalidDataException("Advisor state string exceeded its bound.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new InvalidDataException("Advisor state string is truncated.");
            return StrictUtf8.GetString(bytes);
        }

        private static void ParsePayload(byte[] payload, out long sequence, out List<CityNewsItem> news, out IssueState[] states)
        {
            MemoryStream stream = new MemoryStream(payload, false);
            BinaryReader reader = new BinaryReader(stream, Encoding.UTF8);
            sequence = reader.ReadInt64();
            if (sequence < 0L)
                throw new InvalidDataException("Advisor state sequence is invalid.");

            int newsCount = reader.ReadByte();
            if (newsCount > MaxNewsItems)
                throw new InvalidDataException("Advisor state news history exceeded its bound.");
            news = new List<CityNewsItem>(newsCount);
            int index;
            for (index = 0; index < newsCount; index++)
            {
                DateTime date = ReadDate(reader);
                string title = ReadString(reader);
                string summary = ReadString(reader);
                IssueSeverity severity = ReadSeverity(reader, true);
                if (title.Length == 0 || summary.Length == 0)
                    throw new InvalidDataException("Advisor state contains empty news text.");
                news.Add(new CityNewsItem { Date = date, Title = title, Summary = summary, Severity = severity });
            }

            int stateIssueCount = reader.ReadByte();
            if (stateIssueCount != IssueCount)
                throw new InvalidDataException("Advisor state issue registry is invalid.");
            states = new IssueState[IssueCount];
            for (index = 0; index < IssueCount; index++)
            {
                byte hasIssue = reader.ReadByte();
                if (hasIssue > 1)
                    throw new InvalidDataException("Advisor state issue flag is invalid.");
                if (hasIssue == 1)
                {
                    DateTime startDate = ReadDate(reader);
                    IssueSeverity severity = ReadSeverity(reader, false);
                    states[index] = new IssueState(startDate, severity);
                }
            }

            if (stream.Position != stream.Length)
                throw new InvalidDataException("Advisor state payload has trailing data.");
            reader.Close();
            stream.Close();
        }

        private static DateTime ReadDate(BinaryReader reader)
        {
            long ticks = reader.ReadInt64();
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("Advisor state date is invalid.");
            DateTime date = new DateTime(ticks);
            if (date.TimeOfDay.Ticks != 0L)
                throw new InvalidDataException("Advisor state date must be a simulation calendar day.");
            return date;
        }

        private static IssueSeverity ReadSeverity(BinaryReader reader, bool allowNone)
        {
            byte value = reader.ReadByte();
            if (value > (byte)IssueSeverity.Critical || (!allowNone && value < (byte)IssueSeverity.Warning))
                throw new InvalidDataException("Advisor state severity is invalid.");
            return (IssueSeverity)value;
        }

        private static int IssueIndex(string issueId)
        {
            int index;
            for (index = 0; index < IssueCount; index++)
            {
                if (string.Equals(IssueIds[index], issueId, StringComparison.Ordinal))
                    return index;
            }
            return -1;
        }

        private static uint Crc32(byte[] bytes)
        {
            uint crc = 0xFFFFFFFFU;
            int index;
            for (index = 0; index < bytes.Length; index++)
            {
                crc ^= bytes[index];
                int bit;
                for (bit = 0; bit < 8; bit++)
                    crc = (crc & 1U) == 1U ? (crc >> 1) ^ 0xEDB88320U : crc >> 1;
            }
            return ~crc;
        }

        private sealed class IssueState
        {
            public IssueState(DateTime startDate, IssueSeverity severity)
            {
                StartDate = startDate.Date;
                Severity = severity;
            }

            public DateTime StartDate;
            public IssueSeverity Severity;
        }

        private sealed class Signal
        {
            public Signal(string issueId, IssueSeverity severity, string title, string message,
                string metricText, string detail, AdvisorAction action)
            {
                IssueId = issueId;
                Severity = severity;
                Title = title;
                Message = message;
                MetricText = metricText;
                Detail = detail;
                Action = action;
            }

            public readonly string IssueId;
            public readonly IssueSeverity Severity;
            public readonly string Title;
            public readonly string Message;
            public readonly string MetricText;
            public readonly string Detail;
            public readonly AdvisorAction Action;
        }
    }
}
