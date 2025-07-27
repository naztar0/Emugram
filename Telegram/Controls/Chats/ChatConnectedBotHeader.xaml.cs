//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System.Collections.Generic;
using System.Numerics;
using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.Views;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Hosting;

namespace Telegram.Controls.Chats
{
    public sealed partial class ChatConnectedBotHeader : UserControl
    {
        public DialogViewModel ViewModel => DataContext as DialogViewModel;

        private Chat _chat;

        private ChatView _chatView;
        private UIElement _parent;

        public ChatConnectedBotHeader()
        {
            InitializeComponent();
        }

        public float AnimatedHeight => _collapsed ? 0 : 48;

        public void InitializeParent(ChatView chatView, UIElement parent)
        {
            _chatView = chatView;
            ElementCompositionPreview.SetIsTranslationEnabled(_parent = parent, true);
        }

        public void UpdateChatBusinessBotManageBar(Chat chat, BusinessBotManageBar manageBar)
        {
            _chat = chat;

            if (manageBar != null && ViewModel.ClientService.TryGetUser(manageBar.BotUserId, out User user))
            {
                ShowHide(true);

                Photo.SetUser(ViewModel.ClientService, user, 36);
                Title.Text = user.FullName();
                Subtitle.Text = manageBar.IsBotPaused
                    ? Strings.BizBotStatusStopped
                    : manageBar.CanBotReply
                    ? Strings.BizBotStatusManages
                    : Strings.BizBotStatusAccess;

                ToggleButton.Content = manageBar.IsBotPaused
                    ? Strings.BizBotStart
                    : Strings.BizBotStop;

                ToggleButton.Visibility = manageBar.CanBotReply
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            else
            {
                ShowHide(false);
            }
        }

        private bool _collapsed = true;

        public void ShowHide(bool show)
        {
            if (_collapsed != show)
            {
                return;
            }

            _collapsed = !show;
            Visibility = Visibility.Visible;

            var parent = ElementComposition.GetElementVisual(_parent);
            var visual = ElementComposition.GetElementVisual(this);
            visual.Clip = visual.Compositor.CreateInsetClip();

            var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                visual.Clip = null;
                parent.Properties.InsertVector3("Translation", Vector3.Zero);

                if (_collapsed)
                {
                    Visibility = Visibility.Collapsed;
                }
            };

            _chatView.UpdateMessagesHeaderPadding();

            var clip = visual.Compositor.CreateScalarKeyFrameAnimation();
            clip.InsertKeyFrame(show ? 0 : 1, 48);
            clip.InsertKeyFrame(show ? 1 : 0, 0);
            clip.Duration = Constants.FastAnimation;

            var offset = visual.Compositor.CreateScalarKeyFrameAnimation();
            offset.InsertKeyFrame(show ? 0 : 1, -48);
            offset.InsertKeyFrame(show ? 1 : 0, 0);
            offset.Duration = Constants.FastAnimation;

            visual.Clip.StartAnimation("TopInset", clip);
            parent.StartAnimation("Translation.Y", offset);

            batch.End();
        }

        public IEnumerable<UIElement> GetAnimatableVisuals()
        {
            if (_collapsed)
            {
                yield break;
            }

            yield return ToggleButton;
            yield return MenuButton;
        }

        private void Menu_ContextRequested(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            flyout.CreateFlyoutItem(Remove, Strings.BizBotRemove, Icons.DismissCircle, destructive: true);
            flyout.CreateFlyoutItem(Manage, Strings.BizBotManage, Icons.Settings);

            flyout.ShowAt(sender as Button, FlyoutPlacementMode.BottomEdgeAlignedRight);
        }

        private void Remove()
        {
            var manage = _chat?.BusinessBotManageBar;
            if (manage != null)
            {
                ViewModel.ClientService.Send(new RemoveBusinessConnectedBotFromChat(_chat.Id));
            }
        }

        private void Manage()
        {
            var manage = _chat?.BusinessBotManageBar;
            if (manage != null)
            {
                MessageHelper.OpenUrl(ViewModel.ClientService, ViewModel.NavigationService, manage.ManageUrl);
            }
        }

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            var manage = _chat?.BusinessBotManageBar;
            if (manage != null)
            {
                ViewModel.ClientService.Send(new ToggleBusinessConnectedBotChatIsPaused(_chat.Id, !manage.IsBotPaused));
            }
        }
    }
}
