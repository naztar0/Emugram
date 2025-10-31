//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Hosting;

namespace Telegram.Controls
{
    public partial class SettingsExpander : ContentControl
    {
        private ToggleButton ActionButton;
        private Border PopupHost;
        private ContentPresenter PopupRoot;

        public SettingsExpander()
        {
            DefaultStyleKey = typeof(SettingsExpander);
        }

        protected override void OnApplyTemplate()
        {
            ActionButton = GetTemplateChild(nameof(ActionButton)) as ToggleButton;
            ActionButton.Click += OnClick;
            ActionButton.IsChecked = IsExpanded;

            PopupHost = GetTemplateChild(nameof(PopupHost)) as Border;
            PopupRoot = GetTemplateChild(nameof(PopupRoot)) as ContentPresenter;
            PopupRoot.SizeChanged += OnSizeChanged;

            ElementCompositionPreview.SetIsTranslationEnabled(PopupRoot, true);

            OnExpandedChanged(IsExpanded, IsExpanded);
            base.OnApplyTemplate();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            PopupRoot.Margin = new Thickness(0, 0, 0, IsExpanded ? 0 : -e.NewSize.Height);
        }

        private void OnClick(object sender, RoutedEventArgs e)
        {
            IsExpanded = !IsExpanded;
        }

        public event EventHandler ExpandedChanged;

        #region IsExpanded

        public bool IsExpanded
        {
            get { return (bool)GetValue(IsExpandedProperty); }
            set { SetValue(IsExpandedProperty, value); }
        }

        public static readonly DependencyProperty IsExpandedProperty =
            DependencyProperty.Register("IsExpanded", typeof(bool), typeof(SettingsExpander), new PropertyMetadata(false, OnExpandedChanged));

        private static void OnExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((SettingsExpander)d).OnExpandedChanged((bool)e.NewValue, (bool)e.OldValue);
        }

        private bool _expanded;
        private int _tracker;

        private void OnExpandedChanged(bool newValue, bool oldValue)
        {
            if (PopupHost == null)
            {
                return;
            }

            if (newValue != oldValue)
            {
                VisualStateManager.GoToState(this, newValue ? "Expanded" : "Normal", false);
                ExpandedChanged?.Invoke(this, EventArgs.Empty);
            }

            var tracker = _tracker++;

            _expanded = newValue;
            ActionButton.IsChecked = newValue;

            PopupHost.Height = newValue ? double.NaN : 0;
            PopupRoot.Margin = new Thickness(0, 0, 0, newValue ? 0 : -PopupRoot.ActualHeight);
            PopupRoot.Visibility = Visibility.Visible;

            var visual = ElementComposition.GetElementVisual(PopupRoot);
            visual.Clip = visual.Compositor.CreateInsetClip();

            var clip = visual.Compositor.CreateScalarKeyFrameAnimation();
            clip.InsertKeyFrame(1, newValue ? 0 : 44);
            clip.Duration = Constants.FastAnimation;

            var offset = visual.Compositor.CreateScalarKeyFrameAnimation();
            offset.InsertKeyFrame(1, newValue ? 0 : -44);
            offset.Duration = Constants.FastAnimation;

            var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(1, newValue ? 1 : 0);
            opacity.Duration = Constants.FastAnimation;

            var batch = visual.Compositor.CreateScopedBatch(Windows.UI.Composition.CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                if (_tracker == tracker)
                {
                    PopupRoot.Visibility = _expanded
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            };

            visual.Clip.StartAnimation("TopInset", clip);
            visual.StartAnimation("Translation.Y", offset);
            visual.StartAnimation("Opacity", opacity);

            batch.End();
        }

        #endregion

        #region Header

        public object Header
        {
            get { return (object)GetValue(HeaderProperty); }
            set { SetValue(HeaderProperty, value); }
        }

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register("Header", typeof(object), typeof(SettingsExpander), new PropertyMetadata(null));

        #endregion
    }
}
