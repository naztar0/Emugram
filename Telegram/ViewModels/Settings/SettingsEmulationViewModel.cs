//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Views.Settings;
using Windows.UI.Xaml.Navigation;

namespace Telegram.ViewModels.Settings
{
    public class SettingsEmulationViewModel : ViewModelBase
    {
        private readonly IEmulationService _emulationService;

        public SettingsEmulationViewModel(IClientService clientService, ISettingsService settingsService, IEventAggregator aggregator, IEmulationService emulationService)
            : base(clientService, settingsService, aggregator)
        {
            _emulationService = emulationService;

            Presets = new ObservableCollection<EmulationPresetViewModel>();
        }

        public ObservableCollection<EmulationPresetViewModel> Presets { get; }

        protected override async Task OnNavigatedToAsync(object parameter, NavigationMode mode, NavigationState state)
        {

            var defaultPresetViewModels = EmulationService.DefaultEmulationPresets.Select(p => new EmulationPresetViewModel(p));
            var presets = await _emulationService.GetPresetsAsync();
            var presetViewModels = presets.Select(p => new EmulationPresetViewModel(p));

            IEnumerable<EmulationPresetViewModel> emulationPresetViewModels = defaultPresetViewModels as EmulationPresetViewModel[] ?? defaultPresetViewModels.ToArray();

            Presets.AddRange(emulationPresetViewModels.Union(presetViewModels));

            _selectedItem = Presets.FirstOrDefault(x => x.Id == Settings.Emulation.PresetId) ?? emulationPresetViewModels.First();

            RaisePropertyChanged(nameof(SelectedItem));
        }

        private EmulationPresetViewModel _selectedItem;
        public EmulationPresetViewModel SelectedItem
        {
            get => _selectedItem;
            set => Select(value);
        }

        public bool SelectionChanged { get; private set; }

        private void Select(EmulationPresetViewModel preset)
        {
            if (preset == null || preset.Id == _selectedItem?.Id)
            {
                return;
            }

            Settings.Emulation.PresetId = preset.Id;

            _selectedItem = preset;
            SelectionChanged = true;
            RaisePropertyChanged(nameof(SelectedItem));
        }

        public async void Delete(EmulationPresetViewModel preset)
        {
            await _emulationService.DeletePresetsAsync(preset.Id);

            Presets.Remove(preset);

            if (_selectedItem == preset)
            {
                Select(Presets[0]);
            }
        }

        public void OpenCreateEmulationPreset()
        {
            NavigationService.Navigate(typeof(SettingsEmulationPresetPage));
        }

        public void OpenEditEmulationPreset(EmulationPresetViewModel preset)
        {
            NavigationService.Navigate(typeof(SettingsEmulationPresetPage), preset);
        }
        public void OpenDuplicateEmulationPreset(EmulationPresetViewModel preset)
        {
            preset.Duplicate = true;
            NavigationService.Navigate(typeof(SettingsEmulationPresetPage), preset);
        }
    }

    public class EmulationPresetViewModel : BindableBase
    {
        private readonly EmulationPreset _preset;

        public EmulationPresetViewModel(EmulationPreset preset)
        {
            _preset = preset;
        }

        public bool Duplicate { get; set; }

        public long Id => _preset.Id;
        public long LastUsedDate => _preset.LastUsedDate;
        public string Title => _preset.Title;
        public string AppName => _preset.ApplicationName;
        public string Platform => _preset.SecChUaPlatform;
        public string UserAgent => _preset.UserAgent ?? "Default";
    }
}
