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
                                AddNews(date, "Alerta: " + signal.Title, signal.Message + " " + signal.Detail, signal.Severity);
                            }
                            else
                            {
                                if (previous.Severity != signal.Severity)
                                {
                                    IssueSeverity oldSeverity = previous.Severity;
                                    previous.Severity = signal.Severity;
                                    AddNews(date, "Alerta atualizado: " + signal.Title,
                                        "A gravidade de " + IssueSubject(IssueIds[issueIndex]) + " mudou de " + SeverityName(oldSeverity) + " para " + SeverityName(signal.Severity) + ". " + signal.Message,
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
            AddNews(date, "Recuperação: " + subject,
                RecoveryText(issueId) + " A assessoria encerrou este alerta.", IssueSeverity.None);
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
                    SchoolSignal(snapshot.ElementaryNeed, snapshot.ElementaryCapacity, "ensino fundamental", "education.elementary"));
            if (snapshot.HighSchoolEducationAvailable)
                AddSignal(activeByIssue, prioritySignals, AdvisorId.EducationHealth,
                    SchoolSignal(snapshot.HighSchoolNeed, snapshot.HighSchoolCapacity, "ensino médio", "education.high-school"));
            if (snapshot.UniversityEducationAvailable)
                AddSignal(activeByIssue, prioritySignals, AdvisorId.EducationHealth,
                    SchoolSignal(snapshot.UniversityNeed, snapshot.UniversityCapacity, "ensino superior", "education.university"));

            AddSignal(activeByIssue, prioritySignals, AdvisorId.Utilities,
                UtilitySignal(snapshot.ElectricityUse, snapshot.ElectricityCapacity, "eletricidade", "utilities.electricity", AdvisorAction.Electricity));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Utilities,
                UtilitySignal(snapshot.WaterUse, snapshot.WaterCapacity, "água", "utilities.water", AdvisorAction.Water));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Utilities,
                UtilitySignal(snapshot.SewageUse, snapshot.SewageCapacity, "esgoto", "utilities.sewage", AdvisorAction.Water));

            AddSignal(activeByIssue, prioritySignals, AdvisorId.PublicSafety, SafetySignal(snapshot));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Environment, PollutionSignal(snapshot));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Environment, GarbageSignal(snapshot));
            AddSignal(activeByIssue, prioritySignals, AdvisorId.Transport, TransportSignal(snapshot));
            return activeByIssue;
        }

        private static Signal CashSignal(CitySnapshot snapshot)
        {
            if (snapshot.Cash < 0L)
                return new Signal("finance.cash-negative", IssueSeverity.Critical, "Caixa municipal negativo",
                    "O caixa municipal está negativo (" + Currency(snapshot.Cash) + ").",
                    "Caixa: " + Currency(snapshot.Cash),
                    "Revise o orçamento e acompanhe receitas e despesas antes de assumir novos compromissos.", AdvisorAction.Budget);
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
                ? "A reserva financeira é pequena diante do déficit informado. Revise o orçamento e acompanhe receitas e despesas."
                : "Acompanhe receitas e despesas para reverter o saldo orçamentário negativo.";
            string message = "O saldo orçamentário informado é negativo (" + Currency(snapshot.WeeklyBalance) + ").";
            if (lessThanFourWeeks)
                message += " A reserva financeira está abaixo do limite de referência para esse déficit.";
            return new Signal("finance.weekly-deficit", severity, "Déficit orçamentário",
                message, "Saldo informado: " + Currency(snapshot.WeeklyBalance) + "; caixa: " + Currency(snapshot.Cash), detail, AdvisorAction.Budget);
        }

        private static Signal UnemploymentSignal(CitySnapshot snapshot)
        {
            if (snapshot.Population < 50 || snapshot.UnemploymentPercent < 12)
                return null;
            IssueSeverity severity = snapshot.UnemploymentPercent >= 25 ? IssueSeverity.Critical : IssueSeverity.Warning;
            return new Signal("urban.unemployment", severity,
                severity == IssueSeverity.Critical ? "Desemprego elevado" : "Desemprego em alerta",
                "O desemprego chegou a " + snapshot.UnemploymentPercent + "%.",
                "Desemprego: " + snapshot.UnemploymentPercent + "%; população: " + snapshot.Population,
                "Acompanhe a demanda por empregos e revise a distribuição do zoneamento.", AdvisorAction.Zoning);
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
                severity == IssueSeverity.Critical ? "Abandono de edifícios elevado" : "Edifícios abandonados em alerta",
                "Há " + abandoned + " edifícios abandonados entre " + denominator + " zoneados (" + Number(share, "0.0") + "%).",
                "Abandonados: " + abandoned + "; zoneados: " + denominator + "; participação: " + Number(share, "0.0") + "%",
                "Revise o zoneamento e os indicadores locais para identificar obstáculos à ocupação.", AdvisorAction.Zoning);
        }

        private static Signal SchoolSignal(int need, int capacity, string school, string issueId)
        {
            if (need <= 0 || need <= capacity)
                return null;
            bool critical = (long)capacity * 100L < (long)need * 75L;
            IssueSeverity severity = critical ? IssueSeverity.Critical : IssueSeverity.Warning;
            return new Signal(issueId, severity,
                critical ? "Capacidade escolar muito abaixo da demanda" : "Faltam vagas na rede escolar",
                "A demanda por " + school + " é de " + need + " vagas; a capacidade informada é " + capacity + ".",
                school + ": demanda " + need + ", capacidade " + capacity,
                "Acompanhe a demanda escolar e planeje capacidade compatível com as necessidades informadas.", AdvisorAction.Education);
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
                critical ? "Atendimento de saúde muito abaixo da necessidade" : "Atendimento de saúde em alerta",
                "Há " + sick + " moradores doentes e capacidade informada para " + capacity + ".",
                "Moradores doentes: " + sick + "; capacidade de saúde: " + capacity,
                "Acompanhe a necessidade de atendimento e planeje capacidade de saúde compatível.", AdvisorAction.Health);
        }

        private static Signal UtilitySignal(int use, int capacity, string service, string issueId, AdvisorAction action)
        {
            if (use <= 0 || use <= capacity)
                return null;
            bool critical = (long)capacity * 100L < (long)use * 80L;
            IssueSeverity severity = critical ? IssueSeverity.Critical : IssueSeverity.Warning;
            return new Signal(issueId, severity,
                critical ? "Capacidade de " + service + " muito abaixo do uso" : "Capacidade de " + service + " em alerta",
                "O uso de " + service + " é " + use + ", acima da capacidade informada de " + capacity + ".",
                "Uso de " + service + ": " + use + "; capacidade: " + capacity,
                "Acompanhe o uso e planeje capacidade compatível com a demanda informada.", action);
        }

        private static Signal PollutionSignal(CitySnapshot snapshot)
        {
            if (snapshot.PollutionPercent < 20)
                return null;
            IssueSeverity severity = snapshot.PollutionPercent >= 40 ? IssueSeverity.Critical : IssueSeverity.Warning;
            return new Signal("environment.pollution", severity,
                severity == IssueSeverity.Critical ? "Poluição elevada" : "Poluição em alerta",
                "A poluição chegou a " + snapshot.PollutionPercent + "%.",
                "Poluição: " + snapshot.PollutionPercent + "%",
                "Acompanhe os indicadores ambientais e inspecione as áreas com maior poluição.", AdvisorAction.Pollution);
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

            string capacityText = capacity == 0 ? "sem capacidade informada" : "capacidade de " + capacity;
            string percentageText = capacity > 0 ? " (" + Number((double)stored * 100.0 / (double)capacity, "0.0") + "%)" : string.Empty;
            return new Signal("environment.garbage", severity,
                severity == IssueSeverity.Critical ? "Armazenamento de lixo no limite" : "Armazenamento de lixo em alerta",
                "Há " + stored + " unidades de lixo armazenadas, com " + capacityText + percentageText + ".",
                "Lixo armazenado: " + stored + "; capacidade: " + capacity,
                "Acompanhe o armazenamento e planeje o escoamento do lixo antes de atingir o limite.", AdvisorAction.Garbage);
        }

        private static Signal SafetySignal(CitySnapshot snapshot)
        {
            if (snapshot.CrimePercent >= 25)
                return new Signal("safety.crime", IssueSeverity.Critical, "Criminalidade elevada",
                    "A criminalidade chegou a " + snapshot.CrimePercent + "%.", "Criminalidade: " + snapshot.CrimePercent + "%",
                    "Revise os indicadores de segurança e a cobertura dos serviços policiais.", AdvisorAction.Crime);
            if (snapshot.CrimePercent >= 10)
                return new Signal("safety.crime", IssueSeverity.Warning, "Criminalidade em alerta",
                    "A criminalidade chegou a " + snapshot.CrimePercent + "%.", "Criminalidade: " + snapshot.CrimePercent + "%",
                    "Acompanhe a segurança e inspecione as áreas com maior necessidade.", AdvisorAction.Crime);
            return null;
        }

        private static Signal TransportSignal(CitySnapshot snapshot)
        {
            if (snapshot.TrafficFlowPercent >= 0 && snapshot.TrafficFlowPercent <= 100 && snapshot.TrafficFlowPercent < 35)
                return new Signal("transport.traffic", IssueSeverity.Critical, "Trânsito muito congestionado",
                    "O fluxo de trânsito está em " + snapshot.TrafficFlowPercent + "%.", "Fluxo: " + snapshot.TrafficFlowPercent + "%",
                    "Inspecione os pontos de congestionamento e as rotas usadas pelos moradores.", AdvisorAction.Traffic);
            if (snapshot.TrafficFlowPercent >= 0 && snapshot.TrafficFlowPercent <= 100 && snapshot.TrafficFlowPercent < 60)
                return new Signal("transport.traffic", IssueSeverity.Warning, "Fluxo de trânsito baixo",
                    "O fluxo de trânsito está em " + snapshot.TrafficFlowPercent + "%.", "Fluxo: " + snapshot.TrafficFlowPercent + "%",
                    "Acompanhe os corredores mais carregados e as alternativas de deslocamento.", AdvisorAction.Traffic);
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
            return NewBriefing(advisor, "Aguardando dados da cidade", "Os dados municipais ainda não estão disponíveis; nenhum alerta foi avaliado.",
                "Dados indisponíveis", "Esta assessoria será atualizada quando os dados da cidade estiverem disponíveis.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
        }

        private static AdvisorBriefing UnavailableBriefing(AdvisorId advisor, string issueId)
        {
            AdvisorBriefing briefing;
            if (advisor == AdvisorId.Transport)
                briefing = NewBriefing(advisor, "Fluxo de trânsito indisponível",
                    "O jogo não forneceu um fluxo de trânsito válido neste levantamento; não há base para confirmar um alerta.",
                    "Fluxo: indisponível", "O histórico do alerta fica preservado até que o indicador possa ser avaliado novamente.",
                    AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Traffic, 0);
            else if (string.Equals(issueId, "urban.unemployment", StringComparison.Ordinal))
                briefing = NewBriefing(advisor, "Avaliação de desemprego suspensa",
                    "A população está abaixo do mínimo usado para avaliar o desemprego; o histórico do alerta foi preservado.",
                    "Desemprego: não avaliado", "A assessoria retomará a avaliação quando a população atingir o mínimo definido.",
                    AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Zoning, 0);
            else briefing = NewBriefing(advisor, "Indicador de abandono indisponível",
                "Não há edifícios zoneados suficientes para calcular a participação de abandono.",
                "Abandonados: indisponível", "Um denominador zero não representa abandono total.",
                AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Zoning, 0);
            briefing.IsSuspended = true;
            return briefing;
        }

        private static AdvisorBriefing RecoveryBriefing(AdvisorId advisor, string issueId)
        {
            return NewBriefing(advisor, "Situação normalizada", RecoveryText(issueId),
                "Sem alerta ativo", "A assessoria reconhece a recuperação uma vez e encerra este episódio.",
                AdvisorMood.Pleased, IssueSeverity.None, AdvisorAction.Overview, 0);
        }

        private static AdvisorBriefing CalmBriefing(CitySnapshot snapshot, AdvisorId advisor)
        {
            if (snapshot.Population <= 0)
                return EmptyCityBriefing(advisor);

            switch (advisor)
            {
                case AdvisorId.Finance:
                    return NewBriefing(advisor, "Contas sem alerta", "O caixa não está negativo e o saldo orçamentário informado não indica déficit.",
                        "Caixa: " + Currency(snapshot.Cash) + "; saldo informado: " + Currency(snapshot.WeeklyBalance),
                        "Continue acompanhando as contas municipais.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Budget, 0);
                case AdvisorId.UrbanPlanning:
                    string unemployment = snapshot.Population >= 50
                        ? "Desemprego: " + snapshot.UnemploymentPercent + "%"
                        : "População abaixo do mínimo para avaliar desemprego";
                    long abandoned = NonNegative(snapshot.ResidentialAbandoned) + NonNegative(snapshot.CommercialAbandoned) +
                        NonNegative(snapshot.IndustrialAbandoned) + NonNegative(snapshot.OfficeAbandoned);
                    string abandonment = snapshot.ZonedBuildings > 0
                        ? "; abandonados: " + abandoned + "/" + snapshot.ZonedBuildings
                        : "; participação de abandono indisponível";
                    return NewBriefing(advisor, "Desenvolvimento sem alerta", "Os indicadores urbanos avaliados estão abaixo dos limites de alerta.",
                        unemployment + abandonment, "Acompanhe a ocupação e as demandas de zoneamento.",
                        AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Zoning, 0);
                case AdvisorId.EducationHealth:
                    if (!snapshot.ElementaryEducationAvailable && !snapshot.HighSchoolEducationAvailable &&
                        !snapshot.UniversityEducationAvailable && !snapshot.HealthServiceAvailable)
                        return NewBriefing(advisor, "Serviços ainda não liberados",
                            "A população ainda não tem os marcos necessários para avaliar escolas ou saúde.",
                            "Educação e saúde: avaliação suspensa", "A assessoria começa a cobrar esses serviços quando o jogo os liberar.",
                            AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                    return NewBriefing(advisor, "Capacidade de serviços sem alerta", "Não há déficit de capacidade escolar ou de saúde nos dados recebidos.",
                        "Escolas: demanda e capacidade informadas; saúde: " + snapshot.SickCitizens + " doentes / " + snapshot.HealthCapacity + " de capacidade",
                        "Acompanhe a capacidade conforme a demanda da população.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.Utilities:
                    return NewBriefing(advisor, "Infraestrutura sem alerta", "O uso não excede a capacidade informada de eletricidade, água ou esgoto.",
                        "Eletricidade " + snapshot.ElectricityUse + "/" + snapshot.ElectricityCapacity + "; água " + snapshot.WaterUse + "/" + snapshot.WaterCapacity + "; esgoto " + snapshot.SewageUse + "/" + snapshot.SewageCapacity,
                        "Acompanhe o uso e a capacidade dos serviços.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.PublicSafety:
                    return NewBriefing(advisor, "Segurança sem alerta", "A criminalidade está abaixo do limite de alerta do gabinete.",
                        "Criminalidade: " + snapshot.CrimePercent + "%", "Continue acompanhando os indicadores de segurança.",
                        AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Crime, 0);
                case AdvisorId.Environment:
                    string garbage = snapshot.GarbageCapacity > 0
                        ? snapshot.GarbageStored + "/" + snapshot.GarbageCapacity + " unidades de lixo"
                        : snapshot.GarbageStored + " unidades de lixo; capacidade indisponível";
                    return NewBriefing(advisor, "Ambiente sem alerta", "Poluição e armazenamento de lixo estão abaixo dos limites de alerta.",
                        "Poluição: " + snapshot.PollutionPercent + "%; " + garbage,
                        "Continue acompanhando os indicadores ambientais.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.Transport:
                    if (snapshot.TrafficFlowPercent >= 0 && snapshot.TrafficFlowPercent <= 100)
                        return NewBriefing(advisor, "Trânsito sem alerta", "O fluxo de trânsito está acima dos limites de alerta.",
                            "Fluxo: " + snapshot.TrafficFlowPercent + "%", "Continue acompanhando os corredores da cidade.",
                            AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Traffic, 0);
                    return NewBriefing(advisor, "Fluxo de trânsito indisponível", "O jogo não forneceu um fluxo de trânsito válido neste levantamento.",
                        "Fluxo: indisponível", "Nenhum alerta de trânsito foi avaliado sem uma medição válida.",
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
                    return NewBriefing(advisor, "Preparação financeira", "Antes da expansão, acompanhe o caixa e o saldo orçamentário da cidade.",
                        "Sem moradores: alertas financeiros suspensos", "Use os valores reais do orçamento ao planejar os próximos serviços.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Budget, 0);
                case AdvisorId.UrbanPlanning:
                    return NewBriefing(advisor, "Primeiros bairros", "Planeje zonas residenciais, comerciais e de trabalho conforme a demanda surgir.",
                        "Sem moradores: alertas de ocupação suspensos", "A participação de abandono depende de edifícios zoneados.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Zoning, 0);
                case AdvisorId.EducationHealth:
                    return NewBriefing(advisor, "Serviços para o crescimento", "Planeje escolas e atendimento de saúde conforme a população crescer.",
                        "Sem moradores: alertas de capacidade suspensos", "A necessidade de serviços será avaliada a partir dos dados da cidade.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.Utilities:
                    return NewBriefing(advisor, "Infraestrutura inicial", "Acompanhe o uso e a capacidade de eletricidade, água e esgoto durante o crescimento.",
                        "Sem moradores: alertas de capacidade suspensos", "Não há consumo informado que justifique um alerta de serviço.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.PublicSafety:
                    return NewBriefing(advisor, "Segurança da cidade", "Acompanhe os indicadores de criminalidade quando houver moradores.",
                        "Sem moradores: alertas de segurança suspensos", "Nenhuma crise de segurança foi inferida.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Crime, 0);
                case AdvisorId.Environment:
                    return NewBriefing(advisor, "Ambiente da cidade", "Acompanhe poluição e armazenamento de lixo durante o crescimento.",
                        "Sem moradores: alertas ambientais suspensos", "Capacidade zero e armazenamento vazio não representam uma crise.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Overview, 0);
                case AdvisorId.Transport:
                    return NewBriefing(advisor, "Mobilidade inicial", "Acompanhe o fluxo quando houver deslocamentos na cidade.",
                        "Sem moradores: alertas de trânsito suspensos", "Medições ausentes não são tratadas como fluxo zero.", AdvisorMood.Calm, IssueSeverity.None, AdvisorAction.Traffic, 0);
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
                case AdvisorId.Finance: return "Finanças";
                case AdvisorId.UrbanPlanning: return "Planejamento urbano";
                case AdvisorId.EducationHealth: return "Educação e saúde";
                case AdvisorId.Utilities: return "Serviços públicos";
                case AdvisorId.PublicSafety: return "Segurança pública";
                case AdvisorId.Environment: return "Meio ambiente";
                case AdvisorId.Transport: return "Transportes";
                default: return "Gabinete";
            }
        }

        private static string IssueSubject(string issueId)
        {
            switch (issueId)
            {
                case "finance.cash-negative": return "o caixa municipal";
                case "finance.weekly-deficit": return "o déficit orçamentário";
                case "urban.unemployment": return "o desemprego";
                case "urban.abandonment": return "o abandono de edifícios";
                case "education.elementary": return "as vagas no ensino fundamental";
                case "education.high-school": return "as vagas no ensino médio";
                case "education.university": return "as vagas no ensino superior";
                case "health.capacity": return "a capacidade de saúde";
                case "utilities.electricity": return "a capacidade de eletricidade";
                case "utilities.water": return "a capacidade de água";
                case "utilities.sewage": return "a capacidade de esgoto";
                case "safety.crime": return "a criminalidade";
                case "environment.pollution": return "a poluição";
                case "environment.garbage": return "o armazenamento de lixo";
                case "transport.traffic": return "o fluxo de trânsito";
                default: return "o indicador municipal";
            }
        }

        private static string RecoveryText(string issueId)
        {
            switch (issueId)
            {
                case "finance.cash-negative": return "O caixa municipal voltou a ficar em zero ou acima.";
                case "finance.weekly-deficit": return "O saldo orçamentário informado deixou de indicar déficit.";
                case "urban.unemployment": return "O desemprego voltou para abaixo do limite de alerta.";
                case "urban.abandonment": return "A participação de edifícios abandonados voltou para abaixo do limite de alerta.";
                case "education.elementary": return "A capacidade informada voltou a atender a demanda do ensino fundamental.";
                case "education.high-school": return "A capacidade informada voltou a atender a demanda do ensino médio.";
                case "education.university": return "A capacidade informada voltou a atender a demanda do ensino superior.";
                case "health.capacity": return "A capacidade de saúde informada voltou a atender a necessidade.";
                case "utilities.electricity": return "A capacidade de eletricidade informada voltou a atender o uso.";
                case "utilities.water": return "A capacidade de água informada voltou a atender o uso.";
                case "utilities.sewage": return "A capacidade de esgoto informada voltou a atender o uso.";
                case "safety.crime": return "A criminalidade voltou para abaixo do limite de alerta.";
                case "environment.pollution": return "A poluição voltou para abaixo do limite de alerta.";
                case "environment.garbage": return "O armazenamento de lixo voltou para abaixo do limite de alerta.";
                case "transport.traffic": return "O fluxo de trânsito voltou a ficar em 60% ou mais.";
                default: return "O indicador voltou para abaixo do limite de alerta.";
            }
        }

        private static string SeverityName(IssueSeverity severity)
        {
            return severity == IssueSeverity.Critical ? "crítica" : "de atenção";
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
            return string.Format(Portuguese, "R$ {0:N0}", amount);
        }

        private static string Number(double value, string format)
        {
            return value.ToString(format, Portuguese);
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
