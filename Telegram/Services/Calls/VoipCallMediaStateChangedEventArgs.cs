//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Telegram.Native.Calls;

namespace Telegram.Services.Calls
{
    public partial class VoipCallMediaStateChangedEventArgs
    {
        public VoipCallMediaStateChangedEventArgs(VoipAudioState audio, VoipVideoState video, bool screen)
        {
            Audio = audio;
            Video = video;
            IsScreenSharing = screen;
        }

        public VoipAudioState Audio { get; init; }

        public VoipVideoState Video { get; init; }

        public bool IsScreenSharing { get; init; }
    }
}
