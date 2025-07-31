//
// Copyright (c) Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Telegram.Common;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.Views;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Telegram.Controls
{
    public sealed partial class PlaybackOverlay : UserControlEx
    {
        private bool _presenterPressed;
        private Vector2 _presenterDelta;
        private Vector2 _presenterOffset = Vector2.One;
        private readonly Visual _presenter;


        public PlaybackOverlay()
        {
            InitializeComponent();

            _presenter = ElementComposition.GetElementVisual(Presenter);

            Presenter.PointerPressed += Presenter_PointerPressed;
            Presenter.PointerMoved += Presenter_PointerMoved;
            Presenter.PointerReleased += Presenter_PointerReleased;

            Connected += OnConnected;
            Disconnected += OnDisconnected;
        }

        private void OnConnected(object sender, RoutedEventArgs e)
        {
            TypeResolver.Current.Playback.SourceChanged += OnSourceChanged;
        }

        private void OnDisconnected(object sender, RoutedEventArgs e)
        {
            TypeResolver.Current.Playback.SourceChanged -= OnSourceChanged;
        }

        private void OnSourceChanged(IPlaybackService sender, object args)
        {
            this.BeginOnUIThread(OnSourceChanged);
        }

        private void OnSourceChanged()
        {
            ShowHide(TypeResolver.Current.Playback.CurrentItem?.Content is MessageVideoNote);
        }

        private bool _collapsed;

        private void ShowHide(bool show)
        {
            if (_collapsed != show)
            {
                return;
            }

            Grid.SetRow(this, 0);
            Grid.SetRowSpan(this, 3);

            _collapsed = !show;
            Visibility = Visibility.Visible;

            ElementCompositionPreview.SetIsTranslationEnabled(Presenter, true);

            var visual = ElementComposition.GetElementVisual(Presenter);
            var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                Visibility = _collapsed
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            };

            var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(show ? 0 : 1, _presenterOffset.Y == 0 ? -Presenter.ActualSize.Y - 48 : Presenter.ActualSize.Y + 64);
            animation.InsertKeyFrame(show ? 1 : 0, 0);

            visual.StartAnimation("Translation.Y", animation);
            batch.End();

            if (show)
            {
                _panel = new SwapChainPanel();
                _panel.SizeChanged += _panel_SizeChanged;
                _panel.CompositionScaleChanged += _panel_CompositionScaleChanged;

                Presenter.Child = _panel;
                ((PlaybackService)TypeResolver.Current.Playback).Attach(_panel);
            }
            else if (_panel != null)
            {
                Presenter.Child = null;
                ((PlaybackService)TypeResolver.Current.Playback).Detach(_panel);

                _panel.SizeChanged -= _panel_SizeChanged;
                _panel.CompositionScaleChanged -= _panel_CompositionScaleChanged;
                _panel = null;
            }
        }

        private void _panel_CompositionScaleChanged(SwapChainPanel sender, object args)
        {
            ((PlaybackService)TypeResolver.Current.Playback).UpdateScale();
        }

        private void _panel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ((PlaybackService)TypeResolver.Current.Playback).UpdateSize();
        }

        private SwapChainPanel _panel;

        #region Interactions

        private void Presenter_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _presenterPressed = true;
            Presenter.CapturePointer(e.Pointer);

            var pointer = e.GetCurrentPoint(this);
            var point = pointer.Position.ToVector2();
            _presenterDelta = new Vector2(_presenter.Offset.X - point.X, _presenter.Offset.Y - point.Y);
        }

        private void Presenter_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_presenterPressed)
            {
                return;
            }

            var pointer = e.GetCurrentPoint(this);
            var delta = _presenterDelta + pointer.Position.ToVector2();

            _presenter.Offset = new Vector3(delta, 0);
        }

        private void Presenter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _presenterPressed = false;
            Presenter.ReleasePointerCapture(e.Pointer);

            var pointer = e.GetCurrentPoint(this);
            var offset = _presenterDelta + pointer.Position.ToVector2();

            // Padding maybe
            var p = 8;

            var w = (float)ActualWidth - p * 2;
            var h = (float)ActualHeight - p * 2;

            _presenterOffset = new Vector2((offset.X - p) / w, (offset.Y - p) / h);

            CheckConstraints();
        }

        private void CheckConstraints()
        {
            if (Presenter.Visibility == Visibility.Collapsed)
            {
                return;
            }

            var w = (float)(RootGrid.ActualWidth - Presenter.ActualWidth);
            var h = (float)(RootGrid.ActualHeight - Presenter.ActualHeight);

            if (w == 0 || h == 0)
            {
                return;
            }

            var x = MathF.Round(_presenterOffset.X + (Presenter.ActualSize.X / 2 / w));
            var y = MathF.Round(_presenterOffset.Y + (Presenter.ActualSize.Y / 2 / h));

            var x1 = Math.Max(0, Math.Min(w, x * w));
            var y1 = Math.Max(0, Math.Min(h, y * h));

            //var x2 = x1;
            //var y2 = y1;

            //if (Math.Min(x1, w - x2) < Math.Min(y1, h - y2))
            //{
            //    if (x1 < w - x2)
            //    {
            //        x1 = p;
            //    }
            //    else
            //    {
            //        x1 = w - p;
            //    }
            //}
            //else
            //{
            //    if (y1 < h - y2)
            //    {
            //        y1 = p;
            //    }
            //    else
            //    {
            //        y1 = h - p;
            //    }
            //}

            //var bx1 = (w - 240) / 2;
            //var bx2 = bx1 + 240;

            //if (y2 > h / 2 && ((x1 >= bx1 && x1 <= bx2) || (x2 >= bx1 && x2 <= bx2)))
            //{
            //    y1 = h - 72 - p;
            //}

            if (x1 != _presenter.Offset.X || y1 != _presenter.Offset.Y)
            {
                var anim = BootStrapper.Current.Compositor.CreateVector3KeyFrameAnimation();
                anim.InsertKeyFrame(0, _presenter.Offset);
                anim.InsertKeyFrame(1, new Vector3(x1, y1, 0));

                var batch = BootStrapper.Current.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                batch.Completed += (s, args) =>
                {
                    _presenter.Offset = new Vector3(x1, y1, 0);
                    _presenterOffset = new Vector2(x1 / w, y1 / h);
                };

                _presenter.StartAnimation("Offset", anim);
                batch.End();
            }
        }

        #endregion

    }
}
