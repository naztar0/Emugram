//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;

namespace Telegram.Td
{
    partial class TdHandler : ClientResultHandler
    {
        private readonly RefAction<Object> _closure;
        private readonly Action<Object> _callback;

        public TdHandler(RefAction<Object> closure, Action<Object> callback)
        {
            _closure = closure;
            _callback = callback;
        }

#if TD_WINRT
        public void OnResult(Object result)
#else
        public void OnResult(BaseObject result)
#endif
        {
            try
            {
                var temp = result as Object;

                _closure(ref temp);
                _callback?.Invoke(temp);
            }
            catch
            {
                // We need to explicitly catch here because
                // an exception on the handler thread will cause
                // the app to no longer receive any update from TDLib.
            }
        }
    }
}
