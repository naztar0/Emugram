//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Telegram.Navigation;
using Telegram.Td.Api;
using Windows.UI;
using Windows.UI.Composition;

namespace Telegram.Common
{
    public static class CompositionPathParser
    {
        public static CompositionPath Parse(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return null;

            var segments = ParseSegments(data);
            if (segments?.Count > 0)
            {
                using var builder = new CanvasPathBuilder(null);
                RenderPath(segments, builder);
                return new CompositionPath(CanvasGeometry.CreatePath(builder));
            }

            return null;
        }

        public static CanvasGeometry Parse(ICanvasResourceCreator resourceCreator, string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return null;

            var segments = ParseSegments(data);
            if (segments?.Count > 0)
            {
                using var builder = new CanvasPathBuilder(resourceCreator);
                RenderPath(segments, builder);
                return CanvasGeometry.CreatePath(builder);
            }

            return null;
        }

        public static CompositionAnimation ParseThumbnail(float width, float height, IList<ClosedVectorPath> contours, out ShapeVisual visual, bool animated = true)
        {
            CompositionPath path = contours?.Count > 0
                ? PlaceholderHelper.Foreground.GetOutline(contours)
                : new CompositionPath(CanvasGeometry.CreateRoundedRectangle(null, 0, 0, width, height, 80, 80));

            return CreateThumbnail(width, height, path, out visual, animated);
        }

        public static CompositionAnimation CreateThumbnail(float width, float height, float cornerRadius, out ShapeVisual visual, bool animated = true)
        {
            var path = new CompositionPath(CanvasGeometry.CreateRoundedRectangle(null, 0, 0, width, height, cornerRadius, cornerRadius));
            return CreateThumbnail(width, height, path, out visual, animated);
        }

        public static CompositionAnimation CreateThumbnail(float width, float height, CompositionPath path, out ShapeVisual visual, bool animated = true)
        {
            var backgroundColor = Color.FromArgb(0x33, 0x7A, 0x8A, 0x96);
            var compositor = BootStrapper.Current.Compositor;

            var background = compositor.CreatePathGeometry(path);
            var backgroundShape = compositor.CreateSpriteShape(background);
            backgroundShape.FillBrush = compositor.CreateColorBrush(backgroundColor);

            visual = compositor.CreateShapeVisual();
            visual.Shapes.Add(backgroundShape);
            visual.RelativeSizeAdjustment = Vector2.One;
            visual.ViewBox = compositor.CreateViewBox();
            visual.ViewBox.Size = new Vector2(width, height);
            visual.ViewBox.Stretch = CompositionStretch.Uniform;

            return animated ? CreateAnimation(compositor, background, visual, width) : null;
        }

        private static CompositionAnimation CreateAnimation(Compositor compositor, CompositionPathGeometry background, ShapeVisual visual, float width)
        {
            var transparent = Color.FromArgb(0x00, 0x7A, 0x8A, 0x96);
            var foregroundColor = Color.FromArgb(0x33, 0x7A, 0x8A, 0x96);

            var gradient = compositor.CreateLinearGradientBrush();
            gradient.StartPoint = Vector2.Zero;
            gradient.EndPoint = Vector2.UnitX;

            gradient.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, transparent));
            gradient.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, foregroundColor));
            gradient.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, transparent));

            var foregroundShape = compositor.CreateSpriteShape(background);
            foregroundShape.FillBrush = gradient;
            visual.Shapes.Add(foregroundShape);

            var animation = compositor.CreateVector2KeyFrameAnimation();
            animation.InsertKeyFrame(0, new Vector2(-width, 0));
            animation.InsertKeyFrame(1, new Vector2(width, 0));
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            animation.Duration = TimeSpan.FromSeconds(1);

            gradient.StartAnimation("Offset", animation);
            return animation;
        }

        private static List<PathSegment> ParseSegments(string data)
        {
            var reader = new PathDataReader(data);
            return reader.Read();
        }

        private static void RenderPath(IList<PathSegment> segments, CanvasPathBuilder builder)
        {
            var pathRenderer = new PathRenderer(builder);
            pathRenderer.Render(segments);
        }

        public readonly struct PathSegment : IEquatable<PathSegment>
        {
            public enum SegmentType : byte
            {
                M, L, C, Q, A, z, H, V, S, T,
                m, l, c, q, a, h, v, s, t,
                E, e
            }

            public readonly SegmentType Type;
            public readonly float[] Data;

            public PathSegment(SegmentType type, float[] data = null)
            {
                Type = type;
                Data = data ?? Array.Empty<float>();
            }

            public bool IsAbsolute => Type switch
            {
                SegmentType.M or SegmentType.L or SegmentType.H or SegmentType.V or
                SegmentType.C or SegmentType.S or SegmentType.Q or SegmentType.T or
                SegmentType.A or SegmentType.E => true,
                _ => false
            };

            public bool Equals(PathSegment other) => Type == other.Type && Data.AsSpan().SequenceEqual(other.Data);
            public override bool Equals(object obj) => obj is PathSegment other && Equals(other);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(Type);
                hash.Add(Data.Length);

                foreach (var value in Data)
                {
                    hash.Add(value);
                }

                return hash.ToHashCode();
            }
        }

        private sealed class PathRenderer
        {
            private readonly CanvasPathBuilder _builder;
            private Vector2? _currentPoint;
            private Vector2? _cubicPoint;
            private Vector2? _initialPoint;

            public PathRenderer(CanvasPathBuilder builder)
            {
                _builder = builder;
            }

            public void Render(IList<PathSegment> segments)
            {
                foreach (var segment in segments)
                {
                    RenderSegment(segment);
                }
            }

            private void RenderSegment(PathSegment segment)
            {
                var data = segment.Data.AsSpan();

                switch (segment.Type)
                {
                    case PathSegment.SegmentType.M:
                        MoveTo(data[0], data[1]);
                        data = data[2..];
                        while (data.Length >= 2)
                        {
                            LineTo(data[0], data[1]);
                            data = data[2..];
                        }
                        break;

                    case PathSegment.SegmentType.m:
                        MoveToRelative(data[0], data[1]);
                        data = data[2..];
                        while (data.Length >= 2)
                        {
                            LineToRelative(data[0], data[1]);
                            data = data[2..];
                        }
                        break;

                    case PathSegment.SegmentType.L:
                        while (data.Length >= 2)
                        {
                            LineTo(data[0], data[1]);
                            data = data[2..];
                        }
                        break;

                    case PathSegment.SegmentType.l:
                        while (data.Length >= 2)
                        {
                            LineToRelative(data[0], data[1]);
                            data = data[2..];
                        }
                        break;

                    case PathSegment.SegmentType.H:
                        HorizontalLineTo(data[0]);
                        break;

                    case PathSegment.SegmentType.h:
                        HorizontalLineToRelative(data[0]);
                        break;

                    case PathSegment.SegmentType.V:
                        VerticalLineTo(data[0]);
                        break;

                    case PathSegment.SegmentType.v:
                        VerticalLineToRelative(data[0]);
                        break;

                    case PathSegment.SegmentType.C:
                        while (data.Length >= 6)
                        {
                            CubicBezierTo(data[0], data[1], data[2], data[3], data[4], data[5]);
                            data = data[6..];
                        }
                        break;

                    case PathSegment.SegmentType.c:
                        while (data.Length >= 6)
                        {
                            CubicBezierToRelative(data[0], data[1], data[2], data[3], data[4], data[5]);
                            data = data[6..];
                        }
                        break;

                    case PathSegment.SegmentType.S:
                        while (data.Length >= 4)
                        {
                            SmoothCubicBezierTo(data[0], data[1], data[2], data[3]);
                            data = data[4..];
                        }
                        break;

                    case PathSegment.SegmentType.s:
                        while (data.Length >= 4)
                        {
                            SmoothCubicBezierToRelative(data[0], data[1], data[2], data[3]);
                            data = data[4..];
                        }
                        break;

                    case PathSegment.SegmentType.z:
                        ClosePath();
                        break;
                }
            }

            #region Movement Commands

            private void MoveTo(float x, float y)
            {
                var point = new Vector2(x, y);
                _builder.BeginFigure(point);
                SetInitialPoint(point);
            }

            private void MoveToRelative(float x, float y)
            {
                if (_currentPoint is Vector2 current)
                {
                    var next = new Vector2(x + current.X, y + current.Y);
                    _builder.BeginFigure(next);
                    SetInitialPoint(next);
                }
                else
                {
                    MoveTo(x, y);
                }
            }

            private void LineTo(float x, float y)
            {
                var point = new Vector2(x, y);
                _builder.AddLine(point);
                SetCurrentPoint(point);
            }

            private void LineToRelative(float x, float y)
            {
                if (_currentPoint is Vector2 current)
                {
                    LineTo(x + current.X, y + current.Y);
                }
                else
                {
                    LineTo(x, y);
                }
            }

            private void HorizontalLineTo(float x)
            {
                if (_currentPoint is Vector2 current)
                {
                    LineTo(x, current.Y);
                }
            }

            private void HorizontalLineToRelative(float x)
            {
                if (_currentPoint is Vector2 current)
                {
                    LineTo(x + current.X, current.Y);
                }
            }

            private void VerticalLineTo(float y)
            {
                if (_currentPoint is Vector2 current)
                {
                    LineTo(current.X, y);
                }
            }

            private void VerticalLineToRelative(float y)
            {
                if (_currentPoint is Vector2 current)
                {
                    LineTo(current.X, y + current.Y);
                }
            }

            #endregion

            #region Curve Commands

            private void CubicBezierTo(float x1, float y1, float x2, float y2, float x, float y)
            {
                var endPoint = new Vector2(x, y);
                var controlPoint1 = new Vector2(x1, y1);
                var controlPoint2 = new Vector2(x2, y2);
                _builder.AddCubicBezier(controlPoint1, controlPoint2, endPoint);
                SetCubicPoint(endPoint, controlPoint2);
            }

            private void CubicBezierToRelative(float x1, float y1, float x2, float y2, float x, float y)
            {
                if (_currentPoint is Vector2 current)
                {
                    var endPoint = new Vector2(x + current.X, y + current.Y);
                    var controlPoint1 = new Vector2(x1 + current.X, y1 + current.Y);
                    var controlPoint2 = new Vector2(x2 + current.X, y2 + current.Y);
                    _builder.AddCubicBezier(controlPoint1, controlPoint2, endPoint);
                    SetCubicPoint(endPoint, controlPoint2);
                }
            }

            private void SmoothCubicBezierTo(float x2, float y2, float x, float y)
            {
                if (_currentPoint is Vector2 current)
                {
                    var nextCubic = new Vector2(x2, y2);
                    var next = new Vector2(x, y);
                    var controlPoint1 = _cubicPoint is Vector2 cubic
                        ? new Vector2(2 * current.X - cubic.X, 2 * current.Y - cubic.Y)
                        : current;

                    _builder.AddCubicBezier(controlPoint1, nextCubic, next);
                    SetCubicPoint(next, nextCubic);
                }
            }

            private void SmoothCubicBezierToRelative(float x2, float y2, float x, float y)
            {
                if (_currentPoint is Vector2 current)
                {
                    var nextCubic = new Vector2(x2 + current.X, y2 + current.Y);
                    var next = new Vector2(x + current.X, y + current.Y);
                    var controlPoint1 = _cubicPoint is Vector2 cubic
                        ? new Vector2(2 * current.X - cubic.X, 2 * current.Y - cubic.Y)
                        : current;

                    _builder.AddCubicBezier(controlPoint1, nextCubic, next);
                    SetCubicPoint(next, nextCubic);
                }
            }

            #endregion

            private void ClosePath()
            {
                _builder.EndFigure(CanvasFigureLoop.Closed);
            }

            private void SetCubicPoint(Vector2 point, Vector2 cubic)
            {
                _currentPoint = point;
                _cubicPoint = cubic;
            }

            private void SetInitialPoint(Vector2 point)
            {
                SetCurrentPoint(point);
                _initialPoint = point;
            }

            private void SetCurrentPoint(Vector2 point)
            {
                _currentPoint = point;
                _cubicPoint = null;
            }
        }

        private sealed class PathDataReader
        {
            private static readonly HashSet<char> _whitespace = new() { '\n', '\r', '\t', ' ', ',' };
            private static readonly Dictionary<char, PathSegment.SegmentType> _segmentTypeMap = new()
            {
                ['M'] = PathSegment.SegmentType.M,
                ['m'] = PathSegment.SegmentType.m,
                ['L'] = PathSegment.SegmentType.L,
                ['l'] = PathSegment.SegmentType.l,
                ['C'] = PathSegment.SegmentType.C,
                ['c'] = PathSegment.SegmentType.c,
                ['Q'] = PathSegment.SegmentType.Q,
                ['q'] = PathSegment.SegmentType.q,
                ['A'] = PathSegment.SegmentType.A,
                ['a'] = PathSegment.SegmentType.a,
                ['z'] = PathSegment.SegmentType.z,
                ['Z'] = PathSegment.SegmentType.z,
                ['H'] = PathSegment.SegmentType.H,
                ['h'] = PathSegment.SegmentType.h,
                ['V'] = PathSegment.SegmentType.V,
                ['v'] = PathSegment.SegmentType.v,
                ['S'] = PathSegment.SegmentType.S,
                ['s'] = PathSegment.SegmentType.s,
                ['T'] = PathSegment.SegmentType.T,
                ['t'] = PathSegment.SegmentType.t
            };

            private static readonly Dictionary<PathSegment.SegmentType, int> _argumentCounts = new()
            {
                [PathSegment.SegmentType.H] = 1,
                [PathSegment.SegmentType.h] = 1,
                [PathSegment.SegmentType.V] = 1,
                [PathSegment.SegmentType.v] = 1,
                [PathSegment.SegmentType.M] = 2,
                [PathSegment.SegmentType.m] = 2,
                [PathSegment.SegmentType.L] = 2,
                [PathSegment.SegmentType.l] = 2,
                [PathSegment.SegmentType.T] = 2,
                [PathSegment.SegmentType.t] = 2,
                [PathSegment.SegmentType.S] = 4,
                [PathSegment.SegmentType.s] = 4,
                [PathSegment.SegmentType.Q] = 4,
                [PathSegment.SegmentType.q] = 4,
                [PathSegment.SegmentType.C] = 6,
                [PathSegment.SegmentType.c] = 6,
                [PathSegment.SegmentType.A] = 7,
                [PathSegment.SegmentType.a] = 7
            };

            private readonly string _input;
            private int _position;

            public PathDataReader(string input)
            {
                _input = input ?? throw new ArgumentNullException(nameof(input));
            }

            public List<PathSegment> Read()
            {
                var segments = new List<PathSegment>();

                while (_position < _input.Length)
                {
                    var segmentGroup = ReadSegmentGroup();
                    if (segmentGroup != null)
                    {
                        segments.AddRange(segmentGroup);
                    }
                    else
                    {
                        break;
                    }
                }

                return segments;
            }

            private List<PathSegment> ReadSegmentGroup()
            {
                var segmentType = ReadSegmentType();
                if (!segmentType.HasValue)
                    return null;

                var type = segmentType.Value;
                var argCount = _argumentCounts.GetValueOrDefault(type, 0);

                if (argCount == 0)
                {
                    return new List<PathSegment> { new(type) };
                }

                var data = type is PathSegment.SegmentType.A or PathSegment.SegmentType.a
                    ? ReadArcData()
                    : ReadNumericData();

                return CreateSegments(type, data, argCount);
            }

            private List<PathSegment> CreateSegments(PathSegment.SegmentType type, List<float> data, int argCount)
            {
                var result = new List<PathSegment>();
                var isFirstSegment = true;

                for (int i = 0; i < data.Count; i += argCount)
                {
                    if (i + argCount > data.Count)
                        break;

                    var currentType = type;
                    if (!isFirstSegment)
                    {
                        currentType = type switch
                        {
                            PathSegment.SegmentType.M => PathSegment.SegmentType.L,
                            PathSegment.SegmentType.m => PathSegment.SegmentType.l,
                            _ => type
                        };
                    }

                    var segmentData = new float[argCount];
                    Array.Copy(data.ToArray(), i, segmentData, 0, argCount);
                    result.Add(new PathSegment(currentType, segmentData));
                    isFirstSegment = false;
                }

                return result;
            }

            private List<float> ReadNumericData()
            {
                var data = new List<float>();

                while (true)
                {
                    SkipWhitespace();
                    var value = ReadNumber();
                    if (value.HasValue)
                    {
                        data.Add(value.Value);
                    }
                    else
                    {
                        break;
                    }
                }

                return data;
            }

            private List<float> ReadArcData()
            {
                var data = new List<float>();
                var argIndex = 0;
                const int arcArgCount = 7;

                while (true)
                {
                    SkipWhitespace();
                    var argPosition = argIndex % arcArgCount;

                    float? value = argPosition is 3 or 4 ? ReadFlag() : ReadNumber();

                    if (value.HasValue)
                    {
                        data.Add(value.Value);
                        argIndex++;
                    }
                    else
                    {
                        break;
                    }
                }

                return data;
            }

            private PathSegment.SegmentType? ReadSegmentType()
            {
                SkipWhitespace();

                if (_position < _input.Length && _segmentTypeMap.TryGetValue(_input[_position], out var type))
                {
                    _position++;
                    return type;
                }

                return null;
            }

            private float? ReadNumber()
            {
                if (_position >= _input.Length)
                    return null;

                var start = _position;
                var ch = _input[_position];

                if (!(char.IsDigit(ch) || ch == '.' || ch == '-'))
                    return null;

                var hasDot = ch == '.';
                _position++;

                while (_position < _input.Length)
                {
                    ch = _input[_position];
                    if (char.IsDigit(ch) || ch == 'e' || ch == 'E')
                    {
                        _position++;
                    }
                    else if (ch == '.' && !hasDot)
                    {
                        hasDot = true;
                        _position++;
                    }
                    else if (ch == '-' && _position > start &&
                             (_input[_position - 1] == 'e' || _input[_position - 1] == 'E'))
                    {
                        _position++;
                    }
                    else
                    {
                        break;
                    }
                }

                var numberStr = _input[start.._position];
                return float.TryParse(numberStr, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var result)
                    ? result : null;
            }

            private float? ReadFlag()
            {
                if (_position < _input.Length)
                {
                    var ch = _input[_position];
                    _position++;
                    return ch switch
                    {
                        '0' => 0f,
                        '1' => 1f,
                        _ => null
                    };
                }
                return null;
            }

            private void SkipWhitespace()
            {
                while (_position < _input.Length && _whitespace.Contains(_input[_position]))
                {
                    _position++;
                }
            }
        }
    }
}
