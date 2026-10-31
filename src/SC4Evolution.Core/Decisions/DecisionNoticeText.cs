using System;

namespace SC4Evolution.Core.Decisions
{
    /// <summary>Presentation labels for the native city-level decisions. Unknown inputs are retained.</summary>
    public static class DecisionNoticeText
    {
        public static string Action(string value, bool english)
        {
            if (value == null) return null;
            if (english)
            {
                if (value == "Improve aggregate access") return "Improve access to jobs";
                if (value == "Review the shadow budget") return "Review the estimated budget";
                return value;
            }
            switch (value)
            {
                case "Address abandoned buildings": return "Cuide dos edifícios abandonados";
                case "Review the municipal budget": return "Revise o orçamento municipal";
                case "Expand health services": return "Amplie os serviços de saúde";
                case "Improve transport connections": return "Melhore as conexões de transporte";
                case "Expand employment opportunities": return "Amplie as oportunidades de emprego";
                case "Improve aggregate access": return "Melhore o acesso a empregos";
                case "Review the shadow budget": return "Revise a estimativa orçamentária";
                default: return value;
            }
        }

        public static string Indicator(string value, bool english)
        {
            if (string.IsNullOrEmpty(value)) return value;
            int separator = value.IndexOf(": ", StringComparison.Ordinal);
            if (separator < 0) return value;
            string key = value.Substring(0, separator);
            string label;
            switch (key)
            {
                case "abandonedBuildings": label = english ? "Abandoned buildings" : "Edifícios abandonados"; break;
                case "weeklyBalance": label = english ? "Weekly balance" : "Saldo semanal"; break;
                case "sickCitizens": label = english ? "Sick residents" : "Moradores doentes"; break;
                case "trafficFlowPercent": label = english ? "Traffic flow (%)" : "Fluxo de trânsito (%)"; break;
                case "unemploymentPercent": label = english ? "Unemployment (%)" : "Desemprego (%)"; break;
                case "shadowAccessPressure": label = english ? "Estimated access pressure" : "Pressão estimada de acesso"; break;
                case "shadowFiscalPressure": label = english ? "Estimated fiscal pressure" : "Pressão fiscal estimada"; break;
                default: return value;
            }
            return label + value.Substring(separator);
        }
    }
}
