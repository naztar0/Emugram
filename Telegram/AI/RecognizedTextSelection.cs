using System.Collections.Generic;
using Telegram.Native.AI;

namespace Telegram.AI
{
    public class RecognizedTextSelection
    {
        public RecognizedTextSelection(string text, IList<RecognizedTextBoundingBox> boundingBoxes)
        {
            Text = text;
            BoundingBoxes = boundingBoxes;
        }

        public string Text { get; }

        public IList<RecognizedTextBoundingBox> BoundingBoxes { get; }
    }
}
