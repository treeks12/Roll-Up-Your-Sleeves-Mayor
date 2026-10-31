using System;
using System.Globalization;
using System.Text;
using ColossalFramework.UI;
using SC4Evolution.Mod.Economy;

namespace SC4Evolution.Mod.UI
{
    /// <summary>Adds category detail to the three existing demand tooltips, without changing the bars.</summary>
    internal sealed class NativeDemandTooltips
    {
        private readonly TooltipBinding[] _bindings;

        internal NativeDemandTooltips(InfoPanel info)
        {
            _bindings = new TooltipBinding[]
            {
                new TooltipBinding(info.Find<UISlider>("ResidentialDemand"),
                    new uint[] { 0x21010, 0x21020, 0x21030 }, new string[] { "R$", "R$$", "R$$$" }),
                new TooltipBinding(info.Find<UISlider>("CommercialDemand"),
                    new uint[] { 0x23110, 0x23120, 0x23130 },
                    new string[] { "Serviços $", "Serviços $$", "Serviços $$$" }),
                new TooltipBinding(info.Find<UISlider>("IndustryOfficeDemand"),
                    new uint[] { 0x23320, 0x23330, 0x24100, 0x24200, 0x24300, 0x24400 },
                    new string[] { "Escritórios $$", "Escritórios $$$", "Agricultura", "Indústria suja", "Manufatura", "Alta tecnologia" })
            };
        }

        internal void Refresh(SegmentedDemandSnapshot demand)
        {
            for (int i = 0; i < _bindings.Length; i++) _bindings[i].Refresh(demand);
        }

        internal void Release()
        {
            for (int i = 0; i < _bindings.Length; i++) _bindings[i].Release();
        }

        private sealed class TooltipBinding
        {
            private readonly UISlider _slider;
            private readonly uint[] _keys;
            private readonly string[] _labels;
            private string _nativeText;
            private string _appliedText;
            private string _ownedBlock;

            internal TooltipBinding(UISlider slider, uint[] keys, string[] labels)
            {
                _slider = slider;
                _keys = keys;
                _labels = labels;
            }

            internal void Refresh(SegmentedDemandSnapshot demand)
            {
                if (_slider == null) return;
                // The game replaces this text when its native demand bucket changes.
                // Keep that replacement (and changes made by other mods) as the current base.
                if (!string.Equals(_slider.tooltip, _appliedText, StringComparison.Ordinal))
                    _nativeText = string.IsNullOrEmpty(_ownedBlock) || string.IsNullOrEmpty(_slider.tooltip)
                        ? _slider.tooltip : _slider.tooltip.Replace(_ownedBlock, string.Empty);

                StringBuilder detail = new StringBuilder();
                detail.Append(demand != null && demand.GameplayEnabled
                    ? "\n\nDemanda por categoria" : "\n\nProjeção por categoria");
                if (demand == null || !demand.Converged)
                    detail.Append("\nSem projeção disponível.");
                else
                {
                    for (int i = 0; i < _keys.Length; i++)
                    {
                        int value;
                        detail.Append('\n').Append(_labels[i]).Append(": ");
                        detail.Append(demand.TryGetDemand(_keys[i], out value)
                            ? Math.Max(-1000, Math.Min(1000, value)).ToString("+0;-0;0", CultureInfo.CurrentCulture) : "—");
                    }
                    detail.Append("\nÍndice −1000 a +1000. Valores positivos indicam procura por novas construções.");
                }

                _ownedBlock = detail.ToString();
                _appliedText = (_nativeText ?? string.Empty) + _ownedBlock;
                if (!string.Equals(_slider.tooltip, _appliedText, StringComparison.Ordinal))
                    _slider.tooltip = _appliedText;
            }

            internal void Release()
            {
                if (_slider != null)
                {
                    string current = _slider.tooltip;
                    if (string.Equals(current, _appliedText, StringComparison.Ordinal))
                        _slider.tooltip = _nativeText;
                    else if (!string.IsNullOrEmpty(current) && !string.IsNullOrEmpty(_ownedBlock)
                        && current.IndexOf(_ownedBlock, StringComparison.Ordinal) >= 0)
                        // Another mod may have appended text since our last refresh. Remove only
                        // the exact block we still own, leaving all its surrounding text untouched.
                        _slider.tooltip = current.Replace(_ownedBlock, string.Empty);
                }
                _appliedText = null;
            }
        }
    }
}
