//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using LibVLCSharp.Shared;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.Runtime.InteropServices;
using Windows.UI.Xaml.Controls;

namespace Telegram.Common
{
    // TODO: extracted from VideoViewBase, make it work properly
    public partial class AsyncMediaPlayerSwapChain
    {
        private SwapChainPanel _panel;
        private SharpDX.Direct3D11.Device _d3D11Device;
        private SharpDX.DXGI.Device3 _device3;
        private SwapChain2 _swapChain2;
        private SwapChain1 _swapChain;
        private DeviceContext _deviceContext;
        private bool _loaded;

        public AsyncMediaPlayerSwapChain()
        {
            Create();
        }

        public void Clear()
        {
            if (_swapChain != null)
            {
                try
                {
                    using var backBuffer = _swapChain.GetBackBuffer<Texture2D>(0);
                    using var target = new RenderTargetView(_d3D11Device, backBuffer);

                    _deviceContext.ClearRenderTargetView(target, new RawColor4(0, 0, 0, 0));
                    _swapChain.Present(0, PresentFlags.None);
                }
                catch
                {
                    // All the remote procedure calls must be wrapped in a try-catch block
                }
            }
        }

        /// <summary>
        /// Gets the swapchain parameters to pass to the <see cref="LibVLC"/> constructor.
        /// If you don't pass them to the <see cref="LibVLC"/> constructor, the video won't
        /// be displayed in your application.
        /// Calling this property will throw an <see cref="InvalidOperationException"/> if the VideoView is not yet full Loaded.
        /// </summary>
        /// <returns>The list of arguments to be given to the <see cref="LibVLC"/> constructor.</returns>
        public string[] SwapChainOptions
        {
            get
            {
                if (!_loaded)
                {
                    throw new InvalidOperationException("You must wait for the VideoView to be loaded before calling GetSwapChainOptions()");
                }

                _deviceContext = _d3D11Device!.ImmediateContext;

                return new string[]
                {
                    $"--winrt-d3dcontext=0x{_deviceContext.NativePointer.ToString("x")}",
                    $"--winrt-swapchain=0x{_swapChain!.NativePointer.ToString("x")}"
                };
            }
        }

        /// <summary>
        /// Initializes the SwapChain for use with LibVLC
        /// </summary>
        public void Create()
        {
            //// Do not create the swapchain when the VideoView is collapsed.
            //if (_panel == null || _panel.ActualHeight == 0)
            //{
            //    return;
            //}

            //if (IsDisconnected)
            //{
            //    DestroySwapChain();
            //    return;
            //}

            // TODO: this whole code and player doesn't support device loss
            // This means that device loss CAN'T be recovered without creating a new
            // LibVLC/MediaPlayer instance and everything else associated.
            SharpDX.DXGI.Factory2 dxgiFactory = null;
            try
            {
                var creationFlags =
                    DeviceCreationFlags.BgraSupport /*| DeviceCreationFlags.VideoSupport*/;

                if (Telegram.Constants.DEBUG)
                {
                    creationFlags |= DeviceCreationFlags.Debug;

                    try
                    {
                        dxgiFactory = new SharpDX.DXGI.Factory2(true);
                    }
                    catch (SharpDXException)
                    {
                        dxgiFactory = new SharpDX.DXGI.Factory2(false);
                    }
                }
                else
                {
                    dxgiFactory = new SharpDX.DXGI.Factory2(false);
                }

                _d3D11Device = null;
                int i_adapter = 0;
                int adapterCount = dxgiFactory.GetAdapterCount();

                while (_d3D11Device == null)
                {
                    if (i_adapter == adapterCount)
                    {
                        if (creationFlags.HasFlag(DeviceCreationFlags.VideoSupport))
                        {
                            i_adapter = 0;
                            creationFlags &= ~DeviceCreationFlags.VideoSupport;
                            continue;
                        }
                        else
                        {
                            break;
                        }
                    }

                    try
                    {
                        var adapter = dxgiFactory.GetAdapter(i_adapter++);
                        _d3D11Device = new SharpDX.Direct3D11.Device(adapter, creationFlags);
                        adapter.Dispose();
                        adapter = null;
                        break;
                    }
                    catch (SharpDXException)
                    {
                    }
                }

                if (_d3D11Device is null)
                {
                    throw new InvalidOperationException("Could not create Direct3D11 device : No compatible adapter found.");
                }

                var device = _d3D11Device.QueryInterface<SharpDX.DXGI.Device1>();

                //Create the swapchain
                var swapChainDescription = new SharpDX.DXGI.SwapChainDescription1
                {
                    // Placeholder size
                    Width = 320,
                    Height = 240,
                    Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription =
                    {
                        Count = 1,
                        Quality = 0
                    },
                    Usage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    SwapEffect = SwapEffect.FlipSequential,
                    Flags = SwapChainFlags.None,
                    AlphaMode = AlphaMode.Premultiplied
                };

                _swapChain = new SharpDX.DXGI.SwapChain1(dxgiFactory, _d3D11Device, ref swapChainDescription);
                dxgiFactory.Dispose();
                dxgiFactory = null;

                device.MaximumFrameLatency = 1;

                if (_panel != null)
                {
                    OnAttach(null, _panel);
                }

                // This is necessary so we can call Trim() on suspend
                _device3 = device.QueryInterface<SharpDX.DXGI.Device3>();
                if (_device3 == null)
                {
                    throw new InvalidOperationException("Failed to query interface \"Device3\"");
                }

                device.Dispose();
                device = null;

                _swapChain2 = _swapChain.QueryInterface<SharpDX.DXGI.SwapChain2>();
                if (_swapChain2 == null)
                {
                    throw new InvalidOperationException("Failed to query interface \"SwapChain2\"");
                }

                UpdateScale();
                UpdateSize();
                _loaded = true;
            }
            catch (Exception ex)
            {
                Destroy();
                Telegram.Logger.Error(ex.ToString());
            }
        }

        /// <summary>
        /// Destroys the SwapChain and all related instances.
        /// </summary>
        public void Destroy()
        {
            _swapChain2?.Dispose();
            _swapChain2 = null;

            _device3?.Dispose();
            _device3 = null;

            if (_panel != null)
            {
                Detach(_panel);
            }

            _swapChain?.Dispose();
            _swapChain = null;

            _deviceContext?.Dispose();
            _deviceContext = null;

            _d3D11Device?.Dispose();
            _d3D11Device = null;

            _loaded = false;
        }

        public void Attach(SwapChainPanel panel)
        {
            OnAttach(_panel, _panel = panel);
        }

        private void OnAttach(SwapChainPanel oldPanel, SwapChainPanel newPanel)
        {
            if (oldPanel != null)
            {
                Detach(oldPanel);
            }

            if (_loaded)
            {
                using (var panelNative = ComObject.As<ISwapChainPanelNative>(newPanel))
                {
                    panelNative.SwapChain = _swapChain;
                }

                UpdateScale();
                UpdateSize();
            }
        }

        public void Detach(SwapChainPanel panel)
        {
            using (var panelNative = ComObject.As<ISwapChainPanelNative>(panel))
            {
                panelNative.SwapChain = null;
            }
        }

        readonly Guid SWAPCHAIN_WIDTH = new Guid(0xf1b59347, 0x1643, 0x411a, 0xad, 0x6b, 0xc7, 0x80, 0x17, 0x7a, 0x6, 0xb6);
        readonly Guid SWAPCHAIN_HEIGHT = new Guid(0x6ea976a0, 0x9d60, 0x4bb7, 0xa5, 0xa9, 0x7d, 0xd1, 0x18, 0x7f, 0xc9, 0xbd);

        /// <summary>
        /// Associates width/height private data into the SwapChain, so that VLC knows at which size to render its video.
        /// </summary>
        public void UpdateSize()
        {
            if (_panel is null && _swapChain is not null && !_swapChain.IsDisposed)
            {
                UpdateSize(320, 240);
                return;
            }

            if (_panel is null || _swapChain is null || _swapChain.IsDisposed)
                return;

            var w = (int)(_panel.ActualWidth * _panel.CompositionScaleX);
            var h = (int)(_panel.ActualHeight * _panel.CompositionScaleY);

            UpdateSize(w, h);
        }

        private void UpdateSize(int w, int h)
        {
            var width = IntPtr.Zero;
            var height = IntPtr.Zero;

            try
            {
                width = Marshal.AllocHGlobal(sizeof(int));
                height = Marshal.AllocHGlobal(sizeof(int));

                Marshal.WriteInt32(width, w);
                Marshal.WriteInt32(height, h);

                _swapChain.SetPrivateData(SWAPCHAIN_WIDTH, sizeof(int), width);
                _swapChain.SetPrivateData(SWAPCHAIN_HEIGHT, sizeof(int), height);
            }
            finally
            {
                Marshal.FreeHGlobal(width);
                Marshal.FreeHGlobal(height);
            }
        }

        /// <summary>
        /// Updates the MatrixTransform of the SwapChain.
        /// </summary>
        public void UpdateScale()
        {
            if (_panel is null) return;

            // TODO: experiment
            // CompositionScale changes when che SwapChainPanel is inside a ScrollViewer and ZoomLevel changes.
            // We don't want this to happen, so let's try to use XamlRoot.RasterizationScale instead.

            float scaleX;
            float scaleY;

            if (_panel.XamlRoot != null)
            {
                scaleX = (float)_panel.XamlRoot.RasterizationScale;
                scaleY = (float)_panel.XamlRoot.RasterizationScale;
            }
            else
            {
                scaleX = _panel.CompositionScaleX;
                scaleY = _panel.CompositionScaleY;
            }

            _swapChain2!.MatrixTransform = new RawMatrix3x2
            {
                M11 = 1.0f / scaleX,
                M22 = 1.0f / scaleY
            };
        }
    }
}
