using System.Drawing;
#nullable disable
namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// Pipeline boyunca taşınan zengin veri modeli.
    /// Her stage ihtiyaç duyduğu alanları doldurur.
    /// </summary>
    public class PipelineData
    {
        /// <summary>
        /// İşlenen çerçeve.
        /// </summary>
        public VisionFrame Frame { get; set; }

        /// <summary>
        /// Ön işleme sonrası görüntü.
        /// </summary>
        public Bitmap ProcessedImage { get; set; }

        /// <summary>
        /// Nesne tespit sonuçları (tipi projenin geri kalanına göre genişletilebilir).
        /// Şimdilik dinamik object olarak bırakıyoruz.
        /// </summary>
        public object Detections { get; set; }

        /// <summary>
        /// Sahne analizi sonuçları.
        /// </summary>
        public object SceneInfo { get; set; }

        /// <summary>
        /// İleri seviye aşamalarda kullanılabilecek ek veri.
        /// </summary>
        public object ExtendedData { get; set; }
    }
}
