using Newtonsoft.Json;
using System.Xml;

namespace NEDA.SecurityModules.FaceRecognition
{
    public class FaceRecognizer
    {
        private Dictionary<string, string> _faceDb;

        public FaceRecognizer()
        {
            LoadFaceDatabase();
        }

        private void LoadFaceDatabase()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NEDA.SecurityModules\\FaceRecognition\\FaceDatabase.json");
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                _faceDb = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            }
            else
            {
                _faceDb = new Dictionary<string, string>();
            }
        }

        public bool RecognizeFace(string faceId, out string userName)
        {
            if (_faceDb.ContainsKey(faceId))
            {
                userName = _faceDb[faceId];
                return true;
            }
            else
            {
                userName = "Bilinmeyen";
                return false;
            }
        }

        public void AddFace(string faceId, string userName)
        {
            _faceDb[faceId] = userName;
            SaveDatabase();
        }

        private void SaveDatabase()
        {
            string json = JsonConvert.SerializeObject(_faceDb, Formatting.Indented);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NEDA.SecurityModules\\FaceRecognition\\FaceDatabase.json"), json);
        }
    }
}
