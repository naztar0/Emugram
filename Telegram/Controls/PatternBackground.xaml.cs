//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Numerics;
using Telegram.Common;
using Telegram.Native;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Streams;
using Telegram.Td.Api;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace Telegram.Controls
{
    // TODO: Rewrite
    public partial class ProfileHeaderPattern : Control
    {
        public ProfileHeaderPattern()
        {
            DefaultStyleKey = typeof(ProfileHeaderPattern);
        }

        protected override void OnApplyTemplate()
        {
            var animated = GetTemplateChild("Animated") as AnimatedImage;
            var layoutRoot = GetTemplateChild("LayoutRoot") as Border;

            animated.Ready += OnReady;

            var visual = ElementComposition.GetElementVisual(animated);
            var compositor = visual.Compositor;

            // Create a VisualSurface positioned at the same location as this control and feed that
            // through the color effect.
            var surfaceBrush = compositor.CreateSurfaceBrush();
            var surface = compositor.CreateVisualSurface();

            // Select the source visual and the offset/size of this control in that element's space.
            surface.SourceVisual = visual;
            surface.SourceOffset = new Vector2(0, 0);
            surface.SourceSize = new Vector2(37, 37);
            surfaceBrush.HorizontalAlignmentRatio = 0.5f;
            surfaceBrush.VerticalAlignmentRatio = 0.5f;
            surfaceBrush.Surface = surface;
            surfaceBrush.Stretch = CompositionStretch.Fill;
            surfaceBrush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
            surfaceBrush.SnapToPixels = true;

            var container = compositor.CreateContainerVisual();
            container.Size = new Vector2(1000, 320);

            var clones = Generate(0);

            for (int i = 1; i < clones.Count; i++)
            {
                Vector4 clone = clones[i];

                var redirect = compositor.CreateSpriteVisual();
                redirect.Size = new Vector2(clone.Z);
                redirect.Offset = new Vector3(clone.X, clone.Y, 0);
                redirect.CenterPoint = new Vector3(clone.Z / 2);
                redirect.Opacity = clone.W;
                redirect.Brush = surfaceBrush;

                container.Children.InsertAtTop(redirect);
            }

            ElementCompositionPreview.SetElementChildVisual(layoutRoot, container);
        }

        private void OnReady(object sender, EventArgs e)
        {
            var layoutRoot = GetTemplateChild("LayoutRoot") as Border;
            var container = ElementCompositionPreview.GetElementChildVisual(layoutRoot) as ContainerVisual;

            var scale = container.Compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0, Vector3.Zero);
            scale.InsertKeyFrame(1, Vector3.One);

            var batch = container.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            foreach (var redirect in container.Children)
            {
                redirect.StartAnimation("Scale", scale);
            }

            batch.End();
        }

        public void Update(float avatarTransitionFraction)
        {
            var layoutRoot = GetTemplateChild("LayoutRoot") as Border;
            var container = ElementCompositionPreview.GetElementChildVisual(layoutRoot) as ContainerVisual;

            var clones = Generate(avatarTransitionFraction);
            var i = 0;

            foreach (var redirect in container.Children)
            {
                Vector4 clone = clones[i++];

                redirect.Size = new Vector2(clone.Z);
                redirect.Offset = new Vector3(clone.X, clone.Y, 0);
                redirect.Opacity = clone.W;
            }
        }

        private float windowFunction(float t)
        {
            return BezierPoint.Calculate(0.6f, 0.0f, 0.4f, 1.0f, t);
        }

        private float patternScaleValueAt(float fraction, float t, bool reverse)
        {
            float windowSize = 0.8f;

            float effectiveT;
            float windowStartOffset;
            float windowEndOffset;
            if (reverse)
            {
                effectiveT = 1.0f - t;
                windowStartOffset = 1.0f;
                windowEndOffset = -windowSize;
            }
            else
            {
                effectiveT = t;
                windowStartOffset = -0.3f;
                windowEndOffset = 1.0f;
            }

            float windowPosition = (1.0f - fraction) * windowStartOffset + fraction * windowEndOffset;
            float windowT = MathF.Max(0.0f, MathF.Min(windowSize, effectiveT - windowPosition)) / windowSize;
            float localT = 1.0f - windowFunction(t: windowT);

            return localT;
        }

        private IList<Vector4> Generate(float avatarTransitionFraction)
        {
            var results = new List<Vector4>();

            var avatarPatternFrame = new Vector2(1000 - 36, 86 + 36 * 2);
            //var avatarPatternFrame = new Vector2(500, 500);

            var lokiRng = new LokiRng(seed0: 123, seed1: 0, seed2: 0);
            var numRows = 5;

            for (int row = 0; row < numRows; row++)
            {
                int avatarPatternCount = 7;
                float avatarPatternAngleSpan = MathF.PI * 2.0f / (avatarPatternCount - 1f);

                for (int i = 0; i < avatarPatternCount - 1; i++)
                {
                    float baseItemDistance;
                    float itemDistanceFraction;
                    float itemScaleFraction;
                    float itemDistance;

                    if (IsSmall)
                    {
                        baseItemDistance = 72.0f + row * 28.0f;

                        itemDistanceFraction = MathF.Max(0.0f, MathF.Min(1.0f, baseItemDistance / 140.0f));
                        itemScaleFraction = patternScaleValueAt(fraction: avatarTransitionFraction, t: itemDistanceFraction, reverse: false);
                        itemDistance = baseItemDistance * (1.0f - itemScaleFraction) + 20.0f * itemScaleFraction;
                    }
                    else
                    {
                        baseItemDistance = 100.0f + row * 40.0f;

                        itemDistanceFraction = MathF.Max(0.0f, MathF.Min(1.0f, baseItemDistance / 196.0f));
                        itemScaleFraction = patternScaleValueAt(fraction: avatarTransitionFraction, t: itemDistanceFraction, reverse: false);
                        itemDistance = baseItemDistance * (1.0f - itemScaleFraction) + 28.0f * itemScaleFraction;
                    }


                    float itemAngle = -MathF.PI * 0.5f + i * avatarPatternAngleSpan;

                    if (row % 2 != 0)
                    {
                        itemAngle += avatarPatternAngleSpan * 0.5f;
                    }

                    Vector2 itemPosition = new Vector2(avatarPatternFrame.X * 0.5f + MathF.Cos(itemAngle) * itemDistance, avatarPatternFrame.Y * 0.5f + MathF.Sin(itemAngle) * itemDistance);

                    float itemScale = 0.7f + lokiRng.Next() * (1.0f - 0.7f);
                    float itemSize = MathF.Floor((IsSmall ? 32 : 36) * itemScale);

                    results.Add(new Vector4(itemPosition.X, itemPosition.Y, itemSize, 1.0f - itemScaleFraction));
                }
            }

            return results;
        }

        public bool IsSmall { get; set; } = false;

        #region Source

        public AnimatedImageSource Source
        {
            get { return (AnimatedImageSource)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(AnimatedImageSource), typeof(ProfileHeaderPattern), new PropertyMetadata(null));

        #endregion
    }

    public partial class PatternBackground : ContentControl
    {
        public PatternBackground()
        {
            DefaultStyleKey = typeof(PatternBackground);
        }

        private AnimatedImageSource _pattern;
        private Color _centerColor;
        private Color _edgeColor;

        #region InitializeContent

        private Grid HeaderRoot;
        private Border HeaderGlow;
        private ProfileHeaderPattern Pattern;

        private bool _templateApplied;

        protected override void OnApplyTemplate()
        {
            HeaderRoot = GetTemplateChild(nameof(HeaderRoot)) as Grid;
            HeaderGlow = GetTemplateChild(nameof(HeaderGlow)) as Border;
            Pattern = GetTemplateChild(nameof(Pattern)) as ProfileHeaderPattern;

            _templateApplied = true;

            if (_pattern != null)
            {
                Update(_pattern, _centerColor, _edgeColor);
            }

            base.OnApplyTemplate();
        }

        #endregion

        public void Update(IClientService clientService, UpgradedGift gift)
        {
            var source = DelayedFileSource.FromSticker(clientService, gift.Symbol.Sticker);
            var centerColor = gift.Backdrop.Colors.CenterColor.ToColor();
            var edgeColor = gift.Backdrop.Colors.EdgeColor.ToColor();

            Update(source, centerColor, edgeColor);
        }

        public void Update(AnimatedImageSource pattern, Color centerColor, Color edgeColor)
        {
            _pattern = pattern;
            _centerColor = centerColor;
            _edgeColor = edgeColor;

            if (!_templateApplied)
            {
                return;
            }

            //Identity.Foreground = new SolidColorBrush(Colors.White);
            //BotVerified.ReplacementColor = new SolidColorBrush(Colors.White);

            HeaderRoot.RequestedTheme = ElementTheme.Dark;

            var gradient = new LinearGradientBrush();
            gradient.StartPoint = new Point(0, 0);
            gradient.EndPoint = new Point(0, 1);
            gradient.GradientStops.Add(new GradientStop
            {
                Color = centerColor,
                Offset = 0
            });

            gradient.GradientStops.Add(new GradientStop
            {
                Color = edgeColor,
                Offset = 1
            });

            HeaderRoot.Background = gradient;

            Pattern.Source = pattern;

            var compositor = BootStrapper.Current.Compositor;

            // Create a VisualSurface positioned at the same location as this control and feed that
            // through the color effect.
            var surfaceBrush = compositor.CreateSurfaceBrush();
            surfaceBrush.Stretch = CompositionStretch.None;
            var surface = compositor.CreateVisualSurface();

            // Select the source visual and the offset/size of this control in that element's space.
            surface.SourceVisual = ElementComposition.GetElementVisual(Pattern);
            surface.SourceOffset = new Vector2(0, 0);
            surface.SourceSize = new Vector2(1000, 320);
            surfaceBrush.Surface = surface;
            surfaceBrush.Stretch = CompositionStretch.None;

            CompositionBrush brush;
            var linear = compositor.CreateLinearGradientBrush();
            linear.StartPoint = new Vector2();
            linear.EndPoint = new Vector2(0, 1);
            linear.ColorStops.Add(compositor.CreateColorGradientStop(0, centerColor));
            linear.ColorStops.Add(compositor.CreateColorGradientStop(1, edgeColor));

            brush = linear;

            var radial3 = compositor.CreateRadialGradientBrush();
            //radial.CenterPoint = new Vector2(0.5f, 0.0f);
            radial3.EllipseCenter = new Vector2(0.5f, 0.3f);
            radial3.EllipseRadius = new Vector2(0.4f, 0.6f);
            radial3.ColorStops.Add(compositor.CreateColorGradientStop(0, centerColor));
            radial3.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, edgeColor));
            brush = radial3;

            var radial = compositor.CreateRadialGradientBrush();
            //radial.CenterPoint = new Vector2(0.5f, 0.0f);
            radial.EllipseCenter = new Vector2(0.5f, 0.3f);
            radial.EllipseRadius = new Vector2(0.4f, 0.6f);
            radial.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(200, 0, 0, 0)));
            radial.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, Color.FromArgb(0, 0, 0, 0)));

            var blend = new BlendEffect
            {
                Background = new CompositionEffectSourceParameter("Background"),
                Foreground = new CompositionEffectSourceParameter("Foreground"),
                Mode = BlendEffectMode.SoftLight
            };

            var borderEffectFactory = BootStrapper.Current.Compositor.CreateEffectFactory(blend);
            var borderEffectBrush = borderEffectFactory.CreateBrush();
            borderEffectBrush.SetSourceParameter("Foreground", brush);
            borderEffectBrush.SetSourceParameter("Background", radial); // compositor.CreateColorBrush(Color.FromArgb(80, 0x00, 0x00, 0x00)));

            CompositionMaskBrush maskBrush = compositor.CreateMaskBrush();
            maskBrush.Source = borderEffectBrush; // Set source to content that is to be masked 
            maskBrush.Mask = surfaceBrush; // Set mask to content that is the opacity mask 

            var visual = compositor.CreateSpriteVisual();
            visual.Size = new Vector2(1000, 320);
            visual.Offset = new Vector3(0, 0, 0);
            visual.Brush = maskBrush;

            ElementCompositionPreview.SetElementChildVisual(HeaderGlow, visual);

            var radial2 = new RadialGradientBrush();
            //radial.CenterPoint = new Vector2(0.5f, 0.0f);
            radial2.Center = new Point(0.5f, 0.3f);
            radial2.RadiusX = 0.4;
            radial2.RadiusY = 0.6;
            radial2.GradientStops.Add(new GradientStop { Color = Color.FromArgb(50, 255, 255, 255) });
            radial2.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 0.5 });

            HeaderGlow.Background = radial2;
        }

        #region Footer

        public object Footer
        {
            get { return (object)GetValue(FooterProperty); }
            set { SetValue(FooterProperty, value); }
        }

        public static readonly DependencyProperty FooterProperty =
            DependencyProperty.Register("Footer", typeof(object), typeof(PatternBackground), new PropertyMetadata(null));

        #endregion

        #region ScaleXY

        public double ScaleXY
        {
            get { return (double)GetValue(ScaleXYProperty); }
            set { SetValue(ScaleXYProperty, value); }
        }

        public static readonly DependencyProperty ScaleXYProperty =
            DependencyProperty.Register("ScaleXY", typeof(double), typeof(PatternBackground), new PropertyMetadata(0.85));

        #endregion
    }
}
