//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Cells;
using Telegram.Controls.Media;
using Telegram.Navigation;
using Telegram.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Telegram.Views.Popups
{
    public sealed partial class CallsPopup : ContentPopup
    {
        public CallsViewModel ViewModel => DataContext as CallsViewModel;

        public CallsPopup()
        {
            InitializeComponent();
            Title = Strings.Calls;
        }

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TextListViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.ContextRequested += OnContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }
            else if (args.ItemContainer.ContentTemplateRoot is CallCell content && args.Item is TLCallGroup call)
            {
                content.UpdateCall(ViewModel.ClientService, call);
                args.Handled = true;
            }
        }

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var call = ScrollingHost.ItemFromContainer(sender) as TLCallGroup;

            var flyout = new MenuFlyout();
            flyout.CreateFlyoutItem(ViewModel.DeleteCall, call, Strings.Delete, Icons.Delete, destructive: true);
            flyout.ShowAt(sender, args);
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is TLCallGroup call)
            {
                Hide();
                ViewModel.NavigationService.NavigateToChat(call.ChatId, call.Message?.Id);
            }
        }

        private void NewCall_Click(object sender, RoutedEventArgs e)
        {
            var popup = new ChooseChatsPopup();
            var button = new BadgeButton
            {
                Content = Strings.GroupCallCreateLink,
                Glyph = Icons.LinkAdd,
                Style = BootStrapper.Current.Resources["GlyphBadgeButtonPopupStyle"] as Style,
                Margin = new Thickness(12, 0, 12, 0),
            };

            void handler(object sender, RoutedEventArgs e)
            {
                button.Click -= handler;
                popup.Hide();

                ViewModel.CreateLink();
            }

            button.Click += handler;
            popup.Header = button;

            Hide();
            ViewModel.NavigationService.ShowPopup(popup, new ChooseChatsConfigurationCreateGroupCall());
        }

        private void ScrollingHeader_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (EmptyState != null)
            {
                EmptyState.Margin = new Thickness(0, e.NewSize.Height - 36, 0, 0);
            }
        }

        private void EmptyState_Loaded(object sender, RoutedEventArgs e)
        {
            EmptyState.Margin = new Thickness(0, ScrollingHeader.ActualHeight - 36, 0, 0);
        }
    }
}
