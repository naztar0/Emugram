//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System.Threading.Tasks;

namespace Telegram.Td
{
    public delegate void RefAction<T>(ref T value);

    partial class TdCompletionSource : TaskCompletionSource<Object>, ClientResultHandler
    {
        private readonly RefAction<Object> _closure;

        public TdCompletionSource(RefAction<Object> closure)
        {
            _closure = closure;
        }

#if TD_WINRT
        public void OnResult(Object result)
#else
        public void OnResult(BaseObject result)
#endif
        {
            var temp = result as Object;

            _closure(ref temp);
            SetResult(temp);
        }
    }
}
