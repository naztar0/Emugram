//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Newtonsoft.Json;

namespace Telegram.Entities
{
    public record NavigateToHistoryEntryParameters
    {
        public NavigateToHistoryEntryParameters(int entryId)
        {
            EntryId = entryId;
        }

        [JsonProperty("entryId")]
        public int EntryId { get; init; }
    }
}
