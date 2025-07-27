//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Chats;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Telegram.Views.Profile
{
    public sealed partial class ProfileAnimationsTabPage : ProfileTabPage
    {
        public ProfileAnimationsTabPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (!IsProfile)
            {
                ScrollingHost.Padding = new Thickness(12, 0, 4, 8);
            }

            if (ViewModel.Animations.Empty())
            {
                ScrollingHost.ItemContainerTransitions.Add(new EntranceThemeTransition { IsStaggeringEnabled = false });
            }
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            try
            {
                if (args.InRecycleQueue)
                {
                    return;
                }
                else if (args.ItemContainer.ContentTemplateRoot is Grid content)
                {
                    var photo = content.Children[0] as ImageView;

                    if (args.Item is MessageWithOwner message && message.Content is MessageAnimation animation)
                    {
                        if (animation.Animation.Thumbnail is { Format: ThumbnailFormatJpeg })
                        {
                            photo.SetSource(message.ClientService, animation.Animation.Thumbnail.File, animation.Animation.Minithumbnail);
                        }
                        else if (animation.Animation.Minithumbnail != null)
                        {
                            var bitmap = new BitmapImage();
                            PlaceholderHelper.GetBlurred(bitmap, animation.Animation.Minithumbnail.Data);
                            photo.Source = bitmap;
                        }
                    }
                    else
                    {
                        photo.Clear();
                    }

                    args.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private async void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MessageWithOwner message)
            {
                var response = await ViewModel.ClientService.SendAsync(new GetMessageProperties(message.ChatId, message.Id));
                if (response is not MessageProperties properties)
                {
                    return;
                }

                var element = ScrollingHost.ContainerFromItem(e.ClickedItem);

                var viewModel = new ChatGalleryViewModel(ViewModel.ClientService, ViewModel.StorageService, ViewModel.Aggregator, message.ChatId, ViewModel.Topic, message, properties, true);
                ViewModel.NavigationService.ShowGallery(viewModel, element as SelectorItem);
            }
        }
    }
}
