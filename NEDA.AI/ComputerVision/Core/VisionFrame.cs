using NEDA.Core.NEDA.AI.ComputerVision.Core;
using System;
using System.Collections.Generic;
using System.Drawing;
#nullable disable

namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// İşlenecek tekil görüntü çerçevesi.
    /// </summary>
    public class VisionFrame
    {
        public string FrameId { get; set; }

        public int FrameIndex { get; set; }

        public Bitmap Image { get; set; }

        public DateTime Timestamp { get; set; }

        public VisionContext Context { get; set; }

        public VisionResult Result { get; set; }

        public IDictionary<string, object> Metadata { get; set; }
            = new Dictionary<string, object>();

        public VisionFrame()
        {
        }

        public VisionFrame(int frameIndex, Bitmap image, VisionResult result, DateTime timestamp)
        {
            FrameIndex = frameIndex;
            Image = image;
            Result = result;
            Timestamp = timestamp;
        }
    }
}
