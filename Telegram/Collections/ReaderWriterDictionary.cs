//
// Copyright (c) Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Telegram.Collections
{
    public class ReaderWriterDictionary<TKey, TValue> : IEnumerable<TValue>
    {
        private readonly ReaderWriterLockSlim _lock = new();
        private readonly Dictionary<TKey, TValue> _dictionary = new();

        public TValue this[TKey key]
        {
            set
            {
                _lock.EnterWriteLock();
                try
                {
                    _dictionary[key] = value;
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }
        }

        public void Remove(TKey key)
        {
            _lock.EnterWriteLock();
            try
            {
                _dictionary.Remove(key);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                _dictionary.Clear();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public int Count
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    return _dictionary.Count;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            _lock.EnterReadLock();
            try
            {
                return _dictionary.TryGetValue(key, out value);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public bool ContainsKey(TKey key)
        {
            _lock.EnterReadLock();
            try
            {
                return _dictionary.ContainsKey(key);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public TValue Find(Predicate<TValue> predicate)
        {
            _lock.EnterReadLock();
            try
            {
                return _dictionary.Values.FirstOrDefault(x => predicate(x));
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void ForEach(Action<TValue> action)
        {
            _lock.EnterReadLock();
            try
            {
                foreach (var value in _dictionary.Values)
                {
                    action(value);
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public IEnumerator<TValue> GetEnumerator()
        {
            IList<TValue> snapshot;

            _lock.EnterReadLock();
            try
            {
                snapshot = _dictionary.Values.ToArray();
            }
            finally
            {
                _lock.ExitReadLock();
            }

            return snapshot.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
