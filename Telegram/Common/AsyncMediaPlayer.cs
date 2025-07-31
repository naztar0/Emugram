//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Telegram.Services;
using Telegram.Td;
using Telegram.Td.Api;
using Windows.Foundation;
using Windows.Media.Devices;
using Windows.Storage;
using Windows.System;

namespace Telegram.Common
{
    public partial class AsyncMediaTrack
    {
        public AsyncMediaTrack(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public int Width { get; }

        public int Height { get; }
    }

    public partial class AsyncMediaPlayer
    {
        private readonly DispatcherQueue _dispatcherQueue;

        private readonly AsyncMediaPlayerSwapChain _graphicsContext;

        private readonly LibVLC _library;
        private readonly MediaPlayer _player;

        private readonly bool _enableDebugLogs;

        private Media _media;
        private MediaInput _input;

        private readonly object _closeLock = new();
        private bool _closed;

        public AsyncMediaPlayer(bool createGraphicsContext, params string[] options)
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _enableDebugLogs = SettingsService.Current.VerbosityLevel >= 4;

            if (createGraphicsContext && _dispatcherQueue != null)
            {
                _graphicsContext = new AsyncMediaPlayerSwapChain();
                _library = new LibVLC(_enableDebugLogs, _graphicsContext.SwapChainOptions);
            }
            else
            {
                // Generating plugins cache requires a breakpoint in bank.c#504
                _library = new LibVLC(_enableDebugLogs, options); //"--quiet", "--reset-plugins-cache");
            }

            if (_enableDebugLogs)
            {
                _library.Log += OnLog;
            }

            _player = new MediaPlayer(_library);

            // Stories
            _player.ESSelected += OnESSelected;
            _player.Vout += OnVout;
            _player.Buffering += OnBuffering;
            _player.EndReached += OnEndReached;

            // Gallery
            _player.TimeChanged += OnTimeChanged;
            _player.LengthChanged += OnLengthChanged;
            //_player.EndReached += OnEndReached;
            _player.Playing += OnPlaying;
            _player.Paused += OnPaused;
            _player.Stopped += OnStopped;
            _player.VolumeChanged += OnVolumeChanged;

            // Music
            //_player.TimeChanged += OnTimeChanged;
            //_player.LengthChanged += OnLengthChanged;
            _player.EncounteredError += OnEncounteredError;
            //_player.EndReached += OnEndReached;

            MediaDevice.DefaultAudioRenderDeviceChanged += OnDefaultAudioRenderDeviceChanged;
        }

        private void OnDefaultAudioRenderDeviceChanged(object sender, DefaultAudioRenderDeviceChangedEventArgs args)
        {
            if (args.Role == AudioDeviceRole.Default)
            {
                Write(() => _player.SetAudioOutput(args.Id));
            }
        }

        public AsyncMediaPlayerSwapChain Context => _graphicsContext;

        public void Play(MediaInput input)
        {
            Write(valid => PlayImpl(input, valid));
        }

        private void PlayImpl(MediaInput input, bool play)
        {
            if (play)
            {
                var media = new Media(_library, input);

                _player.Play(media);

                // We need to retain both Media and MediaInput due to the bad (IMHO) design of libvlc API.
                // When creating a Media from a MediaInput, some callbacks are registered to access the stream.
                // The problem is that the library creates a GC handle in MediaInput that is then used by Media
                // to register the aforementioned callbacks. What happens, in my understanding, is that there are
                // some good chances that MediaInput is disposed before Media, and due to that the GC handle is deleted
                // and this causes an access violation in libvlccore when trying to raise the callbacks for the media.
                _media?.Dispose();
                _media = media;

                _input?.Dispose();
                _input = input;
            }
            else
            {
                input.Dispose();
            }
        }

        public void Play(Uri input)
        {
            Write(valid => PlayImpl(input, valid));
        }

        private void PlayImpl(Uri input, bool play)
        {
            if (play)
            {
                // Not sure whether it's file or network caching and if they make any difference at all
                var media = new Media(_library, input, ":file-caching=10000", ":network-caching=10000");

                _player.Play(media);

                // We need to retain both Media and MediaInput due to the bad (IMHO) design of libvlc API.
                // When creating a Media from a MediaInput, some callbacks are registered to access the stream.
                // The problem is that the library creates a GC handle in MediaInput that is then used by Media
                // to register the aforementioned callbacks. What happens, in my understanding, is that there are
                // some good chances that MediaInput is disposed before Media, and due to that the GC handle is deleted
                // and this causes an access violation in libvlccore when trying to raise the callbacks for the media.
                _media?.Dispose();
                _media = media;

                _input?.Dispose();
                _input = null;
            }
        }

        public void Play()
        {
            Write(() => _player.Play());
        }

        public void Stop()
        {
            Write(() => _player.Stop());
        }

        public void Pause(bool pause = true)
        {
            Write(() => _player.SetPause(pause));
        }

        public VLCState State => Read(() => _player.State);

        public bool IsPlaying => Read(() => _player.IsPlaying);

        public bool CanPause => Read(() => _player.CanPause);

        public bool Mute
        {
            get => Read(() => _player.Mute);
            set => Write(() => _player.Mute = value);
        }

        public long Length => Read(() => _player.Length);

        public long Time
        {
            get => Read(() => _player.Time);
            set => Write(() => _player.Time = value);
        }

        public void AddTime(long value)
        {
            Write(() => _player.Time += value);
        }

        public float Scale
        {
            get => Read(() => _player.Scale);
            set => Write(() => _player.Scale = value);
        }

        public float Rate
        {
            get => Read(() => _player.Rate);
            set => Write(() => _player.Rate = value);
        }

        public int Volume
        {
            get => Read(() => _player.Volume);
            set => Write(() => _player.Volume = value);
        }

        public AsyncMediaTrack Track
        {
            get => Read(GetTrack);
        }

        public void Close()
        {
            _workQueue.Clear();
            Write(CloseImpl);
        }

        private void CloseImpl()
        {
            MediaDevice.DefaultAudioRenderDeviceChanged -= OnDefaultAudioRenderDeviceChanged;

            _player.ESSelected -= OnESSelected;
            _player.Vout -= OnVout;
            _player.Buffering -= OnBuffering;
            _player.EndReached -= OnEndReached;
            _player.TimeChanged -= OnTimeChanged;
            _player.LengthChanged -= OnLengthChanged;
            _player.Playing -= OnPlaying;
            _player.Paused -= OnPaused;
            _player.Stopped -= OnStopped;
            _player.VolumeChanged -= OnVolumeChanged;
            _player.EncounteredError -= OnEncounteredError;
            _player.Stop();
            _player.Media = null;

            if (_media != null)
            {
                _media?.Dispose();
                _media = null;
            }

            if (_input != null)
            {
                _input.Dispose();
                _input = null;
            }

            if (_enableDebugLogs)
            {
                _library.Log -= OnLog;
            }

            if (_graphicsContext != null)
            {
                _dispatcherQueue.TryEnqueue(_graphicsContext.Destroy);
            }

            lock (_closeLock)
            {
                _closed = true;

                //_player.Dispose();
                //_library.Dispose();
            }
        }

        private AsyncMediaTrack GetTrack()
        {
            var videoTrack = GetVideoTrack(_player.VideoTrack);
            if (videoTrack is not VideoTrack track)
            {
                return new AsyncMediaTrack(0, 0);
            }

            if (track.Orientation is VideoOrientation.RightTop or VideoOrientation.LeftTop)
            {
                return new AsyncMediaTrack((int)track.Height, (int)track.Width);
            }

            return new AsyncMediaTrack((int)track.Width, (int)track.Height);
        }

        private VideoTrack? GetVideoTrack(int selectedVideoTrack)
        {
            if (selectedVideoTrack == -1)
            {
                return null;
            }

            try
            {
                var media = _player.Media;
                MediaTrack? videoTrack = null;
                if (media != null)
                {
                    videoTrack = media.Tracks?.FirstOrDefault(t => t.Id == selectedVideoTrack);
                    media.Dispose();
                }
                return videoTrack == null ? (VideoTrack?)null : ((MediaTrack)videoTrack).Data.Video;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #region Events

        public event TypedEventHandler<AsyncMediaPlayer, EventArgs> Vout;
        public event TypedEventHandler<AsyncMediaPlayer, MediaPlayerESSelectedEventArgs> ESSelected;
        public event TypedEventHandler<AsyncMediaPlayer, EventArgs> EndReached;
        public event TypedEventHandler<AsyncMediaPlayer, MediaPlayerBufferingEventArgs> Buffering;
        public event TypedEventHandler<AsyncMediaPlayer, MediaPlayerTimeChangedEventArgs> TimeChanged;
        public event TypedEventHandler<AsyncMediaPlayer, MediaPlayerLengthChangedEventArgs> LengthChanged;
        public event TypedEventHandler<AsyncMediaPlayer, EventArgs> Playing;
        public event TypedEventHandler<AsyncMediaPlayer, EventArgs> Paused;
        public event TypedEventHandler<AsyncMediaPlayer, EventArgs> Stopped;
        public event TypedEventHandler<AsyncMediaPlayer, MediaPlayerVolumeChangedEventArgs> VolumeChanged;
        public event TypedEventHandler<AsyncMediaPlayer, EventArgs> EncounteredError;

        private void OnVout(object sender, MediaPlayerVoutEventArgs e)
        {
            TryEnqueue(() => Vout?.Invoke(this, EventArgs.Empty));
        }

        private void OnESSelected(object sender, MediaPlayerESSelectedEventArgs e)
        {
            TryEnqueue(() => ESSelected?.Invoke(this, e));
        }

        private void OnEndReached(object sender, EventArgs e)
        {
            TryEnqueue(() => EndReached?.Invoke(this, EventArgs.Empty));
        }

        private void OnBuffering(object sender, MediaPlayerBufferingEventArgs e)
        {
            TryEnqueue(() => Buffering?.Invoke(this, e));
        }

        private void OnTimeChanged(object sender, MediaPlayerTimeChangedEventArgs e)
        {
            TryEnqueue(() => TimeChanged?.Invoke(this, e));
        }

        private void OnLengthChanged(object sender, MediaPlayerLengthChangedEventArgs e)
        {
            TryEnqueue(() => LengthChanged?.Invoke(this, e));
        }

        private void OnPlaying(object sender, EventArgs e)
        {
            TryEnqueue(() => Playing?.Invoke(this, EventArgs.Empty));
        }

        private void OnPaused(object sender, EventArgs e)
        {
            TryEnqueue(() => Paused?.Invoke(this, EventArgs.Empty));
        }

        private void OnStopped(object sender, EventArgs e)
        {
            TryEnqueue(() => Stopped?.Invoke(this, EventArgs.Empty));
        }

        private void OnVolumeChanged(object sender, MediaPlayerVolumeChangedEventArgs e)
        {
            TryEnqueue(() => VolumeChanged?.Invoke(this, e));
        }

        private void OnEncounteredError(object sender, EventArgs e)
        {
            TryEnqueue(() => EncounteredError?.Invoke(this, EventArgs.Empty));
        }

        private void TryEnqueue(DispatcherQueueHandler action)
        {
            if (_dispatcherQueue != null)
            {
                _dispatcherQueue.TryEnqueue(action);
            }
            else
            {
                ThreadPool.QueueUserWorkItem(state => action());
            }
        }

        #endregion

        #region Logs

        private static readonly Regex _videoLooking = new("using (.*?) module \"(.*?)\" from (.*?)$", RegexOptions.Compiled);
        private static readonly object _syncObject = new();

        private void OnLog(object sender, LogEventArgs e)
        {
            Client.Execute(new AddLogMessage(2, e.FormattedLog));
            return;

            Debug.WriteLine(e.FormattedLog);

            lock (_syncObject)
            {
                var match = _videoLooking.Match(e.FormattedLog);
                if (match.Success)
                {
                    System.IO.File.AppendAllText(ApplicationData.Current.LocalFolder.Path + "\\vlc.txt", string.Format("{2}\n", match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value));
                }
            }
        }

        #endregion

        private bool _workStarted;
        private Thread _workThread;

        private readonly WorkQueue _workQueue = new();
        private readonly object _workLock = new();

        private long _workVersion;

        private T Read<T>(Func<T> value)
        {
            lock (_closeLock)
            {
                if (_closed)
                {
                    return default;
                }

                return value();
            }
        }

        private void Write(Action action)
        {
            Write(new WorkItem(action));
        }

        private void Write(Action<bool> action)
        {
            Write(new VersionedWorkItem(action, Interlocked.Increment(ref _workVersion)));
        }

        private void Write(object workItem)
        {
            _workQueue.Push(workItem);

            lock (_workLock)
            {
                if (_workStarted is false)
                {
                    if (_workThread?.IsAlive is false)
                    {
                        _workThread.Join();
                    }

                    _workStarted = true;
                    _workThread = new Thread(Work);
                    _workThread.Start();
                }
            }
        }

        private void Work()
        {
            while (_workStarted)
            {
                var work = _workQueue.WaitAndPop();
                if (work == null || _closed)
                {
                    _workStarted = false;
                    return;
                }

                try
                {
                    if (work is VersionedWorkItem versioned)
                    {
                        versioned.Action(versioned.Version == Interlocked.Read(ref _workVersion));
                    }
                    else if (work is WorkItem item)
                    {
                        item.Action();
                    }
                }
                catch
                {
                    // Shit happens...
                }

                if (_closed)
                {
                    _workStarted = false;
                    return;
                }
            }
        }

        record WorkItem(Action Action);
        record VersionedWorkItem(Action<bool> Action, long Version);

        class WorkQueue
        {
            private readonly object _workAvailable = new();
            private readonly Queue<object> _work = new();

            public void Push(object item)
            {
                lock (_workAvailable)
                {
                    var was_empty = _work.Count == 0;

                    _work.Enqueue(item);

                    if (was_empty)
                    {
                        Monitor.Pulse(_workAvailable);
                    }
                }
            }

            public object WaitAndPop()
            {
                lock (_workAvailable)
                {
                    while (_work.Count == 0)
                    {
                        var timeout = Monitor.Wait(_workAvailable, 3000);
                        if (timeout is false)
                        {
                            return null;
                        }
                    }

                    return _work.Dequeue();
                }
            }

            public void Clear()
            {
                lock (_workAvailable)
                {
                    _work.Clear();
                }
            }
        }
    }
}
