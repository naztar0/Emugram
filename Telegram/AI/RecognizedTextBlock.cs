using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Telegram.Native.AI;

namespace Telegram.AI
{
    public class RecognizedTextBlock
    {
        public RecognizedTextBlock(List<RecognizedLine> lines)
        {
            // TODO: dynamic tolerance
            var tolerance = 4.0f;
            var padding = lines.Select(x => x.BoundingBox.Height()).Average();

            Polygons = RecognizedTextBoundingBoxSimplifier.Union(lines, tolerance, padding * 0.25f);
            Padding = padding * 0.25f;
            Lines = lines;
        }

        public IList<RecognizedLine> Lines { get; }

        public IList<List<Vector2>> Polygons { get; }

        public float Padding { get; }

        public override string ToString()
        {
            return string.Join('\n', Lines.Select(x => x.Text));
        }
    }
}
