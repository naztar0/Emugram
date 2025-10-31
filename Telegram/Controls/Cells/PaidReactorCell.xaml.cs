//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Telegram.Controls.Media;
using Telegram.Services;
using Telegram.Td.Api;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls.Cells
{
    public sealed partial class PaidReactorCell : Grid
    {
        public PaidReactorCell()
        {
            InitializeComponent();
        }

        public void UpdateCell(IClientService clientService, PaidReactor reactor)
        {
            if (reactor.IsAnonymous)
            {
                Photo.Source = ProfilePictureSourceText.GetGlyph(Icons.AuthorHiddenFilled, long.MinValue);
                Title.Text = Strings.StarsReactionAnonymous;
            }
            else if (clientService.TryGetChat(reactor.SenderId, out Chat chat))
            {
                Photo.Source = ProfilePictureSource.Chat(clientService, chat);
                Title.Text = chat.Title;
            }
            else if (clientService.TryGetUser(reactor.SenderId, out User user))
            {
                Photo.Source = ProfilePictureSource.User(clientService, user);
                Title.Text = user.FullName();
            }

            Badge.Text = Icons.Premium + "\u2004" + reactor.StarCount.ToString("N0");
        }
    }
}
