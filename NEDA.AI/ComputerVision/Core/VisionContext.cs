using NEDA.Core.NEDA.AI.ComputerVision.Pipelines;
using System;
using System.Collections.Generic;
#nullable disable

namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// Vision işlemleri için bağlamsal bilgiler.
    /// Kaynağın nereden geldiği, zaman damgası, video bağlamı vb.
    /// </summary>
    public class VisionContext
    {
        /// <summary>
        /// Görüntü kaynağı tipi (resim, video, kamera vb.)
        /// </summary>
        public VisionSourceType SourceType { get; set; } = VisionSourceType.Unknown;

        /// <summary>
        /// Çerçevenin işlenme / yakalanma zamanı.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Video akışında çerçeve indeksi (video değilse -1 olabilir).
        /// </summary>
        public int FrameIndex { get; set; } = -1;

        /// <summary>
        /// Video ile ilgili ek bilgiler.
        /// </summary>
        public VideoContext VideoContext { get; set; }

        /// <summary>
        /// Kullanılacak özel pipeline; null ise varsayılan pipeline kullanılır.
        /// </summary>
        public VisionPipeline Pipeline { get; set; }

        /// <summary>
        /// İsteğe bağlı ek meta veriler.
        /// </summary>
        public IDictionary<string, object> Metadata { get; set; }
            = new Dictionary<string, object>();
    }

    /// <summary>
    /// Görüntü kaynağının türü.
    /// </summary>
    public enum VisionSourceType
    {
        Unknown = 0,
        Image = 1,
        Video = 2,
        Camera = 3,
        ScreenCapture = 4,
        AugmentedReality = 5
    }

    /// <summary>
    /// Video akışına ait temel bağlam bilgileri.
    /// Gerekirse ileride genişletilebilir.
    /// </summary>
    public class VideoContext
    {
        /// <summary>
        /// Video tanımlayıcı / adı.
        /// </summary>
        public string VideoId { get; set; }

        /// <summary>
        /// Toplam çerçeve sayısı biliniyorsa.
        /// </summary>
        public int? TotalFrames { get; set; }

        /// <summary>
        /// Kaynağın açıklaması (dosya yolu, kamera adı vb.).
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Ek meta veriler.
        /// </summary>
        public IDictionary<string, object> Metadata { get; set; }
            = new Dictionary<string, object>();
    }
}
