//
// Copyright Fela Ameghino & Contributors 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using Telegram.Common;
using Telegram.Services;
using Windows.Foundation;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Automation.Provider;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;

namespace Telegram.Controls
{
    public partial class PlaybackSlider : Control
    {
        private UIElement ProgressBarIndicator;

        public PlaybackSlider()
        {
            DefaultStyleKey = typeof(PlaybackSlider);
        }

        protected override void OnApplyTemplate()
        {
            ProgressBarIndicator = GetTemplateChild(nameof(ProgressBarIndicator)) as UIElement;

            UpdateValue(_position, _duration, _state);

            base.OnApplyTemplate();
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new PlaybackSliderAutomationPeer(this);
        }

        private bool _pressed;
        private bool _entered;

        public bool IsScrubbing => _pressed;

        public TimeSpan Position => _position;

        public TimeSpan Duration => _duration;

        public event TypedEventHandler<PlaybackSlider, PlaybackSliderPositionChanged> PositionChanged;

        private TimeSpan _position;
        private TimeSpan _duration;
        private PlaybackState _state;

        private CompositionPropertySet _props;

        public void UpdateValue(double position, double duration, PlaybackState state)
        {
            UpdateValue(TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(duration), state);
        }

        public void UpdateValue(TimeSpan position, TimeSpan duration, PlaybackState state)
        {
            _position = position;
            _duration = duration;
            _state = state;

            if (ProgressBarIndicator == null)
            {
                return;
            }

            var compositor = Window.Current.Compositor;

            var visual = ElementComposition.GetElementVisual(ProgressBarIndicator);
            var clip = (visual.Clip ??= compositor.CreateInsetClip()) as InsetClip;

            var step = (float)(position.TotalSeconds / duration.TotalSeconds);
            if (double.IsNaN(step))
            {
                step = 0;
            }

            if (_props == null)
            {
                _props = compositor.CreatePropertySet();
                _props.InsertScalar("Progress", 0);
            }

            if (state == PlaybackState.Playing && duration - position > TimeSpan.Zero)
            {
                var linearEasing = compositor.CreateLinearEasingFunction();
                var animation = compositor.CreateScalarKeyFrameAnimation();
                animation.Duration = duration - position;
                animation.InsertKeyFrame(0, step, linearEasing);
                animation.InsertKeyFrame(1, 1, linearEasing);

                _props.StartAnimation("Progress", animation);
            }
            else
            {
                _props.StopAnimation("Progress");
                _props.InsertScalar("Progress", step);
            }

            var progressAnimation = compositor.CreateExpressionAnimation("visual.Size.X - (_.Progress * visual.Size.X)");
            progressAnimation.SetReferenceParameter("_", _props);
            progressAnimation.SetReferenceParameter("visual", visual);

            clip.StartAnimation("RightInset", progressAnimation);
        }

        protected override void OnPointerEntered(PointerRoutedEventArgs e)
        {
            _entered = true;
            VisualStateManager.GoToState(this, "PointerOver", true);

            base.OnPointerEntered(e);
        }

        protected override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed)
            {
                _pressed = true;
                VisualStateManager.GoToState(this, "PointerOver", true);
                CapturePointer(e.Pointer);

                UpdateValue(TimeSpan.FromSeconds(point.Position.X / ActualWidth * _duration.TotalSeconds), _duration, PlaybackState.None);
            }

            base.OnPointerPressed(e);
        }

        protected override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            if (_pressed)
            {
                var point = e.GetCurrentPoint(this);
                UpdateValue(TimeSpan.FromSeconds(point.Position.X / ActualWidth * _duration.TotalSeconds), _duration, PlaybackState.None);
            }

            VisualStateManager.GoToState(this, "PointerOver", true);

            base.OnPointerMoved(e);
        }

        protected override void OnPointerExited(PointerRoutedEventArgs e)
        {
            if (_pressed)
            {
                _entered = true;
                VisualStateManager.GoToState(this, "PointerOver", true);
            }
            else
            {
                _entered = false;
                VisualStateManager.GoToState(this, "Normal", true);
            }

            base.OnPointerExited(e);
        }

        protected override void OnPointerCanceled(PointerRoutedEventArgs e)
        {
            _pressed = false;
            UpdateVisualState(e);

            base.OnPointerCanceled(e);
        }

        protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
        {
            _pressed = false;
            UpdateVisualState(e);

            base.OnPointerCaptureLost(e);
        }

        protected override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            if (_pressed)
            {
                var point = e.GetCurrentPoint(this);
                SetValue(TimeSpan.FromSeconds(point.Position.X / ActualWidth * _duration.TotalSeconds));
            }

            _pressed = false;
            ReleasePointerCapture(e.Pointer);
            UpdateVisualState(e);

            base.OnPointerReleased(e);
        }

        private void UpdateVisualState(PointerRoutedEventArgs e)
        {
            var pointer = e.GetCurrentPoint(this);
            if (pointer.Position.X >= 0 && pointer.Position.Y >= 0 && pointer.Position.X <= ActualWidth && pointer.Position.Y <= ActualHeight)
            {
                _entered = true;
                VisualStateManager.GoToState(this, "PointerOver", true);
            }
            else
            {
                _entered = false;
                VisualStateManager.GoToState(this, "Normal", true);
            }
        }

        public void SetValue(TimeSpan position)
        {
            PositionChanged?.Invoke(this, new PlaybackSliderPositionChanged(position));
        }
    }

    public partial class PlaybackSliderAutomationPeer : FrameworkElementAutomationPeer, IRangeValueProvider, IValueProvider
    {
        private readonly PlaybackSlider _owner;

        public PlaybackSliderAutomationPeer(PlaybackSlider owner)
            : base(owner)
        {
            _owner = owner;
        }

        protected override string GetClassNameCore()
        {
            return "Slider";
        }

        protected override string GetNameCore()
        {
            return "Seek";
        }

        protected override object GetPatternCore(PatternInterface patternInterface)
        {
            if (patternInterface is PatternInterface.RangeValue or PatternInterface.Value)
            {
                return this;
            }

            return base.GetPatternCore(patternInterface);
        }

        public bool IsReadOnly => false;

        public double LargeChange => 1;

        public double SmallChange => 1;

        public double Minimum => 0;

        public double Maximum => _owner.Duration.TotalSeconds;

        public double Value => _owner.Position.TotalSeconds;

        string IValueProvider.Value => _owner.Position.ToDuration();

        public void SetValue(double value)
        {
            throw new NotImplementedException();
        }

        public void SetValue(string value)
        {
            throw new NotImplementedException();
        }
    }
}
