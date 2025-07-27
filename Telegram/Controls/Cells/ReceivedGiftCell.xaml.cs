//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Converters;
using Telegram.Services;
using Telegram.Streams;
using Telegram.Td.Api;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Telegram.Controls.Cells
{
    public sealed partial class ReceivedGiftCell : UserControl
    {
        public ReceivedGiftCell()
        {
            InitializeComponent();
        }

        public void UpdateGift(IClientService clientService, ReceivedGift gift)
        {
            StarCountRoot.Visibility = Visibility.Collapsed;

            if (gift.Gift is SentGiftRegular regular)
            {
                if (gift.IsPinned)
                {
                    Photo.Visibility = Visibility.Collapsed;
                    Pinned.Visibility = Visibility.Visible;
                    Pinned.RequestedTheme = ElementTheme.Default;

                    VisualUtilities.DropShadow(Pinned, target: Shadow);
                }
                else
                {
                    Photo.Visibility = Visibility.Visible;
                    Pinned.Visibility = Visibility.Collapsed;

                    if (gift.IsPrivate)
                    {
                        Photo.Source = PlaceholderImage.GetGlyph(Icons.AuthorHiddenFilled, 5);
                    }
                    else if (clientService.TryGetUser(gift.SenderId, out User user))
                    {
                        Photo.SetUser(clientService, user, 24);
                    }
                    else if (clientService.TryGetChat(gift.SenderId, out Chat chat))
                    {
                        Photo.SetChat(clientService, chat, 24);
                    }
                }

                Pattern.Visibility = Visibility.Collapsed;

                Animated.Source = new DelayedFileSource(clientService, regular.Gift.Sticker);

                StarCount.Text = gift.SellStarCount > 0
                    ? gift.SellStarCount.ToString("N0")
                    : regular.Gift.StarCount.ToString("N0");

                if (regular.Gift.TotalCount > 0)
                {
                    RibbonRoot.Visibility = Visibility.Visible;
                    Ribbon.Text = string.Format(Strings.Gift2Limited1OfRibbon, Formatter.ShortNumber(regular.Gift.TotalCount, true));

                    RibbonTop.Color = _ribbonLimitedTop;
                    RibbonBottom.Color = _ribbonLimitedBottom;

                    if (RibbonPath.Fill is not LinearGradientBrush)
                    {
                        RibbonPath.Fill = RibbonGradient;
                    }
                }
                else
                {
                    RibbonRoot.Visibility = Visibility.Collapsed;
                }

                if (ResaleStarCountRoot != null)
                {
                    ResaleStarCountRoot.Visibility = Visibility.Collapsed;
                }
            }
            else if (gift.Gift is SentGiftUpgraded upgraded)
            {
                var source = DelayedFileSource.FromSticker(clientService, upgraded.Gift.Symbol.Sticker);
                var centerColor = upgraded.Gift.Backdrop.Colors.CenterColor.ToColor();
                var edgeColor = upgraded.Gift.Backdrop.Colors.EdgeColor.ToColor();

                Pattern.Update(source, centerColor, edgeColor);

                if (gift.IsPinned)
                {
                    Pinned.Visibility = Visibility.Visible;
                    Pinned.RequestedTheme = ElementTheme.Dark;

                    VisualUtilities.DropShadow(Pinned, target: Shadow);
                }
                else
                {
                    Pinned.Visibility = Visibility.Collapsed;
                }

                Photo.Visibility = Visibility.Collapsed;
                Pattern.Visibility = Visibility.Visible;

                Animated.Source = new DelayedFileSource(clientService, upgraded.Gift.Model.Sticker);

                RibbonRoot.Visibility = Visibility.Visible;

                if (upgraded.Gift.ResaleStarCount > 0)
                {
                    Ribbon.Text = Strings.Gift2OnSale;

                    RibbonTop.Color = _ribbonResaleTop;
                    RibbonBottom.Color = _ribbonResaleBottom;

                    if (ResaleStarCountRoot != null)
                    {
                        ResaleStarCountRoot.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        FindName(nameof(ResaleStarCountRoot));
                        Grid.SetRow(ResaleStarCountRoot, 0);
                    }

                    ResaleStarCountRoot.Background = new SolidColorBrush(edgeColor.WithBrightness(-0.1f));
                    ResaleStarCount.Text = upgraded.Gift.ResaleStarCount.ToString("N0");
                }
                else
                {
                    Ribbon.Text = string.Format(Strings.Gift2Limited1OfRibbon, Formatter.ShortNumber(upgraded.Gift.MaxUpgradedCount, true));

                    RibbonTop.Color = centerColor.WithBrightness(-0.1f);
                    RibbonBottom.Color = edgeColor.WithBrightness(-0.1f);

                    if (ResaleStarCountRoot != null)
                    {
                        ResaleStarCountRoot.Visibility = Visibility.Collapsed;
                    }
                }
            }

            if (gift.IsSaved)
            {
                if (Hidden != null)
                {
                    Hidden.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                FindName(nameof(Hidden));
                Hidden.Visibility = Visibility.Visible;
            }
        }

        public void UpdateGift(IClientService clientService, GiftForResale gift)
        {
            StarCountRoot.Visibility = Visibility.Collapsed;

            var source = DelayedFileSource.FromSticker(clientService, gift.Gift.Symbol.Sticker);
            var centerColor = gift.Gift.Backdrop.Colors.CenterColor.ToColor();
            var edgeColor = gift.Gift.Backdrop.Colors.EdgeColor.ToColor();

            Pattern.Update(source, centerColor, edgeColor);

            Pinned.Visibility = Visibility.Collapsed;

            Photo.Visibility = Visibility.Collapsed;
            Pattern.Visibility = Visibility.Visible;

            Animated.Source = new DelayedFileSource(clientService, gift.Gift.Model.Sticker);

            FindName(nameof(ResaleStarCountRoot));
            ResaleStarCountRoot.Background = new SolidColorBrush(edgeColor.WithBrightness(-0.1f));
            ResaleStarCount.Text = gift.Gift.ResaleStarCount.ToString("N0");

            RibbonRoot.Visibility = Visibility.Visible;
            Ribbon.Text = string.Format("#{0:N0}", gift.Gift.Number);

            RibbonTop.Color = centerColor.WithBrightness(-0.1f);
            RibbonBottom.Color = edgeColor.WithBrightness(-0.1f);
        }

        private readonly Color _ribbonResaleTop = Color.FromArgb(0xFF, 0xAC, 0xDC, 0x89);
        private readonly Color _ribbonResaleBottom = Color.FromArgb(0xFF, 0x75, 0xC8, 0x73);

        private readonly Color _ribbonLimitedTop = Color.FromArgb(0xFF, 0x8A, 0xD3, 0xF9);
        private readonly Color _ribbonLimitedBottom = Color.FromArgb(0xFF, 0x51, 0x9D, 0xEA);

        private readonly Color _ribbonSoldOutTop = Color.FromArgb(0xFF, 0xFF, 0x5B, 0x54);
        private readonly Color _ribbonSoldOutBottom = Color.FromArgb(0xFF, 0xED, 0x1D, 0x27);

        public void UpdateGift(IClientService clientService, AvailableGift gift)
        {
            Photo.Visibility = Visibility.Collapsed;
            Pinned.Visibility = Visibility.Collapsed;

            StarCountParticles.Source = new ParticlesImageSource(Color.FromArgb(0x80, 0xE8, 0xAB, 0x02), Native.ParticlesType.Premium);
            Animated.Source = new DelayedFileSource(clientService, gift.Gift.Sticker);

            if (gift.Gift.TotalCount > 0 && (gift.Gift.RemainingCount > 0 || gift.MinResaleStarCount == 0))
            {
                StarCount.Text = gift.Gift.StarCount.ToString("N0");

                RibbonRoot.Visibility = Visibility.Visible;
                Ribbon.Text = gift.Gift.RemainingCount > 0
                    ? Strings.Gift2LimitedRibbon
                    : Strings.Gift2SoldOut;

                RibbonTop.Color = gift.Gift.RemainingCount > 0 ? _ribbonLimitedTop : _ribbonSoldOutTop;
                RibbonBottom.Color = gift.Gift.RemainingCount > 0 ? _ribbonLimitedBottom : _ribbonSoldOutBottom;
            }
            else if (gift.MinResaleStarCount > 0)
            {
                StarCount.Text = gift.MinResaleStarCount.ToString("N0");

                RibbonRoot.Visibility = Visibility.Visible;
                Ribbon.Text = Strings.Gift2Resale;

                RibbonTop.Color = _ribbonResaleTop;
                RibbonBottom.Color = _ribbonResaleBottom;
            }
            else
            {
                StarCount.Text = gift.Gift.StarCount.ToString("N0");

                RibbonRoot.Visibility = Visibility.Collapsed;
            }

            if (Hidden != null)
            {
                Hidden.Visibility = Visibility.Collapsed;
            }
        }
    }
}
