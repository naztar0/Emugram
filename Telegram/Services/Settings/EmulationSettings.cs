//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Windows.Storage;

namespace Telegram.Services.Settings
{
    public class EmulationSettings : SettingsServiceBase
    {
        public EmulationSettings(ApplicationDataContainer container)
            : base(container)
        {

        }

        private long? _presetId;
        public long PresetId
        {
            get => _presetId ??= GetValueOrDefault<long>("EmulationPreset", -1);
            set => AddOrUpdateValue(ref _presetId, "EmulationPreset", value);
        }

        private bool? _preventFullscreen;
        public bool PreventFullscreen
        {
            get => _preventFullscreen ??= GetValueOrDefault("PreventFullscreen", false);
            set => AddOrUpdateValue(ref _preventFullscreen, "PreventFullscreen", value);
        }
    }
}
