//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Media;
using Telegram.ViewModels.Settings;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsEmulationPage
    {
        public SettingsEmulationViewModel ViewModel => DataContext as SettingsEmulationViewModel;

        public SettingsEmulationPage()
        {
            InitializeComponent();
            Title = Strings.Emulation;
        }

        private void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is EmulationPresetViewModel preset)
            {
                ViewModel.SelectedItem = preset;
            }
        }

        #region Context menu

        private void Preset_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var info = ScrollingHost.ItemFromContainer(sender) as EmulationPresetViewModel;

            var flyout = new MenuFlyout();

            flyout.CreateFlyoutItem(ViewModel.OpenDuplicateEmulationPreset, info, Strings.Duplicate, Icons.DocumentCopy);

            if (info?.Id > 0)
            {
                flyout.CreateFlyoutItem(ViewModel.OpenEditEmulationPreset, info, Strings.Edit, Icons.Edit);
                flyout.CreateFlyoutItem(ViewModel.Delete, info, Strings.Delete, Icons.Delete, destructive: true);
            }

            flyout.ShowAt(sender, args);
        }

        #endregion

        #region Recycle

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TableListViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.ContextRequested += Preset_ContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            
        }

        #endregion
    }
}
