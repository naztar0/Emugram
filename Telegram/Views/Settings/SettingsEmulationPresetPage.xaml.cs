//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Telegram.ViewModels.Settings;
using Windows.UI.Xaml.Navigation;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsEmulationPresetPage
    {
        public SettingsEmulationPresetViewModel ViewModel => DataContext as SettingsEmulationPresetViewModel;

        public SettingsEmulationPresetPage()
        {
            InitializeComponent();
            Title = Strings.EmulationPreset;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is EmulationPresetViewModel preset)
            {
                ViewModel.LoadPreset(preset.Id, preset.Duplicate);
            }
        }

    }
}
