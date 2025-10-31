using System;
using System.Collections.Generic;
using Telegram.Controls;
using Telegram.Native.Controls;
using Telegram.Native.Media;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibVLCSharp.Platforms.Windows
{
    /// <summary>
     /// Provides data for the <see cref="VideoView{TInitializedEventArgs}.Initialized"/> event.
     /// </summary>
    public partial class VideoViewInitializedEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of <see cref="VideoViewInitializedEventArgs"/> class
        /// </summary>
        /// <param name="swapChainOptions">swap chain parameters</param>
        public VideoViewInitializedEventArgs(AsyncMediaPlayerSwapChain swapChain)
        {
            SwapChain = swapChain;
        }

        public AsyncMediaPlayerSwapChain SwapChain { get; }
    }

    /// <summary>
    /// VideoView base class for the UWP platform
    /// </summary>
    [TemplatePart(Name = PartSwapChainPanelName, Type = typeof(SwapChainPanel))]
    public class VideoView : ControlEx
    {
        private const string PartSwapChainPanelName = "SwapChainPanel";

        private AsyncMediaPlayerSwapChain _context = new(false);
        private SwapChainPanel _panel;
        private bool _initialized;

        /// <summary>
        /// The constructor
        /// </summary>
        public VideoView()
        {
            DefaultStyleKey = typeof(VideoView);
        }

        public event EventHandler<VideoViewInitializedEventArgs> Initialized;

        public bool IsUnloadedExpected { get; set; }

        protected override void OnLoaded()
        {
            IsUnloadedExpected = false;
        }

        protected override void OnUnloaded()
        {
            if (IsUnloadedExpected)
            {
                return;
            }

            if (_initialized)
            {
                _context.Detach(_panel);
            }
            else
            {
                _context.Destroy();
            }
        }

        /// <summary>
        /// Invoked whenever application code or internal processes (such as a rebuilding layout pass) call ApplyTemplate. 
        /// In simplest terms, this means the method is called just before a UI element displays in your app.
        /// Override this method to influence the default post-template logic of a class.
        /// </summary>
        protected override void OnApplyTemplate()
        {
            _panel = (SwapChainPanel)GetTemplateChild(PartSwapChainPanelName);

#if !WINUI
            if (DesignMode.DesignModeEnabled)
                return;
#endif
            _context.Destroy();
            _context.Attach(_panel, false);

            _panel.SizeChanged += OnSizeChanged;
            _panel.CompositionScaleChanged += OnCompositionScaleChanged;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsDisconnected && !IsUnloadedExpected)
            {
                if (_initialized)
                {
                    _context.Detach(_panel);
                }
                else
                {
                    _context.Destroy();
                }
            }
            else if (_context.IsLoaded)
            {
                _context.UpdateSize();
            }
            else if (_context.Create(false))
            {
                _initialized = true;
                Initialized?.Invoke(this, new VideoViewInitializedEventArgs(_context));
            }
        }

        private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
        {
            if (_context.IsLoaded)
            {
                _context.UpdateScale();
            }
        }

        public void Clear()
        {
            _context.Clear();
        }

        public AsyncMediaPlayerSwapChain SwapChain => _context;
    }
}
