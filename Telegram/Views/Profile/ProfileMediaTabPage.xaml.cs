//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using System.Numerics;
using Telegram.Common;
using Telegram.Composition;
using Telegram.Controls;
using Telegram.Navigation;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Chats;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Telegram.Views.Profile
{
    public sealed partial class ProfileMediaTabPage : ProfileTabPage
    {
        public ProfileMediaTabPage()
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

            if (ViewModel.Media.Empty())
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

                    var particles = content.Children[1] as AnimatedImage;
                    var overlay = content.Children[2] as Border;
                    var duration = overlay.Child as TextBlock;

                    if (args.Item is MessageWithOwner message)
                    {
                        if (message.Content is MessagePhoto photoMessage)
                        {
                            var small = photoMessage.Photo.GetSmall();

                            photo.SetSource(message.ClientService, small.Photo, photoMessage.Photo.Minithumbnail, blurRadius: photoMessage.HasSpoiler ? 15 : 0);
                            overlay.Visibility = Visibility.Collapsed;

                            particles.Source = photoMessage.HasSpoiler
                                ? new ParticlesImageSource()
                                : null;
                        }
                        else if (message.Content is MessageVideo videoMessage)
                        {
                            var thumbnail = videoMessage.Cover?.GetThumbnail();
                            thumbnail ??= videoMessage.Video.Thumbnail;

                            var minithumbnail = videoMessage.Cover?.Minithumbnail;
                            minithumbnail ??= videoMessage.Video.Minithumbnail;

                            photo.SetSource(message.ClientService, thumbnail?.File, minithumbnail, blurRadius: videoMessage.HasSpoiler ? 15 : 0);
                            overlay.Visibility = Visibility.Visible;

                            duration.Text = videoMessage.Video.GetDuration();

                            particles.Source = videoMessage.HasSpoiler
                                ? new ParticlesImageSource()
                                : null;
                        }
                    }
                    else
                    {
                        photo.Clear();
                        particles.Source = null;
                        overlay.Visibility = Visibility.Collapsed;
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

        public void Zoom(int factor)
        {
            var parent = this.GetParent<ScrollViewer>();
            var child = ScrollingHost.ItemsPanelRoot as ItemsWrapGrid;

            //var container = ScrollingHost.ContainerFromIndex(child.FirstVisibleIndex) as SelectorItem;
            //parent.RegisterAnchorCandidate(container);
            //parent.anchor

            var y = -(float)parent.VerticalOffset + Header.ActualSize.Y - 88;

            var childSize = child.ActualSize.X > 0 && child.ActualSize.Y > 0 ? new Vector2(child.ActualSize.X, (float)parent.ViewportHeight) : new Vector2(1, 1);
            var childOffset = new Vector2(0, Math.Max(-y, 0));

            var visual = BootStrapper.Current.Compositor.CreateRedirectBrush(child, childOffset, childSize, true);
            var panel = ElementComposition.GetElementVisual(child);

            //parent.AnchorRequested -= Parent_AnchorRequested;
            //parent.AnchorRequested += Parent_AnchorRequested;

            var redirect = visual.Compositor.CreateSpriteVisual();
            redirect.RelativeSizeAdjustment = Vector2.One;
            redirect.Offset = new Vector3(0, Header.ActualSize.Y - Math.Min(y, 0), 0);

            //TestGrid.Margin = new Thickness(24, ViewModel.HeaderHeight - Math.Min(y, 0), 16, 0);
            TestGrid.Height = childSize.Y;

            ElementCompositionPreview.SetElementChildVisual(TestGrid, redirect);
            //ElementComposition.GetElementVisual(child).CenterPoint = new Vector3(0, childOffset.Y, 0);
            //ElementComposition.GetElementVisual(child).Clip = redirect.Compositor.CreateInsetClip(0, childOffset.Y, 0, 0);

            VisualUtilities.QueueCallbackForCompositionRendered(this, () =>
            {
                redirect.Brush = visual;

                var prevColumns = Test.ItemScale;

                var prevWidth = (float)FluidGridView.GetLength(ScrollingHost, Test, out int prevMaxColumns);
                var prevCenter = MathF.Floor(childOffset.Y / (prevWidth * prevMaxColumns)) * prevWidth;

                redirect.CenterPoint = new Vector3(0, prevCenter, 0);

                var nextColumns = prevColumns + factor;

                Test.ItemScale = nextColumns;

                var nextWidth = (float)FluidGridView.GetLength(ScrollingHost, Test, out int nextMaxColumns);
                var nextCenter = MathF.Floor(childOffset.Y / (nextWidth * nextMaxColumns)) * nextWidth;

                Logger.Info(nextCenter);

                panel.CenterPoint = new Vector3(0, nextCenter, 0);

                var opacityIn = visual.Compositor.CreateScalarKeyFrameAnimation();
                opacityIn.InsertKeyFrame(0, 0);
                opacityIn.InsertKeyFrame(1, 1);
                //opacityIn.Duration = TimeSpan.FromSeconds(3);

                var opacityOut = visual.Compositor.CreateScalarKeyFrameAnimation();
                opacityOut.InsertKeyFrame(0, 1);
                opacityOut.InsertKeyFrame(1, 0);
                //opacityOut.Duration = TimeSpan.FromSeconds(3);

                var scaleOut = visual.Compositor.CreateVector3KeyFrameAnimation();
                scaleOut.InsertKeyFrame(0, new Vector3(1));
                scaleOut.InsertKeyFrame(1, new Vector3(nextWidth / prevWidth));
                //scaleOut.Duration = TimeSpan.FromSeconds(3);

                var scaleIn = visual.Compositor.CreateVector3KeyFrameAnimation();
                scaleIn.InsertKeyFrame(0, new Vector3(prevWidth / nextWidth));
                scaleIn.InsertKeyFrame(1, new Vector3(1));
                //scaleIn.Duration = TimeSpan.FromSeconds(3);

                redirect.StartAnimation("Opacity", opacityOut);
                redirect.StartAnimation("Scale", scaleOut);

                panel.StartAnimation("Opacity", opacityIn);
                panel.StartAnimation("Scale", scaleIn);
            });
        }
    }
}
